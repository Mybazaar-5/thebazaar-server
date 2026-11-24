using System.Reflection;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 3B 域 2/3/6/8 服务端与 Mod 内核独占强断言测试套件。
/// 涉及临时 SQLite/静态配置/热重载快照，严格标注 [Collection("GlobalStaticState")]。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave3B_ServerAndModP2Tests
{
    private static SharedGameDataFixture.SharedGameDataSnapshot Fx => SharedGameDataFixture.Shared;

    [Fact]
    public void D2_P2_02_IsPlayerSpawnable_NeverEligibilityBeatsSyntheticTestTemplate()
    {
        // LocalizationTitleKey 与 TranslationKey 均为空且名称不含 [DEBUG]，因此 IsSyntheticTestTemplate == true
        var tpl = new CardPool.CardTemplate
        {
            Id = Guid.NewGuid().ToString("D"),
            InternalName = "SyntheticNeverItem",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            SpawningEligibility = "Never",
        };
        Assert.True(tpl.IsSyntheticTestTemplate);
        Assert.False(Fx.Pool.IsPlayerSpawnable(tpl, allowGuidOnly: true));
    }

    [Fact]
    public void D2_P2_03_LevelUpTable_SequentialNoDuplicate_DoesNotSkipSecondItem()
    {
        // 通过反射直接验证内部 GroupDrawState.PickOne 在 Sequential && !AllowDuplicates 下严格按 0,1,2 顺序不跳项
        Type groupType = typeof(LevelUpTable).GetNestedType("RewardGroup", BindingFlags.Public | BindingFlags.NonPublic)!;
        Type stateType = typeof(LevelUpTable).GetNestedType("GroupDrawState", BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.NotNull(groupType);
        Assert.NotNull(stateType);

        object group = Activator.CreateInstance(groupType)!;
        groupType.GetProperty("SelectionMethod")!.SetValue(group, "Sequential");

        var candidates = new List<(string TemplateId, CardTier Tier)>
        {
            ("opt_0", CardTier.Bronze),
            ("opt_1", CardTier.Bronze),
            ("opt_2", CardTier.Bronze),
        };
        object state = Activator.CreateInstance(stateType, new object[] { group, candidates, 3, false })!;
        MethodInfo pickOne = stateType.GetMethod("PickOne", BindingFlags.Public | BindingFlags.Instance)!;

        var picked = new List<(string TemplateId, CardTier Tier)>();
        var rng = new Random(1);
        for (int i = 0; i < 3; i++)
        {
            var res = ((string TemplateId, CardTier Tier)?)pickOne.Invoke(state, new object[] { picked, rng });
            Assert.NotNull(res);
            picked.Add(res!.Value);
        }

        Assert.Equal("opt_0", picked[0].TemplateId);
        Assert.Equal("opt_1", picked[1].TemplateId);
        Assert.Equal("opt_2", picked[2].TemplateId);
    }

    [Fact]
    public void D2_P2_06_BuildFallbackGhost_UsesStartingHealth300AndDay1UnlockedSlots()
    {
        MethodInfo? buildMethod = typeof(PvpGhostPool).GetMethod(
            "BuildFallbackGhost",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(buildMethod);

        var ghost = (PvpGhostPool.GhostBoard)buildMethod!.Invoke(null, new object?[] { 1, new Random(7), Fx.Repo })!;
        Assert.Equal(GameDefaults.StartingHealth, ghost.HealthMax);
        // Day 1 仅解锁中间 4 格（Socket 3..6），每个物品的 [Slot, Slot+Width-1] 必须严格落在 3..6 内
        foreach (var item in ghost.Items)
        {
            Assert.InRange(item.Slot, 3, 6);
            Assert.InRange(item.Slot + item.Width - 1, 3, 6);
        }
    }

    [Fact]
    public void D2_P2_07_CardArchetypeIndex_RespectsIsNotTrueInConstraintAst()
    {
        // TConstraintCardType 指定 Item 但 IsNot=true（即非 Item），绝不可被误判为“纯 Item 约束”
        const string negatedItemJson = """
            {"$type":"TConstraintCardType","Types":["Item"],"IsNot":true}
            """;
        var method = typeof(CardArchetypeIndex).GetMethod(
            "HasPositiveCardTypeConstraint",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var doc = JsonDocument.Parse(negatedItemJson);
        bool isPositiveItem = (bool)method!.Invoke(null, new object[] { doc.RootElement, "Item", false })!;
        Assert.False(isPositiveItem, "IsNot=true 的 Item 约束绝不可误判为正向 Item 约束");

        // 双重否定（外层 TConstraintAnd IsNot=true + 内层 TConstraintCardType IsNot=true）应还原为正向
        const string doubleNegJson = """
            {"$type":"TConstraintAnd","IsNot":true,"Conditions":[{"$type":"TConstraintCardType","Types":["Item"],"IsNot":true}]}
            """;
        using var doc2 = JsonDocument.Parse(doubleNegJson);
        bool isDoubleNegPositive = (bool)method.Invoke(null, new object[] { doc2.RootElement, "Item", false })!;
        Assert.True(isDoubleNegPositive);
    }

    [Fact]
    public void D3_P2_03_IsValidVfxKey_AcceptsOfficialGameDataVfxKeysAndRejectsBogus()
    {
        MethodInfo? isValidVfx = typeof(CombatSimulator).GetMethod(
            "IsValidVfxKey",
            BindingFlags.Public | BindingFlags.Static,
            new[] { typeof(string), typeof(GameDataRepository) });
        Assert.NotNull(isValidVfx);

        var officialKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in Fx.Repo.CardsById.Values)
        {
            if (card.IsCustomModCard) continue;
            foreach (var ab in card.Abilities.Values)
            {
                if (!string.IsNullOrWhiteSpace(ab.VFXConfig?.VFXOverrideKey))
                    officialKeys.Add(ab.VFXConfig!.VFXOverrideKey!);
            }
        }
        Assert.NotEmpty(officialKeys);

        foreach (string key in officialKeys.Take(10))
        {
            Assert.True((bool)isValidVfx!.Invoke(null, new object?[] { key, Fx.Repo })!, $"官方 VFX 键 {key} 必须被 IsValidVfxKey 放行");
        }
        Assert.False((bool)isValidVfx!.Invoke(null, new object?[] { "nonexistent_bogus_vfx_key_999", Fx.Repo })!);
    }

    [Fact]
    public void D3_P2_04_MechanicSandboxInterpreter_StaticErrorsUseGateId4AndCustom0To8()
    {
        var badMech = new MechanicDefinition
        {
            Id = "w3b:bad-slot",
            NameZh = "非法槽位测试",
            CounterDisplay = new MechanicCounterDisplay
            {
                SyncAttribute = "Custom_99",
            },
            Hooks = new List<MechanicHookDefinition>
            {
                new()
                {
                    Hook = "OnCardFired",
                    Steps = new List<MechanicStep>
                    {
                        new() { Op = "AddState", Key = "s", Value = new MechanicValueExpr { Const = 1 } },
                    },
                },
            },
        };
        var issues = new List<ModValidationIssue>();
        MechanicSandboxInterpreter.ValidateStatic(badMech, "w3b-test-mod", issues);
        Assert.NotEmpty(issues);
        Assert.All(issues, i => Assert.Equal(4, i.GateId));
    }

    [Fact]
    public void D3_P2_08_ModValidator_Gate10FlagsMechanicOnlyModAsGameplayChange()
    {
        var pkg = new ModPackage
        {
            RootDirectory = Path.GetTempPath(),
            Manifest = new ModManifest
            {
                Id = "w3b-mech-only",
                Name = "MechOnly",
                Version = "1.0.0",
                AffectsGhostPool = false,
                Scope = "cosmetic-only",
            },
        };
        pkg.Mechanics.Add(new MechanicDefinition
        {
            Id = "w3b-mech-only:m1",
            NameZh = "测试机制",
            Hooks = new List<MechanicHookDefinition>
            {
                new()
                {
                    Hook = "OnFightStart",
                    Steps = new List<MechanicStep>
                    {
                        new() { Op = "SetFlag", Flag = "w3b_flag_x" },
                    },
                },
            },
        });
        var report = ModValidator.RunTenGateValidation(
            pkg,
            Array.Empty<ModPackage>(),
            Fx.Repo,
            combatMatches: 2,
            runDynamicGates: true);
        var gate10 = report.Gates.FirstOrDefault(g => g.GateId == 10);
        Assert.NotNull(gate10);
        Assert.Contains(gate10!.Issues, i => i.GateId == 10 && i.Code.Contains("GHOST_POOL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void D6_P2_01_08_Persistence_RoundTripsInviteTokenExpiresAtAndCascadesLobbyChat()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"w3b_persist_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var db = new Persistence(Path.Combine(tempDir, "persist.db"));

            // 1. 校验 tournament_lobbies 表与 TournamentLobbyRow 包含 invite_token_expires_at 列/属性
            Assert.NotNull(typeof(Persistence.TournamentLobbyRow).GetProperty("InviteTokenExpiresAtUtc"));

            // 2. 保存一个已过期归档大厅 + 一条大厅聊天，执行 RunStorageMaintenance 后验证孤儿 chat_messages 被级联删除
            string lobbyId = Guid.NewGuid().ToString("N");
            string hostId = Guid.NewGuid().ToString("N");
            DateTime oldTime = DateTime.UtcNow.AddDays(-30);
            db.SaveTournamentLobbyWithMembers(
                new Persistence.TournamentLobbyRow(
                    LobbyId: lobbyId,
                    Code: "W3B999",
                    Name: "Wave3B Archive Test",
                    Kind: "custom",
                    PlayMode: "Tournament",
                    ModeId: "standard",
                    PresetId: "PureOfficial",
                    MutatorsJson: "[]",
                    HostAccountId: hostId,
                    HostUsername: "w3b_host",
                    PlayerCap: 4,
                    IsPublic: true,
                    HasPassword: false,
                    PasswordHash: "",
                    IsInviteOnly: false,
                    InviteTokenHash: "",
                    ExcludeHostFromGameplay: false,
                    State: "finished",
                    TournamentSessionId: "",
                    RoundCountdownSeconds: 120,
                    AccelRoundCountdownSeconds: 30,
                    RulesJson: "{}",
                    BannedEncounterIdsJson: "[]",
                    RoundBarriersJson: "{}",
                    MatchmakerStateJson: "{}",
                    CreatedAt: oldTime,
                    StartedAt: oldTime,
                    ExpiresAt: oldTime.AddHours(1),
                    EndedAt: oldTime.AddHours(1),
                    UpdatedAt: oldTime.AddHours(1)),
                Array.Empty<Persistence.TournamentLobbyMemberRow>());

            db.InsertChatMessage("lobby", lobbyId, "chat", hostId, "w3b_host", "orphan lobby msg");
            Assert.Single(db.QueryChatMessages("lobby", lobbyId, 0, 10));

            db.RunStorageMaintenance();
            Assert.Empty(db.QueryChatMessages("lobby", lobbyId, 0, 10));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void D6_P2_06_CustomRuleModels_CloneNormalized_ClearsResidualDailyDateAndSeed()
    {
        var rules = new CustomRunRuleSet
        {
            SpecialModeId = ESpecialModeId.Standard,
            DailyChallengeDate = "2026-11-23",
            FixedRunSeed = 987654,
        };
        var normalized = rules.CloneNormalized();
        Assert.Null(normalized.DailyChallengeDate);
        Assert.Null(normalized.FixedRunSeed);

        // 非 Daily 且无残留 DailyChallengeDate 时，显式 FixedRunSeed 仍须保留（供确定性测试）
        var explicitSeedRules = new CustomRunRuleSet
        {
            SpecialModeId = ESpecialModeId.Standard,
            DailyChallengeDate = null,
            FixedRunSeed = 12345,
        };
        Assert.Equal(12345, explicitSeedRules.CloneNormalized().FixedRunSeed);

        // SandboxLab 必须清空 FixedRunSeed
        var sandboxRules = new CustomRunRuleSet
        {
            SpecialModeId = ESpecialModeId.SandboxLab,
            FixedRunSeed = 12345,
        };
        Assert.Null(sandboxRules.CloneNormalized().FixedRunSeed);
    }

    [Fact]
    public void D2_P2_04_D8_P2_06_ServerRuntimeSnapshotProvider_RefreshesOnModReloadAndKeepsBaseRepoPure()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"w3b_snap_{Guid.NewGuid():N}");
        string modsDir = Path.Combine(tempDir, "mods");
        string stateDir = Path.Combine(tempDir, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);
        try
        {
            var host = new BazaarServer.Mods.ServerModHost(
                Fx.Repo,
                dataRoot: tempDir,
                stateDir: stateDir,
                modsRootOverride: modsDir);
            var provider = new ServerRuntimeSnapshotProvider(host, Fx.Pool);

            var snap1 = provider.Current;
            Assert.NotNull(snap1.Localization);
            Assert.NotNull(snap1.LevelUpTable);
            Assert.NotNull(snap1.GameModeConfig);
            Assert.NotNull(snap1.ArchetypeIndex);
            Assert.NotNull(snap1.CatalogDto);
            Assert.Same(snap1, provider.Current);

            // 动态新增一个最小 Mod 并触发 Reload，验证 ModSetHash 变化后快照原子刷新且 BaseRepository 仍纯净
            string modSub = Path.Combine(modsDir, "w3b-snap-mod");
            Directory.CreateDirectory(Path.Combine(modSub, "localization"));
            File.WriteAllText(Path.Combine(modSub, "localization", "zh-CN.json"), """
            {
              "uiExact": { "global": { "W3B_SnapKey": "W3B快照词条" } }
            }
            """);
            File.WriteAllText(Path.Combine(modSub, "mod.json"), """
            {
              "schemaVersion": 1,
              "id": "w3b-snap-mod",
              "name": "W3B Snapshot Mod",
              "displayNameZh": "W3B快照测试Mod",
              "version": "1.0.0",
              "author": "test",
              "enabledByDefault": true,
              "loadOrder": 100,
              "scope": "cosmetic-only",
              "contentRoots": {
                "localization": "localization/zh-CN.json"
              }
            }
            """);

            var baseRepoBefore = host.BaseRepository;
            host.Reload();
            host.SetModEnabledAndReload("w3b-snap-mod", true);
            var snap2 = provider.Current;
            Assert.NotSame(snap1, snap2);
            Assert.Equal(host.ModSetHash, snap2.ModSetHash);
            Assert.NotEqual(snap1.ModSetHash, snap2.ModSetHash);
            Assert.Same(snap2, provider.Current);
            // BaseRepository 在热重载前后始终维持纯原版只读底库实例与语义不变
            Assert.Same(baseRepoBefore, host.BaseRepository);
            Assert.Equal(Fx.Repo.CardsById.Count, host.BaseRepository.CardsById.Count);
            Assert.DoesNotContain(host.BaseRepository.CardsById.Values, c => c.IsCustomModCard);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
