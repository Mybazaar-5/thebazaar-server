using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Cards.Encounter.Combat;
using BazaarGameShared.Domain.Cards.Encounter.Event;
using BazaarGameShared.Domain.Cards.Encounter.Pedestal;
using BazaarGameShared.Domain.Cards.Encounter.PVP;
using BazaarGameShared.Domain.Cards.Encounter.Step;
using BazaarGameShared.Domain.Cards.Enchantments;
using BazaarGameShared.Domain.Cards.Interfaces;
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Cards.PlayerEffects;
using BazaarGameShared.Domain.Cards.Skill;
using BazaarGameShared.Domain.Cards.Socket;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Domain.Effect.Actions;
using BazaarGameShared.Domain.Effect.Trigger;
using BazaarGameShared.Domain.Events;
using BazaarGameShared.Domain.Game;
using BazaarGameShared.Domain.Players;
using BazaarGameShared.Domain.Prerequisites;
using BazaarGameShared.Domain.Prerequisites.Conditionals;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Domain.Targeting;
using BazaarGameShared.Domain.Values;
using BazaarGameShared.Infra.Serialization;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace BazaarCore.Tests;

/// <summary>
/// 官方 BazaarGameShared.dll 差分 Oracle 适配器：
/// 将本地 (PlayerState self, PlayerState? opp, RuleExecutionContext ctx) 零失真映射为官方
/// IRun / IPlayer / IPlayerInventory / ICard / ISeedManager / IRunState / ITriggerEvent，
/// 用于直接调用官方 DLL 的 TTargetBase.GetTargets / ITCardConditional / ITPlayerConditional /
/// ITRunConditional / ITPrerequisite / ITValue 进行行为对拍。
/// </summary>
public sealed class OfficialSharedOracle
{
    public static readonly JsonSerializerSettings OfficialJsonSettings;

