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

    /// <summary>由 PlayerState.CloneForCombat() 记录的同 InstanceId 源卡引用。</summary>
    public CardInstance? CombatSourceCard { get; internal set; }

    /// <summary>所在区域槽位起始下标（0..9）；未上阵则为 -1。</summary>
    public int StartSlot { get; set; } = -1;
    /// <summary>最左槽位编号（SocketEffects/Item 与官方 LeftSocketId 口径对齐）。</summary>
    public int LeftSocketId => StartSlot;
    /// <summary>当前卡牌是否处于 Tiny 缩小状态（Tiny 属性 &gt; 0 或携带 Tiny 标签）。</summary>
    public bool IsTiny => MayHaveTiny() && GetAttribute("Tiny") > 0;

    private bool MayHaveTiny()
        => (PermanentAdd.Count > 0 && PermanentAdd.ContainsKey("Tiny"))
        || (CombatAdd.Count > 0 && CombatAdd.ContainsKey("Tiny"))
        || (AuraAdd.Count > 0 && AuraAdd.ContainsKey("Tiny"))
        || (HourAdd.Count > 0 && HourAdd.ContainsKey("Tiny"))
        || (DayAdd.Count > 0 && DayAdd.ContainsKey("Tiny"))
        || (BaseAttributes.Count > 0 && BaseAttributes.ContainsKey("Tiny"))
        || (ExtraTags.Count > 0 && ExtraTags.Contains("Tiny"))
        || (CombatTags.Count > 0 && CombatTags.Contains("Tiny"))
        || (AuraTags.Count > 0 && AuraTags.Contains("Tiny"));
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
    /// <summary>卡牌当前占据的棋盘物理槽位数（Tiny 时 Large=2，Medium/Small=1）。</summary>
    public int SlotWidth => IsTiny
        ? (Template.CardSize == CardSize.Large ? 2 : 1)
        : Template.CardSize.SlotCount();
    internal StandardPricesSettings? AttachedStandardPrices { get; set; }
    private static readonly StandardPricesSettings DefaultStandardPrices = new();

    /// <summary>基础属性（来自当前 Tier 合并 + 附魔基础属性）。</summary>
    public Dictionary<string, double> BaseAttributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>局内永久属性修正（商店/事件/卖牌/永久成长）。</summary>
    public Dictionary<string, double> PermanentAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PermanentMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>本小时临时属性修正（UntilEndOfHour）。</summary>
    public Dictionary<string, double> HourAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> HourMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当日临时属性修正（UntilEndOfDay）。</summary>
    public Dictionary<string, double> DayAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> DayMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>单场战斗临时属性修正（UntilEndOfCombat / TCombatDuration）。</summary>
    public Dictionary<string, double> CombatAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> CombatMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>光环动态加成（由 AuraEngine 刷新重写）。</summary>
    public Dictionary<string, double> AuraAdd { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> AuraMult { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>动态追加标签（永久 ExtraTags / 战斗临时 CombatTags / 光环 AuraTags）。</summary>
    public HashSet<string> ExtraTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CombatTags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AuraTags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Quest 进度（Quest_1..Quest_7）与已解锁能力集合。</summary>
    public Dictionary<string, double> QuestProgress { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CompletedQuests { get; } = new(StringComparer.OrdinalIgnoreCase);

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
    public int TimesFiredThisFight { get; set; }
    public bool HasFiredOnce => TimesFiredThisFight > 0;

    private static readonly CardTier[] AllCardTiers = Enum.GetValues<CardTier>();

    /// <summary>单调递增的属性与光环变更版本号，供战斗快照复用无变动属性以消除热路径重复查表。</summary>
    internal int AttributeVersion { get; private set; }

    internal void MarkAttributeDirty() => AttributeVersion++;

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

    public bool HasTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;
        if (ListContainsIgnoreCase(Template.Tags, tag)) return true;
        if ((ExtraTags.Count > 0 && ExtraTags.Contains(tag))
            || (CombatTags.Count > 0 && CombatTags.Contains(tag))
            || (AuraTags.Count > 0 && AuraTags.Contains(tag))
            || (_questRewardTags.Count > 0 && _questRewardTags.Contains(tag)))
            return true;
        if (!string.IsNullOrEmpty(Enchantment) &&
            Template.Enchantments != null &&
            Template.Enchantments.TryGetValue(Enchantment, out var ench) &&
            ListContainsIgnoreCase(ench.Tags, tag))
            return true;
        if (tag.Equals("Tiny", StringComparison.OrdinalIgnoreCase) && IsTiny)
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

    private bool HasBaseOrStaticTag(string tag, EnchantmentData? ench)
        => ListContainsIgnoreCase(Template.Tags, tag)
        || (ExtraTags.Count > 0 && ExtraTags.Contains(tag))
        || (CombatTags.Count > 0 && CombatTags.Contains(tag))
        || (AuraTags.Count > 0 && AuraTags.Contains(tag))
        || (_questRewardTags.Count > 0 && _questRewardTags.Contains(tag))
        || (ench != null && ListContainsIgnoreCase(ench.Tags, tag));

    public IEnumerable<string> GetAllTags()
    {
        for (int i = 0; i < Template.Tags.Count; i++) yield return Template.Tags[i];
        if (ExtraTags.Count > 0)
        {
            foreach (var t in ExtraTags)
                if (!ListContainsIgnoreCase(Template.Tags, t)) yield return t;
        }
        if (CombatTags.Count > 0)
        {
            foreach (var t in CombatTags)
                if (!ListContainsIgnoreCase(Template.Tags, t) && !ExtraTags.Contains(t)) yield return t;
        }
        if (AuraTags.Count > 0)
        {
            foreach (var t in AuraTags)
                if (!ListContainsIgnoreCase(Template.Tags, t) && !ExtraTags.Contains(t) && !CombatTags.Contains(t)) yield return t;
        }
        if (_questRewardTags.Count > 0)
        {
            foreach (var t in _questRewardTags)
                if (!ListContainsIgnoreCase(Template.Tags, t) && !ExtraTags.Contains(t) && !CombatTags.Contains(t) && !AuraTags.Contains(t)) yield return t;
        }

        EnchantmentData? ench = null;
        if (!string.IsNullOrEmpty(Enchantment) &&
            Template.Enchantments != null &&
            Template.Enchantments.TryGetValue(Enchantment, out ench))
        {
            for (int i = 0; i < ench.Tags.Count; i++)
            {
                var t = ench.Tags[i];
                if (!ListContainsIgnoreCase(Template.Tags, t) && !ExtraTags.Contains(t) && !CombatTags.Contains(t) && !AuraTags.Contains(t) && !_questRewardTags.Contains(t))
                    yield return t;
            }
        }
        if (IsTiny && !HasBaseOrStaticTag("Tiny", ench))
            yield return "Tiny";
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

    public bool CanUpgrade() => Tier < CardTier.Diamond && Template.Tiers != null && Template.Tiers.ContainsKey(Tier.NextTier().ToString());

    public bool TryUpgrade() => TryUpgrade(isTemporary: false);

    public bool TryUpgrade(bool isTemporary)
    {
        if (!CanUpgrade()) return false;
        Tier = Tier.NextTier();
        if (!isTemporary) PermanentTier = Tier;
        RebuildTierAndEnchantmentData();
        if (!isTemporary && CombatSourceCard == null)
            RefillAmmoToMax();
        return true;
    }

    public void ApplyEnchantment(string? enchantment, bool preventOverride = false)
        => ApplyEnchantment(enchantment, preventOverride, DurationTarget.Permanent);

    public void ApplyEnchantment(string? enchantment, bool preventOverride, bool isTemporary)
        => ApplyEnchantment(enchantment, preventOverride, isTemporary ? DurationTarget.Combat : DurationTarget.Permanent);

    /// <summary>按持续时间层级施加附魔（支持 Permanent / Day / Hour / Combat 临时附魔；更高持久层覆盖时自动清理已被顶替的旧短时附魔）。</summary>
    public void ApplyEnchantment(string? enchantment, bool preventOverride, DurationTarget duration)
    {
        if (string.IsNullOrEmpty(enchantment)) return;
        if (preventOverride && !string.IsNullOrEmpty(Enchantment)) return;
        Enchantment = enchantment;
        switch (duration)
        {
            case DurationTarget.Permanent:
                PermanentEnchantment = enchantment;
                // 永久附魔生效时清除已被覆盖的旧 Hour/Day 附魔，防止后续过小时/过天时旧临时附魔顶掉新永久附魔
                DayEnchantment = null;
                HourEnchantment = null;
                break;
            case DurationTarget.Day:
                DayEnchantment = enchantment;
                HourEnchantment = null;
                break;
            case DurationTarget.Hour:
                HourEnchantment = enchantment;
                break;
        }
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
    }

    public void RebuildTierAndEnchantmentData()
    {
        AttributeVersion++;
        _cachedDynamicEffectMask = -1;
        BaseAttributes.Clear();
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

        // 叠加附魔
        if (!string.IsNullOrEmpty(Enchantment) &&
            Template.Enchantments != null &&
            Template.Enchantments.TryGetValue(Enchantment, out var ench))
        {
            foreach (var (k, v) in ench.Attributes)
                BaseAttributes[k] = BaseAttributes.GetValueOrDefault(k, 0) + v;
            foreach (var ab in ench.Abilities.Values)
                ActiveAbilities.Add(ab);
            foreach (var au in ench.Auras.Values)
                ActiveAuras.Add(au);
        }

        // 叠加已完成 Quest 奖励（兼容直接 Attributes 与按 Tier 缩放的 Reward.Tiers）
        if (Template.Quests != null && CompletedQuests.Count > 0)
        {
            for (int qi = 0; qi < Template.Quests.Count; qi++)
            {
                var q = Template.Quests[qi];
                for (int ei = 0; ei < q.Entries.Count; ei++)
                {
                    var entry = q.Entries[ei];
                    if (!CompletedQuests.Contains(entry.AttributeType) || entry.Reward == null) continue;
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

    public double GetAttribute(string attr) => GetAttribute(attr, AttachedStandardPrices);

    public double GetAttribute(string attr, StandardPricesSettings? prices)
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
        }
        else if (c0 is 'T' or 't')
        {
            if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
            {
                var scalar = Math.Max(0, GetRawScalar("Tiny"));
                if (scalar > 0) return scalar;
                return (ExtraTags.Contains("Tiny") || CombatTags.Contains("Tiny") || AuraTags.Contains("Tiny"))
                    ? 1.0
                    : 0.0;
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

        double baseVal;
        if (c0 is 'S' or 's'
            && attr.Equals("SellPrice", StringComparison.OrdinalIgnoreCase)
            && !BaseAttributes.ContainsKey("SellPrice")
            && Template.Type is "Item" or "Skill")
        {
            var effectivePrices = prices ?? DefaultStandardPrices;
            baseVal = Template.Type == "Skill"
                ? effectivePrices.GetSkillSellPrice(Tier)
                : effectivePrices.GetItemSellPrice(Template.CardSize, Tier);
        }
        else
        {
            baseVal = BaseAttributes.Count > 0 && BaseAttributes.TryGetValue(attr, out var bv) ? bv : 0;
        }

        var sum = baseVal;
        if (PermanentAdd.Count > 0 && PermanentAdd.TryGetValue(attr, out var pa)) sum += pa;
        if (HourAdd.Count > 0 && HourAdd.TryGetValue(attr, out var ha)) sum += ha;
        if (DayAdd.Count > 0 && DayAdd.TryGetValue(attr, out var da)) sum += da;
        if (CombatAdd.Count > 0 && CombatAdd.TryGetValue(attr, out var ca)) sum += ca;
        if (AuraAdd.Count > 0 && AuraAdd.TryGetValue(attr, out var aa)) sum += aa;

        // 若采用乘法直乘（如 Golden 双倍价值）
        double totalMult = 1.0;
        if (PermanentMult.Count > 0 && PermanentMult.TryGetValue(attr, out var pm)) totalMult *= pm;
        if (HourMult.Count > 0 && HourMult.TryGetValue(attr, out var hm)) totalMult *= hm;
        if (DayMult.Count > 0 && DayMult.TryGetValue(attr, out var dm)) totalMult *= dm;
        if (CombatMult.Count > 0 && CombatMult.TryGetValue(attr, out var cm)) totalMult *= cm;
        if (AuraMult.Count > 0 && AuraMult.TryGetValue(attr, out var am)) totalMult *= am;

        var value = sum * totalMult;

        if (c0 is 'H' or 'h' && Heated && value <= 0 && attr.Equals("Heated", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (c0 is 'C' or 'c')
        {
            if (Chilled && value <= 0 && attr.Equals("Chilled", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (value > 0 && attr.Equals("CooldownMax", StringComparison.OrdinalIgnoreCase))
            {
                var flatRed = GetAttributeRaw("FlatCooldownReduction");
                var pctRed = Math.Clamp(GetAttributeRaw("PercentCooldownReduction"), -200, 90);
                value = Math.Max(1000, (value - flatRed) * (1.0 - pctRed / 100.0));
            }
        }
        else if (c0 is 'T' or 't' && value > 0 && attr.Equals("TempoCost", StringComparison.OrdinalIgnoreCase))
        {
            // 对齐 CooldownMax 减免公式，应用平摊与百分比节奏消耗减免（如 Death Metal Drum Kit 的闪亮附魔）
            var flatRed = GetAttributeRaw("FlatTempoCostReduction");
            var pctRed = Math.Clamp(GetAttributeRaw("PercentTempoCostReduction"), -200, 100);
            value = Math.Max(0, Math.Round((value - flatRed) * (1.0 - pctRed / 100.0)));
        }

        return value;
    }

    private double GetRawScalar(string attr) => GetAttributeRaw(attr);

    private double GetAttributeRaw(string attr)
    {
        double sum = BaseAttributes.Count > 0 ? BaseAttributes.GetValueOrDefault(attr, 0) : 0;
        if (PermanentAdd.Count > 0 && PermanentAdd.TryGetValue(attr, out var pa)) sum += pa;
        if (HourAdd.Count > 0 && HourAdd.TryGetValue(attr, out var ha)) sum += ha;
        if (DayAdd.Count > 0 && DayAdd.TryGetValue(attr, out var da)) sum += da;
        if (CombatAdd.Count > 0 && CombatAdd.TryGetValue(attr, out var ca)) sum += ca;
        if (AuraAdd.Count > 0 && AuraAdd.TryGetValue(attr, out var aa)) sum += aa;
        return sum;
    }

    public void ModifyAttribute(string attr, string operation, double value, bool permanent)
        => ModifyAttribute(attr, operation, value, permanent ? DurationTarget.Permanent : DurationTarget.Combat);

    public void ModifyAttribute(string attr, string operation, double value, DurationTarget target)
    {
        AttributeVersion++;
        if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase))
        {
            var ammoMax = EffectiveAmmoMax;
            CurrentAmmo = Math.Clamp((int)Math.Round(ApplyScalar(CurrentAmmo, operation, value)), 0, ammoMax);
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
            int nextMs = Math.Max(0, (int)Math.Round(ApplyScalar(curMs, operation, value)));
            if (attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase)) FreezeMs = nextMs;
            else if (attr.Equals("Slow", StringComparison.OrdinalIgnoreCase)) SlowMs = nextMs;
            else HasteMs = nextMs;

            CombatAdd.Remove(attr);
            CombatMult.Remove(attr);
            if (nextMs <= 0)
            {
                PermanentAdd.Remove(attr);
                PermanentMult.Remove(attr);
                HourAdd.Remove(attr);
                HourMult.Remove(attr);
                DayAdd.Remove(attr);
                DayMult.Remove(attr);
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
                PermanentAdd.Remove(attr); PermanentMult.Remove(attr);
                CombatAdd.Remove(attr); CombatMult.Remove(attr);
                HourAdd.Remove(attr); HourMult.Remove(attr);
                DayAdd.Remove(attr); DayMult.Remove(attr);
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

        // Tiny 属性受 ExtraTags / CombatTags 驱动，需与标量修改同步增删标签
        if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
        {
            double curTiny = GetAttribute("Tiny");
            double nextTiny = ApplyScalar(curTiny, operation, value);
            if (nextTiny > 0)
            {
                if (target == DurationTarget.Permanent) ExtraTags.Add("Tiny");
                else CombatTags.Add("Tiny");
            }
            else
            {
                ExtraTags.Remove("Tiny");
                CombatTags.Remove("Tiny");
                AuraTags.Remove("Tiny");
            }
            return;
        }

        var (addDict, multDict) = target switch
        {
            DurationTarget.Permanent => (PermanentAdd, PermanentMult),
            DurationTarget.Hour => (HourAdd, HourMult),
            DurationTarget.Day => (DayAdd, DayMult),
            _ => (CombatAdd, CombatMult)
        };

        switch (operation)
        {
            case "Subtract":
                addDict[attr] = addDict.GetValueOrDefault(attr, 0) - value;
                break;
            case "Multiply":
                multDict[attr] = multDict.GetValueOrDefault(attr, 1.0) * value;
                break;
            case "AdditiveMultiply":
                // 如 +50%（传入 0.5 或 50？DB 里通常存小数如 1.0 代表 +100%，这里稳妥按增量系数处理）
                multDict[attr] = multDict.GetValueOrDefault(attr, 1.0) + value;
                break;
            case "Set":
            {
                multDict.Remove(attr);
                // 计算其余有效乘区并在赋值时精确抵消，避免 PermanentMult / AuraMult 等放大或歪曲 Set 目标值
                double otherMult = 1.0;
                if (!ReferenceEquals(multDict, PermanentMult) && PermanentMult.TryGetValue(attr, out var pm)) otherMult *= pm;
                if (!ReferenceEquals(multDict, HourMult) && HourMult.TryGetValue(attr, out var hm)) otherMult *= hm;
                if (!ReferenceEquals(multDict, DayMult) && DayMult.TryGetValue(attr, out var dm)) otherMult *= dm;
                if (!ReferenceEquals(multDict, CombatMult) && CombatMult.TryGetValue(attr, out var cm)) otherMult *= cm;
                if (AuraMult.TryGetValue(attr, out var am)) otherMult *= am;

                if (Math.Abs(otherMult) < 1e-9)
                {
                    // 若其他层存在乘 0 锁死导致无法通过加法还原目标值，清理对应零乘区
                    if (PermanentMult.TryGetValue(attr, out var pm0) && Math.Abs(pm0) < 1e-9) PermanentMult.Remove(attr);
                    if (HourMult.TryGetValue(attr, out var hm0) && Math.Abs(hm0) < 1e-9) HourMult.Remove(attr);
                    if (DayMult.TryGetValue(attr, out var dm0) && Math.Abs(dm0) < 1e-9) DayMult.Remove(attr);
                    if (CombatMult.TryGetValue(attr, out var cm0) && Math.Abs(cm0) < 1e-9) CombatMult.Remove(attr);
                    if (AuraMult.TryGetValue(attr, out var am0) && Math.Abs(am0) < 1e-9) AuraMult.Remove(attr);
                    otherMult = 1.0;
                }

                double otherLayers = GetAttributeRaw(attr) - addDict.GetValueOrDefault(attr, 0);
                addDict[attr] = (value / otherMult) - otherLayers;
                break;
            }
            default: // Add
                addDict[attr] = addDict.GetValueOrDefault(attr, 0) + value;
                break;
        }

        if ((attr.Equals("Heated", StringComparison.OrdinalIgnoreCase) || attr.Equals("Chilled", StringComparison.OrdinalIgnoreCase))
            && ((string.Equals(operation, "Multiply", StringComparison.OrdinalIgnoreCase) && Math.Abs(value) < 1e-9)
                || (string.Equals(operation, "Set", StringComparison.OrdinalIgnoreCase) && value <= 0)))
        {
            if (attr.Equals("Heated", StringComparison.OrdinalIgnoreCase)) Heated = false;
            else Chilled = false;
        }

        if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
            SyncTinyTag(target, addDict);
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

    private void SyncTinyTag(DurationTarget target, Dictionary<string, double> addDict)
    {
        bool isPositive = addDict.GetValueOrDefault("Tiny", 0) > 0;
        if (target == DurationTarget.Permanent)
        {
            if (isPositive) ExtraTags.Add("Tiny");
            else ExtraTags.Remove("Tiny");
        }
        else if (target == DurationTarget.Combat)
        {
            if (isPositive) CombatTags.Add("Tiny");
            else CombatTags.Remove("Tiny");
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
                AuraMult[attr] = AuraMult.GetValueOrDefault(attr, 1.0) + value;
                break;
            default:
                AuraAdd[attr] = AuraAdd.GetValueOrDefault(attr, 0) + value;
                break;
        }

        if (attr.Equals("Tiny", StringComparison.OrdinalIgnoreCase))
        {
            if (AuraAdd.GetValueOrDefault("Tiny", 0) > 0) AuraTags.Add("Tiny");
            else AuraTags.Remove("Tiny");
        }
    }

    public void ClearAuraModifiers()
    {
        if (AuraAdd.Count > 0 || AuraMult.Count > 0 || AuraTags.Count > 0)
            AttributeVersion++;
        AuraAdd.Clear();
        AuraMult.Clear();
        AuraTags.Clear();
    }

    public void ClearEndOfHourModifiers()
    {
        if (HourAdd.Count > 0 || HourMult.Count > 0)
            AttributeVersion++;
        HourAdd.Clear();
        HourMult.Clear();
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
        if (DayAdd.Count > 0 || DayMult.Count > 0)
            AttributeVersion++;
        DayAdd.Clear();
        DayMult.Clear();
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
            int max = Math.Max(0, (int)Math.Round(GetAttribute("AmmoMax")));
            if (max == 0 && BaseAttributes.TryGetValue("Ammo", out var ammo))
                max = Math.Max(0, (int)Math.Round(ammo));
            return max;
        }
    }

    /// <summary>将当前弹药补满为最新有效弹药上限（供非战斗升阶及脱战复位调用）。</summary>
    public void RefillAmmoToMax() => CurrentAmmo = EffectiveAmmoMax;

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
        if (needRebuild) RebuildTierAndEnchantmentData();

        CombatAdd.Clear();
        CombatMult.Clear();
        CombatTags.Clear();
        CooldownProgressMs = 0;
        HasteMs = 0;
        SlowMs = 0;
        FreezeMs = 0;
        IsFlying = false;
        Heated = false;
        Chilled = false;
        IsDisabled = false;
        IsDestroyed = false;
        TimesFiredThisFight = 0;
        RefillAmmoToMax();
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
        var custom = (int)Math.Round(GetAttribute("BuyPrice"));
        if (custom > 0) return custom;
        return Template.Type == "Skill"
            ? prices.GetSkillBuyPrice(Tier)
            : prices.GetItemBuyPrice(Template.CardSize, Tier);
    }

    public int GetSellPrice(StandardPricesSettings prices)
        => Math.Max(0, (int)Math.Round(GetAttribute("SellPrice", prices)));

    private static double ApplyScalar(double cur, string op, double val) => op switch
    {
        "Subtract" => cur - val,
        "Multiply" => cur * val,
        "AdditiveMultiply" => cur * (1.0 + val),
        "Set" => val,
        _ => cur + val
    };

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
            CombatSourceCard = this,
            AttachedStandardPrices = AttachedStandardPrices
        };
        if (PermanentAdd.Count > 0)
            foreach (var (k, v) in PermanentAdd) copy.PermanentAdd[k] = v;
        if (PermanentMult.Count > 0)
            foreach (var (k, v) in PermanentMult) copy.PermanentMult[k] = v;
        if (HourAdd.Count > 0)
            foreach (var (k, v) in HourAdd) copy.HourAdd[k] = v;
        if (HourMult.Count > 0)
            foreach (var (k, v) in HourMult) copy.HourMult[k] = v;
        if (DayAdd.Count > 0)
            foreach (var (k, v) in DayAdd) copy.DayAdd[k] = v;
        if (DayMult.Count > 0)
            foreach (var (k, v) in DayMult) copy.DayMult[k] = v;
        if (ExtraTags.Count > 0)
            foreach (var t in ExtraTags) copy.ExtraTags.Add(t);
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
