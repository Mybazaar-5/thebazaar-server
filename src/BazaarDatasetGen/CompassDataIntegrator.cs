using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Dataset.Archetypes;
using BazaarCore.Dataset.Simulation;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarDatasetGen;

/// <summary>
/// 外部天梯统计源四层真值整合器：
///   Layer 1: 352 套天梯/标杆原盘直通（含原始技能/附魔/attributeOverrides）；
///   Layer 2: 6,300 套精选实测棋盘智能水合（严格保留原顺序/品质，补齐天梯分位血量、契合技能、Day 7+ 附魔与成长/任务属性）；
///   Layer 3: 252 套真实构筑并入流派目录 + 7 英雄分天获取概率注册表；
///   Layer 4: 真实天梯十档血量标尺对标。
/// </summary>
public static class CompassDataIntegrator
{
    public sealed class CompassDistilledBundle
    {
        public string Version { get; set; } = "";
        public List<AnchorEntry> Anchors { get; set; } = new();
        public List<BoardEntry> Boards { get; set; } = new();
        public List<BuildEntry> Builds { get; set; } = new();
        public Dictionary<string, AvailabilityEntry> Availability { get; set; } = new();
        public Dictionary<string, Dictionary<string, double>> QuestProgress { get; set; } = new();
        public Dictionary<string, List<CalibrationDecile>> Calibration { get; set; } = new();
    }

