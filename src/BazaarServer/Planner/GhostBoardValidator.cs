using System.Globalization;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Mods;

namespace BazaarServer.Planner;

/// <summary>单盘面校验与归一化结果。</summary>
public sealed class GhostBoardValidationResult
{
    public bool Ok => Errors.Count == 0;
    public PvpGhostPool.GhostBoard Board { get; set; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>编排台请求中的单个物品输入（兼容 GhostItem 全部字段 + 前端手改 editedAttributes）。</summary>
public sealed class PlannerItemInput
{
    public int Slot { get; set; }
    public int Width { get; set; } = 1;
    public string TemplateId { get; set; } = "";
    public string InternalName { get; set; } = "";
    public string ZhName { get; set; } = "";
    public string Tier { get; set; } = "Bronze";
    public string? Enchantment { get; set; }
    public Dictionary<string, int>? PermanentAttributes { get; set; }
    public Dictionary<string, double>? PermanentAdd { get; set; }
    public Dictionary<string, double>? PermanentMult { get; set; }
    public List<string>? CompletedQuests { get; set; }
    public Dictionary<string, double>? QuestProgress { get; set; }
    public List<string>? ExtraTags { get; set; }

    /// <summary>前端手改的当前品阶目标最终数值（折算进 PermanentAdd 后在输出中移除）。</summary>
    public Dictionary<string, double>? EditedAttributes { get; set; }
}

/// <summary>编排台请求中的单个技能输入（兼容 GhostSkill 全部字段 + 前端手改 editedAttributes）。</summary>
public sealed class PlannerSkillInput
{
    public string TemplateId { get; set; } = "";
    public string InternalName { get; set; } = "";
    public string ZhName { get; set; } = "";
    public string Tier { get; set; } = "Bronze";
    public Dictionary<string, int>? PermanentAttributes { get; set; }
    public Dictionary<string, double>? PermanentAdd { get; set; }
    public Dictionary<string, double>? PermanentMult { get; set; }
    public List<string>? ExtraTags { get; set; }

    /// <summary>前端手改的当前品阶目标最终数值（折算进 PermanentAdd 后在输出中移除）。</summary>
    public Dictionary<string, double>? EditedAttributes { get; set; }
}

/// <summary>编排台请求中的插座效果输入。</summary>
public sealed class PlannerSocketEffectInput
{
    public int Slot { get; set; }
    public string TemplateId { get; set; } = "";
    public string Tier { get; set; } = "Bronze";
    public string? Enchantment { get; set; }
    public Dictionary<string, int>? PermanentAttributes { get; set; }
}

/// <summary>编排台请求中的完整盘面输入（支持 JsonElement 与强类型双向接入）。</summary>
public sealed class PlannerBoardInput
{
    public string? BoardId { get; set; }
    public int Day { get; set; } = 1;
    public int SourceDay { get; set; }
    public int TargetDay { get; set; }
    public string? Hero { get; set; }
    public int Level { get; set; } = 1;
    public int HealthMax { get; set; } = GameDefaults.StartingHealth;
    public int Prestige { get; set; } = GameDefaults.StartingPrestige;
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int UnlockedSlotsMask { get; set; }
    public string? PowerBucket { get; set; }
    public double EstimatedWinRate { get; set; } = 0.5;
    public string? Source { get; set; }
    public string? Persona { get; set; }
    public string? Archetype { get; set; }
    public bool IsPlayerGhost { get; set; }
    public bool IsCustomRule { get; set; }
    public string? PoolPartition { get; set; }
    public string? DisplayName { get; set; }
    public List<PlannerItemInput>? Items { get; set; }
    public List<PlannerSkillInput>? Skills { get; set; }
    public List<PlannerSocketEffectInput>? SocketEffects { get; set; }
}

/// <summary>
/// 镜像盘面合法性校验器、基础属性目录构建器与手改数值（editedAttributes → permanentAdd）幂等折算器：
/// - 只防崩溃、越界、脏数据与频率类属性篡改，绝不做平衡性限制；
/// - 与 <see cref="PvpGhostPool.ToPlayerState"/> 共用同一套 CardInstance 叠算规则，保证折算 100% 幂等；
/// - 从镜像池克隆、未被编辑的卡牌保留原有数量类 PermanentAdd/PermanentMult、CompletedQuests、QuestProgress、ExtraTags 与 SocketEffects。
/// </summary>
public static class GhostBoardValidator
{
    private const int MaxItems = 10;
    private const int MaxSkills = 20;
    private const int MaxSocketEffects = 10;
    private const int MaxWinsLosses = 99;
    private const double MaxPermanentMult = 1_000_000.0;
    private const double MaxNonQuantityAbsValue = 10_000_000.0;

    private static readonly Dictionary<string, string> CanonicalQuantityKeyMap = BuildCanonicalQuantityKeyMap();

    /// <summary>由镜像序列化自动派生的只读元属性键（非玩家手改或真实战斗属性，不触发篡改警告）。</summary>
    private static readonly HashSet<string> DerivedGhostAttributeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "BuyPrice",
        "QuestCompletedCount",
    };

    /// <summary>
    /// 严禁外部篡改的频率 / 冷却 / 多重触发 / 弹药 / 加速 / 减速 / 冰冻 / 充能 / 节奏与目标数类属性黑名单。
    /// 未编辑克隆卡的非频率合法成长（如 Lifesteal、SellPrice、Value、JoyApplyAmount、Custom_* 等）放行保留。
    /// </summary>
    private static readonly HashSet<string> BlockedFrequencyAttributeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cooldown",
        // "CooldownMax" 不在这里：冷却允许在编排台手改（最低 1 秒，被动物品不能加冷却），见 IsCooldownKey / QuantityMinFor
        "CooldownDisabled",
        "FlatCooldownReduction",
        "PercentCooldownReduction",
        "Multicast",
        "Ammo",
        "AmmoMax",
        "ReloadAmount",
        "ReloadTargets",
        "ChargeAmount",
        "ChargeTargets",
        "PercentChargeReduction",
        "Haste",
        "HasteAmount",
        "HasteTargets",
        "PercentHasteReduction",
        "Slow",
        "SlowAmount",
        "SlowTargets",
        "PercentSlowReduction",
        "Freeze",
        "FreezeAmount",
        "FreezeTargets",
        "PercentFreezeReduction",
        "TempoCost",
        "FlatTempoCostReduction",
        "PercentTempoCostReduction",
        "TempoApplyAmount",
        "TempoRemoveAmount",
        "ForceUseTargets",
        "DisableTargets",
        "DestroyTargets",
        "EnchantTargets",
        "UpgradeTargets",
        "TransformTargets",
        "RepairTargets",
    };

    private const string CooldownKey = "CooldownMax";

    /// <summary>手改冷却的下限（毫秒），与战斗引擎默认冷却下限一致；低于它在战斗里也会被抬到 1 秒。</summary>
    public const double MinEditableCooldownMs = 1000.0;

    private static bool IsCooldownKey(string? key)
        => string.Equals(key?.Trim(), CooldownKey, StringComparison.OrdinalIgnoreCase);

    /// <summary>卡牌本身有冷却（主动物品）；被动物品的冷却为 0，不允许凭空加冷却。</summary>
    private static bool HasNativeCooldown(CardInstance card)
        => card.BaseAttributes.TryGetValue(CooldownKey, out double v) && double.IsFinite(v) && v > 0;

    private static double QuantityMinFor(CardInstance card, string key)
        => IsCooldownKey(key) && HasNativeCooldown(card) ? MinEditableCooldownMs : 0.0;

    /// <summary>冷却按「未扣附魔减免、未套战斗下限」的原始值存取（(基础 + 增量) × 倍率），保证折算幂等。</summary>
    private static double RawLayeredValue(CardInstance card, string key)
    {
        double baseVal = card.BaseAttributes.GetValueOrDefault(key, 0.0);
        double add = card.PermanentAdd.TryGetValue(key, out double a) && double.IsFinite(a) ? a : 0.0;
        double mult = card.PermanentMult.TryGetValue(key, out double m) && double.IsFinite(m) ? m : 1.0;
        return (baseVal + add) * mult;
    }

