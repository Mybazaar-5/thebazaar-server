using System.Diagnostics;
using System.Text;
using BazaarCore.Tests.TestInfrastructure;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 4B 轨 B `/workshop` 前端契约与静态/运行时回归测试：
/// 1. #9 窄横屏（1024px / 1280px）下 6 个顶栏标签自适应紧凑与折行防右缘截断；
/// 2. #9 本机全功能读写模式（mode=local-rw, canWrite=true）自动折叠顶栏公网管理口令栏，且保留按需展开与公网模式正常展示；
/// 3. #2/#3 前端词缀拼装器（studio-v14.js）写入标准 TPlayerConditionalAttribute.attribute、带 target 的数值引用，以及 TDeterminantDuration(UntilEndOfCombat) 防战中临时 Buff 永久滚雪球或 1 秒失效；
/// 4. Node 语法检查与核心 JS 函数运行时强断言。
/// </summary>
public sealed class Wave4B_WorkshopUiContractTests
{
    private static string RepoRoot => SharedGameDataFixture.Shared.RepoRoot;
    private static string WorkshopRoot => Path.Combine(RepoRoot, "src", "BazaarServer", "wwwroot", "workshop");

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
    public void WorkshopUi_NarrowLandscapeTabs_AndLocalAutoCollapseTokenBox_Contract()
    {
        string html = File.ReadAllText(Path.Combine(WorkshopRoot, "index.html"), Encoding.UTF8);
        string css = File.ReadAllText(Path.Combine(WorkshopRoot, "styles.css"), Encoding.UTF8);
        string appJs = File.ReadAllText(Path.Combine(WorkshopRoot, "app.js"), Encoding.UTF8);

        // 1. 窄横屏（含 1280px / 1024px）防 6 号标签右缘截断：必须有窄横屏媒体查询与自适应紧凑/换行规则
        Assert.Contains("tabbar", css);
        Assert.Contains("flex-wrap: wrap", css);
        Assert.Matches(@"@media\s*\(\s*max-width:\s*1(280|320|360)px\s*\)", css);
        Assert.Matches(@"@media\s*\(\s*max-width:\s*1(024|120|180)px\s*\)", css);

        // 2. 顶栏管理口令栏按权限模式自动折叠与按需展开
        Assert.Contains("id=\"tokenBox\"", html);
        Assert.Contains("id=\"btnToggleTokenBox\"", html);
        Assert.Contains("syncTokenBoxVisibility", appJs);
        Assert.Contains("local-rw", appJs);
        Assert.Contains("token-box-collapsed", css);
    }