    public sealed class AnchorEntry
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "ladder";
        public string Hero { get; set; } = "";
        public double Rating { get; set; }
        public int? Wins { get; set; }
        public int? Losses { get; set; }
        public int? Level { get; set; }
        public int HealthMax { get; set; }
        public List<AnchorItemEntry> Items { get; set; } = new();
        public List<AnchorSkillEntry> Skills { get; set; } = new();
    }

    public sealed class AnchorItemEntry
    {
        public string Id { get; set; } = "";
        public string Tier { get; set; } = "Bronze";
        public string? Enchantment { get; set; }
        public Dictionary<string, double>? AttributeOverrides { get; set; }
    }

    public sealed class AnchorSkillEntry
    {
        public string Id { get; set; } = "";
        public string Tier { get; set; } = "Bronze";
    }

    public sealed class BoardEntry
    {
        public string Hero { get; set; } = "";
        public string Period { get; set; } = "d10";
        public double Rating { get; set; }
        public double Quantile { get; set; }
        public string Flow { get; set; } = "";
        public bool IsScaler { get; set; }
        public List<BoardItemEntry> Items { get; set; } = new();
    }

    public sealed class BoardItemEntry
    {
        public string Id { get; set; } = "";
        public string Tier { get; set; } = "Bronze";
        public double Contrib { get; set; }
    }

    public sealed class BuildEntry
    {
        public string Id { get; set; } = "";
        public string Hero { get; set; } = "";
        public string Period { get; set; } = "";
        public string NameZh { get; set; } = "";
        public string Side { get; set; } = "offence";
        public List<string> CoreIds { get; set; } = new();
        public List<string> SupportIds { get; set; } = new();
    }

    public sealed class AvailabilityEntry
    {
        public int[] Days { get; set; } = Array.Empty<int>();
        public Dictionary<string, double[]> Cards { get; set; } = new();
    }

    public sealed class CalibrationDecile
    {
        public double Q { get; set; }
        public int HealthMax { get; set; }
        public double SecMedian { get; set; }
    }

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> ValidEnchantments = new(EnchantmentNames.All, StringComparer.Ordinal);

    public static CompassDistilledBundle LoadBundle(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !File.Exists(jsonPath))
            throw new FileNotFoundException($"未找到外部天梯统计源蒸馏包: {jsonPath}", jsonPath);

        string json = File.ReadAllText(jsonPath);
        return JsonSerializer.Deserialize<CompassDistilledBundle>(json, ReadOpts)
            ?? throw new InvalidDataException($"无法解析外部天梯统计源蒸馏包: {jsonPath}");
    }

    public static void RegisterAvailability(CompassDistilledBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var dict = new Dictionary<string, (int[] Days, Dictionary<string, double[]> Cards)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hero, av) in bundle.Availability)
        {
            if (av.Days.Length > 0 && av.Cards.Count > 0)
                dict[hero] = (av.Days, av.Cards);
        }
        CompassAvailabilityRegistry.Register(dict);
    }

    /// <summary>
    /// 合并现有 320 套扩展流派与罗盘 252 套高纯度真实构筑（构建约 550+ 套全合法流派目录）。
    /// </summary>
    public static ArchetypeCatalog CreateCompassFusedCatalog(GameDataRepository repo, CompassDistilledBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(bundle);

        var expanded = ArchetypeCatalog.CreateExpanded(repo);
        var index = SimulationRepoIndex.GetOrCreate(repo);

        var validAlwaysItemNamesByHero = BuildValidAlwaysLookup(repo.AlwaysSpawnableItems, repo);
        var validAlwaysSkills = repo.AlwaysSpawnableSkills
            .Where(s => repo.Localization == null || repo.HasOfficialZhTitle(s))
            .GroupBy(s => s.InternalName, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var extra = new List<ArchetypeDefinition>(bundle.Builds.Count);
        var usedIds = new HashSet<string>(expanded.All.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var b in bundle.Builds)
        {
            if (!HeroNames.PlayableHeroes.Contains(b.Hero, StringComparer.OrdinalIgnoreCase))
                continue;
            if (!usedIds.Add(b.Id))
                continue;

            var coreNames = new List<string>();
            var usedItemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var collectedCards = new List<CardTemplate>();

            foreach (var cid in b.CoreIds)
            {
                var tpl = repo.GetCard(cid);
                if (tpl == null || tpl.Type != "Item") continue;
                if (!IsValidAlwaysCardForHero(validAlwaysItemNamesByHero, tpl.InternalName, b.Hero)) continue;
                if (ArchetypeBot.IsNonCombatBoardDeadWeight(tpl, index)) continue;
                if (usedItemNames.Add(tpl.InternalName))
                {
                    coreNames.Add(tpl.InternalName);
                    collectedCards.Add(tpl);
                }
            }

            var supportNames = new List<string>();
            foreach (var sid in b.SupportIds)
            {
                var tpl = repo.GetCard(sid);
                if (tpl == null || tpl.Type != "Item") continue;
                if (!IsValidAlwaysCardForHero(validAlwaysItemNamesByHero, tpl.InternalName, b.Hero)) continue;
                if (ArchetypeBot.IsNonCombatBoardDeadWeight(tpl, index)) continue;
                if (!usedItemNames.Add(tpl.InternalName)) continue;

                if (coreNames.Count < 2)
                {
                    coreNames.Add(tpl.InternalName);
                    collectedCards.Add(tpl);
                }
                else
                {
                    supportNames.Add(tpl.InternalName);
                    collectedCards.Add(tpl);
                }
            }

            // 不足保底数则从同英雄 Always 池补齐
            TopUpItemsForHero(validAlwaysItemNamesByHero, b.Hero, supportNames, usedItemNames, minCount: 4);
            if (coreNames.Count < 2 || supportNames.Count < 3)
                continue;

            var prefTags = new Dictionary<string, double>(StringComparer.Ordinal);
            var prefHidden = new Dictionary<string, double>(StringComparer.Ordinal);
            var prefTriggers = new Dictionary<string, double>(StringComparer.Ordinal);
            var posRules = new List<PositioningRule> { PositioningRule.CoreCenter };

            foreach (var card in collectedCards)
            {
                foreach (var t in card.Tags)
                    if (!string.IsNullOrWhiteSpace(t))
                        prefTags[t] = Math.Min(3.2, prefTags.GetValueOrDefault(t, 0.8) + 0.55);
                foreach (var ht in card.HiddenTags)
                    if (!string.IsNullOrWhiteSpace(ht))
                        prefHidden[ht] = Math.Min(3.2, prefHidden.GetValueOrDefault(ht, 0.8) + 0.65);

                var meta = index.GetMeta(card);
                foreach (var tr in meta.TriggerTypes)
                    if (tr.StartsWith("TTriggerOnCard", StringComparison.Ordinal))
                        prefTriggers[tr] = Math.Min(2.8, prefTriggers.GetValueOrDefault(tr, 0.8) + 0.5);
                if (meta.HasNeighborEffect) posRules.Add(PositioningRule.AdjacentToCore);
                if (meta.PrefersLeftMost) posRules.Add(PositioningRule.LeftMost);
                if (meta.PrefersRightMost) posRules.Add(PositioningRule.RightMost);
                if (meta.IsScaler) posRules.Add(PositioningRule.FastTriggerLeftOfScaler);
            }

            if (posRules.Count == 1)
                posRules.Add(PositioningRule.AdjacentToCore);

            // 选取同机制高契合技能
            var coreSkills = validAlwaysSkills
                .Where(s => s.Heroes.Contains(b.Hero, StringComparer.OrdinalIgnoreCase)
                         || s.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase))
                .Select(s => (Skill: s, Score: ScoreCardSynergy(s, index.GetMeta(s), b.Hero, prefTags, prefHidden, prefTriggers)))
                .OrderByDescending(x => x.Score)
                .Select(x => x.Skill.InternalName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();

            if (coreSkills.Count < 2)
                continue;

            var prefEnch = InferPreferredEnchantments(prefHidden);

            extra.Add(new ArchetypeDefinition
            {
                Id = b.Id,
                Hero = b.Hero,
                NameZh = b.NameZh,
                DescriptionZh = $"外部天梯统计源真实构筑（{b.Period} / {b.Side}）：{b.NameZh}",
                CoreItemNames = coreNames.ToArray(),
                SupportItemNames = supportNames.ToArray(),
                CoreSkillNames = coreSkills.ToArray(),
                PreferredTags = prefTags,
                PreferredHiddenTags = prefHidden,
                PreferredTriggers = prefTriggers,
                PreferredEnchantments = prefEnch,
                PositioningRules = posRules.Distinct().ToArray(),
                EarlyFocusTags = prefHidden.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(3).ToArray(),
                EndgamePowerTag = $"Compass_{b.Period}_{b.Side}"
            });
        }

        return ArchetypeCatalog.CreateFromMerged(repo, expanded.All, extra);
    }

    /// <summary>
    /// 水合外部天梯统计源真实天梯原盘 + 6,300 精选真棋盘 + 第 8 英雄社区十胜真值，返回可直接送入交叉定级的合法快照与父 Run。
    /// </summary>
    public static (List<PvpRunRecord> Runs, List<PvpBoardSnapshot> Snapshots) BuildHydratedRealPool(
        GameDataRepository repo,
        IBoardValidator validator,
        CompassDistilledBundle bundle,
        int baseSeed,
        bool requireOfficialZh,
        int maxSnapshotsLimit = 0)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(bundle);

        var index = SimulationRepoIndex.GetOrCreate(repo);
        string version = string.IsNullOrWhiteSpace(repo.GameMode.Version) ? "5.0.0" : repo.GameMode.Version;

        var skillsByHero = new Dictionary<string, List<(CardTemplate Template, CardMeta Meta)>>(StringComparer.OrdinalIgnoreCase);
        var validSkills = repo.AlwaysSpawnableSkills
            .Where(s => repo.Localization == null || repo.HasOfficialZhTitle(s))
            .GroupBy(s => s.InternalName, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        foreach (var hero in HeroNames.PlayableHeroes)
        {
            skillsByHero[hero] = validSkills
                .Where(s => s.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)
                         || s.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase))
                .Select(s => (s, index.GetMeta(s)))
                .ToList();
        }

        var runs = new List<PvpRunRecord>(bundle.Anchors.Count + bundle.Boards.Count + 128);
        var snapshots = new List<PvpBoardSnapshot>(bundle.Anchors.Count + bundle.Boards.Count + 600);

        // 1. Layer 1：还原 352 套天梯真人原盘与标杆盘
        for (int i = 0; i < bundle.Anchors.Count; i++)
        {
            if (maxSnapshotsLimit > 0 && snapshots.Count >= maxSnapshotsLimit)
                break;

            var anchor = bundle.Anchors[i];
            if (!HeroNames.PlayableHeroes.Contains(anchor.Hero, StringComparer.OrdinalIgnoreCase))
                continue;

            var rng = new Random(unchecked(baseSeed ^ (i * 397 + 11)));
            int day = ResolveAnchorDay(anchor);
            int unlockedSlots = GetUnlockedSlotCount(day);
            int level = anchor.Level is > 0 ? anchor.Level.Value : Math.Clamp(day + 1, 1, 20);
            int hp = anchor.HealthMax > 100
                ? anchor.HealthMax
                : InterpolateCalibHealth(bundle.Calibration, day, 0.72, repo, rng);

            int wins = anchor.Wins ?? Math.Clamp(day - 2, 0, 10);
            int losses = anchor.Losses ?? Math.Max(0, day - 1 - wins);
            int prestige = Math.Clamp(20 - losses * 3, 1, 20);

            var ps = new PlayerState($"comp_anc_{i}", anchor.Hero, anchor.Hero, hp, prestige, 10, unlockedSlots)
            {
                Day = day,
                Hour = 6,
                Wins = wins,
                Losses = losses
            };
            ps.Level = level;
            ps.Attributes["HealthMax"] = hp;
            ps.Attributes["Health"] = hp;

            int usedSlots = 0;
            int itemIdx = 0;
            foreach (var it in anchor.Items)
            {
                var tpl = repo.GetCard(it.Id);
                if (tpl == null || tpl.Type != "Item") continue;
                if (requireOfficialZh && repo.Localization != null && !repo.HasOfficialZhTitle(tpl)) continue;

                int w = tpl.CardSize.SlotCount();
                if (usedSlots + w > unlockedSlots) continue;

                var tier = ResolveValidTier(tpl, TierHelper.ParseTier(it.Tier));
                string? ench = (!string.IsNullOrWhiteSpace(it.Enchantment)
                    && ValidEnchantments.Contains(it.Enchantment)
                    && tpl.Enchantments != null
                    && tpl.Enchantments.ContainsKey(it.Enchantment))
                    ? it.Enchantment
                    : null;

                var card = new CardInstance(tpl, tier, ench, $"anc_{i}_{itemIdx++}");
                if (it.AttributeOverrides != null)
                    ApplySafeAttributeOverrides(card, it.AttributeOverrides);

                if (ps.Hand.TryPlaceOrShift(card, usedSlots) || ps.Hand.TryAddAuto(card))
                    usedSlots += w;
            }

            if (ps.Hand.Cards.Count == 0)
                continue;

            // 还原原装技能；若原盘缺失技能则按机制自动补配
            var seenSkillIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sk in anchor.Skills)
            {
                var stpl = repo.GetCard(sk.Id);
                if (stpl == null || stpl.Type != "Skill") continue;
                if (requireOfficialZh && repo.Localization != null && !repo.HasOfficialZhTitle(stpl)) continue;
                if (!seenSkillIds.Add(stpl.Id)) continue;

                var stier = ResolveValidTier(stpl, TierHelper.ParseTier(sk.Tier));
                ps.Skills.Add(new CardInstance(stpl, stier, null, $"ancsk_{i}_{seenSkillIds.Count}"));
            }

            if (ps.Skills.Count == 0 && skillsByHero.TryGetValue(anchor.Hero, out var heroSkills))
            {
                HydrateSkillsForBoard(ps, anchor.Hero, day, 0.72, heroSkills, index, rng);
            }

            string runId = $"comm_anc_run_{i:0000}";
            string boardId = $"comm_anc_{anchor.Hero.ToLowerInvariant()}_d{day:00}_{i:0000}";
            var snap = BuildSnapshotFromState(ps, boardId, runId, version, $"CompassAnchor_{anchor.Kind}", day, repo, validator);
            if (!validator.Validate(snap, repo, requireOfficialZh).IsValid)
                continue;

            snapshots.Add(snap);
            runs.Add(new PvpRunRecord
            {
                RunId = runId,
                Version = version,
                Source = "real_community",
                Hero = anchor.Hero,
                Persona = PersonaType.HardForcer,
                Archetype = snap.Archetype,
                FinalDay = day,
                FinalWins = wins,
                FinalLosses = losses,
                IsVictory = wins >= 10,
                DayBoards = [snap]
            });
        }

        // 2. Layer 2：智能水合 6,300 块精选外部天梯统计源实测棋盘（保序保品质 + 天梯血量/技能/附魔/永久成长）
        for (int i = 0; i < bundle.Boards.Count; i++)
        {
            if (maxSnapshotsLimit > 0 && snapshots.Count >= maxSnapshotsLimit)
                break;

            var board = bundle.Boards[i];
            if (!HeroNames.PlayableHeroes.Contains(board.Hero, StringComparer.OrdinalIgnoreCase))
                continue;

            var rng = new Random(unchecked(baseSeed ^ (i * 7919 + 97)));
            var validTemplates = new List<(CardTemplate Template, BoardItemEntry Entry)>(board.Items.Count);
            int totalWidth = 0;

            foreach (var it in board.Items)
            {
                var tpl = repo.GetCard(it.Id);
                if (tpl == null || tpl.Type != "Item") continue;
                if (requireOfficialZh && repo.Localization != null && !repo.HasOfficialZhTitle(tpl)) continue;
                int w = tpl.CardSize.SlotCount();
                if (totalWidth + w > BoardGrid.Capacity) continue;

                validTemplates.Add((tpl, it));
                totalWidth += w;
            }

            if (validTemplates.Count == 0)
                continue;

            int day = MapBoardToDay(board.Period, board.Quantile, totalWidth, rng);
            int unlockedSlots = GetUnlockedSlotCount(day);

            // 若早期按时期恰好超宽则顺延至正好解锁该宽度的天数
            while (totalWidth > unlockedSlots && day < 4)
            {
                day++;
                unlockedSlots = GetUnlockedSlotCount(day);
            }

            int level = Math.Clamp(day + (board.Quantile >= 0.7 ? 1 : 0), 1, 20);
            int hp = InterpolateCalibHealth(bundle.Calibration, day, board.Quantile, repo, rng);
            int wins = Math.Clamp((int)Math.Round((day - 1) * (0.28 + board.Quantile * 0.62)), 0, Math.Min(10, day - 1));
            int losses = Math.Max(0, Math.Min(6, day - 1 - wins));
            int prestige = Math.Clamp(20 - losses * 3, 1, 20);

            var ps = new PlayerState($"comp_brd_{i}", board.Hero, board.Hero, hp, prestige, 10, unlockedSlots)
            {
                Day = day,
                Hour = 6,
                Wins = wins,
                Losses = losses
            };
            ps.Level = level;
            ps.Attributes["HealthMax"] = hp;
            ps.Attributes["Health"] = hp;

            int slotCursor = 0;
            int itemIdx = 0;
            var placed = new List<(CardInstance Card, double Contrib, CardMeta Meta)>();
            foreach (var (tpl, entry) in validTemplates)
            {
                int w = tpl.CardSize.SlotCount();
                if (slotCursor + w > unlockedSlots) break;

                var tier = ResolveValidTier(tpl, TierHelper.ParseTier(entry.Tier));
                var card = new CardInstance(tpl, tier, null, $"cbrd_{i}_{itemIdx++}");
                if (ps.Hand.TryPlaceOrShift(card, slotCursor) || ps.Hand.TryAddAuto(card))
                {
                    slotCursor += w;
                    placed.Add((card, entry.Contrib, index.GetMeta(tpl)));
                }
            }

            if (placed.Count == 0)
                continue;

            // 水合契合技能
            if (skillsByHero.TryGetValue(board.Hero, out var heroSkills))
                HydrateSkillsForBoard(ps, board.Hero, day, board.Quantile, heroSkills, index, rng);

            // Day 7+ 水合核心附魔
            if (day >= 7)
                HydrateEnchantmentsForBoard(placed, day, board.Quantile, rng);

            // Day 4+ 水合成长属性与任务进度
            if (day >= 4)
                HydratePermanentGrowthForBoard(placed, day, board.Quantile, board.IsScaler, bundle.QuestProgress, rng);

            string flowTag = string.IsNullOrWhiteSpace(board.Flow) ? board.Period : board.Flow;
            string runId = $"comm_brd_run_{i:00000}";
            string boardId = $"comm_brd_{board.Hero.ToLowerInvariant()}_d{day:00}_{i:00000}";
            var snap = BuildSnapshotFromState(ps, boardId, runId, version, $"Compass_{board.Period}_{flowTag}", day, repo, validator);
            if (!validator.Validate(snap, repo, requireOfficialZh).IsValid)
                continue;

            snapshots.Add(snap);
            runs.Add(new PvpRunRecord
            {
                RunId = runId,
                Version = version,
                Source = "real_community",
                Hero = board.Hero,
                Persona = board.Quantile >= 0.60 ? PersonaType.HardForcer : (board.Quantile >= 0.30 ? PersonaType.FlexPivot : PersonaType.Casual),
                Archetype = snap.Archetype,
                FinalDay = day,
                FinalWins = wins,
                FinalLosses = losses,
                IsVictory = wins >= 10,
                DayBoards = [snap]
            });
        }

        // 3. 补足第 8 英雄 Pygmalien（罗盘未收录）及全英雄社区十胜真值轨迹
        var commTenWin = CommunityGhostHydrator.BuildCommunitySnapshots(repo, validator, baseSeed, requireOfficialZh);
        foreach (var run in commTenWin.CommunityRuns)
        {
            if (maxSnapshotsLimit > 0 && snapshots.Count >= maxSnapshotsLimit)
                break;
            if (!string.Equals(run.Hero, HeroNames.Pygmalien, StringComparison.OrdinalIgnoreCase) && maxSnapshotsLimit == 0)
            {
                // 其余 7 英雄也各保底加入变体 0 作为十胜参考标尺
                if (!run.RunId.Contains("_v0_", StringComparison.Ordinal))
                    continue;
            }
            runs.Add(run);
            snapshots.AddRange(run.DayBoards);
        }

        return (runs, snapshots);
    }

    private static int ResolveAnchorDay(AnchorEntry anchor)
    {
        if (anchor.Wins.HasValue && anchor.Losses.HasValue && anchor.Wins.Value + anchor.Losses.Value >= 1)
            return Math.Clamp(anchor.Wins.Value + anchor.Losses.Value, 1, 17);

        return anchor.HealthMax switch
        {
            < 700 => 2,
            < 1100 => 4,
            < 1700 => 6,
            < 2500 => 8,
            < 3500 => 10,
            < 4800 => 12,
            _ => 14
        };
    }

    private static int GetUnlockedSlotCount(int day) => day switch
    {
        <= 1 => 4,
        2 => 6,
        3 => 8,
        _ => 10
    };

    private static int MapBoardToDay(string period, double quantile, int totalWidth, Random rng)
    {
        if (string.Equals(period, "d4", StringComparison.OrdinalIgnoreCase))
        {
            int minDay = totalWidth <= 4 ? 1 : (totalWidth <= 6 ? 2 : (totalWidth <= 8 ? 3 : 4));
            return rng.Next(minDay, 6); // Day 1..5
        }
        if (string.Equals(period, "d7", StringComparison.OrdinalIgnoreCase))
        {
            return rng.Next(6, 10); // Day 6..9
        }
        // d10: 覆盖 Day 10..16，高分位棋盘偏向深水区
        if (quantile >= 0.72)
            return rng.Next(12, 17); // Day 12..16
        if (quantile >= 0.38)
            return rng.Next(10, 15); // Day 10..14
        return rng.Next(10, 13);     // Day 10..12
    }

    private static int InterpolateCalibHealth(
        Dictionary<string, List<CalibrationDecile>> calib,
        int day,
        double quantile,
        GameDataRepository repo,
        Random rng)
    {
        double baseCalibHp;
        if (day <= 4)
        {
            double d4Hp = LookupDecileHp(calib, "4", quantile, 1100);
            double d1Hp = 250 + quantile * 60;
            double t = (day - 1) / 3.0;
            baseCalibHp = d1Hp + t * (d4Hp - d1Hp);
        }
        else if (day <= 7)
        {
            double d4Hp = LookupDecileHp(calib, "4", quantile, 1100);
            double d7Hp = LookupDecileHp(calib, "7", quantile, 2400);
            double t = (day - 4) / 3.0;
            baseCalibHp = d4Hp + t * (d7Hp - d4Hp);
        }
        else if (day <= 10)
        {
            double d7Hp = LookupDecileHp(calib, "7", quantile, 2400);
            double d10Hp = LookupDecileHp(calib, "10", quantile, 4300);
            double t = (day - 7) / 3.0;
            baseCalibHp = d7Hp + t * (d10Hp - d7Hp);
        }
        else
        {
            double d10Hp = LookupDecileHp(calib, "10", quantile, 4300);
            double perDayGain = 340 + quantile * 140;
            baseCalibHp = d10Hp + (day - 10) * perDayGain;
        }

        double jitter = 0.94 + rng.NextDouble() * 0.12;
        int minFloor = 250 + (day - 1) * 140;
        return Math.Max(minFloor, (int)Math.Round(baseCalibHp * jitter));
    }

    private static double LookupDecileHp(
        Dictionary<string, List<CalibrationDecile>> calib,
        string key,
        double quantile,
        double fallback)
    {
        if (!calib.TryGetValue(key, out var list) || list.Count == 0)
            return fallback;

        var closest = list[0];
        double bestDist = Math.Abs(closest.Q - quantile);
        for (int i = 1; i < list.Count; i++)
        {
            double dist = Math.Abs(list[i].Q - quantile);
            if (dist < bestDist)
            {
                bestDist = dist;
                closest = list[i];
            }
        }
        return closest.HealthMax > 100 ? closest.HealthMax : fallback;
    }

    private static void HydrateSkillsForBoard(
        PlayerState ps,
        string hero,
        int day,
        double quantile,
        List<(CardTemplate Template, CardMeta Meta)> heroSkills,
        SimulationRepoIndex index,
        Random rng)
    {
        if (heroSkills.Count == 0) return;

        int targetCount = Math.Clamp(1 + (day * 2) / 3 + (quantile >= 0.75 ? 1 : 0), 1, 9);
        var prefTags = new Dictionary<string, double>(StringComparer.Ordinal);
        var prefHidden = new Dictionary<string, double>(StringComparer.Ordinal);
        var prefTriggers = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var card in ps.Hand.Cards)
        {
            foreach (var t in card.Template.Tags)
                if (!string.IsNullOrWhiteSpace(t))
                    prefTags[t] = prefTags.GetValueOrDefault(t, 0) + 1.0;
            foreach (var ht in card.Template.HiddenTags)
                if (!string.IsNullOrWhiteSpace(ht))
                    prefHidden[ht] = prefHidden.GetValueOrDefault(ht, 0) + 1.2;
            var m = index.GetMeta(card.Template);
            foreach (var tr in m.TriggerTypes)
                prefTriggers[tr] = prefTriggers.GetValueOrDefault(tr, 0) + 0.8;
        }

        var ranked = heroSkills
            .Select(x => (x.Template, Score: ScoreCardSynergy(x.Template, x.Meta, hero, prefTags, prefHidden, prefTriggers) + rng.NextDouble() * 1.6))
            .OrderByDescending(x => x.Score)
            .ToList();

        var seen = new HashSet<string>(ps.Skills.Select(s => s.Template.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ranked)
        {
            if (ps.Skills.Count >= targetCount) break;
            if (!seen.Add(entry.Template.Id)) continue;

            CardTier desired = day switch
            {
                <= 3 => CardTier.Bronze,
                <= 6 => rng.NextDouble() < 0.45 ? CardTier.Silver : CardTier.Bronze,
                <= 10 => (quantile >= 0.7 && rng.NextDouble() < 0.35) ? CardTier.Gold : CardTier.Silver,
                _ => (quantile >= 0.65 && rng.NextDouble() < 0.45) ? CardTier.Diamond : CardTier.Gold
            };

            var tier = ResolveValidTier(entry.Template, desired);
            ps.Skills.Add(new CardInstance(entry.Template, tier, null, $"chsk_{ps.Skills.Count}"));
        }
    }

    private static void HydrateEnchantmentsForBoard(
        List<(CardInstance Card, double Contrib, CardMeta Meta)> placed,
        int day,
        double quantile,
        Random rng)
    {
        int targetEnchants = day switch
        {
            <= 9 => quantile >= 0.48 ? 1 : 0,
            <= 12 => quantile >= 0.72 ? 2 : 1,
            _ => quantile >= 0.78 ? 3 : 2
        };
        if (targetEnchants <= 0) return;

        var candidates = placed
            .Where(x => string.IsNullOrEmpty(x.Card.Enchantment)
                     && x.Card.Template.Enchantments != null
                     && x.Card.Template.Enchantments.Count > 0)
            .OrderByDescending(x => x.Contrib)
            .ThenByDescending(x => (int)x.Card.Tier)
            .ToList();

        int applied = 0;
        foreach (var (card, _, meta) in candidates)
        {
            if (applied >= targetEnchants) break;
            var available = card.Template.Enchantments!.Keys
                .Where(e => ValidEnchantments.Contains(e))
                .ToList();
            if (available.Count == 0) continue;

            string picked = PickBestEnchantmentForCard(available, meta, rng);
            card.ApplyEnchantment(picked);
            applied++;
        }
    }

    private static string PickBestEnchantmentForCard(List<string> available, CardMeta meta, Random rng)
    {
        string[] preferredOrder =
        [
            meta.IsBurn ? "Fiery" : "",
            meta.IsPoison ? "Toxic" : "",
            meta.IsShield ? "Shielded" : "",
            meta.IsWeapon ? "Deadly" : "",
            meta.IsWeapon ? "Obsidian" : "",
            meta.IsHaste ? "Turbo" : "",
            meta.IsSlow ? "Heavy" : "",
            meta.IsFreeze ? "Icy" : "",
            meta.IsHeal ? "Restorative" : "",
            meta.IsRegen ? "Mossy" : "",
            "Shiny",
            "Turbo",
            "Radiant"
        ];

        foreach (var pref in preferredOrder)
        {
            if (!string.IsNullOrEmpty(pref) && available.Contains(pref, StringComparer.Ordinal))
                return pref;
        }
        return available[rng.Next(available.Count)];
    }

    private static void HydratePermanentGrowthForBoard(
        List<(CardInstance Card, double Contrib, CardMeta Meta)> placed,
        int day,
        double quantile,
        bool boardIsScaler,
        Dictionary<string, Dictionary<string, double>> questProgress,
        Random rng)
    {
        string dayKey = Math.Clamp(day, 1, 15).ToString();
        questProgress.TryGetValue(dayKey, out var dayQuests);

        double baseScale = (day - 3) * (0.42 + quantile * 0.75) * (0.85 + rng.NextDouble() * 0.30);

        foreach (var (card, contrib, meta) in placed)
        {
            double questRatio = 0.0;
            if (dayQuests != null)
                dayQuests.TryGetValue(card.Template.Id, out questRatio);

            bool shouldScale = meta.IsProperty || meta.IsValueScaler || meta.IsScaler
                || (boardIsScaler && contrib >= 3.0)
                || questRatio >= 0.35;

            if (!shouldScale)
                continue;

            double mult = (0.7 + (int)card.Tier * 0.25) * (questRatio > 0 ? (0.6 + questRatio) : 1.0);
            double scale = baseScale * mult;

            if (meta.IsProperty || meta.IsValueScaler)
            {
                int valGain = Math.Clamp((int)Math.Round(scale * 1.4), 1, 40);
                card.ModifyAttribute("SellPrice", "Add", valGain, permanent: true);
            }

            if (card.BaseAttributes.GetValueOrDefault("DamageAmount", 0) > 0)
                card.ModifyAttribute("DamageAmount", "Add", Math.Clamp(Math.Round(scale * 2.8), 2, 180), permanent: true);
            if (card.BaseAttributes.GetValueOrDefault("ShieldApplyAmount", 0) > 0)
                card.ModifyAttribute("ShieldApplyAmount", "Add", Math.Clamp(Math.Round(scale * 3.2), 2, 200), permanent: true);
            if (card.BaseAttributes.GetValueOrDefault("BurnApplyAmount", 0) > 0)
                card.ModifyAttribute("BurnApplyAmount", "Add", Math.Clamp(Math.Round(scale * 0.85), 1, 35), permanent: true);
            if (card.BaseAttributes.GetValueOrDefault("PoisonApplyAmount", 0) > 0)
                card.ModifyAttribute("PoisonApplyAmount", "Add", Math.Clamp(Math.Round(scale * 0.70), 1, 28), permanent: true);
            if (card.BaseAttributes.GetValueOrDefault("HealAmount", 0) > 0)
                card.ModifyAttribute("HealAmount", "Add", Math.Clamp(Math.Round(scale * 3.0), 2, 160), permanent: true);
            if (card.BaseAttributes.GetValueOrDefault("RegenApplyAmount", 0) > 0)
                card.ModifyAttribute("RegenApplyAmount", "Add", Math.Clamp(Math.Round(scale * 0.75), 1, 28), permanent: true);
            if (card.BaseAttributes.GetValueOrDefault("CritChance", 0) > 0 && quantile >= 0.5)
                card.ModifyAttribute("CritChance", "Add", Math.Clamp(Math.Round(scale * 1.5), 1, 25), permanent: true);
        }
    }

    private static void ApplySafeAttributeOverrides(CardInstance card, Dictionary<string, double> overrides)
    {
        foreach (var (attr, targetVal) in overrides)
        {
            if (string.Equals(attr, "Multicast", StringComparison.OrdinalIgnoreCase) && targetVal <= 1)
                continue;

            double baseVal = card.BaseAttributes.GetValueOrDefault(attr, 0);
            double delta = targetVal - baseVal;
            if (Math.Abs(delta) < 1e-6) continue;

            if (string.Equals(attr, "CooldownMax", StringComparison.OrdinalIgnoreCase))
            {
                double clamped = Math.Max(1000.0, targetVal);
                delta = clamped - baseVal;
            }
            else if (string.Equals(attr, "PercentCooldownReduction", StringComparison.OrdinalIgnoreCase))
            {
                double clamped = Math.Clamp(targetVal, 0.0, 75.0);
                delta = clamped - baseVal;
            }

            if (Math.Abs(delta) >= 1e-4)
                card.ModifyAttribute(attr, "Add", delta, permanent: true);
        }
    }

    private static PvpBoardSnapshot BuildSnapshotFromState(
        PlayerState ps,
        string boardId,
        string runId,
        string version,
        string archetype,
        int day,
        GameDataRepository repo,
        IBoardValidator validator)
    {
        ps.ClearAuraModifiers();
        foreach (var c in ps.Hand.Cards) c.ClearAuraModifiers();
        foreach (var s in ps.Skills) s.ClearAuraModifiers();

        int unscaledHp = (int)Math.Round(ps.GetAttributeUnscaled("HealthMax"));
        int healthMax = Math.Max(200, unscaledHp > 0 ? unscaledHp : ps.HealthMax);

        var snap = new PvpBoardSnapshot
        {
            BoardId = boardId,
            RunId = runId,
            Version = version,
            Source = "real_community",
            Persona = PersonaType.HardForcer,
            Archetype = archetype,
            Day = day,
            Hero = ps.Hero,
            Level = Math.Max(1, ps.Level),
            HealthMax = healthMax,
            Prestige = Math.Max(1, ps.Prestige),
            Wins = Math.Max(0, ps.Wins),
            Losses = Math.Max(0, ps.Losses),
            UnlockedSlotsMask = ps.Hand.UnlockedMask,
            PowerBucket = PowerBucket.Normal,
            EstimatedWinRate = 0.5
        };

        foreach (var card in ps.Hand.Cards.OrderBy(c => c.StartSlot))
        {
            snap.Items.Add(new PvpItemSnapshot
            {
                Slot = card.StartSlot,
                Width = card.SlotWidth,
                TemplateId = card.Template.Id,
                InternalName = card.Template.InternalName,
                ZhName = repo.GetLocalizedTitle(card.Template),
                Tier = card.Tier,
                Enchantment = string.IsNullOrWhiteSpace(card.Enchantment) ? null : card.Enchantment,
                PermanentAttributes = ExtractPermAttrs(card)
            });
        }

        foreach (var skill in ps.Skills)
        {
            snap.Skills.Add(new PvpSkillSnapshot
            {
                TemplateId = skill.Template.Id,
                InternalName = skill.Template.InternalName,
                ZhName = repo.GetLocalizedTitle(skill.Template),
                Tier = skill.Tier
            });
        }

        snap.BoardSignature = validator.ComputeSignature(snap);
        return snap;
    }

    private static Dictionary<string, double> ExtractPermAttrs(CardInstance card)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (card.PermanentAdd.Count == 0 && card.PermanentMult.Count == 0)
            return result;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in card.PermanentAdd)
            if (Math.Abs(v) > 1e-6) keys.Add(k);
        foreach (var (k, m) in card.PermanentMult)
            if (Math.Abs(m - 1.0) > 1e-6) keys.Add(k);

        foreach (var attr in keys)
        {
            double unscaled = card.GetAttributeUnscaled(attr);
            if (card.PermanentMult.TryGetValue(attr, out var mult) && Math.Abs(mult - 1.0) > 1e-6)
                unscaled *= mult;
            result[attr] = Math.Round(unscaled, 4);
        }
        return result;
    }

    private static CardTier ResolveValidTier(CardTemplate tpl, CardTier desired)
    {
        if (tpl.Tiers == null || tpl.Tiers.Count == 0)
            return desired;
        if (tpl.Tiers.ContainsKey(desired.ToString()))
            return desired;

        var available = Enum.GetValues<CardTier>()
            .Where(t => tpl.Tiers.ContainsKey(t.ToString()))
            .OrderBy(t => (int)t)
            .ToList();
        if (available.Count == 0)
            return tpl.DefaultTier;

        var notHigher = available.Where(t => t <= desired).ToList();
        return notHigher.Count > 0 ? notHigher[^1] : available[0];
    }

    private static Dictionary<string, List<CardTemplate>> BuildValidAlwaysLookup(
        IEnumerable<CardTemplate> items,
        GameDataRepository repo)
    {
        var dict = new Dictionary<string, List<CardTemplate>>(StringComparer.Ordinal);
        foreach (var tpl in items)
        {
            if (string.IsNullOrEmpty(tpl.InternalName)) continue;
            if (repo.Localization != null && !repo.HasOfficialZhTitle(tpl)) continue;
            if (!dict.TryGetValue(tpl.InternalName, out var list))
            {
                list = new List<CardTemplate>();
                dict[tpl.InternalName] = list;
            }
            list.Add(tpl);
        }
        return dict;
    }

    private static bool IsValidAlwaysCardForHero(
        Dictionary<string, List<CardTemplate>> lookup,
        string internalName,
        string hero)
    {
        if (!lookup.TryGetValue(internalName, out var list))
            return false;
        return list.Any(t =>
            t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) ||
            t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase));
    }

    private static void TopUpItemsForHero(
        Dictionary<string, List<CardTemplate>> lookup,
        string hero,
        List<string> target,
        HashSet<string> used,
        int minCount)
    {
        if (target.Count >= minCount) return;
        foreach (var (name, list) in lookup)
        {
            if (target.Count >= minCount) break;
            if (used.Contains(name)) continue;
            if (list.Any(t => t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)
                           || t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase)))
            {
                used.Add(name);
                target.Add(name);
            }
        }
    }

    private static double ScoreCardSynergy(
        CardTemplate tpl,
        CardMeta meta,
        string hero,
        Dictionary<string, double> prefTags,
        Dictionary<string, double> prefHidden,
        Dictionary<string, double> prefTriggers)
    {
        double score = tpl.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) ? 1.5 : 0.3;
        foreach (var t in tpl.Tags)
            if (prefTags.TryGetValue(t, out var w)) score += w;
        foreach (var ht in tpl.HiddenTags)
            if (prefHidden.TryGetValue(ht, out var w)) score += w * 1.1;
        foreach (var tr in meta.TriggerTypes)
            if (prefTriggers.TryGetValue(tr, out var w)) score += w * 0.9;
        return score;
    }

    private static Dictionary<string, double> InferPreferredEnchantments(Dictionary<string, double> prefHidden)
    {
        var ench = new Dictionary<string, double>(StringComparer.Ordinal);
        if (prefHidden.ContainsKey("Burn") || prefHidden.ContainsKey("BurnReference")) ench["Fiery"] = 2.8;
        if (prefHidden.ContainsKey("Poison") || prefHidden.ContainsKey("PoisonReference")) ench["Toxic"] = 2.8;
        if (prefHidden.ContainsKey("Shield") || prefHidden.ContainsKey("ShieldReference")) ench["Shielded"] = 2.6;
        if (prefHidden.ContainsKey("Crit") || prefHidden.ContainsKey("Damage")) ench["Deadly"] = 2.7;
        if (prefHidden.ContainsKey("Haste") || prefHidden.ContainsKey("Charge")) ench["Turbo"] = 2.8;
        if (prefHidden.ContainsKey("Slow")) ench["Heavy"] = 2.4;
        if (prefHidden.ContainsKey("Freeze")) ench["Icy"] = 2.4;
        if (prefHidden.ContainsKey("Heal")) ench["Restorative"] = 2.4;
        if (prefHidden.ContainsKey("Regen")) ench["Mossy"] = 2.4;
        if (ench.Count == 0)
        {
            ench["Turbo"] = 2.5;
            ench["Shiny"] = 2.4;
            ench["Obsidian"] = 2.2;
        }
        return ench;
    }
}
