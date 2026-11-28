using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BazaarLauncher;

/// <summary>
/// v4「种子与每日对手」：规则页卡片 + 生成分享码 / 导入分享码 / 收藏打过的对手 / 沙盒盘面存剧本 四个二级弹窗 + 开局提示。
/// 接口见 docs/PLANNER_API_CONTRACT.md（统一前缀 /api/planner/，游戏内用 Bearer 鉴权）。
/// 网络请求全部在后台线程，结果经 _pendingUpdates 回主线程改界面；种子与剧本三个字段只经 apply-rules 修改，
/// 不随「保存并应用个人配置」整份提交（见 CustomRunRuleSet.ShouldSerialize*），所以不会冲掉网页里刚改的设置。
/// </summary>
internal sealed partial class CustomGameplayUI
{
    // ═══════════════ 数据快照 ═══════════════
    private sealed class SpLineup
    {
        public string Id = "";
        public string Name = "";
        public List<int> Days = new();
        public List<string> MissingModNames = new();
        public List<string> RequiredModIds = new();
        public string SeedText;          // 方案配套种子（可空）
    }

    private sealed class SpRun
    {
        public string RunId = "";
        public string Hero = "";
        public string HeroZh = "";
        public string SeedText;
        public int Day;
        public bool IsFinished;
        public bool IsSandbox;
        public List<int> FacedDays = new();
    }

    private sealed class SpContext
    {
        public bool Supported = true;
        public string CurrentSeedText;
        public string CustomSeedText;
        public string GhostMode = "Random";
        public string SelectedLineupId;
        public List<SpLineup> Lineups = new();
        public SpRun ActiveRun;
        public SpRun RecentRun;
        public readonly Dictionary<string, string> ServerModNames = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ServerDefaultModIds = new(StringComparer.OrdinalIgnoreCase);
    }

    private const string SpSeedAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // 与服务端 FormatSeedCode 一致
    private static readonly Color SpColWarn = new(1f, 0.80f, 0.36f, 1f);

    private SpContext _spCtx;
    private bool _spFetchInFlight;
    private bool _spRefetchQueued;
    private int _spFetchSeq;
    private float _spLastFetchAt = -999f;
    private string _spLastError = "";
    private string _spHeaderMsg = "";
    private bool _spHeaderIsErr;
    private bool _spSeedInputDirty;
    private bool _spBusy;
    private string _spActiveRunToastedId = "";
    private string _spLastSeedStatusText = "";

    // 卡片控件
    private GameObject _spCard;
    private TMP_InputField _spSeedInput;
    private TextMeshProUGUI _spHeaderStatus;
    private TextMeshProUGUI _spSeedStatus;
    private TextMeshProUGUI _spRunSeedsText;
    private TextMeshProUGUI _spOppHint;
    private Transform _spLineupList;
    private string _spLineupFp = "";
    private NativeUguiFactory.ButtonHandle _spBtnApplySeed, _spBtnRandomSeed, _spBtnClearSeed;
    private NativeUguiFactory.ButtonHandle _spBtnCopyActive, _spBtnCopyRecent;
    private NativeUguiFactory.ButtonHandle _spBtnOppRandom, _spBtnOppLineup;
    private NativeUguiFactory.ButtonHandle _spBtnSync, _spBtnWeb, _spBtnExport, _spBtnImport, _spBtnFromRun, _spBtnFavorite;

    // ═══════════════ 小工具 ═══════════════
    private static string SpStr(JToken t)
    {
        if (t == null || t.Type == JTokenType.Null || t.Type == JTokenType.Undefined) return null;
        if (t.Type == JTokenType.String) return (string)t;
        return t is JValue v ? Convert.ToString(v.Value, CultureInfo.InvariantCulture) : null;
    }

    private static List<int> SpIntList(JToken t)
    {
        var list = new List<int>();
        if (t is JArray a)
        {
            foreach (var x in a)
            {
                if (int.TryParse(SpStr(x), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) list.Add(n);
            }
        }
        list.Sort();
        return list;
    }

    private static List<string> SpStrList(JToken t)
    {
        var list = new List<string>();
        if (t is JArray a)
        {
            foreach (var x in a)
            {
                string s = SpStr(x);
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }

    private static string SpGhostMode(JToken t)
    {
        string s = SpStr(t);
        return string.Equals(s, "FixedLineup", StringComparison.OrdinalIgnoreCase) || s == "1" ? "FixedLineup" : "Random";
    }

    /// <summary>[1,2,3,5] → 「第1~3、5天」</summary>
    private static string SpFormatDays(IList<int> days)
    {
        if (days == null || days.Count == 0) return "没有安排的天";
        var d = days.Distinct().OrderBy(x => x).ToList();
        var parts = new List<string>();
        int start = d[0], prev = d[0];
        for (int i = 1; i <= d.Count; i++)
        {
            if (i < d.Count && d[i] == prev + 1) { prev = d[i]; continue; }
            parts.Add(start == prev ? start.ToString(CultureInfo.InvariantCulture) : $"{start}~{prev}");
            if (i < d.Count) { start = d[i]; prev = d[i]; }
        }
        return "第" + string.Join("、", parts) + "天";
    }

    private const string SpSeedFormatHint = "种子码是 S 开头的 8 位（比如 S3KZ9QW1）：点「随机一个」，或者粘贴别人给你的码";

    /// <summary>种子只认固定格式的码：去空格、转大写，O 当 0、I/L 当 1；格式不对返回 null（和服务端规则一样）。</summary>
    private static string SpNormalizeSeedCode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s;
        try { s = raw.Normalize(NormalizationForm.FormKC); } catch { s = raw; }
        var sb = new StringBuilder(8);
        foreach (char ch in s)
        {
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(char.ToUpperInvariant(ch));
        }
        if (sb.Length != 8 || sb[0] != 'S') return null;
        for (int i = 1; i < 8; i++)
        {
            char c = sb[i] switch { 'O' => '0', 'I' => '1', 'L' => '1', _ => sb[i] };
            if (SpSeedAlphabet.IndexOf(c) < 0) return null;
            sb[i] = c;
        }
        if (sb[1] > '3') return null; // 首位最大 3（不能超出 32 位）
        return sb.ToString();
    }

    /// <summary>整数主种子 → 种子码（与服务端 FormatSeedCode 一致）。</summary>
    private static string SpFormatSeedCode(int masterSeed)
    {
        uint v = unchecked((uint)masterSeed);
        var chars = new char[7];
        for (int i = 6; i >= 0; i--) { chars[i] = SpSeedAlphabet[(int)(v & 31)]; v >>= 5; }
        return "S" + new string(chars);
    }

    /// <summary>本地生成一个种子码（S + 7 位，32 位整数精确编码，与服务端 FormatSeedCode 同规则）。</summary>
    private static string SpGenerateSeedCode()
    {
        byte[] b = Guid.NewGuid().ToByteArray();
        uint v = BitConverter.ToUInt32(b, 0) ^ BitConverter.ToUInt32(b, 8);
        if (v == 0) v = 1;
        var chars = new char[7];
        for (int i = 6; i >= 0; i--) { chars[i] = SpSeedAlphabet[(int)(v & 31)]; v >>= 5; }
        return "S" + new string(chars);
    }

    /// <summary>解析错误响应里的 code / error；404 且没有 code 视为「服务器还没有这个接口」。</summary>
    private static (string Code, string Message) SpError(NameAuth.HttpJsonResponse resp)
    {
        if (resp == null) return ("NETWORK", "网络异常，请稍后再试");
        string code = "", msg = "";
        try
        {
            string body = resp.Body?.TrimStart();
            if (!string.IsNullOrEmpty(body) && body.StartsWith("{", StringComparison.Ordinal))
            {
                var o = JObject.Parse(body);
                code = SpStr(o["code"]) ?? "";
                msg = SpStr(o["error"]) ?? SpStr(o["messageZh"]) ?? "";
            }
        }
        catch { }
        if (resp.StatusCode == 404 && string.IsNullOrEmpty(code))
        {
            return ("ENDPOINT_MISSING", "当前服务器还不支持种子与对手方案（服务端需要更新到新版）");
        }
        if (resp.StatusCode == 401 && string.IsNullOrEmpty(msg)) msg = "还没登录游戏账号，登录后再试";
        if (resp.StatusCode == 0 && string.IsNullOrEmpty(msg)) msg = string.IsNullOrWhiteSpace(resp.Error) ? "连不上服务器" : resp.Error;
        if (string.IsNullOrWhiteSpace(msg)) msg = string.IsNullOrWhiteSpace(resp.Error) ? $"请求失败（{resp.StatusCode}）" : resp.Error;
        return (code, msg);
    }

    private static void SpClear(Transform t)
    {
        if (t == null) return;
        for (int i = t.childCount - 1; i >= 0; i--)
        {
            var go = t.GetChild(i).gameObject;
            go.SetActive(false);
            go.transform.SetParent(null, false);
            UnityEngine.Object.Destroy(go);
        }
    }

    private NativeUguiFactory.ButtonHandle SpButton(Transform parent, string label, UnityAction onClick, float width, bool gold = false, bool small = false)
    {
        var h = _ui.CreateButton(
            "Btn",
            parent,
            label,
            small ? 12f : 13f,
            gold ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
            gold ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover,
            gold ? Color.white : NativeUguiFactory.ColCream,
            onClick,
            borderColor: gold ? NativeUguiFactory.ColGold : NativeUguiFactory.ColGoldDim,
            fontStyle: gold ? FontStyles.Bold : FontStyles.Normal,
            padH: 6,
            padV: 2);
        NativeUguiFactory.SetLayout(h.Root, minW: width, prefW: width, minH: small ? 26f : 30f, prefH: small ? 26f : 30f);
        return h;
    }

    private static void SpToggleVisual(NativeUguiFactory.ButtonHandle h, bool on)
    {
        if (h == null) return;
        NativeUguiFactory.ApplyButtonColors(h.Button,
            on ? NativeUguiFactory.ColGoldBtn : NativeUguiFactory.ColBtnNormal,
            on ? NativeUguiFactory.ColGoldBtnHover : NativeUguiFactory.ColBtnHover);
        if (h.Border != null) h.Border.color = on ? NativeUguiFactory.ColGold : NativeUguiFactory.ColGoldDim;
        if (h.Label != null)
        {
            h.Label.color = on ? Color.white : NativeUguiFactory.ColCream;
            h.Label.fontStyle = on ? FontStyles.Bold : FontStyles.Normal;
        }
    }

    private TextMeshProUGUI SpLine(Transform parent, string text, float size = 13f, Color? color = null, bool bold = false)
    {
        var t = _ui.CreateText("Line", parent, text ?? "", size, color ?? NativeUguiFactory.ColCream,
            bold ? FontStyles.Bold : FontStyles.Normal, TextAlignmentOptions.TopLeft, wrap: true);
        NativeUguiFactory.SetLayout(t.gameObject, minH: size + 6f);
        return t;
    }

    private void SpCopy(string text, string okMsg)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            GUIUtility.systemCopyBuffer = text;
            ShowToastBanner(okMsg);
        }
        catch (Exception ex)
        {
            ShowToastBanner("复制失败：" + Truncate(ex.Message, 40), isError: true);
        }
    }

    private static void SpOpenUrl(string url)
    {
        try
        {
            Application.OpenURL(url);
            return;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[SeedPlanner] Application.OpenURL 失败，改用系统默认浏览器: " + ex.Message);
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[SeedPlanner] 打开网页失败: " + ex.Message);
        }
    }

    private SpLineup SpFindLineup(string id)
        => string.IsNullOrWhiteSpace(id) || _spCtx == null
            ? null
            : _spCtx.Lineups.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    private void SpSetHeaderStatus(string msg, bool isErr)
    {
        _spHeaderMsg = msg ?? "";
        _spHeaderIsErr = isErr;
        if (_spHeaderStatus != null)
        {
            _spHeaderStatus.text = _spHeaderMsg;
            _spHeaderStatus.color = isErr ? NativeUguiFactory.ColErrorText : NativeUguiFactory.ColMuted;
        }
    }