    private static readonly JsonSerializerOptions RuleNodeToJsonOptions = new(GameDatabaseLoader.JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ConditionalWeakTable<AbilityDefinition, TCardAbility> AbilityCache = new();
    private static readonly ConcurrentDictionary<string, ITCard> DbTemplateCache = new(StringComparer.OrdinalIgnoreCase);
    private static bool _dbTemplatesLoaded;
    private static readonly object DbLoadLock = new();

    static OfficialSharedOracle()
    {
        // 先行触发 BazaarGameShared.dll 程序集加载，保证 BazaarJsonDerivedTypeConverter 静态构造能索引全部派生类型
        _ = typeof(TCardBase).Assembly;
        _ = typeof(TCardItem).Assembly;
        OfficialJsonSettings = new BazaarJsonSerializerSettings(validate: false);
    }

    public OracleRun Run { get; }
    public OraclePlayer LocalPlayer { get; }
    public OraclePlayer? OpponentPlayer { get; }
    public OracleCard? SelfCard { get; }
    public OracleTriggerEvent? TriggerEvent { get; }

    private readonly StandardPricesSettings? _prices;

    public OfficialSharedOracle(
        PlayerState self,
        PlayerState? opp,
        RuleExecutionContext ctx,
        bool includeDeltaEvent = false,
        bool isSandstormActive = false,
        IEnumerable<Guid>? visitedEncounters = null,
        Guid? gameModeIdOverride = null)
    {
        _prices = ctx.Repo?.GameMode.StandardPrices;
        var prices = _prices;
        var cardMap = new Dictionary<CardInstance, OracleCard>(ReferenceEqualityComparer.Instance);

        var localState = TargetResolver.GetLocalPlayer(ctx);
        var oppState = ReferenceEquals(localState, self) ? opp : self;

        LocalPlayer = new OraclePlayer(localState, isLocalPlayer: true, prices, cardMap);
        OpponentPlayer = oppState != null
            ? new OraclePlayer(oppState, isLocalPlayer: false, prices, cardMap)
            : null;

        OracleCard? ResolveOrCreateCard(CardInstance? ci)
        {
            if (ci == null) return null;
            if (cardMap.TryGetValue(ci, out var existing))
                return existing;
            var ownerState = TargetResolver.FindOwner(ci, ctx);
            var ownerOracle = ReferenceEquals(ownerState, localState) ? LocalPlayer : (OpponentPlayer ?? LocalPlayer);
            return new OracleCard(ci, ownerOracle, ResolveSection(ci, ownerState), prices);
        }

        SelfCard = ResolveOrCreateCard(ctx.SelfCard);
        var srcCard = ResolveOrCreateCard(ctx.TriggerSourceCard);
        var tgtCard = ResolveOrCreateCard(ctx.TriggerTargetCard);

        if (srcCard != null || tgtCard != null || includeDeltaEvent || Math.Abs(ctx.LastAttributeChangeDelta) > 0)
        {
            int deltaInt = (int)Math.Round(ctx.LastAttributeChangeDelta, MidpointRounding.AwayFromZero);
            TriggerEvent = includeDeltaEvent || Math.Abs(ctx.LastAttributeChangeDelta) > 0
                ? new OracleDeltaTriggerEvent(srcCard, tgtCard, deltaInt)
                : new OracleTriggerEvent(srcCard, tgtCard);
        }

        OracleCard? currentEncounter = null;
        if (!string.IsNullOrEmpty(ctx.CurrentEncounterId) && ctx.Repo != null)
        {
            var encTpl = ctx.Repo.GetCard(ctx.CurrentEncounterId);
            if (encTpl != null)
                currentEncounter = new OracleCard(new CardInstance(encTpl), LocalPlayer, null, prices);
        }

        var selectionCards = new List<ICard>();
        if (ctx.SelectionCards != null)
        {
            foreach (var sc in ctx.SelectionCards)
            {
                var oc = ResolveOrCreateCard(sc);
                if (oc != null) selectionCards.Add(oc);
            }
        }

        var gmId = gameModeIdOverride
            ?? (Guid.TryParse(ctx.Repo?.GameMode.Id, out var parsedGm) ? parsedGm : Guid.Empty);
        var playMode = Enum.TryParse<EPlayMode>(ctx.PlayMode, true, out var pm) ? pm : EPlayMode.Unranked;

        bool sandstorm = isSandstormActive ||
                         (ctx.Scope == RuleScope.InCombat &&
                          ctx.Repo != null &&
                          ctx.TimeMs >= ctx.Repo.GameMode.Sandstorm.CountdownStartTime + ctx.Repo.GameMode.Sandstorm.CountdownDuration);

        var runState = new OracleRunState(selectionCards, currentEncounter);
        var seedManager = new OracleSeedManager(ctx.Rng);

        Run = new OracleRun(
            gmId,
            (uint)Math.Max(0, ctx.CurrentDay),
            (uint)Math.Max(0, ctx.CurrentHour),
            playMode,
            LocalPlayer,
            OpponentPlayer,
            seedManager,
            runState,
            sandstorm,
            visitedEncounters);
    }

    public TargetingContext CreateTargetingContext() => new(Run, SelfCard, TriggerEvent);
    public PrereqContext CreatePrereqContext() => new(Run, SelfCard!, TriggerEvent);
    public ValueContext CreateValueContext() => new(Run, SelfCard, TriggerEvent);
    public RunConditionalContext CreateRunConditionalContext() => new(Run, SelfCard, TriggerEvent);

    public CardConditionalContext CreateCardConditionalContext(IEnumerable<CardInstance> pool)
    {
        var list = new List<ICard>();
        foreach (var ci in pool)
        {
            list.Add(MapCard(ci));
        }
        return new CardConditionalContext(Run, list, SelfCard, TriggerEvent);
    }

    public PlayerConditionalContext CreatePlayerConditionalContext(IEnumerable<PlayerState> pool)
    {
        var set = new HashSet<IPlayer>();
        foreach (var p in pool)
        {
            set.Add(MapPlayer(p));
        }
        return new PlayerConditionalContext(Run, set, SelfCard, TriggerEvent);
    }

    public OracleCard MapCard(CardInstance ci)
    {
        if (LocalPlayer.TryGetMappedCard(ci, out var oc)) return oc;
        if (OpponentPlayer != null && OpponentPlayer.TryGetMappedCard(ci, out oc)) return oc;
        return new OracleCard(ci, LocalPlayer, null, _prices);
    }

    public OraclePlayer MapPlayer(PlayerState p)
    {
        if (ReferenceEquals(p, LocalPlayer.Source)) return LocalPlayer;
        if (OpponentPlayer != null && ReferenceEquals(p, OpponentPlayer.Source)) return OpponentPlayer;
        return LocalPlayer;
    }

    private static EInventorySection? ResolveSection(CardInstance ci, PlayerState owner)
    {
        if (owner.Hand.Cards.Contains(ci) || owner.SocketEffects.Contains(ci))
            return EInventorySection.Hand;
        if (owner.Stash.Cards.Contains(ci))
            return EInventorySection.Stash;
        return null;
    }

    // ------------------------------------------------------------------------
    // JSON 反序列化工具（从原始 JSON 或 RuleNode 转为官方 BazaarGameShared 对象）
    // ------------------------------------------------------------------------

    public static T DeserializeFromJson<T>(string json)
        => JsonConvert.DeserializeObject<T>(json, OfficialJsonSettings)
           ?? throw new InvalidOperationException($"官方 DLL 反序列化 {typeof(T).Name} 返回 null: {json}");

    public static T DeserializeFromRuleNode<T>(RuleNode node)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(node, RuleNodeToJsonOptions);
        return DeserializeFromJson<T>(json);
    }

