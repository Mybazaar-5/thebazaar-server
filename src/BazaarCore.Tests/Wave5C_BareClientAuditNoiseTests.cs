using System.Reflection;
using System.Text.Json;
using BazaarCore.Tests.TestInfrastructure;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 5C（P3-01 / P3-02 / P3-03）：裸协议黑匣子误报降噪强断言单测。
/// 通过 Wave2CLauncherAssemblyFixture 直接加载真实 BazaarLauncher.dll 内的零 Unity 纯 .NET 核心组件，验证：
///   1. P3-01（RC-AUDIT-AURA-PURCHASE-FP）：同包真实 EffectAuraExecuted 命中购入卡时降级为 info 并豁免误报，
///      覆盖 Shoe Blade / Slingshot / Aludel / Virus / LauLau / Venomous Dose / Hammock / 冷却光环，
///      而无光环、仅合成刷新（OutOfCombatAuraSync）、目标为他卡/玩家、或 CooldownMax 0->非0 异常仍必报 error；
///   2. P3-02（RC-AUDIT-REVEAL-NOISE）：Data.CardRemoval.HasCardsPendingRemoval() 静默放行，
///      短于 2.0s 的多卡异步 SpawnCard 翻牌加载窗去抖不报，跨窗不误累加，超过 2.0s 持续异常及 spawned=true 仍必报 critical；
///   3. P3-03（RC-AUDIT-SSE-CANCEL）：StartRunAppState 主动取消主菜单 SSE GET /events 触发的无业务栈 Winsock 995（中/英文）/
///      NativeErrorCode 995 / OperationCanceled 降级过滤并写入 trace，而带业务栈、局内断连、非 995 异常或 .995 时间戳绝不误杀；
///   4. 100% 保持 Wave 4C 全部公开反射签名零歧义（无 AmbiguousMatchException）及 Runtime 接线完整。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave5C_BareClientAuditNoiseTests : IClassFixture<Wave2CLauncherAssemblyFixture>
{
    private readonly Wave2CLauncherAssemblyFixture _fx;

    public Wave5C_BareClientAuditNoiseTests(Wave2CLauncherAssemblyFixture fx)
    {
        _fx = fx;
    }

    private Type GetTypeRequired(string fullName)
        => _fx.LauncherAssembly.GetType(fullName, throwOnError: true)!;

    private object CreateAuraContext(
        string effectId,
        string executionContextId,
        string? sourceId,
        IEnumerable<string>? targetCardIds,
        bool hasAnyExplicitTarget)
    {
        Type ctxType = GetTypeRequired("BazaarLauncher.Core.BareAuditPurchaseAuraContext");
        object ctx = Activator.CreateInstance(ctxType)!;
        MethodInfo recordM = ctxType.GetMethod("RecordAuraEvent", BindingFlags.Public | BindingFlags.Instance)!;
        recordM.Invoke(ctx, new object?[] { effectId, executionContextId, sourceId, targetCardIds, hasAnyExplicitTarget });
        return ctx;
    }

    [Fact]
    public void P3_01_AuraExplainedPurchaseJumps_DowngradedAndExempted_WhileUnexplainedStillReportsError()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wave5c_p3_01_" + Guid.NewGuid().ToString("N"));
        string jsonlPath = Path.Combine(tempDir, "logs", "bare_client_audit.jsonl");
        try
        {
            Type engineType = GetTypeRequired("BazaarLauncher.Core.BareClientAuditEngine");
            Type detectorType = GetTypeRequired("BazaarLauncher.Core.ShopPurchaseAttributeDiffDetector");
            Type ctxType = GetTypeRequired("BazaarLauncher.Core.BareAuditPurchaseAuraContext");

            long nowMs = 20_000_000;
            Func<long> clock = () => nowMs;
            using var engine = (IDisposable)Activator.CreateInstance(
                engineType,
                new object?[] { true, jsonlPath, 48, 5000L, 60, 10000L, clock })!;

            MethodInfo captureShelf = engineType.GetMethod("CaptureShopItemBeforePurchase", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo verifyWithAura = engineType.GetMethod("VerifyShopItemAfterPurchaseWithAura", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo getChain = engineType.GetMethod("GetRecentEventChain", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo flushM = engineType.GetMethod("Flush", BindingFlags.Public | BindingFlags.Instance)!;

            // 1. 直接验证 ShopPurchaseAttributeDiffDetector：Shoe Blade 暴击 null->100 命中同包真实光环 -> ExplainedByAura=true, SuggestedSeverity="info"
            object detector = Activator.CreateInstance(detectorType, new object?[] { null })!;
            MethodInfo detCapture = detectorType.GetMethod("CaptureShelfSnapshot", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo detCompareAura = detectorType.GetMethod("CompareAfterPurchaseWithAura", BindingFlags.Public | BindingFlags.Instance)!;

            detCapture.Invoke(detector, new object?[]
            {
                "itm_shoe_det", "8391a75d-56e7-4206-a16e-62dbd986925f", "Shoe Blade",
                new Dictionary<string, int> { ["DamageAmount"] = 25, ["CooldownMax"] = 6000, ["BuyPrice"] = 4 },
                nowMs,
            });
            object shoeAuraCtx = CreateAuraContext("2", "ooc_aura_1_1_itm_shoe_det_2", "itm_shoe_det", new[] { "itm_shoe_det" }, true);
            object?[] compareArgs =
            {
                "itm_shoe_det",
                new Dictionary<string, int> { ["DamageAmount"] = 25, ["CooldownMax"] = 6000, ["CritChance"] = 100, ["SellPrice"] = 2 },
                nowMs + 50,
                shoeAuraCtx,
                null,
            };
            Assert.True((bool)detCompareAura.Invoke(detector, compareArgs)!);
            object jumpObj = compareArgs[4]!;
            Assert.True((bool)jumpObj.GetType().GetProperty("ExplainedByAura")!.GetValue(jumpObj)!);
            Assert.Equal("info", (string)jumpObj.GetType().GetProperty("SuggestedSeverity")!.GetValue(jumpObj)!);
            Assert.Contains("2", (string[])jumpObj.GetType().GetProperty("AuraEffectIds")!.GetValue(jumpObj)!);

            // 2. 端到端验证全部 8 类实机合法入场光环卡（Shoe Blade + 6 张实机非暴击卡 + 冷却减免光环）均被默认豁免不写误报 JSONL
            var legitCases = new (string Id, string Title, Dictionary<string, int> Before, Dictionary<string, int> After, string AuraId)[]
            {
                ("itm_shoe", "Shoe Blade",
                    new Dictionary<string, int> { ["DamageAmount"] = 25, ["BuyPrice"] = 4 },
                    new Dictionary<string, int> { ["DamageAmount"] = 25, ["CritChance"] = 100, ["SellPrice"] = 2 },
                    "2"),
                ("itm_slingshot", "Slingshot",
                    new Dictionary<string, int> { ["Ammo"] = 3, ["DamageAmount"] = 10 },
                    new Dictionary<string, int> { ["Ammo"] = 1, ["DamageAmount"] = 10 },
                    "1"),
                ("itm_aludel", "Aludel",
                    new Dictionary<string, int> { ["Multicast"] = 1, ["PoisonApplyAmount"] = 3 },
                    new Dictionary<string, int> { ["Multicast"] = 2, ["PoisonApplyAmount"] = 3 },
                    "1"),
                ("itm_virus", "Virus",
                    new Dictionary<string, int> { ["PoisonApplyAmount"] = 0 },
                    new Dictionary<string, int> { ["PoisonApplyAmount"] = 2 },
                    "3"),
                ("itm_laulau", "LauLau",
                    new Dictionary<string, int> { ["BurnApplyAmount"] = 0, ["RegenApplyAmount"] = 3 },
                    new Dictionary<string, int> { ["BurnApplyAmount"] = 3, ["RegenApplyAmount"] = 3 },
                    "1"),
                ("itm_venom", "Venomous Dose",
                    new Dictionary<string, int> { ["RegenApplyAmount"] = 0, ["PoisonApplyAmount"] = 2 },
                    new Dictionary<string, int> { ["RegenApplyAmount"] = 2, ["PoisonApplyAmount"] = 2 },
                    "1"),
                ("itm_hammock", "Hammock",
                    new Dictionary<string, int> { ["Custom_0"] = 0, ["HealAmount"] = 15 },
                    new Dictionary<string, int> { ["Custom_0"] = 2, ["HealAmount"] = 15 },
                    "2"),
                ("itm_sunderer", "Sunderer",
                    new Dictionary<string, int> { ["CooldownMax"] = 5000, ["DamageAmount"] = 20 },
                    new Dictionary<string, int> { ["CooldownMax"] = 4500, ["PercentCooldownReduction"] = 10, ["DamageAmount"] = 20 },
                    "skill_cd_10"),
            };

            foreach (var c in legitCases)
            {
                captureShelf.Invoke(engine, new object?[] { c.Id, "tpl_" + c.Id, c.Title, c.Before });
                object auraCtx = CreateAuraContext(c.AuraId, $"ooc_aura_1_1_{c.Id}_{c.AuraId}", c.Id, new[] { c.Id }, true);
                bool emitted = (bool)verifyWithAura.Invoke(engine, new object?[]
                {
                    c.Id, c.After, "Gameplay", "EncounterState", "Encounter", 2, 3, auraCtx, false,
                })!;
                Assert.False(emitted, $"合法入场光环卡 {c.Title} 不应落盘 error/warning 误报");
            }

            // 验证事件链已留痕 ShopPurchaseAuraExplained
            Array chainArr = (Array)getChain.Invoke(engine, null)!;
            Assert.True(chainArr.Length >= legitCases.Length, "豁免的合法光环跳变仍应留痕于环形事件链");

            // 3. 显式开启 emitAuraExplainedInfo=true 时应以 severity="info" 落盘并携带 auraExplained=true
            captureShelf.Invoke(engine, new object?[]
            {
                "itm_shoe_info", "tpl_shoe", "Shoe Blade",
                new Dictionary<string, int> { ["DamageAmount"] = 25 },
            });
            object infoAuraCtx = CreateAuraContext("2", "ooc_aura_1_1_itm_shoe_info_2", "itm_shoe_info", new[] { "itm_shoe_info" }, true);
            Assert.True((bool)verifyWithAura.Invoke(engine, new object?[]
            {
                "itm_shoe_info",
                new Dictionary<string, int> { ["DamageAmount"] = 25, ["CritChance"] = 100 },
                "Gameplay", "EncounterState", "Encounter", 2, 3, infoAuraCtx, true,
            })!);

            // 4. 反例 1：无任何 EffectAuraExecuted（auraContext == null）-> 必须仍报 error
            captureShelf.Invoke(engine, new object?[]
            {
                "itm_bug_no_aura", "tpl_bug_1", "无光环分裂匕首",
                new Dictionary<string, int> { ["DamageAmount"] = 8, ["BurnApplyAmount"] = 0 },
            });
            Assert.True((bool)verifyWithAura.Invoke(engine, new object?[]
            {
                "itm_bug_no_aura",
                new Dictionary<string, int> { ["DamageAmount"] = 8, ["BurnApplyAmount"] = 4 },
                "Gameplay", "EncounterState", "Encounter", 2, 3, null, false,
            })!);

            // 5. 反例 2：同包仅有纯合成 UI 刷新（OutOfCombatAuraSync）而非真实规则光环 -> 绝不可豁免，必须报 error
            object synthCtx = CreateAuraContext("OutOfCombatAuraSync", "ooc_sync_2_3", null, new[] { "itm_bug_synth" }, true);
            Assert.False((bool)ctxType.GetProperty("HasAuraExecuted")!.GetValue(synthCtx)!);
            Assert.Equal(1, (int)ctxType.GetProperty("SyntheticRefreshEventCount")!.GetValue(synthCtx)!);
            captureShelf.Invoke(engine, new object?[]
            {
                "itm_bug_synth", "tpl_bug_2", "仅合成刷新附魔卡",
                new Dictionary<string, int> { ["DamageAmount"] = 10, ["BurnApplyAmount"] = 0 },
            });
            Assert.True((bool)verifyWithAura.Invoke(engine, new object?[]
            {
                "itm_bug_synth",
                new Dictionary<string, int> { ["DamageAmount"] = 10, ["BurnApplyAmount"] = 6 },
                "Gameplay", "EncounterState", "Encounter", 2, 3, synthCtx, false,
            })!);

            // 6. 反例 3：同包真实光环只作用于他卡（other_card_99）而非本购入卡 -> 绝不可豁免，必须报 error
            object otherCardAuraCtx = CreateAuraContext("aura_other", "ooc_aura_2_3_other", "src_1", new[] { "other_card_99" }, true);
            captureShelf.Invoke(engine, new object?[]
            {
                "itm_bug_other", "tpl_bug_3", "未命中光环目标卡",
                new Dictionary<string, int> { ["PoisonApplyAmount"] = 0 },
            });
            Assert.True((bool)verifyWithAura.Invoke(engine, new object?[]
            {
                "itm_bug_other",
                new Dictionary<string, int> { ["PoisonApplyAmount"] = 5 },
                "Gameplay", "EncounterState", "Encounter", 2, 3, otherCardAuraCtx, false,
            })!);

            // 7. 反例 4：同包真实光环仅以玩家为显式目标（AppliedTo 非空但无任何 EffectTargetCard）-> 不豁免单卡跳变，必须报 error
            object playerOnlyAuraCtx = CreateAuraContext("aura_player_hp", "ooc_aura_2_3_p1", "itm_bug_player_only", Array.Empty<string>(), true);
            captureShelf.Invoke(engine, new object?[]
            {
                "itm_bug_player_only", "tpl_bug_4", "仅玩家目标光环卡",
                new Dictionary<string, int> { ["ShieldApplyAmount"] = 0 },
            });
            Assert.True((bool)verifyWithAura.Invoke(engine, new object?[]
            {
                "itm_bug_player_only",
                new Dictionary<string, int> { ["ShieldApplyAmount"] = 20 },
                "Gameplay", "EncounterState", "Encounter", 2, 3, playerOnlyAuraCtx, false,
            })!);

            // 8. 反例 5：CooldownMax 发生 0/null -> 5000 结构缺失跳变，即便有光环也不可豁免，必须报 error
            object cdZeroAuraCtx = CreateAuraContext("2", "ooc_aura_2_3_cd", "itm_bug_cd_zero", new[] { "itm_bug_cd_zero" }, true);
            captureShelf.Invoke(engine, new object?[]
            {
                "itm_bug_cd_zero", "tpl_bug_5", "缺失冷却卡",
                new Dictionary<string, int> { ["DamageAmount"] = 10 },
            });
            Assert.True((bool)verifyWithAura.Invoke(engine, new object?[]
            {
                "itm_bug_cd_zero",
                new Dictionary<string, int> { ["DamageAmount"] = 10, ["CooldownMax"] = 5000 },
                "Gameplay", "EncounterState", "Encounter", 2, 3, cdZeroAuraCtx, false,
            })!);

            flushM.Invoke(engine, new object[] { 2000 });
            string[] lines = File.ReadAllLines(jsonlPath);
            // 仅应落盘 1 条显式 info + 5 条真实异常 error，8 条默认豁免的合法光环卡为 0 条
            Assert.Equal(6, lines.Length);

            var severitiesByInstance = new Dictionary<string, (string Severity, bool AuraExplained)>();
            foreach (string line in lines)
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                Assert.Equal("shop_purchase_attribute_jump", root.GetProperty("category").GetString());
                JsonElement ctx = root.GetProperty("context");
                string iid = ctx.GetProperty("instanceId").GetString()!;
                severitiesByInstance[iid] = (
                    root.GetProperty("severity").GetString()!,
                    ctx.GetProperty("auraExplained").GetBoolean());
            }

            Assert.Equal(("info", true), severitiesByInstance["itm_shoe_info"]);
            Assert.Equal(("error", false), severitiesByInstance["itm_bug_no_aura"]);
            Assert.Equal(("error", false), severitiesByInstance["itm_bug_synth"]);
            Assert.Equal(("error", false), severitiesByInstance["itm_bug_other"]);
            Assert.Equal(("error", false), severitiesByInstance["itm_bug_player_only"]);
            Assert.Equal(("error", false), severitiesByInstance["itm_bug_cd_zero"]);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void P3_02_RevealSkipDebouncer_SkipsPendingRemovalAndDebouncesUnder2Seconds_WhileAlertingSustainedAndSpawned()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wave5c_p3_02_" + Guid.NewGuid().ToString("N"));
        string jsonlPath = Path.Combine(tempDir, "logs", "bare_client_audit.jsonl");
        try
        {
            Type engineType = GetTypeRequired("BazaarLauncher.Core.BareClientAuditEngine");
            Type debouncerType = GetTypeRequired("BazaarLauncher.Core.BareAuditRevealSkipDebouncer");

            // 1. 直接验证去抖状态机的 HasCardsPendingRemoval 与 <2.0s / 跨段不累加 / >=2.0s / spawned=true 规则
            object debouncer = Activator.CreateInstance(debouncerType, new object[] { 2000L, 1000L })!;
            MethodInfo evalM = debouncerType.GetMethod("Evaluate", BindingFlags.Public | BindingFlags.Instance)!;

            // 1a. HasCardsPendingRemoval == true 时，即便 dealt=true 或 spawned=true 均静默抑制（对齐 BoardManager.cs:1306）
            object dPendDealt = evalM.Invoke(debouncer, new object?[] { 1000L, true, true, false, false, false, true, "EncounterState", "Encounter" })!;
            Assert.False((bool)dPendDealt.GetType().GetField("ShouldEmit")!.GetValue(dPendDealt)!);
            Assert.Equal("pending_removal", (string)dPendDealt.GetType().GetField("SuppressReason")!.GetValue(dPendDealt)!);

            object dPendSpawn = evalM.Invoke(debouncer, new object?[] { 1050L, true, false, true, false, false, true, "ChoiceState", "Choice" })!;
            Assert.False((bool)dPendSpawn.GetType().GetField("ShouldEmit")!.GetValue(dPendSpawn)!);

            // 1b. 短于 2.0s 的正常 1.5s 多卡异步 SpawnCard 加载窗（dealt=true, spawned=false）全部去抖抑制
            long[] transientTicks = { 2000L, 2400L, 2900L, 3500L, 3900L };
            foreach (long ts in transientTicks)
            {
                object d = evalM.Invoke(debouncer, new object?[] { ts, false, true, false, false, false, true, "EncounterState", "Encounter" })!;
                Assert.False((bool)d.GetType().GetField("ShouldEmit")!.GetValue(d)!, $"瞬态加载窗 {ts - 2000L}ms < 2000ms 不应误报");
            }

            // 翻牌恢复正常（hasCardsToReveal=false）后立即重置窗口
            object dClear = evalM.Invoke(debouncer, new object?[] { 4000L, false, false, false, false, false, false, "EncounterState", "Encounter" })!;
            Assert.False((bool)dClear.GetType().GetField("ShouldEmit")!.GetValue(dClear)!);

            // 1c. 两段各自 1.4s 的正常翻牌窗间隔 1.2s（> 1.0s 最大连续间隔）绝不跨店累加触发
            Assert.False((bool)GetField(evalM.Invoke(debouncer, new object?[] { 10_000L, false, true, false, false, false, true, "EncounterState", "Encounter" })!, "ShouldEmit"));
            Assert.False((bool)GetField(evalM.Invoke(debouncer, new object?[] { 10_700L, false, true, false, false, false, true, "EncounterState", "Encounter" })!, "ShouldEmit"));
            Assert.False((bool)GetField(evalM.Invoke(debouncer, new object?[] { 11_400L, false, true, false, false, false, true, "EncounterState", "Encounter" })!, "ShouldEmit"));
            // 间隔 1200ms 进入新店重新计时，再经过 1400ms 仍不应触发
            Assert.False((bool)GetField(evalM.Invoke(debouncer, new object?[] { 12_600L, false, true, false, false, false, true, "EncounterState", "Encounter" })!, "ShouldEmit"));
            Assert.False((bool)GetField(evalM.Invoke(debouncer, new object?[] { 13_300L, false, true, false, false, false, true, "EncounterState", "Encounter" })!, "ShouldEmit"));
            Assert.False((bool)GetField(evalM.Invoke(debouncer, new object?[] { 14_000L, false, true, false, false, false, true, "EncounterState", "Encounter" })!, "ShouldEmit"));

            // 2. 端到端通过 BareClientAuditEngine 测试 Direct Prefix + Unity AppLogger 双链路
            long nowMs = 50_000L;
            Func<long> clock = () => nowMs;
            using var engine = (IDisposable)Activator.CreateInstance(
                engineType,
                new object?[] { true, jsonlPath, 48, 5000L, 60, 10000L, clock })!;

            MethodInfo directSkip = engineType.GetMethod("RecordDirectRevealSkip", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo resetDebounce = engineType.GetMethod("ResetRevealSkipDebounce", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo recordUnityLog = engineType.GetMethod("RecordUnityLog", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo flushM = engineType.GetMethod("Flush", BindingFlags.Public | BindingFlags.Instance)!;

            // 2a. HasCardsPendingRemoval=true -> 绝不落盘
            Assert.False((bool)directSkip.Invoke(engine, new object?[] { true, true, false, false, false, true, "Gameplay", "EncounterState", "Encounter", 1, 1 })!);

            // 2b. 1.5s 正常多卡加载窗（Direct）-> 抑制并随后清空
            for (int step = 0; step < 4; step++)
            {
                Assert.False((bool)directSkip.Invoke(engine, new object?[] { false, true, false, false, false, true, "Gameplay", "EncounterState", "Encounter", 1, 1 })!);
                nowMs += 500;
            }
            resetDebounce.Invoke(engine, null);

            // 2c. 持续超过 2.0s 的真实卡滞（Direct）-> 第 2.1s 必须上报 critical
            nowMs = 60_000L;
            Assert.False((bool)directSkip.Invoke(engine, new object?[] { false, true, false, false, false, true, "Gameplay", "EncounterState", "Encounter", 1, 2 })!);
            nowMs = 60_700L;
            Assert.False((bool)directSkip.Invoke(engine, new object?[] { false, true, false, false, false, true, "Gameplay", "EncounterState", "Encounter", 1, 2 })!);
            nowMs = 61_400L;
            Assert.False((bool)directSkip.Invoke(engine, new object?[] { false, true, false, false, false, true, "Gameplay", "EncounterState", "Encounter", 1, 2 })!);
            nowMs = 62_100L;
            Assert.True((bool)directSkip.Invoke(engine, new object?[] { false, true, false, false, false, true, "Gameplay", "EncounterState", "Encounter", 1, 2 })!);

            // 2d. spawned=true（无 pending removal）-> 零延迟立即上报 critical
            nowMs = 70_000L;
            Assert.True((bool)directSkip.Invoke(engine, new object?[] { false, true, true, false, false, true, "Gameplay", "ChoiceState", "Choice", 1, 3 })!);

            // 2e. Unity AppLogger.LogWarning 链路：带毫秒时间戳的 dealt=True, spawned=False 短于 2.0s 去抖，超过 2.0s 上报且同窗时间戳剥离去重
            nowMs = 80_000L;
            Assert.False((bool)recordUnityLog.Invoke(engine, new object?[]
            {
                "Warning",
                "[16:43:49.452] [BoardManager] PlayRevealAnimations skipped while loading/revealing with queued board content (dealt=True, spawned=False, collectibles=False, revealing=False).",
                "", "Gameplay", "EncounterState", "Encounter", 2, 1,
            })!);
            nowMs = 80_800L;
            Assert.False((bool)recordUnityLog.Invoke(engine, new object?[]
            {
                "Warning",
                "[16:43:50.252] [BoardManager] PlayRevealAnimations skipped while loading/revealing with queued board content (dealt=True, spawned=False, collectibles=False, revealing=False).",
                "", "Gameplay", "EncounterState", "Encounter", 2, 1,
            })!);
            nowMs = 81_500L;
            Assert.False((bool)recordUnityLog.Invoke(engine, new object?[]
            {
                "Warning",
                "[16:43:50.952] [BoardManager] PlayRevealAnimations skipped while loading/revealing with queued board content (dealt=True, spawned=False, collectibles=False, revealing=False).",
                "", "Gameplay", "EncounterState", "Encounter", 2, 1,
            })!);
            // 超过 2.0s（82_100ms）触发 1 条，随后 82_150ms 另一毫秒时间戳必须被速率去重拦截
            nowMs = 82_100L;
            Assert.True((bool)recordUnityLog.Invoke(engine, new object?[]
            {
                "Warning",
                "[16:43:51.552] [BoardManager] PlayRevealAnimations skipped while loading/revealing with queued board content (dealt=True, spawned=False, collectibles=False, revealing=False).",
                "", "Gameplay", "EncounterState", "Encounter", 2, 1,
            })!);
            nowMs = 82_150L;
            Assert.False((bool)recordUnityLog.Invoke(engine, new object?[]
            {
                "Warning",
                "[16:43:51.602] [BoardManager] PlayRevealAnimations skipped while loading/revealing with queued board content (dealt=True, spawned=False, collectibles=False, revealing=False).",
                "", "Gameplay", "EncounterState", "Encounter", 2, 1,
            })!);

            // 2f. Unity AppLogger.LogWarning 中 spawned=True 仍零延迟立即触发
            nowMs = 90_000L;
            Assert.True((bool)recordUnityLog.Invoke(engine, new object?[]
            {
                "Warning",
                "[16:44:00.100] [BoardManager] PlayRevealAnimations skipped while loading/revealing with queued board content (dealt=False, spawned=True, collectibles=False, revealing=False).",
                "", "Gameplay", "ChoiceState", "Choice", 2, 2,
            })!);

            flushM.Invoke(engine, new object[] { 2000 });
            string[] lines = File.ReadAllLines(jsonlPath);
            // 精确应为 4 条：Direct 超 2.0s + Direct spawned=true + Log 超 2.0s + Log spawned=true
            Assert.Equal(4, lines.Length);

            using (JsonDocument firstDoc = JsonDocument.Parse(lines[0]))
            {
                Assert.Equal("protocol_reveal_skipped", firstDoc.RootElement.GetProperty("category").GetString());
                Assert.Equal("critical", firstDoc.RootElement.GetProperty("severity").GetString());
                JsonElement ctx = firstDoc.RootElement.GetProperty("context");
                Assert.False(ctx.GetProperty("hasCardsPendingRemoval").GetBoolean());
                Assert.True(ctx.GetProperty("elapsedMs").GetInt64() >= 2000L);
                Assert.True(ctx.GetProperty("debouncedTransientCount").GetInt32() >= 3);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void P3_03_SseWinsock995Cancellation_FilteredInStartRunWindow_WhileBusinessExceptionsPreserved()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wave5c_p3_03_" + Guid.NewGuid().ToString("N"));
        string jsonlPath = Path.Combine(tempDir, "logs", "bare_client_audit.jsonl");
        try
        {
            Type classifierType = GetTypeRequired("BazaarLauncher.Core.BareAuditProtocolSignalClassifier");
            Type engineType = GetTypeRequired("BazaarLauncher.Core.BareClientAuditEngine");

            MethodInfo isSseCancelM = classifierType.GetMethod("IsExpectedSseCancellation", BindingFlags.Public | BindingFlags.Static)!;
            object classifier = Activator.CreateInstance(classifierType)!;
            MethodInfo classifyCtxM = classifierType.GetMethod("ClassifyUnityLogWithContext", BindingFlags.Public | BindingFlags.Instance)!;

            // 1. 分类器精确验证：实机中文 Windows Winsock 995、英文 Winsock 995、NativeErrorCode 995、SSE 读流 OperationCanceled 均识别为预期噪音
            var expectedNoiseCases = new (string LogType, string Msg, string Stack, string Scene, string AppState, string RunState)[]
            {
                // 实机 17/17 样本原文
                ("Exception",
                    "SocketException: 由于线程退出或应用程序请求，已中止 I/O 操作。\r\n",
                    "Rethrow as IOException: Unable to read data from the transport connection: 由于线程退出或应用程序请求，已中止 I/O 操作。\n",
                    "Gameplay", "StartRunAppState", "Choice"),
                // 英文 Windows Winsock 995
                ("Exception",
                    "SocketException: The I/O operation has been aborted because of either a thread exit or an application request.",
                    "Rethrow as IOException: Unable to read data from the transport connection: The I/O operation has been aborted because of either a thread exit or an application request.",
                    "Gameplay", "StartRunAppState", "Choice"),
                // NativeErrorCode 995 + 纯 BCL 底层网络帧（无业务帧）
                ("Exception",
                    "System.Net.Sockets.SocketException (NativeErrorCode=995): WSA_OPERATION_ABORTED",
                    "  at System.Net.Sockets.NetworkStream.Read(Byte[] buffer, Int32 offset, Int32 size)\n  at System.IO.StreamReader.ReadLineAsync()",
                    "Gameplay", "StartRunAppState", ""),
                // 官方 SSE 读流取消/释放链
                ("Exception",
                    "System.OperationCanceledException: The operation was canceled.",
                    "Networking.HttpClientHelpers+<ReadServerSentEventData>d__12.MoveNext () (at <00000000000000000000000000000000>:0)\nNetworking.TournamentAssignedLobbyService+<StreamLoopAsync>d__41.MoveNext ()",
                    "Gameplay", "StartRunAppState", "Choice"),
                ("Exception",
                    "ObjectDisposedException: Cannot access a disposed object. Object name: 'System.Net.Sockets.NetworkStream'.",
                    "Networking.HttpClientHelpers.ReadServerSentEventData ()",
                    "MainMenuScene", "MainMenuAppState", ""),
            };

            foreach (var c in expectedNoiseCases)
            {
                Assert.True((bool)isSseCancelM.Invoke(null, new object?[] { c.LogType, c.Msg, c.Stack, c.Scene, c.AppState, c.RunState })!,
                    $"应识别为预期 SSE 取消噪音: {c.Msg}");

                object? matched = classifyCtxM.Invoke(classifier, new object?[] { c.LogType, c.Msg, c.Stack, c.Scene, c.AppState, c.RunState });
                Assert.NotNull(matched);
                Assert.True((bool)matched!.GetType().GetProperty("IsExpectedNoise")!.GetValue(matched)!);
                Assert.Equal("info", (string)matched.GetType().GetProperty("Severity")!.GetValue(matched)!);
                Assert.Equal("expected_sse_cancel", (string)matched.GetType().GetProperty("SignalId")!.GetValue(matched)!);
            }

            // 2. 端到端引擎验证：预期 SSE 取消返回 false、不写 JSONL 异常、写入 trace；真实业务/网络异常仍必须落盘
            long nowMs = 90_000_000L;
            Func<long> clock = () => nowMs;
            using var engine = (IDisposable)Activator.CreateInstance(
                engineType,
                new object?[] { true, jsonlPath, 48, 5000L, 60, 10000L, clock })!;

            MethodInfo recordUnityLog = engineType.GetMethod("RecordUnityLog", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo getChain = engineType.GetMethod("GetRecentEventChain", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo flushM = engineType.GetMethod("Flush", BindingFlags.Public | BindingFlags.Instance)!;

            foreach (var c in expectedNoiseCases)
            {
                Assert.False((bool)recordUnityLog.Invoke(engine, new object?[]
                {
                    c.LogType, c.Msg, c.Stack, c.Scene, c.AppState, c.RunState, 1, 1,
                })!);
            }

            Array chainArr = (Array)getChain.Invoke(engine, null)!;
            Assert.Equal(expectedNoiseCases.Length, chainArr.Length);

            // 3. 反例保护（绝不可误杀的 6 类真实业务/网络异常）
            var mustKeepCases = new (string LogType, string Msg, string Stack, string Scene, string AppState, string RunState, string ExpectedSeverity)[]
            {
                // 反例 1：即使文案是 Winsock 995 且处于 StartRunAppState，只要包含业务 C# 栈帧就必须保留
                ("Exception",
                    "SocketException: The I/O operation has been aborted because of either a thread exit or an application request.",
                    "  at TheBazaar.GameSimHandler.ProcessEvents()\n  at TheBazaar.AppState.Update()",
                    "Gameplay", "StartRunAppState", "Choice", "critical"),
                // 反例 2：NetworkManager 真实业务命令通信报错（虽在 Networking 命名空间但非 SSE 取消链）
                ("Exception",
                    "SocketException: 由于线程退出或应用程序请求，已中止 I/O 操作。",
                    "  at Networking.NetworkManager.HandleCommandFailure(Exception ex)",
                    "Gameplay", "StartRunAppState", "Choice", "critical"),
                // 反例 3：局内活跃态（EncounterState / CombatState）发生无 SSE 栈的 SocketException 可能是真实断连，绝不误过滤
                ("Exception",
                    "SocketException: 由于线程退出或应用程序请求，已中止 I/O 操作。",
                    "Rethrow as IOException: Unable to read data from the transport connection: 由于线程退出或应用程序请求，已中止 I/O 操作。",
                    "Gameplay", "EncounterState", "Encounter", "critical"),
                // 反例 4：非 995 的真实网络错误（Connection refused）
                ("Exception",
                    "SocketException: No connection could be made because the target machine actively refused it.",
                    "",
                    "Gameplay", "StartRunAppState", "Choice", "critical"),
                // 反例 5：StartRunAppState 内的普通空引用崩溃（即便无栈）
                ("Exception",
                    "NullReferenceException: Object reference not set to an instance of an object",
                    "",
                    "Gameplay", "StartRunAppState", "Choice", "critical"),
                // 反例 6：日志毫秒时间戳恰好为 .995 的普通超时 Error 绝不能被 995 误匹配
                ("Error",
                    "[15:03:17.995] [TaskUtils] Timeout after 5s waiting for condition",
                    "",
                    "Gameplay", "StartRunAppState", "Choice", "error"),
            };

            foreach (var k in mustKeepCases)
            {
                Assert.False((bool)isSseCancelM.Invoke(null, new object?[] { k.LogType, k.Msg, k.Stack, k.Scene, k.AppState, k.RunState })!,
                    $"真实异常绝不可被 SSE 取消规则误杀: {k.Msg}");

                nowMs += 100;
                Assert.True((bool)recordUnityLog.Invoke(engine, new object?[]
                {
                    k.LogType, k.Msg, k.Stack, k.Scene, k.AppState, k.RunState, 1, 1,
                })!, $"真实异常必须落盘告警: {k.Msg}");
            }

            flushM.Invoke(engine, new object[] { 2000 });
            string[] lines = File.ReadAllLines(jsonlPath);
            Assert.Equal(mustKeepCases.Length, lines.Length);

            for (int i = 0; i < mustKeepCases.Length; i++)
            {
                using JsonDocument doc = JsonDocument.Parse(lines[i]);
                Assert.Equal("unity_error_or_exception", doc.RootElement.GetProperty("category").GetString());
                Assert.Equal(mustKeepCases[i].ExpectedSeverity, doc.RootElement.GetProperty("severity").GetString());
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Wave4C_BackwardCompatibilityAndRuntimeWiring_PreservedWithoutReflectionAmbiguity()
    {
        // 1. 严格校验 Wave4C 无参类型 GetMethod(...) 全部零 AmbiguousMatchException
        Type engineType = GetTypeRequired("BazaarLauncher.Core.BareClientAuditEngine");
        Type classifierType = GetTypeRequired("BazaarLauncher.Core.BareAuditProtocolSignalClassifier");
        Type detectorType = GetTypeRequired("BazaarLauncher.Core.ShopPurchaseAttributeDiffDetector");

        Assert.NotNull(engineType.GetMethod("RecordTrace", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(engineType.GetMethod("RecordUnityLog", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(engineType.GetMethod("CaptureShopItemBeforePurchase", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(engineType.GetMethod("VerifyShopItemAfterPurchase", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(engineType.GetMethod("RecordCardArtLoadFailure", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(engineType.GetMethod("Flush", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(classifierType.GetMethod("ClassifyUnityLog", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(detectorType.GetMethod("CompareAfterPurchase", BindingFlags.Public | BindingFlags.Instance));

        // 2. 校验 BareClientAuditRuntime.cs 已正确接线 P3-01 与 P3-02
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string runtimeSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "Core", "BareClientAuditRuntime.cs"));

        Assert.Contains("GameSimEventEffectAuraExecuted", runtimeSrc, StringComparison.Ordinal);
        Assert.Contains("EffectTargetCard", runtimeSrc, StringComparison.Ordinal);
        Assert.Contains("BareAuditPurchaseBatchState", runtimeSrc, StringComparison.Ordinal);
        Assert.Contains("VerifyShopItemAfterPurchaseWithAura", runtimeSrc, StringComparison.Ordinal);
        Assert.Contains("HasCardsPendingRemoval", runtimeSrc, StringComparison.Ordinal);
        Assert.Contains("CardRemoval", runtimeSrc, StringComparison.Ordinal);
        Assert.Contains("RecordDirectRevealSkip", runtimeSrc, StringComparison.Ordinal);
        Assert.Contains("ResetRevealSkipDebounce", runtimeSrc, StringComparison.Ordinal);
    }

    private static object GetField(object target, string fieldName)
        => target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)!.GetValue(target)!;
}
