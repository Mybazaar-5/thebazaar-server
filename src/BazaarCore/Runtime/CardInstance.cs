using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;

namespace BazaarCore.Runtime;

/// <summary>
/// 运行时卡牌实例（物品 / 技能 / 效果卡），支持品质合并、四层 Duration 属性栈 + 光环层、13种附魔与Quest解锁。
/// </summary>
public sealed class CardInstance
{
    public string InstanceId { get; }
    public CardTemplate Template { get; }
    public CardTier Tier { get; private set; }
    public string? Enchantment { get; private set; }

    /// <summary>非战斗临时状态下的永久品质（用于区分战斗内临时升阶与永久升阶）。</summary>
    public CardTier PermanentTier { get; private set; }

    /// <summary>非战斗临时状态下的永久附魔（用于区分战斗内临时附魔与永久附魔）。</summary>
    public string? PermanentEnchantment { get; private set; }

    /// <summary>当日临时附魔（UntilEndOfDay，当日内进入战斗时保留，过天清理）。</summary>
    public string? DayEnchantment { get; private set; }

    /// <summary>本小时临时附魔（UntilEndOfHour，本小时内进入战斗时保留，过小时清理）。</summary>
    public string? HourEnchantment { get; private set; }

    /// <summary>若在战斗中触发永久变形，记录替换后模板 Id。</summary>
    public string? TransformReplacementTemplateId { get; set; }

    /// <summary>若为战斗中永久变形生成的新卡，记录被替换的原始卡牌 InstanceId。</summary>
    public string? TransformedFromInstanceId { get; set; }

    /// <summary>标记是否为战斗内临时生成/临时变形卡牌（战后不回写原玩家状态）。</summary>
    public bool IsCombatTemporary { get; set; }

    /// <summary>P0-06：变形前原卡销毁/禁用态快照，供 TimedTransformExpiry 到期还原时严守原始状态（防 TransformDestroyed 免费复活）。</summary>
    internal bool PreTransformWasDestroyed { get; set; }
    internal bool PreTransformWasDisabled { get; set; }

    /// <summary>由 PlayerState.CloneForCombat() 记录的同 InstanceId 源卡引用。</summary>
    public CardInstance? CombatSourceCard { get; internal set; }

    /// <summary>所在区域槽位起始下标（0..9）；未上阵则为 -1。</summary>
    public int StartSlot { get; set; } = -1;
    /// <summary>最左槽位编号（SocketEffects/Item 与官方 LeftSocketId 口径对齐）。</summary>
    public int LeftSocketId => StartSlot;
    /// <summary>当前卡牌是否处于 Tiny 缩小状态（Tiny 属性 &gt; 0 或携带有效 Tiny 标签）。</summary>
    public bool IsTiny
        => GetRawScalar("Tiny") > 0 || HasExplicitTag("Tiny");
    /// <summary>
    /// 当前有效物理尺寸（受 Tiny 状态影响：Large → Medium，Medium → Small，Small → Small）。
    /// </summary>
    public CardSize EffectiveSize => !IsTiny
        ? Template.CardSize
        : Template.CardSize switch
        {
            CardSize.Large => CardSize.Medium,
            CardSize.Medium => CardSize.Small,
            _ => CardSize.Small
        };
    /// <summary>D1-P1-13 (#118-4)：战中容量溢出时的临时无重叠宽度上限（非空时钳制 SlotWidth 而不驱逐在场卡，脱战自动清空）。</summary>
    internal int? EffectiveCombatWidthCap { get; set; }
    /// <summary>未经战中溢出钳制的真实目标槽位宽度（Tiny 时 Large=2，Medium/Small=1）。</summary>
    public int BaseSlotWidth => IsTiny
        ? (Template.CardSize == CardSize.Large ? 2 : 1)
        : Template.CardSize.SlotCount();
    /// <summary>卡牌当前占据的棋盘物理槽位数（受 Tiny 与战中零重叠溢出保底钳制）。</summary>
    public int SlotWidth => EffectiveCombatWidthCap.HasValue
        ? Math.Clamp(EffectiveCombatWidthCap.Value, 1, BaseSlotWidth)
        : BaseSlotWidth;
    internal StandardPricesSettings? AttachedStandardPrices { get; set; }
    private static readonly StandardPricesSettings DefaultStandardPrices = new();