    public static void EnsureDbTemplatesLoaded(string dbPath)
    {
        if (_dbTemplatesLoaded) return;
        lock (DbLoadLock)
        {
            if (_dbTemplatesLoaded) return;
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Data FROM cards";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var raw = reader.GetFieldValue<byte[]>(1);
                var json = Encoding.UTF8.GetString(raw);
                var card = JsonConvert.DeserializeObject<ITCard>(json, OfficialJsonSettings);
                if (card != null)
                    DbTemplateCache[id] = card;
            }
            _dbTemplatesLoaded = true;
        }
    }

    internal static ITCard ResolveOfficialTemplate(CardTemplate tpl)
    {
        if (DbTemplateCache.TryGetValue(tpl.Id, out var cached))
            return cached;

        if (string.Equals(tpl.TypeDiscriminator, "TCardMusicNoteSocketEffect", StringComparison.Ordinal)
            || tpl.MusicNote.HasValue)
        {
            var note = TryParseMusicNote(tpl.MusicNote, out var parsedNote) ? parsedNote : EMusicNote.A;
            return new TCardMusicNoteSocketEffect
            {
                Id = ParseOrDeterministicGuid(tpl.Id),
                InternalName = tpl.InternalName,
                Size = MapSize(tpl.CardSize),
                StartingTier = ParseTier(tpl.StartingTier),
                MusicNote = note
            };
        }

        var id = ParseOrDeterministicGuid(tpl.Id);
        var size = MapSize(tpl.CardSize);
        var tier = ParseTier(tpl.StartingTier);

        return tpl.Type switch
        {
            "Skill" => new TCardSkill { Id = id, InternalName = tpl.InternalName, Size = size, StartingTier = tier },
            "SocketEffect" or "MusicNoteSocketEffect" => new TCardSocketEffect { Id = id, InternalName = tpl.InternalName, Size = size, StartingTier = tier },
            "CombatEncounter" => new TCardEncounterCombat { Id = id, InternalName = tpl.InternalName, Size = size, StartingTier = tier },
            "EncounterStep" => new TCardEncounterStep { Id = id, InternalName = tpl.InternalName, Size = size, StartingTier = tier },
            "EventEncounter" => new TCardEncounterEvent { Id = id, InternalName = tpl.InternalName, Size = size, StartingTier = tier },
            "PedestalEncounter" => new TCardEncounterPedestal { Id = id, InternalName = tpl.InternalName, Size = size, StartingTier = tier },
            "PvpEncounter" => new TCardEncounterPvp { Id = id, InternalName = tpl.InternalName, Size = size, StartingTier = tier },
            "PlayerEffect" => new TCardPlayerEffect { Id = id, InternalName = tpl.InternalName, Size = size, StartingTier = tier },
            _ => new TCardItem { Id = id, InternalName = tpl.InternalName, Size = size, StartingTier = tier }
        };
    }

    internal static TCardAbility ConvertAbility(AbilityDefinition ab)
    {
        return AbilityCache.GetValue(ab, static def =>
        {
            TTriggerBase trigger = new TTriggerOnCardFired();
            if (def.Trigger != null)
            {
                try
                {
                    trigger = DeserializeFromRuleNode<TTriggerBase>(def.Trigger);
                }
                catch
                {
                    // 若测试构造了未覆盖的自定义触发器节点，回退默认触发器
                }
            }

            ITAction? action = null;
            if (def.Action != null)
            {
                try
                {
                    action = DeserializeFromRuleNode<ITAction>(def.Action);
                }
                catch
                {
                    // 忽略无法映射的合成动作节点
                }
            }

            return new TCardAbility
            {
                Id = def.Id,
                Trigger = trigger,
                Action = action!
            };
        });
    }

    internal static Guid ParseOrDeterministicGuid(string? id)
    {
        if (!string.IsNullOrEmpty(id) && Guid.TryParse(id, out var g))
            return g;
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(id ?? string.Empty));
        return new Guid(hash);
    }

    internal static bool TryParseMusicNote(JsonElement? el, out EMusicNote note)
    {
        note = EMusicNote.A;
        if (!el.HasValue) return false;
        if (el.Value.ValueKind == JsonValueKind.String)
        {
            var s = el.Value.GetString()?.Trim();
            if (!string.IsNullOrEmpty(s) && Enum.TryParse<EMusicNote>(s, true, out var parsed))
            {
                note = parsed;
                return true;
            }
        }
        else if (el.Value.ValueKind == JsonValueKind.Number && el.Value.TryGetInt32(out int num) && num is >= 0 and < 7)
        {
            note = (EMusicNote)num;
            return true;
        }
        return false;
    }

    internal static ECardSize MapSize(CardSize size) => size switch
    {
        CardSize.Small => ECardSize.Small,
        CardSize.Medium => ECardSize.Medium,
        CardSize.Large => ECardSize.Large,
        _ => ECardSize.Small
    };

    internal static ETier ParseTier(string? tier)
        => Enum.TryParse<ETier>(tier, true, out var t) ? t : ETier.Bronze;
}

