using System.Reflection;
using System.Text.Json;
using BazaarCore.Tests.TestInfrastructure;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 4C（任务 #12）：裸协议黑匣子观测模式（StrictBareClientAuditMode）强断言单测。
/// 通过 Wave2CLauncherAssemblyFixture 直接加载真实 BazaarLauncher.dll 内的零 Unity 纯 .NET 核心组件，验证：
///   1. 配置键 StrictBareClientAuditMode 默认 false（零行为回归、零目录/文件 I/O）；
///   2. 开启后决策矩阵禁用全部 CompatAuditPatch / H-7 自愈看门狗并激活黑匣子观测器；
///   3. 环形事件链（RingBuffer）容量有界且严格保序；
///   4. 5 类异常（PlayRevealAnimations skipped 与裸协议信号、Unity Error/Exception、货架购入前后属性跳变、
///      卡图加载失败、状态/输入锁死 > 10s）全部精准落盘为合法单行 JSONL，附完整事件链与上下文快照；
///   5. 去重限流与防风暴令牌桶准确抑制重复刷屏并在窗口恢复后附带 suppressedCount。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave4C_BareClientAuditModeTests : IClassFixture<Wave2CLauncherAssemblyFixture>
{
    private readonly Wave2CLauncherAssemblyFixture _fx;

    public Wave4C_BareClientAuditModeTests(Wave2CLauncherAssemblyFixture fx)
    {
        _fx = fx;
    }

    private Type GetTypeRequired(string fullName)
        => _fx.LauncherAssembly.GetType(fullName, throwOnError: true)!;

    [Fact]
    public void StrictBareClientAuditMode_ConfigExistsAndDefaultsToFalse_WithZeroIoWhenDisabled()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string pluginSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "BazaarLauncherPlugin.cs"));
        Assert.Contains("StrictBareClientAuditMode", pluginSrc, StringComparison.Ordinal);
        Assert.Contains("\"StrictBareClientAuditMode\", false", pluginSrc, StringComparison.Ordinal);

        // 决策矩阵验证
        Type policyType = GetTypeRequired("BazaarLauncher.Core.BareClientAuditPolicy");
        MethodInfo isEnabledM = policyType.GetMethod("IsStrictAuditEnabled", BindingFlags.Public | BindingFlags.Static)!;
        MethodInfo shouldCompatM = policyType.GetMethod("ShouldInstallCompatSelfHealingPatches", BindingFlags.Public | BindingFlags.Static)!;
        MethodInfo shouldAuditM = policyType.GetMethod("ShouldInstallBareAuditObserver", BindingFlags.Public | BindingFlags.Static)!;

        Assert.False((bool)isEnabledM.Invoke(null, new object?[] { false, null })!);
        Assert.False((bool)isEnabledM.Invoke(null, new object?[] { false, "0" })!);
        Assert.True((bool)isEnabledM.Invoke(null, new object?[] { true, null })!);
        Assert.True((bool)isEnabledM.Invoke(null, new object?[] { false, "1" })!);
        Assert.True((bool)isEnabledM.Invoke(null, new object?[] { false, "true" })!);

        Assert.True((bool)shouldCompatM.Invoke(null, new object[] { false })!);
        Assert.False((bool)shouldAuditM.Invoke(null, new object[] { false })!);
        Assert.False((bool)shouldCompatM.Invoke(null, new object[] { true })!);
        Assert.True((bool)shouldAuditM.Invoke(null, new object[] { true })!);

        // 关闭状态下初始化 BareClientAuditEngine 并投递所有事件：绝不创建目录或 JSONL 文件
        string tempDir = Path.Combine(Path.GetTempPath(), "wave4c_audit_disabled_" + Guid.NewGuid().ToString("N"));
        string jsonlPath = Path.Combine(tempDir, "logs", "bare_client_audit.jsonl");
        try
        {
            Type engineType = GetTypeRequired("BazaarLauncher.Core.BareClientAuditEngine");
            long nowMs = 1_000_000;
            Func<long> clock = () => nowMs;
            using var engine = (IDisposable)Activator.CreateInstance(
                engineType,
                new object?[] { false, jsonlPath, 48, 5000L, 40, 10000L, clock })!;

            MethodInfo recordTrace = engineType.GetMethod("RecordTrace", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo recordUnityLog = engineType.GetMethod("RecordUnityLog", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo recordArtFail = engineType.GetMethod("RecordCardArtLoadFailure", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo flushM = engineType.GetMethod("Flush", BindingFlags.Public | BindingFlags.Instance)!;

            recordTrace.Invoke(engine, new object?[] { "client_cmd", "SendSelectItem", "id=1" });
            recordUnityLog.Invoke(engine, new object?[] { "Warning", "PlayRevealAnimations skipped (spawned=True)", "", "Gameplay", "ChoiceState", "Choice", 3, 6 });
            recordUnityLog.Invoke(engine, new object?[] { "Exception", "NullReferenceException", "at BoardManager.Foo()", "Gameplay", "ChoiceState", "Choice", 3, 6 });
            recordArtFail.Invoke(engine, new object?[] { "CardTextureCache", "mod/a.png", "invalid_magic", "card_1", "Gameplay", "ChoiceState", "Choice", 3, 6 });
            flushM.Invoke(engine, new object[] { 1000 });

            Assert.False(File.Exists(jsonlPath), "StrictBareClientAuditMode=false 时严禁创建 bare_client_audit.jsonl");
            Assert.False(Directory.Exists(tempDir), "StrictBareClientAuditMode=false 时严禁产生额外目录 I/O");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RingBuffer_RetainsBoundedNewestOrderUnderOverflow()
    {
        Type openType = GetTypeRequired("BazaarLauncher.Core.BareAuditRingBuffer`1");
        Type closedType = openType.MakeGenericType(typeof(string));
        object ring = Activator.CreateInstance(closedType, new object[] { 32 })!;

        MethodInfo addM = closedType.GetMethod("Add", BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo snapM = closedType.GetMethod("Snapshot", BindingFlags.Public | BindingFlags.Instance)!;
        PropertyInfo capProp = closedType.GetProperty("Capacity", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.Equal(32, (int)capProp.GetValue(ring)!);

        for (int i = 1; i <= 75; i++)
        {
            addM.Invoke(ring, new object[] { $"evt_{i}" });
        }

        string[] snap = (string[])snapM.Invoke(ring, null)!;
        Assert.Equal(32, snap.Length);
        Assert.Equal("evt_44", snap[0]);
        Assert.Equal("evt_75", snap[^1]);
    }

    [Fact]
    public void AuditEngine_WhenEnabled_CapturesAllFiveAnomalyCategoriesAsValidJsonlWithEventChain()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wave4c_audit_enabled_" + Guid.NewGuid().ToString("N"));
        string jsonlPath = Path.Combine(tempDir, "logs", "bare_client_audit.jsonl");
        try
        {
            Type engineType = GetTypeRequired("BazaarLauncher.Core.BareClientAuditEngine");
            long nowMs = 5_000_000;
            Func<long> clock = () => nowMs;
            using var engine = (IDisposable)Activator.CreateInstance(
                engineType,
                new object?[] { true, jsonlPath, 48, 5000L, 60, 10000L, clock })!;

            MethodInfo recordTrace = engineType.GetMethod("RecordTrace", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo recordUnityLog = engineType.GetMethod("RecordUnityLog", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo captureShelf = engineType.GetMethod("CaptureShopItemBeforePurchase", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo verifyPurchase = engineType.GetMethod("VerifyShopItemAfterPurchase", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo recordArtFail = engineType.GetMethod("RecordCardArtLoadFailure", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo sampleDeadlock = engineType.GetMethod(
                "SampleDeadlockState",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[]
                {
                    typeof(bool), typeof(string), typeof(string), typeof(string), typeof(int), typeof(int),
                    typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)
                },
                null)!;
            MethodInfo flushM = engineType.GetMethod("Flush", BindingFlags.Public | BindingFlags.Instance)!;

            // 预埋最近命令与服务端事件链
            recordTrace.Invoke(engine, new object?[] { "client_cmd", "SelectEncounter", "enc=chocolate_event" });
            recordTrace.Invoke(engine, new object?[] { "server_msg", "NetMessageGameSim", "CardSpawned+StateTransitioned(Choice,H6)" });
            recordTrace.Invoke(engine, new object?[] { "client_cmd", "SendSelectItem", "card=item_ench_01" });

            // 1. PlayRevealAnimations skipped（以及普通 Log 不触发）
            Assert.False((bool)recordUnityLog.Invoke(engine, new object?[] { "Log", "Normal info log", "", "Gameplay", "ChoiceState", "Choice", 3, 5 })!);
            Assert.True((bool)recordUnityLog.Invoke(engine, new object?[] {
                "Warning",
                "PlayRevealAnimations skipped while loading/revealing with queued board content (dealt=False, spawned=True, collectibles=False, revealing=False).",
                "BoardManager.PlayRevealAnimations()",
                "Gameplay", "ChoiceState", "Choice", 3, 6
            })!);

            // 2. Unity Exception / Error（同时验证自发 [BareClientAudit] 绝不递归）
            Assert.False((bool)recordUnityLog.Invoke(engine, new object?[] { "Error", "[BareClientAudit] internal write warn", "", "Gameplay", "ChoiceState", "Choice", 3, 6 })!);
            Assert.True((bool)recordUnityLog.Invoke(engine, new object?[] {
                "Exception",
                "NullReferenceException: Object reference not set to an instance of an object",
                "at TheBazaar.GameSimHandler.ProcessEvents()\nat TheBazaar.AppState.Update()",
                "Gameplay", "ChoiceState", "Choice", 3, 6
            })!);

            // 3. 货架购入前后属性跳变（合法卡无跳变不报；附魔衍生属性 0->4 必报且忽略 BuyPrice/SellPrice）
            captureShelf.Invoke(engine, new object?[] {
                "inst_ok", "tpl_ok", "普通铁剑",
                new Dictionary<string, int> { ["DamageAmount"] = 12, ["BuyPrice"] = 4 }
            });
            Assert.False((bool)verifyPurchase.Invoke(engine, new object?[] {
                "inst_ok",
                new Dictionary<string, int> { ["DamageAmount"] = 12, ["SellPrice"] = 2 },
                "Gameplay", "EncounterState", "Encounter", 3, 4
            })!);

            captureShelf.Invoke(engine, new object?[] {
                "inst_jump", "tpl_fiery", "炽热匕首",
                new Dictionary<string, int> { ["DamageAmount"] = 8, ["BurnApplyAmount"] = 0, ["BuyPrice"] = 6 }
            });
            Assert.True((bool)verifyPurchase.Invoke(engine, new object?[] {
                "inst_jump",
                new Dictionary<string, int> { ["DamageAmount"] = 8, ["BurnApplyAmount"] = 4, ["SellPrice"] = 3 },
                "Gameplay", "EncounterState", "Encounter", 3, 4
            })!);

            // 4. 卡图加载失败
            Assert.True((bool)recordArtFail.Invoke(engine, new object?[] {
                "CardTextureCache",
                "mods/sample/assets/missing.png",
                "file_missing_or_empty",
                "custom_starport_blade",
                "Gameplay", "EncounterState", "Encounter", 3, 4
            })!);

            // 5. 状态/输入锁死超过 10s（9.5s 内不触发，10.2s 触发）
            Assert.False((bool)sampleDeadlock.Invoke(engine, new object?[] {
                true, "Gameplay", "ChoiceState", "Choice", 3, 6,
                true, false, false, false, false, true, false, false, false
            })!);
            nowMs += 9_500;
            Assert.False((bool)sampleDeadlock.Invoke(engine, new object?[] {
                true, "Gameplay", "ChoiceState", "Choice", 3, 6,
                true, false, false, false, false, true, false, false, false
            })!);
            nowMs += 700; // 累计 10,200ms > 10s
            Assert.True((bool)sampleDeadlock.Invoke(engine, new object?[] {
                true, "Gameplay", "ChoiceState", "Choice", 3, 6,
                true, false, false, false, false, true, false, false, false
            })!);

            flushM.Invoke(engine, new object[] { 2000 });

            Assert.True(File.Exists(jsonlPath), "开启黑匣子模式后必须生成 logs/bare_client_audit.jsonl");
            string[] lines = File.ReadAllLines(jsonlPath);
            Assert.Equal(5, lines.Length);

            var categories = new List<string>();
            foreach (string line in lines)
            {
                Assert.False(string.IsNullOrWhiteSpace(line));
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                string cat = root.GetProperty("category").GetString()!;
                categories.Add(cat);

                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("timestampUtc").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("summary").GetString()));
                Assert.Equal("Gameplay", root.GetProperty("scene").GetString());
                Assert.True(root.GetProperty("recentEventChain").GetArrayLength() >= 3, "每条记录必须附带最近命令/网络事件链");
                Assert.Equal(JsonValueKind.Object, root.GetProperty("context").ValueKind);

                if (cat == "shop_purchase_attribute_jump")
                {
                    JsonElement ctx = root.GetProperty("context");
                    Assert.Equal("inst_jump", ctx.GetProperty("instanceId").GetString());
                    Assert.True(ctx.GetProperty("hasZeroToNonZeroJump").GetBoolean());
                    string diffsJson = ctx.GetProperty("diffs").GetRawText();
                    Assert.Contains("BurnApplyAmount", diffsJson, StringComparison.Ordinal);
                    Assert.DoesNotContain("BuyPrice", diffsJson, StringComparison.Ordinal);
                    Assert.DoesNotContain("SellPrice", diffsJson, StringComparison.Ordinal);
                }
                else if (cat == "state_or_input_deadlock")
                {
                    JsonElement ctx = root.GetProperty("context");
                    Assert.True(ctx.GetProperty("stalledMs").GetInt64() >= 10_000);
                    string reasonsJson = ctx.GetProperty("lockedReasons").GetRawText();
                    Assert.Contains("BlockInput", reasonsJson, StringComparison.Ordinal);
                    Assert.Contains("HasCardsToReveal", reasonsJson, StringComparison.Ordinal);
                }
                else if (cat == "card_art_load_failure")
                {
                    JsonElement ctx = root.GetProperty("context");
                    Assert.Equal("CardTextureCache", ctx.GetProperty("source").GetString());
                    Assert.Contains("missing.png", ctx.GetProperty("assetPath").GetString()!, StringComparison.Ordinal);
                }
            }

            Assert.Contains("protocol_reveal_skipped", categories);
            Assert.Contains("unity_error_or_exception", categories);
            Assert.Contains("shop_purchase_attribute_jump", categories);
            Assert.Contains("card_art_load_failure", categories);
            Assert.Contains("state_or_input_deadlock", categories);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RateLimiter_DeduplicatesSpamAndEnforcesGlobalStormCap()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wave4c_audit_throttle_" + Guid.NewGuid().ToString("N"));
        string jsonlPath = Path.Combine(tempDir, "logs", "bare_client_audit.jsonl");
        try
        {
            Type engineType = GetTypeRequired("BazaarLauncher.Core.BareClientAuditEngine");
            long nowMs = 9_000_000;
            Func<long> clock = () => nowMs;
            // 同签名冷却 5000ms，全局每 10s 窗口最多 10 条
            using var engine = (IDisposable)Activator.CreateInstance(
                engineType,
                new object?[] { true, jsonlPath, 32, 5000L, 10, 10000L, clock })!;

            MethodInfo recordUnityLog = engineType.GetMethod("RecordUnityLog", BindingFlags.Public | BindingFlags.Instance)!;
            MethodInfo flushM = engineType.GetMethod("Flush", BindingFlags.Public | BindingFlags.Instance)!;

            // 1. 同一异常在 1 秒内狂刷 50 次 -> 仅放行第 1 次
            int allowedSame = 0;
            for (int i = 0; i < 50; i++)
            {
                nowMs += 10;
                if ((bool)recordUnityLog.Invoke(engine, new object?[] { "Exception", "NullReferenceException: Update loop", "at Foo.Update()", "Gameplay", "ChoiceState", "Choice", 1, 1 })!)
                {
                    allowedSame++;
                }
            }
            Assert.Equal(1, allowedSame);

            // 推进超过冷却窗口 5000ms -> 再次放行并携带 suppressedCount == 49
            nowMs += 6000;
            Assert.True((bool)recordUnityLog.Invoke(engine, new object?[] { "Exception", "NullReferenceException: Update loop", "at Foo.Update()", "Gameplay", "ChoiceState", "Choice", 1, 1 })!);

            // 2. 换 30 个完全不同的错误签名模拟不同异常风暴 -> 受全局窗口上限 10 限制（加上刚才同一 10s 窗口内已用的 2 条，总共不超过 10 条）
            int allowedDistinct = 0;
            for (int i = 0; i < 30; i++)
            {
                if ((bool)recordUnityLog.Invoke(engine, new object?[] { "Error", $"DistinctStormError_{i}", $"stack_{i}", "Gameplay", "ChoiceState", "Choice", 1, 1 })!)
                {
                    allowedDistinct++;
                }
            }
            Assert.True(allowedDistinct <= 10, $"全局防风暴限流应拦截超出配额的异常（实际放行={allowedDistinct}）");

            flushM.Invoke(engine, new object[] { 2000 });
            string[] lines = File.ReadAllLines(jsonlPath);
            Assert.True(lines.Length >= 2 && lines.Length <= 10);

            using JsonDocument secondDoc = JsonDocument.Parse(lines[1]);
            Assert.Equal(49, secondDoc.RootElement.GetProperty("suppressedCount").GetInt32());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DeadlockDetector_ResetsCleanlyWhenUnlockedOrLeftRun()
    {
        Type detectorType = GetTypeRequired("BazaarLauncher.Core.BareAuditDeadlockDetector");
        object detector = Activator.CreateInstance(detectorType, new object[] { 10_000L, 15_000L })!;
        MethodInfo evalM = detectorType.GetMethod(
            "Evaluate",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[]
            {
                typeof(long), typeof(bool), typeof(string), typeof(string), typeof(string),
                typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)
            },
            null)!;

        // 锁定 8 秒后解锁，再锁定 8 秒 -> 不应累加触发
        Assert.Null(evalM.Invoke(detector, new object?[] { 0L, true, "Gameplay", "ChoiceState", "Choice", true, false, false, false, false, false, false, false, false }));
        Assert.Null(evalM.Invoke(detector, new object?[] { 8_000L, true, "Gameplay", "ChoiceState", "Choice", true, false, false, false, false, false, false, false, false }));
        // 解锁
        Assert.Null(evalM.Invoke(detector, new object?[] { 8_500L, true, "Gameplay", "ChoiceState", "Choice", false, false, false, false, false, false, false, false, false }));
        // 再次锁定 8 秒 -> 仍不应触发
        Assert.Null(evalM.Invoke(detector, new object?[] { 9_000L, true, "Gameplay", "ChoiceState", "Choice", false, true, false, false, false, false, false, false, false }));
        Assert.Null(evalM.Invoke(detector, new object?[] { 17_000L, true, "Gameplay", "ChoiceState", "Choice", false, true, false, false, false, false, false, false, false }));
        // 持续到 19_500ms（已持续 10_500ms）-> 必须触发
        object? hit = evalM.Invoke(detector, new object?[] { 19_500L, true, "Gameplay", "ChoiceState", "Choice", false, true, false, false, false, false, false, false, false });
        Assert.NotNull(hit);

        // 切回主大厅（isInRun=false）必须清零，不误报
        Assert.Null(evalM.Invoke(detector, new object?[] { 40_000L, false, "MainMenuScene", "MainMenu", "", true, true, true, true, true, true, true, true, true }));
    }

    [Fact]
    public void StrictAuditWiring_CompatAndCombatAndArtCachesHookedCorrectly()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string compatSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "CompatAuditPatch.cs"));
        string combatSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "CombatPatch.cs"));
        string texSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "Modules", "CardTextureCache.cs"));
        string visSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "Modules", "CustomContentVisualModule.cs"));
        string pluginSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "BazaarLauncherPlugin.cs"));

        Assert.Contains("IsStrictBareClientAuditModeActive", compatSrc, StringComparison.Ordinal);
        Assert.Contains("IsStrictBareClientAuditModeActive", combatSrc, StringComparison.Ordinal);
        Assert.Contains("NotifyCardArtLoadFailure", texSrc, StringComparison.Ordinal);
        Assert.Contains("NotifyCardArtLoadFailure", visSrc, StringComparison.Ordinal);
        Assert.Contains("DiagBareClientAuditModule", pluginSrc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtocolInvariantChecker_DetectsAllSevenTrackAAlignedBareClientSignals()
    {
        Type inputType = GetTypeRequired("BazaarLauncher.Core.BareAuditGameSimBatchInput");
        Type checkerType = GetTypeRequired("BazaarLauncher.Core.BareAuditGameSimInvariantChecker");
        MethodInfo inspectM = checkerType.GetMethod("InspectBatch", BindingFlags.Public | BindingFlags.Static)!;
        MethodInfo hour6LockM = checkerType.GetMethod("IsHour6WaitNewDayRevealLock", BindingFlags.Public | BindingFlags.Static)!;

        // 1. 构造同时违背 6 条同包协议不变量的恶意裸包，验证全部精确命中
        object badBatch = Activator.CreateInstance(inputType)!;
        inputType.GetProperty("MessageId")!.SetValue(badBatch, "msg_bad_01");
        inputType.GetProperty("PreviousRunState")!.SetValue(badBatch, "Choice");
        inputType.GetProperty("ResultingRunState")!.SetValue(badBatch, "Choice");
        inputType.GetProperty("HasLeftChoiceInBatch")!.SetValue(badBatch, false);
        inputType.GetProperty("HasCardDealt")!.SetValue(badBatch, true);
        inputType.GetProperty("ExistingPlayerHandMask")!.SetValue(badBatch, 0b0000_0011_1111); // 0..5
        inputType.GetProperty("NewlyUnlockedPlayerHandMask")!.SetValue(badBatch, 0b0000_1111_1111); // 新解锁 6,7
        ((List<string>)inputType.GetProperty("SpawnedCardInstanceIds")!.GetValue(badBatch)!).Add("card_spawn_1");
        ((List<int>)inputType.GetProperty("SpawnedPlayerHandSockets")!.GetValue(badBatch)!).Add(6);
        ((List<string>)inputType.GetProperty("EquippedSkillInstanceIds")!.GetValue(badBatch)!).Add("orphan_skill_99");
        ((List<string>)inputType.GetProperty("TransformReferencedCardIds")!.GetValue(badBatch)!).Add("missing_trans_key");
        ((List<string>)inputType.GetProperty("MovedCardInstanceIds")!.GetValue(badBatch)!).Add("card_spawn_1");

        var listObj = (System.Collections.IList)inspectM.Invoke(null, new[] { badBatch })!;
        var signalIds = new List<string>();
        foreach (object item in listObj)
        {
            signalIds.Add((string)item.GetType().GetProperty("SignalId")!.GetValue(item)!);
        }
        Assert.Contains("same_batch_spawned_and_dealt_in_choice", signalIds);
        Assert.Contains("sockets_unlocked_after_card_spawned_conflict", signalIds);
        Assert.Contains("orphan_player_skill_equipped", signalIds);
        Assert.Contains("transform_missing_sim_card_key", signalIds);
        Assert.Contains("card_moved_same_batch_as_spawned", signalIds);

        // 2. LevelUp 处于 Hour == HoursInADay
        object lvlBatch = Activator.CreateInstance(inputType)!;
        inputType.GetProperty("ResultingRunState")!.SetValue(lvlBatch, "LevelUp");
        inputType.GetProperty("ResultingHour")!.SetValue(lvlBatch, 6);
        inputType.GetProperty("HoursInADay")!.SetValue(lvlBatch, 6);
        var lvlList = (System.Collections.IList)inspectM.Invoke(null, new[] { lvlBatch })!;
        Assert.Single(lvlList);
        Assert.Equal("levelup_at_pvp_hour_clock_lock_risk", lvlList[0]!.GetType().GetProperty("SignalId")!.GetValue(lvlList[0]));

        // 3. 合法干净包（先离 Choice -> Encounter，无孤儿技能，Key 齐全，LevelUp 在 Hour 5）-> 0 违例
        object cleanBatch = Activator.CreateInstance(inputType)!;
        inputType.GetProperty("PreviousRunState")!.SetValue(cleanBatch, "Choice");
        inputType.GetProperty("ResultingRunState")!.SetValue(cleanBatch, "Encounter");
        inputType.GetProperty("HasLeftChoiceInBatch")!.SetValue(cleanBatch, true);
        inputType.GetProperty("HasCardDealt")!.SetValue(cleanBatch, true);
        inputType.GetProperty("ResultingHour")!.SetValue(cleanBatch, 5);
        ((List<string>)inputType.GetProperty("SpawnedCardInstanceIds")!.GetValue(cleanBatch)!).Add("card_ok");
        ((HashSet<string>)inputType.GetProperty("ExistingEntityIds")!.GetValue(cleanBatch)!).Add("skill_ok");
        ((List<string>)inputType.GetProperty("EquippedSkillInstanceIds")!.GetValue(cleanBatch)!).Add("skill_ok");
        ((HashSet<string>)inputType.GetProperty("SimUpdateCardKeys")!.GetValue(cleanBatch)!).Add("trans_ok");
        ((List<string>)inputType.GetProperty("TransformReferencedCardIds")!.GetValue(cleanBatch)!).Add("trans_ok");
        var cleanList = (System.Collections.IList)inspectM.Invoke(null, new[] { cleanBatch })!;
        Assert.Empty(cleanList);

        // 4. Hour 6 _waitForNewDayTransition 死锁判定
        Assert.True((bool)hour6LockM.Invoke(null, new object[] { "Choice", 6, 6, true, false, true })!);
        Assert.False((bool)hour6LockM.Invoke(null, new object[] { "Choice", 6, 6, true, true, true })!); // 真正跨日切场放行
        Assert.False((bool)hour6LockM.Invoke(null, new object[] { "Choice", 5, 6, true, false, true })!);

        // 5. 新增日志信号分类验证（track-a #8 同步清单）
        Type classifierType = GetTypeRequired("BazaarLauncher.Core.BareAuditProtocolSignalClassifier");
        object classifier = Activator.CreateInstance(classifierType)!;
        MethodInfo classifyM = classifierType.GetMethod("ClassifyUnityLog", BindingFlags.Public | BindingFlags.Instance)!;

        Assert.NotNull(classifyM.Invoke(classifier, new object[] { "Warning", "Skipping board movement for card item_1", "" }));
        Assert.NotNull(classifyM.Invoke(classifier, new object[] { "Warning", "Skipping board target update for card item_2", "" }));
        Assert.NotNull(classifyM.Invoke(classifier, new object[] { "Error", "SkillPresentationManager ArgumentException duplicate key", "" }));
        Assert.NotNull(classifyM.Invoke(classifier, new object[] { "Warning", "BoardTransitionController.IsTransitioning still true", "" }));
    }
}