    // ═══════════════ 规则页卡片 ═══════════════
    private void BuildSeedPlannerCard(Transform parent)
    {
        _spCard = _ui.CreateCard("SeedPlannerCard", parent, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 10, spacing: 7f);

        // 标题行：标题 + 状态 + 同步 + 打开网页
        var head = _ui.CreateHorizontalRow("Head", _spCard.transform, spacing: 8f);
        var title = _ui.CreateText("Title", head.transform, "种子与对手（改完下一局生效）", 15.5f, NativeUguiFactory.ColGold, FontStyles.Bold, wrap: false);
        NativeUguiFactory.SetLayout(title.gameObject, minW: 270f, prefW: 280f, minH: 28f);
        _spHeaderStatus = _ui.CreateText("Status", head.transform, "", 12.5f, NativeUguiFactory.ColMuted, FontStyles.Normal, TextAlignmentOptions.MidlineRight, wrap: false);
        NativeUguiFactory.SetLayout(_spHeaderStatus.gameObject, minW: 120f, flexW: 1f, minH: 28f);
        _spBtnSync = SpButton(head.transform, "同步", () => SpFetchContextAsync(manual: true), 70f);
        _spBtnWeb = SpButton(head.transform, "去网页详细编辑", () => SpOpenWebPlanner(null), 150f, gold: true);

        // 种子行
        var seedRow = _ui.CreateHorizontalRow("SeedRow", _spCard.transform, spacing: 6f);
        var seedLbl = _ui.CreateText("Lbl", seedRow.transform, "种子", 13.5f, NativeUguiFactory.ColCream, FontStyles.Bold, wrap: false);
        NativeUguiFactory.SetLayout(seedLbl.gameObject, minW: 44f, prefW: 44f, minH: 30f);
        _spSeedInput = _ui.CreateInputField("SeedInput", seedRow.transform, "S 开头的 8 位种子码，比如 S3KZ9QW1；留空 = 普通随机局", "", 13.5f, 12);
        NativeUguiFactory.SetLayout(_spSeedInput.gameObject, minW: 260f, flexW: 1f, minH: 30f, prefH: 30f);
        _spSeedInput.onValueChanged.AddListener(v =>
        {
            _spSeedInputDirty = !string.Equals((v ?? "").Trim(), _normalRules.CustomSeedText ?? "", StringComparison.Ordinal);
            SpRefreshSeedStatus();
        });
        _spSeedInput.onSubmit.AddListener(_ => SpApplySeedFromInput());
        _spBtnApplySeed = SpButton(seedRow.transform, "应用", SpApplySeedFromInput, 70f, gold: true);
        _spBtnRandomSeed = SpButton(seedRow.transform, "随机一个", SpRandomSeed, 90f);
        _spBtnClearSeed = SpButton(seedRow.transform, "清空", SpClearSeed, 64f);

        _spSeedStatus = _ui.CreateText("SeedStatus", _spCard.transform, "", 12.5f, NativeUguiFactory.ColCream, FontStyles.Normal, TextAlignmentOptions.TopLeft, wrap: true);
        NativeUguiFactory.SetLayout(_spSeedStatus.gameObject, minH: 20f);

        var runRow = _ui.CreateHorizontalRow("RunSeeds", _spCard.transform, spacing: 6f);
        _spRunSeedsText = _ui.CreateText("Txt", runRow.transform, "", 12.5f, NativeUguiFactory.ColMuted, wrap: false);
        NativeUguiFactory.SetLayout(_spRunSeedsText.gameObject, minW: 200f, flexW: 1f, minH: 26f);
        _spBtnCopyActive = SpButton(runRow.transform, "复制本局种子", () => SpCopySeed(active: true), 112f, small: true);
        _spBtnCopyRecent = SpButton(runRow.transform, "复制上局种子", () => SpCopySeed(active: false), 112f, small: true);

        // 对手行
        var oppRow = _ui.CreateHorizontalRow("OppRow", _spCard.transform, spacing: 6f);
        var oppLbl = _ui.CreateText("Lbl", oppRow.transform, "对手", 13.5f, NativeUguiFactory.ColCream, FontStyles.Bold, wrap: false);
        NativeUguiFactory.SetLayout(oppLbl.gameObject, minW: 44f, prefW: 44f, minH: 30f);
        _spBtnOppRandom = SpButton(oppRow.transform, "全随机", () => SpSetOpponents(random: true, lineup: null), 92f);
        _spBtnOppLineup = SpButton(oppRow.transform, "按方案", () => SpSetOpponents(random: false, lineup: null), 92f);
        _spOppHint = _ui.CreateText("Hint", oppRow.transform, "", 12.5f, NativeUguiFactory.ColMuted, wrap: true);
        NativeUguiFactory.SetLayout(_spOppHint.gameObject, minW: 200f, flexW: 1f, minH: 30f);

        _spLineupList = _ui.CreateVerticalGroup("LineupList", _spCard.transform, spacing: 4f, padH: 48).transform;

        // 操作行
        var actRow = _ui.CreateHorizontalRow("Actions", _spCard.transform, spacing: 8f);
        _spBtnExport = SpButton(actRow.transform, "分享", () => SpOpenExportPopup(null, null), 90f, gold: true);
        _spBtnImport = SpButton(actRow.transform, "导入分享码", () => SpOpenImportPopup(false), 118f);
        _spBtnFromRun = SpButton(actRow.transform, "把这局存成方案", () => SpOpenExportPopup(SpPreferredRunSource(), null, autoFromRun: true), 140f);
        _spBtnFavorite = SpButton(actRow.transform, "收藏打过的对手", SpOpenFavoritePopup, 140f);
        var tip = _ui.CreateText("Tip", actRow.transform, "换装备、改数值、看技能效果这些细活在网页里做（右上角「去网页详细编辑」）。", 12f, NativeUguiFactory.ColMuted, wrap: true);
        NativeUguiFactory.SetLayout(tip.gameObject, minW: 200f, flexW: 1f, minH: 30f);

        SpRefreshUi();
    }

    /// <summary>刷新卡片显示；返回 true 表示高度可能变了，需要重新排版。</summary>
    private bool SpRefreshUi()
    {
        if (_spCard == null) return false;
        var c = _spCtx;
        bool supported = c == null || c.Supported;
        bool layoutChanged = false;

        if (!string.IsNullOrEmpty(_spLastError))
        {
            _spHeaderStatus.text = _spLastError;
            _spHeaderStatus.color = NativeUguiFactory.ColErrorText;
        }
        else if (c == null)
        {
            _spHeaderStatus.text = _spFetchInFlight ? "正在读取…" : "";
            _spHeaderStatus.color = NativeUguiFactory.ColMuted;
        }
        else
        {
            _spHeaderStatus.text = _spHeaderMsg;
            _spHeaderStatus.color = _spHeaderIsErr ? NativeUguiFactory.ColErrorText : NativeUguiFactory.ColMuted;
        }

        SpSyncSeedInput(force: false);
        layoutChanged |= SpRefreshSeedStatus();

        string act = c?.ActiveRun != null && !c.ActiveRun.IsFinished ? c.ActiveRun.SeedText : null;
        string rec = c?.RecentRun?.SeedText;
        _spRunSeedsText.text = $"本局种子：{act ?? "—"}      上一局：{rec ?? "—"}";
        _spBtnCopyActive.Button.interactable = !string.IsNullOrEmpty(act);
        _spBtnCopyRecent.Button.interactable = !string.IsNullOrEmpty(rec);

        bool fixedMode = _normalRules.UsesFixedLineup;
        SpToggleVisual(_spBtnOppRandom, !fixedMode);
        SpToggleVisual(_spBtnOppLineup, fixedMode);
        string hint;
        if (!fixedMode)
        {
            hint = "每晚的 PvP 对手像平常一样随机匹配。";
        }
        else
        {
            var sel = SpFindLineup(_normalRules.SelectedGhostLineupId);
            hint = sel != null
                ? $"按方案「{Truncate(sel.Name, 14)}」：{SpFormatDays(sel.Days)}固定，其他天随机。"
                : (c == null ? "正在读取方案…" : "选中的方案已经不在了（开局时会按随机处理），请重新选一个。");
        }
        if (_spOppHint.text != hint)
        {
            _spOppHint.text = hint;
            layoutChanged = true;
        }

        string fp = (c == null ? "null" : (c.Supported ? "1" : "0") + string.Join("|", c.Lineups.Select(l => l.Id + ":" + l.Name + ":" + string.Join(",", l.Days) + ":" + l.MissingModNames.Count)))
            + "#" + (fixedMode ? _normalRules.SelectedGhostLineupId : "");
        if (!string.Equals(fp, _spLineupFp, StringComparison.Ordinal))
        {
            _spLineupFp = fp;
            SpBuildLineupList();
            layoutChanged = true;
        }

        bool canNet = supported && !_spBusy;
        foreach (var b in new[] { _spBtnApplySeed, _spBtnRandomSeed, _spBtnClearSeed, _spBtnOppRandom, _spBtnOppLineup, _spBtnExport, _spBtnImport, _spBtnFromRun, _spBtnFavorite, _spBtnWeb })
        {
            if (b != null) b.Button.interactable = canNet;
        }
        if (_spBtnSync != null) _spBtnSync.Button.interactable = !_spFetchInFlight;
        return layoutChanged;
    }

    private bool SpRefreshSeedStatus()
    {
        if (_spSeedStatus == null) return false;
        var c = _spCtx;
        bool supported = c == null || c.Supported;
        bool daily = string.Equals(_normalRules.SpecialModeId, "DailyChallenge", StringComparison.OrdinalIgnoreCase);
        string seedShown = c?.CurrentSeedText ?? _normalRules.CustomSeedText;
        var sb = new StringBuilder(128);
        if (!supported)
        {
            sb.Append("当前服务器还不支持种子与对手方案（服务端需要更新到新版）。");
        }
        else if (!string.IsNullOrEmpty(seedShown))
        {
            sb.Append("下一局种子：<b>").Append(seedShown).Append("</b>　事件、野怪、商店和升级奖励都会固定。");
            if (daily) sb.Append("\n<color=#F2C14E>你现在选的是「每日挑战」：每日挑战用当天的官方种子，切到其他模式后这个种子才生效。</color>");
        }
        else if (_normalRules.FixedRunSeed.HasValue && _normalRules.FixedRunSeed.Value != 0 && !daily)
        {
            sb.Append("下一局种子：<b>").Append(SpFormatSeedCode(_normalRules.FixedRunSeed.Value)).Append("</b>（点「清空」恢复随机）。");
        }
        else
        {
            sb.Append("下一局：普通随机局（没有设置种子）。");
        }
        if (_spSeedInputDirty) sb.Append("　<color=#F2C14E>输入框里的种子还没应用，点「应用」或按回车。</color>");

        string text = sb.ToString();
        if (string.Equals(text, _spLastSeedStatusText, StringComparison.Ordinal)) return false;
        _spLastSeedStatusText = text;
        _spSeedStatus.text = text;
        return true;
    }

    private void SpSyncSeedInput(bool force)
    {
        if (_spSeedInput == null) return;
        if (!force && (_spSeedInput.isFocused || _spSeedInputDirty)) return;
        string v = _normalRules.CustomSeedText ?? "";
        if (!string.Equals(_spSeedInput.text, v, StringComparison.Ordinal)) _spSeedInput.SetTextWithoutNotify(v);
        _spSeedInputDirty = false;
    }

