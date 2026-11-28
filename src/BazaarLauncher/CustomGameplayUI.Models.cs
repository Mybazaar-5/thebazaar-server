using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace BazaarLauncher;

/// <summary>对手模式既接受字符串（"Random" / "FixedLineup"）也接受数字（0 / 1），服务端换序列化方式也不会解析失败。</summary>
internal sealed class GhostOpponentModeJsonConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(string);

    public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
    {
        switch (reader.TokenType)
        {
            case JsonToken.Integer:
                return Convert.ToInt64(reader.Value) == 1 ? "FixedLineup" : "Random";
            case JsonToken.String:
                string s = (string)reader.Value;
                return string.Equals(s, "FixedLineup", StringComparison.OrdinalIgnoreCase) || s == "1" ? "FixedLineup" : "Random";
            default:
                return "Random";
        }
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        => writer.WriteValue(value as string ?? "Random");
}

internal sealed class StartingItemRule
{
    [JsonProperty("templateId")] public string TemplateId { get; set; } = string.Empty;
    [JsonProperty("tier")] public string Tier { get; set; } = "Bronze";
    [JsonProperty("enchantment")] public string Enchantment { get; set; }
}

internal sealed class StartingSkillRule
{
    [JsonProperty("templateId")] public string TemplateId { get; set; } = string.Empty;
    [JsonProperty("tier")] public string Tier { get; set; } = "Bronze";
}

