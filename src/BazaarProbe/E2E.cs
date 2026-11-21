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
using BazaarGameShared.Infra.Messages.GameSimEvents;
using MessagePack;

namespace BazaarProbe;

/// <summary>
/// 协议级端到端测试：直接扮演客户端，用官方 MessagePack 与 DTO 打自建服。
///   BazaarProbe e2e http://127.0.0.1:8080
/// </summary>
internal static class E2E
{
    public static async Task<int> Run(string baseUrl)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        int fails = 0;

        void Ok(string s) => Console.WriteLine("  OK   " + s);
        void Bad(string s) { fails++; Console.WriteLine("  FAIL " + s); }

        Console.WriteLine($"== E2E against {baseUrl} ==");

        // 1) 登录
        Console.WriteLine();
        Console.WriteLine("[1] POST /api/auth/login/silent");
        string loginBody = "{\"SteamTicket\":{\"TicketAsString\":\"14000000e2e-test-ticket\"}}";
        var loginResp = await http.PostAsync(baseUrl + "/api/auth/login/silent",
            new StringContent(loginBody, Encoding.UTF8, "application/json"));
        string loginJson = await loginResp.Content.ReadAsStringAsync();
        Console.WriteLine("    " + loginResp.StatusCode + " " + loginJson);
        string accessToken = null, accountId = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(loginJson);
            accessToken = doc.RootElement.GetProperty("AccessToken").GetString();
            Ok("login ok, token=" + accessToken?[..12] + "…");
        }
        catch (Exception ex) { Bad("login parse: " + ex.Message); return 1; }

        // 2) Bootstrap
        Console.WriteLine();
        Console.WriteLine("[2] GET /api/Bootstrap/app");
        var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/Bootstrap/app");
        req.Headers.Add("Authorization", "Bearer " + accessToken);
        req.Headers.Add("X-Platform", "Steam");
        req.Headers.Add("X-ClientFlavor", "Paid");
        var bootResp = await http.SendAsync(req);
        string bootJson = await bootResp.Content.ReadAsStringAsync();
        Console.WriteLine("    " + bootResp.StatusCode + " len=" + bootJson.Length);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(bootJson);
            accountId = doc.RootElement.GetProperty("account").GetProperty("summary").GetProperty("AccountId").GetString();
            var heroes = doc.RootElement.GetProperty("heroes").GetProperty("owned").GetArrayLength();
            Ok($"bootstrap ok, accountId={accountId}, heroes={heroes}");
        }
        catch (Exception ex) { Bad("bootstrap parse: " + ex.Message); }

        // 3) /sessions
        Console.WriteLine();
        Console.WriteLine("[3] POST /sessions (InitializeRunCommand)");
        var init = new InitializeRunCommand
        {
            PlayMode = EPlayMode.Unranked,
            SelectedHero = EHero.Vanessa,
            GameModeId = null,
            TournamentSessionId = null,
        };
        byte[] initBytes = MessagePackSerializer.Serialize<INetCommand>(init, MessagePackConfig.Options);
        Console.WriteLine($"    request: {initBytes.Length} bytes  json={MessagePackSerializer.ConvertToJson(initBytes, MessagePackConfig.Options)}");

        var sessReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/sessions");
        sessReq.Content = new ByteArrayContent(initBytes);
        sessReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/msgpack");
        sessReq.Headers.Add("Authorization", "Bearer " + accessToken);
        sessReq.Headers.Add("aid", accountId?.Replace("-", ""));
        sessReq.Headers.Add("uid", "e2e-tester");
        var sessResp = await http.SendAsync(sessReq);
        byte[] sessBody = await sessResp.Content.ReadAsByteArrayAsync();
        string sid = sessResp.Headers.TryGetValues("sid", out var sv) ? string.Join(",", sv) : null;
        string rid = sessResp.Headers.TryGetValues("rid", out var rv) ? string.Join(",", rv) : null;
        Console.WriteLine($"    {sessResp.StatusCode} sid={sid} rid={rid} body={sessBody.Length} bytes");
        Console.WriteLine("    " + MessagePackSerializer.ConvertToJson(sessBody, MessagePackConfig.Options));

        if (sessResp.StatusCode != System.Net.HttpStatusCode.OK) Bad("/sessions status");
        else if (string.IsNullOrEmpty(sid)) Bad("/sessions missing sid header");
        else Ok("/sessions ok");

        INetMessage sessMsg = null;
        try
        {
            sessMsg = MessagePackSerializer.Deserialize<INetMessage>(sessBody, MessagePackConfig.Options);
            Ok("/sessions body decoded as " + sessMsg.GetType().Name);
        }
        catch (Exception ex) { Bad("/sessions decode: " + ex.Message); return 1; }

        // 4) 命令往返（严格遵循状态机顺序：先完成开局选包 -> 进入真实商店测 Reroll / Exit）
        Console.WriteLine();
        Console.WriteLine("[4] POST /commands × N");

        var curState = ERunState.Choice;
        var curSel = new List<string>();
        var curRules = new TSelectionContextRules();
        var cardTypes = new Dictionary<string, ECardType>(StringComparer.Ordinal);

        void TrackMessage(INetMessage m)
        {
            if (m is NetMessageGameStateSync sync && sync.Data != null)
            {
                if (sync.Data.CurrentState != null)
                {
                    curState = sync.Data.CurrentState.StateName;
                    curSel = new List<string>(sync.Data.CurrentState.SelectionSet ?? new List<string>());
                    curRules = sync.Data.CurrentState.SelectionContextRules ?? curRules;
                }
                foreach (var c in sync.Data.Cards)
                    cardTypes[c.InstanceId] = c.Type;
            }
            else if (m is NetMessageGameSim gs && gs.Data != null)
            {
                if (gs.Data.CurrentState != null)
                {
                    curState = gs.Data.CurrentState.StateName;
                    curSel = new List<string>(gs.Data.CurrentState.SelectionSet ?? new List<string>());
                    curRules = gs.Data.CurrentState.SelectionContextRules ?? curRules;
                }
                foreach (var ev in gs.Data.Events)
                {
                    if (ev is GameSimEventCardDealt dealt) cardTypes[dealt.InstanceId] = dealt.Type;
                    else if (ev is GameSimEventCardSpawned sp) cardTypes[sp.InstanceId] = sp.Type;
                }
            }
            else if (m is NetMessageAggregate aggMsg)
            {
                foreach (var inner in aggMsg.Messages) TrackMessage(inner);
            }
        }

        TrackMessage(sessMsg);

        async Task SendCommand(INetCommand cmd, string label)
        {
            byte[] bytes = MessagePackSerializer.Serialize<INetCommand>(cmd, MessagePackConfig.Options);
            var r = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/commands");
            r.Content = new ByteArrayContent(bytes);
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/msgpack");
            r.Headers.Add("sid", sid);
            r.Headers.Add("rid", "0");
            r.Headers.Add("Authorization", "Bearer " + accessToken);
            r.Headers.Add("aid", accountId?.Replace("-", ""));
            r.Headers.Add("uid", "e2e-tester");

            var resp = await http.SendAsync(r);
            byte[] body = await resp.Content.ReadAsByteArrayAsync();
            Console.WriteLine($"    {label}: {resp.StatusCode} {body.Length} bytes");
            if (resp.StatusCode != System.Net.HttpStatusCode.OK) { Bad(label + " status " + resp.StatusCode); return; }
            try
            {
                var msg = MessagePackSerializer.Deserialize<INetMessage>(body, MessagePackConfig.Options);
                string json = MessagePackSerializer.ConvertToJson(body, MessagePackConfig.Options);
                Console.WriteLine("      -> " + msg.GetType().Name + " " + (json.Length > 260 ? json[..260] + "…" : json));
                if (msg is NetMessageError err) Bad(label + " returned NetMessageError: " + err.Message);
                else
                {
                    TrackMessage(msg);
                    Ok(label);
                }
            }
            catch (Exception ex) { Bad(label + " decode: " + ex.Message); }
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

        // 先完成开局选择（!StartSelectionDone 状态下不可 Reroll / Exit），直到进入 Choice
        for (int s = 0; s < 6 && curState != ERunState.Choice; s++)
        {
            if (curSel.Count == 0)
            {
                if (curRules?.CanExit == true)
                {
                    await SendCommand(new ExitCurrentStateCommand(), $"StarterExit#{s}");
                    continue;
                }
                break;
            }
            string pickId = curSel[0];
            ECardType pickType = cardTypes.GetValueOrDefault(pickId, InferType(pickId));
            INetCommand starterCmd = pickType switch
            {
                ECardType.Skill => new SelectSkillCommand { InstanceId = new InstanceId(pickId) },
                ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter
                    => new SelectEncounterCommand { InstanceId = new InstanceId(pickId) },
                _ => new SelectItemCommand { InstanceId = new InstanceId(pickId), Section = EInventorySection.Hand },
            };
            await SendCommand(starterCmd, $"StarterPick#{s}({pickType})");
        }

        await SendCommand(new CheatCommand { Args = new List<string> { "gold", "50" } }, "CheatCommand(gold 50)");
        await SendCommand(new CheatCommand { Args = new List<string> { "day", "3" } }, "CheatCommand(day 3)");

        if (curState == ERunState.Choice && curSel.Count > 0)
        {
            // Hour 1 Choice 槽位 1 为真实商店（支持 Reroll 与 Exit）
            string merchantId = curSel.FirstOrDefault(id => cardTypes.GetValueOrDefault(id, InferType(id)) == ECardType.EventEncounter) ?? curSel[0];
            await SendCommand(new SelectEncounterCommand { InstanceId = new InstanceId(merchantId) }, "SelectEncounterCommand(Merchant)");

            if (curState == ERunState.Encounter && curRules?.RerollRules != null)
                await SendCommand(new RerollCommand(), "RerollCommand");

            if (curRules?.CanExit == true)
                await SendCommand(new ExitCurrentStateCommand(), "ExitCurrentStateCommand");
            else if (curSel.Count > 0)
                await SendCommand(new SelectEncounterCommand { InstanceId = new InstanceId(curSel[0]) }, "StepAfterEncounter");
        }
        else
        {
            Bad($"expected Choice state after starter, got {curState} (sel={curSel.Count})");
        }

        if (curState == ERunState.Choice && curSel.Count > 0)
        {
            await SendCommand(new SelectEncounterCommand { InstanceId = new InstanceId(curSel[0]) }, "SelectEncounterCommand(Hour2)");
        }

        // 5) DELETE /sessions
        Console.WriteLine();
        Console.WriteLine("[5] DELETE /sessions");
        var del = new HttpRequestMessage(HttpMethod.Delete, baseUrl + "/sessions");
        del.Headers.Add("sid", sid);
        var delResp = await http.SendAsync(del);
        Console.WriteLine("    " + delResp.StatusCode);
        if (delResp.StatusCode == System.Net.HttpStatusCode.OK) Ok("delete ok"); else Bad("delete " + delResp.StatusCode);

        // 6) 已删会话应返回 410
        Console.WriteLine();
        Console.WriteLine("[6] POST /commands after delete (expect 410)");
        var after = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/commands");
        after.Content = new ByteArrayContent(MessagePackSerializer.Serialize<INetCommand>(new RerollCommand(), MessagePackConfig.Options));
        after.Content.Headers.ContentType = new MediaTypeHeaderValue("application/msgpack");
        after.Headers.Add("sid", sid);
        var afterResp = await http.SendAsync(after);
        Console.WriteLine("    " + afterResp.StatusCode);
        if ((int)afterResp.StatusCode == 410) Ok("410 as designed (triggers client session re-establish)"); else Bad("expected 410, got " + afterResp.StatusCode);

        Console.WriteLine();
        Console.WriteLine(fails == 0 ? "E2E: ALL OK" : $"E2E: FAILURES = {fails}");
        return fails == 0 ? 0 : 1;
    }
}
