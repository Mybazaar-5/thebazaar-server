using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using BazaarGameShared;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using MessagePack;

namespace BazaarProbe;

/// <summary>
/// 完整无头协议全链路验证：
///   登录 → 开局技能 → Hour1 Choice选商人 → Encounter买卡（验证中心槽位0x78、Shield/Joy属性键）
///   → Hour3 PvE三段式战斗（断言 Aggregate=[Spawn(Combat), CombatSim(Frames>=12), Despawn(Loot)]）
///   → Hour6 真实 PvP（断言 State=PVPCombat、PvpOpponent非空、CombatSim帧完整）→ Cheat终局验收。
/// </summary>
internal static class RunLoop
{
    public static async Task<int> Run(string baseUrl)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        int fails = 0;
        void Ok(string s) => Console.WriteLine("  OK   " + s);
        void Bad(string s) { fails++; Console.WriteLine("  FAIL " + s); }
        void Info(string s) => Console.WriteLine("       " + s);

        Console.WriteLine($"== RunLoop V3 against {baseUrl} ==");

        // 1. 登录 + Bootstrap
        string name = "LoopTester";
        var loginResp = await http.PostAsync(baseUrl + "/api/auth/login/user",
            new StringContent($"{{\"username\":\"{name}\"}}", Encoding.UTF8, "application/json"));
        string loginJson = await loginResp.Content.ReadAsStringAsync();
        string token = System.Text.Json.JsonDocument.Parse(loginJson).RootElement.GetProperty("AccessToken").GetString();

        var bootReq = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/Bootstrap/app");
        bootReq.Headers.Add("Authorization", "Bearer " + token);
        var bootResp = await http.SendAsync(bootReq);
        string bootJson = await bootResp.Content.ReadAsStringAsync();
        using (var doc = System.Text.Json.JsonDocument.Parse(bootJson))
        {
            var track = doc.RootElement.GetProperty("season").GetProperty("track");
            if (track.GetProperty("infiniteTier").ValueKind == System.Text.Json.JsonValueKind.Object)
                Ok("SeasonTrack.infiniteTier present");
            else
                Bad("SeasonTrack.infiniteTier is null");

            if (doc.RootElement.GetProperty("collection").GetProperty("items").GetArrayLength() == 0)
                Ok("CollectionItems clean (0 fake GUIDs)");
            else
                Info("CollectionItems non-empty");
        }

        string accountId = System.Text.Json.JsonDocument.Parse(bootJson).RootElement
            .GetProperty("account").GetProperty("summary").GetProperty("AccountId").GetString();

        // 2. POST /sessions
        var init = new InitializeRunCommand
        {
            PlayMode = EPlayMode.Unranked,
            SelectedHero = EHero.Vanessa,
        };
        byte[] initBytes = MessagePackSerializer.Serialize<INetCommand>(init, MessagePackConfig.Options);
        var sessReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/sessions");
        sessReq.Content = new ByteArrayContent(initBytes);
        sessReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/msgpack");
        sessReq.Headers.Add("Authorization", "Bearer " + token);
        sessReq.Headers.Add("aid", accountId?.Replace("-", ""));
        sessReq.Headers.Add("uid", name);
        var sessResp = await http.SendAsync(sessReq);
        byte[] sessBody = await sessResp.Content.ReadAsByteArrayAsync();
        string sid = sessResp.Headers.TryGetValues("sid", out var sv) ? sv.First() : null;
        if (sessResp.StatusCode != System.Net.HttpStatusCode.OK || string.IsNullOrEmpty(sid))
        {
            Bad($"/sessions HTTP {sessResp.StatusCode}");
            return 1;
        }
        Ok($"/sessions sid={sid[..12]}…");

        var agg = MessagePackSerializer.Deserialize<INetMessage>(sessBody, MessagePackConfig.Options) as NetMessageAggregate;
        var snap = agg?.Messages.OfType<NetMessageGameStateSync>().FirstOrDefault()?.Data;
        var firstSim = agg?.Messages.OfType<NetMessageGameSim>().FirstOrDefault()?.Data;
        if (snap == null || firstSim == null) { Bad("missing snapshot or firstSim"); return 1; }