public sealed class OracleSeedManager : ISeedManager
{
    private readonly Random _rng;

    public OracleSeedManager(Random rng) => _rng = rng;

    public double GetDouble() => _rng.NextDouble();

    public int GetNumber() => _rng.Next();

    public float GetNumber(float maxValue) => (float)(_rng.NextDouble() * maxValue);

    public float GetNumber(float minValue, float maxValue)
    {
        if (maxValue <= minValue) return minValue;
        // 对齐 TRangeValue(MinValue, MaxValue + 1f) 整数范围随机语义
        double raw = Math.Floor(minValue + _rng.NextDouble() * (maxValue - minValue));
        double maxInclusive = Math.Floor(maxValue - 1.0);
        if (maxInclusive >= minValue && raw > maxInclusive) raw = maxInclusive;
        return (float)raw;
    }

    public int GetNumber(int maxValue) => maxValue <= 0 ? 0 : _rng.Next(maxValue);

    public int GetNumber(int minValue, int maxValue) => maxValue <= minValue ? minValue : _rng.Next(minValue, maxValue);

    public T SelectRandomItem<T>(List<T> items, Func<T, double> weightSelector)
    {
        if (items.Count == 0) throw new InvalidOperationException("SelectRandomItem called with empty list.");
        double total = 0;
        for (int i = 0; i < items.Count; i++)
            total += Math.Max(0, weightSelector(items[i]));
        if (total <= 0) return items[_rng.Next(items.Count)];
        double roll = _rng.NextDouble() * total;
        double acc = 0;
        for (int i = 0; i < items.Count; i++)
        {
            acc += Math.Max(0, weightSelector(items[i]));
            if (roll < acc) return items[i];
        }
        return items[^1];
    }
}

public sealed class OracleRunState : IRunState
{
    private readonly IReadOnlyList<ICard> _selectionSet;
    private readonly ICard? _currentEncounter;

    public OracleRunState(IReadOnlyList<ICard> selectionSet, ICard? currentEncounter)
    {
        _selectionSet = selectionSet;
        _currentEncounter = currentEncounter;
    }

    public IEnumerable<ICard> GetCardsInSelectionSet() => _selectionSet;
    public ICard? GetCurrentEncounter() => _currentEncounter;
}

