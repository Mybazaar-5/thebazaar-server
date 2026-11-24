using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Tests.TestInfrastructure;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 5C（Wave 3 w3-workshop）`/workshop` 前端排版与文案一致性回归测试：
/// 1. P2-14（RC-WS-TABBAR-VSCROLL）：`.tabbar` 显式禁用纵向溢出，`.tab-btn.active` 改用负底边距贴边，根除双分辨率白色纵向滚动条；
/// 2. P2-13（RC-WS-CARD-STUDIO-FIT）：Tab 2 左侧 `.bcr-actions` 允许换行且紧凑适配防「🧲 提取效果」裁切，左右栏切换键与预览标题消除「补/丁」「预/览」单字折行；
/// 3. P2-15（RC-WS-OVERVIEW-GRID）：Tab 1 统计卡采用确定性断点（宽屏 6 列 / 窄横屏 ≤1440px 含 1280px 整齐 3×2），双列 `.field` 底部对齐消除长 label 基线错位；
/// 4. P2-16（RC-WS-COPY-SEMANTICS）：统一 Tab 5 空态与 Tab 6 流程按钮文案与实际控件一致；Tab 4 奇遇英雄独立映射「✨ 全英雄通用 (Common)」且与物品中立商店语境解耦；
/// 5. P3-07（RC-WS-PANEL-HEADER）：`.panel-header` 右侧 `.btn-row` 禁止被挤压收缩，左侧动态长摘要独立成副标题行展示。
/// </summary>
public sealed class Wave5C_WorkshopUiFixesTests
{
    private static string RepoRoot => SharedGameDataFixture.Shared.RepoRoot;
    private static string WorkshopRoot => Path.Combine(RepoRoot, "src", "BazaarServer", "wwwroot", "workshop");

    private static string ReadWorkshopFile(string fileName)
        => File.ReadAllText(Path.Combine(WorkshopRoot, fileName), Encoding.UTF8);

    private static string ExtractCssBlock(string css, string selectorPattern)
    {
        var match = Regex.Match(
            css,
            selectorPattern + @"\s*\{(?<body>[^{}]*)\}",
            RegexOptions.Multiline);
        Assert.True(match.Success, $"在 styles.css 中未找到选择器规则：{selectorPattern}");
        return match.Groups["body"].Value;
    }

    private static string ExtractMediaBlock(string css, int maxWidthPx)
    {
        string marker = $"@media (max-width: {maxWidthPx}px)";
        int start = css.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"在 styles.css 中未找到媒体查询：{marker}");
        int braceOpen = css.IndexOf('{', start);
        Assert.True(braceOpen >= 0, $"媒体查询缺少起始花括号：{marker}");