        if (snap.Player.UnlockedSlots == 120) Ok("UnlockedSlots == 120 (centered 4 slots Socket_3..6)");
        else Bad($"UnlockedSlots expected 120, got {snap.Player.UnlockedSlots}");

        if (firstSim.Player.Attributes.ContainsKey(EPlayerAttributeType.Shield) &&
            firstSim.Player.Attributes.ContainsKey(EPlayerAttributeType.Joy))
            Ok("Player.Attributes contains Shield & Joy");
        else
            Bad("Player.Attributes missing Shield or Joy");

        if (firstSim.Opponent.Attributes.ContainsKey(EPlayerAttributeType.Shield) &&
            firstSim.Opponent.Attributes.ContainsKey(EPlayerAttributeType.Joy))
            Ok("Opponent.Attributes contains Shield & Joy");
        else
            Bad("Opponent.Attributes missing Shield or Joy");

        var currentSel = new List<string>(firstSim.CurrentState?.SelectionSet ?? new List<string>());
        var currentState = firstSim.CurrentState?.StateName ?? ERunState.Choice;
        var currentCanExit = firstSim.CurrentState?.SelectionContextRules?.CanExit ?? false;
        var currentDay = firstSim.Run?.Day ?? snap.Run.Day;
        var currentHour = firstSim.Run?.Hour ?? snap.Run.Hour;
        var currentWins = firstSim.Run?.Victories ?? snap.Run.Victories;
        var currentLosses = firstSim.Run?.Defeats ?? snap.Run.Defeats;
        var currentGold = firstSim.Player.Attributes.GetValueOrDefault(EPlayerAttributeType.Gold);
        var currentCards = new Dictionary<string, SimUpdateCard>(firstSim.Cards);
        var cardTypes = new Dictionary<string, ECardType>(StringComparer.Ordinal);
        foreach (var c in snap.Cards) cardTypes[c.InstanceId] = c.Type;
        foreach (var ev in firstSim.Events)
        {
            if (ev is GameSimEventCardDealt dEv) cardTypes[dEv.InstanceId] = dEv.Type;
            else if (ev is GameSimEventCardSpawned sEv) cardTypes[sEv.InstanceId] = sEv.Type;
        }

        static ECardType InferType(string id) => id switch
        {
            _ when id.StartsWith("skl_", StringComparison.Ordinal) => ECardType.Skill,
            _ when id.StartsWith("step_", StringComparison.Ordinal) || id.StartsWith("stp_", StringComparison.Ordinal) => ECardType.EncounterStep,
            _ when id.StartsWith("ped_", StringComparison.Ordinal) => ECardType.PedestalEncounter,
            _ when id.StartsWith("cbt_", StringComparison.Ordinal) => ECardType.CombatEncounter,
            _ when id.StartsWith("pvp_", StringComparison.Ordinal) => ECardType.PvpEncounter,
            _ when id.StartsWith("enc_", StringComparison.Ordinal) => ECardType.EventEncounter,
            _ => ECardType.Item,
        };

        void SyncFromSim(GameSim? sim)
        {
            if (sim == null) return;
            if (sim.Run != null)
            {
                currentDay = sim.Run.Day;
                currentHour = sim.Run.Hour;
                currentWins = sim.Run.Victories;
                currentLosses = sim.Run.Defeats;
            }
            foreach (var ev in sim.Events)
            {
                if (ev is GameSimEventCardDealt dEv) cardTypes[dEv.InstanceId] = dEv.Type;
                else if (ev is GameSimEventCardSpawned sEv) cardTypes[sEv.InstanceId] = sEv.Type;
            }
            if (sim.CurrentState != null)
            {
                currentState = sim.CurrentState.StateName;
                if (sim.CurrentState.SelectionContextRules != null)
                    currentCanExit = sim.CurrentState.SelectionContextRules.CanExit;
                currentSel = sim.CurrentState.SelectionSet != null ? new List<string>(sim.CurrentState.SelectionSet) : new List<string>();
            }
            currentGold = sim.Player?.Attributes?.GetValueOrDefault(EPlayerAttributeType.Gold, currentGold) ?? currentGold;
            foreach (var kv in sim.Cards) currentCards[kv.Key] = kv.Value;
        }