public sealed class OracleRun : IRun
{
    private readonly IPlayer _localPlayer;
    private readonly IPlayer? _opponent;
    private readonly ISeedManager _seedManager;
    private readonly IRunState _runState;
    private readonly bool _sandstormActive;
    private readonly HashSet<Guid> _visitedEncounters;

    public OracleRun(
        Guid gameModeId,
        uint day,
        uint hour,
        EPlayMode playMode,
        IPlayer localPlayer,
        IPlayer? opponent,
        ISeedManager seedManager,
        IRunState runState,
        bool sandstormActive,
        IEnumerable<Guid>? visitedEncounters)
    {
        GameModeId = gameModeId;
        Day = day;
        Hour = hour;
        PlayMode = playMode;
        _localPlayer = localPlayer;
        _opponent = opponent;
        _seedManager = seedManager;
        _runState = runState;
        _sandstormActive = sandstormActive;
        _visitedEncounters = visitedEncounters != null ? new HashSet<Guid>(visitedEncounters) : new HashSet<Guid>();
    }

    public Guid GameModeId { get; }
    public Version DataVersion { get; } = new(5, 0, 0);
    public uint Day { get; }
    public uint Hour { get; }
    public ISeedManager SeedManager => _seedManager;
    public EPlayMode PlayMode { get; }

    public IPlayer? GetOpponent() => _opponent;
    public IPlayer GetPlayer() => _localPlayer;
    public ISeedManager? GetSeedManager() => _seedManager;
    public IRunState? GetCurrentState() => _runState;
    public bool HasVisitedEncounter(Guid encounterId) => _visitedEncounters.Contains(encounterId);
    public bool IsSandstormActive() => _sandstormActive;
    public void PublishEvent<TEvent>(TEvent @event) where TEvent : IGameEvent { }
    public void UpdateCombatStats(InstanceId instanceId, ECardStats type, int value) { }
}

public sealed class OraclePlayerInventory : IPlayerInventory
{
    public SocketedContainer Container { get; set; } = new();

    public OraclePlayerInventory(
        IEnumerable<CardInstance> cards,
        int unlockedMask,
        OraclePlayer owner,
        EInventorySection section,
        StandardPricesSettings? prices,
        Dictionary<CardInstance, OracleCard> cardMap)
    {
        Container.UnlockedSockets = (ushort)(unlockedMask & 0x3FF);
        foreach (var ci in cards)
        {
            var oc = new OracleCard(ci, owner, section, prices);
            cardMap[ci] = oc;
            // 仅将未摧毁且已入格的卡牌放入 SocketedContainer，防止被覆写/摧毁的幽灵卡占用同槽位
            if (!ci.IsDestroyed && ci.StartSlot >= 0 && ci.StartSlot < SocketedContainer.SocketCount)
            {
                int width = (int)oc.Size;
                for (int s = ci.StartSlot; s < ci.StartSlot + width && s < SocketedContainer.SocketCount; s++)
                {
                    if (Container.Sockets[s] == null)
                        Container.Sockets[s] = oc;
                }
            }
        }
    }

    public void ActivateItem(ISocketable item) { }
    public bool AddItem(ISocketable card, EContainerSocketId socketId, bool activateItem) => Container.PlaceOnSocket(card, socketId);
    public bool HasSpaceForCard(ISocketable card) => Container.HasSpaceFor(card);
    public bool RemoveItem(ISocketable item) => Container.Remove(item);
    public List<ICard> GetItems() => Container.GetSocketables().OfType<ICard>().ToList();
    public IEnumerable<ICard> GetItemsAsEnumerable() => Container.GetSocketables().OfType<ICard>();
}

public sealed class OraclePlayer : IPlayer
{
    public PlayerState Source { get; }
    private readonly List<ICard> _skills = new();
    private readonly Dictionary<CardInstance, OracleCard> _cardMap;