        int depth = 0;
        for (int i = braceOpen; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return css.Substring(braceOpen + 1, i - braceOpen - 1);
            }
        }

        Assert.Fail($"媒体查询花括号未闭合：{marker}");
        return string.Empty;
    }

    private static string ExtractElementInnerTextById(string html, string id)
    {
        var match = Regex.Match(
            html,
            $@"id=""{Regex.Escape(id)}""[^>]*>(?<inner>.*?)</(?:button|div|p|span)>",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"在 index.html 中未找到元素 id=\"{id}\"");
        return Regex.Replace(match.Groups["inner"].Value, "<[^>]+>", string.Empty).Trim();
    }

    private static (int ExitCode, string StdOut, string StdErr) RunNode(string arguments)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = arguments,
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.StandardInput.Close();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(20_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"node 执行超时: {arguments}");
        }
        proc.WaitForExit();
        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void P2_14_Tabbar_EliminatesVerticalScrollbar_AndActiveBottomOverlap()
    {
        string css = ReadWorkshopFile("styles.css");

        // 1. 基础 .tabbar 必须显式声明 overflow-y: hidden，避免 overflow-x: auto 触发隐式 overflow-y: auto
        string tabbarRule = ExtractCssBlock(css, @"(?m)^\.tabbar");
        Assert.Contains("overflow-x: auto", tabbarRule);
        Assert.Contains("overflow-y: hidden", tabbarRule);

        // 2. .tab-btn.active 必须移除会造成垂直 1px 溢出的 top: 1px，改用 top: 0 + margin-bottom: -1px
        string activeTabRule = ExtractCssBlock(css, @"(?m)^\.tab-btn\.active");
        Assert.DoesNotContain("top: 1px", activeTabRule);
        Assert.Contains("top: 0", activeTabRule);
        Assert.Contains("margin-bottom: -1px", activeTabRule);

        // 3. 窄屏折行断点（<=1180px）同步重置 overflow-y: visible 与 margin-bottom: 0，避免单轴 hidden 引发隐式滚动
        string media1180 = ExtractMediaBlock(css, 1180);
        Assert.Contains("overflow-x: visible", media1180);
        Assert.Contains("overflow-y: visible", media1180);
        Assert.Contains("margin-bottom: 0", media1180);
    }

    [Fact]
    public void P2_13_CardStudio_PreventsButtonClipping_AndSingleCharWrapping()
    {
        string css = ReadWorkshopFile("styles.css");
        string html = ReadWorkshopFile("index.html");
        string appJs = ReadWorkshopFile("app.js");

        // 1. Tab 2 原版白名单卡片操作行允许换行且配置紧凑按钮尺寸，配合滚动条留槽防第 3 按钮「🧲 提取效果」裁切
        string bcrActionsRule = ExtractCssBlock(css, @"(?m)^\.bcr-actions");
        Assert.Contains("flex-wrap: wrap", bcrActionsRule);

        string bcrBtnRule = ExtractCssBlock(css, @"(?m)^\.bcr-actions\s+\.btn-xs");
        Assert.Contains("padding:", bcrBtnRule);
        Assert.Contains("font-size:", bcrBtnRule);

        string scrollListRule = ExtractCssBlock(css, @"(?m)^\.scroll-list");
        Assert.Contains("scrollbar-gutter: stable", scrollListRule);

        // 2. 左栏模式切换键允许整组折行且按钮内禁止拆字，彻底消除「补/丁」单字折行
        string subtabRowRule = ExtractCssBlock(css, @"(?m)^\.subtab-row");
        Assert.Contains("flex-wrap: wrap", subtabRowRule);
        Assert.DoesNotContain("grid-template-columns: 1fr 1fr", subtabRowRule);

        string subtabRule = ExtractCssBlock(css, @"(?m)^\.subtab");
        Assert.Contains("white-space: nowrap", subtabRule);

        // 3. 右栏实时预览标题与品阶切换键允许整组折行且标题不拆字，彻底消除「预/览」单字折行
        string rightHeaderRule = ExtractCssBlock(css, @"(?m)^\.studio-col-right\s+\.panel-header\.compact");
        Assert.Contains("flex-wrap: wrap", rightHeaderRule);

        string rightH2Rule = ExtractCssBlock(css, @"(?m)^\.studio-col-right\s+\.panel-header\.compact\s+h2");
        Assert.Contains("white-space: nowrap", rightH2Rule);

        string tierSwitcherRule = ExtractCssBlock(css, @"(?m)^\.tier-switcher");
        Assert.Contains("flex-shrink: 0", tierSwitcherRule);

        // 4. 三栏布局与 1360px 窄横屏断点微调左栏宽度并防网格溢出
        string studioGridRule = ExtractCssBlock(css, @"(?m)^\.three-col-studio");
        Assert.Contains("minmax(0, 1fr)", studioGridRule);

        string media1360 = ExtractMediaBlock(css, 1360);
        Assert.Contains(".three-col-studio", media1360);

        // 5. 保留完整 3 枚操作按钮与 5 枚品阶切换键
        Assert.Contains("📋 复制为新卡", appJs);
        Assert.Contains("🔧 直接改原版", appJs);
        Assert.Contains("🧲 提取效果", appJs);
        Assert.Contains("📁 本 Mod 卡牌 &amp; 补丁", html);
        Assert.Contains("<h2>实时鎏金卡面预览</h2>", html);
    }

    [Fact]
    public void P2_15_OverviewGrid_UsesDeterministicBreakpoints_AndBottomAlignedFields()
    {
        string css = ReadWorkshopFile("styles.css");
        string html = ReadWorkshopFile("index.html");

        // 1. Tab 1 统计卡必须固定 6 张，宽屏固定 6 列，窄横屏（≤1440px，覆盖 1280×720）确定性切换 3×2，彻底删除 auto-fit 5+1 孤卡规则
        var stripMatch = Regex.Match(
            html,
            @"id=""currentModCounts""[^>]*>(?<inner>[\s\S]*?)</div>\s*<div class=""panel-footer-actions""",
            RegexOptions.Singleline);
        Assert.True(stripMatch.Success, "在 index.html 中未找到 #currentModCounts 统计网格");
        int chipCount = Regex.Matches(stripMatch.Groups["inner"].Value, @"class=""metric-chip""").Count;
        Assert.Equal(6, chipCount);

        string summaryStripRule = ExtractCssBlock(css, @"(?m)^\.summary-strip");
        Assert.Contains("repeat(6, minmax(0, 1fr))", summaryStripRule);
        Assert.DoesNotContain("auto-fit", css);
        Assert.DoesNotContain("minmax(98px", css);

        string media1440 = ExtractMediaBlock(css, 1440);
        Assert.Contains(".summary-strip", media1440);
        Assert.Contains("repeat(3, minmax(0, 1fr))", media1440);

        // 2. 双列表单与 .field 必须采用底部对齐布局，消除长 label 折行造成的输入框基线错位
        string formGridRule = ExtractCssBlock(css, @"(?m)^\.form-grid");
        Assert.Contains("repeat(2, minmax(0, 1fr))", formGridRule);

        string fieldRule = ExtractCssBlock(css, @"(?m)^\.field");
        Assert.Contains("display: flex", fieldRule);
        Assert.Contains("flex-direction: column", fieldRule);
        Assert.Contains("justify-content: flex-end", fieldRule);

        // 3. Tab 1 英文代号 label 保持简明单行，并将格式说明完整保留在 placeholder/title 中
        Assert.DoesNotContain("扩展包英文代号（用于防撞车前缀，仅小写字母/数字/短横线）*", html);
        Assert.Contains("id=\"mfId\"", html);
        Assert.Contains("仅小写字母/数字/短横线", html);
    }

    [Fact]
    public void P2_16_CopySemantics_AlignButtons_AndSeparateEventCommonHeroContext()
    {
        string html = ReadWorkshopFile("index.html");
        string studioJs = ReadWorkshopFile("studio-v14.js");

        // 1. 后端目录 DTO 语境解耦：heroes 维持物品/奇物店语义不回归，eventHeroes 独立输出「✨ 全英雄通用 (Common)」
        using JsonDocument catalogDoc = JsonDocument.Parse(JsonSerializer.Serialize(ModRuleCatalog.BuildCatalogDto()));
        JsonElement root = catalogDoc.RootElement;

        var itemCommon = root.GetProperty("heroes").EnumerateArray()
            .First(x => x.GetProperty("id").GetString() == HeroNames.Common);
        Assert.Equal("中立/奇物店专属 (Common)", itemCommon.GetProperty("nameZh").GetString());
        Assert.Contains("Curio", itemCommon.GetProperty("descZh").GetString() ?? string.Empty);

        var eventHeroes = root.GetProperty("eventHeroes").EnumerateArray().ToList();
        Assert.Equal(HeroNames.PlayableHeroes.Length + 1, eventHeroes.Count);
        var eventCommon = eventHeroes.First(x => x.GetProperty("id").GetString() == HeroNames.Common);
        string eventCommonName = eventCommon.GetProperty("nameZh").GetString() ?? string.Empty;
        Assert.Contains("全英雄通用", eventCommonName);
        Assert.DoesNotContain("中立/奇物店专属", eventCommonName);
        Assert.Equal("Common", eventCommon.GetProperty("id").GetString());

        // 2. HTML 引导文案与实际按钮文本 100% 一致
        string quickValidateBtnText = ExtractElementInnerTextById(html, "btnQuickValidate");
        string draftValidateBtnText = ExtractElementInnerTextById(html, "btnRunValidateDraft");
        string publishBtnText = ExtractElementInnerTextById(html, "btnPublishReload");

        Assert.Equal("🛡️ 安全检测", quickValidateBtnText);
        Assert.Equal("🧪 体检当前草稿（不写盘）", draftValidateBtnText);
        Assert.Equal("🚀 全绿安全发布并热重载进游戏", publishBtnText);

        // Tab 5 空态文案必须引用真实按钮名，禁止残留旧文案「10道体检」
        string gatesGridInner = ExtractElementInnerTextById(html, "gatesGrid");
        Assert.DoesNotContain("10道体检", html);
        Assert.Contains(quickValidateBtnText, gatesGridInner);
        Assert.Contains(draftValidateBtnText, gatesGridInner);

        // Tab 6 引导文案必须与 Tab 5 真实发布/体检按钮名严格一致，禁止残留「安全发布并热更新」
        Assert.DoesNotContain("安全发布并热更新", html);
        var tabGuideMatch = Regex.Match(
            html,
            @"id=""tab-guide""[^>]*>([\s\S]*?)</section>",
            RegexOptions.Singleline);
        Assert.True(tabGuideMatch.Success, "在 index.html 中未找到 #tab-guide 区块");
        string tabGuideHtml = tabGuideMatch.Groups[1].Value;
        Assert.Contains(publishBtnText, tabGuideHtml);
        Assert.Contains(quickValidateBtnText, tabGuideHtml);
        Assert.Contains(draftValidateBtnText, tabGuideHtml);

        // Tab 4 奇遇英雄选择说明与实际选项文案一致
        Assert.Contains("「✨ 全英雄通用 (Common)」", html);
        Assert.Contains("getEventHeroCatalog(cat)", studioJs);

        // 3. 若环境具备 Node，则进行前端语法校验及 getEventHeroCatalog 运行时语境隔离断言
        bool hasNode = false;
        try
        {
            var (vc, _, _) = RunNode("--version");
            hasNode = vc == 0;
        }
        catch (System.ComponentModel.Win32Exception) { }

        if (!hasNode) return;

        var (c1, _, e1) = RunNode($"--check \"{Path.Combine(WorkshopRoot, "app.js")}\"");
        Assert.True(c1 == 0, $"app.js 语法错误：{e1}");
        var (c2, _, e2) = RunNode($"--check \"{Path.Combine(WorkshopRoot, "studio-v14.js")}\"");
        Assert.True(c2 == 0, $"studio-v14.js 语法错误：{e2}");

        string nodeTestScript = """
            const fs = require('fs');
            const src = fs.readFileSync(process.argv[2], 'utf8');
            const m = src.match(/function getEventHeroCatalog\(([^)]*)\)\s*\{([\s\S]*?)\r?\n  \}/);
            if (!m) throw new Error('missing getEventHeroCatalog in studio-v14.js');
            const getEventHeroCatalog = new Function(m[1], m[2]);

            // 场景 1：优先使用后端下发的独立 eventHeroes
            const itemCommon = { id: 'Common', nameZh: '中立/奇物店专属 (Common)', descZh: '仅 Curio' };
            const evCommon = { id: 'Common', nameZh: '✨ 全英雄通用 (Common)', descZh: '任意英雄可遇' };
            const vanessa = { id: 'Vanessa', nameZh: '瓦内莎 (Vanessa)', descZh: '海盗' };
            const out1 = getEventHeroCatalog({ heroes: [itemCommon, vanessa], eventHeroes: [evCommon, vanessa] });
            if (out1[0].id !== 'Common' || !out1[0].nameZh.includes('全英雄通用') || out1[0].nameZh.includes('奇物店')) {
              throw new Error('eventHeroes 语境映射失败: ' + JSON.stringify(out1));
            }

            // 场景 2：旧版仅含 heroes 字典时仍兜底将 Common 映射为全英雄通用，且绝不污染原 itemCommon 对象
            const out2 = getEventHeroCatalog({ heroes: [itemCommon, vanessa] });
            if (out2[0].id !== 'Common' || !out2[0].nameZh.includes('全英雄通用') || out2[0].nameZh.includes('奇物店')) {
              throw new Error('fallback heroes 语境映射失败: ' + JSON.stringify(out2));
            }
            if (itemCommon.nameZh !== '中立/奇物店专属 (Common)') {
              throw new Error('意外修改了物品商店语境原对象');
            }

            // 场景 3：空目录兜底
            const out3 = getEventHeroCatalog({});
            if (out3.length !== 1 || out3[0].id !== 'Common' || !out3[0].nameZh.includes('全英雄通用')) {
              throw new Error('空目录兜底失败: ' + JSON.stringify(out3));
            }

            console.log('EVENT_HERO_CONTEXT_OK');
            """;

        string tmpScript = Path.Combine(Path.GetTempPath(), $"w5c_event_heroes_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tmpScript, nodeTestScript, Encoding.UTF8);
            var (rc, stdout, stderr) = RunNode($"\"{tmpScript}\" \"{Path.Combine(WorkshopRoot, "studio-v14.js")}\"");
            Assert.True(rc == 0 && stdout.Contains("EVENT_HERO_CONTEXT_OK"), $"studio-v14.js 奇遇英雄语境测试失败:\nOUT={stdout}\nERR={stderr}");
        }
        finally
        {
            try { if (File.Exists(tmpScript)) File.Delete(tmpScript); } catch { }
        }
    }

    [Fact]
    public void P3_07_PanelHeader_ProtectsActionButtonRow_AndExtractsLongSummaries()
    {
        string css = ReadWorkshopFile("styles.css");
        string html = ReadWorkshopFile("index.html");
        string appJs = ReadWorkshopFile("app.js");

        // 1. .panel-header 弹性布局保护：右侧 .btn-row 禁止被压缩，左侧标题容器 min-width: 0 + flex: 1 1 auto
        string panelHeaderRule = ExtractCssBlock(css, @"(?m)^\.panel-header");
        Assert.Contains("flex-wrap: wrap", panelHeaderRule);

        string headerFirstChildRule = ExtractCssBlock(css, @"(?m)^\.panel-header\s*>\s*:first-child");
        Assert.Contains("min-width: 0", headerFirstChildRule);
        Assert.Contains("flex: 1 1 auto", headerFirstChildRule);

        string headerBtnRowRule = ExtractCssBlock(css, @"(?m)^\.panel-header\s*>\s*\.btn-row");
        Assert.Contains("flex-shrink: 0", headerBtnRowRule);

        Assert.Contains(".panel-subtitle", css);
        Assert.Contains(".val-summary-banner", css);

        // 2. Tab 1 左栏 #metaStatsText 必须独立置于 .panel-header 下方，避免在 1280×720 下挤压右侧按钮叠行与孤词折行
        var tab1LeftHeaderMatch = Regex.Match(
            html,
            @"<div class=""panel-header[^""]*"">\s*<div>\s*<h2>已有扩展包列表</h2>(?<inside>[\s\S]*?)</div>\s*<div class=""btn-row"">[\s\S]*?</div>\s*</div>\s*(?<after><p[^>]*id=""metaStatsText""[^>]*>)",
            RegexOptions.Singleline);
        Assert.True(tab1LeftHeaderMatch.Success, "Tab 1 左侧 #metaStatsText 应作为独立副标题行位于 .panel-header 下方");
        Assert.DoesNotContain("metaStatsText", tab1LeftHeaderMatch.Groups["inside"].Value);

        // 3. Tab 5 #valSummaryBanner 必须独立置于 .panel-header 下方全宽展示，避免长体检摘要挤压顶部 3 按钮折行
        var tab5HeaderMatch = Regex.Match(
            html,
            @"<div class=""panel-header[^""]*"">\s*<div>\s*<h2>10 道工业级防 Bug 铁闸[\s\S]*?</h2>(?<inside>[\s\S]*?)</div>\s*<div class=""btn-row"">[\s\S]*?</div>\s*</div>\s*(?<after><p[^>]*id=""valSummaryBanner""[^>]*>)",
            RegexOptions.Singleline);
        Assert.True(tab5HeaderMatch.Success, "Tab 5 #valSummaryBanner 应作为独立全宽摘要横幅位于 .panel-header 下方");
        Assert.DoesNotContain("valSummaryBanner", tab5HeaderMatch.Groups["inside"].Value);

        // 4. app.js 对 #metaStatsText 使用不换行空格防孤词 Mod，对 #valSummaryBanner 切换通过/失败样式类
        Assert.Contains(@"\u00A0个\u00A0Mod", appJs);
        Assert.Contains("summaryEl.classList.toggle('is-pass'", appJs);
        Assert.Contains("summaryEl.classList.toggle('is-fail'", appJs);
    }
}