        async Task<(bool ok, INetMessage msg)> Send(INetCommand cmd, string label)
        {
            byte[] bytes = MessagePackSerializer.Serialize<INetCommand>(cmd, MessagePackConfig.Options);
            var r = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/commands");
            r.Content = new ByteArrayContent(bytes);
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/msgpack");
            r.Headers.Add("sid", sid);
            r.Headers.Add("rid", "0");
            r.Headers.Add("Authorization", "Bearer " + token);
            r.Headers.Add("aid", accountId?.Replace("-", ""));
            r.Headers.Add("uid", name);

            var resp = await http.SendAsync(r);
            byte[] body = await resp.Content.ReadAsByteArrayAsync();
            if (resp.StatusCode != System.Net.HttpStatusCode.OK)
            {
                Bad($"{label}: HTTP {resp.StatusCode}");
                return (false, null);
            }
            var m = MessagePackSerializer.Deserialize<INetMessage>(body, MessagePackConfig.Options);
            if (m is NetMessageError err)
            {
                Bad($"{label}: NetMessageError {err.ErrorType} {err.Message}");
                return (false, m);
            }
            if (m is NetMessageGameSim singleSim)
            {
                SyncFromSim(singleSim.Data);
            }
            else if (m is NetMessageAggregate aggMsg)
            {
                foreach (var inner in aggMsg.Messages.OfType<NetMessageGameSim>())
                    SyncFromSim(inner.Data);
            }
            return (true, m);
        }

        // 3. 完成开局选包/技能 → 应切入 Hour1 Choice 遭遇三选一
        if (currentSel.Count < 1) { Bad("starter selection empty"); return 1; }
        for (int starterStep = 0; starterStep < 6 && currentState != ERunState.Choice; starterStep++)
        {
            if (currentSel.Count == 0)
            {
                if (currentCanExit)
                {
                    await Send(new ExitCurrentStateCommand(), $"StarterExit#{starterStep}");
                    continue;
                }
                break;
            }
            string pickId = currentSel[0];
            var pickType = cardTypes.GetValueOrDefault(pickId, InferType(pickId));
            INetCommand starterCmd = pickType switch
            {
                ECardType.Skill => new SelectSkillCommand { InstanceId = new InstanceId(pickId) },
                ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter
                    => new SelectEncounterCommand { InstanceId = new InstanceId(pickId) },
                _ => new SelectItemCommand { InstanceId = new InstanceId(pickId), Section = EInventorySection.Hand },
            };
            await Send(starterCmd, $"StarterPick#{starterStep}({pickType})");
        }
        if (currentState == ERunState.Choice && currentSel.Count >= 3)
            Ok($"starter completed -> Hour1 Choice ({currentSel.Count} encounters)");
        else
            Bad($"after starter expected Choice with 3 encounters, got state={currentState} sel={currentSel.Count}");

        // 4. Hour 1 选遭遇 → 应进入 EncounterState 揭开迷雾并铺开商品
        var (encOk, encMsg) = await Send(new SelectEncounterCommand { InstanceId = new InstanceId(currentSel[0]) }, "Hour1.SelectMerchant");
        if (encOk && currentState == ERunState.Encounter)
            Ok($"entered EncounterState, sel={currentSel.Count}");
        else
            Bad($"expected EncounterState, got {currentState}");

        // 校验候选商品 Owner == null, Socket == null
        var candidateCard = currentSel.FirstOrDefault();
        if (candidateCard != null && currentCards.TryGetValue(candidateCard, out var cDelta))
        {
            if (cDelta.Placement?.Owner == null && cDelta.Placement?.Socket == null)
                Ok("merchant card has Owner=null Socket=null (centered layout)");
            else
                Bad($"merchant card placement invalid: owner={cDelta.Placement?.Owner} socket={cDelta.Placement?.Socket}");
        }