    public OraclePlayer(
        PlayerState source,
        bool isLocalPlayer,
        StandardPricesSettings? prices,
        Dictionary<CardInstance, OracleCard> cardMap)
    {
        Source = source;
        _cardMap = cardMap;
        CombatantId = isLocalPlayer ? ECombatantId.Player : ECombatantId.Opponent;
        Hero = Enum.TryParse<EHero>(source.Hero, true, out var h) ? h : EHero.Common;

        Hand = new OraclePlayerInventory(source.Hand.Cards, source.Hand.UnlockedMask, this, EInventorySection.Hand, prices, cardMap);
        Stash = new OraclePlayerInventory(source.Stash.Cards, source.Stash.UnlockedMask, this, EInventorySection.Stash, prices, cardMap);
        Socket = new OraclePlayerInventory(source.SocketEffects, source.Hand.UnlockedMask, this, EInventorySection.Hand, prices, cardMap);

        foreach (var sk in source.Skills)
        {
            var oc = new OracleCard(sk, this, null, prices);
            cardMap[sk] = oc;
            _skills.Add(oc);
        }
    }

    public bool TryGetMappedCard(CardInstance ci, out OracleCard card) => _cardMap.TryGetValue(ci, out card!);

    public ECombatantId CombatantId { get; }
    public EHero Hero { get; }
    public IPlayerInventory Hand { get; }
    public IPlayerInventory Stash { get; }
    public IPlayerInventory Socket { get; }

    public int? GetAttributeValue(EPlayerAttributeType attrType)
        => (int)Math.Round(Source.GetAttribute(attrType.ToString()), MidpointRounding.AwayFromZero);

    public int? GetUnscaledAttributeValue(EPlayerAttributeType attrType)
        => (int)Math.Round(Source.GetAttributeUnscaled(attrType.ToString()), MidpointRounding.AwayFromZero);

    public IEnumerable<ICard> GetSkills() => _skills;
}

public sealed class OracleCard : ICard, ISocketable, IEnchantable
{
    public CardInstance Source { get; }
    private readonly StandardPricesSettings? _prices;
    private readonly ITCard _template;
    private readonly ReadOnlyDictionary<EEnchantmentType, TEnchantment>? _enchantments;

    public OracleCard(CardInstance source, IPlayer? owner, EInventorySection? section, StandardPricesSettings? prices)
    {
        Source = source;
        _prices = prices;
        Owner = owner;
        Section = section;
        _template = OfficialSharedOracle.ResolveOfficialTemplate(source.Template);

        TemplateId = OfficialSharedOracle.ParseOrDeterministicGuid(source.Template.Id);
        Size = OfficialSharedOracle.MapSize(source.EffectiveSize);
        Tier = OfficialSharedOracle.ParseTier(source.Tier.ToString());
        Type = MapCardType(source.Template);

        Heroes = new HashSet<EHero>();
        foreach (var h in source.Template.Heroes)
        {
            if (Enum.TryParse<EHero>(h, true, out var eh))
                Heroes.Add(eh);
        }

        // 仅同步官方可见 ECardTag（模板 + 动态/光环追加 + 附魔静态 Tag），不混入 Tiny 或衍生效果位
        Tags = new HashSet<ECardTag>();
        void TryAddTag(string raw)
        {
            if (Enum.TryParse<ECardTag>(raw, true, out var et))
                Tags.Add(et);
        }
        foreach (var t in source.Template.Tags) TryAddTag(t);
        foreach (var t in source.ExtraTags) TryAddTag(t);
        foreach (var t in source.CombatTags) TryAddTag(t);
        foreach (var t in source.AuraTags) TryAddTag(t);
        if (!string.IsNullOrEmpty(source.Enchantment) &&
            source.Template.Enchantments != null &&
            source.Template.Enchantments.TryGetValue(source.Enchantment, out var enchData))
        {
            foreach (var t in enchData.Tags) TryAddTag(t);
        }

        HiddenTags = new HashSet<EHiddenTag>();
        foreach (var ht in source.GetAllHiddenTags())
        {
            if (Enum.TryParse<EHiddenTag>(ht, true, out var eht))
                HiddenTags.Add(eht);
        }

        LeftSocketId = source.StartSlot >= 0 && source.StartSlot < SocketedContainer.SocketCount
            ? (EContainerSocketId)source.StartSlot
            : null;

        State = source.IsDestroyed
            ? ECardState.Transformed
            : (source.IsDisabled ? ECardState.Disabled : ECardState.Alive);

        if (!string.IsNullOrEmpty(source.Enchantment) &&
            Enum.TryParse<EEnchantmentType>(source.Enchantment, true, out var parsedEnch))
        {
            Enchantment = parsedEnch;
        }

        if (source.Template.Enchantments != null)
        {
            var dict = new Dictionary<EEnchantmentType, TEnchantment>();
            foreach (var key in source.Template.Enchantments.Keys)
            {
                if (Enum.TryParse<EEnchantmentType>(key, true, out var ek))
                    dict[ek] = new TEnchantment();
            }
            _enchantments = new ReadOnlyDictionary<EEnchantmentType, TEnchantment>(dict);
        }
    }