    /// <summary>基础属性（来自当前 Tier 合并 + 全局修饰型/无冲突附魔属性）。</summary>
    public Dictionary<string, double> BaseAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 附魔独立动作参数隔离域（P1-51）：当基础 Tiers 已含同名非零动作参数（*Amount/*Targets）且附魔自带专属 Abilities/Auras 时，
    /// 附魔同名动作参数存于此隔离域而不累加污染 BaseAttributes（消除 Icebreaker/Tantius Carrier/Magnifying Glass 等全部 15 张可达卡污染）。
    /// </summary>
    public Dictionary<string, double> EnchantmentBaseAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> EnchantmentAuraAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> EnchantmentAuraMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> EnchantmentAuraAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enchantmentAbilityIds = new(StringComparer.Ordinal);
    private readonly HashSet<AbilityDefinition> _enchantmentAbilities = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AuraDefinition> _enchantmentAuras = new(ReferenceEqualityComparer.Instance);

    private static readonly HashSet<string> EnchantmentActionParamAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DamageAmount", "ShieldApplyAmount", "BurnApplyAmount", "PoisonApplyAmount",
        "HealAmount", "RegenApplyAmount", "RageApplyAmount", "TempoApplyAmount", "JoyApplyAmount",
        "HasteAmount", "HasteTargets", "SlowAmount", "SlowTargets",
        "FreezeAmount", "FreezeTargets", "ChargeAmount", "ChargeTargets",
        "ReloadAmount", "ReloadTargets", "FlyingTargets", "ForceUseTargets",
        "UpgradeTargets", "DestroyTargets", "DisableTargets", "EnchantTargets", "TransformTargets"
    };

    /// <summary>局内永久属性修正（商店/事件/卖牌/永久成长）。</summary>
    public Dictionary<string, double> PermanentAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PermanentMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PermanentAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本小时临时属性修正（UntilEndOfHour）。</summary>
    public Dictionary<string, double> HourAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> HourMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> HourAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当日临时属性修正（UntilEndOfDay）。</summary>
    public Dictionary<string, double> DayAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> DayMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> DayAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>单场战斗临时属性修正（UntilEndOfCombat / TCombatDuration 聚合投影）。</summary>
    public Dictionary<string, double> CombatAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> CombatMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> CombatAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>单场战斗零乘数（Multiply 0.0）引用计数，避免覆写破坏 CombatMult 非零连乘值，到期减 1 无损恢复（修复 P0-08 Trail Markers）。</summary>
    public Dictionary<string, int> CombatZeroMultCount { get; } = new(StringComparer.OrdinalIgnoreCase);

    // D1-P1-07 (#126/#128 备忘)：非限时战中基线与限时 TCombatDuration 活性修饰列表严格分槽，单向重算聚合（0 逆除法，ResetForCombat 同步清空）
    internal readonly record struct TimedAttributeModifier(int TokenId, string Attr, string EffectiveOp, double Value);
    private readonly Dictionary<string, double> _combatBaseAdd = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _combatBaseMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _combatBaseAdditiveMult = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _combatBaseZeroMultCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TimedAttributeModifier> _activeTimedCombatModifiers = new();
    private int _nextTimedModifierToken = 1;

    /// <summary>光环动态加成（由 AuraEngine 刷新重写）。</summary>
    public Dictionary<string, double> AuraAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> AuraMult { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> AuraAdditiveMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>D1-P1-02：五层对称正/负标签集（按 Base -> Permanent -> Day -> Hour -> Combat -> Aura 逐层叠算）。</summary>
    public HashSet<string> ExtraTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DayTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HourTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CombatTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AuraTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PermanentRemovedTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DayRemovedTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> HourRemovedTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CombatRemovedTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AuraRemovedTags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quest 进度（Quest_1..Quest_7）与已解锁能力集合。</summary>
    public Dictionary<string, double> QuestProgress { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CompletedQuests { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>D1-P2-01：战中限时（UntilEndOfCombat / TCombatDuration）任务进度与完成分槽，战后由 ResetForCombat 清零且不污染永久状态。</summary>
    public Dictionary<string, double> CombatQuestProgress { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CombatCompletedQuests { get; } = new(StringComparer.OrdinalIgnoreCase);

    public double GetQuestProgress(string questAttr)
        => QuestProgress.GetValueOrDefault(questAttr, 0) + CombatQuestProgress.GetValueOrDefault(questAttr, 0);

    public bool IsQuestCompleted(string questAttr)
        => CompletedQuests.Contains(questAttr) || CombatCompletedQuests.Contains(questAttr);

    public bool HasQuestProgress(string questAttr)
        => QuestProgress.ContainsKey(questAttr) || CombatQuestProgress.ContainsKey(questAttr);

    /// <summary>当前生效的 Ability / Aura 集合（含基础品质、附魔与已解锁 Quest 奖励）。</summary>
    public List<AbilityDefinition> ActiveAbilities { get; } = new();
    public List<AuraDefinition> ActiveAuras { get; } = new();
    private readonly HashSet<string> _questRewardTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _questRewardHiddenTags = new(StringComparer.OrdinalIgnoreCase);

    // 战斗实时状态
    public int CooldownProgressMs { get; set; }
    public int HasteMs { get; set; }
    public int SlowMs { get; set; }
    public int FreezeMs { get; set; }
    public int CurrentAmmo { get; set; }
    public bool IsFlying { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsDestroyed { get; set; }
    public bool Heated { get; set; }
    public bool Chilled { get; set; }
    /// <summary>D1-P2-01：卡牌是否正在执行施放链路（供 TCardConditionalCardState "Casting" 判定）。</summary>
    public bool IsCasting { get; set; }
    /// <summary>D1-P2-01：卡牌是否由变形产生（供 TCardConditionalCardState "Transformed" 判定）。</summary>
    public bool WasTransformed { get; set; }
    public int TimesFiredThisFight { get; set; }
    public bool HasFiredOnce => TimesFiredThisFight > 0;

    /// <summary>V14 开放沙盒机制单场战斗隔离黑板（战后自动清空，零原版副作用）。</summary>
    public HashSet<string> CombatCustomFlags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> CombatCustomCounters { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly CardTier[] AllCardTiers = Enum.GetValues<CardTier>();

    /// <summary>单调递增的属性与光环变更版本号，供战斗快照复用无变动属性以消除热路径重复查表。</summary>
    internal int AttributeVersion { get; private set; }

    public void MarkAttributeDirty() => AttributeVersion++;

    public CardInstance(CardTemplate template, CardTier? tierOverride = null, string? enchantment = null, string? instanceId = null)
        : this(template, tierOverride, enchantment, instanceId, skipInitialRebuild: false)
    {
    }

    private CardInstance(CardTemplate template, CardTier? tierOverride, string? enchantment, string? instanceId, bool skipInitialRebuild)
    {
        Template = template;
        Tier = SpawnQueryEngine.ClampToSupportedTier(template, tierOverride ?? template.DefaultTier);
        Enchantment = enchantment;
        PermanentTier = Tier;
        PermanentEnchantment = Enchantment;
        InstanceId = instanceId ?? Guid.NewGuid().ToString("N");
        if (!skipInitialRebuild)
        {
            RebuildTierAndEnchantmentData();
            ResetForCombat(null);
        }
    }

    private static readonly string[] TrackedDynamicEffectTags =
        ["Burn", "Poison", "Shield", "Heal", "Regen", "Freeze", "Slow", "Haste"];

    private int _cachedDynamicEffectMask = -1;
    private string? _cachedDynamicEnch;
    private int _cachedDynamicAbCount = -1;
    private AbilityDefinition? _cachedDynamicLastAb;

    private static int GetDynamicEffectTagBit(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return 0;
        if (tag.Equals("Burn", StringComparison.OrdinalIgnoreCase)) return 1 << 0;
        if (tag.Equals("Poison", StringComparison.OrdinalIgnoreCase)) return 1 << 1;
        if (tag.Equals("Shield", StringComparison.OrdinalIgnoreCase)) return 1 << 2;
        if (tag.Equals("Heal", StringComparison.OrdinalIgnoreCase)) return 1 << 3;
        if (tag.Equals("Regen", StringComparison.OrdinalIgnoreCase)) return 1 << 4;
        if (tag.Equals("Freeze", StringComparison.OrdinalIgnoreCase)) return 1 << 5;
        if (tag.Equals("Slow", StringComparison.OrdinalIgnoreCase)) return 1 << 6;
        if (tag.Equals("Haste", StringComparison.OrdinalIgnoreCase)) return 1 << 7;
        return 0;
    }

    private static int MapActionToEffectBit(string? actionType) => actionType switch
    {
        "TActionPlayerBurnApply" => 1 << 0,
        "TActionPlayerPoisonApply" => 1 << 1,
        "TActionPlayerShieldApply" => 1 << 2,
        "TActionPlayerHeal" or "TActionPlayerReviveHeal" => 1 << 3,
        "TActionPlayerRegenApply" => 1 << 4,
        "TActionCardFreeze" => 1 << 5,
        "TActionCardSlow" => 1 << 6,
        "TActionCardHaste" => 1 << 7,
        _ => 0
    };

    private static int MapEnchantmentToEffectBit(string? enchantment) => enchantment switch
    {
        "Fiery" => 1 << 0,
        "Toxic" => 1 << 1,
        "Shielded" => 1 << 2,
        "Restorative" => 1 << 3,
        "Mossy" => 1 << 4,
        "Icy" => 1 << 5,
        "Heavy" => 1 << 6,
        "Turbo" => 1 << 7,
        _ => 0
    };

    private static int CollectEffectMaskFromNode(RuleNode? node, int depth = 0)
    {
        if (node == null || depth > 16) return 0;
        int mask = MapActionToEffectBit(node.Type);
        if (node.Actions != null)
        {
            for (int i = 0; i < node.Actions.Count; i++)
                mask |= CollectEffectMaskFromNode(node.Actions[i], depth + 1);
        }
        return mask;
    }

    private int GetDynamicEffectMask()
    {
        int abCount = ActiveAbilities.Count;
        var lastAb = abCount > 0 ? ActiveAbilities[abCount - 1] : null;
        if (_cachedDynamicEffectMask >= 0
            && _cachedDynamicAbCount == abCount
            && ReferenceEquals(_cachedDynamicLastAb, lastAb)
            && string.Equals(_cachedDynamicEnch, Enchantment, StringComparison.Ordinal))
        {
            return _cachedDynamicEffectMask;
        }

        int mask = 0;
        for (int i = 0; i < abCount; i++)
            mask |= CollectEffectMaskFromNode(ActiveAbilities[i].Action);

        if (!string.IsNullOrEmpty(Enchantment))
        {
            if (Template.Enchantments != null && Template.Enchantments.TryGetValue(Enchantment, out var ench))
            {
                for (int i = 0; i < ench.HiddenTags.Count; i++)
                    mask |= GetDynamicEffectTagBit(ench.HiddenTags[i]);
                foreach (var ab in ench.Abilities.Values)
                    mask |= CollectEffectMaskFromNode(ab.Action);
                foreach (var au in ench.Auras.Values)
                    mask |= CollectEffectMaskFromNode(au.Action);
            }
            else
            {
                // 仅当模板未定义该附魔键时，回退采用附魔名称到效果位的默认映射
                mask |= MapEnchantmentToEffectBit(Enchantment);
            }
        }

        _cachedDynamicEffectMask = mask;
        _cachedDynamicAbCount = abCount;
        _cachedDynamicLastAb = lastAb;
        _cachedDynamicEnch = Enchantment;
        return mask;
    }

    private static bool ListContainsIgnoreCase(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>D1-P1-02：按生命周期层受控追加标签（同步清除同层负集并标脏）。</summary>
    public void AddTag(string tag, DurationTarget duration)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        string clean = tag.Trim();
        var (addSet, remSet) = duration switch
        {
            DurationTarget.Permanent => (ExtraTags, PermanentRemovedTags),
            DurationTarget.Day => (DayTags, DayRemovedTags),
            DurationTarget.Hour => (HourTags, HourRemovedTags),
            _ => (CombatTags, CombatRemovedTags)
        };
        remSet.Remove(clean);
        addSet.Add(clean);
        AttributeVersion++;
    }

    /// <summary>D1-P1-02：按生命周期层受控剥离标签（可剥离模板原生/附魔/低层标签，同步清除同层正集并标脏）。</summary>
    public void RemoveTag(string tag, DurationTarget duration)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        string clean = tag.Trim();
        var (addSet, remSet) = duration switch
        {
            DurationTarget.Permanent => (ExtraTags, PermanentRemovedTags),
            DurationTarget.Day => (DayTags, DayRemovedTags),
            DurationTarget.Hour => (HourTags, HourRemovedTags),
            _ => (CombatTags, CombatRemovedTags)
        };
        addSet.Remove(clean);
        remSet.Add(clean);
        AttributeVersion++;
    }

    /// <summary>D1-P1-02：光环层追加标签。</summary>
    public void ApplyAuraAddTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        string clean = tag.Trim();
        AuraRemovedTags.Remove(clean);
        AuraTags.Add(clean);
        AttributeVersion++;
    }

    /// <summary>D1-P1-02：光环层剥离标签。</summary>
    public void ApplyAuraRemoveTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        string clean = tag.Trim();
        AuraTags.Remove(clean);
        AuraRemovedTags.Add(clean);
        AttributeVersion++;
    }

    /// <summary>D1-P1-02 (#124-5)：按 Base -> Permanent -> Day -> Hour -> Combat -> Aura 逐层判定显式标签存在性（不含 Tiny 标量投影）。</summary>
    private bool HasExplicitTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;

        // Step 0: Base (Template ∪ Enchant ∪ QuestReward)
        bool present = ListContainsIgnoreCase(Template.Tags, tag)
            || (_questRewardTags.Count > 0 && _questRewardTags.Contains(tag))
            || (!string.IsNullOrEmpty(Enchantment)
                && Template.Enchantments != null
                && Template.Enchantments.TryGetValue(Enchantment, out var ench)
                && ListContainsIgnoreCase(ench.Tags, tag));

        // Step 1: Permanent
        if (ExtraTags.Count > 0 && ExtraTags.Contains(tag)) present = true;
        if (PermanentRemovedTags.Count > 0 && PermanentRemovedTags.Contains(tag)) present = false;

        // Step 2: Day
        if (DayTags.Count > 0 && DayTags.Contains(tag)) present = true;
        if (DayRemovedTags.Count > 0 && DayRemovedTags.Contains(tag)) present = false;

        // Step 3: Hour
        if (HourTags.Count > 0 && HourTags.Contains(tag)) present = true;
        if (HourRemovedTags.Count > 0 && HourRemovedTags.Contains(tag)) present = false;

        // Step 4: Combat
        if (CombatTags.Count > 0 && CombatTags.Contains(tag)) present = true;
        if (CombatRemovedTags.Count > 0 && CombatRemovedTags.Contains(tag)) present = false;

        // Step 5: Aura
        if (AuraTags.Count > 0 && AuraTags.Contains(tag)) present = true;
        if (AuraRemovedTags.Count > 0 && AuraRemovedTags.Contains(tag)) present = false;

        return present;
    }

    public bool HasTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;
        if (HasExplicitTag(tag)) return true;
        if (tag.Equals("Tiny", StringComparison.OrdinalIgnoreCase) && GetRawScalar("Tiny") > 0)
            return true;
        return false;
    }

    public bool HasHiddenTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;
        if (ListContainsIgnoreCase(Template.HiddenTags, tag)) return true;
        if (_questRewardHiddenTags.Count > 0 && _questRewardHiddenTags.Contains(tag)) return true;
        if (!string.IsNullOrEmpty(Enchantment) &&
            Template.Enchantments != null &&
            Template.Enchantments.TryGetValue(Enchantment, out var ench) &&
            ListContainsIgnoreCase(ench.HiddenTags, tag))
            return true;
        if (tag.Equals("Tiny", StringComparison.OrdinalIgnoreCase) && IsTiny)
            return true;
        int bit = GetDynamicEffectTagBit(tag);
        if (bit != 0 && (GetDynamicEffectMask() & bit) != 0)
            return true;
        return false;
    }

    /// <summary>D1-P1-02 (#124-5)：按 Base -> Permanent -> Day -> Hour -> Combat -> Aura 逐层叠算返回当前全部有效标签。</summary>
    public IEnumerable<string> GetAllTags()
    {
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Step 0: Base
        for (int i = 0; i < Template.Tags.Count; i++)
            resolved.Add(Template.Tags[i]);
        if (_questRewardTags.Count > 0)
        {
            foreach (var t in _questRewardTags) resolved.Add(t);
        }
        if (!string.IsNullOrEmpty(Enchantment)
            && Template.Enchantments != null
            && Template.Enchantments.TryGetValue(Enchantment, out var ench))
        {
            for (int i = 0; i < ench.Tags.Count; i++) resolved.Add(ench.Tags[i]);
        }

        // Step 1: Permanent
        if (ExtraTags.Count > 0) foreach (var t in ExtraTags) resolved.Add(t);
        if (PermanentRemovedTags.Count > 0) foreach (var t in PermanentRemovedTags) resolved.Remove(t);

        // Step 2: Day
        if (DayTags.Count > 0) foreach (var t in DayTags) resolved.Add(t);
        if (DayRemovedTags.Count > 0) foreach (var t in DayRemovedTags) resolved.Remove(t);

        // Step 3: Hour
        if (HourTags.Count > 0) foreach (var t in HourTags) resolved.Add(t);
        if (HourRemovedTags.Count > 0) foreach (var t in HourRemovedTags) resolved.Remove(t);

        // Step 4: Combat
        if (CombatTags.Count > 0) foreach (var t in CombatTags) resolved.Add(t);
        if (CombatRemovedTags.Count > 0) foreach (var t in CombatRemovedTags) resolved.Remove(t);

        // Step 5: Aura
        if (AuraTags.Count > 0) foreach (var t in AuraTags) resolved.Add(t);
        if (AuraRemovedTags.Count > 0) foreach (var t in AuraRemovedTags) resolved.Remove(t);

        if (GetRawScalar("Tiny") > 0)
            resolved.Add("Tiny");

        return resolved;
    }

    /// <summary>返回包含模板、附魔与动态生效能力衍生的全部隐藏标签。</summary>
    public IEnumerable<string> GetAllHiddenTags()
    {
        for (int i = 0; i < Template.HiddenTags.Count; i++) yield return Template.HiddenTags[i];
        if (_questRewardHiddenTags.Count > 0)
        {
            foreach (var t in _questRewardHiddenTags)
                if (!ListContainsIgnoreCase(Template.HiddenTags, t)) yield return t;
        }

        EnchantmentData? ench = null;
        if (!string.IsNullOrEmpty(Enchantment) &&
            Template.Enchantments != null &&
            Template.Enchantments.TryGetValue(Enchantment, out ench))
        {
            for (int i = 0; i < ench.HiddenTags.Count; i++)
            {
                var t = ench.HiddenTags[i];
                if (!ListContainsIgnoreCase(Template.HiddenTags, t) && !_questRewardHiddenTags.Contains(t)) yield return t;
            }
        }
        if (IsTiny
            && !ListContainsIgnoreCase(Template.HiddenTags, "Tiny")
            && !_questRewardHiddenTags.Contains("Tiny")
            && (ench == null || !ListContainsIgnoreCase(ench.HiddenTags, "Tiny")))
            yield return "Tiny";

        int mask = GetDynamicEffectMask();
        if (mask != 0)
        {
            for (int i = 0; i < TrackedDynamicEffectTags.Length; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                var dt = TrackedDynamicEffectTags[i];
                if (!ListContainsIgnoreCase(Template.HiddenTags, dt)
                    && !_questRewardHiddenTags.Contains(dt)
                    && (ench == null || !ListContainsIgnoreCase(ench.HiddenTags, dt)))
                    yield return dt;
            }
        }
    }

    /// <summary>返回卡牌所属的全部英雄标签（P1-12，供战斗帧差分回写 CombatSimCardUpdate.Heroes）。</summary>
    public IEnumerable<string> GetAllHeroes()
    {
        if (Template.Heroes == null) yield break;
        for (int i = 0; i < Template.Heroes.Count; i++)
            yield return Template.Heroes[i];
    }

    /// <summary>
    /// 收集当前在该卡牌任一属性层中存在的全部属性键集合（P1-11，用于精确判定附魔移除或限时修饰到期后彻底消失的属性键）。
    /// </summary>
    public HashSet<string> GetActiveAttributeKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in BaseAttributes.Keys) keys.Add(k);
        foreach (var k in PermanentAdd.Keys) keys.Add(k);
        foreach (var k in PermanentMult.Keys) keys.Add(k);
        foreach (var k in PermanentAdditiveMult.Keys) keys.Add(k);
        foreach (var k in HourAdd.Keys) keys.Add(k);
        foreach (var k in HourMult.Keys) keys.Add(k);
        foreach (var k in HourAdditiveMult.Keys) keys.Add(k);
        foreach (var k in DayAdd.Keys) keys.Add(k);
        foreach (var k in DayMult.Keys) keys.Add(k);
        foreach (var k in DayAdditiveMult.Keys) keys.Add(k);
        foreach (var k in CombatAdd.Keys) keys.Add(k);
        foreach (var k in CombatMult.Keys) keys.Add(k);
        foreach (var k in CombatAdditiveMult.Keys) keys.Add(k);
        foreach (var k in CombatZeroMultCount.Keys) keys.Add(k);
        foreach (var k in AuraAdd.Keys) keys.Add(k);
        foreach (var k in AuraMult.Keys) keys.Add(k);
        foreach (var k in AuraAdditiveMult.Keys) keys.Add(k);
        foreach (var k in EnchantmentBaseAttributes.Keys) keys.Add(k);
        foreach (var k in EnchantmentAuraAdd.Keys) keys.Add(k);
        foreach (var k in EnchantmentAuraMult.Keys) keys.Add(k);
        foreach (var k in EnchantmentAuraAdditiveMult.Keys) keys.Add(k);
        return keys;
    }

    /// <summary>判断指定属性键当前是否仍存在于该卡牌任一属性层中（P1-11）。</summary>
    public bool HasAnyAttributeKey(string attr)
        => BaseAttributes.ContainsKey(attr)
        || PermanentAdd.ContainsKey(attr)
        || PermanentMult.ContainsKey(attr)
        || PermanentAdditiveMult.ContainsKey(attr)
        || HourAdd.ContainsKey(attr)
        || HourMult.ContainsKey(attr)
        || HourAdditiveMult.ContainsKey(attr)
        || DayAdd.ContainsKey(attr)
        || DayMult.ContainsKey(attr)
        || DayAdditiveMult.ContainsKey(attr)
        || CombatAdd.ContainsKey(attr)
        || CombatMult.ContainsKey(attr)
        || CombatAdditiveMult.ContainsKey(attr)
        || CombatZeroMultCount.ContainsKey(attr)
        || AuraAdd.ContainsKey(attr)
        || AuraMult.ContainsKey(attr)
        || AuraAdditiveMult.ContainsKey(attr)
        || EnchantmentBaseAttributes.ContainsKey(attr)
        || EnchantmentAuraAdd.ContainsKey(attr)
        || EnchantmentAuraMult.ContainsKey(attr)
        || EnchantmentAuraAdditiveMult.ContainsKey(attr);

    public void SetTier(CardTier newTier) => SetTier(newTier, isTemporary: false);

    public void SetTier(CardTier newTier, bool isTemporary)
    {
        newTier = SpawnQueryEngine.ClampToSupportedTier(Template, newTier);
        if (!isTemporary) PermanentTier = newTier;
        if (newTier == Tier)
        {
            if (!isTemporary && CombatSourceCard == null)
                RefillAmmoToMax();
            return;
        }
        Tier = newTier;
        RebuildTierAndEnchantmentData();
        if (!isTemporary && CombatSourceCard == null)
            RefillAmmoToMax();
    }

    /// <summary>
    /// 返回卡牌下一可升品质（P1-19：放行 Diamond → Legendary 以支持全库唯一 5 阶特例卡 Maker's Mark；
    /// 普通 4 阶卡因 Template.Tiers 不含 Legendary 键，在 CanUpgrade() 中自然拦截）。
    /// </summary>
    public static CardTier GetNextUpgradeTier(CardTier tier) => tier switch
    {
        CardTier.Bronze => CardTier.Silver,
        CardTier.Silver => CardTier.Gold,
        CardTier.Gold => CardTier.Diamond,
        CardTier.Diamond => CardTier.Legendary,
        _ => tier
    };

    public bool CanUpgrade()
    {
        var next = GetNextUpgradeTier(Tier);
        return next != Tier && Template.Tiers != null && Template.Tiers.ContainsKey(next.ToString());
    }

    public bool TryUpgrade() => TryUpgrade(isTemporary: false);

    public bool TryUpgrade(bool isTemporary)
    {
        if (!CanUpgrade()) return false;
        Tier = GetNextUpgradeTier(Tier);
        if (!isTemporary) PermanentTier = Tier;
        RebuildTierAndEnchantmentData();
        if (!isTemporary && CombatSourceCard == null)
            RefillAmmoToMax();
        return true;
    }

    /// <summary>无歧义 1 参便捷入口：永久施加附魔（允许覆写）。</summary>
    public void ApplyEnchantment(string? enchantment)
        => ApplyEnchantment(enchantment, DurationTarget.Permanent, preventOverride: false);

    /// <summary>
    /// P0-07 权威主签名：按显式 <see cref="DurationTarget"/> 持久层施加附魔。
    /// 写入更高持久层（如 Permanent）时不再清空未到期的短时层（Day/Hour），表层附魔按 Hour ?? Day ?? Permanent 自动收敛。
    /// </summary>
    public void ApplyEnchantment(string? enchantment, DurationTarget duration, bool preventOverride = false)
    {
        if (string.IsNullOrEmpty(enchantment)) return;
        if (preventOverride && !string.IsNullOrEmpty(Enchantment)) return;
        switch (duration)
        {
            case DurationTarget.Permanent:
                PermanentEnchantment = enchantment;
                Enchantment = HourEnchantment ?? DayEnchantment ?? PermanentEnchantment;
                break;
            case DurationTarget.Day:
                DayEnchantment = enchantment;
                Enchantment = HourEnchantment ?? DayEnchantment ?? PermanentEnchantment;
                break;
            case DurationTarget.Hour:
                HourEnchantment = enchantment;
                Enchantment = HourEnchantment;
                break;
            default:
                Enchantment = enchantment;
                break;
        }
        RebuildTierAndEnchantmentData();
        if (CombatSourceCard == null)
            RefillAmmoToMax();
    }

    /// <summary>P0-07：识别当前生效表层附魔及其真实来源持久层（Combat / Hour / Day / Permanent）。</summary>
    public (string? Enchantment, DurationTarget Layer) GetEffectiveEnchantmentLayer()
    {
        if (string.IsNullOrEmpty(Enchantment))
            return (null, DurationTarget.Permanent);
        var expectedNonCombat = HourEnchantment ?? DayEnchantment ?? PermanentEnchantment;
        if (!string.Equals(Enchantment, expectedNonCombat, StringComparison.Ordinal))
            return (Enchantment, DurationTarget.Combat);
        if (!string.IsNullOrEmpty(HourEnchantment) && string.Equals(Enchantment, HourEnchantment, StringComparison.Ordinal))
            return (HourEnchantment, DurationTarget.Hour);
        if (!string.IsNullOrEmpty(DayEnchantment) && string.Equals(Enchantment, DayEnchantment, StringComparison.Ordinal))
            return (DayEnchantment, DurationTarget.Day);
        return (PermanentEnchantment, DurationTarget.Permanent);
    }

    /// <summary>P0-07：按生命周期从短到长（Combat &lt; Hour &lt; Day &lt; Permanent）返回较短持久层。</summary>
    public static DurationTarget ShorterDuration(DurationTarget a, DurationTarget b)
    {
        static int Rank(DurationTarget d) => d switch
        {
            DurationTarget.Combat => 0,
            DurationTarget.Hour => 1,
            DurationTarget.Day => 2,
            _ => 3
        };
        return Rank(a) <= Rank(b) ? a : b;
    }

    /// <summary>P0-04/P0-07：一次性无损同步三层非战斗附魔并按 Hour ?? Day ?? Permanent 收敛表层附魔。</summary>
    internal void SyncNonCombatEnchantments(string? perm, string? day, string? hour)
    {
        PermanentEnchantment = string.IsNullOrEmpty(perm) ? null : perm;
        DayEnchantment = string.IsNullOrEmpty(day) ? null : day;
        HourEnchantment = string.IsNullOrEmpty(hour) ? null : hour;
        Enchantment = HourEnchantment ?? DayEnchantment ?? PermanentEnchantment;
        RebuildTierAndEnchantmentData();
    }

    public void RemoveEnchantment() => RemoveEnchantment(DurationTarget.Permanent);

    public void RemoveEnchantment(bool isTemporary)
        => RemoveEnchantment(isTemporary ? DurationTarget.Combat : DurationTarget.Permanent);

    /// <summary>按持续时间层级移除附魔并自动回退至外层持久附魔。</summary>
    public void RemoveEnchantment(DurationTarget duration)
    {
        switch (duration)
        {
            case DurationTarget.Permanent:
                PermanentEnchantment = null;
                DayEnchantment = null;
                HourEnchantment = null;
                Enchantment = null;
                break;
            case DurationTarget.Day:
                DayEnchantment = null;
                Enchantment = HourEnchantment ?? PermanentEnchantment;
                break;
            case DurationTarget.Hour:
                HourEnchantment = null;
                Enchantment = DayEnchantment ?? PermanentEnchantment;
                break;
            default:
                Enchantment = HourEnchantment ?? DayEnchantment ?? PermanentEnchantment;
                break;
        }
        RebuildTierAndEnchantmentData();
        if (CombatSourceCard == null)
            RefillAmmoToMax();
    }

    public void RebuildTierAndEnchantmentData()
    {
        AttributeVersion++;
        _cachedDynamicEffectMask = -1;
        BaseAttributes.Clear();
        EnchantmentBaseAttributes.Clear();
        EnchantmentAuraAdd.Clear();
        EnchantmentAuraMult.Clear();
        EnchantmentAuraAdditiveMult.Clear();
        _enchantmentAbilityIds.Clear();
        _enchantmentAbilities.Clear();
        _enchantmentAuras.Clear();
        ActiveAbilities.Clear();
        ActiveAuras.Clear();
        _questRewardTags.Clear();
        _questRewardHiddenTags.Clear();

        if (Template.Attributes != null)
        {
            foreach (var (k, v) in Template.Attributes)
                BaseAttributes[k] = v;
        }

        // 从 Bronze 依次向上合并到当前 Tier（高阶增量覆盖低阶）
        List<string>? activeAbilityIds = null;
        List<string>? activeAuraIds = null;

        if (Template.Tiers != null && Template.Tiers.Count > 0)
        {
            for (int i = 0; i < AllCardTiers.Length; i++)
            {
                var t = AllCardTiers[i];
                if (t > Tier) break;
                if (!Template.Tiers.TryGetValue(t.ToString(), out var td)) continue;
                foreach (var (k, v) in td.Attributes)
                    BaseAttributes[k] = v;
                if (td.AbilityIds.Count > 0) activeAbilityIds = td.AbilityIds;
                if (td.AuraIds.Count > 0) activeAuraIds = td.AuraIds;
            }
        }
        else
        {
            foreach (var ab in Template.Abilities.Values)
                ActiveAbilities.Add(ab);
            foreach (var au in Template.Auras.Values)
                ActiveAuras.Add(au);
        }

        // 仅对自定义/Mod 卡牌（Version 以 "mod:" 开头或非原版 GUID ID）启用全空 tiers 回退，
        // 严禁对官方 GameData.db 原版卡启用——官方库中 114 张卡（如 Clamera / Bombs Away! / Lucky Clover / A-G Minor）
        // 在 Template.Auras 中保留了未启用的 "Tooltip Handling"（Aura "9"），其 Tiers.*.AuraIds 故意置空。
        bool allowUnlistedTierFallback =
            (Template.Version?.StartsWith("mod:", StringComparison.OrdinalIgnoreCase) == true)
            || (!string.IsNullOrEmpty(Template.Id) && !Guid.TryParse(Template.Id, out _));

        if (activeAbilityIds != null)
        {
            for (int i = 0; i < activeAbilityIds.Count; i++)
            {
                if (Template.Abilities.TryGetValue(activeAbilityIds[i], out var ab))
                    ActiveAbilities.Add(ab);
            }
        }
        else if (allowUnlistedTierFallback && Template.Tiers != null && Template.Tiers.Count > 0 && Template.Abilities.Count > 0)
        {
            // 兜底修复（V13.1）：若自定义 Mod 卡声明了 tiers 与 abilities，但所有阶均未显式配置 abilityIds，
            // 自动回退激活卡牌声明的全部 Abilities，避免战斗中静默 0 触发；同时绝不影响“低阶未解锁、高阶才配 abilityIds”语义。
            bool anyTierDeclaresAbilities = false;
            foreach (var td in Template.Tiers.Values)
            {
                if (td.AbilityIds.Count > 0) { anyTierDeclaresAbilities = true; break; }
            }
            if (!anyTierDeclaresAbilities)
            {
                foreach (var ab in Template.Abilities.Values)
                    ActiveAbilities.Add(ab);
            }
        }

        if (activeAuraIds != null)
        {
            for (int i = 0; i < activeAuraIds.Count; i++)
            {
                if (Template.Auras.TryGetValue(activeAuraIds[i], out var au))
                    ActiveAuras.Add(au);
            }
        }
        else if (allowUnlistedTierFallback && Template.Tiers != null && Template.Tiers.Count > 0 && Template.Auras.Count > 0)
        {
            // 兜底修复（V13.1）：同理，仅对自定义 Mod 卡在全部阶均未显式配置 auraIds 时自动回退激活全部 Auras。
            bool anyTierDeclaresAuras = false;
            foreach (var td in Template.Tiers.Values)
            {
                if (td.AuraIds.Count > 0) { anyTierDeclaresAuras = true; break; }
            }
            if (!anyTierDeclaresAuras)
            {
                foreach (var au in Template.Auras.Values)
                    ActiveAuras.Add(au);
            }
        }

        // 叠加附魔（P1-51：区分全局修饰型/无冲突属性与同名动作参数冲突，隔离全部 15 张可达卡附魔参数污染）
        if (!string.IsNullOrEmpty(Enchantment) &&
            Template.Enchantments != null &&
            Template.Enchantments.TryGetValue(Enchantment, out var ench))
        {
            bool enchHasOwnEffects = ench.Abilities.Count > 0 || ench.Auras.Count > 0;
            foreach (var (k, v) in ench.Attributes)
            {
                bool hasExisting = BaseAttributes.TryGetValue(k, out double existingBase);
                bool isolateToEnchantDomain = enchHasOwnEffects
                    && EnchantmentActionParamAttributes.Contains(k)
                    && hasExisting
                    && Math.Abs(existingBase) > 1e-9;
                if (isolateToEnchantDomain)
                {
                    EnchantmentBaseAttributes[k] = v;
                }
                else
                {
                    // 物品若未在 Tiers 显式写 "Multicast": 1（如自定义卡或原版 Combat Core），其隐含基础施放次数为 1 而非 0，
                    // 否则 Shiny 附魔写入 attributes.Multicast=+1 会变成 0+1=1 彻底失效。
                    double curBase = hasExisting
                        ? existingBase
                        : (k.Equals("Multicast", StringComparison.OrdinalIgnoreCase)
                           && string.Equals(Template.Type, "Item", StringComparison.OrdinalIgnoreCase)
                           && v > 0
                            ? 1.0
                            : 0.0);
                    BaseAttributes[k] = curBase + v;
                }
            }
            foreach (var ab in ench.Abilities.Values)
            {
                ActiveAbilities.Add(ab);
                _enchantmentAbilities.Add(ab);
                if (!string.IsNullOrEmpty(ab.Id))
                    _enchantmentAbilityIds.Add(ab.Id);
            }
            foreach (var au in ench.Auras.Values)
            {
                ActiveAuras.Add(au);
                _enchantmentAuras.Add(au);
            }
        }

        // 叠加已完成 Quest 奖励（兼容永久完成与战中限时完成，兼容直接 Attributes 与按 Tier 缩放的 Reward.Tiers）
        if (Template.Quests != null && (CompletedQuests.Count > 0 || CombatCompletedQuests.Count > 0))
        {
            for (int qi = 0; qi < Template.Quests.Count; qi++)
            {
                var q = Template.Quests[qi];
                for (int ei = 0; ei < q.Entries.Count; ei++)
                {
                    var entry = q.Entries[ei];
                    if (!IsQuestCompleted(entry.AttributeType) || entry.Reward == null) continue;
                    if (entry.Reward.Tiers != null && entry.Reward.Tiers.Count > 0)
                    {
                        Dictionary<string, double>? mergedTierAttrs = null;
                        for (int ti = 0; ti < AllCardTiers.Length; ti++)
                        {
                            var t = AllCardTiers[ti];
                            if (t > Tier) break;
                            if (!entry.Reward.Tiers.TryGetValue(t.ToString(), out var rtd)) continue;
                            mergedTierAttrs ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                            foreach (var (k, v) in rtd.Attributes)
                                mergedTierAttrs[k] = v;
                        }
                        if (mergedTierAttrs != null)
                        {
                            foreach (var (k, v) in mergedTierAttrs)
                                BaseAttributes[k] = BaseAttributes.GetValueOrDefault(k, 0) + v;
                        }
                    }
                    if (entry.Reward.Attributes != null)
                    {
                        foreach (var (k, v) in entry.Reward.Attributes)
                            BaseAttributes[k] = BaseAttributes.GetValueOrDefault(k, 0) + v;
                    }
                    if (entry.Reward.Abilities != null)
                    {
                        foreach (var ab in entry.Reward.Abilities.Values)
                            ActiveAbilities.Add(ab);
                    }
                    if (entry.Reward.Auras != null)
                    {
                        foreach (var au in entry.Reward.Auras.Values)
                            ActiveAuras.Add(au);
                    }
                    if (entry.Reward.Tags != null)
                    {
                        for (int k = 0; k < entry.Reward.Tags.Count; k++)
                        {
                            var rt = entry.Reward.Tags[k];
                            if (!string.IsNullOrWhiteSpace(rt)) _questRewardTags.Add(rt);
                        }
                    }
                    if (entry.Reward.HiddenTags != null)
                    {
                        for (int k = 0; k < entry.Reward.HiddenTags.Count; k++)
                        {
                            var rht = entry.Reward.HiddenTags[k];
                            if (!string.IsNullOrWhiteSpace(rht)) _questRewardHiddenTags.Add(rht);
                        }
                    }
                }
            }
        }
    }

    public double GetAttributeUnscaled(string attr) => GetAttributeUnscaled(attr, AttachedStandardPrices);

    public double GetAttributeUnscaled(string attr, StandardPricesSettings? prices)
    {
        if (attr.Equals("SellPrice", StringComparison.OrdinalIgnoreCase)
            && !BaseAttributes.ContainsKey("SellPrice")
            && Template.Type is "Item" or "Skill")
        {
            var effectivePrices = prices ?? DefaultStandardPrices;
            double baseSell = Template.Type == "Skill"
                ? effectivePrices.GetSkillSellPrice(Tier)
                : effectivePrices.GetItemSellPrice(Template.CardSize, Tier);
            return baseSell + PermanentAdd.GetValueOrDefault(attr, 0);
        }
        var baseVal = BaseAttributes.GetValueOrDefault(attr, 0);
        return baseVal + PermanentAdd.GetValueOrDefault(attr, 0);
    }

    /// <summary>P0-02：判断卡牌是否在基础/附魔/任意修饰层中显式声明了该属性键。</summary>
    public bool HasDeclaredAttributeKey(string attr)
    {
        if (string.IsNullOrEmpty(attr)) return false;
        return (BaseAttributes.Count > 0 && BaseAttributes.ContainsKey(attr))
            || (EnchantmentBaseAttributes.Count > 0 && EnchantmentBaseAttributes.ContainsKey(attr))
            || (PermanentAdd.Count > 0 && PermanentAdd.ContainsKey(attr))
            || (PermanentMult.Count > 0 && PermanentMult.ContainsKey(attr))
            || (PermanentAdditiveMult.Count > 0 && PermanentAdditiveMult.ContainsKey(attr))
            || (HourAdd.Count > 0 && HourAdd.ContainsKey(attr))
            || (HourMult.Count > 0 && HourMult.ContainsKey(attr))
            || (HourAdditiveMult.Count > 0 && HourAdditiveMult.ContainsKey(attr))
            || (DayAdd.Count > 0 && DayAdd.ContainsKey(attr))
            || (DayMult.Count > 0 && DayMult.ContainsKey(attr))
            || (DayAdditiveMult.Count > 0 && DayAdditiveMult.ContainsKey(attr))
            || (CombatAdd.Count > 0 && CombatAdd.ContainsKey(attr))
            || (CombatMult.Count > 0 && CombatMult.ContainsKey(attr))
            || (CombatAdditiveMult.Count > 0 && CombatAdditiveMult.ContainsKey(attr))
            || (CombatZeroMultCount.Count > 0 && CombatZeroMultCount.ContainsKey(attr))
            || (AuraAdd.Count > 0 && AuraAdd.ContainsKey(attr))
            || (AuraMult.Count > 0 && AuraMult.ContainsKey(attr))
            || (AuraAdditiveMult.Count > 0 && AuraAdditiveMult.ContainsKey(attr));
    }

    /// <summary>P0-02：对齐官方 ICard.GetAttributeValue(ECardAttributeType).HasValue 的通用存在性判定。</summary>
    public bool HasAttribute(string attr)
    {
        if (string.IsNullOrEmpty(attr)) return false;

        if (attr.Equals("CooldownMax", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Cooldown", StringComparison.OrdinalIgnoreCase))
            return HasDeclaredAttributeKey("CooldownMax") && GetAttribute("CooldownMax") > 0;
        if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
            return BaseAttributes.ContainsKey("Ammo")
                || BaseAttributes.ContainsKey("AmmoMax")
                || EffectiveAmmoMax > 0
                || CurrentAmmo > 0;
        if (attr.Equals("SellPrice", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("BuyPrice", StringComparison.OrdinalIgnoreCase))
            return Template.Type is "Item" or "Skill" || HasDeclaredAttributeKey(attr);
        if (attr.Equals("Multicast", StringComparison.OrdinalIgnoreCase))
            return HasDeclaredAttributeKey("Multicast")
                || (string.Equals(Template.Type, "Item", StringComparison.OrdinalIgnoreCase)
                    && BaseAttributes.GetValueOrDefault("CooldownMax", 0) > 0);
        // Flying / Heated / Chilled / Tiny / Lifesteal 为全卡默认 0 的状态位属性（全库 133+ 处 == 0 规则直查未声明的普通卡）
        if (attr.Equals("Flying", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Heated", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Chilled", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Lifesteal", StringComparison.OrdinalIgnoreCase))
            return true;
        if (attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase))
            return FreezeMs > 0 || HasDeclaredAttributeKey("Freeze");
        if (attr.Equals("Slow", StringComparison.OrdinalIgnoreCase))
            return SlowMs > 0 || HasDeclaredAttributeKey("Slow");
        if (attr.Equals("Haste", StringComparison.OrdinalIgnoreCase))
            return HasteMs > 0 || HasDeclaredAttributeKey("Haste");
        if (attr.StartsWith("Quest_", StringComparison.OrdinalIgnoreCase))
            return HasQuestProgress(attr) || HasDeclaredAttributeKey(attr);
        if (attr.Equals("Counter", StringComparison.OrdinalIgnoreCase))
            return TimesFiredThisFight > 0 || HasDeclaredAttributeKey("Counter");

        return HasDeclaredAttributeKey(attr);
    }

    /// <summary>D1-P1-17：判断指定卡牌属性是否合法允许负值（Custom_*、*Reduction、*Modifier、*Offset、*Crit）。</summary>
    public static bool AllowsNegativeCardAttribute(string attr)
    {
        if (string.IsNullOrEmpty(attr)) return false;
        if (attr.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase)) return true;
        if (attr.EndsWith("Reduction", StringComparison.OrdinalIgnoreCase)) return true;
        if (attr.EndsWith("Modifier", StringComparison.OrdinalIgnoreCase)) return true;
        if (attr.EndsWith("Offset", StringComparison.OrdinalIgnoreCase)) return true;
        if (attr.Equals("DamageCrit", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("ShieldCrit", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("HealCrit", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("BurnCrit", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("PoisonCrit", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("RegenCrit", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("JoyCrit", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public double GetAttribute(string attr) => GetAttributeWithCooldownFloor(attr, 1000.0, AttachedStandardPrices);

    public double GetAttribute(string attr, StandardPricesSettings? prices)
        => GetAttributeWithCooldownFloor(attr, 1000.0, prices);

    /// <summary>
    /// D1-P1-16：允许调用方传入自定义冷却下限（默认 1000ms，战斗模拟支持 200..1000ms 词条动态下限而不被提前硬钳）。
    /// </summary>
    public double GetAttributeWithCooldownFloor(string attr, double minCooldownFloorMs = 1000.0, StandardPricesSettings? prices = null)
    {
        if (string.IsNullOrEmpty(attr)) return 0;

        // 按首字符快速分派高频实时属性，跳过常规属性（Damage/Shield/Burn/Poison 等）的多余字符串比较
        char c0 = attr[0];
        if (c0 is 'A' or 'a')
        {
            if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
                return CurrentAmmo;
        }
        else if (c0 is 'C' or 'c')
        {
            if (attr.Equals("Counter", StringComparison.OrdinalIgnoreCase))
                return TimesFiredThisFight;
            // D1-P2-07：支持 "Cooldown" 别名返回剩余冷却毫秒（战中实时剩余，战外未计时回退 CooldownMax）
            if (attr.Equals("Cooldown", StringComparison.OrdinalIgnoreCase))
            {
                double cdMax = GetAttributeWithCooldownFloor("CooldownMax", minCooldownFloorMs, prices);
                if (cdMax <= 0) return 0;
                return (CombatSourceCard != null || CooldownProgressMs > 0)
                    ? Math.Max(0.0, cdMax - CooldownProgressMs)
                    : cdMax;
            }
        }
        else if (c0 is 'Q' or 'q')
        {
            // 修复 P2-19：规则公式直接查询 Quest_X 属性时，若各属性层无显式声明值则回退读取 QuestProgress 实时进度
            if (attr.StartsWith("Quest_", StringComparison.OrdinalIgnoreCase))
            {
                bool hasExplicitAttr = BaseAttributes.ContainsKey(attr)
                    || PermanentAdd.ContainsKey(attr)
                    || HourAdd.ContainsKey(attr)
                    || DayAdd.ContainsKey(attr)
                    || CombatAdd.ContainsKey(attr)
                    || AuraAdd.ContainsKey(attr);
                if (!hasExplicitAttr && HasQuestProgress(attr))
                    return GetQuestProgress(attr);
            }
        }
        else if (c0 is 'T' or 't')
        {
            if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
            {
                var scalar = Math.Max(0, GetRawScalar("Tiny"));
                if (scalar > 0) return scalar;
                return HasExplicitTag("Tiny") ? 1.0 : 0.0;
            }
        }
        else if (c0 is 'F' or 'f')
        {
            if (attr.Equals("Flying", StringComparison.OrdinalIgnoreCase))
            {
                double rawFly = GetRawScalar("Flying");
                if (IsFlying)
                    return Math.Max(1.0, 1.0 + rawFly);
                return Math.Max(0, rawFly);
            }
            if (attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase))
                return FreezeMs > 0 ? FreezeMs : Math.Max(0, GetRawScalar("Freeze"));
        }
        else if (c0 is 'H' or 'h')
        {
            if (attr.Equals("Haste", StringComparison.OrdinalIgnoreCase))
                return HasteMs > 0 ? HasteMs : Math.Max(0, GetRawScalar("Haste"));
        }
        else if (c0 is 'S' or 's')
        {
            if (attr.Equals("Slow", StringComparison.OrdinalIgnoreCase))
                return SlowMs > 0 ? SlowMs : Math.Max(0, GetRawScalar("Slow"));
        }

        double baseVal = GetEffectiveBaseAttribute(attr, prices);

        var sum = baseVal;
        if (PermanentAdd.Count > 0 && PermanentAdd.TryGetValue(attr, out var pa)) sum += pa;
        if (HourAdd.Count > 0 && HourAdd.TryGetValue(attr, out var ha)) sum += ha;
        if (DayAdd.Count > 0 && DayAdd.TryGetValue(attr, out var da)) sum += da;
        if (CombatAdd.Count > 0 && CombatAdd.TryGetValue(attr, out var ca)) sum += ca;
        if (AuraAdd.Count > 0 && AuraAdd.TryGetValue(attr, out var aa)) sum += aa;

        // D1-P1-07：严格分离加法乘区 (*AdditiveMult) 与纯乘区 (*Mult)，叠加顺序为 sum * Math.Max(0, 1 + sumAddMult) * prodPureMult
        double totalAdditiveMult = 0.0;
        if (PermanentAdditiveMult.Count > 0 && PermanentAdditiveMult.TryGetValue(attr, out var pam)) totalAdditiveMult += pam;
        if (HourAdditiveMult.Count > 0 && HourAdditiveMult.TryGetValue(attr, out var ham)) totalAdditiveMult += ham;
        if (DayAdditiveMult.Count > 0 && DayAdditiveMult.TryGetValue(attr, out var dam)) totalAdditiveMult += dam;
        if (CombatAdditiveMult.Count > 0 && CombatAdditiveMult.TryGetValue(attr, out var cam)) totalAdditiveMult += cam;
        if (AuraAdditiveMult.Count > 0 && AuraAdditiveMult.TryGetValue(attr, out var aam)) totalAdditiveMult += aam;
        double additiveFactor = Math.Max(0.0, 1.0 + totalAdditiveMult);

        double totalPureMult = 1.0;
        if (CombatZeroMultCount.Count > 0 && CombatZeroMultCount.GetValueOrDefault(attr, 0) > 0)
        {
            totalPureMult = 0.0;
        }
        else
        {
            if (PermanentMult.Count > 0 && PermanentMult.TryGetValue(attr, out var pm)) totalPureMult *= pm;
            if (HourMult.Count > 0 && HourMult.TryGetValue(attr, out var hm)) totalPureMult *= hm;
            if (DayMult.Count > 0 && DayMult.TryGetValue(attr, out var dm)) totalPureMult *= dm;
            if (CombatMult.Count > 0 && CombatMult.TryGetValue(attr, out var cm)) totalPureMult *= cm;
            if (AuraMult.Count > 0 && AuraMult.TryGetValue(attr, out var am)) totalPureMult *= am;
        }

        bool allowNegative = AllowsNegativeCardAttribute(attr);
        var value = sum * additiveFactor * totalPureMult;
        if (!double.IsFinite(value))
            value = SafeMath.ClampFinite(value, allowNegative ? -SafeMath.DefaultCombatMax : 0.0, SafeMath.DefaultCombatMax, 0.0);

        if (c0 is 'H' or 'h' && Heated && value <= 0 && attr.Equals("Heated", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (c0 is 'C' or 'c')
        {
            if (Chilled && value <= 0 && attr.Equals("Chilled", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (value > 0 && attr.Equals("CooldownMax", StringComparison.OrdinalIgnoreCase))
            {
                var flatRed = SafeMath.ClampFinite(GetAttributeRaw("FlatCooldownReduction"), -SafeMath.DefaultCombatMax, SafeMath.DefaultCombatMax, 0.0);
                var pctRed = SafeMath.ClampFinite(GetAttributeRaw("PercentCooldownReduction"), -200.0, 90.0, 0.0);
                double floorMs = SafeMath.ClampFinite(minCooldownFloorMs, 0.0, SafeMath.DefaultCombatMax, 1000.0);
                value = SafeMath.ClampFinite((value - flatRed) * (1.0 - pctRed / 100.0), floorMs, SafeMath.DefaultCombatMax, floorMs);
            }
        }
        else if (c0 is 'T' or 't' && value > 0 && attr.Equals("TempoCost", StringComparison.OrdinalIgnoreCase))
        {
            // 对齐 CooldownMax 减免公式，应用平摊与百分比节奏消耗减免（如 Death Metal Drum Kit 的闪亮附魔）
            var flatRed = SafeMath.ClampFinite(GetAttributeRaw("FlatTempoCostReduction"), -SafeMath.DefaultCombatMax, SafeMath.DefaultCombatMax, 0.0);
            var pctRed = SafeMath.ClampFinite(GetAttributeRaw("PercentTempoCostReduction"), -200.0, 100.0, 0.0);
            value = SafeMath.ClampFinite(Math.Round((value - flatRed) * (1.0 - pctRed / 100.0)), 0.0, SafeMath.DefaultCombatMax, 0.0);
        }

        if (!allowNegative && (!double.IsFinite(value) || value < 0))
            return 0;

        return value;
    }

    /// <summary>
    /// 计算属性的纯基线值（由 GetAttribute 与 GetAttributeRaw / ModifyAttribute("Set") 共享，严格不依赖修改层，确保 Add/Set/Multiply 幂等）。
    /// </summary>
    private double GetEffectiveBaseAttribute(string attr, StandardPricesSettings? prices = null)
    {
        if (BaseAttributes.Count > 0 && BaseAttributes.TryGetValue(attr, out var bv))
            return bv;

        char c0 = attr[0];
        if (c0 is 'S' or 's'
            && attr.Equals("SellPrice", StringComparison.OrdinalIgnoreCase)
            && Template.Type is "Item" or "Skill")
        {
            var effectivePrices = prices ?? DefaultStandardPrices;
            return Template.Type == "Skill"
                ? effectivePrices.GetSkillSellPrice(Tier)
                : effectivePrices.GetItemSellPrice(Template.CardSize, Tier);
        }

        if (c0 is 'M' or 'm'
            && attr.Equals("Multicast", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Template.Type, "Item", StringComparison.OrdinalIgnoreCase)
            && BaseAttributes.GetValueOrDefault("CooldownMax", 0) > 0)
        {
            // 主动冷却物品（含未显式写 Multicast:1 的 53+ 张原版卡与自定义卡）隐含基础施放次数恒为 1
            return 1.0;
        }

        return 0;
    }

    private double GetRawScalar(string attr) => GetAttributeRaw(attr);

    private double GetAttributeRaw(string attr)
    {
        double sum = GetEffectiveBaseAttribute(attr);
        if (PermanentAdd.Count > 0 && PermanentAdd.TryGetValue(attr, out var pa)) sum += pa;
        if (HourAdd.Count > 0 && HourAdd.TryGetValue(attr, out var ha)) sum += ha;
        if (DayAdd.Count > 0 && DayAdd.TryGetValue(attr, out var da)) sum += da;
        if (CombatAdd.Count > 0 && CombatAdd.TryGetValue(attr, out var ca)) sum += ca;
        if (AuraAdd.Count > 0 && AuraAdd.TryGetValue(attr, out var aa)) sum += aa;

        double totalAdditiveMult = 0.0;
        if (PermanentAdditiveMult.Count > 0 && PermanentAdditiveMult.TryGetValue(attr, out var pam)) totalAdditiveMult += pam;
        if (HourAdditiveMult.Count > 0 && HourAdditiveMult.TryGetValue(attr, out var ham)) totalAdditiveMult += ham;
        if (DayAdditiveMult.Count > 0 && DayAdditiveMult.TryGetValue(attr, out var dam)) totalAdditiveMult += dam;
        if (CombatAdditiveMult.Count > 0 && CombatAdditiveMult.TryGetValue(attr, out var cam)) totalAdditiveMult += cam;
        if (AuraAdditiveMult.Count > 0 && AuraAdditiveMult.TryGetValue(attr, out var aam)) totalAdditiveMult += aam;
        double additiveFactor = Math.Max(0.0, 1.0 + totalAdditiveMult);

        double totalPureMult = 1.0;
        if (CombatZeroMultCount.Count > 0 && CombatZeroMultCount.GetValueOrDefault(attr, 0) > 0)
        {
            totalPureMult = 0.0;
        }
        else
        {
            if (PermanentMult.Count > 0 && PermanentMult.TryGetValue(attr, out var pm)) totalPureMult *= pm;
            if (HourMult.Count > 0 && HourMult.TryGetValue(attr, out var hm)) totalPureMult *= hm;
            if (DayMult.Count > 0 && DayMult.TryGetValue(attr, out var dm)) totalPureMult *= dm;
            if (CombatMult.Count > 0 && CombatMult.TryGetValue(attr, out var cm)) totalPureMult *= cm;
            if (AuraMult.Count > 0 && AuraMult.TryGetValue(attr, out var am)) totalPureMult *= am;
        }

        return SafeMath.ClampFinite(sum * additiveFactor * totalPureMult, -SafeMath.DefaultCombatMax, SafeMath.DefaultCombatMax, 0.0);
    }

    /// <summary>
    /// D1-P1-17 / D1-P1-08：严格按持久层边界汇总不高于 <paramref name="maxLayer"/> 的加法净值，
    /// 绝对排除瞬态 AuraAdd/AuraMult 与比目标持久层更短生命周期的临时层，杜绝光环反向烘焙。
    /// </summary>
    private double GetAdditiveSumUpToLayerWithoutAura(string attr, DurationTarget maxLayer)
    {
        double sum = GetEffectiveBaseAttribute(attr, AttachedStandardPrices);
        if (PermanentAdd.Count > 0 && PermanentAdd.TryGetValue(attr, out var pa)) sum += pa;
        if (maxLayer == DurationTarget.Permanent) return sum;
        if (DayAdd.Count > 0 && DayAdd.TryGetValue(attr, out var da)) sum += da;
        if (maxLayer == DurationTarget.Day) return sum;
        if (HourAdd.Count > 0 && HourAdd.TryGetValue(attr, out var ha)) sum += ha;
        if (maxLayer == DurationTarget.Hour) return sum;
        if (CombatAdd.Count > 0 && CombatAdd.TryGetValue(attr, out var ca)) sum += ca;
        return sum;
    }

    public void ModifyAttribute(string attr, string operation, double value, bool permanent)
        => ModifyAttribute(attr, operation, value, permanent ? DurationTarget.Permanent : DurationTarget.Combat, clampNonNegativeSubtract: true);

    public void ModifyAttribute(string attr, string operation, double value, DurationTarget target)
        => ModifyAttribute(attr, operation, value, target, clampNonNegativeSubtract: true);

    public void ModifyAttribute(string attr, string operation, double value, DurationTarget target, bool clampNonNegativeSubtract)
    {
        AttributeVersion++;
        if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
        {
            var ammoMax = EffectiveAmmoMax;
            CurrentAmmo = Math.Clamp(SafeMath.ToSafeInt(ApplyScalar(CurrentAmmo, operation, value), 0, Math.Max(0, ammoMax)), 0, ammoMax);
            return;
        }

        // Freeze / Slow / Haste 直接作用于实时毫秒字段（并清除残留层防止解控失效或负值干扰）
        if (attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Slow", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Haste", StringComparison.OrdinalIgnoreCase))
        {
            int curMs = attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase) ? FreezeMs
                      : attr.Equals("Slow", StringComparison.OrdinalIgnoreCase) ? SlowMs
                      : HasteMs;
            int nextMs = SafeMath.ToSafeInt(ApplyScalar(curMs, operation, value), 0, SafeMath.DefaultCombatMax);
            if (attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase)) FreezeMs = nextMs;
            else if (attr.Equals("Slow", StringComparison.OrdinalIgnoreCase)) SlowMs = nextMs;
            else HasteMs = nextMs;

            CombatAdd.Remove(attr);
            CombatMult.Remove(attr);
            CombatAdditiveMult.Remove(attr);
            CombatZeroMultCount.Remove(attr);
            _combatBaseAdd.Remove(attr);
            _combatBaseMult.Remove(attr);
            _combatBaseAdditiveMult.Remove(attr);
            _combatBaseZeroMultCount.Remove(attr);
            if (nextMs <= 0)
            {
                PermanentAdd.Remove(attr);
                PermanentMult.Remove(attr);
                PermanentAdditiveMult.Remove(attr);
                HourAdd.Remove(attr);
                HourMult.Remove(attr);
                HourAdditiveMult.Remove(attr);
                DayAdd.Remove(attr);
                DayMult.Remove(attr);
                DayAdditiveMult.Remove(attr);
            }
            return;
        }

        if (attr.Equals("Flying", StringComparison.OrdinalIgnoreCase))
        {
            double curFly = GetAttribute("Flying");
            double nextFly = ApplyScalar(curFly, operation, value);
            IsFlying = nextFly > 0;
            if (!IsFlying)
            {
                PermanentAdd.Remove(attr); PermanentMult.Remove(attr); PermanentAdditiveMult.Remove(attr);
                CombatAdd.Remove(attr); CombatMult.Remove(attr); CombatAdditiveMult.Remove(attr); CombatZeroMultCount.Remove(attr);
                _combatBaseAdd.Remove(attr); _combatBaseMult.Remove(attr); _combatBaseAdditiveMult.Remove(attr); _combatBaseZeroMultCount.Remove(attr);
                HourAdd.Remove(attr); HourMult.Remove(attr); HourAdditiveMult.Remove(attr);
                DayAdd.Remove(attr); DayMult.Remove(attr); DayAdditiveMult.Remove(attr);
                // 若存在光环赋予的 Flying，则通过 CombatAdd 压制抵消，确保 FlyingStop/Set 0 立即落地生效
                double auraFly = AuraAdd.GetValueOrDefault(attr, 0);
                if (auraFly > 0)
                    CombatAdd[attr] = -auraFly;
            }
            else if (CombatAdd.TryGetValue(attr, out var negSuppress) && negSuppress < 0)
            {
                CombatAdd.Remove(attr);
            }
            return;
        }

        // D1-P1-13：移除旧版 "Tiny" 提前 return 特判，统一按真实 DurationTarget 写入对应持久层并由 SyncTinyTag 投影标签
        bool hasActiveTimedForAttr = target == DurationTarget.Combat && HasActiveTimedModifierForAttr(attr);
        var (addDict, multDict, addMultDict) = target switch
        {
            DurationTarget.Permanent => (PermanentAdd, PermanentMult, PermanentAdditiveMult),
            DurationTarget.Hour => (HourAdd, HourMult, HourAdditiveMult),
            DurationTarget.Day => (DayAdd, DayMult, DayAdditiveMult),
            _ => hasActiveTimedForAttr
                ? (_combatBaseAdd, _combatBaseMult, _combatBaseAdditiveMult)
                : (CombatAdd, CombatMult, CombatAdditiveMult)
        };
        var zeroDict = hasActiveTimedForAttr ? _combatBaseZeroMultCount : CombatZeroMultCount;

        switch (operation)
        {
            case "Subtract":
                if (!AllowsNegativeCardAttribute(attr) && value > 0 && clampNonNegativeSubtract)
                {
                    double layerPositiveSum = Math.Max(0.0, GetAdditiveSumUpToLayerWithoutAura(attr, target));
                    double clamped = Math.Min(value, layerPositiveSum);
                    if (clamped <= 1e-9)
                        return;
                    addDict[attr] = addDict.GetValueOrDefault(attr, 0) - clamped;
                }
                else
                {
                    addDict[attr] = addDict.GetValueOrDefault(attr, 0) - value;
                }
                break;
            case "Multiply":
                if (target == DurationTarget.Combat && Math.Abs(value) < 1e-9)
                {
                    zeroDict[attr] = zeroDict.GetValueOrDefault(attr, 0) + 1;
                }
                else
                {
                    multDict[attr] = multDict.GetValueOrDefault(attr, 1.0) * value;
                }
                break;
            case "AdditiveMultiply":
            {
                double nextAddMult = addMultDict.GetValueOrDefault(attr, 0.0) + value;
                if (Math.Abs(nextAddMult) < 1e-9) addMultDict.Remove(attr);
                else addMultDict[attr] = nextAddMult;
                break;
            }
            case "Set":
            {
                multDict.Remove(attr);
                addMultDict.Remove(attr);
                if (target == DurationTarget.Combat)
                    zeroDict.Remove(attr);

                // D1-P1-08 & D1-P1-07：仅基于不高于目标持久层的非光环乘区与加法层反算 Set 目标，绝不把瞬态 AuraAdd/AuraMult 反向烘入持久层
                double otherPureMult = 1.0;
                double otherAddMult = 0.0;
                if (target != DurationTarget.Permanent)
                {
                    if (PermanentMult.TryGetValue(attr, out var pm)) otherPureMult *= pm;
                    if (PermanentAdditiveMult.TryGetValue(attr, out var pam)) otherAddMult += pam;
                }
                if (target is DurationTarget.Hour or DurationTarget.Combat)
                {
                    if (DayMult.TryGetValue(attr, out var dm)) otherPureMult *= dm;
                    if (DayAdditiveMult.TryGetValue(attr, out var dam)) otherAddMult += dam;
                }
                if (target == DurationTarget.Combat)
                {
                    if (HourMult.TryGetValue(attr, out var hm)) otherPureMult *= hm;
                    if (HourAdditiveMult.TryGetValue(attr, out var ham)) otherAddMult += ham;
                }
                double otherMult = Math.Max(0.0, 1.0 + otherAddMult) * otherPureMult;

                if (Math.Abs(otherMult) < 1e-9)
                {
                    if (target != DurationTarget.Permanent)
                    {
                        if (PermanentMult.TryGetValue(attr, out var pm0) && Math.Abs(pm0) < 1e-9) PermanentMult.Remove(attr);
                        if (PermanentAdditiveMult.ContainsKey(attr)) PermanentAdditiveMult.Remove(attr);
                    }
                    if (target is DurationTarget.Hour or DurationTarget.Combat)
                    {
                        if (DayMult.TryGetValue(attr, out var dm0) && Math.Abs(dm0) < 1e-9) DayMult.Remove(attr);
                        if (DayAdditiveMult.ContainsKey(attr)) DayAdditiveMult.Remove(attr);
                    }
                    if (target == DurationTarget.Combat)
                    {
                        if (HourMult.TryGetValue(attr, out var hm0) && Math.Abs(hm0) < 1e-9) HourMult.Remove(attr);
                        if (HourAdditiveMult.ContainsKey(attr)) HourAdditiveMult.Remove(attr);
                    }
                    otherMult = 1.0;
                }

                double otherLayers = GetAdditiveSumUpToLayerWithoutAura(attr, target) - CombatOrLayerAddValue(attr, target, addDict);
                double safeTarget = SafeMath.ClampFinite(value, AllowsNegativeCardAttribute(attr) ? -SafeMath.DefaultCombatMax : 0.0, SafeMath.DefaultCombatMax, 0.0);
                addDict[attr] = SafeMath.ClampFinite((safeTarget / otherMult) - otherLayers, -SafeMath.DefaultCombatMax, SafeMath.DefaultCombatMax, 0.0);
                break;
            }
            default: // Add
                addDict[attr] = addDict.GetValueOrDefault(attr, 0) + value;
                break;
        }

        if (hasActiveTimedForAttr)
            RecomputeCombatAttributeFromBaseline(attr);

        if ((attr.Equals("Heated", StringComparison.OrdinalIgnoreCase) || attr.Equals("Chilled", StringComparison.OrdinalIgnoreCase))
            && ((string.Equals(operation, "Multiply", StringComparison.OrdinalIgnoreCase) && Math.Abs(value) < 1e-9)
                || (string.Equals(operation, "Set", StringComparison.OrdinalIgnoreCase) && value <= 0)))
        {
            if (attr.Equals("Heated", StringComparison.OrdinalIgnoreCase)) Heated = false;
            else Chilled = false;
        }

        if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
            SyncTinyTag(target, target == DurationTarget.Combat ? CombatAdd : addDict);

        if (attr.Equals("AmmoMax", StringComparison.OrdinalIgnoreCase))
        {
            if (target == DurationTarget.Permanent && CombatSourceCard == null)
            {
                // 非战斗状态下永久调整弹药上限后，立即补满当前弹药以同步界面与 Multicast 光环
                RefillAmmoToMax();
            }
            else
            {
                CurrentAmmo = Math.Clamp(CurrentAmmo, 0, EffectiveAmmoMax);
            }
        }
    }

    private double CombatOrLayerAddValue(string attr, DurationTarget target, Dictionary<string, double> addDict)
        => target == DurationTarget.Combat ? CombatAdd.GetValueOrDefault(attr, 0) : addDict.GetValueOrDefault(attr, 0);

    private bool HasActiveTimedModifierForAttr(string attr)
    {
        for (int i = 0; i < _activeTimedCombatModifiers.Count; i++)
        {
            if (_activeTimedCombatModifiers[i].Attr.Equals(attr, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// D1-P1-07 (#126/#128 备忘)：向限时活性列表登记 TCombatDuration 战斗修饰并在非限时基线上单向重算聚合（0 逆除法）。
    /// 返回唯一 tokenId 供到期撤销使用。
    /// </summary>
    public int ApplyTimedCombatModifier(string attr, string effectiveOp, double value)
    {
        AttributeVersion++;
        if (string.IsNullOrEmpty(attr)) return 0;

        // 首次为该 attr 施加限时修饰时，将当前 Combat* 快照锁定为非限时整场战中基线
        if (!HasActiveTimedModifierForAttr(attr))
        {
            if (CombatAdd.TryGetValue(attr, out var ca)) _combatBaseAdd[attr] = ca; else _combatBaseAdd.Remove(attr);
            if (CombatMult.TryGetValue(attr, out var cm)) _combatBaseMult[attr] = cm; else _combatBaseMult.Remove(attr);
            if (CombatAdditiveMult.TryGetValue(attr, out var cam)) _combatBaseAdditiveMult[attr] = cam; else _combatBaseAdditiveMult.Remove(attr);
            if (CombatZeroMultCount.TryGetValue(attr, out var cz) && cz > 0) _combatBaseZeroMultCount[attr] = cz; else _combatBaseZeroMultCount.Remove(attr);
        }

        int tokenId = _nextTimedModifierToken++;
        _activeTimedCombatModifiers.Add(new TimedAttributeModifier(tokenId, attr, effectiveOp, value));
        RecomputeCombatAttributeFromBaseline(attr);
        if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
            SyncTinyTag(DurationTarget.Combat, CombatAdd);

        if (attr.Equals("AmmoMax", StringComparison.OrdinalIgnoreCase))
            CurrentAmmo = Math.Clamp(CurrentAmmo, 0, EffectiveAmmoMax);
        return tokenId;
    }

    /// <summary>D1-P1-07 (#126/#128 备忘)：按 tokenId 摘除到期限时修饰并单向重算（0 逆除法、绝不丢失非限时战斗增益）。</summary>
    public bool RevertTimedCombatModifier(int tokenId)
    {
        if (tokenId <= 0) return false;
        int idx = -1;
        for (int i = 0; i < _activeTimedCombatModifiers.Count; i++)
        {
            if (_activeTimedCombatModifiers[i].TokenId == tokenId) { idx = i; break; }
        }
        if (idx < 0) return false;

        string attr = _activeTimedCombatModifiers[idx].Attr;
        _activeTimedCombatModifiers.RemoveAt(idx);
        AttributeVersion++;
        RecomputeCombatAttributeFromBaseline(attr);
        if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
            SyncTinyTag(DurationTarget.Combat, CombatAdd);

        if (attr.Equals("AmmoMax", StringComparison.OrdinalIgnoreCase))
            CurrentAmmo = Math.Clamp(CurrentAmmo, 0, EffectiveAmmoMax);
        return true;
    }

    private void RecomputeCombatAttributeFromBaseline(string attr)
    {
        double add = _combatBaseAdd.GetValueOrDefault(attr, 0.0);
        double mult = _combatBaseMult.GetValueOrDefault(attr, 1.0);
        double addMult = _combatBaseAdditiveMult.GetValueOrDefault(attr, 0.0);
        int zeroCnt = _combatBaseZeroMultCount.GetValueOrDefault(attr, 0);

        for (int i = 0; i < _activeTimedCombatModifiers.Count; i++)
        {
            var m = _activeTimedCombatModifiers[i];
            if (!m.Attr.Equals(attr, StringComparison.OrdinalIgnoreCase)) continue;
            switch (m.EffectiveOp)
            {
                case "Subtract":
                    add -= m.Value;
                    break;
                case "Multiply":
                    if (Math.Abs(m.Value) < 1e-9) zeroCnt++;
                    else mult *= m.Value;
                    break;
                case "AdditiveMultiply":
                    addMult += m.Value;
                    break;
                default: // Add
                    add += m.Value;
                    break;
            }
        }

        if (Math.Abs(add) < 1e-9) CombatAdd.Remove(attr); else CombatAdd[attr] = add;
        if (Math.Abs(mult - 1.0) < 1e-9) CombatMult.Remove(attr); else CombatMult[attr] = mult;
        if (Math.Abs(addMult) < 1e-9) CombatAdditiveMult.Remove(attr); else CombatAdditiveMult[attr] = addMult;
        if (zeroCnt <= 0) CombatZeroMultCount.Remove(attr); else CombatZeroMultCount[attr] = zeroCnt;
    }

    /// <summary>
    /// D1-P1-08：受控归零复位（用于非限时 Multiply 0）。
    /// 1) 强制递增 AttributeVersion 避免快照脏读；
    /// 2) 仅抵消不高于 target 的持久层累加值，严禁烘焙瞬态 AuraAdd；
    /// 3) 严守 P0-02 零幽灵键不变量：卡牌原本无该属性或待写抵消量 <= 1e-9 时绝不向 *Add 写入 0 键值对。
    /// </summary>
    public void ResetAttributeToZero(string attr, DurationTarget target)
    {
        AttributeVersion++;
        if (string.IsNullOrEmpty(attr)) return;

        if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
        {
            CurrentAmmo = 0;
            return;
        }
        if (attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Slow", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Haste", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Flying", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
        {
            ModifyAttribute(attr, "Set", 0, target, clampNonNegativeSubtract: true);
            return;
        }

        if (attr.Equals("Heated", StringComparison.OrdinalIgnoreCase)) Heated = false;
        else if (attr.Equals("Chilled", StringComparison.OrdinalIgnoreCase)) Chilled = false;

        double effBase = GetEffectiveBaseAttribute(attr, AttachedStandardPrices);
        bool hadDeclaredKey = HasDeclaredAttributeKey(attr);
        if (!hadDeclaredKey && Math.Abs(effBase) <= 1e-9)
            return;

        switch (target)
        {
            case DurationTarget.Permanent:
                PermanentMult.Remove(attr);
                PermanentAdditiveMult.Remove(attr);
                if (Math.Abs(effBase) > 1e-9)
                    PermanentAdd[attr] = -effBase;
                else
                    PermanentAdd.Remove(attr);
                break;
            case DurationTarget.Day:
            {
                DayMult.Remove(attr);
                DayAdditiveMult.Remove(attr);
                DayAdd.Remove(attr);
                double under = effBase + PermanentAdd.GetValueOrDefault(attr, 0);
                if (Math.Abs(under) > 1e-9)
                    DayAdd[attr] = -under;
                break;
            }
            case DurationTarget.Hour:
            {
                HourMult.Remove(attr);
                HourAdditiveMult.Remove(attr);
                HourAdd.Remove(attr);
                double under = effBase + PermanentAdd.GetValueOrDefault(attr, 0) + DayAdd.GetValueOrDefault(attr, 0);
                if (Math.Abs(under) > 1e-9)
                    HourAdd[attr] = -under;
                break;
            }
            default:
            {
                CombatMult.Remove(attr);
                CombatAdditiveMult.Remove(attr);
                CombatAdd.Remove(attr);
                CombatZeroMultCount.Remove(attr);
                _combatBaseMult.Remove(attr);
                _combatBaseAdditiveMult.Remove(attr);
                _combatBaseAdd.Remove(attr);
                _combatBaseZeroMultCount.Remove(attr);
                // D1-P1-07：归零时同步清除该属性的活性限时修饰，避免 under == 0 或后续定时事件令旧限时修饰残影复活
                if (_activeTimedCombatModifiers.Count > 0)
                    _activeTimedCombatModifiers.RemoveAll(m => m.Attr.Equals(attr, StringComparison.OrdinalIgnoreCase));
                double under = effBase
                    + PermanentAdd.GetValueOrDefault(attr, 0)
                    + DayAdd.GetValueOrDefault(attr, 0)
                    + HourAdd.GetValueOrDefault(attr, 0);
                if (Math.Abs(under) > 1e-9)
                {
                    _combatBaseAdd[attr] = -under;
                    CombatAdd[attr] = -under;
                }
                break;
            }
        }

        if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
        {
            var syncAddDict = target switch
            {
                DurationTarget.Permanent => PermanentAdd,
                DurationTarget.Day => DayAdd,
                DurationTarget.Hour => HourAdd,
                _ => CombatAdd
            };
            SyncTinyTag(target, syncAddDict);
        }

        if (attr.Equals("AmmoMax", StringComparison.OrdinalIgnoreCase))
        {
            if (target == DurationTarget.Permanent && CombatSourceCard == null)
                RefillAmmoToMax();
            else
                CurrentAmmo = Math.Clamp(CurrentAmmo, 0, EffectiveAmmoMax);
        }
    }

    private void SyncTinyTag(DurationTarget target, Dictionary<string, double> addDict)
    {
        bool isPositive = addDict.GetValueOrDefault("Tiny", 0) > 0;
        var (posSet, negSet) = target switch
        {
            DurationTarget.Permanent => (ExtraTags, PermanentRemovedTags),
            DurationTarget.Day => (DayTags, DayRemovedTags),
            DurationTarget.Hour => (HourTags, HourRemovedTags),
            _ => (CombatTags, CombatRemovedTags)
        };
        if (isPositive)
        {
            negSet.Remove("Tiny");
            posSet.Add("Tiny");
        }
        else
        {
            posSet.Remove("Tiny");
        }
    }

    public void ApplyAuraAttribute(string attr, string operation, double value)
    {
        AttributeVersion++;
        switch (operation)
        {
            case "Subtract":
                AuraAdd[attr] = AuraAdd.GetValueOrDefault(attr, 0) - value;
                break;
            case "Multiply":
                AuraMult[attr] = AuraMult.GetValueOrDefault(attr, 1.0) * value;
                break;
            case "AdditiveMultiply":
            {
                double next = AuraAdditiveMult.GetValueOrDefault(attr, 0.0) + value;
                if (Math.Abs(next) < 1e-9) AuraAdditiveMult.Remove(attr);
                else AuraAdditiveMult[attr] = next;
                break;
            }
            default:
                AuraAdd[attr] = AuraAdd.GetValueOrDefault(attr, 0) + value;
                break;
        }

        if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
        {
            if (AuraAdd.GetValueOrDefault("Tiny", 0) > 0) ApplyAuraAddTag("Tiny");
            else AuraTags.Remove("Tiny");
        }
    }

    public bool IsEnchantmentAbility(string? abilityId)
        => !string.IsNullOrEmpty(abilityId) && _enchantmentAbilityIds.Contains(abilityId);

    /// <summary>D1-P1-09：按对象引用精准判定附魔技能（防 Mod 附魔技能与基础技能 ID 同名撞车）。</summary>
    public bool IsEnchantmentAbility(AbilityDefinition? ability)
        => ability != null && _enchantmentAbilities.Contains(ability);

    public bool IsEnchantmentAura(AuraDefinition? aura)
        => aura != null && _enchantmentAuras.Contains(aura);

    public bool HasIsolatedEnchantmentAttribute(string attr)
        => EnchantmentBaseAttributes.ContainsKey(attr);

    /// <summary>
    /// D1-P1-09 (#118-4)：读取附魔隔离域最终动作参数值。
    /// 严格隔离基础模板 BaseAttributes（无外部增益时维持精确隔离底值），同时叠加外部非模板持久/限时/战斗/光环增益与附魔专属光环输出。
    /// </summary>
    public double GetEnchantmentAttribute(string attr)
    {
        double sum = EnchantmentBaseAttributes.GetValueOrDefault(attr, 0)
                   + EnchantmentAuraAdd.GetValueOrDefault(attr, 0);
        if (PermanentAdd.Count > 0 && PermanentAdd.TryGetValue(attr, out var pa)) sum += pa;
        if (HourAdd.Count > 0 && HourAdd.TryGetValue(attr, out var ha)) sum += ha;
        if (DayAdd.Count > 0 && DayAdd.TryGetValue(attr, out var da)) sum += da;
        if (CombatAdd.Count > 0 && CombatAdd.TryGetValue(attr, out var ca)) sum += ca;
        if (AuraAdd.Count > 0 && AuraAdd.TryGetValue(attr, out var aa)) sum += aa;

        double totalAddMult = EnchantmentAuraAdditiveMult.GetValueOrDefault(attr, 0.0);
        if (PermanentAdditiveMult.Count > 0 && PermanentAdditiveMult.TryGetValue(attr, out var pam)) totalAddMult += pam;
        if (HourAdditiveMult.Count > 0 && HourAdditiveMult.TryGetValue(attr, out var ham)) totalAddMult += ham;
        if (DayAdditiveMult.Count > 0 && DayAdditiveMult.TryGetValue(attr, out var dam)) totalAddMult += dam;
        if (CombatAdditiveMult.Count > 0 && CombatAdditiveMult.TryGetValue(attr, out var cam)) totalAddMult += cam;
        if (AuraAdditiveMult.Count > 0 && AuraAdditiveMult.TryGetValue(attr, out var aam)) totalAddMult += aam;
        double additiveFactor = Math.Max(0.0, 1.0 + totalAddMult);

        double pureMult;
        if (CombatZeroMultCount.Count > 0 && CombatZeroMultCount.GetValueOrDefault(attr, 0) > 0)
        {
            pureMult = 0.0;
        }
        else
        {
            pureMult = EnchantmentAuraMult.GetValueOrDefault(attr, 1.0);
            if (PermanentMult.Count > 0 && PermanentMult.TryGetValue(attr, out var pm)) pureMult *= pm;
            if (HourMult.Count > 0 && HourMult.TryGetValue(attr, out var hm)) pureMult *= hm;
            if (DayMult.Count > 0 && DayMult.TryGetValue(attr, out var dm)) pureMult *= dm;
            if (CombatMult.Count > 0 && CombatMult.TryGetValue(attr, out var cm)) pureMult *= cm;
            if (AuraMult.Count > 0 && AuraMult.TryGetValue(attr, out var am)) pureMult *= am;
        }

        bool allowNegative = AllowsNegativeCardAttribute(attr);
        double res = sum * additiveFactor * pureMult;
        if (!double.IsFinite(res))
            return SafeMath.ClampFinite(res, allowNegative ? -SafeMath.DefaultCombatMax : 0.0, SafeMath.DefaultCombatMax, 0.0);
        return (!allowNegative && res < 0) ? 0.0 : res;
    }

    /// <summary>按当前执行能力归属读取对应域的属性值（兼容字符串重载，供存量单测无缝复用）。</summary>
    public double GetAttributeForAbility(string attr, string? abilityId)
        => (IsEnchantmentAbility(abilityId) && HasIsolatedEnchantmentAttribute(attr))
            ? GetEnchantmentAttribute(attr)
            : GetAttribute(attr);

    /// <summary>D1-P1-09：按对象引用读取能力归属域属性值（引擎热路径优先调用，零同名 ID 碰撞）。</summary>
    public double GetAttributeForAbility(string attr, AbilityDefinition? ability)
        => (IsEnchantmentAbility(ability) && HasIsolatedEnchantmentAttribute(attr))
            ? GetEnchantmentAttribute(attr)
            : GetAttribute(attr);

    /// <summary>P0-08：带存在性判定的能力属性读取（兼容字符串重载）。</summary>
    public bool TryGetAttributeForAbility(string attr, string? abilityId, out double value)
    {
        if (string.IsNullOrEmpty(attr))
        {
            value = 0;
            return false;
        }
        if (IsEnchantmentAbility(abilityId) && HasIsolatedEnchantmentAttribute(attr))
        {
            value = GetEnchantmentAttribute(attr);
            return true;
        }
        if (HasAttribute(attr))
        {
            value = GetAttribute(attr);
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>D1-P1-09：带存在性判定的对象引用能力属性读取。</summary>
    public bool TryGetAttributeForAbility(string attr, AbilityDefinition? ability, out double value)
    {
        if (string.IsNullOrEmpty(attr))
        {
            value = 0;
            return false;
        }
        if (IsEnchantmentAbility(ability) && HasIsolatedEnchantmentAttribute(attr))
        {
            value = GetEnchantmentAttribute(attr);
            return true;
        }
        if (HasAttribute(attr))
        {
            value = GetAttribute(attr);
            return true;
        }
        value = 0;
        return false;
    }

    public void ApplyEnchantmentAuraAttribute(string attr, string operation, double value)
    {
        AttributeVersion++;
        switch (operation)
        {
            case "Subtract":
                EnchantmentAuraAdd[attr] = EnchantmentAuraAdd.GetValueOrDefault(attr, 0) - value;
                break;
            case "Multiply":
                EnchantmentAuraMult[attr] = EnchantmentAuraMult.GetValueOrDefault(attr, 1.0) * value;
                break;
            case "AdditiveMultiply":
            {
                double next = EnchantmentAuraAdditiveMult.GetValueOrDefault(attr, 0.0) + value;
                if (Math.Abs(next) < 1e-9) EnchantmentAuraAdditiveMult.Remove(attr);
                else EnchantmentAuraAdditiveMult[attr] = next;
                break;
            }
            default:
                EnchantmentAuraAdd[attr] = EnchantmentAuraAdd.GetValueOrDefault(attr, 0) + value;
                break;
        }
    }

    public void ClearAuraModifiers()
    {
        if (AuraAdd.Count > 0 || AuraMult.Count > 0 || AuraAdditiveMult.Count > 0
            || AuraTags.Count > 0 || AuraRemovedTags.Count > 0
            || EnchantmentAuraAdd.Count > 0 || EnchantmentAuraMult.Count > 0 || EnchantmentAuraAdditiveMult.Count > 0)
            AttributeVersion++;
        AuraAdd.Clear();
        AuraMult.Clear();
        AuraAdditiveMult.Clear();
        AuraTags.Clear();
        AuraRemovedTags.Clear();
        EnchantmentAuraAdd.Clear();
        EnchantmentAuraMult.Clear();
        EnchantmentAuraAdditiveMult.Clear();
    }

    public void ClearEndOfHourModifiers()
    {
        if (HourAdd.Count > 0 || HourMult.Count > 0 || HourAdditiveMult.Count > 0
            || HourTags.Count > 0 || HourRemovedTags.Count > 0)
            AttributeVersion++;
        HourAdd.Clear();
        HourMult.Clear();
        HourAdditiveMult.Clear();
        HourTags.Clear();
        HourRemovedTags.Clear();
        if (HourEnchantment != null)
        {
            HourEnchantment = null;
            var fallback = DayEnchantment ?? PermanentEnchantment;
            if (!string.Equals(Enchantment, fallback, StringComparison.Ordinal))
            {
                Enchantment = fallback;
                RebuildTierAndEnchantmentData();
            }
        }
        if (CombatSourceCard == null)
            RefillAmmoToMax();
    }

    public void ClearEndOfDayModifiers()
    {
        if (DayAdd.Count > 0 || DayMult.Count > 0 || DayAdditiveMult.Count > 0
            || DayTags.Count > 0 || DayRemovedTags.Count > 0
            || HourAdd.Count > 0 || HourMult.Count > 0 || HourAdditiveMult.Count > 0
            || HourTags.Count > 0 || HourRemovedTags.Count > 0)
            AttributeVersion++;
        DayAdd.Clear();
        DayMult.Clear();
        DayAdditiveMult.Clear();
        DayTags.Clear();
        DayRemovedTags.Clear();
        // D1-P2-05：换日时同步清空全部 Hour 层修饰与标签，防止 Hour 层跨日泄漏
        HourAdd.Clear();
        HourMult.Clear();
        HourAdditiveMult.Clear();
        HourTags.Clear();
        HourRemovedTags.Clear();
        if (DayEnchantment != null || HourEnchantment != null)
        {
            DayEnchantment = null;
            HourEnchantment = null;
            if (!string.Equals(Enchantment, PermanentEnchantment, StringComparison.Ordinal))
            {
                Enchantment = PermanentEnchantment;
                RebuildTierAndEnchantmentData();
            }
        }
        if (CombatSourceCard == null)
            RefillAmmoToMax();
    }

    /// <summary>当前卡牌有效弹药上限（综合基础属性、各级修正与光环倍率）。</summary>
    public int EffectiveAmmoMax
    {
        get
        {
            int max = SafeMath.ToSafeInt(GetAttribute("AmmoMax"), 0, SafeMath.DefaultCombatMax);
            if (max == 0 && BaseAttributes.TryGetValue("Ammo", out var ammo))
                max = SafeMath.ToSafeInt(ammo, 0, SafeMath.DefaultCombatMax);
            return max;
        }
    }

    /// <summary>将当前弹药补满为最新有效弹药上限（供非战斗升阶及脱战复位调用）。</summary>
    public void RefillAmmoToMax() => CurrentAmmo = EffectiveAmmoMax;

    public void RevertCombatAttributeModifier(string attr, string operation, double value)
    {
        AttributeVersion++;
        switch (operation)
        {
            case "Subtract":
            {
                double next = CombatAdd.GetValueOrDefault(attr, 0) + value;
                if (Math.Abs(next) < 1e-9) CombatAdd.Remove(attr);
                else CombatAdd[attr] = next;
                break;
            }
            case "Multiply":
            {
                if (Math.Abs(value) < 1e-9)
                {
                    if (CombatZeroMultCount.TryGetValue(attr, out int cnt))
                    {
                        if (cnt <= 1) CombatZeroMultCount.Remove(attr);
                        else CombatZeroMultCount[attr] = cnt - 1;
                    }
                }
                else
                {
                    double next = CombatMult.GetValueOrDefault(attr, 1.0) / value;
                    if (Math.Abs(next - 1.0) < 1e-9) CombatMult.Remove(attr);
                    else CombatMult[attr] = next;
                }
                break;
            }
            case "AdditiveMultiply":
            {
                double next = CombatAdditiveMult.GetValueOrDefault(attr, 0.0) - value;
                if (Math.Abs(next) < 1e-9) CombatAdditiveMult.Remove(attr);
                else CombatAdditiveMult[attr] = next;
                break;
            }
            default: // Add
            {
                double next = CombatAdd.GetValueOrDefault(attr, 0) - value;
                if (Math.Abs(next) < 1e-9) CombatAdd.Remove(attr);
                else CombatAdd[attr] = next;
                break;
            }
        }
    }

    public void ResetForCombat(GameDataRepository? repo)
    {
        bool needRebuild = false;
        var expectedEnch = HourEnchantment ?? DayEnchantment ?? PermanentEnchantment;
        if (!string.Equals(Enchantment, expectedEnch, StringComparison.Ordinal))
        {
            Enchantment = expectedEnch;
            needRebuild = true;
        }
        if (Tier != PermanentTier)
        {
            Tier = PermanentTier;
            needRebuild = true;
        }
        if (CombatCompletedQuests.Count > 0)
        {
            CombatCompletedQuests.Clear();
            needRebuild = true;
        }
        CombatQuestProgress.Clear();
        if (needRebuild) RebuildTierAndEnchantmentData();

        CombatAdd.Clear();
        CombatMult.Clear();
        CombatAdditiveMult.Clear();
        CombatZeroMultCount.Clear();
        _combatBaseAdd.Clear();
        _combatBaseMult.Clear();
        _combatBaseAdditiveMult.Clear();
        _combatBaseZeroMultCount.Clear();
        _activeTimedCombatModifiers.Clear();
        CombatTags.Clear();
        CombatRemovedTags.Clear();
        EffectiveCombatWidthCap = null;
        CooldownProgressMs = 0;
        HasteMs = 0;
        SlowMs = 0;
        FreezeMs = 0;
        IsFlying = false;
        Heated = false;
        Chilled = false;
        IsCasting = false;
        IsDisabled = false;
        IsDestroyed = false;
        TimesFiredThisFight = 0;
        CombatCustomFlags.Clear();
        CombatCustomCounters.Clear();
        AttributeVersion++;
        RefillAmmoToMax();
    }

    /// <summary>
    /// D1-P1-12：唯一权威变形保留状态迁移入口（统一供 AbilityExecutor.CopyTransformRetentionState 与 RunEngine 祭坛变形调用）。
    /// 按 maxDuration 完整迁移永久及覆盖范围内的 Day/Hour/Combat 属性与正负标签层、按 ShorterDuration 迁移分层附魔，
    /// 末尾统一 RebuildTierAndEnchantmentData() + RefillAmmoToMax() + MarkAttributeDirty()。
    /// </summary>
    public void CopyTransformRetentionFrom(
        CardInstance original,
        DurationTarget maxDuration,
        bool inheritEnchantment,
        Func<CardTemplate, string, bool>? isEnchantmentCompatible = null)
    {
        if (original == null) return;

        bool copyDay = maxDuration != DurationTarget.Permanent || original.DayAdd.Count > 0 || original.DayMult.Count > 0 || original.DayAdditiveMult.Count > 0 || original.DayTags.Count > 0 || original.DayRemovedTags.Count > 0;
        bool copyHour = maxDuration != DurationTarget.Permanent || original.HourAdd.Count > 0 || original.HourMult.Count > 0 || original.HourAdditiveMult.Count > 0 || original.HourTags.Count > 0 || original.HourRemovedTags.Count > 0;
        bool copyCombat = maxDuration == DurationTarget.Combat;

        foreach (var (k, v) in original.PermanentAdd) PermanentAdd[k] = v;
        foreach (var (k, v) in original.PermanentMult) PermanentMult[k] = v;
        foreach (var (k, v) in original.PermanentAdditiveMult) PermanentAdditiveMult[k] = v;
        foreach (var t in original.ExtraTags) ExtraTags.Add(t);
        foreach (var t in original.PermanentRemovedTags) PermanentRemovedTags.Add(t);

        if (copyDay)
        {
            foreach (var (k, v) in original.DayAdd) DayAdd[k] = v;
            foreach (var (k, v) in original.DayMult) DayMult[k] = v;
            foreach (var (k, v) in original.DayAdditiveMult) DayAdditiveMult[k] = v;
            foreach (var t in original.DayTags) DayTags.Add(t);
            foreach (var t in original.DayRemovedTags) DayRemovedTags.Add(t);
        }
        if (copyHour)
        {
            foreach (var (k, v) in original.HourAdd) HourAdd[k] = v;
            foreach (var (k, v) in original.HourMult) HourMult[k] = v;
            foreach (var (k, v) in original.HourAdditiveMult) HourAdditiveMult[k] = v;
            foreach (var t in original.HourTags) HourTags.Add(t);
            foreach (var t in original.HourRemovedTags) HourRemovedTags.Add(t);
        }
        if (copyCombat)
        {
            foreach (var (k, v) in original.CombatAdd) CombatAdd[k] = v;
            foreach (var (k, v) in original.CombatMult) CombatMult[k] = v;
            foreach (var (k, v) in original.CombatAdditiveMult) CombatAdditiveMult[k] = v;
            foreach (var (k, v) in original.CombatZeroMultCount) CombatZeroMultCount[k] = v;
            foreach (var t in original.CombatTags) CombatTags.Add(t);
            foreach (var t in original.CombatRemovedTags) CombatRemovedTags.Add(t);
        }

        if (inheritEnchantment)
        {
            bool CanInherit(string? e)
                => !string.IsNullOrEmpty(e)
                   && (isEnchantmentCompatible != null
                       ? isEnchantmentCompatible(Template, e)
                       : (Template.Enchantments == null || Template.Enchantments.Count == 0 || Template.Enchantments.ContainsKey(e)));

            if (CanInherit(original.PermanentEnchantment))
                ApplyEnchantment(original.PermanentEnchantment, ShorterDuration(DurationTarget.Permanent, maxDuration), preventOverride: false);
            if (CanInherit(original.DayEnchantment))
                ApplyEnchantment(original.DayEnchantment, ShorterDuration(DurationTarget.Day, maxDuration), preventOverride: false);
            if (CanInherit(original.HourEnchantment))
                ApplyEnchantment(original.HourEnchantment, ShorterDuration(DurationTarget.Hour, maxDuration), preventOverride: false);

            var (effEnch, effLayer) = original.GetEffectiveEnchantmentLayer();
            if (effLayer == DurationTarget.Combat && CanInherit(effEnch))
                ApplyEnchantment(effEnch, DurationTarget.Combat, preventOverride: false);
        }

        WasTransformed = true;
        TransformedFromInstanceId = original.InstanceId;
        RebuildTierAndEnchantmentData();
        RefillAmmoToMax();
        MarkAttributeDirty();
    }

    /// <summary>
    /// D1-P2-04：二合一融合时将消耗卡的非价格持久/限时属性层与正负标签无损合并至保留主卡并标脏重建。
    /// </summary>
    public void MergeFromFusedCard(CardInstance consumed, bool skipPriceAttributes = true)
    {
        if (consumed == null || ReferenceEquals(this, consumed)) return;

        static bool IsPriceAttr(string k)
            => k.Equals("BuyPrice", StringComparison.OrdinalIgnoreCase)
               || k.Equals("SellPrice", StringComparison.OrdinalIgnoreCase);

        void MergeAdd(Dictionary<string, double> dst, Dictionary<string, double> src)
        {
            foreach (var (k, v) in src)
            {
                if (skipPriceAttributes && IsPriceAttr(k)) continue;
                double next = dst.GetValueOrDefault(k, 0.0) + v;
                if (Math.Abs(next) < 1e-9) dst.Remove(k);
                else dst[k] = next;
            }
        }
        void MergeMult(Dictionary<string, double> dst, Dictionary<string, double> src)
        {
            foreach (var (k, v) in src)
            {
                if (skipPriceAttributes && IsPriceAttr(k)) continue;
                double next = dst.GetValueOrDefault(k, 1.0) * v;
                if (Math.Abs(next - 1.0) < 1e-9) dst.Remove(k);
                else dst[k] = next;
            }
        }

        MergeAdd(PermanentAdd, consumed.PermanentAdd);
        MergeMult(PermanentMult, consumed.PermanentMult);
        MergeAdd(PermanentAdditiveMult, consumed.PermanentAdditiveMult);
        MergeAdd(DayAdd, consumed.DayAdd);
        MergeMult(DayMult, consumed.DayMult);
        MergeAdd(DayAdditiveMult, consumed.DayAdditiveMult);
        MergeAdd(HourAdd, consumed.HourAdd);
        MergeMult(HourMult, consumed.HourMult);
        MergeAdd(HourAdditiveMult, consumed.HourAdditiveMult);

        foreach (var t in consumed.ExtraTags) { PermanentRemovedTags.Remove(t); ExtraTags.Add(t); }
        foreach (var t in consumed.DayTags) { DayRemovedTags.Remove(t); DayTags.Add(t); }
        foreach (var t in consumed.HourTags) { HourRemovedTags.Remove(t); HourTags.Add(t); }

        RebuildTierAndEnchantmentData();
        if (CombatSourceCard == null)
            RefillAmmoToMax();
        MarkAttributeDirty();
    }

    public double EffectiveCooldownMs => GetAttribute("CooldownMax");

    public bool HasActiveCooldown
        => Template.Type == "Item"
        && EffectiveCooldownMs > 0
        && GetAttribute("CooldownDisabled") <= 0
        && !IsDisabled
        && !IsDestroyed;

    public int GetBuyPrice(StandardPricesSettings prices)
    {
        var custom = SafeMath.ToSafeInt(GetAttribute("BuyPrice"), 0, SafeMath.DefaultCombatMax);
        if (custom > 0) return custom;
        return Template.Type == "Skill"
            ? prices.GetSkillBuyPrice(Tier)
            : prices.GetItemBuyPrice(Template.CardSize, Tier);
    }

    public int GetSellPrice(StandardPricesSettings prices)
        => SafeMath.ToSafeInt(GetAttribute("SellPrice", prices), 0, SafeMath.DefaultCombatMax);

    private static double ApplyScalar(double cur, string op, double val) => op switch
    {
        "Subtract" => cur - val,
        "Multiply" => cur * val,
        "AdditiveMultiply" => cur * (1.0 + val),
        "Set" => val,
        _ => cur + val
    };

    /// <summary>
    /// P0-04：唯一权威非战斗状态深拷贝，完整保留 Permanent/Day/Hour 三层属性与附魔、槽位、价格上下文、标签与任务进度，
    /// 不携带单场战斗临时状态，并仅执行一次 RebuildTierAndEnchantmentData 与 RefillAmmoToMax。
    /// </summary>
    public CardInstance CloneNonCombatState(string? instanceIdOverride = null)
    {
        var effectiveEnch = HourEnchantment ?? DayEnchantment ?? PermanentEnchantment;
        var copy = new CardInstance(Template, PermanentTier, effectiveEnch, instanceIdOverride ?? InstanceId, skipInitialRebuild: true)
        {
            StartSlot = StartSlot,
            PermanentEnchantment = PermanentEnchantment,
            DayEnchantment = DayEnchantment,
            HourEnchantment = HourEnchantment,
            TransformReplacementTemplateId = TransformReplacementTemplateId,
            TransformedFromInstanceId = TransformedFromInstanceId,
            AttachedStandardPrices = AttachedStandardPrices
        };
        if (PermanentAdd.Count > 0)
            foreach (var (k, v) in PermanentAdd) copy.PermanentAdd[k] = v;
        if (PermanentMult.Count > 0)
            foreach (var (k, v) in PermanentMult) copy.PermanentMult[k] = v;
        if (PermanentAdditiveMult.Count > 0)
            foreach (var (k, v) in PermanentAdditiveMult) copy.PermanentAdditiveMult[k] = v;
        if (HourAdd.Count > 0)
            foreach (var (k, v) in HourAdd) copy.HourAdd[k] = v;
        if (HourMult.Count > 0)
            foreach (var (k, v) in HourMult) copy.HourMult[k] = v;
        if (HourAdditiveMult.Count > 0)
            foreach (var (k, v) in HourAdditiveMult) copy.HourAdditiveMult[k] = v;
        if (DayAdd.Count > 0)
            foreach (var (k, v) in DayAdd) copy.DayAdd[k] = v;
        if (DayMult.Count > 0)
            foreach (var (k, v) in DayMult) copy.DayMult[k] = v;
        if (DayAdditiveMult.Count > 0)
            foreach (var (k, v) in DayAdditiveMult) copy.DayAdditiveMult[k] = v;
        if (ExtraTags.Count > 0)
            foreach (var t in ExtraTags) copy.ExtraTags.Add(t);
        if (PermanentRemovedTags.Count > 0)
            foreach (var t in PermanentRemovedTags) copy.PermanentRemovedTags.Add(t);
        if (DayTags.Count > 0)
            foreach (var t in DayTags) copy.DayTags.Add(t);
        if (DayRemovedTags.Count > 0)
            foreach (var t in DayRemovedTags) copy.DayRemovedTags.Add(t);
        if (HourTags.Count > 0)
            foreach (var t in HourTags) copy.HourTags.Add(t);
        if (HourRemovedTags.Count > 0)
            foreach (var t in HourRemovedTags) copy.HourRemovedTags.Add(t);
        if (QuestProgress.Count > 0)
            foreach (var (k, v) in QuestProgress) copy.QuestProgress[k] = v;
        if (CompletedQuests.Count > 0)
            foreach (var c in CompletedQuests) copy.CompletedQuests.Add(c);
        copy.WasTransformed = WasTransformed;

        copy.RebuildTierAndEnchantmentData();
        copy.RefillAmmoToMax();
        return copy;
    }

    public CardInstance CloneForCombat() => CloneForCombatInternal(resetImmediately: true);

    internal CardInstance CloneForCombatInternal(bool resetImmediately)
    {
        var effectiveEnch = HourEnchantment ?? DayEnchantment ?? PermanentEnchantment;
        var copy = new CardInstance(Template, PermanentTier, effectiveEnch, InstanceId, skipInitialRebuild: true)
        {
            StartSlot = StartSlot,
            PermanentEnchantment = PermanentEnchantment,
            DayEnchantment = DayEnchantment,
            HourEnchantment = HourEnchantment,
            TransformReplacementTemplateId = TransformReplacementTemplateId,
            TransformedFromInstanceId = TransformedFromInstanceId,
            WasTransformed = WasTransformed,
            CombatSourceCard = this,
            AttachedStandardPrices = AttachedStandardPrices
        };
        if (PermanentAdd.Count > 0)
            foreach (var (k, v) in PermanentAdd) copy.PermanentAdd[k] = v;
        if (PermanentMult.Count > 0)
            foreach (var (k, v) in PermanentMult) copy.PermanentMult[k] = v;
        if (PermanentAdditiveMult.Count > 0)
            foreach (var (k, v) in PermanentAdditiveMult) copy.PermanentAdditiveMult[k] = v;
        if (HourAdd.Count > 0)
            foreach (var (k, v) in HourAdd) copy.HourAdd[k] = v;
        if (HourMult.Count > 0)
            foreach (var (k, v) in HourMult) copy.HourMult[k] = v;
        if (HourAdditiveMult.Count > 0)
            foreach (var (k, v) in HourAdditiveMult) copy.HourAdditiveMult[k] = v;
        if (DayAdd.Count > 0)
            foreach (var (k, v) in DayAdd) copy.DayAdd[k] = v;
        if (DayMult.Count > 0)
            foreach (var (k, v) in DayMult) copy.DayMult[k] = v;
        if (DayAdditiveMult.Count > 0)
            foreach (var (k, v) in DayAdditiveMult) copy.DayAdditiveMult[k] = v;
        if (ExtraTags.Count > 0)
            foreach (var t in ExtraTags) copy.ExtraTags.Add(t);
        if (PermanentRemovedTags.Count > 0)
            foreach (var t in PermanentRemovedTags) copy.PermanentRemovedTags.Add(t);
        if (DayTags.Count > 0)
            foreach (var t in DayTags) copy.DayTags.Add(t);
        if (DayRemovedTags.Count > 0)
            foreach (var t in DayRemovedTags) copy.DayRemovedTags.Add(t);
        if (HourTags.Count > 0)
            foreach (var t in HourTags) copy.HourTags.Add(t);
        if (HourRemovedTags.Count > 0)
            foreach (var t in HourRemovedTags) copy.HourRemovedTags.Add(t);
        if (QuestProgress.Count > 0)
            foreach (var (k, v) in QuestProgress) copy.QuestProgress[k] = v;
        if (CompletedQuests.Count > 0)
            foreach (var c in CompletedQuests) copy.CompletedQuests.Add(c);

        // 跳过构造期重复重建，仅在此处一次性 Rebuild（并按需执行 ResetForCombat）
        copy.RebuildTierAndEnchantmentData();
        if (resetImmediately)
            copy.ResetForCombat(null);
        return copy;
    }
}