        // 加钱买 1 张卡并退出商店
        await Send(new CheatCommand { Args = new List<string> { "gold", "50" } }, "Cheat(gold 50)");
        if (candidateCard != null)
        {
            var (buyOk, buyMsg) = await Send(new SelectItemCommand
            {
                InstanceId = new InstanceId(candidateCard),
                TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_3 },
                Section = EInventorySection.Hand,
            }, "BuyItem(Socket_3)");
            var sim = ExtractLastSim(buyMsg);
            var pur = sim?.Events.OfType<GameSimEventCardPurchased>().FirstOrDefault();
            if (buyOk && pur != null)
                Ok($"purchased {pur.InstanceId} -> socket={pur.LeftSocketId} price={pur.BuyPrice}");
            else
                Bad("purchase failed");
        }

        if (currentCanExit)
            await Send(new ExitCurrentStateCommand(), "ExitHour1Encounter"); // -> Hour 2 Choice

        // 5. 跳过 Hour 2 进 Hour 3 野怪战
        if (currentState == ERunState.Choice && currentSel.Count > 0)
        {
            await Send(new SelectEncounterCommand { InstanceId = new InstanceId(currentSel[0]) }, "Hour2.Select");
            for (int h2 = 0; h2 < 6 && currentState != ERunState.Choice; h2++)
            {
                if (currentCanExit)
                    await Send(new ExitCurrentStateCommand(), $"ExitHour2Encounter#{h2}");
                else if (currentSel.Count > 0)
                    await Send(new SelectEncounterCommand { InstanceId = new InstanceId(currentSel[0]) }, $"StepHour2#{h2}");
                else
                    break;
            }
        }

        // Hour 3 选野怪 → 断言三段式战斗包
        var (pveOk, pveMsg) = await Send(new SelectEncounterCommand { InstanceId = new InstanceId(currentSel[0]) }, "Hour3.SelectMonster");
        if (pveOk && pveMsg is NetMessageAggregate pveAgg && pveAgg.Messages.Count == 3)
        {
            var spawn = pveAgg.Messages[0] as NetMessageGameSim;
            var combat = pveAgg.Messages[1] as NetMessageCombatSim;
            var despawn = pveAgg.Messages[2] as NetMessageGameSim;
            bool spawnOk = spawn?.Data?.CurrentState?.StateName == ERunState.Combat
                           && spawn.Data.Events.OfType<GameSimEventCardSpawned>().Any();
            bool framesOk = combat?.Data?.Frames != null && combat.Data.Frames.Count >= 12;
            bool despawnOk = despawn?.Data?.Events.OfType<GameSimEventCardDisposed>().Any() == true;

            if (spawnOk && framesOk && despawnOk)
                Ok($"Hour3 PvE 3-part sequence valid! frames={combat!.Data.Frames.Count} winner={combat.Data.Winner} postState={despawn!.Data.CurrentState?.StateName}");
            else
                Bad($"Hour3 PvE 3-part invalid: spawn={spawnOk} frames={combat?.Data?.Frames?.Count} despawn={despawnOk}");
        }
        else
        {
            Bad($"Hour3 expected 3-part Aggregate, got {(pveMsg as NetMessageAggregate)?.Messages.Count}");
        }

        // 若在 Loot / LevelUp / Encounter 稳步推进到 Hour 6 PvP
        for (int guard = 0; guard < 24; guard++)
        {
            if (currentState == ERunState.Choice && currentHour == 6 &&
                currentSel.Any(s => cardTypes.GetValueOrDefault(s, InferType(s)) == ECardType.PvpEncounter))
                break;

            if (currentState == ERunState.Choice && currentSel.Count > 0)
            {
                await Send(new SelectEncounterCommand { InstanceId = new InstanceId(currentSel[0]) }, $"HourChoice#{guard}");
            }
            else if (currentCanExit)
            {
                await Send(new ExitCurrentStateCommand(), $"AdvanceToHour6#{guard}");
            }
            else if (currentSel.Count > 0)
            {
                string optId = currentSel[0];
                var optType = cardTypes.GetValueOrDefault(optId, InferType(optId));
                INetCommand stepCmd = optType switch
                {
                    ECardType.Skill => new SelectSkillCommand { InstanceId = new InstanceId(optId) },
                    ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter
                        => new SelectEncounterCommand { InstanceId = new InstanceId(optId) },
                    _ => new SelectItemCommand { InstanceId = new InstanceId(optId), Section = EInventorySection.Hand },
                };
                await Send(stepCmd, $"AdvanceStep#{guard}({optType})");
            }
            else
            {
                break;
            }
        }

        string pvpId = currentSel.FirstOrDefault(s => cardTypes.GetValueOrDefault(s, InferType(s)) == ECardType.PvpEncounter);
        if (!string.IsNullOrEmpty(pvpId))
        {
            var dealtPvpType = cardTypes.GetValueOrDefault(pvpId, InferType(pvpId));
            if (dealtPvpType == ECardType.PvpEncounter)
                Ok($"Hour6 PvP candidate typed as ECardType.PvpEncounter ({pvpId})");
            else
                Bad($"Hour6 PvP candidate expected PvpEncounter, got {dealtPvpType}");

            var (pvpOk, pvpMsg) = await Send(new SelectEncounterCommand { InstanceId = new InstanceId(pvpId) }, "Hour6.SelectPvP");
            if (pvpOk && pvpMsg is NetMessageAggregate pvpAgg && pvpAgg.Messages.Count == 3)
            {
                var spawn = pvpAgg.Messages[0] as NetMessageGameSim;
                var combat = pvpAgg.Messages[1] as NetMessageCombatSim;
                bool isPvpState = spawn?.Data?.CurrentState?.StateName == ERunState.PVPCombat;
                bool hasOpp = spawn?.Data?.CurrentState?.PvpOpponent != null;
                bool hasFrames = combat?.Data?.Frames != null && combat.Data.Frames.Count >= 12;
                if (isPvpState && hasOpp && hasFrames)
                    Ok($"Hour6 PVP 3-part valid! vs='{spawn!.Data.CurrentState!.PvpOpponent!.Name}' frames={combat!.Data.Frames.Count} winner={combat.Data.Winner} postD{currentDay}H{currentHour} WL={currentWins}/{currentLosses}");
                else
                    Bad($"Hour6 PVP invalid: state={spawn?.Data?.CurrentState?.StateName} hasOpp={hasOpp} frames={combat?.Data?.Frames?.Count}");
            }
            else
            {
                Bad("Hour6 PVP did not return 3-part Aggregate");
            }
        }
        else
        {
            Bad($"did not reach Hour6 PvP (state={currentState} D{currentDay}H{currentHour})");
        }

        // 6. GM 10胜终局验收
        var (winOk, _) = await Send(new CheatCommand { Args = new List<string> { "win", "10" } }, "Cheat(win 10)");
        if (winOk && currentState == ERunState.EndRunVictory)
            Ok("EndRunVictory reached cleanly");
        else
            Bad($"expected EndRunVictory, got {currentState}");

        await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, baseUrl + "/sessions")
        {
            Headers = { { "sid", sid } }
        });

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "RunLoop V3: ALL OK" : $"RunLoop V3: FAILURES = {fails}");
        return fails == 0 ? 0 : 1;
    }

    private static GameSim ExtractLastSim(INetMessage msg)
    {
        if (msg is NetMessageGameSim g) return g.Data;
        if (msg is NetMessageAggregate a)
        {
            GameSim last = null;
            foreach (var m in a.Messages)
            {
                if (m is NetMessageGameSim gg) last = gg.Data;
            }
            return last;
        }
        return null;
    }
}