    public Guid TemplateId { get; }
    public HashSet<EHero> Heroes { get; }
    public HashSet<EHiddenTag> HiddenTags { get; }
    public string Name => Source.Template.InternalName;
    public ECardSize Size { get; }
    public HashSet<ECardTag> Tags { get; }
    public ETier Tier { get; }
    public ECardType Type { get; }
    public IPlayer? Owner { get; set; }
    public EContainerSocketId? LeftSocketId { get; set; }
    public EInventorySection? Section { get; set; }
    public ECardState State { get; set; }
    public EEnchantmentType? Enchantment { get; set; }

    public EContainerSocketId? GetSocketId() => LeftSocketId;
    public InstanceId GetInstanceId() => new(Source.InstanceId);
    public ReadOnlyDictionary<EEnchantmentType, TEnchantment>? GetEnchantments() => _enchantments;
    public ITCard? GetTemplate() => _template;

    public int? GetAttributeValue(ECardAttributeType type)
        => (int)Math.Round(Source.GetAttribute(type.ToString(), _prices), MidpointRounding.AwayFromZero);

    public int? GetUnscaledAttributeValue(ECardAttributeType type)
        => (int)Math.Round(Source.GetAttributeUnscaled(type.ToString(), _prices), MidpointRounding.AwayFromZero);

    public bool TryGetAttributeValue(ECardAttributeType type, out int? value)
    {
        value = GetAttributeValue(type);
        return value.HasValue;
    }

    public ETier GetNextTier() => Tier switch
    {
        ETier.Bronze => ETier.Silver,
        ETier.Silver => ETier.Gold,
        ETier.Gold => ETier.Diamond,
        _ => ETier.Legendary
    };

    public IEnumerable<TCardAbility> GetActiveAbilities()
    {
        foreach (var ab in Source.ActiveAbilities)
        {
            yield return OfficialSharedOracle.ConvertAbility(ab);
        }
    }

    private static ECardType MapCardType(CardTemplate tpl) => tpl.Type switch
    {
        "Item" => ECardType.Item,
        "Skill" => ECardType.Skill,
        "SocketEffect" or "MusicNoteSocketEffect" => ECardType.SocketEffect,
        "CombatEncounter" => ECardType.CombatEncounter,
        "EncounterStep" => ECardType.EncounterStep,
        "EventEncounter" => ECardType.EventEncounter,
        "PedestalEncounter" => ECardType.PedestalEncounter,
        "PvpEncounter" => ECardType.PvpEncounter,
        "PlayerEffect" => ECardType.PlayerEffect,
        _ when tpl.TypeDiscriminator is "TCardSocketEffect" or "TCardMusicNoteSocketEffect" => ECardType.SocketEffect,
        _ => ECardType.Item
    };
}

public class OracleTriggerEvent : ITriggerEvent, ITargetCardEvent
{
    public OracleTriggerEvent(ICard? triggerSource, ICard? targetCard)
    {
        TriggerSource = triggerSource;
        TargetCard = targetCard!;
    }

    public ICard? TriggerSource { get; }
    public IPlayer? TriggerSourcePlayerOwner => TriggerSource?.Owner;
    public ICard TargetCard { get; }
}

public sealed class OracleDeltaTriggerEvent : OracleTriggerEvent, IGameEventWithDelta
{
    public OracleDeltaTriggerEvent(ICard? triggerSource, ICard? targetCard, int deltaValue)
        : base(triggerSource, targetCard)
    {
        CurrentValue = deltaValue;
    }

    public int PreviousValue => 0;
    public int CurrentValue { get; }
}