    private static Dictionary<string, string> BuildCanonicalQuantityKeyMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in PlannerCatalogConstants.QuantityAttributes)
            map[key] = key;
        return map;
    }

    /// <summary>判断属性键是否属于允许手改的 7 项数量类属性，并返回标准 PascalCase 键名。</summary>
    public static bool TryGetCanonicalQuantityKey(string? rawKey, out string canonicalKey)
    {
        canonicalKey = "";
        if (string.IsNullOrWhiteSpace(rawKey)) return false;
        return CanonicalQuantityKeyMap.TryGetValue(rawKey.Trim(), out canonicalKey!);
    }

    /// <summary>判断属性键是否属于禁止外部篡改的频率 / 控制 / 目标类属性。</summary>
    public static bool IsBlockedFrequencyAttribute(string? rawKey)
        => !string.IsNullOrWhiteSpace(rawKey) && BlockedFrequencyAttributeKeys.Contains(rawKey.Trim());

    /// <summary>
    /// 判断既有克隆卡上的属性键是否允许保留（属于 7 项数量类，或是不在频率黑名单且为合法 <see cref="ECardAttributeType"/> 的非频率属性）。
    /// </summary>
    private static bool TryResolveAllowedNonFrequencyKey(string? rawKey, out string canonicalKey)
    {
        canonicalKey = "";
        if (string.IsNullOrWhiteSpace(rawKey)) return false;
        string trimmed = rawKey.Trim();
        if (TryGetCanonicalQuantityKey(trimmed, out canonicalKey))
            return true;
        if (DerivedGhostAttributeKeys.Contains(trimmed) || BlockedFrequencyAttributeKeys.Contains(trimmed))
            return false;
        if (Enum.TryParse<ECardAttributeType>(trimmed, ignoreCase: true, out var parsedAttr))
        {
            canonicalKey = parsedAttr.ToString();
            return true;
        }
        return false;
    }

    private static void AddWarningOnce(List<string> warnings, string message)
    {
        if (!string.IsNullOrWhiteSpace(message) && !warnings.Contains(message, StringComparer.Ordinal))
            warnings.Add(message);
    }

    // ── 1. 卡牌目录公共工具（供 catalog/cards 与校验器共用）────────────────────

    /// <summary>返回卡牌支持的所有品阶名称列表（按 Bronze → Silver → Gold → Diamond → Legendary 升序）。</summary>
    public static List<string> GetSupportedTiers(CardTemplate tpl)
    {
        ArgumentNullException.ThrowIfNull(tpl);
        var result = new List<string>(5);
        if (tpl.Tiers is { Count: > 0 })
        {
            var keys = new HashSet<string>(tpl.Tiers.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (string tier in PlannerCatalogConstants.TierOrder)
            {
                if (keys.Contains(tier))
                    result.Add(tier);
            }
        }

        if (result.Count == 0)
        {
            string fallback = NormalizeTierName(tpl.StartingTier) ?? "Bronze";
            result.Add(fallback);
        }

        return result;
    }

    /// <summary>返回卡牌支持的所有合法附魔名称列表（无附魔或不可附魔返回空列表）。</summary>
    public static List<string> GetSupportedEnchantments(CardTemplate tpl)
    {
        ArgumentNullException.ThrowIfNull(tpl);
        var result = new List<string>();
        if (tpl.Enchantments is not { Count: > 0 })
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawKey in tpl.Enchantments.Keys)
        {
            if (string.IsNullOrWhiteSpace(rawKey)) continue;
            if (!Enum.TryParse<EEnchantmentType>(rawKey.Trim(), ignoreCase: true, out var parsed)) continue;
            string canonical = parsed.ToString();
            if (seen.Add(canonical))
                result.Add(canonical);
        }

        return result;
    }

    /// <summary>
    /// 构建该卡各支持品阶下的数量类基础属性字典（仅抽取 <see cref="PlannerCatalogConstants.QuantityAttributes"/>
    /// 中真实存在于 <see cref="CardInstance.BaseAttributes"/> 的键，没有的键省略）。
    /// </summary>
    public static Dictionary<string, Dictionary<string, int>> BuildBaseAttributesByTier(CardTemplate tpl)
    {
        ArgumentNullException.ThrowIfNull(tpl);
        var byTier = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (string tierName in GetSupportedTiers(tpl))
        {
            var card = new CardInstance(tpl, TierHelper.ParseTier(tierName), null, "cat");
            var attrs = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string key in PlannerCatalogConstants.QuantityAttributes)
            {
                if (card.BaseAttributes.TryGetValue(key, out double val) && double.IsFinite(val))
                {
                    if (IsCooldownKey(key) && val <= 0) continue; // 被动物品没有冷却，不给前端显示冷却输入框
                    attrs[key] = (int)Math.Round(val);
                }
            }
            byTier[tierName] = attrs;
        }
        return byTier;
    }

    /// <summary>解析卡牌的简体中文显示名（优先 GetLocalizedTitle，依次回退本地化键、DisplayTitle 与 InternalName）。</summary>
    public static string ResolveCardNameZh(CardTemplate tpl, GameDataRepository? repo)
    {
        if (tpl == null) return "";
        string? title = repo?.Localization?.GetLocalizedTitle(tpl);
        if (!string.IsNullOrWhiteSpace(title))
            return title.Trim();
        string? localized = repo?.Localization?.GetLocalizedText(tpl.Localization?.Title);
        if (!string.IsNullOrWhiteSpace(localized))
            return localized.Trim();
        if (!string.IsNullOrWhiteSpace(tpl.DisplayTitle))
            return tpl.DisplayTitle.Trim();
        return tpl.InternalName?.Trim() ?? tpl.Id;
    }

    // ── 2. 盘面校验与归一化入口（JsonElement / PlannerBoardInput / GhostBoard）───

    /// <summary>
    /// 校验并归一化来自 JSON 请求体（如 validate-board / POST lineups）的盘面：
    /// 容错解析数值字段与前端手改 <c>editedAttributes</c>，防崩溃与溢出，折算后剥离 <c>editedAttributes</c>。
    /// </summary>
    public static GhostBoardValidationResult ValidateAndNormalize(
        int day,
        JsonElement boardJson,
        GameDataRepository effectiveRepo,
        CardPool effectivePool,
        ServerModHost? modHost = null,
        IReadOnlySet<string>? explicitAllowedModIds = null)
    {
        var parseErrors = new List<string>();
        var input = ParseBoardFromJson(boardJson, day, parseErrors);
        var result = ValidateAndNormalize(
            day,
            input,
            effectiveRepo,
            effectivePool,
            modHost,
            explicitAllowedModIds);
        if (parseErrors.Count > 0)
            result.Errors.InsertRange(0, parseErrors);
        return result;
    }

    /// <summary>校验并归一化已反序列化的 <see cref="PvpGhostPool.GhostBoard"/>（供 BZR1 蓝图导入与单测直接调用）。</summary>
    public static GhostBoardValidationResult ValidateAndNormalize(
        int day,
        PvpGhostPool.GhostBoard? board,
        GameDataRepository effectiveRepo,
        CardPool effectivePool,
        ServerModHost? modHost = null,
        IReadOnlySet<string>? explicitAllowedModIds = null)
    {
        var input = board != null ? ToBoardInput(board) : null;
        return ValidateAndNormalize(
            day,
            input,
            effectiveRepo,
            effectivePool,
            modHost,
            explicitAllowedModIds);
    }

    /// <summary>核心盘面校验、频率类属性剥除/钳回与数量类属性幂等折算实现。</summary>
    public static GhostBoardValidationResult ValidateAndNormalize(
        int day,
        PlannerBoardInput? input,
        GameDataRepository effectiveRepo,
        CardPool effectivePool,
        ServerModHost? modHost = null,
        IReadOnlySet<string>? explicitAllowedModIds = null)
    {
        ArgumentNullException.ThrowIfNull(effectiveRepo);
        ArgumentNullException.ThrowIfNull(effectivePool);

        var result = new GhostBoardValidationResult();
        if (input == null)
        {
            result.Errors.Add("盘面数据不能为空");
            return result;
        }

        if (day < 1 || day > PlannerCatalogConstants.MaxLineupDays)
            result.Errors.Add($"天数要在 1–{PlannerCatalogConstants.MaxLineupDays} 之间");

        int safeDay = Math.Clamp(day, 1, PlannerCatalogConstants.MaxLineupDays);

        // 1) 英雄校验
        string? normalizedHero = PlannerCatalogConstants.NormalizeHeroId(input.Hero);
        if (normalizedHero == null)
        {
            result.Errors.Add($"不认识的英雄：{input.Hero}");
            normalizedHero = "Vanessa";
        }

        // 2) 顶层数值防溢出校验
        int healthMax = input.HealthMax;
        if (healthMax < PlannerCatalogConstants.HealthMin || healthMax > PlannerCatalogConstants.HealthMax)
        {
            result.Errors.Add($"最大生命要在 {PlannerCatalogConstants.HealthMin}–{PlannerCatalogConstants.HealthMax} 之间");
            healthMax = Math.Clamp(healthMax, PlannerCatalogConstants.HealthMin, PlannerCatalogConstants.HealthMax);
        }

        int level = input.Level;
        if (level < PlannerCatalogConstants.LevelMin || level > PlannerCatalogConstants.LevelMax)
        {
            result.Errors.Add($"等级要在 {PlannerCatalogConstants.LevelMin}–{PlannerCatalogConstants.LevelMax} 之间");
            level = Math.Clamp(level, PlannerCatalogConstants.LevelMin, PlannerCatalogConstants.LevelMax);
        }

        int prestige = input.Prestige;
        if (prestige < PlannerCatalogConstants.PrestigeMin || prestige > PlannerCatalogConstants.PrestigeMax)
        {
            result.Errors.Add($"声望要在 {PlannerCatalogConstants.PrestigeMin}–{PlannerCatalogConstants.PrestigeMax} 之间");
            prestige = Math.Clamp(prestige, PlannerCatalogConstants.PrestigeMin, PlannerCatalogConstants.PrestigeMax);
        }

        int wins = input.Wins;
        if (wins < 0 || wins > MaxWinsLosses)
        {
            result.Errors.Add($"胜场要在 0–{MaxWinsLosses} 之间");
            wins = Math.Clamp(wins, 0, MaxWinsLosses);
        }

        int losses = input.Losses;
        if (losses < 0 || losses > MaxWinsLosses)
        {
            result.Errors.Add($"败场要在 0–{MaxWinsLosses} 之间");
            losses = Math.Clamp(losses, 0, MaxWinsLosses);
        }

        double estWinRate = input.EstimatedWinRate;
        if (!double.IsFinite(estWinRate))
        {
            result.Errors.Add("盘面胜率估计值无效");
            estWinRate = 0.5;
        }
        else
        {
            estWinRate = Math.Clamp(estWinRate, 0.0, 1.0);
        }

        // 3) 名牌文本清洗（先 Trim 防前导空白挤占字数上限，再剥离 TMP 富文本与控制符，超 24 字报错并截断，空串归一化为 null）
        string? normalizedDisplayName = null;
        if (!string.IsNullOrWhiteSpace(input.DisplayName))
        {
            string cleaned = AccountStore.SanitizeDisplayText(input.DisplayName.Trim(), maxLength: PlannerCatalogConstants.MaxDisplayName + 64);
            if (cleaned.Length > PlannerCatalogConstants.MaxDisplayName)
            {
                result.Errors.Add($"名牌最多 {PlannerCatalogConstants.MaxDisplayName} 个字");
                cleaned = cleaned[..PlannerCatalogConstants.MaxDisplayName].Trim();
            }
            if (cleaned.Length > 0)
                normalizedDisplayName = cleaned;
        }

        // 4) 物品校验与数值折算
        var rawItems = input.Items ?? new List<PlannerItemInput>();
        if (rawItems.Count > MaxItems)
            result.Errors.Add($"物品最多 {MaxItems} 件");

        var occupiedBy = new string?[10];
        int totalItemWidth = 0;
        int unlockedMask = PvpGhostPool.DefaultUnlockedMaskForDay(safeDay);
        var normalizedItems = new List<PvpGhostPool.GhostItem>(Math.Min(rawItems.Count, MaxItems));

        for (int i = 0; i < rawItems.Count && i < MaxItems; i++)
        {
            var item = rawItems[i];
            if (item == null)
            {
                result.Errors.Add($"第 {i + 1} 件物品数据为空");
                continue;
            }

            string tplId = item.TemplateId?.Trim() ?? "";
            var tpl = ResolveTemplateOrReportError(
                tplId,
                expectedType: "Item",
                slotLabel: $"第 {item.Slot + 1} 格的物品",
                effectiveRepo,
                effectivePool,
                modHost,
                result.Errors);
            if (tpl == null)
                continue;

            string zhName = ResolveCardNameZh(tpl, effectiveRepo);
            int width = Math.Clamp(tpl.CardSize.SlotCount(), 1, 3);
            totalItemWidth += width;

            // 品阶校验
            var supportedTiers = GetSupportedTiers(tpl);
            string? canonicalTier = MatchSupportedValue(item.Tier, supportedTiers);
            if (canonicalTier == null)
            {
                string tierLabels = string.Join("/", supportedTiers.Select(PlannerCatalogConstants.GetTierNameZh));
                result.Errors.Add($"「{zhName}」没有{PlannerCatalogConstants.GetTierNameZh(item.Tier)}品阶（可选：{tierLabels}）");
                canonicalTier = supportedTiers[0];
            }

            // 附魔校验
            string? canonicalEnchantment = null;
            if (!string.IsNullOrWhiteSpace(item.Enchantment))
            {
                var supportedEnchants = GetSupportedEnchantments(tpl);
                canonicalEnchantment = MatchSupportedValue(item.Enchantment, supportedEnchants);
                if (canonicalEnchantment == null)
                    result.Errors.Add($"「{zhName}」不支持「{item.Enchantment.Trim()}」附魔");
            }

            // 槽位越界与重叠校验
            int slot = item.Slot;
            if (slot < 0 || slot + width > 10)
            {
                result.Errors.Add($"「{zhName}」超出了 10 个格子");
            }
            else
            {
                bool overlapped = false;
                for (int k = 0; k < width; k++)
                {
                    int s = slot + k;
                    if (occupiedBy[s] != null)
                    {
                        if (!overlapped)
                        {
                            result.Errors.Add($"「{zhName}」和「{occupiedBy[s]}」重叠了");
                            overlapped = true;
                        }
                    }
                    else
                    {
                        occupiedBy[s] = zhName;
                    }
                    unlockedMask |= 1 << s;
                }
            }

            // 警告：跨英雄专属卡 & 未勾选 Mod 卡
            AppendCrossHeroWarningIfNeeded(tpl, zhName, normalizedHero, result.Warnings);
            AppendUnselectedModWarningIfNeeded(tpl, zhName, modHost, explicitAllowedModIds, result.Warnings);

            // 清洗任务与额外标签
            var cleanCompletedQuests = CleanStringList(item.CompletedQuests);
            var cleanQuestProgress = CleanFiniteDoubleDict(item.QuestProgress, zhName, "任务进度", result.Errors);
            var cleanExtraTags = CleanStringList(item.ExtraTags);

            // 频率类属性剥除/钳回 + 数量类 editedAttributes 幂等折算
            var (outPermAttrs, outPermAdd, outPermMult) = SanitizeAndFoldAttributes(
                tpl: tpl,
                zhName: zhName,
                coreTier: TierHelper.ParseTier(canonicalTier),
                enchantment: canonicalEnchantment,
                completedQuests: cleanCompletedQuests,
                questProgress: cleanQuestProgress,
                inputPermAttrs: item.PermanentAttributes,
                inputPermAdd: item.PermanentAdd,
                inputPermMult: item.PermanentMult,
                inputEditedAttrs: item.EditedAttributes,
                errors: result.Errors,
                warnings: result.Warnings);

            normalizedItems.Add(new PvpGhostPool.GhostItem
            {
                Slot = Math.Clamp(slot, 0, 9),
                Width = width,
                TemplateId = tpl.Id,
                InternalName = tpl.InternalName ?? "",
                ZhName = zhName,
                Tier = canonicalTier,
                Enchantment = canonicalEnchantment,
                PermanentAttributes = outPermAttrs,
                PermanentAdd = outPermAdd.Count > 0 ? outPermAdd : null,
                PermanentMult = outPermMult.Count > 0 ? outPermMult : null,
                CompletedQuests = cleanCompletedQuests.Count > 0 ? cleanCompletedQuests : null,
                QuestProgress = cleanQuestProgress.Count > 0 ? cleanQuestProgress : null,
                ExtraTags = cleanExtraTags.Count > 0 ? cleanExtraTags : null,
            });
        }

        if (totalItemWidth > 10)
            result.Errors.Add($"物品总占格（{totalItemWidth} 格）超过 10 格上限");

        // 5) 技能校验与数值折算
        var rawSkills = input.Skills ?? new List<PlannerSkillInput>();
        if (rawSkills.Count > MaxSkills)
            result.Errors.Add($"技能最多 {MaxSkills} 个");

        var normalizedSkills = new List<PvpGhostPool.GhostSkill>(Math.Min(rawSkills.Count, MaxSkills));
        for (int i = 0; i < rawSkills.Count && i < MaxSkills; i++)
        {
            var sk = rawSkills[i];
            if (sk == null)
            {
                result.Errors.Add($"第 {i + 1} 个技能数据为空");
                continue;
            }

            string tplId = sk.TemplateId?.Trim() ?? "";
            var tpl = ResolveTemplateOrReportError(
                tplId,
                expectedType: "Skill",
                slotLabel: $"第 {i + 1} 个技能",
                effectiveRepo,
                effectivePool,
                modHost,
                result.Errors);
            if (tpl == null)
                continue;

            string zhName = ResolveCardNameZh(tpl, effectiveRepo);
            var supportedTiers = GetSupportedTiers(tpl);
            string? canonicalTier = MatchSupportedValue(sk.Tier, supportedTiers);
            if (canonicalTier == null)
            {
                string tierLabels = string.Join("/", supportedTiers.Select(PlannerCatalogConstants.GetTierNameZh));
                result.Errors.Add($"技能「{zhName}」没有{PlannerCatalogConstants.GetTierNameZh(sk.Tier)}品阶（可选：{tierLabels}）");
                canonicalTier = supportedTiers[0];
            }

            AppendCrossHeroWarningIfNeeded(tpl, zhName, normalizedHero, result.Warnings);
            AppendUnselectedModWarningIfNeeded(tpl, zhName, modHost, explicitAllowedModIds, result.Warnings);

            var cleanExtraTags = CleanStringList(sk.ExtraTags);
            var (outPermAttrs, outPermAdd, outPermMult) = SanitizeAndFoldAttributes(
                tpl: tpl,
                zhName: zhName,
                coreTier: TierHelper.ParseTier(canonicalTier),
                enchantment: null,
                completedQuests: null,
                questProgress: null,
                inputPermAttrs: sk.PermanentAttributes,
                inputPermAdd: sk.PermanentAdd,
                inputPermMult: sk.PermanentMult,
                inputEditedAttrs: sk.EditedAttributes,
                errors: result.Errors,
                warnings: result.Warnings);

            normalizedSkills.Add(new PvpGhostPool.GhostSkill
            {
                TemplateId = tpl.Id,
                InternalName = tpl.InternalName ?? "",
                ZhName = zhName,
                Tier = canonicalTier,
                PermanentAttributes = outPermAttrs.Count > 0 ? outPermAttrs : null,
                PermanentAdd = outPermAdd.Count > 0 ? outPermAdd : null,
                PermanentMult = outPermMult.Count > 0 ? outPermMult : null,
                ExtraTags = cleanExtraTags.Count > 0 ? cleanExtraTags : null,
            });
        }

        // 6) 插座效果校验（克隆盘面原样保留合法项，过滤/钳制非数量篡改）
        var rawSockets = input.SocketEffects ?? new List<PlannerSocketEffectInput>();
        if (rawSockets.Count > MaxSocketEffects)
            result.Errors.Add($"插座效果最多 {MaxSocketEffects} 个");

        var normalizedSockets = new List<PvpGhostPool.GhostSocketEffect>(Math.Min(rawSockets.Count, MaxSocketEffects));
        for (int i = 0; i < rawSockets.Count && i < MaxSocketEffects; i++)
        {
            var se = rawSockets[i];
            if (se == null) continue;
            if (se.Slot < 0 || se.Slot > 9)
            {
                result.Errors.Add($"第 {i + 1} 个插座效果槽位（{se.Slot}）超出 0–9 范围");
                continue;
            }

            string tplId = se.TemplateId?.Trim() ?? "";
            var seTpl = !string.IsNullOrEmpty(tplId) ? effectiveRepo.GetCard(tplId) : null;
            if (seTpl == null || !effectivePool.IsAssetValid(seTpl.Id) || MonsterPool.IsBlockedDebugOrBlankArtSkill(seTpl))
            {
                result.Errors.Add($"第 {i + 1} 个插座效果模板无效：{tplId}");
                continue;
            }

            var supportedTiers = GetSupportedTiers(seTpl);
            string canonicalTier = MatchSupportedValue(se.Tier, supportedTiers) ?? supportedTiers[0];
            string? canonicalEnch = null;
            if (!string.IsNullOrWhiteSpace(se.Enchantment))
            {
                canonicalEnch = MatchSupportedValue(se.Enchantment, GetSupportedEnchantments(seTpl));
            }

            Dictionary<string, int>? cleanSeAttrs = null;
            if (se.PermanentAttributes is { Count: > 0 })
            {
                var seCard = new CardInstance(seTpl, TierHelper.ParseTier(canonicalTier), canonicalEnch, "val_se");
                string seZhName = ResolveCardNameZh(seTpl, effectiveRepo);
                cleanSeAttrs = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var (ak, av) in se.PermanentAttributes)
                {
                    if (string.IsNullOrWhiteSpace(ak)) continue;
                    string trimmedKey = ak.Trim();
                    if (DerivedGhostAttributeKeys.Contains(trimmedKey)) continue;

                    if (IsBlockedFrequencyAttribute(trimmedKey) || IsCooldownKey(trimmedKey)) // 插座效果的冷却保持原样
                    {
                        if (seCard.BaseAttributes.TryGetValue(trimmedKey, out double baseFreq) && double.IsFinite(baseFreq))
                        {
                            int baseInt = (int)Math.Round(baseFreq);
                            cleanSeAttrs[trimmedKey] = baseInt;
                            if (av != baseInt)
                                AddWarningOnce(result.Warnings, $"「{seZhName}」的频率/控制属性 {trimmedKey} 不可篡改，已恢复基础值");
                        }
                        else
                        {
                            AddWarningOnce(result.Warnings, $"「{seZhName}」的频率/控制属性 {trimmedKey} 不可篡改，已移除");
                        }
                        continue;
                    }

                    if (TryGetCanonicalQuantityKey(trimmedKey, out string canonKey))
                    {
                        cleanSeAttrs[canonKey] = Math.Clamp(av, 0, PlannerCatalogConstants.QuantityMax);
                    }
                    else if (TryResolveAllowedNonFrequencyKey(trimmedKey, out string canonNonFreq))
                    {
                        cleanSeAttrs[canonNonFreq] = Math.Clamp(av, -PlannerCatalogConstants.QuantityMax, PlannerCatalogConstants.QuantityMax);
                    }
                    else if (seCard.BaseAttributes.TryGetValue(trimmedKey, out double baseVal) && double.IsFinite(baseVal))
                    {
                        cleanSeAttrs[trimmedKey] = (int)Math.Round(baseVal);
                    }
                }
                if (cleanSeAttrs.Count == 0) cleanSeAttrs = null;
            }

            normalizedSockets.Add(new PvpGhostPool.GhostSocketEffect
            {
                Slot = se.Slot,
                TemplateId = seTpl.Id,
                Tier = canonicalTier,
                Enchantment = canonicalEnch,
                PermanentAttributes = cleanSeAttrs,
            });
        }

        if (normalizedItems.Count == 0 && normalizedSkills.Count == 0)
            result.Warnings.Add("这一天的对手什么都没带，开局时会是一个空手对手");

        string powerBucket = string.IsNullOrWhiteSpace(input.PowerBucket) ? "Normal" : input.PowerBucket.Trim();
        string source = string.IsNullOrWhiteSpace(input.Source) ? "custom" : input.Source.Trim();
        string poolPartition = string.IsNullOrWhiteSpace(input.PoolPartition)
            ? PvpGhostPool.StandardPartition
            : input.PoolPartition.Trim();

        result.Board = new PvpGhostPool.GhostBoard
        {
            BoardId = input.BoardId?.Trim() ?? "",
            Day = safeDay,
            SourceDay = input.SourceDay > 0 ? input.SourceDay : safeDay,
            TargetDay = input.TargetDay > 0 ? input.TargetDay : safeDay,
            Hero = normalizedHero,
            Level = level,
            HealthMax = healthMax,
            Prestige = prestige,
            Wins = wins,
            Losses = losses,
            UnlockedSlotsMask = unlockedMask & 1023,
            PowerBucket = powerBucket,
            EstimatedWinRate = estWinRate,
            Source = source,
            Persona = input.Persona?.Trim() ?? "",
            Archetype = input.Archetype?.Trim() ?? "",
            IsPlayerGhost = input.IsPlayerGhost,
            IsCustomRule = input.IsCustomRule,
            PoolPartition = poolPartition,
            DisplayName = normalizedDisplayName,
            Items = normalizedItems,
            Skills = normalizedSkills,
            SocketEffects = normalizedSockets,
        };

        return result;
    }

    // ── 3. 核心：频率类属性防篡改 + editedAttributes 幂等折算 ──────────────────

    private static (
        Dictionary<string, int> PermanentAttributes,
        Dictionary<string, double> PermanentAdd,
        Dictionary<string, double> PermanentMult) SanitizeAndFoldAttributes(
        CardTemplate tpl,
        string zhName,
        CardTier coreTier,
        string? enchantment,
        List<string>? completedQuests,
        Dictionary<string, double>? questProgress,
        Dictionary<string, int>? inputPermAttrs,
        Dictionary<string, double>? inputPermAdd,
        Dictionary<string, double>? inputPermMult,
        Dictionary<string, double>? inputEditedAttrs,
        List<string> errors,
        List<string> warnings)
    {
        // 1) 构建 CardInstance 并挂载任务状态（注意：是否先调 RebuildTierAndEnchantmentData 需按分支严格对齐 PvpGhostPool.ToPlayerState）
        var card = new CardInstance(tpl, coreTier, enchantment, "val");
        if (questProgress is { Count: > 0 })
        {
            foreach (var (qk, qv) in questProgress)
                card.QuestProgress[qk] = qv;
        }
        bool hasCompletedQuests = completedQuests is { Count: > 0 };
        if (hasCompletedQuests)
        {
            foreach (string qid in completedQuests!)
                card.CompletedQuests.Add(qid);
        }

        // 2) 过滤原有 PermanentAdd / PermanentMult：
        //    - 按显式频率/控制黑名单剥除篡改并追加去重中文 warnings；
        //    - 保留 7 项数量类及克隆卡的合法非频率成长（如 Lifesteal、SellPrice、JoyApplyAmount、Custom_* 等）。
        var cleanAdd = new Dictionary<string, double>(StringComparer.Ordinal);
        if (inputPermAdd != null)
        {
            foreach (var (rawKey, val) in inputPermAdd)
            {
                if (string.IsNullOrWhiteSpace(rawKey)) continue;
                string trimmedKey = rawKey.Trim();
                if (!double.IsFinite(val))
                {
                    errors.Add($"「{zhName}」的永久属性增量 {trimmedKey} 数值无效");
                    continue;
                }
                if (DerivedGhostAttributeKeys.Contains(trimmedKey)) continue;
                if (IsBlockedFrequencyAttribute(trimmedKey))
                {
                    if (Math.Abs(val) > 1e-6)
                        AddWarningOnce(warnings, $"「{zhName}」的频率/控制属性 {trimmedKey} 不可篡改，已移除");
                    continue;
                }
                if (!TryResolveAllowedNonFrequencyKey(trimmedKey, out string canonKey)) continue;
                if (IsCooldownKey(canonKey) && !HasNativeCooldown(card))
                {
                    if (Math.Abs(val) > 1e-6)
                        AddWarningOnce(warnings, $"「{zhName}」是被动物品，没有冷却，冷却改动已忽略");
                    continue;
                }
                if (!TryGetCanonicalQuantityKey(canonKey, out _) && Math.Abs(val) > MaxNonQuantityAbsValue)
                {
                    errors.Add($"「{zhName}」的永久属性增量 {canonKey} 超出允许范围");
                    continue;
                }
                if (Math.Abs(val) > 1e-6)
                    cleanAdd[canonKey] = val;
            }
        }

        var cleanMult = new Dictionary<string, double>(StringComparer.Ordinal);
        if (inputPermMult != null)
        {
            foreach (var (rawKey, val) in inputPermMult)
            {
                if (string.IsNullOrWhiteSpace(rawKey)) continue;
                string trimmedKey = rawKey.Trim();
                if (!double.IsFinite(val))
                {
                    errors.Add($"「{zhName}」的永久属性倍率 {trimmedKey} 数值无效");
                    continue;
                }
                if (DerivedGhostAttributeKeys.Contains(trimmedKey)) continue;
                if (IsBlockedFrequencyAttribute(trimmedKey))
                {
                    if (Math.Abs(val - 1.0) > 1e-6)
                        AddWarningOnce(warnings, $"「{zhName}」的频率/控制属性 {trimmedKey} 不可篡改，已移除");
                    continue;
                }
                if (!TryResolveAllowedNonFrequencyKey(trimmedKey, out string canonKey)) continue;
                if (IsCooldownKey(canonKey) && !HasNativeCooldown(card))
                {
                    if (Math.Abs(val - 1.0) > 1e-6)
                        AddWarningOnce(warnings, $"「{zhName}」是被动物品，没有冷却，冷却改动已忽略");
                    continue;
                }
                if (val < 0)
                {
                    errors.Add($"「{zhName}」的永久属性倍率 {canonKey} 不能为负数");
                    continue;
                }
                if (val > MaxPermanentMult)
                {
                    errors.Add($"「{zhName}」的永久属性倍率 {canonKey} 超出上限（0–{(int)MaxPermanentMult}）");
                    continue;
                }
                if (Math.Abs(val - 1.0) > 1e-6)
                    cleanMult[canonKey] = val;
            }
        }

        // 3) 还原到 CardInstance（严格对齐 PvpGhostPool.ToPlayerState L3126–3155 顺序）：
        //    - 有分层 PermanentAdd/Mult 时：先 RebuildTierAndEnchantmentData 再挂载分层；
        //    - 无分层而走平铺 PermanentAttributes 时：先基于任务前 BaseAttributes 算平铺 delta 再 RebuildTierAndEnchantmentData。
        bool hasLayeredPerm = cleanAdd.Count > 0 || cleanMult.Count > 0;
        if (hasLayeredPerm)
        {
            if (hasCompletedQuests)
                card.RebuildTierAndEnchantmentData();
            foreach (var (ak, av) in cleanAdd)
                card.PermanentAdd[ak] = av;
            foreach (var (ak, mv) in cleanMult)
                card.PermanentMult[ak] = mv;
        }
        else if (inputPermAttrs is { Count: > 0 })
        {
            foreach (var (ak, av) in inputPermAttrs)
            {
                if (string.IsNullOrWhiteSpace(ak)) continue;
                string trimmedKey = ak.Trim();
                if (DerivedGhostAttributeKeys.Contains(trimmedKey) || IsBlockedFrequencyAttribute(trimmedKey)) continue;
                if (!TryResolveAllowedNonFrequencyKey(trimmedKey, out string canonKey)) continue;
                if (IsCooldownKey(canonKey) && !HasNativeCooldown(card)) continue;
                double baseVal = card.BaseAttributes.GetValueOrDefault(canonKey, 0.0);
                double delta = av - baseVal;
                if (Math.Abs(delta) > 0.001)
                    card.PermanentAdd[canonKey] = delta;
            }
            if (hasCompletedQuests)
                card.RebuildTierAndEnchantmentData();
        }
        else if (hasCompletedQuests)
        {
            card.RebuildTierAndEnchantmentData();
        }

        // 4) 折算前端手改 editedAttributes → PermanentAdd（严格保持 7 项数量类白名单）
        bool appliedQuantityEdit = false;
        if (inputEditedAttrs is { Count: > 0 })
        {
            // 先检查是否存在有效数量类编辑；若存在且原卡走平铺回退分支，必须先提升其他属性差值至 cleanAdd，
            // 否则 ToPlayerState 切入 hasLayeredPerm 分支时会丢失其他属性的永久成长。
            foreach (var (rawKey, rawVal) in inputEditedAttrs)
            {
                if (string.IsNullOrWhiteSpace(rawKey)) continue;
                if (!TryGetCanonicalQuantityKey(rawKey, out string key))
                {
                    AddWarningOnce(warnings, $"「{zhName}」的 {rawKey.Trim()} 不能手改，已忽略");
                    continue;
                }
                if (IsCooldownKey(key) && !HasNativeCooldown(card))
                {
                    AddWarningOnce(warnings, $"「{zhName}」是被动物品，没有冷却，冷却改动已忽略");
                    continue;
                }
                if (!double.IsFinite(rawVal))
                {
                    errors.Add($"「{zhName}」的 {key} 数值无效");
                    continue;
                }

                if (!appliedQuantityEdit && !hasLayeredPerm && card.PermanentAdd.Count > 0)
                {
                    foreach (var (pk, pv) in card.PermanentAdd)
                    {
                        if (Math.Abs(pv) > 1e-6)
                            cleanAdd[pk] = pv;
                    }
                }
                appliedQuantityEdit = true;

                double target = Math.Clamp(Math.Round(rawVal), QuantityMinFor(card, key), PlannerCatalogConstants.QuantityMax);
                ApplyTargetQuantityValue(card, cleanAdd, cleanMult, key, target);
            }
        }

        // 5) 对所有数量类属性做越界安全钳制（0..QuantityMax），并将原始未钳制负值归零至 -baseVal
        bool layeredActive = hasLayeredPerm || appliedQuantityEdit;
        foreach (string key in PlannerCatalogConstants.QuantityAttributes)
        {
            bool exists = card.BaseAttributes.ContainsKey(key)
                       || card.PermanentAdd.ContainsKey(key)
                       || card.PermanentMult.ContainsKey(key);
            if (!exists) continue;

            double baseVal = card.BaseAttributes.GetValueOrDefault(key, 0.0);
            double pAdd = card.PermanentAdd.TryGetValue(key, out double pa) && double.IsFinite(pa) ? pa : 0.0;
            double pMult = card.PermanentMult.TryGetValue(key, out double pm) && double.IsFinite(pm) ? pm : 1.0;
            double rawVal = (baseVal + pAdd) * pMult;
            double cur = card.GetAttribute(key);
            double minVal = QuantityMinFor(card, key);

            if (!double.IsFinite(rawVal) || rawVal < minVal - 1e-6 || rawVal > PlannerCatalogConstants.QuantityMax ||
                !double.IsFinite(cur) || cur < 0.0 || cur > PlannerCatalogConstants.QuantityMax)
            {
                double clamped = double.IsFinite(rawVal)
                    ? Math.Clamp(Math.Round(rawVal), minVal, PlannerCatalogConstants.QuantityMax)
                    : Math.Clamp(Math.Round(baseVal), minVal, PlannerCatalogConstants.QuantityMax);
                if (!layeredActive && card.PermanentAdd.Count > 0)
                {
                    foreach (var (pk, pv) in card.PermanentAdd)
                    {
                        if (Math.Abs(pv) > 1e-6)
                            cleanAdd[pk] = pv;
                    }
                }
                layeredActive = true;
                ApplyTargetQuantityValue(card, cleanAdd, cleanMult, key, clamped);
            }
        }

        // 6) 重算输出 PermanentAttributes：
        //    - 7 项数量类：凡 BaseAttributes / PermanentAdd / PermanentMult 存在的键，写入最终值；
        //    - 合法非频率成长键：保留其真实计算终值（确保未编辑克隆卡的 Lifesteal/SellPrice 等不丢失）；
        //    - 频率/控制类：存在于 BaseAttributes 时钳回基础值（若被篡改则追加去重 warning），否则移除并追加去重 warning。
        var outPermAttrs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string key in PlannerCatalogConstants.QuantityAttributes)
        {
            bool exists = card.BaseAttributes.ContainsKey(key)
                       || card.PermanentAdd.ContainsKey(key)
                       || card.PermanentMult.ContainsKey(key);
            if (!exists) continue;

            // 冷却输出原始值（不扣附魔减免、不套战斗下限），再次提交时才不会重复扣减
            double finalVal = IsCooldownKey(key) ? RawLayeredValue(card, key) : card.GetAttribute(key);
            if (IsCooldownKey(key) && !(finalVal > 0)) continue; // 被动物品不输出冷却
            int rounded = (int)Math.Round(Math.Clamp(double.IsFinite(finalVal) ? finalVal : 0.0, 0.0, PlannerCatalogConstants.QuantityMax));
            outPermAttrs[key] = rounded;
        }

        var nonFreqGrowthKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string k in card.PermanentAdd.Keys)
        {
            if (!TryGetCanonicalQuantityKey(k, out _) && !IsBlockedFrequencyAttribute(k))
                nonFreqGrowthKeys.Add(k);
        }
        foreach (string k in card.PermanentMult.Keys)
        {
            if (!TryGetCanonicalQuantityKey(k, out _) && !IsBlockedFrequencyAttribute(k))
                nonFreqGrowthKeys.Add(k);
        }
        foreach (string k in nonFreqGrowthKeys)
        {
            double val = card.GetAttribute(k);
            int rounded = (int)Math.Round(Math.Clamp(double.IsFinite(val) ? val : 0.0, -PlannerCatalogConstants.QuantityMax, PlannerCatalogConstants.QuantityMax));
            outPermAttrs[k] = rounded;
        }

        if (inputPermAttrs is { Count: > 0 })
        {
            foreach (var (rawKey, rawVal) in inputPermAttrs)
            {
                if (string.IsNullOrWhiteSpace(rawKey)) continue;
                string trimmedKey = rawKey.Trim();
                if (DerivedGhostAttributeKeys.Contains(trimmedKey)) continue;
                if (TryGetCanonicalQuantityKey(trimmedKey, out _)) continue;
                if (outPermAttrs.ContainsKey(trimmedKey)) continue;

                if (IsBlockedFrequencyAttribute(trimmedKey))
                {
                    if (card.BaseAttributes.TryGetValue(trimmedKey, out double baseFreq) && double.IsFinite(baseFreq))
                    {
                        int baseInt = (int)Math.Round(baseFreq);
                        outPermAttrs[trimmedKey] = baseInt;
                        if (rawVal != baseInt)
                            AddWarningOnce(warnings, $"「{zhName}」的频率/控制属性 {trimmedKey} 不可篡改，已恢复基础值");
                    }
                    else if (string.Equals(trimmedKey, "Ammo", StringComparison.OrdinalIgnoreCase) &&
                             card.BaseAttributes.TryGetValue("AmmoMax", out double baseAmmoMax) &&
                             double.IsFinite(baseAmmoMax) &&
                             rawVal == (int)Math.Round(baseAmmoMax))
                    {
                        // 真实录制镜像会附带当前 Ammo == 基础 AmmoMax，属于派生冗余字段，静默剥除不误报
                    }
                    else
                    {
                        AddWarningOnce(warnings, $"「{zhName}」的频率/控制属性 {trimmedKey} 不可篡改，已移除");
                    }
                    continue;
                }

                if (card.BaseAttributes.TryGetValue(trimmedKey, out double baseVal) && double.IsFinite(baseVal))
                    outPermAttrs[trimmedKey] = (int)Math.Round(baseVal);
            }
        }

        return (outPermAttrs, cleanAdd, cleanMult);
    }

    private static void ApplyTargetQuantityValue(
        CardInstance card,
        Dictionary<string, double> cleanAdd,
        Dictionary<string, double> cleanMult,
        string key,
        double target)
    {
        double baseVal = card.BaseAttributes.GetValueOrDefault(key, 0.0);
        double mult = card.PermanentMult.TryGetValue(key, out double m) && double.IsFinite(m) ? m : 1.0;
        double nextAdd;
        if (mult > 1e-6)
        {
            // 直接基于 baseVal 反解 nextAdd = target / mult - baseVal，彻底避免负 PermanentAdd 经 GetAttribute 钳底为 0 引发的偏差
            nextAdd = target / mult - baseVal;
            if (!double.IsFinite(nextAdd))
            {
                card.PermanentMult.Remove(key);
                cleanMult.Remove(key);
                nextAdd = target - baseVal;
            }
        }
        else
        {
            card.PermanentMult.Remove(key);
            cleanMult.Remove(key);
            nextAdd = target - baseVal;
        }

        if (Math.Abs(nextAdd) < 1e-6)
        {
            card.PermanentAdd.Remove(key);
            cleanAdd.Remove(key);
        }
        else
        {
            card.PermanentAdd[key] = nextAdd;
            cleanAdd[key] = nextAdd;
        }
    }

    // ── 4. 模板解析、警告与容错反序列化辅助 ────────────────────────────────────

    private static CardTemplate? ResolveTemplateOrReportError(
        string tplId,
        string expectedType,
        string slotLabel,
        GameDataRepository effectiveRepo,
        CardPool effectivePool,
        ServerModHost? modHost,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(tplId))
        {
            errors.Add($"{slotLabel}缺少模板 ID");
            return null;
        }

        var tpl = effectiveRepo.GetCard(tplId);
        if (tpl == null)
        {
            // 若该卡存在于服务端其他未选中的 Mod 中，给出明确提示
            var globalTpl = modHost?.ActiveRepository.GetCard(tplId);
            if (globalTpl != null && globalTpl.IsCustomModCard && !string.IsNullOrWhiteSpace(globalTpl.CustomModOrigin))
            {
                string gName = ResolveCardNameZh(globalTpl, modHost?.ActiveRepository);
                errors.Add($"「{gName}」来自当前环境未启用的 Mod「{globalTpl.CustomModOrigin}」");
            }
            else
            {
                errors.Add($"{slotLabel}不在当前卡库中（{tplId}）");
            }
            return null;
        }

        if (!string.Equals(tpl.Type, expectedType, StringComparison.OrdinalIgnoreCase))
        {
            string zhName = ResolveCardNameZh(tpl, effectiveRepo);
            errors.Add($"「{zhName}」不是合法的{(expectedType == "Item" ? "物品" : "技能")}卡");
            return null;
        }

        if (!effectivePool.IsAssetValid(tpl.Id) || MonsterPool.IsBlockedDebugOrBlankArtSkill(tpl))
        {
            string zhName = ResolveCardNameZh(tpl, effectiveRepo);
            errors.Add($"「{zhName}」资源不可用或为调试/禁用卡");
            return null;
        }

        return tpl;
    }

    private static void AppendCrossHeroWarningIfNeeded(
        CardTemplate tpl,
        string zhName,
        string normalizedHero,
        List<string> warnings)
    {
        if (tpl.Heroes is not { Count: > 0 }) return;
        foreach (string h in tpl.Heroes)
        {
            if (string.Equals(h, "Common", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h, normalizedHero, StringComparison.OrdinalIgnoreCase))
                return;
        }

        var heroZhList = tpl.Heroes
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Select(PlannerCatalogConstants.GetHeroNameZh)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (heroZhList.Count > 0)
        {
            warnings.Add($"「{zhName}」是{string.Join("/", heroZhList)}的专属卡，放在{PlannerCatalogConstants.GetHeroNameZh(normalizedHero)}身上照样能用");
        }
    }

    private static void AppendUnselectedModWarningIfNeeded(
        CardTemplate tpl,
        string zhName,
        ServerModHost? modHost,
        IReadOnlySet<string>? explicitAllowedModIds,
        List<string> warnings)
    {
        if (!tpl.IsCustomModCard || string.IsNullOrWhiteSpace(tpl.CustomModOrigin)) return;
        if (explicitAllowedModIds == null) return;
        if (explicitAllowedModIds.Contains(tpl.CustomModOrigin)) return;

        string modName = tpl.CustomModOrigin;
        if (modHost != null)
        {
            var pkg = modHost.CommunityStore?.ResolvePackage(tpl.CustomModOrigin)
                ?? modHost.FindDiscoveredPackage(tpl.CustomModOrigin)
                ?? modHost.Registry.LoadedPackages.FirstOrDefault(
                    p => p?.Manifest != null &&
                         string.Equals(p.Manifest.Id, tpl.CustomModOrigin, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pkg?.Manifest?.Name))
                modName = pkg!.Manifest.Name.Trim();
        }
        AddWarningOnce(warnings, $"「{zhName}」来自 Mod「{modName}」，编排环境里没开这个 Mod");
    }

    private static string? NormalizeTierName(string? rawTier)
    {
        if (string.IsNullOrWhiteSpace(rawTier)) return null;
        string trimmed = rawTier.Trim();
        foreach (string tier in PlannerCatalogConstants.TierOrder)
        {
            if (string.Equals(tier, trimmed, StringComparison.OrdinalIgnoreCase))
                return tier;
        }
        return null;
    }

    private static string? MatchSupportedValue(string? raw, IReadOnlyList<string> supported)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim();
        for (int i = 0; i < supported.Count; i++)
        {
            if (string.Equals(supported[i], trimmed, StringComparison.OrdinalIgnoreCase))
                return supported[i];
        }
        return null;
    }

    private static List<string> CleanStringList(List<string>? source)
    {
        var list = new List<string>();
        if (source == null || source.Count == 0) return list;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in source)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string trimmed = raw.Trim();
            if (seen.Add(trimmed))
                list.Add(trimmed);
        }
        return list;
    }

    private static Dictionary<string, double> CleanFiniteDoubleDict(
        Dictionary<string, double>? source,
        string zhName,
        string labelZh,
        List<string> errors)
    {
        var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (source == null || source.Count == 0) return dict;
        foreach (var (k, v) in source)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            if (!double.IsFinite(v))
            {
                errors.Add($"「{zhName}」的{labelZh} {k} 数值无效");
                continue;
            }
            dict[k.Trim()] = v;
        }
        return dict;
    }

    private static PlannerBoardInput ToBoardInput(PvpGhostPool.GhostBoard board)
    {
        var input = new PlannerBoardInput
        {
            BoardId = board.BoardId,
            Day = board.Day,
            SourceDay = board.SourceDay,
            TargetDay = board.TargetDay,
            Hero = board.Hero,
            Level = board.Level,
            HealthMax = board.HealthMax,
            Prestige = board.Prestige,
            Wins = board.Wins,
            Losses = board.Losses,
            UnlockedSlotsMask = board.UnlockedSlotsMask,
            PowerBucket = board.PowerBucket,
            EstimatedWinRate = board.EstimatedWinRate,
            Source = board.Source,
            Persona = board.Persona,
            Archetype = board.Archetype,
            IsPlayerGhost = board.IsPlayerGhost,
            IsCustomRule = board.IsCustomRule,
            PoolPartition = board.PoolPartition,
            DisplayName = board.DisplayName,
            Items = new List<PlannerItemInput>(),
            Skills = new List<PlannerSkillInput>(),
            SocketEffects = new List<PlannerSocketEffectInput>(),
        };

        if (board.Items != null)
        {
            foreach (var it in board.Items)
            {
                if (it == null) continue;
                input.Items.Add(new PlannerItemInput
                {
                    Slot = it.Slot,
                    Width = it.Width,
                    TemplateId = it.TemplateId,
                    InternalName = it.InternalName,
                    ZhName = it.ZhName,
                    Tier = it.Tier,
                    Enchantment = it.Enchantment,
                    PermanentAttributes = it.PermanentAttributes != null ? new Dictionary<string, int>(it.PermanentAttributes, StringComparer.OrdinalIgnoreCase) : null,
                    PermanentAdd = it.PermanentAdd != null ? new Dictionary<string, double>(it.PermanentAdd, StringComparer.OrdinalIgnoreCase) : null,
                    PermanentMult = it.PermanentMult != null ? new Dictionary<string, double>(it.PermanentMult, StringComparer.OrdinalIgnoreCase) : null,
                    CompletedQuests = it.CompletedQuests != null ? new List<string>(it.CompletedQuests) : null,
                    QuestProgress = it.QuestProgress != null ? new Dictionary<string, double>(it.QuestProgress, StringComparer.OrdinalIgnoreCase) : null,
                    ExtraTags = it.ExtraTags != null ? new List<string>(it.ExtraTags) : null,
                });
            }
        }

        if (board.Skills != null)
        {
            foreach (var sk in board.Skills)
            {
                if (sk == null) continue;
                input.Skills.Add(new PlannerSkillInput
                {
                    TemplateId = sk.TemplateId,
                    InternalName = sk.InternalName,
                    ZhName = sk.ZhName,
                    Tier = sk.Tier,
                    PermanentAttributes = sk.PermanentAttributes != null ? new Dictionary<string, int>(sk.PermanentAttributes, StringComparer.OrdinalIgnoreCase) : null,
                    PermanentAdd = sk.PermanentAdd != null ? new Dictionary<string, double>(sk.PermanentAdd, StringComparer.OrdinalIgnoreCase) : null,
                    PermanentMult = sk.PermanentMult != null ? new Dictionary<string, double>(sk.PermanentMult, StringComparer.OrdinalIgnoreCase) : null,
                    ExtraTags = sk.ExtraTags != null ? new List<string>(sk.ExtraTags) : null,
                });
            }
        }

        if (board.SocketEffects != null)
        {
            foreach (var se in board.SocketEffects)
            {
                if (se == null) continue;
                input.SocketEffects.Add(new PlannerSocketEffectInput
                {
                    Slot = se.Slot,
                    TemplateId = se.TemplateId,
                    Tier = se.Tier,
                    Enchantment = se.Enchantment,
                    PermanentAttributes = se.PermanentAttributes != null ? new Dictionary<string, int>(se.PermanentAttributes, StringComparer.OrdinalIgnoreCase) : null,
                });
            }
        }

        return input;
    }

    // ── 5. 容错 JsonElement 解析（防非整数/非有限数/缺失字段导致异常）───────────

    private static PlannerBoardInput? ParseBoardFromJson(JsonElement root, int defaultDay, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("盘面数据必须是 JSON 对象");
            return null;
        }

        var input = new PlannerBoardInput
        {
            BoardId = ReadOptionalString(root, "boardId"),
            Day = ReadIntField(root, "day", defaultDay, "天数", errors),
            SourceDay = ReadIntField(root, "sourceDay", 0, "原始天数", errors),
            TargetDay = ReadIntField(root, "targetDay", 0, "目标天数", errors),
            Hero = ReadOptionalString(root, "hero"),
            Level = ReadIntField(root, "level", 1, "等级", errors),
            HealthMax = ReadIntField(root, "healthMax", GameDefaults.StartingHealth, "最大生命", errors),
            Prestige = ReadIntField(root, "prestige", GameDefaults.StartingPrestige, "声望", errors),
            Wins = ReadIntField(root, "wins", 0, "胜场", errors),
            Losses = ReadIntField(root, "losses", 0, "败场", errors),
            UnlockedSlotsMask = ReadIntField(root, "unlockedSlotsMask", 0, "解锁格子掩码", errors),
            PowerBucket = ReadOptionalString(root, "powerBucket"),
            EstimatedWinRate = ReadDoubleField(root, "estimatedWinRate", 0.5, "胜率估计", errors),
            Source = ReadOptionalString(root, "source"),
            Persona = ReadOptionalString(root, "persona"),
            Archetype = ReadOptionalString(root, "archetype"),
            IsPlayerGhost = ReadBoolField(root, "isPlayerGhost", false),
            IsCustomRule = ReadBoolField(root, "isCustomRule", false),
            PoolPartition = ReadOptionalString(root, "poolPartition"),
            DisplayName = ReadOptionalString(root, "displayName"),
            Items = new List<PlannerItemInput>(),
            Skills = new List<PlannerSkillInput>(),
            SocketEffects = new List<PlannerSocketEffectInput>(),
        };

        if (TryGetPropertyCI(root, "items", out var itemsEl) && itemsEl.ValueKind != JsonValueKind.Null)
        {
            if (itemsEl.ValueKind != JsonValueKind.Array)
            {
                errors.Add("物品列表格式无效");
            }
            else
            {
                int idx = 0;
                foreach (var el in itemsEl.EnumerateArray())
                {
                    idx++;
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"第 {idx} 件物品数据格式无效");
                        continue;
                    }
                    input.Items.Add(new PlannerItemInput
                    {
                        Slot = ReadIntField(el, "slot", 0, $"第 {idx} 件物品槽位", errors),
                        Width = ReadIntField(el, "width", 1, $"第 {idx} 件物品宽度", errors),
                        TemplateId = ReadOptionalString(el, "templateId") ?? "",
                        InternalName = ReadOptionalString(el, "internalName") ?? "",
                        ZhName = ReadOptionalString(el, "zhName") ?? "",
                        Tier = ReadOptionalString(el, "tier") ?? "Bronze",
                        Enchantment = ReadOptionalString(el, "enchantment"),
                        PermanentAttributes = ReadIntDict(el, "permanentAttributes", $"第 {idx} 件物品属性", errors),
                        PermanentAdd = ReadDoubleDict(el, "permanentAdd", $"第 {idx} 件物品属性增量", errors),
                        PermanentMult = ReadDoubleDict(el, "permanentMult", $"第 {idx} 件物品属性倍率", errors),
                        CompletedQuests = ReadStringList(el, "completedQuests"),
                        QuestProgress = ReadDoubleDict(el, "questProgress", $"第 {idx} 件物品任务进度", errors),
                        ExtraTags = ReadStringList(el, "extraTags"),
                        EditedAttributes = ReadDoubleDict(el, "editedAttributes", $"第 {idx} 件物品手改属性", errors),
                    });
                }
            }
        }

        if (TryGetPropertyCI(root, "skills", out var skillsEl) && skillsEl.ValueKind != JsonValueKind.Null)
        {
            if (skillsEl.ValueKind != JsonValueKind.Array)
            {
                errors.Add("技能列表格式无效");
            }
            else
            {
                int idx = 0;
                foreach (var el in skillsEl.EnumerateArray())
                {
                    idx++;
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"第 {idx} 个技能数据格式无效");
                        continue;
                    }
                    input.Skills.Add(new PlannerSkillInput
                    {
                        TemplateId = ReadOptionalString(el, "templateId") ?? "",
                        InternalName = ReadOptionalString(el, "internalName") ?? "",
                        ZhName = ReadOptionalString(el, "zhName") ?? "",
                        Tier = ReadOptionalString(el, "tier") ?? "Bronze",
                        PermanentAttributes = ReadIntDict(el, "permanentAttributes", $"第 {idx} 个技能属性", errors),
                        PermanentAdd = ReadDoubleDict(el, "permanentAdd", $"第 {idx} 个技能属性增量", errors),
                        PermanentMult = ReadDoubleDict(el, "permanentMult", $"第 {idx} 个技能属性倍率", errors),
                        ExtraTags = ReadStringList(el, "extraTags"),
                        EditedAttributes = ReadDoubleDict(el, "editedAttributes", $"第 {idx} 个技能手改属性", errors),
                    });
                }
            }
        }

        if (TryGetPropertyCI(root, "socketEffects", out var socketsEl) && socketsEl.ValueKind != JsonValueKind.Null)
        {
            if (socketsEl.ValueKind != JsonValueKind.Array)
            {
                errors.Add("插座效果列表格式无效");
            }
            else
            {
                int idx = 0;
                foreach (var el in socketsEl.EnumerateArray())
                {
                    idx++;
                    if (el.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"第 {idx} 个插座效果格式无效");
                        continue;
                    }
                    input.SocketEffects.Add(new PlannerSocketEffectInput
                    {
                        Slot = ReadIntField(el, "slot", 0, $"第 {idx} 个插座效果槽位", errors),
                        TemplateId = ReadOptionalString(el, "templateId") ?? "",
                        Tier = ReadOptionalString(el, "tier") ?? "Bronze",
                        Enchantment = ReadOptionalString(el, "enchantment"),
                        PermanentAttributes = ReadIntDict(el, "permanentAttributes", $"第 {idx} 个插座效果属性", errors),
                    });
                }
            }
        }

        return input;
    }

    private static bool TryGetPropertyCI(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? ReadOptionalString(JsonElement obj, string name)
    {
        if (!TryGetPropertyCI(obj, name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static bool ReadBoolField(JsonElement obj, string name, bool fallback)
    {
        if (!TryGetPropertyCI(obj, name, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static int ReadIntField(JsonElement obj, string name, int fallback, string labelZh, List<string> errors)
    {
        if (!TryGetPropertyCI(obj, name, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Null) return fallback;
        if (TryReadFiniteDouble(el, out double d))
        {
            double rounded = Math.Round(d);
            if (rounded >= int.MinValue && rounded <= int.MaxValue)
                return (int)rounded;
        }
        errors.Add($"{labelZh}数值无效");
        return fallback;
    }

    private static double ReadDoubleField(JsonElement obj, string name, double fallback, string labelZh, List<string> errors)
    {
        if (!TryGetPropertyCI(obj, name, out var el) || el.ValueKind == JsonValueKind.Null) return fallback;
        if (TryReadFiniteDouble(el, out double d)) return d;
        errors.Add($"{labelZh}数值无效");
        return fallback;
    }

    private static bool TryReadFiniteDouble(JsonElement el, out double value)
    {
        value = 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double num) && double.IsFinite(num))
        {
            value = num;
            return true;
        }
        if (el.ValueKind == JsonValueKind.String &&
            double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
            double.IsFinite(parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }

    private static Dictionary<string, int>? ReadIntDict(JsonElement obj, string name, string labelZh, List<string> errors)
    {
        if (!TryGetPropertyCI(obj, name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{labelZh}格式无效");
            return null;
        }
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null) continue;
            if (TryReadFiniteDouble(prop.Value, out double d) && d >= int.MinValue && d <= int.MaxValue)
            {
                dict[prop.Name] = (int)Math.Round(d);
            }
            else
            {
                errors.Add($"{labelZh} {prop.Name} 数值无效");
            }
        }
        return dict;
    }

    private static Dictionary<string, double>? ReadDoubleDict(JsonElement obj, string name, string labelZh, List<string> errors)
    {
        if (!TryGetPropertyCI(obj, name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{labelZh}格式无效");
            return null;
        }
        var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Null) continue;
            if (TryReadFiniteDouble(prop.Value, out double d))
            {
                dict[prop.Name] = d;
            }
            else
            {
                errors.Add($"{labelZh} {prop.Name} 数值无效");
            }
        }
        return dict;
    }

    private static List<string>? ReadStringList(JsonElement obj, string name)
    {
        if (!TryGetPropertyCI(obj, name, out var el) || el.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
        }
        return list;
    }
}