    private void SpBuildLineupList()
    {
        SpClear(_spLineupList);
        var c = _spCtx;
        if (c == null || !c.Supported) return;
        if (c.Lineups.Count == 0)
        {
            SpLine(_spLineupList, "还没有对手方案。打完一局点「把这局存成方案」，或者点右上角「去网页详细编辑」从镜像池挑对手。", 12.5f, NativeUguiFactory.ColMuted);
            return;
        }
        const int maxShown = 8;
        for (int i = 0; i < c.Lineups.Count && i < maxShown; i++)
        {
            var l = c.Lineups[i];
            bool sel = _normalRules.UsesFixedLineup && string.Equals(l.Id, _normalRules.SelectedGhostLineupId, StringComparison.OrdinalIgnoreCase);
            var row = _ui.CreateHorizontalRow("Lineup", _spLineupList, spacing: 8f);
            var captured = l;
            var btn = SpButton(row.transform, sel ? "使用中" : "用这个", () => SpSetOpponents(random: false, lineup: captured), 76f, small: true);
            SpToggleVisual(btn, sel); // 忙的时候点击会被 SpSetOpponents 忽略，这里不禁用按钮，免得重建时卡在灰色
            var name = _ui.CreateText("Name", row.transform, l.Name, 13f, sel ? NativeUguiFactory.ColGold : NativeUguiFactory.ColCream, sel ? FontStyles.Bold : FontStyles.Normal, wrap: false);
            NativeUguiFactory.SetLayout(name.gameObject, minW: 160f, prefW: 240f, minH: 26f);
            var days = _ui.CreateText("Days", row.transform, SpFormatDays(l.Days) + "固定" + (string.IsNullOrEmpty(l.SeedText) ? "" : "  · 种子 " + l.SeedText), 12.5f, NativeUguiFactory.ColMuted, wrap: false);
            NativeUguiFactory.SetLayout(days.gameObject, minW: 120f, flexW: 1f, minH: 26f);
            SpButton(row.transform, "分享", () => SpOpenExportPopup("lineup", captured.Id), 60f, small: true);
            SpButton(row.transform, "去网页改", () => SpOpenWebPlanner(captured.Id), 80f, small: true);
            if (l.MissingModNames.Count > 0)
            {
                var miss = _ui.CreateText("Missing", row.transform, "缺 Mod：" + Truncate(string.Join("、", l.MissingModNames), 24) + "（这些卡开局时去掉）", 12f, NativeUguiFactory.ColErrorText, wrap: false);
                NativeUguiFactory.SetLayout(miss.gameObject, minW: 160f, prefW: 300f, minH: 26f);
            }
        }
        if (c.Lineups.Count > maxShown)
        {
            SpLine(_spLineupList, $"还有 {c.Lineups.Count - maxShown} 个方案，在网页里管理（「去网页详细编辑」）。", 12f, NativeUguiFactory.ColMuted);
        }
    }

    // ═══════════════ 同步与应用 ═══════════════

    /// <summary>面板打开时调用：距上次同步超过 3 秒就静默同步一次（网页里改过的种子 / 剧本会带回来）。</summary>
    private void SpOnPanelOpened()
    {
        if (_spCard == null) return;
        if (Time.unscaledTime - _spLastFetchAt > 3f) SpFetchContextAsync(manual: false);
    }

    private void SpFetchContextAsync(bool manual)
    {
        if (_spFetchInFlight)
        {
            _spRefetchQueued = true;
            return;
        }
        _spFetchInFlight = true;
        int seq = ++_spFetchSeq;
        _spLastFetchAt = Time.unscaledTime;
        if (manual) SpSetHeaderStatus("正在同步…", false);
        SpRefreshUi();

        Task.Run(async () =>
        {
            NameAuth.HttpJsonResponse resp = null;
            SpContext parsed = null;
            try
            {
                await NameAuth.EnsureAccountIdentityAsync().ConfigureAwait(false);
                resp = await NameAuth.GetJsonAsync("/api/planner/me/context").ConfigureAwait(false);
                if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body))
                {
                    parsed = SpParseContext(JObject.Parse(resp.Body));
                }
            }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning("[SeedPlanner] 同步失败: " + ex.Message);
            }

