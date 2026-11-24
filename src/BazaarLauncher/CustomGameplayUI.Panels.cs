using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BazaarLauncher;

/// <summary>
/// D6-P2-07：零 Unity 依赖的锦标赛成员只读规则与 Mod 同步摘要格式化纯 .NET 内核。
/// 覆盖 Wave 2-3 倍率（敌方强度/商店价格）、突变词条、禁用遭遇统计及逐 Mod 同步状态，可供纯 .NET 8 单元测试直测。
/// </summary>
public static class LobbyMemberRulesSummaryFormatter
{
    public static string FormatSummary(
        string specialMode,
        string preset,
        IEnumerable<string> mutators,
        IEnumerable<string> bannedEncounterIds,
        IEnumerable<(string ModId, string Status)> modSyncStatuses,
        double enemyStatMult,
        double shopPriceMult)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append("模式: ").Append(string.IsNullOrWhiteSpace(specialMode) ? "Standard" : specialMode.Trim());
        sb.Append(" | 预设: ").Append(string.IsNullOrWhiteSpace(preset) ? "PureOfficial" : preset.Trim());
        sb.Append(" | 敌方倍率: ").Append(enemyStatMult.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('x');
        sb.Append(" | 商店价格: ").Append(shopPriceMult.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('x');

        var mutList = mutators?.Where(m => !string.IsNullOrWhiteSpace(m)).ToList() ?? new List<string>();
        if (mutList.Count > 0)
            sb.Append("\n词条: ").Append(string.Join("、", mutList));

        var bannedList = bannedEncounterIds?.Where(b => !string.IsNullOrWhiteSpace(b)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
        sb.Append("\n禁用遭遇: ").Append(bannedList.Count).Append(" 项");

        var modList = modSyncStatuses?.Where(m => !string.IsNullOrWhiteSpace(m.ModId)).ToList() ?? new List<(string ModId, string Status)>();
        if (modList.Count > 0)
        {
            sb.Append("\n房间 Mod: ");
            for (int i = 0; i < modList.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(modList[i].ModId).Append('[').Append(string.IsNullOrWhiteSpace(modList[i].Status) ? "未知" : modList[i].Status).Append(']');
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// CustomGameplayUI 原生 uGUI 界面装配与零每帧开销刷新逻辑：
/// - 全部控件基于 UnityEngine.UI + TextMeshProUGUI 构建；
/// - 弹窗闭合时 _modalPanelRoot.SetActive(false)，不触发任何布局、字符串拼接或 GC；
/// - HUD 胶囊、局内只读徽章与战斗倍速芯片仅在状态键变更时增量更新。
/// </summary>
internal sealed partial class CustomGameplayUI
{
    private sealed class RuleEditorWidgets
    {
        public bool IsTournament;
        public Func<CustomRunRuleSet> GetRules;
        public Func<RuleEditorBuffer> GetBuf;
        public NativeUguiFactory.ToggleHandle MonsterLootToggle;

        public TMP_InputField GoldInput;
        public TMP_InputField IncomeInput;
        public TMP_InputField ExpInput;
        public TMP_InputField PrestigeInput;
        public TMP_InputField HealthInput;

        public TMP_InputField ItemSearchInput;
        public NativeUguiFactory.ToggleHandle ItemHeroOnlyToggle;
        public readonly NativeUguiFactory.ButtonHandle[] ItemTierButtons = new NativeUguiFactory.ButtonHandle[Tiers.Length];
        public TextMeshProUGUI ItemEnchantLabel;
        public Transform ItemMatchesContainer;
        public Transform SelectedItemsContainer;
        public TextMeshProUGUI SelectedItemsHeader;

        public TMP_InputField SkillSearchInput;
        public NativeUguiFactory.ToggleHandle SkillHeroOnlyToggle;
        public readonly NativeUguiFactory.ButtonHandle[] SkillTierButtons = new NativeUguiFactory.ButtonHandle[Tiers.Length];
        public Transform SkillMatchesContainer;
        public Transform SelectedSkillsContainer;
        public TextMeshProUGUI SelectedSkillsHeader;

        public Transform MultipliersGroupContainer;
        public readonly Dictionary<string, (Slider Slider, TextMeshProUGUI ValLabel)> MultiplierControls =
            new(StringComparer.OrdinalIgnoreCase);
        public string BuiltCategoriesFingerprint = "";
        public string BuiltItemMatchesFingerprint = "";
        public string BuiltSkillMatchesFingerprint = "";
        public string BuiltSelectedItemsFingerprint = "";
        public string BuiltSelectedSkillsFingerprint = "";

        // D6-P1-01/02/03：Wave 2/3 全部扩展字段与动态词条控件
        public TextMeshProUGUI ActiveModePresetLabel;
        public TMP_InputField ArchetypeWeightInput;
        public TMP_InputField ArchetypeCapInput;
        public TMP_InputField EndlessMaxDayInput;
        public TMP_InputField CrossHeroBiasInput;
        public TMP_InputField BossRushWavesInput;
        public NativeUguiFactory.ToggleHandle BossRushPvpToggle;
        public NativeUguiFactory.ToggleHandle SandboxRerollToggle;
        public NativeUguiFactory.ToggleHandle SandboxZeroPriceToggle;
        public TMP_InputField ShopHighTierInput;
        public TMP_InputField ShopPriceMultInput;
        public TMP_InputField LootBonusChoicesInput;
        public TMP_InputField LootTierBonusInput;
        public TMP_InputField EnemyStatMultInput;
        public NativeUguiFactory.ToggleHandle DisableFatesReviveToggle;
        public TMP_InputField FixedSeedInput;
        public Transform MutatorsContainer;
        public string BuiltMutatorsFingerprint = "";
    }

    private sealed class ArchetypeGridWidgets
    {
        public Func<CustomRunRuleSet> GetRules;
        public bool IsPersonalSharedCard;
        public NativeUguiFactory.ToggleHandle PromptBeforeRunToggle;
        public NativeUguiFactory.ButtonHandle ClearBtn;
        public NativeUguiFactory.ButtonHandle LockNowBtn;
        public TextMeshProUGUI HeaderText;
        public TextMeshProUGUI DescText;
        public Transform GridContainer;
        public readonly List<(ArchetypeCatalogEntry Entry, NativeUguiFactory.ButtonHandle Btn)> Buttons = new();
        public string BuiltFingerprint = "";
    }

    private sealed class PresetSlotWidgets
    {
        public int SlotIndex;
        public TMP_InputField NameInput;
        public TextMeshProUGUI SummaryText;
        public NativeUguiFactory.ButtonHandle SaveBtn;
        public NativeUguiFactory.ButtonHandle LoadBtn;
        public NativeUguiFactory.ButtonHandle ClearBtn;
    }

    private readonly NativeUguiFactory _ui = new();

    private GameObject _uiRootGo;
    private Canvas _hudCanvas;
    private GraphicRaycaster _hudRaycaster;
    private RectTransform _safeViewportRoot;
    private Canvas _modalCanvas;
    private GraphicRaycaster _modalRaycaster;
    private RectTransform _modalWindowRt;
    private Camera _cachedMainCam;
    private bool? _cachedCircleGlyphsSupported;

    private int _lastViewportScreenW = -1;
    private int _lastViewportScreenH = -1;
    private Rect _lastViewportCamRect = new(-1f, -1f, -1f, -1f);
    private Rect _lastLetterBoxRect = new(-1f, -1f, -1f, -1f);
    private static bool _letterBoxTypeChecked;
    private static Func<Rect> _letterBoxScreenRectGetter;

    // 规则胶囊双态配色（激活态翠绿高亮描边 vs 官方标准稳重灰蓝）
    private static readonly Color ColRuleActiveBg = new(0.05f, 0.22f, 0.15f, 0.96f);
    private static readonly Color ColRuleActiveHoverBg = new(0.08f, 0.30f, 0.20f, 0.98f);
    private static readonly Color ColRuleActiveBorder = new(0.24f, 0.88f, 0.56f, 1f);
    private static readonly Color ColRuleActiveDot = new(0.32f, 0.98f, 0.64f, 1f);
    private static readonly Color ColRuleActiveTitle = new(0.78f, 1.0f, 0.88f, 1f);
    private static readonly Color ColRuleActiveSub = new(0.56f, 0.95f, 0.74f, 1f);

    private static readonly Color ColRuleVanillaBg = new(0.10f, 0.13f, 0.17f, 0.92f);
    private static readonly Color ColRuleVanillaHoverBg = new(0.15f, 0.20f, 0.25f, 0.96f);
    private static readonly Color ColRuleVanillaBorder = new(0.36f, 0.44f, 0.52f, 0.86f);
    private static readonly Color ColRuleVanillaDot = new(0.50f, 0.58f, 0.66f, 0.90f);
    private static readonly Color ColRuleVanillaTitle = new(0.80f, 0.86f, 0.90f, 1f);
    private static readonly Color ColRuleVanillaSub = new(0.64f, 0.70f, 0.76f, 1f);

    // ── HUD 常驻轻量组件（主菜单胶囊 / 局内右上角状态胶囊 / 战斗倍速芯片） ──
    private NativeUguiFactory.ButtonHandle _menuRulePillBtn;
    private Image _pillStatusDot;
    private TextMeshProUGUI _pillSubText;

    private GameObject _inRunBadgeGo;
    private RectTransform _inRunBadgeRt;
    private Button _inRunBadgeBtn;
    private Image _inRunBadgeBg;
    private Image _inRunBadgeBorder;
    private Image _inRunBadgeDot;
    private TextMeshProUGUI _inRunBadgeText;
    private TextMeshProUGUI _inRunBadgeSubText;

    private GameObject _speedChipGo;
    private RectTransform _speedChipRt;
    private readonly NativeUguiFactory.ButtonHandle[] _speedChipButtons = new NativeUguiFactory.ButtonHandle[4];
    private static readonly float[] SpeedChipValues = { 1f, 2f, 3f, 5f };

    private GameObject _tourneyWaitBannerGo;
    private RectTransform _tourneyWaitBannerRt;
    private Image _tourneyWaitBannerDot;
    private TextMeshProUGUI _tourneyWaitTitleText;
    private TextMeshProUGUI _tourneyWaitSubText;
    private string _lastTourneyWaitKey = "";

    // ── 主配置模态面板 ──────────────────────────────────────────────────────
    private GameObject _modalPanelRoot;
    private Image _overviewLampBg;
    private Image _overviewLampBorder;
    private Image _overviewLampDot;
    private TextMeshProUGUI _overviewLampTitle;
    private TextMeshProUGUI _overviewLampDetail;
    private NativeUguiFactory.ButtonHandle _btnResetAllOfficial;
    private TextMeshProUGUI _headerAccountText;

    private NativeUguiFactory.ButtonHandle _tabNormalBtn;
    private NativeUguiFactory.ButtonHandle _tabTournamentBtn;
    private NativeUguiFactory.ButtonHandle _btnRefreshCatalog;

    private GameObject _cosmeticsToolboxGo;
    private TextMeshProUGUI _toolboxChestText;
    private Image _toolboxRunBadgeBg;
    private TextMeshProUGUI _toolboxRunBadgeText;
    private GameObject _toolboxConcedeRow;
    private readonly List<(string HeroKey, NativeUguiFactory.ButtonHandle Btn)> _toolboxHeroBtns = new();
    private readonly List<NativeUguiFactory.ButtonHandle> _toolboxActionBtns = new();

    private NativeUguiFactory.ScrollViewHandle _mainScroll;
    private GameObject _normalTabContainer;
    private GameObject _tournamentTabContainer;

    private ArchetypeGridWidgets _normalArchetypeGrid;
    private RuleEditorWidgets _normalWidgets;
    private NativeUguiFactory.ButtonHandle _btnSaveNormal;

    // 锦标赛 Tab 控件（顶部含个人流派倾向共享卡，房主与所有队员共同可见）
    private ArchetypeGridWidgets _tourneyPersonalArchetypeGrid;
    private TextMeshProUGUI _presetMemoryStatusText;
    private readonly PresetSlotWidgets[] _presetSlots = new PresetSlotWidgets[TournamentPresetStore.SlotCount];
    private GameObject _tourneyNoLobbyCard;
    private GameObject _tourneyLobbyActiveCard;
    private TextMeshProUGUI _tourneyLobbyHeaderText;
    private NativeUguiFactory.ButtonHandle _btnSaveLobbyHeader;
    private GameObject _tourneyHostBody;
    private GameObject _tourneyMemberBody;
    private NativeUguiFactory.ButtonHandle _btnCap8;
    private NativeUguiFactory.ButtonHandle _btnCap10;
    private TextMeshProUGUI _tourneyHostMembersText;
    private NativeUguiFactory.ToggleHandle _togBotLock;
    private NativeUguiFactory.ToggleHandle _togMatch4;
    private NativeUguiFactory.ToggleHandle _togLastHuman;
    private NativeUguiFactory.ToggleHandle _togAllowTendency;
    private RuleEditorWidgets _lobbyWidgets;
    private NativeUguiFactory.ButtonHandle _btnSaveLobbyBottom;
    private TextMeshProUGUI _tourneyMemberSummaryText;

    private TextMeshProUGUI _bottomStatusText;
    private readonly NativeUguiFactory.ButtonHandle[] _fpsLimitButtons = new NativeUguiFactory.ButtonHandle[4];
    private static readonly (int Fps, string Label)[] FpsPresetOptions =
    {
        (60, "60省电"),
        (90, "90"),
        (120, "120"),
        (0, "原版"),
    };

    // ── 全模式统一开局流派倾向确认弹窗（Day 1 Hour 1 仅此一次确认锁定）────
    private GameObject _preRunPromptRoot;
    private TextMeshProUGUI _openingPromptTitleText;
    private TextMeshProUGUI _openingPromptSubText;
    private ArchetypeGridWidgets _preRunArchetypeGrid;
    private NativeUguiFactory.ToggleHandle _preRunPromptAutoToggle;

    // ── 增量脏检查缓存（零无效刷新） ────────────────────────────────────────
    private string _lastPillKey = "";
    private string _lastInRunBadgeKey = "";
    private int _lastSpeedTenths = -1;
    private string _lastToolboxKey = "";
    private UiTab? _lastModalTab;
    private bool _hudBuilt;
    private bool _modalBuilt;
    private bool _preRunBuilt;

    private void EnsureHudBuilt()
    {
        if (_hudBuilt && _hudCanvas != null && _safeViewportRoot != null) return;
        _hudBuilt = true;

        if (_uiRootGo == null)
        {
            _uiRootGo = new GameObject("BazaarNativeUguiRoot");
            DontDestroyOnLoad(_uiRootGo);
        }

        _hudCanvas = _ui.CreateOverlayCanvas("BazaarHudCanvas", _uiRootGo.transform, sortingOrder: 420);
        _hudRaycaster = _hudCanvas.GetComponent<GraphicRaycaster>();

        // 在非 16:9 窗口（如 1386x1368 上下黑边）下严格内缩到真实主画面区域，避免 HUD 掉入黑边遮罩
        var safeGo = new GameObject("SafeViewportRoot", typeof(RectTransform));
        safeGo.transform.SetParent(_hudCanvas.transform, worldPositionStays: false);
        _safeViewportRoot = (RectTransform)safeGo.transform;
        NativeUguiFactory.StretchFill(_safeViewportRoot);
        UpdateSafeViewportRootIfNeeded(force: true);

        // 1. 主菜单 / 英雄选择页 / 锦标赛大厅 规则状态胶囊按钮（左移并收窄至顶部居中偏左空域，与左侧段位徽标及右侧宝箱/宝石留出干净边距）
        _menuRulePillBtn = _ui.CreateButton(
            "MenuRuleStatusPill",
            _safeViewportRoot,
            "",
            15f,
            NativeUguiFactory.ColDefaultPillBg,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColDefaultPillText,
            OnMenuPillClicked,
            borderColor: NativeUguiFactory.ColDefaultPillBorder,
            fontStyle: FontStyles.Bold,
            align: TextAlignmentOptions.TopLeft,
            padH: 40,
            padV: 7);

        var pillRt = (RectTransform)_menuRulePillBtn.Root.transform;
        pillRt.anchorMin = new Vector2(0.5f, 1f);
        pillRt.anchorMax = new Vector2(0.5f, 1f);
        pillRt.pivot = new Vector2(0.5f, 1f);
        pillRt.anchoredPosition = new Vector2(-230f, -12f);
        pillRt.sizeDelta = new Vector2(480f, 54f);

        // 左侧双态圆灯
        var pillDotGo = new GameObject("StatusDot", typeof(RectTransform));
        pillDotGo.transform.SetParent(_menuRulePillBtn.Root.transform, worldPositionStays: false);
        var pdrt = (RectTransform)pillDotGo.transform;
        pdrt.anchorMin = new Vector2(0f, 0.5f);
        pdrt.anchorMax = new Vector2(0f, 0.5f);
        pdrt.pivot = new Vector2(0.5f, 0.5f);
        pdrt.anchoredPosition = new Vector2(20f, 0f);
        pdrt.sizeDelta = new Vector2(14f, 14f);
        _pillStatusDot = pillDotGo.AddComponent<Image>();
        _pillStatusDot.sprite = _ui.CircleSprite;
        _pillStatusDot.color = NativeUguiFactory.ColDefaultPillBorder;
        _pillStatusDot.raycastTarget = false;

        // 主标题微调为上半行
        var pTitleRt = _menuRulePillBtn.Label.rectTransform;
        pTitleRt.anchorMin = new Vector2(0f, 0.5f);
        pTitleRt.anchorMax = new Vector2(1f, 1f);
        pTitleRt.offsetMin = new Vector2(38f, 0f);
        pTitleRt.offsetMax = new Vector2(-14f, -4f);
        _menuRulePillBtn.Label.alignment = TextAlignmentOptions.BottomLeft;
        _menuRulePillBtn.Label.textWrappingMode = TextWrappingModes.NoWrap;
        _menuRulePillBtn.Label.overflowMode = TextOverflowModes.Ellipsis;

        // 副行摘要
        _pillSubText = _ui.CreateText(
            "SubSummary",
            _menuRulePillBtn.Root.transform,
            "",
            12.5f,
            NativeUguiFactory.ColMuted,
            FontStyles.Normal,
            TextAlignmentOptions.TopLeft,
            wrap: false,
            raycastTarget: false);
        _pillSubText.overflowMode = TextOverflowModes.Ellipsis;
        var pSubRt = _pillSubText.rectTransform;
        pSubRt.anchorMin = new Vector2(0f, 0f);
        pSubRt.anchorMax = new Vector2(1f, 0.5f);
        pSubRt.offsetMin = new Vector2(38f, 4f);
        pSubRt.offsetMax = new Vector2(-14f, 0f);

        // 2. 局内右上角安全区域规则状态胶囊（紧邻设置齿轮左侧，支持非战斗点击直达配置，绝不遮挡齿轮/时钟/地毯）
        // 注意：Button 使用 ColorTint 叠乘 targetGraphic.color，因此背景 Image 必须保持 Color.white
        _inRunBadgeGo = _ui.CreateBox("InRunRuleMiniBadge", _safeViewportRoot, Color.white, rounded: true, raycastTarget: true);
        _inRunBadgeBg = _inRunBadgeGo.GetComponent<Image>();
        _inRunBadgeBtn = _inRunBadgeGo.AddComponent<Button>();
        _inRunBadgeBtn.targetGraphic = _inRunBadgeBg;
        _inRunBadgeBtn.onClick.AddListener(OnInRunRuleBadgeClicked);
        ApplyInRunPillButtonColors(_inRunBadgeBtn, ColRuleVanillaBg, ColRuleVanillaHoverBg);
        _inRunBadgeBorder = _ui.AddOutlineOverlay(_inRunBadgeGo.transform, ColRuleVanillaBorder);
        _inRunBadgeRt = (RectTransform)_inRunBadgeGo.transform;
        _inRunBadgeRt.anchorMin = new Vector2(1f, 1f);
        _inRunBadgeRt.anchorMax = new Vector2(1f, 1f);
        _inRunBadgeRt.pivot = new Vector2(1f, 1f);
        _inRunBadgeRt.anchoredPosition = new Vector2(-132f, -12f);
        _inRunBadgeRt.sizeDelta = new Vector2(248f, 32f);

        var ibDotGo = new GameObject("Dot", typeof(RectTransform));
        ibDotGo.transform.SetParent(_inRunBadgeGo.transform, worldPositionStays: false);
        var ibdrt = (RectTransform)ibDotGo.transform;
        ibdrt.anchorMin = new Vector2(0f, 0.5f);
        ibdrt.anchorMax = new Vector2(0f, 0.5f);
        ibdrt.pivot = new Vector2(0.5f, 0.5f);
        ibdrt.anchoredPosition = new Vector2(14f, 0f);
        ibdrt.sizeDelta = new Vector2(10f, 10f);
        _inRunBadgeDot = ibDotGo.AddComponent<Image>();
        _inRunBadgeDot.sprite = _ui.CircleSprite;
        _inRunBadgeDot.color = ColRuleVanillaDot;
        _inRunBadgeDot.raycastTarget = false;

        _inRunBadgeText = _ui.CreateText(
            "Text",
            _inRunBadgeGo.transform,
            "",
            13f,
            ColRuleVanillaTitle,
            FontStyles.Bold,
            TextAlignmentOptions.MidlineLeft,
            wrap: false,
            raycastTarget: false);
        _inRunBadgeText.overflowMode = TextOverflowModes.Ellipsis;
        var ibtrt = _inRunBadgeText.rectTransform;
        ibtrt.anchorMin = Vector2.zero;
        ibtrt.anchorMax = Vector2.one;
        ibtrt.offsetMin = new Vector2(12f, 2f);
        ibtrt.offsetMax = new Vector2(-12f, -2f);

        _inRunBadgeSubText = _ui.CreateText(
            "SubSummary",
            _inRunBadgeGo.transform,
            "",
            11.5f,
            ColRuleActiveSub,
            FontStyles.Normal,
            TextAlignmentOptions.TopLeft,
            wrap: false,
            raycastTarget: false);
        _inRunBadgeSubText.overflowMode = TextOverflowModes.Ellipsis;
        var ibsrt = _inRunBadgeSubText.rectTransform;
        ibsrt.anchorMin = new Vector2(0f, 0f);
        ibsrt.anchorMax = new Vector2(1f, 0.48f);
        ibsrt.offsetMin = new Vector2(12f, 3f);
        ibsrt.offsetMax = new Vector2(-12f, 0f);
        _inRunBadgeSubText.gameObject.SetActive(false);
        _inRunBadgeGo.SetActive(false);

        // 3. 战斗界面右上角轻量倍速切换小芯片（1x / 2x / 3x / 5x）
        _speedChipGo = _ui.CreateBox("CombatSpeedChip", _safeViewportRoot, NativeUguiFactory.ColPanelBg, rounded: true, raycastTarget: true);
        _ui.AddOutlineOverlay(_speedChipGo.transform, NativeUguiFactory.ColGoldDim);
        _speedChipRt = (RectTransform)_speedChipGo.transform;
        _speedChipRt.anchorMin = new Vector2(1f, 1f);
        _speedChipRt.anchorMax = new Vector2(1f, 1f);
        _speedChipRt.pivot = new Vector2(1f, 1f);
        _speedChipRt.anchoredPosition = new Vector2(-132f, -50f);
        _speedChipRt.sizeDelta = new Vector2(262f, 34f);

        var scRow = _speedChipGo.AddComponent<HorizontalLayoutGroup>();
        scRow.padding = new RectOffset(8, 6, 4, 4);
        scRow.spacing = 4f;
        scRow.childAlignment = TextAnchor.MiddleCenter;
        scRow.childControlWidth = true;
        scRow.childControlHeight = true;
        scRow.childForceExpandWidth = false;

        var scTitle = _ui.CreateText("Tag", _speedChipGo.transform, "倍速", 13f, NativeUguiFactory.ColGold, FontStyles.Bold, TextAlignmentOptions.Center, wrap: false);
        NativeUguiFactory.SetLayout(scTitle.gameObject, minW: 36f, prefW: 36f, minH: 24f);

        for (int i = 0; i < 4; i++)
        {
            float spd = SpeedChipValues[i];
            var btn = _ui.CreateButton(
                $"Btn{spd:0}x",
                _speedChipGo.transform,
                $"{spd:0}x",
                13.5f,
                NativeUguiFactory.ColBtnNormal,
                NativeUguiFactory.ColBtnHover,
                NativeUguiFactory.ColCream,
                () => OnCombatSpeedChipClicked(spd),
                borderColor: new Color(0.45f, 0.38f, 0.22f, 0.8f),
                fontStyle: FontStyles.Bold,
                padH: 4,
                padV: 2);
            NativeUguiFactory.SetLayout(btn.Root, minW: 46f, prefW: 48f, flexW: 1f, minH: 24f, prefH: 26f);
            _speedChipButtons[i] = btn;
        }
        _speedChipGo.SetActive(false);

        // 4. 锦标赛非战斗阶段 PvP 同步等待横幅（紧邻只读小徽章下方，仅等待对手时显示）
        _tourneyWaitBannerGo = _ui.CreateBox("TourneyWaitBanner", _safeViewportRoot, NativeUguiFactory.ColPanelBg, rounded: true, raycastTarget: false);
        _ui.AddOutlineOverlay(_tourneyWaitBannerGo.transform, NativeUguiFactory.ColGold);
        _tourneyWaitBannerRt = (RectTransform)_tourneyWaitBannerGo.transform;
        _tourneyWaitBannerRt.anchorMin = new Vector2(1f, 1f);
        _tourneyWaitBannerRt.anchorMax = new Vector2(1f, 1f);
        _tourneyWaitBannerRt.pivot = new Vector2(1f, 1f);
        _tourneyWaitBannerRt.anchoredPosition = new Vector2(-132f, -50f);
        _tourneyWaitBannerRt.sizeDelta = new Vector2(316f, 52f);

        var wbDotGo = new GameObject("Dot", typeof(RectTransform));
        wbDotGo.transform.SetParent(_tourneyWaitBannerGo.transform, worldPositionStays: false);
        var wbdrt = (RectTransform)wbDotGo.transform;
        wbdrt.anchorMin = new Vector2(0f, 0.5f);
        wbdrt.anchorMax = new Vector2(0f, 0.5f);
        wbdrt.pivot = new Vector2(0.5f, 0.5f);
        wbdrt.anchoredPosition = new Vector2(16f, 0f);
        wbdrt.sizeDelta = new Vector2(10f, 10f);
        _tourneyWaitBannerDot = wbDotGo.AddComponent<Image>();
        _tourneyWaitBannerDot.sprite = _ui.CircleSprite;
        _tourneyWaitBannerDot.color = NativeUguiFactory.ColGold;
        _tourneyWaitBannerDot.raycastTarget = false;

        _tourneyWaitTitleText = _ui.CreateText(
            "Title",
            _tourneyWaitBannerGo.transform,
            "",
            13f,
            NativeUguiFactory.ColGold,
            FontStyles.Bold,
            TextAlignmentOptions.MidlineLeft,
            wrap: false,
            raycastTarget: false);
        _tourneyWaitTitleText.overflowMode = TextOverflowModes.Ellipsis;
        var wtrt = _tourneyWaitTitleText.rectTransform;
        wtrt.anchorMin = new Vector2(0f, 0.5f);
        wtrt.anchorMax = new Vector2(1f, 1f);
        wtrt.offsetMin = new Vector2(28f, 0f);
        wtrt.offsetMax = new Vector2(-10f, -4f);

        _tourneyWaitSubText = _ui.CreateText(
            "Sub",
            _tourneyWaitBannerGo.transform,
            "",
            11.5f,
            NativeUguiFactory.ColCream,
            FontStyles.Normal,
            TextAlignmentOptions.MidlineLeft,
            wrap: false,
            raycastTarget: false);
        _tourneyWaitSubText.overflowMode = TextOverflowModes.Ellipsis;
        var wsrt = _tourneyWaitSubText.rectTransform;
        wsrt.anchorMin = new Vector2(0f, 0f);
        wsrt.anchorMax = new Vector2(1f, 0.5f);
        wsrt.offsetMin = new Vector2(28f, 4f);
        wsrt.offsetMax = new Vector2(-10f, 0f);
        _tourneyWaitBannerGo.SetActive(false);
    }

    private void EnsureModalBuilt()
    {
        if (_modalBuilt && _modalPanelRoot != null) return;
        _modalBuilt = true;
        EnsureHudBuilt();

        _modalCanvas = _ui.CreateOverlayCanvas("BazaarCustomRulesModalCanvas", _uiRootGo.transform, sortingOrder: 500);
        _modalRaycaster = _modalCanvas.GetComponent<GraphicRaycaster>();
        _modalCanvas.enabled = false;
        if (_modalRaycaster != null) _modalRaycaster.enabled = false;

        _modalPanelRoot = new GameObject("ModalRoot", typeof(RectTransform));
        _modalPanelRoot.transform.SetParent(_modalCanvas.transform, worldPositionStays: false);
        NativeUguiFactory.StretchFill((RectTransform)_modalPanelRoot.transform);

        // 背景遮罩作为 MainWindow 同级兄弟节点，防止窗口内点击事件冒泡误关闭
        var backdropGo = _ui.CreateBox("Backdrop", _modalPanelRoot.transform, NativeUguiFactory.ColBackdrop, rounded: false, raycastTarget: true);
        NativeUguiFactory.StretchFill((RectTransform)backdropGo.transform);
        var backdropBtn = backdropGo.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(ClosePanel);

        // 中央暗金主卡片
        var win = _ui.CreateCard(
            "MainWindow",
            _modalPanelRoot.transform,
            NativeUguiFactory.ColPanelBg,
            NativeUguiFactory.ColGold,
            padH: 14,
            padV: 12,
            spacing: 8f);
        var wrt = (RectTransform)win.transform;
        _modalWindowRt = wrt;
        wrt.anchorMin = new Vector2(0.5f, 0.5f);
        wrt.anchorMax = new Vector2(0.5f, 0.5f);
        wrt.pivot = new Vector2(0.5f, 0.5f);
        wrt.sizeDelta = new Vector2(1260f, 880f);
        FitModalWindowToSafeViewport();
        // 阻止点击卡片内部穿透到背景遮罩
        win.GetComponent<Image>().raycastTarget = true;

        BuildModalHeader(win.transform);
        BuildModalTabBar(win.transform);
        BuildMainMenuCosmeticsToolbox(win.transform);

        _mainScroll = _ui.CreateScrollView("MainScroll", win.transform);
        NativeUguiFactory.SetLayout(_mainScroll.Root, flexH: 1f, minH: 240f);

        _normalTabContainer = _ui.CreateVerticalGroup("NormalTabContainer", _mainScroll.Content, spacing: 10f);
        BuildNormalTabBody(_normalTabContainer.transform);

        _tournamentTabContainer = _ui.CreateVerticalGroup("TournamentTabContainer", _mainScroll.Content, spacing: 10f);
        BuildTournamentTabBody(_tournamentTabContainer.transform);

        BuildExtendedTabs(_mainScroll.Content);

        BuildBottomStatusBar(win.transform);

        _modalPanelRoot.SetActive(false);
    }

    private void BuildModalHeader(Transform parent)
    {
        var bar = _ui.CreateCard("TopHeader", parent, NativeUguiFactory.ColHeaderBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 8, spacing: 6f);
        NativeUguiFactory.SetLayout(bar, minH: 64f);

        var row = _ui.CreateHorizontalRow("HeaderRow", bar.transform, spacing: 10f);

        var title = _ui.CreateText("Title", row.transform, "大巴扎工坊 · 社交 · 性能控制中心", 19.5f, NativeUguiFactory.ColGold, FontStyles.Bold, TextAlignmentOptions.MidlineLeft, wrap: false);
        NativeUguiFactory.SetLayout(title.gameObject, minW: 310f, prefW: 330f, minH: 36f);

        // 显眼【总览状态灯】
        var lamp = _ui.CreateBox("OverviewStatusLamp", row.transform, NativeUguiFactory.ColDefaultPillBg, rounded: true, raycastTarget: false);
        _overviewLampBg = lamp.GetComponent<Image>();
        _overviewLampBorder = _ui.AddOutlineOverlay(lamp.transform, NativeUguiFactory.ColDefaultPillBorder);
        NativeUguiFactory.SetLayout(lamp, minW: 340f, flexW: 1f, minH: 42f, prefH: 44f);

        var dotGo = new GameObject("Dot", typeof(RectTransform));
        dotGo.transform.SetParent(lamp.transform, worldPositionStays: false);
        var drt = (RectTransform)dotGo.transform;
        drt.anchorMin = new Vector2(0f, 0.5f);
        drt.anchorMax = new Vector2(0f, 0.5f);
        drt.pivot = new Vector2(0.5f, 0.5f);
        drt.anchoredPosition = new Vector2(18f, 0f);
        drt.sizeDelta = new Vector2(14f, 14f);
        _overviewLampDot = dotGo.AddComponent<Image>();
        _overviewLampDot.sprite = _ui.CircleSprite;
        _overviewLampDot.color = NativeUguiFactory.ColDefaultPillBorder;
        _overviewLampDot.raycastTarget = false;

        _overviewLampTitle = _ui.CreateText("LampTitle", lamp.transform, "官方默认规则", 14.5f, NativeUguiFactory.ColDefaultPillText, FontStyles.Bold, TextAlignmentOptions.BottomLeft, wrap: false);
        var ltrt = _overviewLampTitle.rectTransform;
        ltrt.anchorMin = new Vector2(0f, 0.5f);
        ltrt.anchorMax = new Vector2(1f, 1f);
        ltrt.offsetMin = new Vector2(34f, 0f);
        ltrt.offsetMax = new Vector2(-10f, -3f);

        _overviewLampDetail = _ui.CreateText("LampDetail", lamp.transform, "未开启任何自定义", 12f, NativeUguiFactory.ColMuted, FontStyles.Normal, TextAlignmentOptions.TopLeft, wrap: false);
        var ldrt = _overviewLampDetail.rectTransform;
        ldrt.anchorMin = new Vector2(0f, 0f);
        ldrt.anchorMax = new Vector2(1f, 0.5f);
        ldrt.offsetMin = new Vector2(34f, 3f);
        ldrt.offsetMax = new Vector2(-10f, 0f);

        // 显眼【一键恢复官方默认（全部关闭）】按钮
        _btnResetAllOfficial = _ui.CreateButton(
            "BtnResetAllOfficial",
            row.transform,
            "一键恢复官方默认（全部关闭）",
            13.5f,
            NativeUguiFactory.ColDangerBg,
            new Color(0.56f, 0.16f, 0.18f, 1f),
            NativeUguiFactory.ColCream,
            OnOneClickResetOfficialClicked,
            borderColor: NativeUguiFactory.ColDangerBorder,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_btnResetAllOfficial.Root, minW: 215f, prefW: 224f, minH: 38f, prefH: 40f);

        _headerAccountText = _ui.CreateText("AccountInfo", row.transform, "", 13f, NativeUguiFactory.ColMuted, FontStyles.Normal, TextAlignmentOptions.MidlineRight, wrap: false);
        NativeUguiFactory.SetLayout(_headerAccountText.gameObject, minW: 120f, prefW: 155f, minH: 34f);

        var btnNativeSettings = _ui.CreateButton(
            "BtnOpenNativeSettings",
            row.transform,
            "原生设置",
            13.5f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColGold,
            OpenNativeGameSettings,
            borderColor: NativeUguiFactory.ColGoldDim,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(btnNativeSettings.Root, minW: 92f, prefW: 98f, minH: 38f, prefH: 40f);

        var btnClose = _ui.CreateButton(
            "BtnClose",
            row.transform,
            "× 关闭",
            14f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColCream,
            ClosePanel,
            borderColor: NativeUguiFactory.ColGoldDim,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(btnClose.Root, minW: 82f, prefW: 86f, minH: 38f, prefH: 40f);
    }

    private void OpenNativeGameSettings()
    {
        ClosePanel();
        BazaarLauncher.Core.MainThreadDispatcher.Enqueue(() =>
        {
            try
            {
                var popupType = AccessTools.TypeByName("TheBazaar.PopupManager");
                if (popupType == null) return;

                object pm = null;
                var servicesType = AccessTools.TypeByName("TheBazaar.AppFramework.Services");
                if (servicesType != null)
                {
                    var getM = servicesType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    if (getM != null)
                    {
                        try { pm = getM.MakeGenericMethod(popupType).Invoke(null, null); } catch { }
                    }
                }
                pm ??= UnityEngine.Object.FindFirstObjectByType(popupType);
                if (pm != null)
                {
                    var showM = popupType.GetMethod("ShowSettings", BindingFlags.Public | BindingFlags.Instance);
                    showM?.Invoke(pm, new object[] { null });
                }
            }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning("OpenNativeGameSettings: " + ex.Message);
            }
        });
    }

    private void BuildModalTabBar(Transform parent)
    {
        var row = _ui.CreateHorizontalRow("TabBar", parent, spacing: 6f);
        NativeUguiFactory.SetLayout(row, minH: 36f);

        _tabNormalBtn = _ui.CreateButton(
            "TabNormal",
            row.transform,
            "常规规则",
            14f,
            NativeUguiFactory.ColGoldBtn,
            NativeUguiFactory.ColGoldBtnHover,
            Color.white,
            () => SwitchTab(UiTab.Normal),
            borderColor: NativeUguiFactory.ColGold,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_tabNormalBtn.Root, minW: 118f, prefW: 126f, minH: 36f, prefH: 36f);

        _tabTournamentBtn = _ui.CreateButton(
            "TabTournament",
            row.transform,
            "锦标赛房规",
            14f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColCream,
            () => SwitchTab(UiTab.Tournament),
            borderColor: NativeUguiFactory.ColGoldDim,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_tabTournamentBtn.Root, minW: 118f, prefW: 126f, minH: 36f, prefH: 36f);

        _tabWorkshopBtn = _ui.CreateButton(
            "TabWorkshop",
            row.transform,
            "自建工坊 & Mod",
            14f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColCream,
            () => SwitchTab(UiTab.Workshop),
            borderColor: NativeUguiFactory.ColGoldDim,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_tabWorkshopBtn.Root, minW: 146f, prefW: 158f, minH: 36f, prefH: 36f);

        _tabSocialBtn = _ui.CreateButton(
            "TabSocial",
            row.transform,
            "多人社交与观战",
            14f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColCream,
            () => SwitchTab(UiTab.Social),
            borderColor: NativeUguiFactory.ColGoldDim,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_tabSocialBtn.Root, minW: 146f, prefW: 158f, minH: 36f, prefH: 36f);

        _tabPerformanceBtn = _ui.CreateButton(
            "TabPerformance",
            row.transform,
            "性能监控与诊断",
            14f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColCream,
            () => SwitchTab(UiTab.Performance),
            borderColor: NativeUguiFactory.ColGoldDim,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_tabPerformanceBtn.Root, minW: 146f, prefW: 158f, minH: 36f, prefH: 36f);

        var spacer = new GameObject("Spacer", typeof(RectTransform));
        spacer.transform.SetParent(row.transform, worldPositionStays: false);
        NativeUguiFactory.SetLayout(spacer, flexW: 1f);

        _btnRefreshCatalog = _ui.CreateButton(
            "BtnRefresh",
            row.transform,
            "刷新",
            13.5f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColCream,
            OnRefreshClicked,
            borderColor: NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(_btnRefreshCatalog.Root, minW: 72f, prefW: 78f, minH: 34f, prefH: 36f);
    }

    private void BuildMainMenuCosmeticsToolbox(Transform parent)
    {
        _cosmeticsToolboxGo = _ui.CreateCard("CosmeticsToolbox", parent, NativeUguiFactory.ColAmberBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 8, spacing: 6f);

        var top = _ui.CreateHorizontalRow("TopRow", _cosmeticsToolboxGo.transform, spacing: 10f);
        var title = _ui.CreateText("Title", top.transform, "快捷切英雄 & 换肤开箱百宝箱", 15f, NativeUguiFactory.ColGold, FontStyles.Bold, wrap: false);
        NativeUguiFactory.SetLayout(title.gameObject, minW: 250f, prefW: 260f, minH: 24f);

        _toolboxChestText = _ui.CreateText("ChestText", top.transform, "当前宝箱：同步中", 14f, NativeUguiFactory.ColGold, FontStyles.Bold, wrap: false);
        NativeUguiFactory.SetLayout(_toolboxChestText.gameObject, minW: 160f, flexW: 1f, minH: 24f);

        var runBadge = _ui.CreateBox("RunBadge", top.transform, NativeUguiFactory.ColCustomPillBg, rounded: true, raycastTarget: false);
        _toolboxRunBadgeBg = runBadge.GetComponent<Image>();
        NativeUguiFactory.SetLayout(runBadge, minW: 300f, prefW: 336f, minH: 24f);
        _toolboxRunBadgeText = _ui.CreateText("Txt", runBadge.transform, "", 13f, NativeUguiFactory.ColEmeraldText, FontStyles.Bold, TextAlignmentOptions.Center, wrap: false);
        NativeUguiFactory.StretchFill(_toolboxRunBadgeText.rectTransform);

        _toolboxConcedeRow = _ui.CreateHorizontalRow("ConcedeRow", _cosmeticsToolboxGo.transform, spacing: 10f);
        var warnTxt = _ui.CreateText(
            "Warn",
            _toolboxConcedeRow.transform,
            "提示：当前账号仍有未结束对局，原生主菜单会隐藏切英雄与换装按钮；可点击右侧放弃对局解锁。",
            13f,
            NativeUguiFactory.ColCream);
        NativeUguiFactory.SetLayout(warnTxt.gameObject, flexW: 1f, minH: 24f);

        var concedeBtn = _ui.CreateButton(
            "BtnConcede",
            _toolboxConcedeRow.transform,
            "放弃当前对局并解锁",
            13.5f,
            NativeUguiFactory.ColDangerBg,
            new Color(0.56f, 0.16f, 0.18f, 1f),
            NativeUguiFactory.ColCream,
            ConcedeActiveRunFromToolboxAsync,
            borderColor: NativeUguiFactory.ColDangerBorder,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(concedeBtn.Root, minW: 190f, prefW: 200f, minH: 28f);
        _toolboxActionBtns.Add(concedeBtn);

        var heroRow = _ui.CreateHorizontalRow("HeroRow", _cosmeticsToolboxGo.transform, spacing: 5f);
        var hLbl = _ui.CreateText("Lbl", heroRow.transform, "一键切英雄：", 14f, NativeUguiFactory.ColGold, FontStyles.Bold, wrap: false);
        NativeUguiFactory.SetLayout(hLbl.gameObject, minW: 90f, prefW: 92f, minH: 28f);

        _toolboxHeroBtns.Clear();
        foreach (var (heroKey, shortZh) in QuickSwitchHeroes)
        {
            string capturedKey = heroKey;
            string capturedZh = shortZh;
            var hb = _ui.CreateButton(
                "Hero_" + heroKey,
                heroRow.transform,
                shortZh,
                13f,
                NativeUguiFactory.ColBtnNormal,
                NativeUguiFactory.ColBtnHover,
                NativeUguiFactory.ColCream,
                () => OnQuickSwitchHeroClicked(capturedKey, capturedZh));
            NativeUguiFactory.SetLayout(hb.Root, minW: 72f, flexW: 1f, minH: 28f);
            _toolboxHeroBtns.Add((heroKey, hb));
            _toolboxActionBtns.Add(hb);
        }

        var actRow = _ui.CreateHorizontalRow("ActRow", _cosmeticsToolboxGo.transform, spacing: 8f);
        var btnLoadout = _ui.CreateButton(
            "BtnLoadout",
            actRow.transform,
            "打开换装/皮肤界面",
            14f,
            NativeUguiFactory.ColGoldBtn,
            NativeUguiFactory.ColGoldBtnHover,
            Color.white,
            () =>
            {
                ClosePanel();
                GameDriver.OpenLoadoutPopup();
                SetStatus("已打开英雄换装与皮肤界面", isError: false);
            },
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(btnLoadout.Root, minW: 196f, prefW: 200f, minH: 30f);
        _toolboxActionBtns.Add(btnLoadout);

        var btnChestScene = _ui.CreateButton(
            "BtnChestScene",
            actRow.transform,
            "前往 3D 开箱场景",
            14f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColCream,
            () =>
            {
                ClosePanel();
                GameDriver.OpenChestScene();
                SetStatus("正在前往 3D 宝箱开启场景...", isError: false);
            });
        NativeUguiFactory.SetLayout(btnChestScene.Root, minW: 176f, prefW: 180f, minH: 30f);
        _toolboxActionBtns.Add(btnChestScene);

        var btnGrant = _ui.CreateButton(
            "BtnGrantChests",
            actRow.transform,
            "免费补充30宝箱",
            14f,
            NativeUguiFactory.ColGoldBtn,
            NativeUguiFactory.ColGoldBtnHover,
            Color.white,
            () => GrantFreeChestsFromToolboxAsync(30),
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(btnGrant.Root, minW: 176f, prefW: 180f, minH: 30f);
        _toolboxActionBtns.Add(btnGrant);
    }

    private void BuildNormalTabBody(Transform parent)
    {
        // 顶部快捷操作条
        var bar = _ui.CreateCard("NormalActionBar", parent, NativeUguiFactory.ColSubCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 8);
        var row = _ui.CreateHorizontalRow("Row", bar.transform, spacing: 8f);
        var info = _ui.CreateText("Info", row.transform, "个人普通 / 排位对局规则（保存后将在下次开局生效）", 14.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(info.gameObject, flexW: 1f, minH: 30f);

        var btnStd = _ui.CreateButton(
            "BtnStd",
            row.transform,
            "恢复标准 15/5/3/20/300",
            13.5f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColCream,
            () =>
            {
                _normalRules.ResetToStandardValues(preserveArchetypeAndLobbyFlags: false);
                _normalBuf.SyncFromRules(_normalRules);
                SetStatus("已恢复标准默认草稿，点击「保存个人配置」或「一键恢复官方默认」同步生效。", isError: false);
                RefreshModalContentsIfOpen();
            });
        NativeUguiFactory.SetLayout(btnStd.Root, minW: 196f, prefW: 204f, minH: 32f);

        _btnSaveNormal = _ui.CreateButton(
            "BtnSaveNormal",
            row.transform,
            "保存并应用个人配置",
            14f,
            NativeUguiFactory.ColGoldBtn,
            NativeUguiFactory.ColGoldBtnHover,
            Color.white,
            SaveNormalRulesAsync,
            borderColor: NativeUguiFactory.ColGold,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_btnSaveNormal.Root, minW: 186f, prefW: 196f, minH: 32f);

        _normalArchetypeGrid = BuildArchetypeTendencyCard(parent, () => _normalRules, isPreRunPrompt: false);
        _normalWidgets = BuildRuleEditorSections(
            parent,
            isTournament: false,
            () => _normalRules,
            () => _normalBuf,
            onEdited: RefreshSummaryIndicators);
    }

    private void BuildTournamentTabBody(Transform parent)
    {
        // 0. 房主与所有参赛成员共享的「个人流派倾向」卡（全模式同一套开局一次锁定规则，不写入房间公共规则）
        _tourneyPersonalArchetypeGrid = BuildArchetypeTendencyCard(
            parent,
            () => _normalRules,
            isPreRunPrompt: false,
            isPersonalSharedCard: true);

        // 1. 锦标赛房主规则本地持久化 & 3 槽位预设模板卡片
        var presetCard = _ui.CreateCard("TourneyPresetCard", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 8f);
        var pHeader = _ui.CreateHorizontalRow("PresetHeader", presetCard.transform, spacing: 8f);
        var pTitle = _ui.CreateText("Title", pHeader.transform, "锦标赛房主规则持久化与预设模板（自动记忆 + 3 槽位）", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(pTitle.gameObject, flexW: 1f, minH: 28f);

        var btnLoadLast = _ui.CreateButton(
            "BtnLoadLast",
            pHeader.transform,
            "加载上次记忆规则",
            13.5f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColCream,
            OnLoadLastTournamentRulesClicked,
            borderColor: NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btnLoadLast.Root, minW: 162f, prefW: 168f, minH: 30f);

        var btnResetTourney = _ui.CreateButton(
            "BtnResetTourney",
            pHeader.transform,
            "恢复锦标赛默认",
            13.5f,
            NativeUguiFactory.ColBtnNormal,
            NativeUguiFactory.ColBtnHover,
            NativeUguiFactory.ColCream,
            OnResetTournamentOnlyClicked,
            borderColor: NativeUguiFactory.ColDangerBorder);
        NativeUguiFactory.SetLayout(btnResetTourney.Root, minW: 132f, prefW: 140f, minH: 30f);

        _presetMemoryStatusText = _ui.CreateText("MemoryStatus", presetCard.transform, "", 13f, NativeUguiFactory.ColMuted);
        NativeUguiFactory.SetLayout(_presetMemoryStatusText.gameObject, minH: 22f);

        for (int i = 0; i < TournamentPresetStore.SlotCount; i++)
        {
            int slotIdx = i;
            var slotRow = _ui.CreateCard($"PresetSlot_{i}", presetCard.transform, NativeUguiFactory.ColSubCardBg, null, padH: 10, padV: 6, spacing: 4f);
            var h = _ui.CreateHorizontalRow("Row", slotRow.transform, spacing: 8f);

            var tag = _ui.CreateText("Tag", h.transform, $"槽位 {i + 1}", 13.5f, NativeUguiFactory.ColGold, FontStyles.Bold, wrap: false);
            NativeUguiFactory.SetLayout(tag.gameObject, minW: 52f, prefW: 54f, minH: 28f);

            var nameIn = _ui.CreateInputField("SlotName", h.transform, "输入预设名称...", $"预设方案 {i + 1}", fontSize: 13.5f, charLimit: 24);
            NativeUguiFactory.SetLayout(nameIn.gameObject, minW: 180f, prefW: 190f, minH: 28f);
            nameIn.onEndEdit.AddListener(v =>
            {
                TournamentPresetStore.RenamePresetSlot(slotIdx, v);
                RefreshPresetSlotsUi();
            });

            var sumTxt = _ui.CreateText("Summary", h.transform, "（空槽位）", 13f, NativeUguiFactory.ColMuted, wrap: false);
            NativeUguiFactory.SetLayout(sumTxt.gameObject, minW: 260f, flexW: 1f, minH: 28f);

            var btnSaveSlot = _ui.CreateButton(
                "BtnSaveSlot",
                h.transform,
                "存入当前",
                13f,
                NativeUguiFactory.ColGoldBtn,
                NativeUguiFactory.ColGoldBtnHover,
                Color.white,
                () => OnSaveTournamentPresetSlot(slotIdx),
                fontStyle: FontStyles.Bold);
            NativeUguiFactory.SetLayout(btnSaveSlot.Root, minW: 88f, prefW: 92f, minH: 28f);

            var btnLoadSlot = _ui.CreateButton(
                "BtnLoadSlot",
                h.transform,
                "载入并应用",
                13f,
                NativeUguiFactory.ColBtnNormal,
                NativeUguiFactory.ColBtnHover,
                NativeUguiFactory.ColCream,
                () => OnLoadTournamentPresetSlot(slotIdx));
            NativeUguiFactory.SetLayout(btnLoadSlot.Root, minW: 96f, prefW: 102f, minH: 28f);

            var btnClearSlot = _ui.CreateButton(
                "BtnClearSlot",
                h.transform,
                "清空",
                13f,
                NativeUguiFactory.ColBtnNormal,
                NativeUguiFactory.ColBtnHover,
                NativeUguiFactory.ColMuted,
                () => OnClearTournamentPresetSlot(slotIdx));
            NativeUguiFactory.SetLayout(btnClearSlot.Root, minW: 60f, prefW: 64f, minH: 28f);

            _presetSlots[i] = new PresetSlotWidgets
            {
                SlotIndex = i,
                NameInput = nameIn,
                SummaryText = sumTxt,
                SaveBtn = btnSaveSlot,
                LoadBtn = btnLoadSlot,
                ClearBtn = btnClearSlot,
            };
        }

        // 2. 未进入房间引导卡片
        _tourneyNoLobbyCard = _ui.CreateCard("TourneyNoLobbyCard", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 12, spacing: 8f);
        _ui.CreateText("Title", _tourneyNoLobbyCard.transform, "未检测到当前锦标赛房间", 16f, NativeUguiFactory.ColGold, FontStyles.Bold);
        _ui.CreateText("Desc", _tourneyNoLobbyCard.transform,
            "请先在游戏主界面选择「锦标赛」创建房间或通过房间码加入。房主建房/入厅时将自动加载最近记忆规则并同步到全房。",
            14f, NativeUguiFactory.ColCream);
        var detectBtn = _ui.CreateButton(
            "BtnDetectLobby",
            _tourneyNoLobbyCard.transform,
            "立即检测所在锦标赛房间",
            14f,
            NativeUguiFactory.ColGoldBtn,
            NativeUguiFactory.ColGoldBtnHover,
            Color.white,
            () => RefreshTournamentLobbyAsync(forceOverwriteHostDraft: true),
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(detectBtn.Root, minW: 224f, prefW: 230f, minH: 34f);

        // 3. 房间详情与房主编辑 / 成员只读公示区
        _tourneyLobbyActiveCard = _ui.CreateVerticalGroup("TourneyLobbyActive", parent, spacing: 8f);

        var infoBar = _ui.CreateCard("LobbyInfoBar", _tourneyLobbyActiveCard.transform, NativeUguiFactory.ColSubCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 8);
        var infoRow = _ui.CreateHorizontalRow("Row", infoBar.transform, spacing: 8f);
        _tourneyLobbyHeaderText = _ui.CreateText("LobbyHeader", infoRow.transform, "", 14.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(_tourneyLobbyHeaderText.gameObject, flexW: 1f, minH: 30f);

        _btnSaveLobbyHeader = _ui.CreateButton(
            "BtnSaveLobbyHeader",
            infoRow.transform,
            "保存并广播规则到全房",
            14f,
            NativeUguiFactory.ColGoldBtn,
            NativeUguiFactory.ColGoldBtnHover,
            Color.white,
            SaveLobbyRulesAsync,
            borderColor: NativeUguiFactory.ColGold,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_btnSaveLobbyHeader.Root, minW: 214f, prefW: 220f, minH: 32f);

        // 房间本场 Mod 横幅（房主与所有成员均可见，支持一键同步/全部下载到本地）
        BuildTournamentRoomModBanner(_tourneyLobbyActiveCard.transform);

        // 房主编辑体
        _tourneyHostBody = _ui.CreateVerticalGroup("TourneyHostBody", _tourneyLobbyActiveCard.transform, spacing: 8f);
        BuildTournamentHostControls(_tourneyHostBody.transform);
        _lobbyWidgets = BuildRuleEditorSections(
            _tourneyHostBody.transform,
            isTournament: true,
            () => _lobbyRules,
            () => _lobbyBuf,
            onEdited: () =>
            {
                _lobbyDirty = true;
                RefreshSummaryIndicators();
            });

        var bottomSaveRow = _ui.CreateHorizontalRow("BottomSaveRow", _tourneyHostBody.transform, spacing: 8f);
        var sp = new GameObject("Spacer", typeof(RectTransform));
        sp.transform.SetParent(bottomSaveRow.transform, worldPositionStays: false);
        NativeUguiFactory.SetLayout(sp, flexW: 1f);
        _btnSaveLobbyBottom = _ui.CreateButton(
            "BtnSaveLobbyBottom",
            bottomSaveRow.transform,
            "保存并广播规则到全房",
            15f,
            NativeUguiFactory.ColGoldBtn,
            NativeUguiFactory.ColGoldBtnHover,
            Color.white,
            SaveLobbyRulesAsync,
            borderColor: NativeUguiFactory.ColGold,
            fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_btnSaveLobbyBottom.Root, minW: 236f, prefW: 244f, minH: 38f);

        // 成员只读公示体
        _tourneyMemberBody = _ui.CreateCard("TourneyMemberBody", _tourneyLobbyActiveCard.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 12, spacing: 6f);
        _ui.CreateText("NoticeTitle", _tourneyMemberBody.transform, "《本场锦标赛房主规则公示》（每 2.5 秒自动同步，仅房主可修改）", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        _tourneyMemberSummaryText = _ui.CreateText("MemberSummary", _tourneyMemberBody.transform, "", 14f, NativeUguiFactory.ColCream);
        NativeUguiFactory.SetLayout(_tourneyMemberSummaryText.gameObject, minH: 120f);
        var memberActionRow = _ui.CreateHorizontalRow("MemberModActions", _tourneyMemberBody.transform, spacing: 8f);
        var btnRetrySync = _ui.CreateButton("RetrySyncRoomMods", memberActionRow.transform, "重试同步房间 Mod", 13.5f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () => SyncCurrentLobbyRequiredModsAsync(force: true));
        NativeUguiFactory.SetLayout(btnRetrySync.Root, minW: 156f, prefW: 170f, minH: 30f);
    }

    private void BuildTournamentHostControls(Transform parent)
    {
        var capCard = _ui.CreateCard("LobbyCapCard", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 6f);
        _ui.CreateText("Title", capCard.transform, "房主大厅容量与机器人管理", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold);

        var row = _ui.CreateHorizontalRow("CapAndBots", capCard.transform, spacing: 6f);
        var capLbl = _ui.CreateText("CapLbl", row.transform, "赛制容量：", 14f, NativeUguiFactory.ColCream, wrap: false);
        NativeUguiFactory.SetLayout(capLbl.gameObject, minW: 78f, prefW: 80f, minH: 30f);

        _btnCap8 = _ui.CreateButton("Cap8", row.transform, "8人标准", 13.5f, NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
        {
            _lobbyRules.PlayerCap = 8;
            _lobbyDirty = true;
            RefreshModalContentsIfOpen();
        }, fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_btnCap8.Root, minW: 96f, prefW: 102f, minH: 30f);

        _btnCap10 = _ui.CreateButton("Cap10", row.transform, "10人缓冲", 13.5f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            _lobbyRules.PlayerCap = 10;
            _lobbyDirty = true;
            RefreshModalContentsIfOpen();
        }, fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(_btnCap10.Root, minW: 96f, prefW: 102f, minH: 30f);

        // D6-P1-02：补齐 2..16 人数微调按钮（不限制为 8/10 死值）
        var btnCapDec = _ui.CreateButton("CapDec", row.transform, "-1人", 12.5f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            _lobbyRules.PlayerCap = Math.Clamp(_lobbyRules.PlayerCap - 1, 2, 16);
            _lobbyDirty = true;
            RefreshModalContentsIfOpen();
        }, padH: 4, padV: 2);
        NativeUguiFactory.SetLayout(btnCapDec.Root, minW: 48f, prefW: 52f, minH: 30f);

        var btnCapInc = _ui.CreateButton("CapInc", row.transform, "+1人", 12.5f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            _lobbyRules.PlayerCap = Math.Clamp(_lobbyRules.PlayerCap + 1, 2, 16);
            _lobbyDirty = true;
            RefreshModalContentsIfOpen();
        }, padH: 4, padV: 2);
        NativeUguiFactory.SetLayout(btnCapInc.Root, minW: 48f, prefW: 52f, minH: 30f);

        var botLbl = _ui.CreateText("BotLbl", row.transform, "   机器人：", 14f, NativeUguiFactory.ColCream, wrap: false);
        NativeUguiFactory.SetLayout(botLbl.gameObject, minW: 86f, prefW: 88f, minH: 30f);

        void AddBotBtn(string label, string action, bool primary = false, float width = 94f)
        {
            var b = _ui.CreateButton(
                "Bot_" + action,
                row.transform,
                label,
                13.5f,
                primary ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
                primary ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover,
                primary ? Color.white : NativeUguiFactory.ColCream,
                () => SendLobbyBotActionAsync(action),
                fontStyle: primary ? FontStyles.Bold : FontStyles.Normal);
            NativeUguiFactory.SetLayout(b.Root, minW: width, prefW: width + 4f, minH: 30f);
        }

        AddBotBtn("+1 机器人", "add");
        AddBotBtn("一键补满", "fill", primary: true, width: 104f);
        AddBotBtn("-1 机器人", "remove");
        AddBotBtn("清空机器人", "clear", width: 102f);

        _tourneyHostMembersText = _ui.CreateText("Members", capCard.transform, "", 13f, NativeUguiFactory.ColMuted);

        var swCard = _ui.CreateCard("TourneyCoreSwitches", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 6f);
        _ui.CreateText("Title", swCard.transform, "锦标赛核心赛制开关", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold);

        _togBotLock = _ui.CreateToggle("BotLock", swCard.transform, "机器人锁 1 声望不致死（防止早期意外淘汰导致真人轮空）", true, v =>
        {
            _lobbyRules.BotNonLethalPrestigeLock = v;
            _lobbyDirty = true;
            RefreshSummaryIndicators();
        });
        _togMatch4 = _ui.CreateToggle("Match4", swCard.transform, "全随机 + 4 轮内必遇真人（保证对局内真人交手频率）", true, v =>
        {
            _lobbyRules.HumanMatchEvery4Rounds = v;
            _lobbyDirty = true;
            RefreshSummaryIndicators();
        });
        _togLastHuman = _ui.CreateToggle("LastHuman", swCard.transform, "真人决战到底吃鸡（最后存活真人夺冠结算）", true, v =>
        {
            _lobbyRules.LastHumanStandingMode = v;
            _lobbyDirty = true;
            RefreshSummaryIndicators();
        });
        _togAllowTendency = _ui.CreateToggle("AllowTendency", swCard.transform, "允许选手自选 0-3 流派倾向（仅合法卡池内倍率加权，绝不虚空造卡）", true, v =>
        {
            _lobbyRules.AllowTendencySelection = v;
            _lobbyDirty = true;
            RefreshSummaryIndicators();
        });

        // 房主选择本房使用的社区/服务器 Mod
        BuildTournamentHostModPickerCard(parent);
    }

    // ── 共享规则区块构建器（流派倾向 / 野怪 / 开局数值 / 固定卡牌 / 事件倍率）──

    private ArchetypeGridWidgets BuildArchetypeTendencyCard(
        Transform parent, Func<CustomRunRuleSet> getRules, bool isPreRunPrompt, bool isPersonalSharedCard = false)
    {
        var card = _ui.CreateCard("ArchetypeCard", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 6f);
        var w = new ArchetypeGridWidgets { GetRules = getRules, IsPersonalSharedCard = isPersonalSharedCard };

        var top = _ui.CreateHorizontalRow("Header", card.transform, spacing: 8f);
        w.HeaderText = _ui.CreateText("Title", top.transform, "流派倾向多选（0-3 项）", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(w.HeaderText.gameObject, flexW: 1f, minH: 26f);

        if (!isPreRunPrompt)
        {
            w.LockNowBtn = _ui.CreateButton("BtnLockOpeningArch", top.transform, "确认锁定本局", 13f,
                NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
                {
                    var r = getRules();
                    LockOpeningTendencyOnActiveRunAsync(r?.SelectedArchetypeMask ?? 0UL, closePromptAfter: true);
                }, fontStyle: FontStyles.Bold);
            NativeUguiFactory.SetLayout(w.LockNowBtn.Root, minW: 118f, prefW: 124f, minH: 26f);
            w.LockNowBtn.Root.SetActive(false);
        }

        w.ClearBtn = _ui.CreateButton("BtnClearArch", top.transform, "清空倾向", 13f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            var r = getRules();
            if (r != null) r.SelectedArchetypeMask = 0UL;
            if (!isPreRunPrompt && !IsOpeningEditableInRun())
            {
                SchedulePersonalTendencySync();
            }
            RefreshArchetypeGrid(w, getRules());
            RefreshSummaryIndicators();
        });
        NativeUguiFactory.SetLayout(w.ClearBtn.Root, minW: 88f, prefW: 92f, minH: 26f);

        w.DescText = _ui.CreateText("Desc", card.transform,
            isPersonalSharedCard
                ? "说明：仅自己生效（不影响房间他人），在合法卡池内概率 ×2（多标封顶 ×3）；进局锁定或离开第 1 小时后途中严禁修改。"
                : "说明：全模式共用，在合法卡池内概率 ×2（多标叠加封顶 ×3）；进局锁定或离开第 1 小时后途中严禁修改。",
            13f, NativeUguiFactory.ColMuted);

        if (!isPreRunPrompt)
        {
            w.PromptBeforeRunToggle = _ui.CreateToggle("PromptBeforeRun", card.transform,
                "进局 Day 1 Hour 1 自动弹窗确认本局流派倾向（仅限开场确认锁定 1 次，途中不可更改）",
                true,
                v =>
                {
                    var r = getRules();
                    if (r != null) r.PromptTendencyBeforeRun = v;
                    if (_normalRules != null)
                    {
                        _normalRules.PromptTendencyBeforeRun = v;
                        _normalBuf?.SyncFromRules(_normalRules);
                        PersistMigratedDefaultRulesAsync(_normalRules.Clone());
                    }
                    SyncPromptBeforeRunToggles(v);
                },
                fontSize: 13.5f);
        }

        var gridGo = new GameObject("Grid", typeof(RectTransform));
        gridGo.transform.SetParent(card.transform, worldPositionStays: false);
        var glg = gridGo.AddComponent<GridLayoutGroup>();
        glg.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        glg.constraintCount = 3;
        glg.cellSize = new Vector2(isPreRunPrompt ? 364f : 382f, 48f);
        glg.spacing = new Vector2(8f, 6f);
        glg.childAlignment = TextAnchor.UpperLeft;
        w.GridContainer = gridGo.transform;

        return w;
    }

    private RuleEditorWidgets BuildRuleEditorSections(
        Transform parent,
        bool isTournament,
        Func<CustomRunRuleSet> getRules,
        Func<RuleEditorBuffer> getBuf,
        Action onEdited)
    {
        var w = new RuleEditorWidgets
        {
            IsTournament = isTournament,
            GetRules = getRules,
            GetBuf = getBuf,
        };

        // 1. 野怪战利品全池自选开关（默认开启）
        var lootCard = _ui.CreateCard("MonsterLootCard", parent, NativeUguiFactory.ColAmberBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 4f);
        w.MonsterLootToggle = _ui.CreateToggle(
            "MonsterLootToggle",
            lootCard.transform,
            "开启野怪战利品全池任选 1 件（击败后 10 格分页 + 0 金币翻页，默认开启）",
            true,
            v =>
            {
                var r = getRules();
                if (r != null) r.MonsterLootChooseOne = v;
                onEdited?.Invoke();
            },
            fontSize: 15f);
        _ui.CreateText("LootHint", lootCard.transform,
            "提示：已默认开启（共享标准镜像池）；取消勾选后恢复官方原版从野怪专属战利品中随机掉落 1 件。",
            13f, NativeUguiFactory.ColMuted);

        // 2. 开局数值预设
        var statsCard = _ui.CreateCard("StartingStatsCard", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 6f);
        var sHeader = _ui.CreateHorizontalRow("Header", statsCard.transform, spacing: 8f);
        var sTitle = _ui.CreateText("Title", sHeader.transform, "开局数值预设", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(sTitle.gameObject, flexW: 1f, minH: 26f);

        var btnResetStats = _ui.CreateButton("BtnResetStats", sHeader.transform, "恢复全部默认规则", 13f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                var r = getRules();
                var b = getBuf();
                if (r != null)
                {
                    // D6-P1-02：统一调用 ResetToDefaults()，杜绝部分字段漏清零导致隐形倍率/词条残留
                    r.ResetToDefaults();
                    b?.SyncFromRules(r);
                    if (!isTournament)
                    {
                        Modules.NativeMenuEmbedModule.Instance?.SyncSelectedQuickModeFromRules(r.SpecialModeId, r.PresetId);
                    }
                }
                SyncEditorWidgets(w, getRules(), getBuf());
                onEdited?.Invoke();
            });
        NativeUguiFactory.SetLayout(btnResetStats.Root, minW: 168f, prefW: 176f, minH: 26f);

        var statsRow = _ui.CreateHorizontalRow("StatsRow", statsCard.transform, spacing: 8f);
        w.GoldInput = BuildStatCell(statsRow.transform, "初始金币", 0, 9999, 5, () => getRules().StartingGold, v =>
        {
            getRules().StartingGold = v;
            getBuf().GoldText = v.ToString();
        }, onEdited);
        w.IncomeInput = BuildStatCell(statsRow.transform, "初始收入", 0, 999, 1, () => getRules().StartingIncome, v =>
        {
            getRules().StartingIncome = v;
            getBuf().IncomeText = v.ToString();
        }, onEdited);
        w.ExpInput = BuildStatCell(statsRow.transform, "初始经验", 0, 999, 1, () => getRules().StartingExperience, v =>
        {
            getRules().StartingExperience = v;
            getBuf().ExperienceText = v.ToString();
        }, onEdited);
        w.PrestigeInput = BuildStatCell(statsRow.transform, "初始声望", 1, 999, 5, () => getRules().StartingPrestige, v =>
        {
            getRules().StartingPrestige = v;
            getBuf().PrestigeText = v.ToString();
        }, onEdited);
        w.HealthInput = BuildStatCell(statsRow.transform, "最大生命", 50, 99999, 50, () => getRules().StartingHealth, v =>
        {
            getRules().StartingHealth = v;
            getBuf().HealthText = v.ToString();
        }, onEdited);

        // 3. 开局固定物品与固定技能（中文检索）
        var cardsCard = _ui.CreateCard("StartingCardsCard", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 8f);
        _ui.CreateText("Title", cardsCard.transform, "开局固定物品与固定技能（中文检索）", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold);

        var splitRow = _ui.CreateHorizontalRow("SplitCols", cardsCard.transform, spacing: 12f);
        splitRow.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.UpperLeft;

        // 左列：物品
        var leftCol = _ui.CreateCard("ItemCol", splitRow.transform, NativeUguiFactory.ColSubCardBg, null, padH: 10, padV: 8, spacing: 6f);
        NativeUguiFactory.SetLayout(leftCol, minW: 540f, flexW: 1f);
        w.SelectedItemsHeader = _ui.CreateText("Hdr", leftCol.transform, "开局固定物品（已选 0 件）", 14.5f, NativeUguiFactory.ColGold, FontStyles.Bold);

        var itemSearchRow = _ui.CreateHorizontalRow("SearchRow", leftCol.transform, spacing: 6f);
        w.ItemSearchInput = _ui.CreateInputField("ItemSearch", itemSearchRow.transform, "输入中文名或ID检索物品...", "", 13.5f, 40);
        NativeUguiFactory.SetLayout(w.ItemSearchInput.gameObject, minW: 260f, flexW: 1f, minH: 28f);
        w.ItemSearchInput.onValueChanged.AddListener(val =>
        {
            getBuf().ItemSearch = val ?? "";
            RebuildCardMatches(w, isSkill: false, getRules(), getBuf(), onEdited);
        });
        w.ItemHeroOnlyToggle = _ui.CreateToggle("ItemHeroOnly", itemSearchRow.transform, "仅当前英雄/通用", true, val =>
        {
            getBuf().ItemHeroFilterOnly = val;
            RebuildCardMatches(w, isSkill: false, getRules(), getBuf(), onEdited);
        }, fontSize: 13f);
        NativeUguiFactory.SetLayout(w.ItemHeroOnlyToggle.Root, minW: 158f, prefW: 162f, minH: 28f);

        var itemTierRow = _ui.CreateHorizontalRow("TierRow", leftCol.transform, spacing: 4f);
        var itLbl = _ui.CreateText("Lbl", itemTierRow.transform, "品质：", 13.5f, NativeUguiFactory.ColCream, wrap: false);
        NativeUguiFactory.SetLayout(itLbl.gameObject, minW: 46f, prefW: 46f, minH: 26f);
        for (int t = 0; t < Tiers.Length; t++)
        {
            int tierIdx = t;
            var tb = _ui.CreateButton("Tier_" + Tiers[t], itemTierRow.transform, TierLabelsZh[t], 12.5f,
                NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
                {
                    getBuf().ItemTierIdx = tierIdx;
                    RefreshTierButtons(w.ItemTierButtons, tierIdx);
                }, padH: 6, padV: 2);
            NativeUguiFactory.SetLayout(tb.Root, minW: 88f, flexW: 1f, minH: 26f);
            w.ItemTierButtons[t] = tb;
        }

        var enchRow = _ui.CreateHorizontalRow("EnchRow", leftCol.transform, spacing: 6f);
        var eLbl = _ui.CreateText("Lbl", enchRow.transform, "附魔：", 13.5f, NativeUguiFactory.ColCream, wrap: false);
        NativeUguiFactory.SetLayout(eLbl.gameObject, minW: 46f, prefW: 46f, minH: 26f);
        var btnPrevE = _ui.CreateButton("PrevE", enchRow.transform, "<", 13f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            var b = getBuf();
            b.ItemEnchantIdx = (b.ItemEnchantIdx - 1 + Enchantments.Length) % Enchantments.Length;
            w.ItemEnchantLabel.text = Enchantments[b.ItemEnchantIdx].LabelZh;
        }, padH: 4, padV: 2);
        NativeUguiFactory.SetLayout(btnPrevE.Root, minW: 32f, prefW: 34f, minH: 26f);

        w.ItemEnchantLabel = _ui.CreateText("EnchVal", enchRow.transform, Enchantments[0].LabelZh, 13.5f, NativeUguiFactory.ColGold, FontStyles.Bold, TextAlignmentOptions.Center, wrap: false);
        NativeUguiFactory.SetLayout(w.ItemEnchantLabel.gameObject, minW: 150f, flexW: 1f, minH: 26f);

        var btnNextE = _ui.CreateButton("NextE", enchRow.transform, ">", 13f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            var b = getBuf();
            b.ItemEnchantIdx = (b.ItemEnchantIdx + 1) % Enchantments.Length;
            w.ItemEnchantLabel.text = Enchantments[b.ItemEnchantIdx].LabelZh;
        }, padH: 4, padV: 2);
        NativeUguiFactory.SetLayout(btnNextE.Root, minW: 32f, prefW: 34f, minH: 26f);

        var btnClearE = _ui.CreateButton("ClrE", enchRow.transform, "清除附魔", 12.5f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            getBuf().ItemEnchantIdx = 0;
            w.ItemEnchantLabel.text = Enchantments[0].LabelZh;
        }, padH: 6, padV: 2);
        NativeUguiFactory.SetLayout(btnClearE.Root, minW: 80f, prefW: 84f, minH: 26f);

        var itemMatchesGo = _ui.CreateVerticalGroup("ItemMatches", leftCol.transform, spacing: 4f);
        w.ItemMatchesContainer = itemMatchesGo.transform;
        var selItemsGo = _ui.CreateVerticalGroup("SelectedItems", leftCol.transform, spacing: 4f);
        w.SelectedItemsContainer = selItemsGo.transform;

        // 右列：技能
        var rightCol = _ui.CreateCard("SkillCol", splitRow.transform, NativeUguiFactory.ColSubCardBg, null, padH: 10, padV: 8, spacing: 6f);
        NativeUguiFactory.SetLayout(rightCol, minW: 540f, flexW: 1f);
        w.SelectedSkillsHeader = _ui.CreateText("Hdr", rightCol.transform, "开局固定技能（已选 0 个）", 14.5f, NativeUguiFactory.ColGold, FontStyles.Bold);

        var skillSearchRow = _ui.CreateHorizontalRow("SearchRow", rightCol.transform, spacing: 6f);
        w.SkillSearchInput = _ui.CreateInputField("SkillSearch", skillSearchRow.transform, "输入中文名或ID检索技能...", "", 13.5f, 40);
        NativeUguiFactory.SetLayout(w.SkillSearchInput.gameObject, minW: 260f, flexW: 1f, minH: 28f);
        w.SkillSearchInput.onValueChanged.AddListener(val =>
        {
            getBuf().SkillSearch = val ?? "";
            RebuildCardMatches(w, isSkill: true, getRules(), getBuf(), onEdited);
        });
        w.SkillHeroOnlyToggle = _ui.CreateToggle("SkillHeroOnly", skillSearchRow.transform, "仅当前英雄/通用", true, val =>
        {
            getBuf().SkillHeroFilterOnly = val;
            RebuildCardMatches(w, isSkill: true, getRules(), getBuf(), onEdited);
        }, fontSize: 13f);
        NativeUguiFactory.SetLayout(w.SkillHeroOnlyToggle.Root, minW: 158f, prefW: 162f, minH: 28f);

        var skillTierRow = _ui.CreateHorizontalRow("TierRow", rightCol.transform, spacing: 4f);
        var stLbl = _ui.CreateText("Lbl", skillTierRow.transform, "品质：", 13.5f, NativeUguiFactory.ColCream, wrap: false);
        NativeUguiFactory.SetLayout(stLbl.gameObject, minW: 46f, prefW: 46f, minH: 26f);
        for (int t = 0; t < Tiers.Length; t++)
        {
            int tierIdx = t;
            var tb = _ui.CreateButton("Tier_" + Tiers[t], skillTierRow.transform, TierLabelsZh[t], 12.5f,
                NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
                {
                    getBuf().SkillTierIdx = tierIdx;
                    RefreshTierButtons(w.SkillTierButtons, tierIdx);
                }, padH: 6, padV: 2);
            NativeUguiFactory.SetLayout(tb.Root, minW: 88f, flexW: 1f, minH: 26f);
            w.SkillTierButtons[t] = tb;
        }

        var skillMatchesGo = _ui.CreateVerticalGroup("SkillMatches", rightCol.transform, spacing: 4f);
        w.SkillMatchesContainer = skillMatchesGo.transform;
        var selSkillsGo = _ui.CreateVerticalGroup("SelectedSkills", rightCol.transform, spacing: 4f);
        w.SelectedSkillsContainer = selSkillsGo.transform;

        // 4. 全局事件分类倍率（0x ~ 5x）
        var multCard = _ui.CreateCard("MultipliersCard", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 6f);
        var mHeader = _ui.CreateHorizontalRow("Header", multCard.transform, spacing: 8f);
        var mTitle = _ui.CreateText("Title", mHeader.transform, "全局事件分类倍率（按官方 4 大类分组，0x 禁用 ~ 5x）", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(mTitle.gameObject, flexW: 1f, minH: 26f);

        var btnResetMults = _ui.CreateButton("BtnResetMults", mHeader.transform, "全部恢复 1.0x", 13f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                var r = getRules();
                if (r != null)
                {
                    r.EnsureCollections();
                    foreach (var cat in GetActiveCategories())
                    {
                        r.EncounterCategoryMultipliers[cat.Key] = 1.0;
                    }
                    SyncMultipliersUi(w, r);
                    onEdited?.Invoke();
                }
            });
        NativeUguiFactory.SetLayout(btnResetMults.Root, minW: 126f, prefW: 132f, minH: 26f);

        var multBody = _ui.CreateVerticalGroup("MultGroups", multCard.transform, spacing: 6f);
        w.MultipliersGroupContainer = multBody.transform;

        // 5. D6-P1-01/02/03：Wave 2/3 模式预设、动态肉鸽词条与全部 21 项进阶规则调参区
        BuildWave23AdvancedControlsCard(parent, w, getRules, getBuf, onEdited);

        return w;
    }

    private void BuildWave23AdvancedControlsCard(
        Transform parent,
        RuleEditorWidgets w,
        Func<CustomRunRuleSet> getRules,
        Func<RuleEditorBuffer> getBuf,
        Action onEdited)
    {
        var card = _ui.CreateCard("Wave23AdvancedCard", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 6f);
        var hdr = _ui.CreateHorizontalRow("Header", card.transform, spacing: 8f);
        var title = _ui.CreateText("Title", hdr.transform, "进阶玩法模式 / 肉鸽词条 / 经济与掉率调参（21 项全参数）", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(title.gameObject, flexW: 1f, minH: 24f);
        w.ActiveModePresetLabel = _ui.CreateText("ActiveMode", hdr.transform, "经典官方", 13f, NativeUguiFactory.ColEmeraldText, FontStyles.Bold, TextAlignmentOptions.MidlineRight, wrap: false);
        NativeUguiFactory.SetLayout(w.ActiveModePresetLabel.gameObject, minW: 160f, prefW: 200f, minH: 24f);

        // 快捷预设/模式选择排（拆分每日挑战与肉鸽词条为独立按钮）
        var modeRow = _ui.CreateHorizontalRow("ModePresetRow", card.transform, spacing: 4f);
        var quickButtons = new (string Label, string PresetId, string ModeId)[]
        {
            ("经典官方", "PureOfficial", "Standard"),
            ("爽玩增强", "EnhancedFun", "Standard"),
            ("极限肉鸽", "ExtremeRoguelike", "Standard"),
            ("无尽爬塔", "PureOfficial", "EndlessTower"),
            ("跨职业", "PureOfficial", "CrossHeroMayhem"),
            ("每日挑战", "PureOfficial", "DailyChallenge"),
            ("肉鸽词条", "ExtremeRoguelike", "MutatorChallenge"),
            ("Boss车轮", "PureOfficial", "BossRush"),
            ("沙盒实验", "PureOfficial", "SandboxLab"),
        };
        for (int i = 0; i < quickButtons.Length; i++)
        {
            var qb = quickButtons[i];
            var btn = _ui.CreateButton("ModeBtn_" + i, modeRow.transform, qb.Label, 11.5f,
                NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
                {
                    var r = getRules();
                    if (r == null) return;
                    r.ApplyLocalPresetDefaults(qb.PresetId, qb.ModeId);
                    if (!w.IsTournament)
                    {
                        Modules.NativeMenuEmbedModule.Instance?.SyncSelectedQuickModeFromRules(r.SpecialModeId, r.PresetId);
                    }
                    SyncEditorWidgets(w, r, getBuf());
                    onEdited?.Invoke();
                }, padH: 4, padV: 2);
            NativeUguiFactory.SetLayout(btn.Root, minW: 78f, flexW: 1f, minH: 26f);
        }

        // 第一行数值：经济与掉率（商店高阶概率 / 商店价格倍率 / 野怪额外选 / 野怪品阶加成 / 敌人数值倍率）
        var econRow = _ui.CreateHorizontalRow("EconRow", card.transform, spacing: 6f);
        w.ShopHighTierInput = BuildDoubleStatCell(econRow.transform, "高阶率加成%", -50.0, 100.0, 5.0, "0.#", () => getRules().ShopHighTierBonusPct, v => getRules().ShopHighTierBonusPct = v, onEdited);
        w.ShopPriceMultInput = BuildDoubleStatCell(econRow.transform, "商店价格倍率", 0.1, 5.0, 0.1, "0.##", () => getRules().ShopPriceMultiplier, v => getRules().ShopPriceMultiplier = v, onEdited);
        w.LootBonusChoicesInput = BuildStatCell(econRow.transform, "野怪额外选", 0, 5, 1, () => getRules().MonsterLootBonusChoices, v => getRules().MonsterLootBonusChoices = v, onEdited);
        w.LootTierBonusInput = BuildStatCell(econRow.transform, "战利品升阶", 0, 3, 1, () => getRules().MonsterLootTierBonus, v => getRules().MonsterLootTierBonus = v, onEdited);
        w.EnemyStatMultInput = BuildDoubleStatCell(econRow.transform, "敌人强度倍率", 0.25, 5.0, 0.1, "0.##", () => getRules().EnemyStatMultiplier, v => getRules().EnemyStatMultiplier = v, onEdited);

        // 第二行数值：模式专参 & 流派权重 & 固定种子
        var modeNumRow = _ui.CreateHorizontalRow("ModeNumRow", card.transform, spacing: 6f);
        w.EndlessMaxDayInput = BuildStatCell(modeNumRow.transform, "无尽最大天", 15, 999, 5, () => getRules().EndlessMaxDay, v => getRules().EndlessMaxDay = v, onEdited);
        w.CrossHeroBiasInput = BuildDoubleStatCell(modeNumRow.transform, "本职业权重", 0.0, 1.0, 0.05, "0.##", () => getRules().CrossHeroOwnHeroBias, v => getRules().CrossHeroOwnHeroBias = v, onEdited);
        w.BossRushWavesInput = BuildStatCell(modeNumRow.transform, "Boss总波次", 5, 30, 1, () => getRules().BossRushTotalWaves, v => getRules().BossRushTotalWaves = v, onEdited);
        w.ArchetypeWeightInput = BuildDoubleStatCell(modeNumRow.transform, "流派权重倍率", 1.0, 10.0, 0.5, "0.#", () => getRules().ArchetypeWeightMultiplier, v => getRules().ArchetypeWeightMultiplier = v, onEdited);
        w.ArchetypeCapInput = BuildDoubleStatCell(modeNumRow.transform, "多流派上限", 1.0, 10.0, 0.5, "0.#", () => getRules().ArchetypeMultiMatchCap, v => getRules().ArchetypeMultiMatchCap = v, onEdited);

        // 第三行开关与固定种子
        var flagRow = _ui.CreateHorizontalRow("ModeFlagsRow", card.transform, spacing: 8f);
        w.DisableFatesReviveToggle = _ui.CreateToggle("TogNoRevive", flagRow.transform, "禁用命运复活", false, v => { getRules().DisableFatesRevive = v; onEdited?.Invoke(); }, fontSize: 12.5f);
        w.BossRushPvpToggle = _ui.CreateToggle("TogBossPvp", flagRow.transform, "Boss战含里程碑PvP", true, v => { getRules().BossRushIncludeMilestonePvp = v; onEdited?.Invoke(); }, fontSize: 12.5f);
        w.SandboxRerollToggle = _ui.CreateToggle("TogSbReroll", flagRow.transform, "沙盒免费刷新", true, v => { getRules().SandboxFreeInfiniteReroll = v; onEdited?.Invoke(); }, fontSize: 12.5f);
        w.SandboxZeroPriceToggle = _ui.CreateToggle("TogSbZero", flagRow.transform, "沙盒0金商店", false, v => { getRules().SandboxZeroShopPrice = v; onEdited?.Invoke(); }, fontSize: 12.5f);

        w.FixedSeedInput = _ui.CreateInputField("FixedSeed", flagRow.transform, "固定种子(可空)", "", 12.5f, 12, TMP_InputField.ContentType.IntegerNumber);
        NativeUguiFactory.SetLayout(w.FixedSeedInput.gameObject, minW: 128f, prefW: 140f, minH: 26f);
        w.FixedSeedInput.onEndEdit.AddListener(txt =>
        {
            var r = getRules();
            if (r == null) return;
            r.FixedRunSeed = int.TryParse(txt?.Trim(), out int s) ? s : (int?)null;
            onEdited?.Invoke();
        });

        // 第四行：来自 /custom-rules/catalog 的动态肉鸽突变词条勾选区
        _ui.CreateText("MutTitle", card.transform, "肉鸽突变词条（来自服务端规则目录，点击开关并自动避让冲突组）", 12.5f, NativeUguiFactory.ColMuted);
        var mutBox = _ui.CreateVerticalGroup("MutatorsBox", card.transform, spacing: 4f);
        w.MutatorsContainer = mutBox.transform;
    }

    private TMP_InputField BuildDoubleStatCell(
        Transform parent,
        string label,
        double min,
        double max,
        double step,
        string fmt,
        Func<double> getter,
        Action<double> setter,
        Action onEdited)
    {
        var cell = _ui.CreateCard("DStat_" + label, parent, NativeUguiFactory.ColSubCardBg, null, padH: 6, padV: 5, spacing: 3f);
        NativeUguiFactory.SetLayout(cell, minW: 160f, flexW: 1f);
        _ui.CreateText("Label", cell.transform, label, 12f, NativeUguiFactory.ColMuted, wrap: false);

        var row = _ui.CreateHorizontalRow("InputRow", cell.transform, spacing: 3f);
        TMP_InputField input = null;

        var btnMinus = _ui.CreateButton("Minus", row.transform, "-", 14f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            double next = Math.Round(Math.Clamp(getter() - step, min, max), 2);
            setter(next);
            if (input != null) input.SetTextWithoutNotify(next.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture));
            onEdited?.Invoke();
        }, fontStyle: FontStyles.Bold, padH: 4, padV: 2);
        NativeUguiFactory.SetLayout(btnMinus.Root, minW: 26f, prefW: 26f, minH: 26f);

        input = _ui.CreateInputField("Val", row.transform, "", getter().ToString(fmt, System.Globalization.CultureInfo.InvariantCulture), 13f, 8);
        NativeUguiFactory.SetLayout(input.gameObject, minW: 60f, flexW: 1f, minH: 26f);
        input.onEndEdit.AddListener(txt =>
        {
            double cur = getter();
            if (double.TryParse(txt?.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            {
                double clamped = Math.Round(Math.Clamp(parsed, min, max), 2);
                setter(clamped);
                input.SetTextWithoutNotify(clamped.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                input.SetTextWithoutNotify(cur.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture));
            }
            onEdited?.Invoke();
        });

        var btnPlus = _ui.CreateButton("Plus", row.transform, "+", 14f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            double next = Math.Round(Math.Clamp(getter() + step, min, max), 2);
            setter(next);
            if (input != null) input.SetTextWithoutNotify(next.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture));
            onEdited?.Invoke();
        }, fontStyle: FontStyles.Bold, padH: 4, padV: 2);
        NativeUguiFactory.SetLayout(btnPlus.Root, minW: 26f, prefW: 26f, minH: 26f);

        return input;
    }

    private TMP_InputField BuildStatCell(
        Transform parent,
        string label,
        int min,
        int max,
        int step,
        Func<int> getter,
        Action<int> setter,
        Action onEdited)
    {
        var cell = _ui.CreateCard("Stat_" + label, parent, NativeUguiFactory.ColSubCardBg, null, padH: 8, padV: 6, spacing: 4f);
        NativeUguiFactory.SetLayout(cell, minW: 180f, flexW: 1f);
        _ui.CreateText("Label", cell.transform, label, 13f, NativeUguiFactory.ColMuted, wrap: false);

        var row = _ui.CreateHorizontalRow("InputRow", cell.transform, spacing: 4f);
        TMP_InputField input = null;

        var btnMinus = _ui.CreateButton("Minus", row.transform, "-", 15f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            int next = Mathf.Clamp(getter() - step, min, max);
            setter(next);
            if (input != null) input.SetTextWithoutNotify(next.ToString());
            onEdited?.Invoke();
        }, fontStyle: FontStyles.Bold, padH: 4, padV: 2);
        NativeUguiFactory.SetLayout(btnMinus.Root, minW: 30f, prefW: 30f, minH: 28f);

        input = _ui.CreateInputField("Val", row.transform, "", getter().ToString(), 14f, 6, TMP_InputField.ContentType.IntegerNumber);
        NativeUguiFactory.SetLayout(input.gameObject, minW: 72f, flexW: 1f, minH: 28f);
        input.onEndEdit.AddListener(txt =>
        {
            int cur = getter();
            if (int.TryParse(txt?.Trim(), out int parsed))
            {
                int clamped = Mathf.Clamp(parsed, min, max);
                setter(clamped);
                input.SetTextWithoutNotify(clamped.ToString());
            }
            else
            {
                input.SetTextWithoutNotify(cur.ToString());
            }
            onEdited?.Invoke();
        });

        var btnPlus = _ui.CreateButton("Plus", row.transform, "+", 15f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            int next = Mathf.Clamp(getter() + step, min, max);
            setter(next);
            if (input != null) input.SetTextWithoutNotify(next.ToString());
            onEdited?.Invoke();
        }, fontStyle: FontStyles.Bold, padH: 4, padV: 2);
        NativeUguiFactory.SetLayout(btnPlus.Root, minW: 30f, prefW: 30f, minH: 28f);

        return input;
    }

    private void BuildBottomStatusBar(Transform parent)
    {
        var bar = _ui.CreateCard("BottomBar", parent, NativeUguiFactory.ColHeaderBg, null, padH: 12, padV: 6);
        NativeUguiFactory.SetLayout(bar, minH: 34f);
        var row = _ui.CreateHorizontalRow("Row", bar.transform, spacing: 6f);
        _bottomStatusText = _ui.CreateText("Status", row.transform, "", 13f, NativeUguiFactory.ColMuted, wrap: false);
        NativeUguiFactory.SetLayout(_bottomStatusText.gameObject, minW: 260f, flexW: 1f, minH: 24f);

        var fpsLbl = _ui.CreateText("FpsLbl", row.transform, "帧率上限:", 12.5f, NativeUguiFactory.ColGold, FontStyles.Bold, TextAlignmentOptions.MidlineRight, wrap: false);
        NativeUguiFactory.SetLayout(fpsLbl.gameObject, minW: 66f, prefW: 70f, minH: 24f);

        for (int i = 0; i < FpsPresetOptions.Length; i++)
        {
            int capFps = FpsPresetOptions[i].Fps;
            string label = FpsPresetOptions[i].Label;
            var btn = _ui.CreateButton($"BtnFps_{capFps}", row.transform, label, 12f,
                NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream,
                () =>
                {
                    FrameRateGuard.SetMaxForegroundFps(capFps);
                    SetStatus(capFps > 0 ? $"已设置前台稳帧上限 {capFps} FPS（失焦后台自动降频 20 FPS）" : "已恢复跟随原版帧率设置（失焦后台仍自动降频 20 FPS）", isError: false);
                    RefreshFpsPresetButtonsUi();
                    RefreshStatusBarUi();
                }, borderColor: NativeUguiFactory.ColGoldDim, padH: 6, padV: 2);
            NativeUguiFactory.SetLayout(btn.Root, minW: capFps == 60 ? 64f : 44f, prefW: capFps == 60 ? 68f : 46f, minH: 24f);
            _fpsLimitButtons[i] = btn;
        }
        RefreshFpsPresetButtonsUi();

        var hotkey = _ui.CreateText("Hotkey", row.transform, "F8 打开/收起 · Esc 关闭", 12.5f, NativeUguiFactory.ColMuted, align: TextAlignmentOptions.MidlineRight, wrap: false);
        NativeUguiFactory.SetLayout(hotkey.gameObject, minW: 170f, prefW: 176f, minH: 24f);
    }

    private void RefreshFpsPresetButtonsUi()
    {
        int cur = FrameRateGuard.GetConfiguredMaxForegroundFps();
        for (int i = 0; i < _fpsLimitButtons.Length; i++)
        {
            var b = _fpsLimitButtons[i];
            if (b == null) continue;
            bool on = cur == FpsPresetOptions[i].Fps;
            NativeUguiFactory.ApplyButtonColors(
                b.Button,
                on ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
                on ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover);
            b.Label.color = on ? Color.white : NativeUguiFactory.ColCream;
        }
    }

    private void EnsurePreRunPromptBuilt()
    {
        if (_preRunBuilt && _preRunPromptRoot != null) return;
        _preRunBuilt = true;
        EnsureModalBuilt();

        _preRunPromptRoot = _ui.CreateBox("PreRunPromptRoot", _modalCanvas.transform, NativeUguiFactory.ColBackdrop, rounded: false, raycastTarget: true);
        NativeUguiFactory.StretchFill((RectTransform)_preRunPromptRoot.transform);

        var card = _ui.CreateCard("PreRunCard", _preRunPromptRoot.transform, NativeUguiFactory.ColPanelBg, NativeUguiFactory.ColGold, padH: 16, padV: 14, spacing: 10f);
        var crt = (RectTransform)card.transform;
        crt.anchorMin = new Vector2(0.5f, 0.5f);
        crt.anchorMax = new Vector2(0.5f, 0.5f);
        crt.pivot = new Vector2(0.5f, 0.5f);
        crt.sizeDelta = new Vector2(1220f, 620f);

        _openingPromptTitleText = _ui.CreateText("Title", card.transform, "开局流派倾向确认（本局仅此 1 次，确认后途中不可更改）", 18f, NativeUguiFactory.ColGold, FontStyles.Bold);
        _openingPromptSubText = _ui.CreateText("Sub", card.transform, "全模式统一规则：确认锁定或选第一个事件离开第 1 小时后，本局流派倾向将永久锁定。", 13f, NativeUguiFactory.ColMuted);

        var scroll = _ui.CreateScrollView("PreRunScroll", card.transform);
        NativeUguiFactory.SetLayout(scroll.Root, flexH: 1f, minH: 240f);
        _preRunArchetypeGrid = BuildArchetypeTendencyCard(scroll.Content, () => _openingPromptDraftRules, isPreRunPrompt: true);

        _preRunPromptAutoToggle = _ui.CreateToggle("PreRunAutoPromptToggle", card.transform,
            "进局 Day 1 Hour 1 自动弹出此确认窗（取消后下次进局不再自动弹出，仍可按 F8 在首小时锁定）",
            _normalRules?.PromptTendencyBeforeRun ?? true,
            v =>
            {
                if (_openingPromptDraftRules != null) _openingPromptDraftRules.PromptTendencyBeforeRun = v;
                if (_normalRules != null)
                {
                    _normalRules.PromptTendencyBeforeRun = v;
                    _normalBuf?.SyncFromRules(_normalRules);
                    PersistMigratedDefaultRulesAsync(_normalRules.Clone());
                }
                SyncPromptBeforeRunToggles(v);
            },
            fontSize: 13f);

        var btnRow = _ui.CreateHorizontalRow("BtnRow", card.transform, spacing: 10f);
        var btnCancel = _ui.CreateButton("BtnCancel", btnRow.transform, "暂不锁定（先看事件）", 13.5f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, DismissOpeningPromptOnly);
        NativeUguiFactory.SetLayout(btnCancel.Root, minW: 164f, prefW: 172f, minH: 36f);

        var sp = new GameObject("Spacer", typeof(RectTransform));
        sp.transform.SetParent(btnRow.transform, worldPositionStays: false);
        NativeUguiFactory.SetLayout(sp, flexW: 1f);

        var btnSkip = _ui.CreateButton("BtnSkip", btnRow.transform, "不选倾向并锁定 (0项)", 14f, NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
        {
            LockOpeningTendencyOnActiveRunAsync(0UL, closePromptAfter: true);
        });
        NativeUguiFactory.SetLayout(btnSkip.Root, minW: 184f, prefW: 192f, minH: 36f);

        var btnConfirm = _ui.CreateButton("BtnConfirm", btnRow.transform, "确认锁定本局倾向", 14.5f, NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
        {
            LockOpeningTendencyOnActiveRunAsync(_openingPromptDraftRules.SelectedArchetypeMask, closePromptAfter: true);
        }, borderColor: NativeUguiFactory.ColGold, fontStyle: FontStyles.Bold);
        NativeUguiFactory.SetLayout(btnConfirm.Root, minW: 188f, prefW: 196f, minH: 36f);

        _preRunPromptRoot.SetActive(false);
    }

    private void SyncPromptBeforeRunToggles(bool enabled)
    {
        if (_normalArchetypeGrid?.PromptBeforeRunToggle?.Toggle != null)
        {
            _normalArchetypeGrid.PromptBeforeRunToggle.Toggle.SetIsOnWithoutNotify(enabled);
        }
        if (_tourneyPersonalArchetypeGrid?.PromptBeforeRunToggle?.Toggle != null)
        {
            _tourneyPersonalArchetypeGrid.PromptBeforeRunToggle.Toggle.SetIsOnWithoutNotify(enabled);
        }
        if (_preRunPromptAutoToggle?.Toggle != null)
        {
            _preRunPromptAutoToggle.Toggle.SetIsOnWithoutNotify(enabled);
        }
    }

    private void RefreshOpeningPromptVisuals()
    {
        if (!_preRunBuilt || _preRunPromptRoot == null) return;
        if (_openingPromptTitleText != null)
        {
            string modeLabel = IsActiveRunTournamentMode() ? "锦标赛" : "常规/排位";
            _openingPromptTitleText.text = $"[{modeLabel}] {FormatHeroZh(DetectCurrentHero())} · 开局流派倾向确认（本局仅此 1 次，确认后途中不可修改）";
        }
        if (_preRunPromptAutoToggle?.Toggle != null)
        {
            _preRunPromptAutoToggle.Toggle.SetIsOnWithoutNotify(_normalRules?.PromptTendencyBeforeRun ?? true);
        }
        RefreshArchetypeGrid(_preRunArchetypeGrid, _openingPromptDraftRules);
    }

    // ── 事件驱动增量刷新（仅在状态变化或界面打开时调用，绝不每帧执行）────────

    private void UpdateSafeViewportRootIfNeeded(bool force = false)
    {
        if (_safeViewportRoot == null) return;

        int sw = Screen.width;
        int sh = Screen.height;
        if (_cachedMainCam == null || !_cachedMainCam.isActiveAndEnabled)
        {
            _cachedMainCam = Camera.main;
        }
        Rect camRect = _cachedMainCam != null ? _cachedMainCam.rect : new Rect(0f, 0f, 1f, 1f);
        Rect lbRect = TryReadLetterBoxScreenRect();

        if (!force
            && sw == _lastViewportScreenW
            && sh == _lastViewportScreenH
            && RectApproximatelyEqual(camRect, _lastViewportCamRect)
            && RectApproximatelyEqual(lbRect, _lastLetterBoxRect))
        {
            return;
        }

        _lastViewportScreenW = sw;
        _lastViewportScreenH = sh;
        _lastViewportCamRect = camRect;
        _lastLetterBoxRect = lbRect;

        Rect safeRect;
        if (IsNormalizedCropRect(lbRect))
        {
            // 优先对齐游戏原生 LetterBoxManager.CurrentScreenRect，确保 16:9 / 16:10 / 超宽屏 / 窄窗一致贴边
            safeRect = ClampNormalizedRect(lbRect);
        }
        else if (IsNormalizedCropRect(camRect))
        {
            safeRect = ClampNormalizedRect(camRect);
        }
        else
        {
            safeRect = ComputeGameViewportSafeRect(sw, sh);
        }

        _safeViewportRoot.anchorMin = new Vector2(safeRect.xMin, safeRect.yMin);
        _safeViewportRoot.anchorMax = new Vector2(safeRect.xMax, safeRect.yMax);
        _safeViewportRoot.offsetMin = Vector2.zero;
        _safeViewportRoot.offsetMax = Vector2.zero;

        // 分辨率/安全区变更时同步钳制模态主窗大小并刷新胶囊自适应宽度（D4-P1-11）
        FitModalWindowToSafeViewport();
        _lastPillKey = "";
        _lastInRunBadgeKey = "";
        _nextHudSyncTime = 0f;
    }

    private void FitModalWindowToSafeViewport()
    {
        if (_modalWindowRt == null || _modalCanvas == null) return;
        try
        {
            float scaleFactor = _modalCanvas.scaleFactor > 0.01f ? _modalCanvas.scaleFactor : 1f;
            float logicalW = Screen.width / scaleFactor;
            float logicalH = Screen.height / scaleFactor;
            if (logicalW < 100f || logicalH < 100f) return;

            float clampedW = Mathf.Clamp(logicalW * 0.92f, 760f, 1260f);
            float clampedH = Mathf.Clamp(logicalH * 0.90f, 520f, 880f);
            _modalWindowRt.sizeDelta = new Vector2(clampedW, clampedH);
        }
        catch { }
    }

    private static bool RectApproximatelyEqual(Rect a, Rect b)
        => Mathf.Abs(a.x - b.x) < 0.0005f
           && Mathf.Abs(a.y - b.y) < 0.0005f
           && Mathf.Abs(a.width - b.width) < 0.0005f
           && Mathf.Abs(a.height - b.height) < 0.0005f;

    private static bool IsNormalizedCropRect(Rect r)
        => r.width > 0.05f && r.height > 0.05f
           && (Mathf.Abs(r.xMin) > 0.001f
               || Mathf.Abs(r.yMin) > 0.001f
               || Mathf.Abs(r.xMax - 1f) > 0.001f
               || Mathf.Abs(r.yMax - 1f) > 0.001f);

    private static Rect ClampNormalizedRect(Rect r)
    {
        float xMin = Mathf.Clamp01(r.xMin);
        float yMin = Mathf.Clamp01(r.yMin);
        float xMax = Mathf.Clamp01(r.xMax);
        float yMax = Mathf.Clamp01(r.yMax);
        return new Rect(xMin, yMin, Mathf.Max(0.05f, xMax - xMin), Mathf.Max(0.05f, yMax - yMin));
    }

    private static Rect TryReadLetterBoxScreenRect()
    {
        try
        {
            if (!_letterBoxTypeChecked)
            {
                _letterBoxTypeChecked = true;
                var lbType = AccessTools.TypeByName("TheBazaar.UI.LetterBoxManager");
                var prop = lbType?.GetProperty("CurrentScreenRect", BindingFlags.Public | BindingFlags.Static);
                if (prop != null && prop.PropertyType == typeof(Rect) && prop.GetMethod != null)
                {
                    _letterBoxScreenRectGetter = (Func<Rect>)Delegate.CreateDelegate(typeof(Func<Rect>), prop.GetMethod);
                }
            }
            if (_letterBoxScreenRectGetter != null)
            {
                Rect r = _letterBoxScreenRectGetter();
                if (r.width > 0.05f && r.height > 0.05f) return r;
            }
        }
        catch { }
        return new Rect(0f, 0f, 1f, 1f);
    }

    private static Rect ComputeGameViewportSafeRect(int screenW, int screenH)
    {
        if (screenW <= 0 || screenH <= 0)
        {
            return new Rect(0f, 0f, 1f, 1f);
        }

        // 对齐游戏原生 LetterBoxManager 支持区间 [16:10, 21:9]：16:9 与 16:10 均全幅匹配真实边栏，仅更窄或更宽窗口启用对称内缩
        const float minAspect = 16f / 10f;
        const float maxAspect = 21f / 9f;
        float windowAspect = (float)screenW / screenH;
        if (windowAspect < minAspect - 0.001f)
        {
            float h = windowAspect / minAspect;
            float ym = (1f - h) * 0.5f;
            return new Rect(0f, ym, 1f, h);
        }
        if (windowAspect > maxAspect + 0.001f)
        {
            float w = maxAspect / windowAspect;
            float xm = (1f - w) * 0.5f;
            return new Rect(xm, 0f, w, 1f);
        }
        return new Rect(0f, 0f, 1f, 1f);
    }

    private static void ApplyInRunPillButtonColors(Button btn, Color normalBg, Color hoverBg)
    {
        if (btn == null) return;
        NativeUguiFactory.ApplyButtonColors(btn, normalBg, hoverBg);
        var cb = btn.colors;
        // 战斗期间保持胶囊显色不变，不随 interactable=false 灰暗化
        cb.disabledColor = normalBg;
        btn.colors = cb;
    }

    private float ResolveSafeViewportCanvasWidth()
    {
        if (_safeViewportRoot != null && _safeViewportRoot.rect.width > 200f)
        {
            return _safeViewportRoot.rect.width;
        }
        if (_hudCanvas != null)
        {
            var crt = (RectTransform)_hudCanvas.transform;
            if (crt.rect.width > 200f) return crt.rect.width;
        }
        return 1920f;
    }

    private bool HasCircleIndicatorGlyphs()
    {
        if (_cachedCircleGlyphsSupported.HasValue && _ui.HasVerifiedChineseFont)
        {
            return _cachedCircleGlyphsSupported.Value;
        }
        var font = _ui.Font;
        if (font == null) return false;
        try
        {
            bool ok = font.HasCharacter('\u25CF', searchFallbacks: true, tryAddCharacter: true)
                   && font.HasCharacter('\u25CB', searchFallbacks: true, tryAddCharacter: true);
            if (_ui.HasVerifiedChineseFont)
            {
                _cachedCircleGlyphsSupported = ok;
            }
            return ok;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPostOpeningRunAppState(string appState)
    {
        if (string.IsNullOrEmpty(appState)) return false;
        return string.Equals(appState, "EncounterState", StringComparison.Ordinal)
            || string.Equals(appState, "PedestalState", StringComparison.Ordinal)
            || string.Equals(appState, "LootState", StringComparison.Ordinal)
            || string.Equals(appState, "LevelUpState", StringComparison.Ordinal)
            || string.Equals(appState, "CombatState", StringComparison.Ordinal)
            || string.Equals(appState, "PVPCombatState", StringComparison.Ordinal)
            || string.Equals(appState, "ReplayState", StringComparison.Ordinal)
            || string.Equals(appState, "TournamentPvpChoiceState", StringComparison.Ordinal)
            || string.Equals(appState, "TournamentWaitingRoomState", StringComparison.Ordinal)
            || string.Equals(appState, "EndRunVictoryState", StringComparison.Ordinal)
            || string.Equals(appState, "EndRunDefeatState", StringComparison.Ordinal);
    }

    private static string ReadCurrentRunWindowInfo(out uint day, out uint hour, out string appState)
    {
        day = 0;
        hour = 0;
        appState = GameInspector.GetAppStateName();
        var run = GameInspector.GetDataRun();
        if (run == null) return "";
        try
        {
            day = Convert.ToUInt32(GameInspector.GetMember(run, "Day") ?? 0u);
            hour = Convert.ToUInt32(GameInspector.GetMember(run, "Hour") ?? 0u);
            string id = GameInspector.GetMember(run, "RunId")?.ToString()
                ?? GameInspector.GetMember(run, "Id")?.ToString();
            if (!string.IsNullOrEmpty(id)) return id;
            int objIdentity = RuntimeHelpers.GetHashCode(run);
            return $"{objIdentity}|{GameInspector.GetMember(run, "Hero")}|{GameInspector.GetMember(run, "PlayMode")}";
        }
        catch
        {
            return "";
        }
    }

    private bool IsOpeningEditableInRun()
    {
        string scene = GameInspector.GetActiveSceneName();
        if (GameInspector.IsMainMenuSceneName(scene)) return false;
        ReadCurrentRunWindowInfo(out uint day, out uint hour, out string appState);
        if (!GameInspector.IsActiveInRunState(scene, appState)) return false;
        if (_activeRunTendencyLocked || !_activeRunAllowTendency) return false;
        return day == 1 && hour == 1 && string.Equals(appState, "ChoiceState", StringComparison.Ordinal);
    }

    private bool IsPersonalTendencyLockedInActiveRun()
    {
        string scene = GameInspector.GetActiveSceneName();
        if (GameInspector.IsMainMenuSceneName(scene)) return false;
        string appState = GameInspector.GetAppStateName();
        if (!GameInspector.IsActiveInRunState(scene, appState)) return false;
        return !IsOpeningEditableInRun();
    }

    private void RefreshHudOverlays(bool force = false)
    {
        EnsureHudBuilt();
        if (force)
        {
            UpdateSafeViewportRootIfNeeded(force: true);
        }
        if (!_ui.HasVerifiedChineseFont)
        {
            if (_ui.RefreshNativeAssetsIfNeeded(forceRescan: false))
            {
                _cachedCircleGlyphsSupported = null;
            }
        }

        string activeScene = GameInspector.GetActiveSceneName();
        bool inMainMenu = GameInspector.IsMainMenuSceneName(activeScene);
        bool inCombat = IsInActiveCombatPlayback();
        bool inRun = !inMainMenu && GameInspector.IsActiveInRunState(activeScene, GameInspector.GetAppStateName());
        bool hasGlyphs = HasCircleIndicatorGlyphs();
        float safeW = ResolveSafeViewportCanvasWidth();
        int safeWidthBucket = Mathf.RoundToInt(safeW);

        if (inRun)
        {
            string runKey = ReadCurrentRunWindowInfo(out uint day, out uint hour, out string appState);
            // 注意：进入 Gameplay 场景初期有 ~120ms 处于 StartRunAppState/null 过渡态，仅在真正离开首小时 ChoiceState 后才视为离开开场窗口
            bool leftOpeningWindow = day > 1 || hour > 1 || IsPostOpeningRunAppState(appState);
            bool newlyLocked = false;
            if (leftOpeningWindow)
            {
                if (!_activeRunTendencyLocked)
                {
                    _activeRunTendencyLocked = true;
                    newlyLocked = true;
                }
                if (_showPreRunPrompt) DismissOpeningPromptOnly();
            }

            bool readyToFetchOpeningState = day == 1 && hour == 1 && string.Equals(appState, "ChoiceState", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(runKey)
                && !string.Equals(_activeRunFetchedId, runKey, StringComparison.OrdinalIgnoreCase)
                && !_activeRunFetchInFlight
                && Time.unscaledTime >= _nextActiveRunRetryTime
                && (readyToFetchOpeningState || leftOpeningWindow))
            {
                _activeRunFetchedId = runKey;
                _activeRunTendencyLocked = leftOpeningWindow;
                _activeRunAllowTendency = true;
                FetchActiveRunTendencyStateAsync(runKey);
            }
            if (newlyLocked && _isOpen)
            {
                RefreshModalContentsIfOpen();
            }
        }
        else if (_showPreRunPrompt)
        {
            DismissOpeningPromptOnly();
        }

        // 1. 主菜单 / 英雄选择页 / 锦标赛大厅 规则状态胶囊
        bool showPill = inMainMenu && !inCombat && !_isOpen && !_showPreRunPrompt;
        if (_menuRulePillBtn != null && _menuRulePillBtn.Root.activeSelf != showPill)
        {
            _menuRulePillBtn.Root.SetActive(showPill);
        }

        if (showPill)
        {
            bool tourneyContext = IsTournamentUiContext();
            var targetRules = ResolveHudDisplayRules(tourneyContext);
            var sum = SummarizeRules(targetRules, tourneyContext);
            string key = $"{tourneyContext}|{sum.IsCustomActive}|{sum.ActiveCount}|{sum.SummaryText}|{_isOpen}|{hasGlyphs}|{_ui.HasVerifiedChineseFont}|{safeWidthBucket}";
            if (force || !string.Equals(_lastPillKey, key, StringComparison.Ordinal))
            {
                _lastPillKey = key;
                ApplyDualStatePillVisuals(sum, tourneyContext, hasGlyphs, safeW);
            }
        }

        // 2. 局内右上角安全区域规则状态胶囊（紧邻设置齿轮左侧，绝不遮挡齿轮）
        bool showInRunBadge = inRun && !_isOpen;
        if (_inRunBadgeGo != null && _inRunBadgeGo.activeSelf != showInRunBadge)
        {
            _inRunBadgeGo.SetActive(showInRunBadge);
        }

        bool inRunBadgeIsCustom = false;
        if (showInRunBadge)
        {
            bool isTourneyRun = IsActiveRunTournamentMode();
            var sum = SummarizeInRunDisplayRules(isTourneyRun);
            inRunBadgeIsCustom = sum.IsCustomActive;
            string key = $"{isTourneyRun}|{sum.IsCustomActive}|{sum.ActiveCount}|{sum.SummaryText}|{inCombat}|{hasGlyphs}|{_ui.HasVerifiedChineseFont}|{safeWidthBucket}|{_activeRunTendencyLocked}|{_activeRunRules != null}|{_activeRunIsCustomRuleRun}";
            if (force || !string.Equals(_lastInRunBadgeKey, key, StringComparison.Ordinal))
            {
                _lastInRunBadgeKey = key;
                ApplyInRunBadgeVisuals(sum, isTourneyRun, inCombat, hasGlyphs, safeW);
            }
        }

        // 动态排布右上角倍速芯片与锦标赛等待横幅的纵向偏移，确保与上方规则胶囊互不重叠
        float rightStackTopY = showInRunBadge
            ? (inRunBadgeIsCustom ? -62f : -48f)
            : -12f;
        if (_speedChipRt != null)
        {
            var pos = _speedChipRt.anchoredPosition;
            if (Mathf.Abs(pos.y - rightStackTopY) > 0.5f)
            {
                _speedChipRt.anchoredPosition = new Vector2(pos.x, rightStackTopY);
            }
        }
        if (_tourneyWaitBannerRt != null)
        {
            var pos = _tourneyWaitBannerRt.anchoredPosition;
            if (Mathf.Abs(pos.y - rightStackTopY) > 0.5f)
            {
                _tourneyWaitBannerRt.anchoredPosition = new Vector2(pos.x, rightStackTopY);
            }
        }

        // 3. 战斗界面右上角倍速切换小芯片（1x / 2x / 3x / 5x）
        bool showSpeedChip = inCombat && !_isOpen;
        if (_speedChipGo != null && _speedChipGo.activeSelf != showSpeedChip)
        {
            _speedChipGo.SetActive(showSpeedChip);
        }
        if (showSpeedChip)
        {
            float eff = CombatPatch.GetDisplayCombatSpeed();
            int effTenths = Mathf.RoundToInt(eff * 10f);
            if (force || _lastSpeedTenths != effTenths)
            {
                _lastSpeedTenths = effTenths;
                for (int i = 0; i < _speedChipButtons.Length; i++)
                {
                    float v = SpeedChipValues[i];
                    bool active = Mathf.Abs(eff - v) < 0.35f;
                    var btn = _speedChipButtons[i];
                    NativeUguiFactory.ApplyButtonColors(
                        btn.Button,
                        active ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
                        active ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover);
                    btn.Label.color = active ? Color.white : NativeUguiFactory.ColCream;
                    if (btn.Border != null)
                    {
                        btn.Border.color = active ? NativeUguiFactory.ColGold : new Color(0.45f, 0.38f, 0.22f, 0.7f);
                    }
                }
            }
        }

        // 4. 锦标赛多真人回合等待对手同步横幅（CanStartPvp == false / WaitingForOtherPlayers / 错误提示拦截）
        string waitTitle = "", waitSub = "";
        bool hasUrgentTimer = false;
        bool showTourneyWait = inRun && !inCombat && !_isOpen && IsActiveRunTournamentMode()
            && TryBuildTourneyWaitBannerText(out waitTitle, out waitSub, out hasUrgentTimer);
        if (_tourneyWaitBannerGo != null && _tourneyWaitBannerGo.activeSelf != showTourneyWait)
        {
            _tourneyWaitBannerGo.SetActive(showTourneyWait);
        }
        if (showTourneyWait && _tourneyWaitTitleText != null && _tourneyWaitSubText != null)
        {
            string wkey = $"{waitTitle}|{waitSub}|{hasUrgentTimer}";
            if (force || !string.Equals(_lastTourneyWaitKey, wkey, StringComparison.Ordinal))
            {
                _lastTourneyWaitKey = wkey;
                _tourneyWaitTitleText.text = waitTitle;
                _tourneyWaitSubText.text = waitSub;
                if (_tourneyWaitBannerDot != null)
                {
                    _tourneyWaitBannerDot.color = hasUrgentTimer
                        ? NativeUguiFactory.ColEmeraldText
                        : NativeUguiFactory.ColGold;
                }
            }
        }

        // 5. 隐藏画布与射线检测彻底休眠（消除空转 Overdraw 与 EventSystem 射线扫描损耗）
        if (_hudCanvas != null)
        {
            // P1-49：将活跃 Toast 纳入 anyHud，避免弹窗打开或主菜单关闭胶囊时被定时 RefreshHudOverlays 误熄灭
            bool showToast = _toastGo != null && _toastGo.activeSelf;
            bool anyHud = showPill || showInRunBadge || showSpeedChip || showTourneyWait || showToast;
            if (_hudCanvas.enabled != anyHud) _hudCanvas.enabled = anyHud;
            if (_hudRaycaster != null)
            {
                bool needHudRay = showPill || (showInRunBadge && !inCombat) || showSpeedChip;
                if (_hudRaycaster.enabled != needHudRay) _hudRaycaster.enabled = needHudRay;
            }
        }
        if (_modalCanvas != null)
        {
            bool needModal = _isOpen || _showPreRunPrompt;
            if (_modalCanvas.enabled != needModal) _modalCanvas.enabled = needModal;
            if (_modalRaycaster != null && _modalRaycaster.enabled != needModal) _modalRaycaster.enabled = needModal;
        }
    }

    private CustomRunRuleSet ResolveInRunDisplayRules(bool isTourneyRun)
    {
        // P1-15：在活跃局中优先展示服务端 /custom-rules/me/active-run-tendency 下发的权威 SQLite 活跃局规则 _activeRunRules，
        // 不再误读大厅可变 _normalRules，彻底根治冷重启续玩后右上角 HUD 退回灰色「官方标准规则 (F8)」
        var baseRules = _activeRunRules != null
            ? _activeRunRules
            : (isTourneyRun ? (_currentLobby?.Rules ?? _lobbyRules) : _normalRules);
        var display = baseRules != null ? baseRules.Clone() : new CustomRunRuleSet();
        if (!_activeRunAllowTendency)
        {
            display.SelectedArchetypeMask = 0UL;
        }
        else if (_activeRunTendencyLocked)
        {
            display.SelectedArchetypeMask = _activeRunLockedMask;
        }
        else
        {
            display.SelectedArchetypeMask = _activeRunRules != null
                ? _activeRunRules.SelectedArchetypeMask
                : (_normalRules?.SelectedArchetypeMask ?? 0UL);
        }
        return display;
    }

    /// <summary>P1-15：统一汇总局内权威展示规则；若服务端显式标注 isCustomRuleRun=true（例如仅勾选本局 Mod 列表）则兜底点亮自定义规则状态。</summary>
    private RuleActivationSummary SummarizeInRunDisplayRules(bool isTourneyRun)
    {
        var effectiveDisplayRules = ResolveInRunDisplayRules(isTourneyRun);
        var sum = SummarizeRules(effectiveDisplayRules, isTourneyRun);
        if (!sum.IsCustomActive && _activeRunRules != null && _activeRunIsCustomRuleRun)
        {
            int modCount = _activeRunRules.SelectedModIds?.Count ?? 0;
            string fallbackSummary = modCount > 0 ? $"本局 Mod×{modCount}" : "自定义规则";
            return new RuleActivationSummary(true, true, Math.Max(1, modCount), fallbackSummary);
        }
        return sum;
    }

    private void ApplyInRunBadgeVisuals(RuleActivationSummary sum, bool isTourneyRun, bool inCombat, bool hasGlyphs, float safeW)
    {
        if (_inRunBadgeGo == null || _inRunBadgeRt == null) return;

        // 战斗回放中禁用点击透传以免干扰操作；非战斗阶段允许点击直达配置面板
        bool canClick = !inCombat;
        if (_inRunBadgeBg != null) _inRunBadgeBg.raycastTarget = canClick;
        if (_inRunBadgeBtn != null) _inRunBadgeBtn.interactable = canClick;

        // 右上角齿轮占据最右侧 ~110px，中央顶部留给对手事件立绘，胶囊宽度自动限制在半屏安全宽度内
        float maxAvailW = Mathf.Max(206f, (safeW * 0.5f) - 270f);
        string modePrefix = isTourneyRun ? "[赛] " : "";

        if (sum.IsCustomActive)
        {
            float targetW = Mathf.Min(334f, maxAvailW);
            _inRunBadgeRt.sizeDelta = new Vector2(targetW, 46f);
            _inRunBadgeRt.anchoredPosition = new Vector2(-132f, -12f);

            if (_inRunBadgeBg != null) _inRunBadgeBg.color = Color.white;
            if (_inRunBadgeBtn != null) ApplyInRunPillButtonColors(_inRunBadgeBtn, ColRuleActiveBg, ColRuleActiveHoverBg);
            if (_inRunBadgeBorder != null) _inRunBadgeBorder.color = ColRuleActiveBorder;
            if (_inRunBadgeDot != null)
            {
                _inRunBadgeDot.gameObject.SetActive(!hasGlyphs);
                _inRunBadgeDot.color = ColRuleActiveDot;
            }

            float textLeft = hasGlyphs ? 12f : 26f;
            var trt = _inRunBadgeText.rectTransform;
            trt.anchorMin = new Vector2(0f, 0.46f);
            trt.anchorMax = new Vector2(1f, 1f);
            trt.offsetMin = new Vector2(textLeft, 0f);
            trt.offsetMax = new Vector2(-12f, -3f);
            _inRunBadgeText.alignment = TextAlignmentOptions.BottomLeft;
            _inRunBadgeText.fontSize = 13f;
            _inRunBadgeText.color = ColRuleActiveTitle;
            _inRunBadgeText.text = hasGlyphs
                ? $"{modePrefix}● 自定义规则生效 (F8)"
                : $"{modePrefix}自定义规则生效 (F8)";

            if (_inRunBadgeSubText != null)
            {
                var srt = _inRunBadgeSubText.rectTransform;
                srt.anchorMin = new Vector2(0f, 0f);
                srt.anchorMax = new Vector2(1f, 0.48f);
                srt.offsetMin = new Vector2(textLeft, 3f);
                srt.offsetMax = new Vector2(-12f, 0f);
                _inRunBadgeSubText.color = ColRuleActiveSub;
                _inRunBadgeSubText.text = $"已启 {sum.ActiveCount} 项 · {Truncate(sum.SummaryText, 24)}";
                _inRunBadgeSubText.gameObject.SetActive(true);
            }
        }
        else
        {
            float targetW = Mathf.Min(232f, maxAvailW);
            _inRunBadgeRt.sizeDelta = new Vector2(targetW, 32f);
            _inRunBadgeRt.anchoredPosition = new Vector2(-132f, -12f);

            if (_inRunBadgeBg != null) _inRunBadgeBg.color = Color.white;
            if (_inRunBadgeBtn != null) ApplyInRunPillButtonColors(_inRunBadgeBtn, ColRuleVanillaBg, ColRuleVanillaHoverBg);
            if (_inRunBadgeBorder != null) _inRunBadgeBorder.color = ColRuleVanillaBorder;
            if (_inRunBadgeDot != null)
            {
                _inRunBadgeDot.gameObject.SetActive(!hasGlyphs);
                _inRunBadgeDot.color = ColRuleVanillaDot;
            }

            float textLeft = hasGlyphs ? 12f : 26f;
            var trt = _inRunBadgeText.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(textLeft, 2f);
            trt.offsetMax = new Vector2(-12f, -2f);
            _inRunBadgeText.alignment = TextAlignmentOptions.MidlineLeft;
            _inRunBadgeText.fontSize = 12.5f;
            _inRunBadgeText.color = ColRuleVanillaTitle;
            _inRunBadgeText.text = hasGlyphs
                ? $"{modePrefix}○ 官方标准规则 (F8)"
                : $"{modePrefix}官方标准规则 (F8)";

            if (_inRunBadgeSubText != null)
            {
                _inRunBadgeSubText.gameObject.SetActive(false);
            }
        }
    }

    private void ApplyDualStatePillVisuals(RuleActivationSummary sum, bool isTourney, bool hasGlyphs, float safeW)
    {
        if (_menuRulePillBtn == null) return;

        var pillRt = (RectTransform)_menuRulePillBtn.Root.transform;
        float targetW = Mathf.Clamp(safeW * 0.30f, 340f, 480f);
        pillRt.sizeDelta = new Vector2(targetW, 54f);
        pillRt.anchoredPosition = new Vector2(-Mathf.Min(230f, safeW * 0.12f), -12f);

        float textLeft = hasGlyphs ? 16f : 38f;
        var pTitleRt = _menuRulePillBtn.Label.rectTransform;
        pTitleRt.offsetMin = new Vector2(textLeft, 0f);
        var pSubRt = _pillSubText.rectTransform;
        pSubRt.offsetMin = new Vector2(textLeft, 4f);

        string modePrefix = isTourney ? "[锦标赛] " : "";
        if (sum.IsCustomActive)
        {
            NativeUguiFactory.ApplyButtonColors(
                _menuRulePillBtn.Button,
                ColRuleActiveBg,
                ColRuleActiveHoverBg);
            _menuRulePillBtn.Border.color = ColRuleActiveBorder;
            _pillStatusDot.gameObject.SetActive(!hasGlyphs);
            _pillStatusDot.color = ColRuleActiveDot;
            _menuRulePillBtn.Label.color = ColRuleActiveTitle;
            _menuRulePillBtn.Label.text = hasGlyphs
                ? $"{modePrefix}● 自定义规则生效 (F8)"
                : $"{modePrefix}自定义规则生效 (F8)";
            _pillSubText.color = ColRuleActiveSub;
            _pillSubText.text = $"已启 {sum.ActiveCount} 项 · {Truncate(sum.SummaryText, 26)} · 点击配置";
        }
        else
        {
            NativeUguiFactory.ApplyButtonColors(
                _menuRulePillBtn.Button,
                ColRuleVanillaBg,
                ColRuleVanillaHoverBg);
            _menuRulePillBtn.Border.color = ColRuleVanillaBorder;
            _pillStatusDot.gameObject.SetActive(!hasGlyphs);
            _pillStatusDot.color = ColRuleVanillaDot;
            _menuRulePillBtn.Label.color = ColRuleVanillaTitle;
            _menuRulePillBtn.Label.text = hasGlyphs
                ? $"{modePrefix}○ 官方标准规则 (F8)"
                : $"{modePrefix}官方标准规则 (F8)";
            _pillSubText.color = ColRuleVanillaSub;
            _pillSubText.text = "当前使用官方标准规则 · 点击配置自定义选项";
        }
    }

    private void RefreshModalContentsIfOpen()
    {
        if (!_isOpen || _modalPanelRoot == null || !_modalPanelRoot.activeSelf) return;

        _ui.RefreshNativeAssetsIfNeeded(forceRescan: false);

        string userLabel = string.IsNullOrEmpty(NameAuth.CurrentUsername) ? "未登录" : NameAuth.CurrentUsername;
        _headerAccountText.text = $"{userLabel} | {FormatHeroZh(DetectCurrentHero())}";

        RefreshSummaryIndicators();
        RefreshTabButtonsVisual();
        RefreshMainMenuToolboxUi();

        bool tabChanged = _lastModalTab != _activeTab;
        _lastModalTab = _activeTab;
        _normalTabContainer.SetActive(_activeTab == UiTab.Normal);
        _tournamentTabContainer.SetActive(_activeTab == UiTab.Tournament);
        UpdateExtendedTabsVisibility();

        bool layoutDirty = tabChanged;
        if (_activeTab == UiTab.Normal)
        {
            layoutDirty |= RefreshArchetypeGrid(_normalArchetypeGrid, _normalRules);
            layoutDirty |= SyncEditorWidgets(_normalWidgets, _normalRules, _normalBuf);
            if (_btnSaveNormal != null) _btnSaveNormal.Button.interactable = !_isBusy;
        }
        else if (_activeTab == UiTab.Tournament)
        {
            RefreshTournamentTabUi();
            layoutDirty = true;
        }
        else
        {
            layoutDirty = true;
        }

        RefreshStatusBarUi();
        RefreshFpsPresetButtonsUi();
        if (layoutDirty && _mainScroll?.Content != null)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(_mainScroll.Content);
        }
    }

    private void RefreshSummaryIndicators()
    {
        if (_modalBuilt && _modalPanelRoot != null && _modalPanelRoot.activeSelf)
        {
            bool isTourney = _activeTab == UiTab.Tournament;
            var activeRules = isTourney ? _lobbyRules : _normalRules;
            var sum = SummarizeRules(activeRules, isTourney);
            bool hasGlyphs = HasCircleIndicatorGlyphs();

            if (sum.IsCustomActive)
            {
                _overviewLampBg.color = ColRuleActiveBg;
                _overviewLampBorder.color = ColRuleActiveBorder;
                _overviewLampDot.color = ColRuleActiveDot;
                _overviewLampTitle.color = ColRuleActiveTitle;
                _overviewLampTitle.text = hasGlyphs
                    ? $"● 自定义规则生效（已开启 {sum.ActiveCount} 项）"
                    : $"自定义规则生效（已开启 {sum.ActiveCount} 项）";
                _overviewLampDetail.color = ColRuleActiveSub;
                _overviewLampDetail.text = sum.IsPowerModified
                    ? $"{sum.SummaryText} · 超模局自动隔离"
                    : $"{sum.SummaryText} · 合法卡池加权";
            }
            else
            {
                _overviewLampBg.color = ColRuleVanillaBg;
                _overviewLampBorder.color = ColRuleVanillaBorder;
                _overviewLampDot.color = ColRuleVanillaDot;
                _overviewLampTitle.color = ColRuleVanillaTitle;
                _overviewLampTitle.text = hasGlyphs
                    ? "○ 官方标准规则（全部未开启）"
                    : "官方标准规则（全部未开启）";
                _overviewLampDetail.color = ColRuleVanillaSub;
                _overviewLampDetail.text = "未开启任何自定义项 · 对局镜像进公共池";
            }
        }
        RefreshHudOverlays(force: true);
    }

    private static void StyleTab(NativeUguiFactory.ButtonHandle btn, bool active)
    {
        if (btn == null) return;
        NativeUguiFactory.ApplyButtonColors(
            btn.Button,
            active ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
            active ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover);
        btn.Label.color = active ? Color.white : NativeUguiFactory.ColCream;
        if (btn.Border != null)
        {
            btn.Border.color = active ? NativeUguiFactory.ColGold : NativeUguiFactory.ColGoldDim;
        }
    }

    private void RefreshTabButtonsVisual()
    {
        StyleTab(_tabNormalBtn, _activeTab == UiTab.Normal);
        StyleTab(_tabTournamentBtn, _activeTab == UiTab.Tournament);
        StyleTab(_tabWorkshopBtn, _activeTab == UiTab.Workshop);
        StyleTab(_tabSocialBtn, _activeTab == UiTab.Social);
        StyleTab(_tabPerformanceBtn, _activeTab == UiTab.Performance);
    }

    private void RefreshMainMenuToolboxUi()
    {
        bool inMenu = GameInspector.IsMainMenuSceneName(GameInspector.GetActiveSceneName());
        _cosmeticsToolboxGo.SetActive(inMenu);
        if (!inMenu) return;

        bool hasActiveRun = GameInspector.ReadHasActiveRunFlag();
        string curHero = DetectCurrentHero();
        int chestCount = GameDriver.ReadClientBundledChestCount();
        string key = $"{hasActiveRun}|{curHero}|{chestCount}|{_isBusy}";
        if (string.Equals(_lastToolboxKey, key, StringComparison.Ordinal)) return;
        _lastToolboxKey = key;

        _toolboxChestText.text = chestCount >= 0 ? $"当前宝箱：{chestCount} 个" : "当前宝箱：同步中";
        _toolboxRunBadgeBg.color = hasActiveRun ? new Color(0.28f, 0.11f, 0.30f, 0.96f) : NativeUguiFactory.ColCustomPillBg;
        _toolboxRunBadgeText.color = hasActiveRun ? new Color(0.92f, 0.72f, 1.0f, 1f) : NativeUguiFactory.ColEmeraldText;
        _toolboxRunBadgeText.text = hasActiveRun ? "[进行中] 已有对局（原生切英雄已隐藏）" : "[空闲] 主菜单空闲（可随时切英雄/换装/开箱）";
        _toolboxConcedeRow.SetActive(hasActiveRun);

        foreach (var (heroKey, btn) in _toolboxHeroBtns)
        {
            bool sel = string.Equals(curHero, heroKey, StringComparison.OrdinalIgnoreCase);
            NativeUguiFactory.ApplyButtonColors(
                btn.Button,
                sel ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
                sel ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover);
            btn.Label.color = sel ? Color.white : NativeUguiFactory.ColCream;
        }
        foreach (var b in _toolboxActionBtns)
        {
            b.Button.interactable = !_isBusy;
        }
    }

    private void RefreshTournamentTabUi()
    {
        RefreshArchetypeGrid(_tourneyPersonalArchetypeGrid, _normalRules);
        RefreshPresetSlotsUi();

        bool hasLobby = _currentLobby != null;
        _tourneyNoLobbyCard.SetActive(!hasLobby);
        _tourneyLobbyActiveCard.SetActive(hasLobby);
        if (!hasLobby) return;

        bool isHost = IsLocalPlayerLobbyHost(_currentLobby);
        int memberCount = _currentLobby.Members?.Count ?? 0;
        int botCount = _currentLobby.Members?.Count(m => m.IsAi) ?? 0;

        _tourneyLobbyHeaderText.text =
            $"房间「{_currentLobby.Name}」| 房间码：{_currentLobby.Code} | 身份：{(isHost ? "房主（可编辑）" : "成员（只读公示）")} | 人数：{memberCount}/{_currentLobby.PlayerCap}（含 {botCount} AI）";
        _btnSaveLobbyHeader.Root.SetActive(isHost);
        _btnSaveLobbyHeader.Button.interactable = !_isBusy;

        _tourneyHostBody.SetActive(isHost);
        _tourneyMemberBody.SetActive(!isHost);
        RefreshTournamentRoomModsUi();
        if (isHost && Core.ModContentSyncClient.Instance.GetCommunityModsSnapshot().Count == 0 && !_communityFetchInFlight)
        {
            RefreshCommunityModsAsync(force: false);
        }

        if (isHost)
        {
            bool cap10 = _lobbyRules.PlayerCap == 10;
            NativeUguiFactory.ApplyButtonColors(_btnCap8.Button,
                !cap10 ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
                !cap10 ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover);
            _btnCap8.Label.color = !cap10 ? Color.white : NativeUguiFactory.ColCream;

            NativeUguiFactory.ApplyButtonColors(_btnCap10.Button,
                cap10 ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
                cap10 ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover);
            _btnCap10.Label.color = cap10 ? Color.white : NativeUguiFactory.ColCream;

            _tourneyHostMembersText.text = FormatMembersStrip(_currentLobby);

            _togBotLock.Toggle.SetIsOnWithoutNotify(_lobbyRules.BotNonLethalPrestigeLock);
            _togMatch4.Toggle.SetIsOnWithoutNotify(_lobbyRules.HumanMatchEvery4Rounds);
            _togLastHuman.Toggle.SetIsOnWithoutNotify(_lobbyRules.LastHumanStandingMode);
            _togAllowTendency.Toggle.SetIsOnWithoutNotify(_lobbyRules.AllowTendencySelection);

            SyncEditorWidgets(_lobbyWidgets, _lobbyRules, _lobbyBuf);
            _btnSaveLobbyBottom.Button.interactable = !_isBusy;
        }
        else
        {
            var r = (_currentLobby.Rules ?? _lobbyRules).Clone();
            var sum = SummarizeRules(r, isTournament: true);
            string badge = sum.IsPowerModified ? "[娱乐局] 自定义娱乐对局（超模隔离）" : "[标准局] 标准合规对局";
            string members = FormatMembersStrip(_currentLobby);

            var sb = new System.Text.StringBuilder(512);
            sb.AppendLine($"赛制规模：{(r.PlayerCap == 10 ? "10人缓冲赛制" : "8人标准赛制")}（上限 {r.PlayerCap} 人）   |   {badge}");
            if (!string.IsNullOrEmpty(members)) sb.AppendLine(members);
            sb.AppendLine();
            sb.AppendLine($"- 机器人锁1声望不致死：{(r.BotNonLethalPrestigeLock ? "[开启]" : "[关闭]")}");
            sb.AppendLine($"- 全随机+4轮内必遇真人：{(r.HumanMatchEvery4Rounds ? "[开启]" : "[关闭]")}");
            sb.AppendLine($"- 真人决战到底吃鸡：{(r.LastHumanStandingMode ? "[开启]" : "[关闭]")}");
            sb.AppendLine($"- 野怪掉落全池任选1件：{(r.MonsterLootChooseOne ? "[默认开启]" : "[关闭(原版单掉)]")}");
            sb.AppendLine($"- 允许选手自选0-3流派：{(r.AllowTendencySelection ? "[开启]" : "[关闭]")}");
            sb.AppendLine($"- 开局数值：金币 {r.StartingGold} / 收入 {r.StartingIncome} / 经验 {r.StartingExperience} / 声望 {r.StartingPrestige} / 生命 {r.StartingHealth}");

            if (r.StartingItems.Count > 0)
            {
                sb.AppendLine("- 开局固定物品：" + string.Join("、", r.StartingItems.Select(x => $"{ResolveItemName(x.TemplateId)}({x.Tier})")));
            }
            if (r.StartingSkills.Count > 0)
            {
                sb.AppendLine("- 开局固定技能：" + string.Join("、", r.StartingSkills.Select(x => $"{ResolveSkillName(x.TemplateId)}({x.Tier})")));
            }

            var multDiffs = r.EncounterCategoryMultipliers
                .Where(kv => Math.Abs(kv.Value - 1.0) > 0.01)
                .Select(kv => $"{kv.Key}={kv.Value:F1}x")
                .ToList();
            if (multDiffs.Count > 0)
            {
                sb.AppendLine("- 事件倍率调整：" + string.Join("，", multDiffs));
            }
            var modStatuses = new List<(string ModId, string Status)>();
            if (_currentLobby.RequiredMods != null && _currentLobby.RequiredMods.Count > 0)
            {
                foreach (var m in _currentLobby.RequiredMods)
                {
                    string display = string.IsNullOrWhiteSpace(m.Name) ? m.ModId : $"{m.Name}({m.ModId})";
                    modStatuses.Add((display, m.AvailableOnServer ? "服务端就绪" : "服务端缺失"));
                }
            }
            else if (r.SelectedModIds != null && r.SelectedModIds.Count > 0)
            {
                foreach (var id in r.SelectedModIds)
                    modStatuses.Add((id, "待同步"));
            }

            sb.AppendLine();
            sb.AppendLine(LobbyMemberRulesSummaryFormatter.FormatSummary(
                GetModeDisplayZh(r.SpecialModeId, r.PresetId),
                r.PresetId,
                r.MutatorIds,
                r.BannedEncounterIds,
                modStatuses,
                r.EnemyStatMultiplier,
                r.ShopPriceMultiplier));
            if (Math.Abs(r.ShopHighTierBonusPct) > 0.01 || r.MonsterLootBonusChoices != 0 || r.MonsterLootTierBonus != 0)
            {
                sb.AppendLine($"- 商店高阶率加成：{r.ShopHighTierBonusPct:+0.#;-0.#;0}% | 野怪额外选项：+{r.MonsterLootBonusChoices} | 野怪品阶加成：+{r.MonsterLootTierBonus}");
            }
            _tourneyMemberSummaryText.text = sb.ToString();
        }
    }

    private void RefreshPresetSlotsUi()
    {
        var store = TournamentPresetStore.LoadSnapshot();
        if (_presetMemoryStatusText != null)
        {
            if (store.LastTournamentRules != null)
            {
                var memSum = SummarizeRules(store.LastTournamentRules, isTournament: true);
                _presetMemoryStatusText.text =
                    $"自动记忆状态：已保存上次房主规则（{(memSum.IsCustomActive ? memSum.SummaryText : "官方默认")}） · 配置文件：{TournamentPresetStore.FilePath}";
                _presetMemoryStatusText.color = NativeUguiFactory.ColEmeraldText;
            }
            else
            {
                _presetMemoryStatusText.text =
                    $"自动记忆状态：尚无历史记忆（首次保存锦标赛规则后自动写入 {TournamentPresetStore.FilePath}）";
                _presetMemoryStatusText.color = NativeUguiFactory.ColMuted;
            }
        }

        for (int i = 0; i < TournamentPresetStore.SlotCount && i < store.Presets.Count; i++)
        {
            var slot = store.Presets[i];
            var w = _presetSlots[i];
            if (w == null) continue;

            if (!w.NameInput.isFocused)
            {
                w.NameInput.SetTextWithoutNotify(slot.Name ?? $"预设方案 {i + 1}");
            }

            bool hasData = slot.Rules != null;
            if (hasData)
            {
                var sum = SummarizeRules(slot.Rules, isTournament: true);
                w.SummaryText.text = sum.IsCustomActive ? $"[自定义] {sum.SummaryText}" : "[默认] 官方默认配置";
                w.SummaryText.color = sum.IsCustomActive ? NativeUguiFactory.ColCustomPillText : NativeUguiFactory.ColDefaultPillText;
            }
            else
            {
                w.SummaryText.text = "（空槽位 — 可点击「存入当前」保存当前锦标赛规则）";
                w.SummaryText.color = NativeUguiFactory.ColMuted;
            }

            w.LoadBtn.Button.interactable = hasData && !_isBusy;
            w.ClearBtn.Button.interactable = hasData && !_isBusy;
            w.SaveBtn.Button.interactable = !_isBusy;
        }
    }

    private bool RefreshArchetypeGrid(ArchetypeGridWidgets w, CustomRunRuleSet rules)
    {
        if (w == null || rules == null) return false;

        string hero = DetectCurrentHero();
        bool isOpeningWindow = w != _preRunArchetypeGrid && IsOpeningEditableInRun();
        bool lockedInRun = w != _preRunArchetypeGrid && IsPersonalTendencyLockedInActiveRun();
        bool tourneyDisallowed = w.IsPersonalSharedCard && _currentLobby?.Rules != null && !_currentLobby.Rules.AllowTendencySelection;
        bool editable = !lockedInRun && !tourneyDisallowed;
        ulong effectiveMask = lockedInRun
            ? (_activeRunAllowTendency ? _activeRunLockedMask : 0UL)
            : rules.SelectedArchetypeMask;
        int selectedCount = CountBits(effectiveMask);

        string prefix = w.IsPersonalSharedCard ? "我的流派倾向" : "流派倾向多选";
        string lockTag = lockedInRun
            ? " [已锁定 · 途中不可改]"
            : (tourneyDisallowed ? " [房主已禁自选]" : (isOpeningWindow ? " [开场待锁定]" : ""));
        w.HeaderText.text = $"{prefix}{lockTag}（0-3 项，已选 {selectedCount}/3 · {FormatHeroZh(hero)}）";

        if (w.LockNowBtn != null)
        {
            w.LockNowBtn.Root.SetActive(isOpeningWindow && _activeRunAllowTendency);
            w.LockNowBtn.Button.interactable = !_isBusy;
        }
        if (w.ClearBtn != null)
        {
            w.ClearBtn.Button.interactable = editable && !_isBusy;
        }
        if (w.PromptBeforeRunToggle != null)
        {
            w.PromptBeforeRunToggle.Toggle.SetIsOnWithoutNotify(rules.PromptTendencyBeforeRun);
        }

        string fp = _catalog.Archetypes == null
            ? "empty"
            : $"{_catalog.Archetypes.Count}:{string.Join(",", _catalog.Archetypes.Select(a => a.Key))}";

        bool rebuilt = false;
        if (!string.Equals(w.BuiltFingerprint, fp, StringComparison.Ordinal))
        {
            rebuilt = true;
            w.BuiltFingerprint = fp;
            ClearChildren(w.GridContainer);
            w.Buttons.Clear();

            if (_catalog.Archetypes != null)
            {
                foreach (var arch in _catalog.Archetypes)
                {
                    var captured = arch;
                    var btn = _ui.CreateButton(
                        "Arch_" + arch.Key,
                        w.GridContainer,
                        arch.NameZh,
                        13f,
                        NativeUguiFactory.ColSubCardBg,
                        NativeUguiFactory.ColBtnHover,
                        NativeUguiFactory.ColCream,
                        () => OnArchetypeChipClicked(captured, w.GetRules(), w),
                        borderColor: NativeUguiFactory.ColGoldDim,
                        align: TextAlignmentOptions.MidlineLeft,
                        padH: 10,
                        padV: 4);
                    w.Buttons.Add((arch, btn));
                }
            }
        }

        foreach (var (arch, btn) in w.Buttons)
        {
            int count = arch.GetHeroCount(hero);
            ulong bitMask = (arch.Bit >= 0 && arch.Bit < 64) ? (1UL << arch.Bit) : 0UL;
            bool isSelected = bitMask != 0UL && (effectiveMask & bitMask) != 0UL;
            bool zeroCards = count <= 0;

            btn.Button.interactable = editable && (!zeroCards || isSelected) && bitMask != 0UL;
            string mark = isSelected ? "[已选] " : "";
            string cardTag = zeroCards ? "(0张 置灰)" : $"({count}张可用)";
            btn.Label.text = $"{mark}{arch.NameZh} {cardTag}\n<size=11><color=#ADA498>{Truncate(arch.DescZh, 22)}</color></size>";

            if ((zeroCards && !isSelected) || (!editable && !isSelected))
            {
                NativeUguiFactory.ApplyButtonColors(btn.Button, NativeUguiFactory.ColBtnDisabled, NativeUguiFactory.ColBtnDisabled);
                btn.Label.color = new Color(0.50f, 0.50f, 0.54f, 1f);
                if (btn.Border != null) btn.Border.color = new Color(0.25f, 0.25f, 0.28f, 0.6f);
            }
            else if (isSelected)
            {
                NativeUguiFactory.ApplyButtonColors(btn.Button, NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover);
                btn.Label.color = Color.white;
                if (btn.Border != null) btn.Border.color = NativeUguiFactory.ColGold;
            }
            else
            {
                NativeUguiFactory.ApplyButtonColors(btn.Button, NativeUguiFactory.ColSubCardBg, NativeUguiFactory.ColBtnHover);
                btn.Label.color = NativeUguiFactory.ColCream;
                if (btn.Border != null) btn.Border.color = new Color(0.45f, 0.39f, 0.25f, 0.7f);
            }
        }
        return rebuilt;
    }

    private bool SyncEditorWidgets(RuleEditorWidgets w, CustomRunRuleSet rules, RuleEditorBuffer buf)
    {
        if (w == null || rules == null) return false;
        rules.EnsureCollections();

        w.MonsterLootToggle.Toggle.SetIsOnWithoutNotify(rules.MonsterLootChooseOne);
        SyncStatsInputs(w, rules, buf);

        w.ItemHeroOnlyToggle.Toggle.SetIsOnWithoutNotify(buf.ItemHeroFilterOnly);
        w.SkillHeroOnlyToggle.Toggle.SetIsOnWithoutNotify(buf.SkillHeroFilterOnly);
        if (!w.ItemSearchInput.isFocused) w.ItemSearchInput.SetTextWithoutNotify(buf.ItemSearch ?? "");
        if (!w.SkillSearchInput.isFocused) w.SkillSearchInput.SetTextWithoutNotify(buf.SkillSearch ?? "");
        w.ItemEnchantLabel.text = Enchantments[Math.Clamp(buf.ItemEnchantIdx, 0, Enchantments.Length - 1)].LabelZh;

        RefreshTierButtons(w.ItemTierButtons, buf.ItemTierIdx);
        RefreshTierButtons(w.SkillTierButtons, buf.SkillTierIdx);

        bool changed = false;
        changed |= RebuildCardMatches(w, isSkill: false, rules, buf, () =>
        {
            if (w.IsTournament) _lobbyDirty = true;
            RefreshSummaryIndicators();
        });
        changed |= RebuildCardMatches(w, isSkill: true, rules, buf, () =>
        {
            if (w.IsTournament) _lobbyDirty = true;
            RefreshSummaryIndicators();
        });
        changed |= RebuildSelectedCards(w, isSkill: false, rules, () =>
        {
            if (w.IsTournament) _lobbyDirty = true;
            RefreshSummaryIndicators();
        });
        changed |= RebuildSelectedCards(w, isSkill: true, rules, () =>
        {
            if (w.IsTournament) _lobbyDirty = true;
            RefreshSummaryIndicators();
        });

        string prevCatFp = w.BuiltCategoriesFingerprint;
        EnsureMultiplierRowsBuilt(w, w.GetRules, () =>
        {
            if (w.IsTournament) _lobbyDirty = true;
            RefreshSummaryIndicators();
        });
        changed |= !string.Equals(prevCatFp, w.BuiltCategoriesFingerprint, StringComparison.Ordinal);
        SyncMultipliersUi(w, rules);
        changed |= SyncWave23AdvancedControlsUi(w, rules);
        return changed;
    }

    private bool SyncWave23AdvancedControlsUi(RuleEditorWidgets w, CustomRunRuleSet rules)
    {
        if (w == null || rules == null) return false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        if (w.ActiveModePresetLabel != null)
            w.ActiveModePresetLabel.text = $"{GetModeDisplayZh(rules.SpecialModeId, rules.PresetId)} ({rules.PresetId})";
        if (w.ShopHighTierInput != null && !w.ShopHighTierInput.isFocused)
            w.ShopHighTierInput.SetTextWithoutNotify(rules.ShopHighTierBonusPct.ToString("0.#", inv));
        if (w.ShopPriceMultInput != null && !w.ShopPriceMultInput.isFocused)
            w.ShopPriceMultInput.SetTextWithoutNotify(rules.ShopPriceMultiplier.ToString("0.##", inv));
        if (w.LootBonusChoicesInput != null && !w.LootBonusChoicesInput.isFocused)
            w.LootBonusChoicesInput.SetTextWithoutNotify(rules.MonsterLootBonusChoices.ToString(inv));
        if (w.LootTierBonusInput != null && !w.LootTierBonusInput.isFocused)
            w.LootTierBonusInput.SetTextWithoutNotify(rules.MonsterLootTierBonus.ToString(inv));
        if (w.EnemyStatMultInput != null && !w.EnemyStatMultInput.isFocused)
            w.EnemyStatMultInput.SetTextWithoutNotify(rules.EnemyStatMultiplier.ToString("0.##", inv));
        if (w.EndlessMaxDayInput != null && !w.EndlessMaxDayInput.isFocused)
            w.EndlessMaxDayInput.SetTextWithoutNotify(rules.EndlessMaxDay.ToString(inv));
        if (w.CrossHeroBiasInput != null && !w.CrossHeroBiasInput.isFocused)
            w.CrossHeroBiasInput.SetTextWithoutNotify(rules.CrossHeroOwnHeroBias.ToString("0.##", inv));
        if (w.BossRushWavesInput != null && !w.BossRushWavesInput.isFocused)
            w.BossRushWavesInput.SetTextWithoutNotify(rules.BossRushTotalWaves.ToString(inv));
        if (w.ArchetypeWeightInput != null && !w.ArchetypeWeightInput.isFocused)
            w.ArchetypeWeightInput.SetTextWithoutNotify(rules.ArchetypeWeightMultiplier.ToString("0.#", inv));
        if (w.ArchetypeCapInput != null && !w.ArchetypeCapInput.isFocused)
            w.ArchetypeCapInput.SetTextWithoutNotify(rules.ArchetypeMultiMatchCap.ToString("0.#", inv));
        if (w.FixedSeedInput != null && !w.FixedSeedInput.isFocused)
            w.FixedSeedInput.SetTextWithoutNotify(rules.FixedRunSeed.HasValue ? rules.FixedRunSeed.Value.ToString(inv) : string.Empty);

        w.DisableFatesReviveToggle?.Toggle?.SetIsOnWithoutNotify(rules.DisableFatesRevive);
        w.BossRushPvpToggle?.Toggle?.SetIsOnWithoutNotify(rules.BossRushIncludeMilestonePvp);
        w.SandboxRerollToggle?.Toggle?.SetIsOnWithoutNotify(rules.SandboxFreeInfiniteReroll);
        w.SandboxZeroPriceToggle?.Toggle?.SetIsOnWithoutNotify(rules.SandboxZeroShopPrice);

        if (w.MutatorsContainer == null) return false;
        var mutList = _catalog?.Mutators;
        string fp = (mutList == null ? "0" : mutList.Count.ToString(inv)) + "|" + string.Join(",", rules.MutatorIds);
        if (string.Equals(fp, w.BuiltMutatorsFingerprint, StringComparison.Ordinal)) return false;
        w.BuiltMutatorsFingerprint = fp;

        ClearChildren(w.MutatorsContainer);
        if (mutList == null || mutList.Count == 0)
        {
            _ui.CreateText("MutEmpty", w.MutatorsContainer, "待拉取服务端规则目录后展示可选肉鸽词条。", 12f, NativeUguiFactory.ColMuted);
            return true;
        }

        GameObject curRow = null;
        for (int i = 0; i < mutList.Count; i++)
        {
            if (i % 4 == 0)
            {
                curRow = _ui.CreateHorizontalRow("MutRow_" + (i / 4), w.MutatorsContainer, spacing: 5f);
            }
            var m = mutList[i];
            if (m == null || string.IsNullOrWhiteSpace(m.Id)) continue;
            bool selected = rules.MutatorIds.Exists(id => string.Equals(id, m.Id, StringComparison.OrdinalIgnoreCase));
            var capturedMut = m;
            var btn = _ui.CreateButton("Mut_" + m.Id, curRow.transform,
                (selected ? "✓ " : "") + (string.IsNullOrWhiteSpace(m.NameZh) ? m.Id : m.NameZh),
                11.5f,
                selected ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
                selected ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover,
                selected ? Color.white : NativeUguiFactory.ColCream,
                () =>
                {
                    var r = w.GetRules();
                    if (r == null) return;
                    r.EnsureCollections();
                    int idx = r.MutatorIds.FindIndex(id => string.Equals(id, capturedMut.Id, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        r.MutatorIds.RemoveAt(idx);
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(capturedMut.ConflictGroup) && _catalog?.Mutators != null)
                        {
                            for (int k = r.MutatorIds.Count - 1; k >= 0; k--)
                            {
                                var other = _catalog.Mutators.Find(x => string.Equals(x.Id, r.MutatorIds[k], StringComparison.OrdinalIgnoreCase));
                                if (other != null && string.Equals(other.ConflictGroup, capturedMut.ConflictGroup, StringComparison.OrdinalIgnoreCase))
                                {
                                    r.MutatorIds.RemoveAt(k);
                                }
                            }
                        }
                        r.MutatorIds.Add(capturedMut.Id);
                    }
                    r.RefreshPowerFlag();
                    if (w.IsTournament) _lobbyDirty = true;
                    SyncWave23AdvancedControlsUi(w, r);
                    RefreshSummaryIndicators();
                }, padH: 5, padV: 2);
            NativeUguiFactory.SetLayout(btn.Root, minW: 110f, flexW: 1f, minH: 25f);
        }
        return true;
    }

    private static void SyncStatsInputs(RuleEditorWidgets w, CustomRunRuleSet rules, RuleEditorBuffer buf)
    {
        if (w == null || rules == null) return;
        buf?.SyncFromRules(rules);
        if (!w.GoldInput.isFocused) w.GoldInput.SetTextWithoutNotify(rules.StartingGold.ToString());
        if (!w.IncomeInput.isFocused) w.IncomeInput.SetTextWithoutNotify(rules.StartingIncome.ToString());
        if (!w.ExpInput.isFocused) w.ExpInput.SetTextWithoutNotify(rules.StartingExperience.ToString());
        if (!w.PrestigeInput.isFocused) w.PrestigeInput.SetTextWithoutNotify(rules.StartingPrestige.ToString());
        if (!w.HealthInput.isFocused) w.HealthInput.SetTextWithoutNotify(rules.StartingHealth.ToString());
    }

    private static void RefreshTierButtons(NativeUguiFactory.ButtonHandle[] buttons, int activeIdx)
    {
        for (int i = 0; i < buttons.Length; i++)
        {
            var b = buttons[i];
            if (b == null) continue;
            bool on = i == activeIdx;
            NativeUguiFactory.ApplyButtonColors(b.Button,
                on ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
                on ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover);
            b.Label.color = on ? Color.white : NativeUguiFactory.ColCream;
        }
    }

    private bool RebuildCardMatches(
        RuleEditorWidgets w, bool isSkill, CustomRunRuleSet rules, RuleEditorBuffer buf, Action onChanged)
    {
        string hero = DetectCurrentHero();
        var source = isSkill ? _catalog.Skills : _catalog.Items;
        string query = isSkill ? buf.SkillSearch : buf.ItemSearch;
        bool heroOnly = isSkill ? buf.SkillHeroFilterOnly : buf.ItemHeroFilterOnly;

        string fp = $"{_catalogLoaded}|{source?.Count ?? 0}|{(heroOnly ? hero : "*")}|{query ?? ""}";
        string prevFp = isSkill ? w.BuiltSkillMatchesFingerprint : w.BuiltItemMatchesFingerprint;
        if (string.Equals(prevFp, fp, StringComparison.Ordinal))
        {
            return false;
        }
        if (isSkill) w.BuiltSkillMatchesFingerprint = fp;
        else w.BuiltItemMatchesFingerprint = fp;

        Transform container = isSkill ? w.SkillMatchesContainer : w.ItemMatchesContainer;
        ClearChildren(container);

        var matches = FilterCatalog(source, query, heroOnly ? hero : null, 8);
        if (matches.Count == 0)
        {
            _ui.CreateText("Empty", container, _catalogLoaded ? "无匹配结果（尝试清空搜索或取消仅当前英雄过滤）" : "目录加载中...", 12.5f, NativeUguiFactory.ColMuted);
            return true;
        }

        foreach (var entry in matches)
        {
            var cap = entry;
            var row = _ui.CreateHorizontalRow("Match", container, spacing: 6f);
            string tag = isSkill ? $"{cap.Hero}/{cap.StartingTier}" : $"{cap.Hero}/{cap.Size}/{cap.StartingTier}";
            var lbl = _ui.CreateText("Name", row.transform, $"{cap.NameZh} [{tag}]", 13f, NativeUguiFactory.ColCream, wrap: false);
            NativeUguiFactory.SetLayout(lbl.gameObject, flexW: 1f, minH: 24f);

            var addBtn = _ui.CreateButton("Add", row.transform, "+ 添加", 12.5f,
                NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColGold, () =>
                {
                    rules.EnsureCollections();
                    if (isSkill)
                    {
                        string selectedTier = Tiers[Math.Clamp(buf.SkillTierIdx, 0, Tiers.Length - 1)];
                        // P1-44：技能按 TemplateId 去重，避免重复添加同一技能导致客户端 SkillPresentationManager._skillProxyLookup 抛键冲突异常
                        var existing = rules.StartingSkills.FirstOrDefault(
                            s => s != null && string.Equals(s.TemplateId, cap.Id, StringComparison.OrdinalIgnoreCase));
                        if (existing != null)
                        {
                            existing.Tier = selectedTier;
                            SetStatus($"技能「{cap.NameZh}」已在开局技能列表中，已更新为 {selectedTier} 品质（同技能不可重复添加）。", isError: false);
                            ShowToastBanner($"已更新开局技能「{cap.NameZh}」为 {selectedTier}（自动去重）");
                        }
                        else
                        {
                            rules.StartingSkills.Add(new StartingSkillRule
                            {
                                TemplateId = cap.Id,
                                Tier = selectedTier,
                            });
                        }
                    }
                    else
                    {
                        string ench = Enchantments[Math.Clamp(buf.ItemEnchantIdx, 0, Enchantments.Length - 1)].Key;
                        rules.StartingItems.Add(new StartingItemRule
                        {
                            TemplateId = cap.Id,
                            Tier = Tiers[Math.Clamp(buf.ItemTierIdx, 0, Tiers.Length - 1)],
                            Enchantment = string.IsNullOrEmpty(ench) ? null : ench,
                        });
                    }
                    RebuildSelectedCards(w, isSkill, rules, onChanged);
                    onChanged?.Invoke();
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_mainScroll.Content);
                }, fontStyle: FontStyles.Bold, padH: 8, padV: 2);
            NativeUguiFactory.SetLayout(addBtn.Root, minW: 70f, prefW: 74f, minH: 24f);
        }
        return true;
    }

    private static void DeduplicateStartingSkillsInPlace(CustomRunRuleSet rules)
    {
        if (rules?.StartingSkills == null || rules.StartingSkills.Count <= 1) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rules.StartingSkills.Count;)
        {
            var entry = rules.StartingSkills[i];
            if (entry == null || string.IsNullOrWhiteSpace(entry.TemplateId) || !seen.Add(entry.TemplateId.Trim()))
            {
                rules.StartingSkills.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    private bool RebuildSelectedCards(RuleEditorWidgets w, bool isSkill, CustomRunRuleSet rules, Action onChanged)
    {
        rules.EnsureCollections();
        string fp;
        if (isSkill)
        {
            DeduplicateStartingSkillsInPlace(rules);
            fp = $"{_catalogLoaded}|{rules.StartingSkills.Count}:{string.Join(",", rules.StartingSkills.Select(s => $"{s.TemplateId}/{s.Tier}"))}";
            if (string.Equals(w.BuiltSelectedSkillsFingerprint, fp, StringComparison.Ordinal))
            {
                w.SelectedSkillsHeader.text = $"开局固定技能（已选 {rules.StartingSkills.Count} 个）";
                return false;
            }
            w.BuiltSelectedSkillsFingerprint = fp;
        }
        else
        {
            fp = $"{_catalogLoaded}|{rules.StartingItems.Count}:{string.Join(",", rules.StartingItems.Select(s => $"{s.TemplateId}/{s.Tier}/{s.Enchantment ?? ""}"))}";
            if (string.Equals(w.BuiltSelectedItemsFingerprint, fp, StringComparison.Ordinal))
            {
                w.SelectedItemsHeader.text = $"开局固定物品（已选 {rules.StartingItems.Count} 件）";
                return false;
            }
            w.BuiltSelectedItemsFingerprint = fp;
        }

        Transform container = isSkill ? w.SelectedSkillsContainer : w.SelectedItemsContainer;
        ClearChildren(container);

        if (isSkill)
        {
            w.SelectedSkillsHeader.text = $"开局固定技能（已选 {rules.StartingSkills.Count} 个）";
            if (rules.StartingSkills.Count == 0) return true;
            _ui.CreateText("Lbl", container, "已配置的开局技能：", 12.5f, NativeUguiFactory.ColMuted);

            for (int i = 0; i < rules.StartingSkills.Count; i++)
            {
                int idx = i;
                var sk = rules.StartingSkills[i];
                var row = _ui.CreateHorizontalRow("SelSkill", container, spacing: 6f);
                var lbl = _ui.CreateText("Name", row.transform, $"- {ResolveSkillName(sk.TemplateId)}（{TranslateTierZh(sk.Tier)}）", 13f, NativeUguiFactory.ColGold, wrap: false);
                NativeUguiFactory.SetLayout(lbl.gameObject, flexW: 1f, minH: 24f);

                var rmBtn = _ui.CreateButton("Remove", row.transform, "移除", 12.5f,
                    NativeUguiFactory.ColDangerBg, new Color(0.56f, 0.16f, 0.18f, 1f), NativeUguiFactory.ColCream, () =>
                    {
                        if (idx >= 0 && idx < rules.StartingSkills.Count)
                        {
                            rules.StartingSkills.RemoveAt(idx);
                            RebuildSelectedCards(w, isSkill: true, rules, onChanged);
                            onChanged?.Invoke();
                            LayoutRebuilder.ForceRebuildLayoutImmediate(_mainScroll.Content);
                        }
                    }, padH: 6, padV: 2);
                NativeUguiFactory.SetLayout(rmBtn.Root, minW: 58f, prefW: 60f, minH: 24f);
            }
        }
        else
        {
            w.SelectedItemsHeader.text = $"开局固定物品（已选 {rules.StartingItems.Count} 件）";
            if (rules.StartingItems.Count == 0) return true;
            _ui.CreateText("Lbl", container, "已配置的开局物品：", 12.5f, NativeUguiFactory.ColMuted);

            for (int i = 0; i < rules.StartingItems.Count; i++)
            {
                int idx = i;
                var st = rules.StartingItems[i];
                string enchTag = string.IsNullOrEmpty(st.Enchantment) ? "" : $" [{FindEnchantLabel(st.Enchantment)}]";
                var row = _ui.CreateHorizontalRow("SelItem", container, spacing: 6f);
                var lbl = _ui.CreateText("Name", row.transform, $"- {ResolveItemName(st.TemplateId)}（{TranslateTierZh(st.Tier)}）{enchTag}", 13f, NativeUguiFactory.ColGold, wrap: false);
                NativeUguiFactory.SetLayout(lbl.gameObject, flexW: 1f, minH: 24f);

                var rmBtn = _ui.CreateButton("Remove", row.transform, "移除", 12.5f,
                    NativeUguiFactory.ColDangerBg, new Color(0.56f, 0.16f, 0.18f, 1f), NativeUguiFactory.ColCream, () =>
                    {
                        if (idx >= 0 && idx < rules.StartingItems.Count)
                        {
                            rules.StartingItems.RemoveAt(idx);
                            RebuildSelectedCards(w, isSkill: false, rules, onChanged);
                            onChanged?.Invoke();
                            LayoutRebuilder.ForceRebuildLayoutImmediate(_mainScroll.Content);
                        }
                    }, padH: 6, padV: 2);
                NativeUguiFactory.SetLayout(rmBtn.Root, minW: 58f, prefW: 60f, minH: 24f);
            }
        }
        return true;
    }

    private List<EncounterCategoryEntry> GetActiveCategories()
        => (_catalog.EncounterCategories != null && _catalog.EncounterCategories.Count > 0)
            ? _catalog.EncounterCategories
            : FallbackEncounterCategories;

    private void EnsureMultiplierRowsBuilt(RuleEditorWidgets w, Func<CustomRunRuleSet> getRules, Action onChanged)
    {
        var categories = GetActiveCategories();
        string fp = string.Join(",", categories.Select(c => c.Key));
        if (string.Equals(w.BuiltCategoriesFingerprint, fp, StringComparison.Ordinal)) return;

        w.BuiltCategoriesFingerprint = fp;
        ClearChildren(w.MultipliersGroupContainer);
        w.MultiplierControls.Clear();

        foreach (var group in categories.GroupBy(c => string.IsNullOrWhiteSpace(c.GroupZh) ? "其他" : c.GroupZh))
        {
            var gCard = _ui.CreateCard("Group_" + group.Key, w.MultipliersGroupContainer, NativeUguiFactory.ColSubCardBg, null, padH: 10, padV: 8, spacing: 5f);
            _ui.CreateText("GroupTitle", gCard.transform, $"· {group.Key}", 14f, NativeUguiFactory.ColGold, FontStyles.Bold);

            foreach (var cat in group)
            {
                var capCat = cat;
                var row = _ui.CreateHorizontalRow("Cat_" + cat.Key, gCard.transform, spacing: 8f);

                var nameLbl = _ui.CreateText("Name", row.transform, cat.NameZh, 13.5f, NativeUguiFactory.ColCream, wrap: false);
                NativeUguiFactory.SetLayout(nameLbl.gameObject, minW: 230f, prefW: 240f, minH: 26f);

                TextMeshProUGUI valLbl = null;
                Slider slider = null;

                slider = _ui.CreateSlider("Slider", row.transform, 0f, 5f, (float)cat.DefaultMult, rawVal =>
                {
                    double snapped = Math.Round(rawVal * 10.0) / 10.0;
                    var r = getRules();
                    if (r != null)
                    {
                        r.EnsureCollections();
                        r.EncounterCategoryMultipliers[capCat.Key] = snapped;
                    }
                    UpdateMultiplierLabel(valLbl, snapped);
                    onChanged?.Invoke();
                });
                NativeUguiFactory.SetLayout(slider.gameObject, minW: 320f, flexW: 1f, minH: 26f);

                valLbl = _ui.CreateText("Val", row.transform, $"{cat.DefaultMult:F1}x", 13.5f, NativeUguiFactory.ColMuted, wrap: false);
                NativeUguiFactory.SetLayout(valLbl.gameObject, minW: 118f, prefW: 122f, minH: 26f);

                var btn0 = _ui.CreateButton("Btn0", row.transform, "禁用 0x", 12.5f,
                    NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
                    {
                        var r = getRules();
                        if (r != null)
                        {
                            r.EnsureCollections();
                            r.EncounterCategoryMultipliers[capCat.Key] = 0.0;
                        }
                        slider.SetValueWithoutNotify(0f);
                        UpdateMultiplierLabel(valLbl, 0.0);
                        onChanged?.Invoke();
                    }, padH: 6, padV: 2);
                NativeUguiFactory.SetLayout(btn0.Root, minW: 72f, prefW: 74f, minH: 24f);

                var btn1 = _ui.CreateButton("Btn1", row.transform, "标准 1x", 12.5f,
                    NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
                    {
                        var r = getRules();
                        if (r != null)
                        {
                            r.EnsureCollections();
                            r.EncounterCategoryMultipliers[capCat.Key] = 1.0;
                        }
                        slider.SetValueWithoutNotify(1f);
                        UpdateMultiplierLabel(valLbl, 1.0);
                        onChanged?.Invoke();
                    }, padH: 6, padV: 2);
                NativeUguiFactory.SetLayout(btn1.Root, minW: 72f, prefW: 74f, minH: 24f);

                w.MultiplierControls[cat.Key] = (slider, valLbl);
            }
        }
    }

    private void SyncMultipliersUi(RuleEditorWidgets w, CustomRunRuleSet rules)
    {
        if (w == null || rules == null) return;
        rules.EnsureCollections();
        foreach (var cat in GetActiveCategories())
        {
            if (!w.MultiplierControls.TryGetValue(cat.Key, out var pair)) continue;
            double cur = rules.EncounterCategoryMultipliers.TryGetValue(cat.Key, out double mv) ? mv : cat.DefaultMult;
            pair.Slider.SetValueWithoutNotify((float)cur);
            UpdateMultiplierLabel(pair.ValLabel, cur);
        }
    }

    private static void UpdateMultiplierLabel(TextMeshProUGUI lbl, double val)
    {
        if (lbl == null) return;
        lbl.text = val <= 0.01 ? "0.0x（已禁用）" : $"{val:F1}x";
        lbl.color = Math.Abs(val - 1.0) > 0.01 ? NativeUguiFactory.ColGold : NativeUguiFactory.ColMuted;
    }

    private void RefreshStatusBarUi()
    {
        if (_bottomStatusText == null) return;
        _bottomStatusText.text = _statusIsError ? $"[警告] {_statusMessage}" : $"[状态] {_statusMessage}";
        _bottomStatusText.color = _statusIsError ? NativeUguiFactory.ColErrorText : NativeUguiFactory.ColMuted;
    }

    // ── UI 事件回调 ─────────────────────────────────────────────────────────

    private void OnMenuPillClicked()
    {
        if (IsTournamentUiContext())
        {
            _activeTab = UiTab.Tournament;
        }
        OpenPanel();
    }

    private void OnInRunRuleBadgeClicked()
    {
        if (IsInActiveCombatPlayback()) return;
        _activeTab = IsActiveRunTournamentMode() ? UiTab.Tournament : UiTab.Normal;
        ToggleOpen();
    }

    private void OnCombatSpeedChipClicked(float speed)
    {
        CombatPatch.SetManualUiSpeed(speed);
        RefreshHudOverlays(force: true);
    }

    private void SwitchTab(UiTab tab)
    {
        _activeTab = tab;
        if (tab == UiTab.Tournament)
        {
            _nextLobbyPollTime = 0f;
            RefreshTournamentLobbyAsync(forceOverwriteHostDraft: false);
        }
        RefreshModalContentsIfOpen();
    }

    private void OnRefreshClicked()
    {
        if (_activeTab == UiTab.Tournament)
        {
            RefreshTournamentLobbyAsync(forceOverwriteHostDraft: true);
        }
        else if (_activeTab == UiTab.Workshop)
        {
            BazaarLauncher.Core.ModContentSyncClient.Instance.TriggerFullSyncInBackground(forceDownload: false);
            RefreshWorkshopTabUi();
        }
        else if (_activeTab == UiTab.Social)
        {
            RefreshSocialTabData();
        }
        else if (_activeTab == UiTab.Performance)
        {
            BazaarLauncher.Modules.PerfOptimizerModule.Instance?.TriggerPingProbeBackground();
            RefreshPerformanceTabFull();
        }
        else
        {
            StartInitialBackgroundFetch();
        }
    }

    private void OnOneClickResetOfficialClicked()
    {
        if (_activeTab == UiTab.Tournament)
        {
            TournamentPresetStore.ResetAllToOfficialDefault();
            _lobbyRules.ResetToStandardValues(preserveArchetypeAndLobbyFlags: false);
            _lobbyBuf.SyncFromRules(_lobbyRules);
            _lobbyDirty = false;
            if (_currentLobby != null && IsLocalPlayerLobbyHost(_currentLobby))
            {
                SaveLobbyRulesAsync();
            }
            else
            {
                SetStatus("已一键恢复锦标赛官方默认规则并更新本地记忆！", isError: false);
                RefreshModalContentsIfOpen();
            }
        }
        else
        {
            _normalRules.ResetToStandardValues(preserveArchetypeAndLobbyFlags: false);
            _normalBuf.SyncFromRules(_normalRules);
            ResetNormalRulesAsync();
        }
    }

    private void OnLoadLastTournamentRulesClicked()
    {
        var last = TournamentPresetStore.GetLastTournamentRules();
        if (last == null)
        {
            SetStatus("本地尚无历史锦标赛记忆规则。", isError: true);
            RefreshStatusBarUi();
            return;
        }
        _lobbyRules = last;
        _lobbyBuf.SyncFromRules(_lobbyRules);
        _lobbyDirty = true;
        SetStatus("已加载上次记忆的锦标赛规则草稿（房主点击「保存并广播」后全房生效）。", isError: false);
        RefreshModalContentsIfOpen();
    }

    private void OnResetTournamentOnlyClicked()
    {
        _lobbyRules.ResetToStandardValues(preserveArchetypeAndLobbyFlags: false);
        _lobbyBuf.SyncFromRules(_lobbyRules);
        TournamentPresetStore.ResetAllToOfficialDefault();
        _lobbyDirty = true;
        SetStatus("已重置锦标赛草稿与默认记忆为官方标准规则。", isError: false);
        RefreshModalContentsIfOpen();
    }

    private void OnSaveTournamentPresetSlot(int slotIndex)
    {
        string slotName = _presetSlots[slotIndex]?.NameInput?.text;
        TournamentPresetStore.SavePresetSlot(slotIndex, slotName, _lobbyRules);
        SetStatus($"已将当前锦标赛规则存入「{slotName}」（槽位 {slotIndex + 1}）！", isError: false);
        RefreshModalContentsIfOpen();
    }

    private void OnLoadTournamentPresetSlot(int slotIndex)
    {
        var loaded = TournamentPresetStore.LoadPresetSlotRules(slotIndex);
        if (loaded == null)
        {
            SetStatus($"槽位 {slotIndex + 1} 为空，请先存入规则。", isError: true);
            RefreshStatusBarUi();
            return;
        }
        _lobbyRules = loaded;
        _lobbyBuf.SyncFromRules(_lobbyRules);
        _lobbyDirty = true;
        TournamentPresetStore.SaveLastTournamentRules(_lobbyRules);

        if (_currentLobby != null && IsLocalPlayerLobbyHost(_currentLobby))
        {
            SaveLobbyRulesAsync();
        }
        else
        {
            SetStatus($"已加载预设槽位 {slotIndex + 1} 到当前锦标赛草稿！", isError: false);
            RefreshModalContentsIfOpen();
        }
    }

    private void OnClearTournamentPresetSlot(int slotIndex)
    {
        TournamentPresetStore.ClearPresetSlot(slotIndex);
        SetStatus($"已清空预设槽位 {slotIndex + 1}。", isError: false);
        RefreshModalContentsIfOpen();
    }

    private void OnQuickSwitchHeroClicked(string heroKey, string shortZh)
    {
        if (GameInspector.ReadHasActiveRunFlag())
        {
            SetStatus("当前有进行中对局（HasActiveRun=true），请先放弃或完成对局后再切换英雄。", isError: true);
            RefreshStatusBarUi();
            return;
        }
        GameDriver.SwitchHeroInMainMenu(heroKey);
        SetStatus($"已切换当前英雄为：{shortZh} ({heroKey})", isError: false);
        _lastToolboxKey = "";
        RefreshModalContentsIfOpen();
    }

    private void OnArchetypeChipClicked(ArchetypeCatalogEntry arch, CustomRunRuleSet rules, ArchetypeGridWidgets w)
    {
        if (arch == null || rules == null) return;
        if (w != _preRunArchetypeGrid && IsPersonalTendencyLockedInActiveRun())
        {
            SetStatus("本局流派倾向已锁定，严禁对局途中修改！", isError: true);
            RefreshStatusBarUi();
            return;
        }
        if (w != null && w.IsPersonalSharedCard && _currentLobby?.Rules != null && !_currentLobby.Rules.AllowTendencySelection)
        {
            SetStatus("当前锦标赛房主已关闭流派倾向自选。", isError: true);
            RefreshStatusBarUi();
            return;
        }

        ulong bitMask = (arch.Bit >= 0 && arch.Bit < 64) ? (1UL << arch.Bit) : 0UL;
        if (bitMask == 0UL) return;

        bool isSelected = (rules.SelectedArchetypeMask & bitMask) != 0UL;
        int selectedCount = CountBits(rules.SelectedArchetypeMask);
        if (isSelected)
        {
            rules.SelectedArchetypeMask &= ~bitMask;
        }
        else if (selectedCount < 3)
        {
            rules.SelectedArchetypeMask |= bitMask;
        }
        else
        {
            SetStatus("最多只能同时选择 3 个流派倾向！请先取消一项。", isError: true);
            RefreshStatusBarUi();
            return;
        }

        if (w != null && w.IsPersonalSharedCard && !IsOpeningEditableInRun())
        {
            SchedulePersonalTendencySync();
        }
        RefreshArchetypeGrid(w, rules);
        RefreshSummaryIndicators();
    }

    private static string FormatMembersStrip(LobbyDetailSnapshot lobby)
    {
        if (lobby?.Members == null || lobby.Members.Count == 0) return "";
        string hostAid = NameAuth.NormalizeAccountId(lobby.HostAccountId);
        var parts = lobby.Members.Select(m =>
        {
            string tag = m.IsAi ? "[AI]" : (NameAuth.NormalizeAccountId(m.AccountId) == hostAid ? "[房主]" : "[真人]");
            return $"{tag}{m.Username}";
        });
        return "当前成员：" + string.Join("  ·  ", parts);
    }

    private static void ClearChildren(Transform parent)
    {
        if (parent == null) return;
        for (int i = parent.childCount - 1; i >= 0; i--)
        {
            UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
        }
    }

    private static TourneyStatusSnapshot ResolveCurrentTourneyStatus()
    {
        if (_latestTourneyStatus != null) return _latestTourneyStatus;
        try
        {
            _cachedTssType ??= AccessTools.TypeByName("Networking.TournamentStatusService");
            if (_cachedTssType == null) return null;
            _tssCurrentStatusProp ??= _cachedTssType.GetProperty("CurrentStatus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var tssObj = UnityEngine.Object.FindFirstObjectByType(_cachedTssType);
            if (tssObj == null) return null;
            var dto = _tssCurrentStatusProp?.GetValue(tssObj);
            if (dto == null) return null;
            _latestTourneyStatus = ReadTourneyStatusSnapshot(dto);
            return _latestTourneyStatus;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime GetServerOrUtcNow()
    {
        try
        {
            if (_serverTimeProp == null)
            {
                var cstType = AccessTools.TypeByName("TheBazaar.Data");
                var prop = cstType?.GetProperty("CurrentServerTime", BindingFlags.Public | BindingFlags.Static);
                var obj = prop?.GetValue(null);
                _serverTimeProp = obj?.GetType().GetProperty("ServerTime", BindingFlags.Public | BindingFlags.Instance);
            }
            if (_serverTimeProp != null)
            {
                var cstProp = AccessTools.TypeByName("TheBazaar.Data")?.GetProperty("CurrentServerTime", BindingFlags.Public | BindingFlags.Static);
                var cstObj = cstProp?.GetValue(null);
                if (cstObj != null && _serverTimeProp.GetValue(cstObj) is DateTime dt)
                {
                    return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                }
            }
        }
        catch { }
        return DateTime.UtcNow;
    }

    private static bool TryBuildTourneyWaitBannerText(out string title, out string sub, out bool hasUrgentTimer)
    {
        title = "";
        sub = "";
        hasUrgentTimer = false;

        var status = ResolveCurrentTourneyStatus();
        bool hasRecentErr = Time.unscaledTime < _tourneyWaitErrorUntil && !string.IsNullOrEmpty(_tourneyWaitErrorNotice);

        uint runHour = 0;
        uint runDay = 0;
        try
        {
            var run = GameInspector.GetDataRun();
            if (run != null)
            {
                runHour = Convert.ToUInt32(GameInspector.GetMember(run, "Hour") ?? 0u);
                runDay = Convert.ToUInt32(GameInspector.GetMember(run, "Day") ?? 0u);
            }
        }
        catch { }

        string appState = GameInspector.GetAppStateName();
        if (status != null)
        {
            bool waitingPhase = !status.CanStartPvp
                && (status.WaitingForOtherPlayers
                    || status.WaitingForHumanDayStart
                    || runHour >= 6
                    || string.Equals(appState, "TournamentPvpChoiceState", StringComparison.Ordinal)
                    || string.Equals(status.Phase, "WaitingRoom", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status.Phase, "DayWaiting", StringComparison.OrdinalIgnoreCase)
                    || hasRecentErr);

            if (!waitingPhase) return false;

            int active = Math.Max(status.ActivePlayers, 0);
            int ready = Math.Clamp(status.ReadyPlayers, 0, Math.Max(active, status.ReadyPlayers));
            uint day = status.CurrentDay > 0 ? status.CurrentDay : runDay;

            string countdownText = "";
            if (status.DeadlineAt.HasValue)
            {
                var utcDeadline = status.DeadlineAt.Value.Kind == DateTimeKind.Utc
                    ? status.DeadlineAt.Value
                    : status.DeadlineAt.Value.ToUniversalTime();
                var rem = utcDeadline - GetServerOrUtcNow();
                int totalSec = Math.Max(0, (int)Math.Ceiling(rem.TotalSeconds));
                hasUrgentTimer = totalSec > 0 && totalSec <= 15;
                countdownText = totalSec >= 60
                    ? $"{totalSec / 60:00}:{totalSec % 60:00}"
                    : $"{totalSec}秒";
            }

            if (status.WaitingForHumanDayStart)
            {
                title = $"等待选手同步第 {Math.Max(1u, day)} 天";
            }
            else if (runHour >= 6 || string.Equals(appState, "TournamentPvpChoiceState", StringComparison.Ordinal) || status.WaitingForOtherPlayers)
            {
                title = "等待对手到达第 6 小时";
            }
            else
            {
                title = "等待锦标赛 PvP 就位";
            }

            var parts = new List<string>(3);
            if (active > 0) parts.Add($"已就绪 {ready}/{active}");
            if (!string.IsNullOrEmpty(countdownText)) parts.Add($"倒计时 {countdownText}");
            if (!string.IsNullOrWhiteSpace(status.AssignedOpponentUsername))
            {
                parts.Add($"对手 {status.AssignedOpponentUsername}");
            }
            else if (hasRecentErr)
            {
                parts.Add("非卡顿，请稍候");
            }
            sub = parts.Count > 0 ? string.Join(" · ", parts) : "正在等待其余选手完成当前回合...";
            return true;
        }

        if (hasRecentErr)
        {
            title = "等待锦标赛 PvP 同步";
            sub = _tourneyWaitErrorNotice;
            return true;
        }

        return false;
    }
}