    [Fact]
    public void WorkshopAffixBuilder_UsesStandardConditionalAttribute_TargetRefs_AndDeterminantCombatDuration()
    {
        string studioJs = File.ReadAllText(Path.Combine(WorkshopRoot, "studio-v14.js"), Encoding.UTF8);
        string appJs = File.ReadAllText(Path.Combine(WorkshopRoot, "app.js"), Encoding.UTF8);

        // 1. TPlayerConditionalAttribute 必须输出标准字段 attribute 而非 attributeType
        Assert.DoesNotMatch(@"\$type:\s*'TPlayerConditionalAttribute'\s*,\s*attributeType:", studioJs);
        Assert.Matches(@"\$type:\s*'TPlayerConditionalAttribute'\s*,\s*attribute:", studioJs);

        // 2. 战中临时属性修改必须默认输出 TDeterminantDuration(UntilEndOfCombat) 而非无 durationInMs 的 TCombatDuration
        Assert.Contains("TDeterminantDuration", studioJs);
        Assert.Contains("UntilEndOfCombat", studioJs);
        Assert.DoesNotContain("a.duration = { $type: 'TCombatDuration' }", studioJs);

        // 3. 禁止旧版无 target 的 TReferenceValueCardTagCount 错误拼装
        Assert.DoesNotContain("{ $type: 'TReferenceValueCardTagCount', tags:", studioJs);

        // 4. Node 语法检查与核心逻辑运行时测试（若环境有 node）
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

        // 运行时断言 A：测试 app.js 的 syncTokenBoxVisibility 在 local-rw / token-rw / 公网只读 三种模式下的折叠行为
        string appTestScript = """
            const fs = require('fs');
            const src = fs.readFileSync(process.argv[2], 'utf8');
            const m = src.match(/function syncTokenBoxVisibility\(([^)]*)\)\s*\{([\s\S]*?)\r?\n  \}/);
            if (!m) throw new Error('missing syncTokenBoxVisibility');
            const makeEl = () => {
              const classes = new Set();
              return {
                textContent: '',
                title: '',
                classList: {
                  add: (c) => classes.add(c),
                  remove: (c) => classes.delete(c),
                  toggle: (c, f) => (f ? classes.add(c) : classes.delete(c)),
                  contains: (c) => classes.has(c)
                }
              };
            };
            const els = { tokenBox: makeEl(), btnToggleTokenBox: makeEl() };
            const $ = (id) => els[id] || null;
            const state = { tokenBoxManualExpanded: false };
            const fn = new Function('$', 'state', m[1], m[2]);

            // 1. 本机全功能读写（local-rw）：默认折叠 tokenBox，显示展开小按钮
            fn($, state, { canWrite: true, mode: 'local-rw' });
            if (!els.tokenBox.classList.contains('hidden')) throw new Error('local-rw must hide tokenBox');
            if (els.btnToggleTokenBox.classList.contains('hidden')) throw new Error('local-rw should show toggle button');

            // 2. 本机模式下手动展开
            state.tokenBoxManualExpanded = true;
            fn($, state, { canWrite: true, mode: 'local-rw' });
            if (els.tokenBox.classList.contains('hidden')) throw new Error('manual expand should reveal tokenBox');

            // 3. 公网只读模式：必须直接展示 tokenBox，隐藏折叠按钮
            state.tokenBoxManualExpanded = false;
            fn($, state, { canWrite: false, mode: 'public-ro' });
            if (els.tokenBox.classList.contains('hidden')) throw new Error('public-ro must show tokenBox');
            if (!els.btnToggleTokenBox.classList.contains('hidden')) throw new Error('public-ro should hide toggle button');

            // 4. 公网口令已解锁模式（token-rw）：保持展示 tokenBox 便于更换或清空口令
            fn($, state, { canWrite: true, mode: 'token-rw' });
            if (els.tokenBox.classList.contains('hidden')) throw new Error('token-rw should show tokenBox');

            console.log('TOKEN_BOX_OK');
            """;

        string tmpAppTest = Path.Combine(Path.GetTempPath(), $"w4b_app_{Guid.NewGuid():N}.js");
        string tmpStudioTest = Path.Combine(Path.GetTempPath(), $"w4b_studio_{Guid.NewGuid():N}.js");
        try
        {
            File.WriteAllText(tmpAppTest, appTestScript, Encoding.UTF8);
            var (rcApp, outApp, errApp) = RunNode($"\"{tmpAppTest}\" \"{Path.Combine(WorkshopRoot, "app.js")}\"");
            Assert.True(rcApp == 0 && outApp.Contains("TOKEN_BOX_OK"), $"app.js 运行时测试失败:\nOUT={outApp}\nERR={errApp}");

            // 运行时断言 B：测试 studio-v14.js 的 buildDurationRuleNode / parseDurationFromRuleNode / buildValueRuleNode
            string studioTestScript = """
                const fs = require('fs');
                const src = fs.readFileSync(process.argv[2], 'utf8');
                const extractFn = (name) => {
                  const re = new RegExp(`function ${name}\\(([^)]*)\\)\\s*\\{([\\s\\S]*?)\\r?\\n  \\}`);
                  const m = src.match(re);
                  if (!m) throw new Error('missing ' + name);
                  return { args: m[1], body: m[2] };
                };
                const buildDurDef = extractFn('buildDurationRuleNode');
                const parseDurDef = extractFn('parseDurationFromRuleNode');
                const buildValDef = extractFn('buildValueRuleNode');

                const buildDur = new Function(buildDurDef.args, buildDurDef.body);
                const parseDur = new Function(parseDurDef.args, parseDurDef.body);
                const buildVal = new Function(buildValDef.args, buildValDef.body);

                const combatDur = buildDur('Combat', 2000);
                if (!combatDur || combatDur.$type !== 'TDeterminantDuration' || combatDur.durationType !== 'UntilEndOfCombat')
                  throw new Error('Combat duration must emit TDeterminantDuration(UntilEndOfCombat): ' + JSON.stringify(combatDur));

                const parsedCombat = parseDur(combatDur);
                if (parsedCombat.duration !== 'Combat')
                  throw new Error('parseDurationFromRuleNode failed on TDeterminantDuration(UntilEndOfCombat): ' + JSON.stringify(parsedCombat));

                const timedDur = buildDur('TimedCombat', 3000);
                if (!timedDur || timedDur.$type !== 'TCombatDuration' || timedDur.durationInMs !== 3000)
                  throw new Error('TimedCombat duration must emit TCombatDuration(durationInMs): ' + JSON.stringify(timedDur));

                const parsedTimed = parseDur(timedDur);
                if (parsedTimed.duration !== 'TimedCombat' || parsedTimed.durationMs !== 3000)
                  throw new Error('parseDurationFromRuleNode failed on TCombatDuration: ' + JSON.stringify(parsedTimed));

                if (buildDur('Permanent', 0) !== undefined)
                  throw new Error('Permanent duration must be undefined');

                const tagCountVal = buildVal('cardTagCount', 'Weapon');
                if (!tagCountVal || tagCountVal.$type !== 'TReferenceValueCardCount' || !tagCountVal.target || tagCountVal.target.$type !== 'TTargetCardSection')
                  throw new Error('cardTagCount must emit TReferenceValueCardCount with TTargetCardSection target: ' + JSON.stringify(tagCountVal));

                console.log('STUDIO_DURATION_AND_REF_OK');
                """;

            File.WriteAllText(tmpStudioTest, studioTestScript, Encoding.UTF8);
            var (rcSt, outSt, errSt) = RunNode($"\"{tmpStudioTest}\" \"{Path.Combine(WorkshopRoot, "studio-v14.js")}\"");
            Assert.True(rcSt == 0 && outSt.Contains("STUDIO_DURATION_AND_REF_OK"), $"studio-v14.js 运行时测试失败:\nOUT={outSt}\nERR={errSt}");
        }
        finally
        {
            try { if (File.Exists(tmpAppTest)) File.Delete(tmpAppTest); } catch { }
            try { if (File.Exists(tmpStudioTest)) File.Delete(tmpStudioTest); } catch { }
        }
    }
}