/// <summary>
/// 客户端多模式与自定义玩法规则集（完整对齐服务端 CustomRuleModels，支持 21 项 Wave 2/3 字段、边界钳制与零序列化手写深拷贝）。
/// </summary>
internal sealed class CustomRunRuleSet
{
    [JsonProperty("selectedArchetypeMask")] public ulong SelectedArchetypeMask { get; set; }
    [JsonProperty("archetypeWeightMultiplier")] public double ArchetypeWeightMultiplier { get; set; } = 2.0;
    [JsonProperty("archetypeMultiMatchCap")] public double ArchetypeMultiMatchCap { get; set; } = 3.0;
    [JsonProperty("allowTendencySelection")] public bool AllowTendencySelection { get; set; } = true;
    [JsonProperty("promptTendencyBeforeRun")] public bool PromptTendencyBeforeRun { get; set; } = true;
    [JsonProperty("startingGold")] public int StartingGold { get; set; } = 15;
    [JsonProperty("startingIncome")] public int StartingIncome { get; set; } = 5;
    [JsonProperty("startingExperience")] public int StartingExperience { get; set; } = 3;
    [JsonProperty("startingPrestige")] public int StartingPrestige { get; set; } = 20;
    [JsonProperty("startingHealth")] public int StartingHealth { get; set; } = 300;
    [JsonProperty("startingItems")] public List<StartingItemRule> StartingItems { get; set; } = new();
    [JsonProperty("startingSkills")] public List<StartingSkillRule> StartingSkills { get; set; } = new();
    [JsonProperty("monsterLootChooseOne")] public bool MonsterLootChooseOne { get; set; } = true;
    [JsonProperty("encounterCategoryMultipliers")] public Dictionary<string, double> EncounterCategoryMultipliers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonProperty("bannedEncounterIds")] public List<string> BannedEncounterIds { get; set; } = new();
    [JsonProperty("playerCap")] public int PlayerCap { get; set; } = 8;
    [JsonProperty("botNonLethalPrestigeLock")] public bool BotNonLethalPrestigeLock { get; set; } = true;
    [JsonProperty("humanMatchEvery4Rounds")] public bool HumanMatchEvery4Rounds { get; set; } = true;
    [JsonProperty("lastHumanStandingMode")] public bool LastHumanStandingMode { get; set; } = true;
    [JsonProperty("isCustomPowerModified")] public bool IsCustomPowerModified { get; set; }

    // Wave 2/3 新增：预设档位 + 7 大模式 + 14 突变词条 + 经济/掉率/种子调参（完整对齐服务端 CustomRuleModels）
    [JsonProperty("presetId")] public string PresetId { get; set; } = "PureOfficial";
    [JsonProperty("specialModeId")] public string SpecialModeId { get; set; } = "Standard";
    [JsonProperty("mutatorIds")] public List<string> MutatorIds { get; set; } = new();
    [JsonProperty("dailyChallengeDate")] public string DailyChallengeDate { get; set; }
    [JsonProperty("fixedRunSeed")] public int? FixedRunSeed { get; set; }
    [JsonProperty("endlessMaxDay")] public int EndlessMaxDay { get; set; } = 99;
    [JsonProperty("crossHeroOwnHeroBias")] public double CrossHeroOwnHeroBias { get; set; } = 0.40;
    [JsonProperty("bossRushTotalWaves")] public int BossRushTotalWaves { get; set; } = 10;
    [JsonProperty("bossRushIncludeMilestonePvp")] public bool BossRushIncludeMilestonePvp { get; set; } = true;
    [JsonProperty("sandboxFreeInfiniteReroll")] public bool SandboxFreeInfiniteReroll { get; set; } = true;
    [JsonProperty("sandboxZeroShopPrice")] public bool SandboxZeroShopPrice { get; set; } = false;
    [JsonProperty("shopHighTierBonusPct")] public double ShopHighTierBonusPct { get; set; } = 0.0;
    [JsonProperty("shopPriceMultiplier")] public double ShopPriceMultiplier { get; set; } = 1.0;
    [JsonProperty("monsterLootBonusChoices")] public int MonsterLootBonusChoices { get; set; } = 0;
    [JsonProperty("monsterLootTierBonus")] public int MonsterLootTierBonus { get; set; } = 0;
    [JsonProperty("enemyStatMultiplier")] public double EnemyStatMultiplier { get; set; } = 1.0;
    [JsonProperty("disableFatesRevive")] public bool DisableFatesRevive { get; set; } = false;
    [JsonProperty("selectedModIds")] public List<string> SelectedModIds { get; set; } = new();

    // ── v4 种子与每日对手剧本 ──
    // 这三项只由「种子与每日对手」卡片通过 /api/planner/apply-rules 修改；整份保存（PUT /custom-rules/me）时
    // 故意不发送（ShouldSerialize 返回 false），服务端按「没带就保留」处理，避免面板里旧的副本把网页刚改的设置冲掉。
    [JsonProperty("customSeedText")] public string CustomSeedText { get; set; }
    [JsonProperty("ghostOpponentMode")][JsonConverter(typeof(GhostOpponentModeJsonConverter))] public string GhostOpponentMode { get; set; } = "Random";
    [JsonProperty("selectedGhostLineupId")] public string SelectedGhostLineupId { get; set; }
    public bool ShouldSerializeCustomSeedText() => false;
    public bool ShouldSerializeGhostOpponentMode() => false;
    public bool ShouldSerializeSelectedGhostLineupId() => false;

    // Mod 自定义玩法模式：以前客户端不认识这个字段，整份保存时会把它冲掉，这里补上让它原样往返
    [JsonProperty("customModeId")] public string CustomModeId { get; set; }

    internal bool UsesFixedLineup
        => string.Equals(GhostOpponentMode, "FixedLineup", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(SelectedGhostLineupId);

    public void EnsureCollections()
    {
        StartingItems ??= new List<StartingItemRule>();
        StartingSkills ??= new List<StartingSkillRule>();
        EncounterCategoryMultipliers ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        BannedEncounterIds ??= new List<string>();
        MutatorIds ??= new List<string>();
        SelectedModIds ??= new List<string>();
        if (string.IsNullOrWhiteSpace(PresetId)) PresetId = "PureOfficial";
        if (string.IsNullOrWhiteSpace(SpecialModeId)) SpecialModeId = "Standard";
        if (string.IsNullOrWhiteSpace(GhostOpponentMode)) GhostOpponentMode = "Random";
    }

    /// <summary>D6-P1-02：统一默认重置入口（彻底清空全部 Wave 2/3 扩展字段与旧预设残留）。</summary>
    public void ResetToDefaults() => ResetToStandardValues(preserveArchetypeAndLobbyFlags: false);

    public void ResetToStandardValues(bool preserveArchetypeAndLobbyFlags = false)
    {
        ulong mask = preserveArchetypeAndLobbyFlags ? SelectedArchetypeMask : 0UL;
        bool allowTend = !preserveArchetypeAndLobbyFlags || AllowTendencySelection;
        bool promptBefore = !preserveArchetypeAndLobbyFlags || PromptTendencyBeforeRun;
        int cap = preserveArchetypeAndLobbyFlags ? Math.Clamp(PlayerCap, 2, 16) : 8;
        bool botLock = !preserveArchetypeAndLobbyFlags || BotNonLethalPrestigeLock;
        bool match4 = !preserveArchetypeAndLobbyFlags || HumanMatchEvery4Rounds;
        bool lastHuman = !preserveArchetypeAndLobbyFlags || LastHumanStandingMode;

        SelectedArchetypeMask = mask;
        ArchetypeWeightMultiplier = 2.0;
        ArchetypeMultiMatchCap = 3.0;
        AllowTendencySelection = allowTend;
        PromptTendencyBeforeRun = promptBefore;
        StartingGold = 15;
        StartingIncome = 5;
        StartingExperience = 3;
        StartingPrestige = 20;
        StartingHealth = 300;
        StartingItems = new List<StartingItemRule>();
        StartingSkills = new List<StartingSkillRule>();
        MonsterLootChooseOne = true;
        EncounterCategoryMultipliers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        BannedEncounterIds = new List<string>();
        PlayerCap = cap;
        BotNonLethalPrestigeLock = botLock;
        HumanMatchEvery4Rounds = match4;
        LastHumanStandingMode = lastHuman;
        PresetId = "PureOfficial";
        SpecialModeId = "Standard";
        MutatorIds = new List<string>();
        DailyChallengeDate = null;
        FixedRunSeed = null;
        EndlessMaxDay = 99;
        CrossHeroOwnHeroBias = 0.40;
        BossRushTotalWaves = 10;
        BossRushIncludeMilestonePvp = true;
        SandboxFreeInfiniteReroll = true;
        SandboxZeroShopPrice = false;
        ShopHighTierBonusPct = 0.0;
        ShopPriceMultiplier = 1.0;
        MonsterLootBonusChoices = 0;
        MonsterLootTierBonus = 0;
        EnemyStatMultiplier = 1.0;
        DisableFatesRevive = false;
        SelectedModIds = new List<string>();
        CustomModeId = null;
        // 种子与对手剧本不在这里清：它们由服务端保管、只在「种子与每日对手」卡片里改，
        // 本地清掉而服务端没清会让卡片显示和实际不一致（「一键恢复官方默认」走服务端重置，会一起清）。
        IsCustomPowerModified = false;
    }

    /// <summary>D6-P1-02/03：对齐服务端 CustomRuleModels.CloneNormalized() 的全部数值上下界与 PlayerCap(2..16)。</summary>
    public void NormalizeBounds()
    {
        EnsureCollections();
        ArchetypeWeightMultiplier = Math.Clamp(ArchetypeWeightMultiplier, 1.0, 10.0);
        ArchetypeMultiMatchCap = Math.Clamp(ArchetypeMultiMatchCap, 1.0, 10.0);
        // 上限与服务端 CustomRunRuleSet.CloneNormalized 一致：以前这里放得更宽，填了也会被服务端悄悄截断
        StartingGold = Math.Clamp(StartingGold, 0, 999);
        StartingIncome = Math.Clamp(StartingIncome, 0, 200);
        StartingExperience = Math.Clamp(StartingExperience, 0, 500);
        StartingPrestige = Math.Clamp(StartingPrestige, 1, 100);
        StartingHealth = Math.Clamp(StartingHealth, 50, 10000);
        PlayerCap = Math.Clamp(PlayerCap, 2, 16);
        EndlessMaxDay = Math.Clamp(EndlessMaxDay, 15, 99);
        CrossHeroOwnHeroBias = Math.Clamp(CrossHeroOwnHeroBias, 0.0, 1.0);
        BossRushTotalWaves = Math.Clamp(BossRushTotalWaves, 5, 30);
        ShopHighTierBonusPct = Math.Clamp(ShopHighTierBonusPct, 0.0, 100.0);
        ShopPriceMultiplier = Math.Clamp(ShopPriceMultiplier, 0.1, 5.0);
        MonsterLootBonusChoices = Math.Clamp(MonsterLootBonusChoices, 0, 5);
        MonsterLootTierBonus = Math.Clamp(MonsterLootTierBonus, 0, 3);
        EnemyStatMultiplier = Math.Clamp(EnemyStatMultiplier, 0.25, 5.0);
        if (CustomSeedText != null)
        {
            string s = CustomSeedText.Trim();
            CustomSeedText = s.Length == 0 ? null : (s.Length > 48 ? s.Substring(0, 48) : s);
        }
        if (!string.Equals(GhostOpponentMode, "FixedLineup", StringComparison.OrdinalIgnoreCase)) GhostOpponentMode = "Random";
        else GhostOpponentMode = "FixedLineup";
        if (SelectedGhostLineupId != null && (SelectedGhostLineupId.Trim().Length == 0 || SelectedGhostLineupId.Length > 64)) SelectedGhostLineupId = null;
        if (string.IsNullOrWhiteSpace(CustomModeId)) CustomModeId = null;
        RefreshPowerFlag();
    }

    /// <summary>D6-P1-02：统一预设/特殊模式本地默认值应用（修正 SandboxLab.StartingExperience=3 并清理前模式残留列表）。</summary>
    public void ApplyLocalPresetDefaults(string presetId, string specialModeId)
    {
        EnsureCollections();
        string normPreset = string.IsNullOrWhiteSpace(presetId) ? "PureOfficial" : presetId.Trim();
        string normMode = string.IsNullOrWhiteSpace(specialModeId) ? "Standard" : specialModeId.Trim();

        PresetId = normPreset;
        SpecialModeId = normMode;

        // 切换模式/预设时清理旧模式残留词条、日期与固定种子（肉鸽词条模式保留用户当前勾选的 MutatorIds）
        if (!string.Equals(normMode, "MutatorChallenge", StringComparison.OrdinalIgnoreCase))
        {
            MutatorIds.Clear();
        }
        if (!string.Equals(normMode, "DailyChallenge", StringComparison.OrdinalIgnoreCase))
        {
            DailyChallengeDate = null;
        }
        FixedRunSeed = null;

        if (string.Equals(normMode, "SandboxLab", StringComparison.OrdinalIgnoreCase))
        {
            StartingGold = 999;
            StartingIncome = 100;
            StartingExperience = 3;
            StartingHealth = 1000;
            StartingPrestige = 99;
            SandboxFreeInfiniteReroll = true;
            SandboxZeroShopPrice = false;
            ShopHighTierBonusPct = 0.0;
            ShopPriceMultiplier = 1.0;
            MonsterLootBonusChoices = 0;
            MonsterLootTierBonus = 0;
            EnemyStatMultiplier = 1.0;
            DisableFatesRevive = false;
            NormalizeBounds();
            return;
        }

        if (string.Equals(normPreset, "EnhancedFun", StringComparison.OrdinalIgnoreCase))
        {
            StartingGold = 25;
            StartingIncome = 7;
            StartingExperience = 6;
            StartingPrestige = 25;
            StartingHealth = 350;
            MonsterLootChooseOne = true;
            MonsterLootBonusChoices = 1;
            MonsterLootTierBonus = 0;
            ShopHighTierBonusPct = 15.0;
            ShopPriceMultiplier = 1.0;
            EnemyStatMultiplier = 1.0;
            DisableFatesRevive = false;
            NormalizeBounds();
            return;
        }

        if (string.Equals(normPreset, "ExtremeRoguelike", StringComparison.OrdinalIgnoreCase))
        {
            StartingGold = 12;
            StartingIncome = 4;
            StartingExperience = 3;
            StartingPrestige = 12;
            StartingHealth = 220;
            MonsterLootChooseOne = true;
            MonsterLootBonusChoices = 0;
            MonsterLootTierBonus = 1;
            ShopHighTierBonusPct = 20.0;
            ShopPriceMultiplier = 1.10;
            EnemyStatMultiplier = 1.25;
            DisableFatesRevive = false;
            NormalizeBounds();
            return;
        }

        StartingGold = 15;
        StartingIncome = 5;
        StartingExperience = 3;
        StartingPrestige = 20;
        StartingHealth = 300;
        MonsterLootChooseOne = true;
        MonsterLootBonusChoices = 0;
        MonsterLootTierBonus = 0;
        ShopHighTierBonusPct = 0.0;
        ShopPriceMultiplier = 1.0;
        EnemyStatMultiplier = 1.0;
        DisableFatesRevive = false;
        NormalizeBounds();
    }

    public bool ComputeCustomPowerModified()
    {
        EnsureCollections();
        if (!string.Equals(SpecialModeId, "Standard", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(PresetId, "PureOfficial", StringComparison.OrdinalIgnoreCase)
            || MutatorIds.Count > 0
            || SelectedModIds.Count > 0
            || !string.IsNullOrWhiteSpace(CustomModeId))
        {
            // 与服务端一致：种子 / 对手剧本不算改强度，只有 Mod 自定义模式算
            return true;
        }
        if (StartingGold != 15 || StartingIncome != 5 || StartingExperience != 3
            || StartingPrestige != 20 || StartingHealth != 300
            || Math.Abs(ShopHighTierBonusPct) > 1e-6
            || Math.Abs(ShopPriceMultiplier - 1.0) > 1e-6
            || MonsterLootBonusChoices != 0
            || MonsterLootTierBonus != 0
            || Math.Abs(EnemyStatMultiplier - 1.0) > 1e-6
            || DisableFatesRevive)
        {
            return true;
        }
        if (StartingItems.Count > 0 || StartingSkills.Count > 0) return true;
        if (BannedEncounterIds.Count > 0) return true;
        foreach (var kv in EncounterCategoryMultipliers)
        {
            if (kv.Value < 0.8 || kv.Value > 1.25) return true;
        }
        return false;
    }

    public void RefreshPowerFlag() => IsCustomPowerModified = ComputeCustomPowerModified();

    /// <summary>高性能手写成员深拷贝（零 JSON 序列化 GC）。</summary>
    public CustomRunRuleSet Clone()
    {
        EnsureCollections();
        var copy = new CustomRunRuleSet
        {
            SelectedArchetypeMask = SelectedArchetypeMask,
            ArchetypeWeightMultiplier = ArchetypeWeightMultiplier,
            ArchetypeMultiMatchCap = ArchetypeMultiMatchCap,
            AllowTendencySelection = AllowTendencySelection,
            PromptTendencyBeforeRun = PromptTendencyBeforeRun,
            StartingGold = StartingGold,
            StartingIncome = StartingIncome,
            StartingExperience = StartingExperience,
            StartingPrestige = StartingPrestige,
            StartingHealth = StartingHealth,
            StartingItems = new List<StartingItemRule>(StartingItems.Count),
            StartingSkills = new List<StartingSkillRule>(StartingSkills.Count),
            MonsterLootChooseOne = MonsterLootChooseOne,
            EncounterCategoryMultipliers = new Dictionary<string, double>(EncounterCategoryMultipliers, StringComparer.OrdinalIgnoreCase),
            BannedEncounterIds = new List<string>(BannedEncounterIds),
            PlayerCap = PlayerCap,
            BotNonLethalPrestigeLock = BotNonLethalPrestigeLock,
            HumanMatchEvery4Rounds = HumanMatchEvery4Rounds,
            LastHumanStandingMode = LastHumanStandingMode,
            PresetId = PresetId,
            SpecialModeId = SpecialModeId,
            MutatorIds = new List<string>(MutatorIds),
            DailyChallengeDate = DailyChallengeDate,
            FixedRunSeed = FixedRunSeed,
            EndlessMaxDay = EndlessMaxDay,
            CrossHeroOwnHeroBias = CrossHeroOwnHeroBias,
            BossRushTotalWaves = BossRushTotalWaves,
            BossRushIncludeMilestonePvp = BossRushIncludeMilestonePvp,
            SandboxFreeInfiniteReroll = SandboxFreeInfiniteReroll,
            SandboxZeroShopPrice = SandboxZeroShopPrice,
            ShopHighTierBonusPct = ShopHighTierBonusPct,
            ShopPriceMultiplier = ShopPriceMultiplier,
            MonsterLootBonusChoices = MonsterLootBonusChoices,
            MonsterLootTierBonus = MonsterLootTierBonus,
            EnemyStatMultiplier = EnemyStatMultiplier,
            DisableFatesRevive = DisableFatesRevive,
            SelectedModIds = new List<string>(SelectedModIds),
            CustomSeedText = CustomSeedText,
            GhostOpponentMode = GhostOpponentMode,
            SelectedGhostLineupId = SelectedGhostLineupId,
            CustomModeId = CustomModeId,
        };

        for (int i = 0; i < StartingItems.Count; i++)
        {
            var it = StartingItems[i];
            if (it != null)
            {
                copy.StartingItems.Add(new StartingItemRule
                {
                    TemplateId = it.TemplateId,
                    Tier = it.Tier,
                    Enchantment = it.Enchantment,
                });
            }
        }

        for (int i = 0; i < StartingSkills.Count; i++)
        {
            var sk = StartingSkills[i];
            if (sk != null)
            {
                copy.StartingSkills.Add(new StartingSkillRule
                {
                    TemplateId = sk.TemplateId,
                    Tier = sk.Tier,
                });
            }
        }

        copy.RefreshPowerFlag();
        return copy;
    }
}

/// <summary>D6-P1-12：战报回放观看视角解析结果。</summary>
internal sealed class ReplayPerspectiveResult
{
    public string LabelZh { get; set; } = string.Empty;
    public string OpponentName { get; set; } = string.Empty;
    public bool IsWin { get; set; }
    public bool IsDraw { get; set; }
}

/// <summary>
/// D6-P1-12：按当前查看账号视角判定战报回放的「胜利 / 落败 / 平局」与对手昵称，
/// 大小写无关兼容 Player1/Player2/Draw 与 p1/p2/draw 及 winnerAccountId == "__draw__"。
/// </summary>
internal static class ReplayOutcomeResolver
{
    internal static ReplayPerspectiveResult ResolvePerspective(
        string viewerAccountId,
        string p1AccountId,
        string p2AccountId,
        string opponentAccountId,
        string p1Name,
        string p2Name,
        string winnerAccountId,
        string winnerSide,
        bool? isDraw)
    {
        string v = NormalizeId(viewerAccountId);
        string p1 = NormalizeId(p1AccountId);
        string p2 = NormalizeId(p2AccountId);
        string oppAcc = NormalizeId(opponentAccountId);
        string winAcc = NormalizeId(winnerAccountId);
        string ws = (winnerSide ?? string.Empty).Trim();

        bool viewerIsP1 = !string.IsNullOrEmpty(v) && !string.IsNullOrEmpty(p1)
            && string.Equals(v, p1, StringComparison.OrdinalIgnoreCase);
        bool viewerIsP2 = !viewerIsP1 && !string.IsNullOrEmpty(v) && !string.IsNullOrEmpty(p2)
            && string.Equals(v, p2, StringComparison.OrdinalIgnoreCase);
        if (!viewerIsP1 && !viewerIsP2 && !string.IsNullOrEmpty(oppAcc) && !string.IsNullOrEmpty(p1)
            && string.Equals(oppAcc, p1, StringComparison.OrdinalIgnoreCase))
        {
            viewerIsP2 = true;
        }

        string oppName = viewerIsP2
            ? (!string.IsNullOrWhiteSpace(p1Name) ? p1Name.Trim() : "对手")
            : (!string.IsNullOrWhiteSpace(p2Name) ? p2Name.Trim() : "对手");

        bool draw = isDraw == true
            || string.Equals(winnerAccountId?.Trim(), "__draw__", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ws, "draw", StringComparison.OrdinalIgnoreCase);

        bool isWin = false;
        if (!draw)
        {
            if (!string.IsNullOrEmpty(winAcc) && !string.IsNullOrEmpty(v))
            {
                isWin = string.Equals(winAcc, v, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                bool p1Won = string.Equals(ws, "Player1", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ws, "p1", StringComparison.OrdinalIgnoreCase);
                bool p2Won = string.Equals(ws, "Player2", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ws, "p2", StringComparison.OrdinalIgnoreCase);
                isWin = viewerIsP2 ? p2Won : p1Won;
            }
        }

        return new ReplayPerspectiveResult
        {
            LabelZh = draw ? "平局" : (isWin ? "胜利" : "落败"),
            OpponentName = oppName,
            IsWin = !draw && isWin,
            IsDraw = draw,
        };
    }

    private static string NormalizeId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        return raw.Replace("-", string.Empty).Trim();
    }
}

/// <summary>
/// CustomGameplayUI 数据模型、规则摘要与零序列化手写深拷贝工具（完整对齐 Wave 2/3 的 3 套预设档位 + 7 大玩法模式 + 14 条肉鸽突变词条）。
/// </summary>
internal sealed partial class CustomGameplayUI
{
    internal enum UiTab
    {
        Normal = 0,
        Tournament = 1,
        Workshop = 2,
        Social = 3,
        Performance = 4,
    }

    internal sealed class ArchetypeCatalogEntry
    {
        [JsonProperty("bit")] public int Bit { get; set; }
        [JsonProperty("key")] public string Key { get; set; } = string.Empty;
        [JsonProperty("nameZh")] public string NameZh { get; set; } = string.Empty;
        [JsonProperty("descZh")] public string DescZh { get; set; } = string.Empty;
        [JsonProperty("heroCounts")] public Dictionary<string, int> HeroCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public int GetHeroCount(string hero)
        {
            if (HeroCounts == null || string.IsNullOrWhiteSpace(hero)) return 0;
            if (HeroCounts.TryGetValue(hero, out int c)) return c;
            foreach (var kv in HeroCounts)
            {
                if (string.Equals(kv.Key, hero, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return 0;
        }
    }

    internal sealed class EncounterCategoryEntry
    {
        [JsonProperty("key")] public string Key { get; set; } = string.Empty;
        [JsonProperty("groupZh")] public string GroupZh { get; set; } = string.Empty;
        [JsonProperty("nameZh")] public string NameZh { get; set; } = string.Empty;
        [JsonProperty("defaultMult")] public double DefaultMult { get; set; } = 1.0;
    }

    internal sealed class CatalogCardEntry
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("nameZh")] public string NameZh { get; set; } = string.Empty;
        [JsonProperty("hero")] public string Hero { get; set; } = string.Empty;
        [JsonProperty("size")] public string Size { get; set; } = string.Empty;
        [JsonProperty("startingTier")] public string StartingTier { get; set; } = "Bronze";
        [JsonProperty("archetypeMask")] public ulong ArchetypeMask { get; set; }
    }

    internal sealed class MutatorCatalogItem
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("nameZh")] public string NameZh { get; set; } = string.Empty;
        [JsonProperty("descZh")] public string DescZh { get; set; } = string.Empty;
        [JsonProperty("conflictGroup")] public string ConflictGroup { get; set; }
        [JsonProperty("scoreMultiplierDelta")] public double ScoreMultiplierDelta { get; set; }
    }

    internal sealed class PresetCatalogItem
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("nameZh")] public string NameZh { get; set; } = string.Empty;
        [JsonProperty("descZh")] public string DescZh { get; set; } = string.Empty;
    }

    internal sealed class SpecialModeCatalogItem
    {
        [JsonProperty("id")] public string Id { get; set; } = string.Empty;
        [JsonProperty("nameZh")] public string NameZh { get; set; } = string.Empty;
        [JsonProperty("descZh")] public string DescZh { get; set; } = string.Empty;
    }

    internal sealed class CustomRulesCatalog
    {
        [JsonProperty("archetypes")] public List<ArchetypeCatalogEntry> Archetypes { get; set; } = new();
        [JsonProperty("encounterCategories")] public List<EncounterCategoryEntry> EncounterCategories { get; set; } = new();
        [JsonProperty("items")] public List<CatalogCardEntry> Items { get; set; } = new();
        [JsonProperty("skills")] public List<CatalogCardEntry> Skills { get; set; } = new();
        [JsonProperty("mutators")] public List<MutatorCatalogItem> Mutators { get; set; } = new();
        [JsonProperty("presets")] public List<PresetCatalogItem> Presets { get; set; } = new();
        [JsonProperty("specialModes")] public List<SpecialModeCatalogItem> SpecialModes { get; set; } = new();
    }

    internal sealed class LobbyMemberSummary
    {
        public string AccountId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Hero { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public bool IsAi { get; set; }
    }

    internal sealed class RequiredModEntry
    {
        public string ModId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public int CustomCardCount { get; set; }
        public int CustomEventCount { get; set; }
        public int AssetCount { get; set; }
        public bool AvailableOnServer { get; set; } = true;
        public string DownloadUrl { get; set; } = string.Empty;
    }

    internal sealed class LobbyDetailSnapshot
    {
        public string LobbyId { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string HostAccountId { get; set; } = string.Empty;
        public int PlayerCap { get; set; } = 8;
        public List<LobbyMemberSummary> Members { get; set; } = new();
        public List<RequiredModEntry> RequiredMods { get; set; } = new();
        public CustomRunRuleSet Rules { get; set; }
    }

    internal sealed class RuleEditorBuffer
    {
        public string GoldText = "15";
        public string IncomeText = "5";
        public string ExperienceText = "3";
        public string PrestigeText = "20";
        public string HealthText = "300";

        public string ItemSearch = string.Empty;
        public bool ItemHeroFilterOnly = true;
        public int ItemTierIdx;
        public int ItemEnchantIdx;

        public string SkillSearch = string.Empty;
        public bool SkillHeroFilterOnly = true;
        public int SkillTierIdx;

        public void SyncFromRules(CustomRunRuleSet rules)
        {
            if (rules == null) return;
            GoldText = rules.StartingGold.ToString();
            IncomeText = rules.StartingIncome.ToString();
            ExperienceText = rules.StartingExperience.ToString();
            PrestigeText = rules.StartingPrestige.ToString();
            HealthText = rules.StartingHealth.ToString();
        }
    }

    internal readonly struct RuleActivationSummary
    {
        public readonly bool IsCustomActive;
        public readonly bool IsPowerModified;
        public readonly int ActiveCount;
        public readonly string SummaryText;

        public RuleActivationSummary(bool isCustomActive, bool isPowerModified, int activeCount, string summaryText)
        {
            IsCustomActive = isCustomActive;
            IsPowerModified = isPowerModified;
            ActiveCount = activeCount;
            SummaryText = summaryText;
        }
    }

    internal static string GetModeDisplayZh(string modeId, string presetId)
    {
        if (string.Equals(modeId, "EndlessTower", StringComparison.OrdinalIgnoreCase)) return "无尽爬塔";
        if (string.Equals(modeId, "CrossHeroMayhem", StringComparison.OrdinalIgnoreCase)) return "跨职业混池";
        if (string.Equals(modeId, "DailyChallenge", StringComparison.OrdinalIgnoreCase)) return "每日同种子挑战";
        if (string.Equals(modeId, "MutatorChallenge", StringComparison.OrdinalIgnoreCase)) return "肉鸽突变词条";
        if (string.Equals(modeId, "BossRush", StringComparison.OrdinalIgnoreCase)) return "Boss车轮战";
        if (string.Equals(modeId, "SandboxLab", StringComparison.OrdinalIgnoreCase)) return "沙盒实验室";
        if (string.Equals(presetId, "EnhancedFun", StringComparison.OrdinalIgnoreCase)) return "爽玩增强";
        if (string.Equals(presetId, "ExtremeRoguelike", StringComparison.OrdinalIgnoreCase)) return "极限肉鸽";
        return "经典官方";
    }

    internal static string TranslateTierZh(string tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return "青铜";
        if (string.Equals(tier, "Bronze", StringComparison.OrdinalIgnoreCase)) return "青铜";
        if (string.Equals(tier, "Silver", StringComparison.OrdinalIgnoreCase)) return "白银";
        if (string.Equals(tier, "Gold", StringComparison.OrdinalIgnoreCase)) return "黄金";
        if (string.Equals(tier, "Diamond", StringComparison.OrdinalIgnoreCase)) return "钻石";
        if (string.Equals(tier, "Legendary", StringComparison.OrdinalIgnoreCase)) return "传奇";
        return tier;
    }

    internal static RuleActivationSummary SummarizeRules(CustomRunRuleSet rules, bool isTournament)
    {
        if (rules == null)
        {
            return new RuleActivationSummary(false, false, 0, "经典官方");
        }
        rules.EnsureCollections();

        var tags = new List<string>(8);

        string modeLabel = GetModeDisplayZh(rules.SpecialModeId, rules.PresetId);
        if (!string.Equals(modeLabel, "经典官方", StringComparison.Ordinal))
        {
            tags.Add(modeLabel);
        }

        if (rules.MutatorIds.Count > 0)
        {
            tags.Add($"词条×{rules.MutatorIds.Count}");
        }

        int archCount = CountBits(rules.SelectedArchetypeMask);
        if (archCount > 0)
        {
            tags.Add($"流派倾向×{archCount}");
        }

        if (rules.StartingGold != 15 || rules.StartingIncome != 5 || rules.StartingExperience != 3
            || rules.StartingPrestige != 20 || rules.StartingHealth != 300)
        {
            tags.Add("开局预设");
        }

        int cardCount = rules.StartingItems.Count + rules.StartingSkills.Count;
        if (cardCount > 0)
        {
            tags.Add($"固定发牌×{cardCount}");
        }

        if (!rules.MonsterLootChooseOne)
        {
            tags.Add("野怪单掉");
        }

        bool hasMultOverride = false;
        foreach (var kv in rules.EncounterCategoryMultipliers)
        {
            if (Math.Abs(kv.Value - 1.0) > 0.001)
            {
                hasMultOverride = true;
                break;
            }
        }
        if (hasMultOverride || rules.BannedEncounterIds.Count > 0)
        {
            tags.Add("事件调整");
        }

        if (rules.MonsterLootBonusChoices > 0 || rules.MonsterLootTierBonus > 0
            || Math.Abs(rules.ShopHighTierBonusPct) > 0.01
            || Math.Abs(rules.ShopPriceMultiplier - 1.0) > 0.01
            || Math.Abs(rules.EnemyStatMultiplier - 1.0) > 0.01
            || rules.DisableFatesRevive)
        {
            tags.Add("进阶调参");
        }

        if (!isTournament)
        {
            // 每日挑战用当天官方种子，自填的种子不生效，就不标了
            bool isDaily = string.Equals(rules.SpecialModeId, "DailyChallenge", StringComparison.OrdinalIgnoreCase);
            if (!isDaily && !string.IsNullOrWhiteSpace(rules.CustomSeedText)) tags.Add("固定种子");
            if (rules.UsesFixedLineup) tags.Add("固定对手方案");
        }

        if (isTournament && (rules.PlayerCap != 8 || !rules.BotNonLethalPrestigeLock
                             || !rules.HumanMatchEvery4Rounds || !rules.LastHumanStandingMode
                             || !rules.AllowTendencySelection))
        {
            tags.Add(rules.PlayerCap != 8 ? $"{rules.PlayerCap}人赛制" : "赛制调整");
        }

        bool powerMod = rules.ComputeCustomPowerModified();
        bool customActive = tags.Count > 0;
        string summary = customActive ? string.Join(" / ", tags) : "经典官方";
        return new RuleActivationSummary(customActive, powerMod, tags.Count, summary);
    }

    internal static readonly string[] Tiers = { "Bronze", "Silver", "Gold", "Diamond", "Legendary" };
    internal static readonly string[] TierLabelsZh = { "青铜", "白银", "黄金", "钻石", "传奇" };

    internal static readonly string[] LeaderboardBoardKeys =
    {
        "endless",
        "boss_rush",
        "daily:today",
        "ranked",
        "mutator",
        "cross_hero",
        "tournament",
    };

    internal static readonly (string Key, string LabelZh)[] LeaderboardBoards =
    {
        ("endless", "无尽爬塔榜"),
        ("boss_rush", "Boss车轮榜"),
        ("daily:today", "每日挑战榜"),
        ("ranked", "赛季排位榜"),
        ("mutator", "肉鸽词条榜"),
        ("cross_hero", "跨职业混池榜"),
        ("tournament", "锦标赛冠军榜"),
    };

    private static readonly (string Key, string LabelZh)[] Enchantments =
    {
        ("", "无附魔"),
        ("Fiery", "炽焰"),
        ("Toxic", "剧毒"),
        ("Shielded", "护盾"),
        ("Heavy", "沉重"),
        ("Icy", "冰霜"),
        ("Turbo", "极速"),
        ("Restorative", "治愈"),
        ("Deadly", "致命"),
        ("Golden", "黄金"),
        ("Radiant", "辉光"),
        ("Obsidian", "黑曜"),
        ("Shiny", "闪耀"),
        ("Mossy", "青苔"),
        ("Mystical", "秘法"),
        ("Ethereal", "虚灵"),
        ("Tiny", "微型"),
    };

    private static readonly (string Key, string ShortZh)[] QuickSwitchHeroes =
    {
        ("Vanessa", "瓦内莎"),
        ("Pygmalien", "皮格马利翁"),
        ("Dooley", "杜利"),
        ("Mak", "马克"),
        ("Jules", "朱尔斯"),
        ("Stelle", "斯黛拉"),
        ("Karnok", "卡诺克"),
        ("Hero8", "双龙"),
    };

    private static readonly Dictionary<string, string> HeroNameZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Vanessa"] = "瓦内莎",
        ["Pygmalien"] = "皮格马利翁",
        ["Dooley"] = "杜利",
        ["Mak"] = "马克",
        ["Stelle"] = "斯黛拉",
        ["Jules"] = "朱尔斯",
        ["Karnok"] = "卡诺克",
        ["Hero8"] = "双龙",
        ["Common"] = "通用",
    };

    private static readonly List<EncounterCategoryEntry> FallbackEncounterCategories = new()
    {
        new() { Key = "PedestalEnchant", GroupZh = "祭坛强化", NameZh = "指定附魔祭坛", DefaultMult = 1.0 },
        new() { Key = "PedestalEnchantRandom", GroupZh = "祭坛强化", NameZh = "随机附魔祭坛", DefaultMult = 1.0 },
        new() { Key = "PedestalUpgrade", GroupZh = "祭坛强化", NameZh = "升阶改造祭坛", DefaultMult = 1.0 },
        new() { Key = "EventCultAndMystery", GroupZh = "奇遇事件", NameZh = "邪教与神秘事件", DefaultMult = 1.0 },
        new() { Key = "EventExpeditionAgent", GroupZh = "奇遇事件", NameZh = "探险与特工事件", DefaultMult = 1.0 },
        new() { Key = "EventEconomyStats", GroupZh = "奇遇事件", NameZh = "经济与属性事件", DefaultMult = 1.0 },
        new() { Key = "EventFreeDealTransform", GroupZh = "奇遇事件", NameZh = "免费交易与变形事件", DefaultMult = 1.0 },
        new() { Key = "MerchantItems", GroupZh = "商店商人", NameZh = "常规物品商人", DefaultMult = 1.0 },
        new() { Key = "MerchantSkillsRare", GroupZh = "商店商人", NameZh = "技能与稀有商人", DefaultMult = 1.0 },
        new() { Key = "Monsters", GroupZh = "野怪战斗", NameZh = "PvE 野怪遭遇", DefaultMult = 1.0 },
    };
}