            _pendingUpdates.Enqueue(() =>
            {
                _spFetchInFlight = false;
                if (seq == _spFetchSeq)
                {
                    if (parsed != null)
                    {
                        _spCtx = parsed;
                        _spLastError = "";
                        // 这三项以服务端为准（网页或另一台电脑可能改过）
                        _normalRules.CustomSeedText = parsed.CustomSeedText;
                        _normalRules.GhostOpponentMode = parsed.GhostMode;
                        _normalRules.SelectedGhostLineupId = parsed.SelectedLineupId;
                        SpSetHeaderStatus(manual ? "已同步 " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) : "", false);
                        var after = _spAfterFetch;
                        _spAfterFetch = null;
                        if (after != null)
                        {
                            try { after(); } catch (Exception ex) { BazaarLauncherPlugin.Log?.LogWarning("[SeedPlanner] 打开弹窗失败: " + ex.Message); }
                        }
                    }
                    else
                    {
                        if (_spAfterFetch != null)
                        {
                            _spAfterFetch = null;
                            ShowToastBanner("读取对局信息失败，稍后再试", isError: true);
                        }
                        var (code, msg) = SpError(resp);
                        if (code == "ENDPOINT_MISSING")
                        {
                            _spCtx = new SpContext { Supported = false };
                            _spLastError = "";
                        }
                        else
                        {
                            _spLastError = "同步失败：" + Truncate(msg, 40);
                        }
                    }
                    MarkHudDirty();
                    if (_isOpen)
                    {
                        RefreshModalContentsIfOpen();
                        if (SpRefreshUi() && _mainScroll?.Content != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_mainScroll.Content);
                    }
                    if (SpIsPopupOpen()) SpRerenderPopup();
                }
                if (_spRefetchQueued)
                {
                    _spRefetchQueued = false;
                    SpFetchContextAsync(manual: false);
                }
            });
        });
    }

    private static SpContext SpParseContext(JObject o)
    {
        var c = new SpContext();
        if (o["rules"] is JObject r)
        {
            c.CustomSeedText = SpStr(r["customSeedText"]);
            c.GhostMode = SpGhostMode(r["ghostOpponentMode"]);
            c.SelectedLineupId = SpStr(r["selectedGhostLineupId"]);
        }
        c.CurrentSeedText = SpStr(o["currentSeedText"]);
        if (o["mods"] is JArray mods)
        {
            foreach (var m in mods.OfType<JObject>())
            {
                string id = SpStr(m["modId"]);
                if (string.IsNullOrWhiteSpace(id)) continue;
                c.ServerModNames[id] = SpStr(m["name"]) ?? id;
                if ((bool?)m["isServerDefault"] == true) c.ServerDefaultModIds.Add(id);
            }
        }
        if (o["lineups"] is JArray ls)
        {
            foreach (var l in ls.OfType<JObject>())
            {
                var x = new SpLineup
                {
                    Id = SpStr(l["lineupId"]) ?? "",
                    Name = SpStr(l["name"]) ?? "未命名方案",
                    Days = SpIntList(l["dayNumbers"]),
                    RequiredModIds = SpStrList(l["requiredModIds"]),
                    SeedText = SpStr(l["seedText"]),
                };
                if (l["missingMods"] is JArray mm)
                {
                    foreach (var mo in mm.OfType<JObject>()) x.MissingModNames.Add(SpStr(mo["name"]) ?? SpStr(mo["modId"]) ?? "?");
                }
                if (x.Id.Length > 0) c.Lineups.Add(x);
            }
        }
        c.ActiveRun = SpParseRun(o["activeRun"] as JObject);
        c.RecentRun = SpParseRun(o["recentRun"] as JObject);
        return c;
    }

    private static SpRun SpParseRun(JObject r)
    {
        if (r == null) return null;
        return new SpRun
        {
            RunId = SpStr(r["runId"]) ?? "",
            Hero = SpStr(r["hero"]) ?? "",
            HeroZh = SpStr(r["heroZh"]) ?? FormatHeroZh(SpStr(r["hero"])),
            SeedText = SpStr(r["seedText"]),
            Day = (int?)r["day"] ?? 0,
            IsFinished = (bool?)r["isFinished"] == true,
            IsSandbox = (bool?)r["isSandbox"] == true,
            FacedDays = SpIntList(r["facedDays"]),
        };
    }

    /// <summary>
    /// 调 /api/planner/apply-rules（只改请求里带的字段）。成功后只把种子 / 剧本三项和新开的 Mod 写回本地，
    /// 不整份替换 _normalRules，面板里没保存的其他改动不受影响。
    /// </summary>
    private void SpApplyRules(JObject patch, Func<JObject, string> okToast, Action<JObject> onOk = null)
    {
        if (_spBusy)
        {
            ShowToastBanner("上一个操作还没完成，请稍等", isError: true);
            return;
        }
        _spBusy = true;
        SpRefreshUi();
        string body = patch.ToString(Formatting.None);

        Task.Run(async () =>
        {
            var resp = await NameAuth.PostJsonAsync("/api/planner/apply-rules", body).ConfigureAwait(false);
            JObject obj = null;
            try
            {
                if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body)) obj = JObject.Parse(resp.Body);
            }
            catch { }

            _pendingUpdates.Enqueue(() =>
            {
                _spBusy = false;
                if (resp.Ok && obj != null)
                {
                    if (obj["rules"] is JObject r)
                    {
                        _normalRules.CustomSeedText = SpStr(r["customSeedText"]);
                        _normalRules.GhostOpponentMode = SpGhostMode(r["ghostOpponentMode"]);
                        _normalRules.SelectedGhostLineupId = SpStr(r["selectedGhostLineupId"]);
                    }
                    if (patch["customSeedText"] != null && string.IsNullOrEmpty(SpStr(patch["customSeedText"])))
                    {
                        _normalRules.FixedRunSeed = null; // 服务端「清空种子」会连旧版整数种子一起清
                    }
                    if (patch["addModIds"] is JArray add)
                    {
                        _normalRules.EnsureCollections();
                        foreach (string id in add.Select(SpStr).Where(s => !string.IsNullOrWhiteSpace(s)))
                        {
                            if (!_normalRules.SelectedModIds.Contains(id, StringComparer.OrdinalIgnoreCase)) _normalRules.SelectedModIds.Add(id);
                        }
                    }
                    if (_spCtx != null)
                    {
                        _spCtx.CurrentSeedText = SpStr(obj["currentSeedText"]);
                        _spCtx.CustomSeedText = _normalRules.CustomSeedText;
                        _spCtx.GhostMode = _normalRules.GhostOpponentMode;
                        _spCtx.SelectedLineupId = _normalRules.SelectedGhostLineupId;
                    }
                    _spLastError = "";
                    string toast = okToast?.Invoke(obj);
                    if (!string.IsNullOrEmpty(toast))
                    {
                        ShowToastBanner(toast);
                        SetStatus(toast, isError: false);
                    }
                    onOk?.Invoke(obj);
                    SpFetchContextAsync(manual: false);
                }
                else
                {
                    var (code, msg) = SpError(resp);
                    string text = code == "SEED_INVALID" ? "种子格式不对：" + msg : "没保存成功：" + msg;
                    ShowToastBanner(Truncate(text, 60), isError: true);
                    SetStatus(Truncate(text, 80), isError: true);
                    if (code == "ENDPOINT_MISSING") _spCtx = new SpContext { Supported = false };
                }
                _rulesVersion++;
                MarkHudDirty();
                if (_isOpen) RefreshModalContentsIfOpen();
            });
        });
    }

    // ── 种子 ──
    private void SpApplySeedFromInput()
    {
        if (_spSeedInput == null || _spBusy) return;
        string raw = (_spSeedInput.text ?? "").Trim();
        string v = raw.Length == 0 ? "" : SpNormalizeSeedCode(raw);
        if (v == null)
        {
            ShowToastBanner("种子码格式不对。" + SpSeedFormatHint, isError: true, seconds: 5f);
            return;
        }
        _spSeedInput.SetTextWithoutNotify(v);
        SpApplyRules(new JObject { ["customSeedText"] = v },
            o => v.Length == 0 ? "已清空种子，下一局是普通随机局" : "下一局种子：" + (SpStr(o["currentSeedText"]) ?? v),
            _ => SpSyncSeedInput(force: true));
    }

    private void SpRandomSeed()
    {
        if (_spSeedInput == null || _spBusy) return;
        string code = SpGenerateSeedCode();
        _spSeedInput.SetTextWithoutNotify(code);
        SpApplyRules(new JObject { ["customSeedText"] = code },
            o => "下一局种子：" + (SpStr(o["currentSeedText"]) ?? code) + "（已应用）",
            _ => SpSyncSeedInput(force: true));
    }

    private void SpClearSeed()
    {
        if (_spSeedInput == null || _spBusy) return;
        _spSeedInput.SetTextWithoutNotify("");
        SpApplyRules(new JObject { ["customSeedText"] = "" },
            _ => "已清空种子，下一局是普通随机局",
            _ => SpSyncSeedInput(force: true));
    }

    private void SpCopySeed(bool active)
    {
        var run = active ? _spCtx?.ActiveRun : _spCtx?.RecentRun;
        if (run == null || string.IsNullOrEmpty(run.SeedText)) return;
        SpCopy(run.SeedText, $"已复制{(active ? "本局" : "上局")}种子 {run.SeedText}");
    }

    // ── 对手 ──
    private void SpSetOpponents(bool random, SpLineup lineup)
    {
        if (_spBusy) return;
        if (random)
        {
            if (!_normalRules.UsesFixedLineup) return;
            SpApplyRules(new JObject { ["ghostOpponentMode"] = "Random" }, _ => "下一局起对手全随机");
            return;
        }

        var target = lineup ?? SpFindLineup(_normalRules.SelectedGhostLineupId);
        if (target == null)
        {
            bool has = _spCtx != null && _spCtx.Lineups.Count > 0;
            ShowToastBanner(has ? "在下面点「用这个」选一个方案" : "还没有方案：打完一局点「把这局存成方案」，或去网页做一个", isError: !has);
            return;
        }
        if (_normalRules.UsesFixedLineup && string.Equals(target.Id, _normalRules.SelectedGhostLineupId, StringComparison.OrdinalIgnoreCase)) return;

        var patch = new JObject { ["ghostOpponentMode"] = "FixedLineup", ["selectedGhostLineupId"] = target.Id };
        // 方案配了种子：种子一起换上，事件才和方案对得上
        string lineupSeed = SpNormalizeSeedCode(target.SeedText);
        if (lineupSeed != null) patch["customSeedText"] = lineupSeed;
        // 剧本用到、游戏设置里还没开、本服又有的玩法 Mod：一起开（不开的话这些卡开局时会被去掉）
        _normalRules.EnsureCollections();
        var mine = new HashSet<string>(_normalRules.SelectedModIds, StringComparer.OrdinalIgnoreCase);
        var toAdd = target.RequiredModIds
            .Where(id => !mine.Contains(id)
                         && _spCtx != null
                         && _spCtx.ServerModNames.ContainsKey(id)
                         && !_spCtx.ServerDefaultModIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (toAdd.Count > 0) patch["addModIds"] = new JArray(toAdd);

        string days = SpFormatDays(target.Days);
        string name = Truncate(target.Name, 14);
        SpApplyRules(patch,
            _ => $"下一局起，{days}按方案「{name}」出战" + (lineupSeed != null ? $"，种子 {lineupSeed}" : "") + (toAdd.Count > 0 ? $"；已顺便开启 {toAdd.Count} 个方案需要的 Mod" : ""),
            _ => { SpPresyncMods(toAdd); SpSyncSeedInput(force: true); });
    }

    /// <summary>开局前先把新开的 Mod 卡图 / 文案拉到本地，进局就能正常显示。</summary>
    private void SpPresyncMods(IEnumerable<string> ids)
    {
        var list = ids?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list == null || list.Count == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var (ok, summary) = await ModContentSyncClient.Instance
                    .SyncSelectedModBundleAsync(list, false, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                if (!ok)
                {
                    _pendingUpdates.Enqueue(() => SetStatus("Mod 资源预同步没完成（进局后会再试）：" + Truncate(summary, 50), isError: true));
                }
            }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning("[SeedPlanner] Mod 预同步失败: " + ex.Message);
            }
        });
    }

    // ── 网页（细致编辑）──
    /// <summary>在浏览器打开网页编排台；带 lineupId 时直接打开那个方案。</summary>
    private void SpOpenWebPlanner(string lineupId)
    {
        SpSetHeaderStatus("正在打开网页…", false);
        Task.Run(async () =>
        {
            string url = null, err = null;
            try
            {
                await NameAuth.EnsureAccountIdentityAsync().ConfigureAwait(false);
                var resp = await NameAuth.PostJsonAsync("/api/planner/web-ticket", new { }).ConfigureAwait(false);
                if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body))
                {
                    var o = JObject.Parse(resp.Body);
                    string path = SpStr(o["path"]);
                    string ticket = SpStr(o["ticket"]);
                    if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(ticket))
                    {
                        path = "/planner/#ticket=" + Uri.EscapeDataString(ticket);
                    }
                    string baseUrl = NameAuth.GetServerBaseUrl();
                    if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(baseUrl))
                    {
                        url = baseUrl + (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path);
                        if (!string.IsNullOrWhiteSpace(lineupId) && url.Contains("#"))
                        {
                            url += "&lineup=" + Uri.EscapeDataString(lineupId.Trim());
                        }
                    }
                    else
                    {
                        err = "服务器没有返回打开链接";
                    }
                }
                else
                {
                    var (code, msg) = SpError(resp);
                    err = resp.StatusCode == 401 ? "还没登录游戏账号，登录后再试" : msg;
                }
            }
            catch (Exception ex)
            {
                err = ex.Message;
            }

            _pendingUpdates.Enqueue(() =>
            {
                // 只允许 http / https，防止服务端地址配置异常时打开奇怪的协议
                if (url != null
                    && Uri.TryCreate(url, UriKind.Absolute, out var u)
                    && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                {
                    SpOpenUrl(url);
                    SpSetHeaderStatus("已在浏览器打开（这个链接 2 分钟内有效、只能用一次）", false);
                    ShowToastBanner(string.IsNullOrWhiteSpace(lineupId) ? "已在浏览器打开" : "已在浏览器打开这个方案");
                }
                else
                {
                    string text = "打不开网页：" + (err ?? "地址无效");
                    SpSetHeaderStatus(Truncate(text, 48), true);
                    ShowToastBanner(Truncate(text, 60), isError: true);
                }
            });
        });
    }

    // ── 开局提示（由 FetchActiveRunTendencyStateAsync 在主线程调用）──
    private void SpOnActiveRunInfo(string runId, string seedText, bool customSeeded, string pinnedLineupName, string startToastZh)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(startToastZh)) parts.Add(startToastZh.Trim()); // 服务端只返回一次，例如「剧本已删除，改为随机对手」

        if (!string.IsNullOrWhiteSpace(runId) && !string.Equals(runId, _spActiveRunToastedId, StringComparison.OrdinalIgnoreCase))
        {
            _spActiveRunToastedId = runId;
            if (customSeeded && !string.IsNullOrWhiteSpace(seedText)) parts.Add("本局种子 " + seedText);
            if (!string.IsNullOrWhiteSpace(pinnedLineupName)) parts.Add($"对手按方案「{Truncate(pinnedLineupName, 14)}」");
            if (parts.Count > 0)
            {
                int modCount = _activeRunRules?.SelectedModIds?.Count ?? 0;
                if (modCount > 0) parts.Add($"Mod {modCount} 个");
            }
        }
        if (parts.Count > 0) ShowToastBanner(string.Join("  ·  ", parts), isError: false, seconds: 7f);
    }

    // ═══════════════ 二级弹窗框架（sortingOrder 530，盖在主面板 500 之上、Toast 560 之下）═══════════════
    private Canvas _spPopupCanvas;
    private GameObject _spPopupRoot;
    private RectTransform _spPopupWindow;
    private TextMeshProUGUI _spPopupTitle;
    private Transform _spPopupBody;
    private Transform _spPopupFooter;
    private ScrollRect _spPopupScroll;
    private string _spPopupKind = "";

    private bool SpIsPopupOpen() => _spPopupRoot != null && _spPopupRoot.activeSelf;

    private void SpEnsurePopup()
    {
        if (_spPopupRoot != null) return;
        EnsureHudBuilt();
        _spPopupCanvas = _ui.CreateOverlayCanvas("BazaarSeedPlannerPopupCanvas", _uiRootGo.transform, sortingOrder: 530);
        _spPopupRoot = new GameObject("PopupRoot", typeof(RectTransform));
        _spPopupRoot.transform.SetParent(_spPopupCanvas.transform, worldPositionStays: false);
        NativeUguiFactory.StretchFill((RectTransform)_spPopupRoot.transform);

        var backdrop = _ui.CreateBox("Backdrop", _spPopupRoot.transform, new Color(0f, 0f, 0f, 0.55f), rounded: false, raycastTarget: true);
        NativeUguiFactory.StretchFill((RectTransform)backdrop.transform);
        var backdropBtn = backdrop.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(SpClosePopup);

        var win = _ui.CreateCard("PopupWindow", _spPopupRoot.transform, NativeUguiFactory.ColPanelBg, NativeUguiFactory.ColGold, padH: 16, padV: 12, spacing: 8f);
        _spPopupWindow = (RectTransform)win.transform;
        _spPopupWindow.anchorMin = new Vector2(0.5f, 0.5f);
        _spPopupWindow.anchorMax = new Vector2(0.5f, 0.5f);
        _spPopupWindow.pivot = new Vector2(0.5f, 0.5f);
        _spPopupWindow.sizeDelta = new Vector2(860f, 600f);

        var head = _ui.CreateHorizontalRow("Head", win.transform, spacing: 8f);
        _spPopupTitle = _ui.CreateText("Title", head.transform, "", 17f, NativeUguiFactory.ColGold, FontStyles.Bold, wrap: false);
        NativeUguiFactory.SetLayout(_spPopupTitle.gameObject, flexW: 1f, minH: 32f);
        var close = SpButton(head.transform, "× 关闭", SpClosePopup, 86f);
        NativeUguiFactory.SetLayout(close.Root, minW: 86f, prefW: 86f, minH: 32f, prefH: 32f);

        var scroll = _ui.CreateScrollView("Body", win.transform);
        NativeUguiFactory.SetLayout(scroll.Root, flexH: 1f, minH: 120f);
        _spPopupScroll = scroll.ScrollRect;
        _spPopupBody = scroll.Content;

        var footer = _ui.CreateHorizontalRow("Footer", win.transform, spacing: 8f);
        footer.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleRight;
        NativeUguiFactory.SetLayout(footer, minH: 34f);
        _spPopupFooter = footer.transform;

        _spPopupRoot.SetActive(false);
    }

    private void SpBeginPopup(string kind, string title, float w, float h)
    {
        SpEnsurePopup();
        bool sameKind = string.Equals(_spPopupKind, kind, StringComparison.Ordinal) && SpIsPopupOpen();
        float keepScroll = sameKind && _spPopupScroll != null ? _spPopupScroll.verticalNormalizedPosition : 1f;
        _spPopupKind = kind;
        _spPopupTitle.text = title;
        _spPopupWindow.sizeDelta = new Vector2(w, h);
        SpClear(_spPopupBody);
        SpClear(_spPopupFooter);
        _spPopupRoot.SetActive(true);
        _spPopupCanvas.enabled = true;
        if (_spPopupScroll != null) _spPopupScroll.verticalNormalizedPosition = keepScroll;
    }

    private void SpEndPopup()
    {
        if (_spPopupBody != null) LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)_spPopupBody);
        if (_spPopupWindow != null) LayoutRebuilder.ForceRebuildLayoutImmediate(_spPopupWindow);
    }

    private void SpClosePopup()
    {
        if (_spPopupRoot == null || !_spPopupRoot.activeSelf) return;
        try
        {
            var es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject != null && es.currentSelectedGameObject.transform.IsChildOf(_spPopupRoot.transform))
            {
                es.SetSelectedGameObject(null);
            }
        }
        catch { }
        _spPopupRoot.SetActive(false);
        _spPopupKind = "";
    }

    /// <summary>后台数据回来后重画当前弹窗（输入框内容都存在状态字段里，重画不会丢）。</summary>
    private void SpRerenderPopup()
    {
        switch (_spPopupKind)
        {
            case "export": SpRenderExport(); break;
            case "import": SpRenderImport(); break;
            case "capture": SpRenderCapture(); break;
        }
    }

    private void SpChoiceRow(Transform parent, string label, IList<(string Key, string Text, bool Enabled)> opts, string selected, Action<string> onPick, float btnW)
    {
        var row = _ui.CreateHorizontalRow("Choice", parent, spacing: 6f);
        if (!string.IsNullOrEmpty(label))
        {
            var l = _ui.CreateText("Lbl", row.transform, label, 13f, NativeUguiFactory.ColMuted, wrap: false);
            NativeUguiFactory.SetLayout(l.gameObject, minW: 96f, prefW: 96f, minH: 30f);
        }
        foreach (var o in opts)
        {
            string key = o.Key;
            var h = SpButton(row.transform, o.Text, () => onPick(key), btnW);
            SpToggleVisual(h, o.Enabled && string.Equals(selected, key, StringComparison.Ordinal));
            if (!o.Enabled)
            {
                h.Button.interactable = false;
                h.Label.color = NativeUguiFactory.ColMuted;
            }
        }
    }

    private NativeUguiFactory.ButtonHandle SpFooterButton(string label, UnityAction onClick, bool gold = false, float width = 150f, bool enabled = true)
    {
        var h = SpButton(_spPopupFooter, label, onClick, width, gold);
        h.Button.interactable = enabled;
        return h;
    }

    // ═══════════════ 分享（生成分享码 / 把一局存成方案）═══════════════
    private string _spExpSource = "";
    private string _spExpLineupId;
    private bool _spExpWithGhosts = true;
    private bool _spExpIncludeRules;
    private string _spExpHero = "Vanessa";
    private bool _spExpBusy;
    private string _spExpShort, _spExpOffline, _spExpSummary, _spExpError;
    private bool _spFromRunBusy;
    private string _spFromRunLineupId, _spFromRunMsg;
    private Action _spAfterFetch;   // 第一次打开时还没有对局信息：读完再接着打开

    private List<(string Key, string Text, bool Enabled)> SpExportSources()
    {
        var a = _spCtx?.ActiveRun;
        var r = _spCtx?.RecentRun;
        string cur = _spCtx?.CurrentSeedText;
        bool aOk = a != null && !a.IsFinished && !string.IsNullOrEmpty(a.SeedText);
        bool rOk = r != null && !string.IsNullOrEmpty(r.SeedText);
        bool lOk = _spCtx != null && _spCtx.Lineups.Any(l => l.Days.Count > 0);
        return new List<(string, string, bool)>
        {
            ("active", aOk ? $"这局（{a.HeroZh} 第{a.Day}天）" : "这局（现在没在打）", aOk),
            ("recent", rOk ? $"上一局（{r.HeroZh} 打到第{r.Day}天）" : "上一局（没有）", rOk),
            ("lineup", lOk ? "我的一个方案" : "我的方案（还没有）", lOk),
            ("rules", !string.IsNullOrEmpty(cur) ? $"只分享种子 {cur}" : "只分享种子（没设）", !string.IsNullOrEmpty(cur)),
        };
    }

    /// <summary>「把这局存成方案」默认用哪一局：正在打的优先，其次上一局。</summary>
    private string SpPreferredRunSource()
    {
        var a = _spCtx?.ActiveRun;
        if (a != null && !a.IsFinished && !string.IsNullOrEmpty(a.SeedText)) return "active";
        var r = _spCtx?.RecentRun;
        if (r != null && !string.IsNullOrEmpty(r.SeedText)) return "recent";
        return "active";
    }

    private SpRun SpExpRun() => _spExpSource == "active" ? _spCtx?.ActiveRun : _spExpSource == "recent" ? _spCtx?.RecentRun : null;

    /// <summary>还没有对局信息时先读一次，读完再继续；已经有了就直接继续（顺便在后台刷新）。</summary>
    private bool SpEnsureContext(Action then)
    {
        if (_spCtx != null && !_spCtx.Supported)
        {
            ShowToastBanner("当前服务器还不支持分享码（服务端需要更新）", isError: true);
            return false;
        }
        if (_spCtx == null)
        {
            _spAfterFetch = then;
            ShowToastBanner("正在读取对局信息…");
            SpFetchContextAsync(manual: false);
            return false;
        }
        if (Time.unscaledTime - _spLastFetchAt > 10f) SpFetchContextAsync(manual: false);
        return true;
    }

    /// <summary>打开分享弹窗。source = active / recent / lineup / rules（空 = 自动挑）；从方案行点「分享」时直接出码；autoFromRun = 直接把这局存成方案。</summary>
    private void SpOpenExportPopup(string source, string lineupId, bool autoFromRun = false)
    {
        if (!SpEnsureContext(() => SpOpenExportPopup(source, lineupId, autoFromRun))) return;
        var sources = SpExportSources();
        string pick = !string.IsNullOrEmpty(source) && sources.Any(s => s.Key == source && s.Enabled)
            ? source
            : (sources.FirstOrDefault(s => s.Enabled).Key ?? "");
        _spExpSource = pick;
        _spExpLineupId = !string.IsNullOrEmpty(lineupId) ? lineupId : _spCtx.Lineups.FirstOrDefault(l => l.Days.Count > 0)?.Id;
        _spExpWithGhosts = true;
        _spExpIncludeRules = false;
        _spExpHero = DetectCurrentHero();
        _spExpShort = _spExpOffline = _spExpSummary = _spExpError = null;
        _spFromRunLineupId = _spFromRunMsg = null;
        _spPopupKind = "";
        SpRenderExport();
        if (pick == "lineup" && !string.IsNullOrEmpty(lineupId)) SpDoExport();
        else if (autoFromRun && (pick == "active" || pick == "recent")) SpDoFromRun();
    }

    private void SpRenderExport()
    {
        SpBeginPopup("export", "分享给朋友", 920f, 660f);
        var body = _spPopupBody;
        SpLine(body, "把种子（还可以带上每天的对手）打包成一个码发给朋友，朋友导入以后就能打同一局。", 12.5f, NativeUguiFactory.ColMuted);

        var sources = SpExportSources();
        if (!sources.Any(s => s.Enabled))
        {
            SpLine(body, "现在没有能分享的东西：先打一局，或者在卡片里设一个种子。", 13.5f, SpColWarn);
            SpFooterButton("关闭", SpClosePopup, width: 100f);
            SpEndPopup();
            return;
        }

        SpChoiceRow(body, "分享什么", sources, _spExpSource, k =>
        {
            _spExpSource = k;
            _spExpShort = null;
            _spExpError = null;
            SpRenderExport();
        }, 200f);

        var run = SpExpRun();
        if (_spExpSource == "lineup")
        {
            var ls = _spCtx.Lineups.Where(l => l.Days.Count > 0).Take(6).ToList();
            if (ls.All(l => !string.Equals(l.Id, _spExpLineupId, StringComparison.OrdinalIgnoreCase))) _spExpLineupId = ls.FirstOrDefault()?.Id;
            SpChoiceRow(body, "哪个方案", ls.Select(l => (l.Id, Truncate(l.Name, 12), true)).ToList(), _spExpLineupId, k =>
            {
                _spExpLineupId = k;
                _spExpShort = null;
                SpRenderExport();
            }, 150f);
            var sel = SpFindLineup(_spExpLineupId);
            if (sel != null)
            {
                string cur = _spCtx?.CurrentSeedText;
                string seedLine = !string.IsNullOrEmpty(sel.SeedText)
                    ? $"种子 {sel.SeedText} + {SpFormatDays(sel.Days)}的对手（没安排的天朋友那边随机）。"
                    : (!string.IsNullOrEmpty(cur)
                        ? $"这个方案没配种子，会用你下一局的种子 {cur}；对手：{SpFormatDays(sel.Days)}。"
                        : "这个方案没配种子，你也没设下一局的种子：先去网页给它配一个种子。");
                SpLine(body, seedLine, 12.5f, string.IsNullOrEmpty(sel.SeedText) && string.IsNullOrEmpty(cur) ? SpColWarn : NativeUguiFactory.ColCream);
            }
        }
        else if (run != null)
        {
            int faced = run.FacedDays.Count;
            if (faced > 0)
            {
                _ui.CreateToggle("WithGhosts", body, $"带上对手（{SpFormatDays(run.FacedDays)}，就是你实际遇到的）", _spExpWithGhosts, v => { _spExpWithGhosts = v; _spExpShort = null; }, fontSize: 13f);
                SpLine(body, "没打到的天，朋友那边会随机。想把后面的天也定好：点下面「存成方案再改」，没打到的天会自动从镜像池补好（到第 15 天），你看着换。", 12f, NativeUguiFactory.ColMuted);
            }
            else
            {
                SpLine(body, "这局还没打过 PvP，只能分享种子；也可以「存成方案再改」，每天的对手自动补好。", 12.5f, NativeUguiFactory.ColMuted);
            }
        }
        else if (_spExpSource == "rules")
        {
            var heroRow = _ui.CreateHorizontalRow("Heroes", body, spacing: 4f);
            var hl = _ui.CreateText("Lbl", heroRow.transform, "用哪个英雄打", 13f, NativeUguiFactory.ColMuted, wrap: false);
            NativeUguiFactory.SetLayout(hl.gameObject, minW: 96f, prefW: 96f, minH: 30f);
            foreach (var (key, zh) in QuickSwitchHeroes)
            {
                string k = key;
                var hb = SpButton(heroRow.transform, zh, () => { _spExpHero = k; _spExpShort = null; SpRenderExport(); }, 84f, small: true);
                SpToggleVisual(hb, string.Equals(_spExpHero, key, StringComparison.OrdinalIgnoreCase));
            }
            SpLine(body, "同一个种子换英雄打，事件会不一样，所以要说清楚是哪个英雄。", 12f, NativeUguiFactory.ColMuted);
        }

        _ui.CreateToggle("IncludeRules", body, "带上我的规则（模式、词条、开局数值）", _spExpIncludeRules, v => { _spExpIncludeRules = v; _spExpShort = null; }, fontSize: 13f);

        if (!string.IsNullOrEmpty(_spExpError)) SpLine(body, _spExpError, 13f, NativeUguiFactory.ColErrorText);

        if (!string.IsNullOrEmpty(_spFromRunMsg))
        {
            var fr = _ui.CreateCard("FromRun", body, NativeUguiFactory.ColSubCardBg, NativeUguiFactory.ColEmeraldText, padH: 12, padV: 10, spacing: 6f);
            SpLine(fr.transform, _spFromRunMsg, 13f, NativeUguiFactory.ColEmeraldText, bold: true);
            if (!string.IsNullOrEmpty(_spFromRunLineupId))
            {
                var frRow = _ui.CreateHorizontalRow("Btns", fr.transform, spacing: 8f);
                string lid = _spFromRunLineupId;
                SpButton(frRow.transform, "去网页改这个方案", () => SpOpenWebPlanner(lid), 160f, gold: true);
                SpButton(frRow.transform, "不改了，直接分享", () =>
                {
                    _spExpSource = "lineup";
                    _spExpLineupId = lid;
                    _spFromRunMsg = null;
                    SpDoExport();
                }, 150f);
                SpButton(frRow.transform, "下一局就用它", () =>
                {
                    var l = SpFindLineup(lid);
                    if (l != null) SpSetOpponents(random: false, lineup: l);
                    else ShowToastBanner("方案列表还在刷新，稍等一下再点", isError: true);
                }, 130f);
            }
        }

        if (!string.IsNullOrEmpty(_spExpShort))
        {
            var res = _ui.CreateCard("Result", body, NativeUguiFactory.ColSubCardBg, NativeUguiFactory.ColGold, padH: 12, padV: 10, spacing: 6f);
            if (!string.IsNullOrEmpty(_spExpSummary)) SpLine(res.transform, _spExpSummary, 12.5f);
            SpLine(res.transform, "① 朋友和你在同一个服务器：发这个短码（已经复制好了）", 12.5f, NativeUguiFactory.ColCream, bold: true);
            var codeRow = _ui.CreateHorizontalRow("Code", res.transform, spacing: 10f);
            var codeTxt = _ui.CreateText("Code", codeRow.transform, _spExpShort, 26f, NativeUguiFactory.ColGold, FontStyles.Bold, wrap: false);
            NativeUguiFactory.SetLayout(codeTxt.gameObject, minW: 260f, flexW: 1f, minH: 40f);
            string shortCode = _spExpShort, offline = _spExpOffline;
            SpButton(codeRow.transform, "复制短码", () => SpCopy(shortCode, "短码已复制：" + shortCode), 110f, gold: true);
            if (!string.IsNullOrEmpty(offline))
            {
                SpLine(res.transform, "② 朋友在别的服务器（短码在那边用不了）：发长码", 12.5f, NativeUguiFactory.ColCream, bold: true);
                var longRow = _ui.CreateHorizontalRow("Long", res.transform, spacing: 10f);
                var lt = _ui.CreateText("Len", longRow.transform, $"长码 {offline.Length:N0} 个字符，直接粘贴就能导入", 12.5f, NativeUguiFactory.ColMuted, wrap: false);
                NativeUguiFactory.SetLayout(lt.gameObject, minW: 260f, flexW: 1f, minH: 30f);
                SpButton(longRow.transform, "复制长码", () => SpCopy(offline, $"长码已复制（{offline.Length:N0} 个字符）"), 110f);
            }
        }

        bool isRun = _spExpSource == "active" || _spExpSource == "recent";
        SpFooterButton("关闭", SpClosePopup, width: 100f);
        if (isRun)
        {
            SpFooterButton(_spFromRunBusy ? "正在存…" : "存成方案再改", SpDoFromRun, width: 140f, enabled: !_spFromRunBusy && !_spExpBusy);
        }
        SpFooterButton(_spExpBusy ? "生成中…" : (string.IsNullOrEmpty(_spExpShort) ? "生成并复制" : "重新生成"), SpDoExport, gold: true, width: 140f,
            enabled: !_spExpBusy && !string.IsNullOrEmpty(_spExpSource) && (_spExpSource != "lineup" || !string.IsNullOrEmpty(_spExpLineupId)));
        SpEndPopup();
    }

    private void SpDoExport()
    {
        if (_spExpBusy || string.IsNullOrEmpty(_spExpSource)) return;
        bool isLineup = _spExpSource == "lineup";
        if (isLineup && string.IsNullOrEmpty(_spExpLineupId)) return;
        var run = SpExpRun();
        bool withGhosts = isLineup || (run != null && run.FacedDays.Count > 0 && _spExpWithGhosts);
        _spExpBusy = true;
        _spExpError = null;
        SpRenderExport();
        var req = new JObject
        {
            ["source"] = _spExpSource,
            ["scope"] = withGhosts ? "events_and_ghosts" : "events_only",
            ["includeRules"] = _spExpIncludeRules,
            ["title"] = isLineup ? (SpFindLineup(_spExpLineupId)?.Name ?? "") : "",
            ["hero"] = _spExpSource == "rules" ? _spExpHero : null,
            ["lineupId"] = isLineup ? _spExpLineupId : null,
        };
        string body = req.ToString(Formatting.None);
        Task.Run(async () =>
        {
            var resp = await NameAuth.PostJsonAsync("/api/planner/blueprints/export", body).ConfigureAwait(false);
            JObject o = null;
            try { if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body)) o = JObject.Parse(resp.Body); } catch { }
            _pendingUpdates.Enqueue(() =>
            {
                _spExpBusy = false;
                if (o != null && !string.IsNullOrEmpty(SpStr(o["shortCode"])))
                {
                    _spExpShort = SpStr(o["shortCode"]);
                    _spExpOffline = SpStr(o["offlineCode"]);
                    var s = o["summary"] as JObject;
                    var gd = s == null ? new List<int>() : SpIntList(s["ghostDays"]);
                    _spExpSummary = s == null
                        ? ""
                        : $"种子 {SpStr(s["seedText"])} · 英雄 {SpStr(s["heroZh"]) ?? FormatHeroZh(SpStr(s["hero"]))} · "
                          + (gd.Count > 0 ? $"{SpFormatDays(gd)}的对手固定，其他天随机" : "对手全随机（只带了种子）")
                          + ((bool?)s["includeRules"] == true ? " · 带上了规则" : "");
                    SpCopy(_spExpShort, "短码已复制：" + _spExpShort);
                }
                else
                {
                    var (code, msg) = SpError(resp);
                    _spExpError = code switch
                    {
                        "SEED_UNAVAILABLE" => isLineup ? "这个方案没配种子，你也没设下一局的种子：先去网页给方案配一个种子。" : "这局没有种子，不能分享。",
                        "NO_RUN" => "找不到这局了（可能刚结束），点「同步」后再试。",
                        "NOT_FOUND" => "这个方案已经不在了（可能删掉了），点「同步」后再试。",
                        _ => "生成失败：" + msg,
                    };
                }
                if (_spPopupKind == "export" && SpIsPopupOpen()) SpRenderExport();
            });
        });
    }

    /// <summary>把这局存成方案：打过的天放实际遇到的对手，没打到的天自动从镜像池补到第 15 天，种子记在方案上。</summary>
    private void SpDoFromRun()
    {
        if (_spFromRunBusy) return;
        string src = _spExpSource == "recent" ? "recent" : "active";
        _spFromRunBusy = true;
        _spExpError = null;
        _spFromRunMsg = null;
        _spFromRunLineupId = null;
        SpRenderExport();
        string body = new JObject { ["source"] = src, ["fillToDay"] = 15 }.ToString(Formatting.None);
        Task.Run(async () =>
        {
            var resp = await NameAuth.PostJsonAsync("/api/planner/lineups/from-run", body).ConfigureAwait(false);
            JObject o = null;
            try { if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body)) o = JObject.Parse(resp.Body); } catch { }
            _pendingUpdates.Enqueue(() =>
            {
                _spFromRunBusy = false;
                string lid = (o?["lineup"] as JObject) != null ? SpStr(o["lineup"]["lineupId"]) : null;
                if (!string.IsNullOrEmpty(lid))
                {
                    _spFromRunLineupId = lid;
                    _spFromRunMsg = SpStr(o["messageZh"]) ?? "已存成方案";
                    SpFetchContextAsync(manual: false); // 方案列表里马上能看到
                }
                else
                {
                    var (code, msg) = SpError(resp);
                    _spExpError = code switch
                    {
                        "NO_RUN" => "找不到这局了（可能刚结束），点「同步」后再试。",
                        "LINEUP_LIMIT" => "方案已经满了（最多 50 套），先去网页删掉不用的。",
                        _ => "存成方案失败：" + msg,
                    };
                }
                if (_spPopupKind == "export" && SpIsPopupOpen()) SpRenderExport();
            });
        });
    }

    // ═══════════════ 导入分享码 ═══════════════
    private string _spImpCodeText = "";
    private string _spImpBuffer;     // 从剪贴板载入的长码，只放内存，不塞进输入框
    private string _spImpCode;       // 识别成功时用的那个码
    private JObject _spImpPreview;
    private string _spImpOpp = "random";
    private bool _spImpApplyRules;
    private readonly HashSet<string> _spImpEnableMods = new(StringComparer.OrdinalIgnoreCase);
    private bool _spImpBusy;
    private string _spImpError;
    private string _spImpDoneMsg;
    private string _spImpDoneLineupId;

    /// <summary>打开导入弹窗；fromHud = 从主菜单「分享码」按钮进来，剪贴板里有分享码就直接识别。</summary>
    private void SpOpenImportPopup(bool fromHud)
    {
        if (_spCtx != null && !_spCtx.Supported)
        {
            ShowToastBanner("当前服务器还不支持分享码", isError: true);
            return;
        }
        _spImpCodeText = "";
        _spImpBuffer = null;
        _spImpCode = null;
        _spImpPreview = null;
        _spImpOpp = "random";
        _spImpApplyRules = false;
        _spImpEnableMods.Clear();
        _spImpError = null;
        _spImpDoneMsg = null;
        _spImpDoneLineupId = null;
        _spPopupKind = "";
        SpRenderImport();
        if (_spCtx == null) SpFetchContextAsync(manual: false);
        if (fromHud)
        {
            string clip = "";
            try { clip = (GUIUtility.systemCopyBuffer ?? "").Trim(); } catch { }
            if (clip.StartsWith("BZ-", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("BZR1:", StringComparison.OrdinalIgnoreCase))
            {
                SpImportFromClipboard();
            }
        }
    }

    private void SpRenderImport()
    {
        SpBeginPopup("import", "导入分享码", 900f, 660f);
        var body = _spPopupBody;

        if (!string.IsNullOrEmpty(_spImpDoneMsg))
        {
            var ok = _ui.CreateCard("Done", body, NativeUguiFactory.ColSubCardBg, NativeUguiFactory.ColEmeraldText, padH: 12, padV: 10, spacing: 6f);
            SpLine(ok.transform, _spImpDoneMsg, 14f, NativeUguiFactory.ColEmeraldText, bold: true);
            SpLine(ok.transform, "种子和对手已经设好了，直接开新局就行（开局请选分享里的那个英雄）。导入的对手会存成一个方案，想改哪天的对手可以去网页改，改完还能再分享。", 12.5f);
            SpFooterButton("再导入一个", () => SpOpenImportPopup(false), width: 120f);
            if (!string.IsNullOrEmpty(_spImpDoneLineupId))
            {
                string lid = _spImpDoneLineupId;
                SpFooterButton("去网页改这个方案", () => SpOpenWebPlanner(lid), width: 160f);
            }
            SpFooterButton("完成", SpClosePopup, gold: true, width: 100f);
            SpEndPopup();
            return;
        }

        var topRow = _ui.CreateHorizontalRow("Top", body, spacing: 8f);
        var topTxt = _ui.CreateText("Txt", topRow.transform, "粘贴朋友发来的短码（BZ- 开头）或长码（BZR1: 开头）。长码太长就先复制，再点「从剪贴板粘贴并识别」。", 12.5f, NativeUguiFactory.ColMuted, wrap: true);
        NativeUguiFactory.SetLayout(topTxt.gameObject, minW: 300f, flexW: 1f, minH: 30f);
        SpButton(topRow.transform, "我要分享给别人", () => SpOpenExportPopup(null, null), 140f);
        var row = _ui.CreateHorizontalRow("CodeRow", body, spacing: 6f);
        var input = _ui.CreateInputField("Code", row.transform, "BZ-XXXX-XXXX", _spImpBuffer != null ? "" : _spImpCodeText, 15f, 128);
        NativeUguiFactory.SetLayout(input.gameObject, minW: 240f, flexW: 1f, minH: 34f, prefH: 34f);
        input.onValueChanged.AddListener(v =>
        {
            _spImpCodeText = v ?? "";
            if (!string.IsNullOrEmpty(v)) _spImpBuffer = null;
        });
        input.onSubmit.AddListener(_ => SpDoImportPreview());
        SpButton(row.transform, "从剪贴板粘贴并识别", SpImportFromClipboard, 170f);
        SpButton(row.transform, _spImpBusy && _spImpPreview == null ? "识别中…" : "识别", SpDoImportPreview, 80f, gold: true).Button.interactable = !_spImpBusy;

        if (_spImpBuffer != null)
        {
            SpLine(body, $"已载入长码（{_spImpBuffer.Length:N0} 个字符）", 12.5f, NativeUguiFactory.ColEmeraldText);
        }
        if (!string.IsNullOrEmpty(_spImpError)) SpLine(body, _spImpError, 13f, NativeUguiFactory.ColErrorText);

        bool canApply = false;
        var p = _spImpPreview;
        if (p != null)
        {
            var sum = _ui.CreateCard("Summary", body, NativeUguiFactory.ColSubCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 8, spacing: 4f);
            string title = SpStr(p["title"]);
            if (!string.IsNullOrWhiteSpace(title)) SpLine(sum.transform, "标题：" + title, 13f, NativeUguiFactory.ColGold, bold: true);
            string heroZh = SpStr(p["heroZh"]) ?? FormatHeroZh(SpStr(p["hero"]));
            var ghostDays = SpIntList(p["ghostDays"]);
            bool hasGhosts = SpStr(p["scope"]) == "events_and_ghosts" && ghostDays.Count > 0;
            bool hasRules = (bool?)p["hasRules"] == true;
            SpLine(sum.transform, $"来自 {SpStr(p["creatorUsername"]) ?? "未知"}　·　英雄 {heroZh}　·　种子 {SpStr(p["seedText"])}", 13f);
            SpLine(sum.transform, "内容：" + (hasGhosts ? $"事件 + {SpFormatDays(ghostDays)}的对手" : "只有事件（对手随机）")
                + "　·　规则：" + (hasRules ? (SpStr(p["rulesSummaryZh"]) ?? "带有规则") : "没有附带"), 13f);
            SpLine(sum.transform, $"这个种子是用「{heroZh}」打的，开局请选同一个英雄，否则事件会不一样。", 12.5f, SpColWarn);

            if (p["modDiff"] is JObject md)
            {
                if (md["autoEnable"] is JArray auto && auto.Count > 0)
                {
                    SpLine(body, "需要这些 Mod（本服有，会一起开启）：", 13f, NativeUguiFactory.ColEmeraldText, bold: true);
                    foreach (var m in auto.OfType<JObject>())
                    {
                        string id = SpStr(m["modId"]);
                        if (string.IsNullOrEmpty(id)) continue;
                        _ui.CreateToggle("Mod", body, $"{SpStr(m["name"]) ?? id}  v{SpStr(m["version"]) ?? "?"}", _spImpEnableMods.Contains(id),
                            v => { if (v) _spImpEnableMods.Add(id); else _spImpEnableMods.Remove(id); }, fontSize: 12.5f);
                    }
                }
                if (md["missing"] is JArray miss && miss.Count > 0)
                {
                    SpLine(body, "本服没有这些 Mod（仍可导入，但事件可能和原局不一样）：", 13f, NativeUguiFactory.ColErrorText, bold: true);
                    foreach (var m in miss.OfType<JObject>())
                    {
                        var aff = SpIntList(m["affectedDays"]);
                        SpLine(body, $"· {SpStr(m["name"]) ?? SpStr(m["modId"])} v{SpStr(m["version"]) ?? "?"}" + (aff.Count > 0 ? $" —— {SpFormatDays(aff)}的对手会去掉相关卡牌" : ""), 12.5f, NativeUguiFactory.ColErrorText);
                    }
                }
                if (md["versionMismatch"] is JArray mis && mis.Count > 0)
                {
                    SpLine(body, "Mod 版本不同（事件或数值可能有细微差别）：", 13f, SpColWarn, bold: true);
                    foreach (var m in mis.OfType<JObject>())
                    {
                        SpLine(body, $"· {SpStr(m["name"]) ?? SpStr(m["modId"])}：本服 v{SpStr(m["version"]) ?? "?"}，原局 v{SpStr(m["expectedVersion"]) ?? "?"}", 12.5f, SpColWarn);
                    }
                }
                if (md["serverExtra"] is JArray extra && extra.Count > 0)
                {
                    SpLine(body, "你额外开着这些玩法 Mod，事件可能和原局不同：" + string.Join("、", extra.OfType<JObject>().Select(m => SpStr(m["name"]) ?? SpStr(m["modId"]))), 12.5f, SpColWarn);
                }
            }
            foreach (string w in SpStrList(p["warnings"])) SpLine(body, w, 12.5f, SpColWarn);

            SpChoiceRow(body, "对手怎么安排", new List<(string, string, bool)>
            {
                ("lineup", hasGhosts ? $"按分享的对手（{SpFormatDays(ghostDays)}）" : "按分享的对手（这个分享没带）", hasGhosts),
                ("random", "对手全随机", true),
            }, _spImpOpp, k => { _spImpOpp = k; SpRenderImport(); }, 290f);
            if (_spImpOpp == "lineup" && hasGhosts)
            {
                SpLine(body, "会新建一个方案，并设为下一局的对手；没带的天照常随机。", 12f, NativeUguiFactory.ColMuted);
            }
            if (hasRules)
            {
                _ui.CreateToggle("ApplyRules", body, "同时套用分享里的规则（" + (SpStr(p["rulesSummaryZh"]) ?? "模式 / 词条 / 开局数值") + "）—— 会覆盖你现在的规则设置", _spImpApplyRules, v => _spImpApplyRules = v, fontSize: 12.5f);
            }
            if ((bool?)p["hasActiveRun"] == true)
            {
                SpLine(body, "你有一局正在进行，它不受影响；导入的设置从下一局开始生效。", 12.5f, NativeUguiFactory.ColMuted);
            }
            canApply = true;
        }

        SpFooterButton("关闭", SpClosePopup, width: 100f);
        SpFooterButton(_spImpBusy && p != null ? "导入中…" : "套用到下一局", SpDoImportApply, gold: true, width: 150f, enabled: canApply && !_spImpBusy);
        SpEndPopup();
    }

    private void SpImportFromClipboard()
    {
        string clip = "";
        try { clip = GUIUtility.systemCopyBuffer ?? ""; } catch { }
        clip = clip.Trim();
        if (clip.Length == 0)
        {
            _spImpError = "剪贴板是空的：先在朋友发来的消息里复制分享码。";
            SpRenderImport();
            return;
        }
        if (clip.Length > 2 * 1024 * 1024)
        {
            _spImpError = "剪贴板里的内容太长了，不像是分享码。";
            SpRenderImport();
            return;
        }
        if (clip.Length > 128 || clip.StartsWith("BZR1:", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("{", StringComparison.Ordinal))
        {
            _spImpBuffer = clip;
            _spImpCodeText = "";
        }
        else
        {
            _spImpBuffer = null;
            _spImpCodeText = clip;
        }
        SpDoImportPreview();
    }

    private void SpDoImportPreview()
    {
        if (_spImpBusy) return;
        string code = (_spImpBuffer ?? _spImpCodeText ?? "").Trim();
        if (code.Length == 0)
        {
            _spImpError = "先粘贴分享码。";
            SpRenderImport();
            return;
        }
        _spImpBusy = true;
        _spImpError = null;
        _spImpPreview = null;
        SpRenderImport();
        string body = new JObject { ["code"] = code }.ToString(Formatting.None);
        Task.Run(async () =>
        {
            var resp = await NameAuth.PostJsonAsync("/api/planner/blueprints/preview", body).ConfigureAwait(false);
            JObject o = null;
            try { if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body)) o = JObject.Parse(resp.Body); } catch { }
            _pendingUpdates.Enqueue(() =>
            {
                _spImpBusy = false;
                if (o != null)
                {
                    _spImpPreview = o;
                    _spImpCode = code;
                    bool hasGhosts = SpStr(o["scope"]) == "events_and_ghosts" && SpIntList(o["ghostDays"]).Count > 0;
                    _spImpOpp = hasGhosts ? "lineup" : "random";
                    _spImpApplyRules = false;
                    _spImpEnableMods.Clear();
                    if (o["modDiff"]?["autoEnable"] is JArray auto)
                    {
                        foreach (var m in auto.OfType<JObject>())
                        {
                            string id = SpStr(m["modId"]);
                            if (!string.IsNullOrEmpty(id)) _spImpEnableMods.Add(id);
                        }
                    }
                }
                else
                {
                    var (c, msg) = SpError(resp);
                    _spImpError = c switch
                    {
                        "CODE_NOT_FOUND" => "这个短码在本服务器上不存在。朋友在别的服务器的话，请他发长码（BZR1: 开头）。",
                        "CODE_INVALID" => "看不懂这个码：" + msg,
                        _ => "识别失败：" + msg,
                    };
                }
                if (_spPopupKind == "import" && SpIsPopupOpen()) SpRenderImport();
            });
        });
    }

    private void SpDoImportApply()
    {
        if (_spImpBusy || _spImpPreview == null || string.IsNullOrEmpty(_spImpCode)) return;
        _spImpBusy = true;
        _spImpError = null;
        SpRenderImport();
        bool applyRules = _spImpApplyRules;
        var enable = _spImpEnableMods.ToList();
        string body = new JObject
        {
            ["code"] = _spImpCode,
            ["opponents"] = _spImpOpp,
            ["applyRules"] = applyRules,
            ["enableModIds"] = new JArray(enable),
            ["apply"] = true,
        }.ToString(Formatting.None);

        Task.Run(async () =>
        {
            var resp = await NameAuth.PostJsonAsync("/api/planner/blueprints/import", body).ConfigureAwait(false);
            JObject o = null;
            CustomRunRuleSet fullRules = null;
            try
            {
                if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body))
                {
                    o = JObject.Parse(resp.Body);
                    if (applyRules && o["rules"] is JObject rj) fullRules = TryDeserializeRules(rj.ToString(Formatting.None));
                }
            }
            catch { }

            _pendingUpdates.Enqueue(() =>
            {
                _spImpBusy = false;
                if (o != null)
                {
                    if (fullRules != null)
                    {
                        // 玩家勾了「套用分享里的规则」：整份换成服务端合并后的规则
                        fullRules.NormalizeBounds();
                        _normalRules = fullRules;
                        _normalBuf.SyncFromRules(_normalRules);
                        Modules.NativeMenuEmbedModule.Instance?.SyncSelectedQuickModeFromRules(_normalRules.SpecialModeId, _normalRules.PresetId);
                    }
                    else if (o["rules"] is JObject r)
                    {
                        // 只写回种子 / 剧本三项和新开的 Mod，面板里没保存的其他改动保留
                        _normalRules.CustomSeedText = SpStr(r["customSeedText"]);
                        _normalRules.GhostOpponentMode = SpGhostMode(r["ghostOpponentMode"]);
                        _normalRules.SelectedGhostLineupId = SpStr(r["selectedGhostLineupId"]);
                        _normalRules.EnsureCollections();
                        foreach (string id in SpStrList(o["rulesPatch"]?["addModIds"]))
                        {
                            if (!_normalRules.SelectedModIds.Contains(id, StringComparer.OrdinalIgnoreCase)) _normalRules.SelectedModIds.Add(id);
                        }
                    }
                    var presync = SpStrList(o["rulesPatch"]?["addModIds"]);
                    presync.AddRange(enable);
                    SpPresyncMods(presync);

                    _spImpDoneMsg = SpStr(o["messageZh"]) ?? "导入成功，下一局生效";
                    _spImpDoneLineupId = (o["lineup"] as JObject) != null ? SpStr(o["lineup"]["lineupId"]) : null;
                    ShowToastBanner(Truncate(_spImpDoneMsg, 60));
                    SetStatus(_spImpDoneMsg, isError: false);
                    _rulesVersion++;
                    MarkHudDirty();
                    SpSyncSeedInput(force: true);
                    SpFetchContextAsync(manual: false);
                    if (_isOpen) RefreshModalContentsIfOpen();
                }
                else
                {
                    var (c, msg) = SpError(resp);
                    _spImpError = c == "LINEUP_LIMIT" ? "方案已经满了（最多 50 套），先在网页里删掉一些。" : "导入失败：" + msg;
                }
                if (_spPopupKind == "import" && SpIsPopupOpen()) SpRenderImport();
            });
        });
    }

    // ═══════════════ 收藏打过的对手 / 沙盒盘面存剧本（共用「存到哪个剧本、哪一天」）═══════════════
    private const string SpFavoriteLineupName = "收藏的对手";
    private string _spCapKind = "";            // "faced" | "sandbox"
    private string _spCapRunPick = "";         // "active" | "recent"
    private int _spCapSourceDay;
    private string _spCapLineupId;             // null = 新建剧本
    private string _spCapNewName = SpFavoriteLineupName;
    private int _spCapTargetDay = 1;
    private bool _spCapBusy;
    private string _spCapError, _spCapDone;

    private SpRun SpCapRun() => _spCapRunPick == "active" ? _spCtx?.ActiveRun : _spCapRunPick == "recent" ? _spCtx?.RecentRun : null;

    private void SpOpenFavoritePopup()
    {
        if (_spCtx == null || !_spCtx.Supported)
        {
            ShowToastBanner(_spCtx == null ? "还在读取对局信息，稍后再点" : "当前服务器还不支持对手方案", isError: true);
            if (_spCtx == null) SpFetchContextAsync(manual: true);
            return;
        }
        var a = _spCtx.ActiveRun;
        var r = _spCtx.RecentRun;
        if (a != null && !a.IsFinished && a.FacedDays.Count > 0) _spCapRunPick = "active";
        else if (r != null && r.FacedDays.Count > 0) _spCapRunPick = "recent";
        else
        {
            ShowToastBanner("还没有打过 PvP 的对局可以收藏：打完一晚的 PvP 再来", isError: true);
            return;
        }
        _spCapKind = "faced";
        var run = SpCapRun();
        _spCapSourceDay = run.FacedDays[run.FacedDays.Count - 1];
        _spCapTargetDay = _spCapSourceDay;
        SpCapPickDefaultLineup();
        _spCapError = _spCapDone = null;
        _spPopupKind = "";
        SpRenderCapture();
    }

    /// <summary>沙盒控制台「存为剧本某天的对手」按钮。</summary>
    private void SpOpenCaptureSandboxPopup()
    {
        if (_spCtx != null && !_spCtx.Supported)
        {
            ShowToastBanner("当前服务器还不支持对手方案", isError: true);
            return;
        }
        if (_spCtx == null) SpFetchContextAsync(manual: false); // 剧本列表回来后会自动重画弹窗
        _spCapKind = "sandbox";
        _spCapRunPick = "";
        var a = _spCtx?.ActiveRun;
        _spCapTargetDay = a != null && !a.IsFinished && a.Day > 0 ? a.Day : 1;
        _spCapLineupId = null;
        _spCapNewName = "沙盒方案";
        _spCapError = _spCapDone = null;
        _spPopupKind = "";
        SpRenderCapture();
    }

    private void SpCapPickDefaultLineup()
    {
        var fav = _spCtx?.Lineups.FirstOrDefault(l => string.Equals(l.Name, SpFavoriteLineupName, StringComparison.Ordinal));
        _spCapLineupId = fav?.Id;
        _spCapNewName = SpFavoriteLineupName;
    }

    private void SpRenderCapture()
    {
        bool faced = _spCapKind == "faced";
        SpBeginPopup("capture", faced ? "收藏打过的对手" : "把沙盒盘面存成方案", 860f, 600f);
        var body = _spPopupBody;

        if (!string.IsNullOrEmpty(_spCapDone))
        {
            var ok = _ui.CreateCard("Done", body, NativeUguiFactory.ColSubCardBg, NativeUguiFactory.ColEmeraldText, padH: 12, padV: 10, spacing: 6f);
            SpLine(ok.transform, _spCapDone, 14f, NativeUguiFactory.ColEmeraldText, bold: true);
            SpLine(ok.transform, "想下一局就遇到它：在规则页卡片里把这个方案点成「使用中」。想调整装备或血量：去网页改。", 12.5f);
            SpFooterButton("去网页详细编辑", () => { SpClosePopup(); SpOpenWebPlanner(null); }, width: 150f);
            SpFooterButton("完成", SpClosePopup, gold: true, width: 100f);
            SpEndPopup();
            return;
        }

        if (faced)
        {
            SpLine(body, "把某一晚遇到的 PvP 对手（他当时的整套阵容）存进方案，以后可以安排在任意一天再打一次。", 12.5f, NativeUguiFactory.ColMuted);
            var a = _spCtx?.ActiveRun;
            var r = _spCtx?.RecentRun;
            bool aOk = a != null && !a.IsFinished && a.FacedDays.Count > 0;
            bool rOk = r != null && r.FacedDays.Count > 0;
            SpChoiceRow(body, "哪一局", new List<(string, string, bool)>
            {
                ("active", aOk ? $"进行中：{a.HeroZh} 第{a.Day}天" : "进行中的对局（没有）", aOk),
                ("recent", rOk ? $"上一局：{r.HeroZh} 第{r.Day}天" : "上一局（没有）", rOk),
            }, _spCapRunPick, k =>
            {
                _spCapRunPick = k;
                var run2 = SpCapRun();
                if (run2 != null && run2.FacedDays.Count > 0)
                {
                    _spCapSourceDay = run2.FacedDays[run2.FacedDays.Count - 1];
                    _spCapTargetDay = _spCapSourceDay;
                }
                SpRenderCapture();
            }, 240f);

            var run = SpCapRun();
            if (run != null && run.FacedDays.Count > 0)
            {
                // 每行最多 10 天
                for (int i = 0; i < run.FacedDays.Count; i += 10)
                {
                    var chunk = run.FacedDays.Skip(i).Take(10)
                        .Select(d => (d.ToString(CultureInfo.InvariantCulture), $"第{d}天", true))
                        .ToList();
                    SpChoiceRow(body, i == 0 ? "哪一晚的对手" : "", chunk, _spCapSourceDay.ToString(CultureInfo.InvariantCulture), k =>
                    {
                        if (int.TryParse(k, out int d))
                        {
                            _spCapSourceDay = d;
                            _spCapTargetDay = d;
                        }
                        SpRenderCapture();
                    }, 66f);
                }
            }
        }
        else
        {
            SpLine(body, "把你现在沙盒局里的盘面（物品、技能、血量、等级）存成方案里某一天的对手。只有在「沙盒实验」模式的对局里才能用。", 12.5f, NativeUguiFactory.ColMuted);
        }

        // 存到哪个剧本
        SpLine(body, "存到哪个方案", 13f, NativeUguiFactory.ColCream, bold: true);
        var list = _ui.CreateVerticalGroup("Targets", body, spacing: 4f);
        var newRow = _ui.CreateHorizontalRow("New", list.transform, spacing: 8f);
        var newBtn = SpButton(newRow.transform, _spCapLineupId == null ? "已选" : "选这个", () => { _spCapLineupId = null; SpRenderCapture(); }, 76f, small: true);
        SpToggleVisual(newBtn, _spCapLineupId == null);
        var nl = _ui.CreateText("Lbl", newRow.transform, "新建方案，名字：", 13f, NativeUguiFactory.ColCream, wrap: false);
        NativeUguiFactory.SetLayout(nl.gameObject, minW: 120f, prefW: 120f, minH: 28f);
        var nameIn = _ui.CreateInputField("Name", newRow.transform, "方案名字", _spCapNewName, 13f, 40);
        NativeUguiFactory.SetLayout(nameIn.gameObject, minW: 220f, prefW: 280f, minH: 28f, prefH: 28f);
        nameIn.onValueChanged.AddListener(v => _spCapNewName = v ?? "");
        nameIn.onSelect.AddListener(_ =>
        {
            if (_spCapLineupId != null)
            {
                _spCapLineupId = null; // 点名字输入框就是想新建；只改状态，不重画，免得打断输入
                SpToggleVisual(newBtn, true);
                newBtn.Label.text = "已选";
            }
        });

        var lineups = _spCtx?.Lineups ?? new List<SpLineup>();
        if (_spCtx == null) SpLine(list.transform, "正在读取你的方案列表…", 12.5f, NativeUguiFactory.ColMuted);
        foreach (var l in lineups.Take(12))
        {
            var row = _ui.CreateHorizontalRow("Lineup", list.transform, spacing: 8f);
            string id = l.Id;
            bool sel = string.Equals(_spCapLineupId, id, StringComparison.OrdinalIgnoreCase);
            var b = SpButton(row.transform, sel ? "已选" : "选这个", () => { _spCapLineupId = id; SpRenderCapture(); }, 76f, small: true);
            SpToggleVisual(b, sel);
            var t = _ui.CreateText("Name", row.transform, $"{l.Name}　<color=#9C968C>{SpFormatDays(l.Days)}已安排</color>", 13f, sel ? NativeUguiFactory.ColGold : NativeUguiFactory.ColCream, wrap: false);
            NativeUguiFactory.SetLayout(t.gameObject, flexW: 1f, minH: 26f);
        }
        if (lineups.Count > 12) SpLine(list.transform, $"还有 {lineups.Count - 12} 个方案没列出（可以在网页里整理）。", 12f, NativeUguiFactory.ColMuted);

        // 放在第几天
        var dayRow = _ui.CreateHorizontalRow("Day", body, spacing: 8f);
        var dl = _ui.CreateText("Lbl", dayRow.transform, "放在方案的第几天", 13f, NativeUguiFactory.ColCream, FontStyles.Bold, wrap: false);
        NativeUguiFactory.SetLayout(dl.gameObject, minW: 140f, prefW: 140f, minH: 30f);
        var dayIn = _ui.CreateInputField("DayInput", dayRow.transform, "1~30", _spCapTargetDay.ToString(CultureInfo.InvariantCulture), 14f, 2, TMP_InputField.ContentType.IntegerNumber);
        NativeUguiFactory.SetLayout(dayIn.gameObject, minW: 70f, prefW: 70f, minH: 30f, prefH: 30f);
        var dayWarn = _ui.CreateText("Warn", dayRow.transform, "", 12.5f, SpColWarn, wrap: true);
        NativeUguiFactory.SetLayout(dayWarn.gameObject, flexW: 1f, minH: 30f);
        void UpdateDayWarn()
        {
            var target = SpFindLineup(_spCapLineupId);
            dayWarn.text = target != null && target.Days.Contains(_spCapTargetDay)
                ? $"这个方案第 {_spCapTargetDay} 天已经有对手，保存会替换掉它。"
                : "";
        }
        dayIn.onValueChanged.AddListener(v =>
        {
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d)) _spCapTargetDay = d;
            UpdateDayWarn();
        });
        UpdateDayWarn();

        if (!string.IsNullOrEmpty(_spCapError)) SpLine(body, _spCapError, 13f, NativeUguiFactory.ColErrorText);

        SpFooterButton("取消", SpClosePopup, width: 100f);
        SpFooterButton(_spCapBusy ? "保存中…" : "保存到方案", SpDoCapture, gold: true, width: 140f, enabled: !_spCapBusy);
        SpEndPopup();
    }

    private void SpDoCapture()
    {
        if (_spCapBusy) return;
        if (_spCapTargetDay < 1 || _spCapTargetDay > 30)
        {
            _spCapError = "天数要在 1~30 之间。";
            SpRenderCapture();
            return;
        }
        string newName = string.IsNullOrWhiteSpace(_spCapNewName) ? (_spCapKind == "faced" ? SpFavoriteLineupName : "沙盒方案") : _spCapNewName.Trim();
        if (newName.Length > 40) newName = newName.Substring(0, 40);
        string lineupId = _spCapLineupId;
        int targetDay = _spCapTargetDay;
        string path;
        JObject req;
        if (_spCapKind == "faced")
        {
            var run = SpCapRun();
            if (run == null)
            {
                _spCapError = "找不到这局了，点卡片上的「同步」后再试。";
                SpRenderCapture();
                return;
            }
            path = "/api/planner/lineups/capture-faced";
            req = new JObject
            {
                ["runId"] = string.IsNullOrEmpty(run.RunId) ? null : run.RunId,
                ["sourceDay"] = _spCapSourceDay,
                ["lineupId"] = lineupId,
                ["newLineupName"] = newName,
                ["targetDay"] = targetDay,
            };
        }
        else
        {
            path = "/api/planner/lineups/capture-sandbox";
            req = new JObject { ["lineupId"] = lineupId, ["newLineupName"] = newName, ["day"] = targetDay };
        }

        _spCapBusy = true;
        _spCapError = null;
        SpRenderCapture();
        string body = req.ToString(Formatting.None);
        Task.Run(async () =>
        {
            var resp = await NameAuth.PostJsonAsync(path, body).ConfigureAwait(false);
            JObject o = null;
            try { if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body)) o = JObject.Parse(resp.Body); } catch { }
            _pendingUpdates.Enqueue(() =>
            {
                _spCapBusy = false;
                if (o != null)
                {
                    string savedName = SpStr(o["lineup"]?["name"]) ?? SpFindLineup(lineupId)?.Name ?? newName;
                    _spCapDone = $"已存进方案「{savedName}」第 {targetDay} 天";
                    ShowToastBanner(_spCapDone);
                    SpFetchContextAsync(manual: false);
                }
                else
                {
                    var (c, msg) = SpError(resp);
                    _spCapError = c switch
                    {
                        "NOT_IN_SANDBOX" => "现在不在沙盒局里：先在主大厅切到「沙盒实验」模式开一局。",
                        "NO_FACED_GHOST" => "这一晚没有记录到对手。",
                        "LINEUP_LIMIT" => "方案已经满了（最多 50 套），先在网页里删掉一些。",
                        _ => "没保存成功：" + msg,
                    };
                }
                if (_spPopupKind == "capture" && SpIsPopupOpen()) SpRenderCapture();
            });
        });
    }
}
