using BazaarCore.Data;
using BazaarServer.Game;

namespace BazaarServer.Data;

/// <summary>
/// Hour 3 PvE 野怪三槽位难度层级（左青铜 / 中白银 / 右黄金）。
/// </summary>
public enum EMonsterSlotTier
{
    LeftBronze = 0,
    MidSilver = 1,
    RightGold = 2,
}

/// <summary>
/// 单天三槽位（左/中/右）官方精选 <c>TCardEncounterCombat</c> GUID 名册。
/// </summary>
public sealed record DayMonsterRoster(
    int Day,
    IReadOnlyList<string> LeftIds,
    IReadOnlyList<string> MidIds,
    IReadOnlyList<string> RightIds)
{
    /// <summary>枚举该天所有槽位配置的精选战斗遭遇模板 ID。</summary>
    public IEnumerable<string> AllIds()
    {
        foreach (string id in LeftIds) yield return id;
        foreach (string id in MidIds) yield return id;
        foreach (string id in RightIds) yield return id;
    }
}

/// <summary>
/// V12 官方每日（Day 1..10+）PvE 野怪调度表。
///
/// 设计准则：
///   1. 每个精选 GUID 均来自 <c>GameData.db</c> 真实 <c>CombatEncounter</c> 模板（含有效白名单资产与非空怪兽手牌）；
///   2. 严格维持三槽难度与血量/等级单调递增（左青铜 &lt;= 中白银 &lt;= 右黄金）；
///   3. 运行时逐条校验资产完整性、非调试/占位怪（排除 <c>[DEBUG]</c>/<c>Dummy</c>/<c>20HP</c>/<c>10000HP</c>/<c>Test Subject Alpha</c> 等）及自定义禁用名单；
///   4. 若任意一天因禁用/精简库等原因合法候选不足 3 只，平滑回退至 <see cref="MonsterPool"/> 按等级分桶选怪并保持去重。
/// </summary>
public static class OfficialDaySchedule
{
    private static readonly IReadOnlyDictionary<int, DayMonsterRoster> _curatedRosters =
        new Dictionary<int, DayMonsterRoster>
        {
            [1] = new(
                Day: 1,
                LeftIds:
                [
                    "bd0711a6-d9d9-4a47-8a18-8f991057f7dd", // Kyver Drone (Lv1, 75HP, Bronze)
                    "1d24717f-7bfb-48e9-9320-6a69d72e233e", // Banannibal (Lv1, 100HP, Bronze)
                    "bb1e3506-3735-4669-be90-915a55a7ee05", // Fanged Inglet (Lv1, 100HP, Bronze)
                    "45c473f5-25bd-4c1c-8357-cfe460c58f69", // Pyro (Lv1, 100HP, Bronze)
                ],
                MidIds:
                [
                    "1d24717f-7bfb-48e9-9320-6a69d72e233e", // Banannibal (Lv1, 100HP, Bronze)
                    "bb1e3506-3735-4669-be90-915a55a7ee05", // Fanged Inglet (Lv1, 100HP, Bronze)
                    "45c473f5-25bd-4c1c-8357-cfe460c58f69", // Pyro (Lv1, 100HP, Bronze)
                ],
                RightIds:
                [
                    "289ff978-dd65-4484-b905-8ee16d28033c", // Coconut Crab (Lv2, 200HP, Bronze)
                    "82a64f45-ca55-4a24-b1ef-18d41cc77fa5", // The Baron Boys (Lv2, 200HP, Bronze)
                    "1eabe08b-2949-407d-b22d-c6e579e0cd14", // Rogue Scrapper (Lv2, 300HP, Bronze)
                ]),

            [2] = new(
                Day: 2,
                LeftIds:
                [
                    "289ff978-dd65-4484-b905-8ee16d28033c", // Coconut Crab (Lv2, 200HP, Bronze)
                    "82a64f45-ca55-4a24-b1ef-18d41cc77fa5", // The Baron Boys (Lv2, 200HP, Bronze)
                ],
                MidIds:
                [
                    "66245d12-d5eb-4c08-9185-6ddaf626bbd3", // Covetous Thief (Silver, Lv3, 300HP)
                    "8cac11e0-0550-42be-bbfc-b18697e3369f", // Soda Swiller (Lv3, 300HP, Silver)
                    "b93f5b44-ba9f-4196-a94e-c5de17ab5c5d", // Boarrior (Lv3, 350HP, Silver)
                ],
                RightIds:
                [
                    "dbf8eefa-99bd-475b-ad4d-91a9734518cf", // Calico (Lv4, 450HP, Gold)
                    "41c5f103-2d76-4a51-ae28-ee294490b071", // Ventriloquist (Lv4, 450HP, Gold)
                    "325c88c3-9496-44bf-b900-4adf1f84bf2f", // Eccentric Etherwright (Lv4, 600HP, Gold)
                ]),

            [3] = new(
                Day: 3,
                LeftIds:
                [
                    "289ff978-dd65-4484-b905-8ee16d28033c", // Coconut Crab (Lv2, 200HP, Bronze)
                    "82a64f45-ca55-4a24-b1ef-18d41cc77fa5", // The Baron Boys (Lv2, 200HP, Bronze)
                    "1eabe08b-2949-407d-b22d-c6e579e0cd14", // Rogue Scrapper (Lv2, 300HP, Bronze)
                ],
                MidIds:
                [
                    "66245d12-d5eb-4c08-9185-6ddaf626bbd3", // Covetous Thief (Silver, Lv3, 300HP)
                    "8cac11e0-0550-42be-bbfc-b18697e3369f", // Soda Swiller (Lv3, 300HP, Silver)
                    "b93f5b44-ba9f-4196-a94e-c5de17ab5c5d", // Boarrior (Lv3, 350HP, Silver)
                ],
                RightIds:
                [
                    "dbf8eefa-99bd-475b-ad4d-91a9734518cf", // Calico (Lv4, 450HP, Gold)
                    "41c5f103-2d76-4a51-ae28-ee294490b071", // Ventriloquist (Lv4, 450HP, Gold)
                    "325c88c3-9496-44bf-b900-4adf1f84bf2f", // Eccentric Etherwright (Lv4, 600HP, Gold)
                ]),

            [4] = new(
                Day: 4,
                LeftIds:
                [
                    "8d8c7a87-404b-4bbd-82e1-3c6e3440bdd6", // Covetous Thief (Bronze, Lv3, 300HP)
                    "8e389a21-9e14-49c4-bdf3-24183f9f9295", // Dabbling Apprentice (Lv3, 300HP)
                    "6547a38e-d27f-4443-84bb-db8e04bae467", // Overwhelmed Student (Lv3, 300HP)
                    "04b60df4-91d9-4065-94e0-f9f462a3b104", // Tempest Bravo (Lv3, 300HP)
                ],
                MidIds:
                [
                    "85902df4-6926-40a3-affc-537c55e89b86", // Rogue Scrapper / 等同 Lv4
                    "55046a2f-f81e-4720-aefa-e74b14a06dad", // Frost Street Challenger (Lv4, 450HP)
                    "753ccc3c-726a-4b18-8726-621aedce54b6", // Sommelier (Lv4, 450HP)
                    "4101117c-9cb0-480e-bfac-814da069ec80", // Street Gamer (Lv4, 450HP)
                    "35d68424-87ba-4d8a-8776-8d8302a425f9", // Product Demonstrator (Lv4, 600HP)
                ],
                RightIds:
                [
                    "38936647-a688-49d1-8e5c-9c2c79f18a18", // Hoverbike Hooligan (Lv5, 650HP, Gold)
                    "c69bf8e8-944c-4b42-b59e-8ad568e0d3f7", // Giant Mosquito (Lv5, 700HP, Gold)
                    "e890b42d-43d7-4c4f-a03b-bef8f9d2e23d", // Castaway Corsair (Lv5, 700HP, Diamond)
                    "be111234-2b9f-4e30-b0d6-368c49780faa", // Mutant Baker (Lv5, 700HP, Diamond)
                ]),

            [5] = new(
                Day: 5,
                LeftIds:
                [
                    "6129261a-c4e5-4ff9-b057-58ae1ef29bf7", // Bloodreef Raider (Lv4, 450HP)
                    "4a4542cd-91d8-4463-84fc-c111fc7535ee", // Boilerroom Brawler (Lv4, 450HP)
                    "91e59b0c-968c-4c45-9391-5104571fee28", // Outlands Dervish (Lv4, 450HP)
                    "6c45b720-a58c-43ba-a66e-e0ecfb1b69e2", // Flame Juggler / Lv4 Bronze
                ],
                MidIds:
                [
                    "66e0b55f-022d-4c54-9558-ce7d077a01dd", // Lv5 Silver
                    "9e637cb7-18cf-460d-9c50-5401ee4be521", // Lv5 Silver
                    "850d92a9-d1c5-4334-b046-4f2a1025b28d", // Lv5 Silver
                    "51d0f501-2276-4b88-882b-6535ca6df1a8", // Lv5 Silver
                ],
                RightIds:
                [
                    "b511bd4d-5ad6-4f54-bc74-89622da4d956", // Ahexa (Lv6, 900HP, Gold)
                    "8eb6cf35-27ab-4726-b9f7-05133a4b94b8", // Hellbilly (Lv6, 900HP, Gold)
                    "5253c1ef-af51-43ac-a307-4e707e5747a6", // Prince Marianas (Lv6, 900HP, Gold)
                ]),

            [6] = new(
                Day: 6,
                LeftIds:
                [
                    "ae118438-c54d-4129-98d1-b576e4a4d380", // Lv5 Bronze
                    "47c042ba-707a-4e00-aea2-c1ed2ed880e5", // Lv5 Bronze
                    "de126a7a-a0bc-4a6f-96f0-442920104fcf", // Lv5 Bronze
                ],
                MidIds:
                [
                    "59917a2d-3253-44f4-aaa4-ca30b512eb59", // Lv6 Silver
                    "a75621b9-11a2-4405-8254-0e58a300110f", // Lv6 Silver
                    "54bd46ca-2dec-4fbf-bae9-4cfff1c80043", // Lv6 Silver
                ],
                RightIds:
                [
                    "dbc00a8b-7fae-4792-a9da-698d3cd4f578", // Gorgon Noble (Lv7, 1200HP, Gold)
                    "c472b821-e68b-4567-b7e1-3e63f67936b3", // Mod Squad (Lv7, 1200HP, Gold)
                    "9c167a0c-d348-4e09-8f6b-be0ac65d3cda", // Trashtown Mayor (Lv7, 1200HP, Gold)
                    "cead2300-dce5-4c9c-82c4-e4c05d1923bf", // Flame Juggler (Lv7, 1200HP, Diamond)
                    "a2baf3bc-6164-4a16-ab41-48b450f694d1", // Foreman (Lv7, 1200HP, Diamond)
                ]),

            [7] = new(
                Day: 7,
                LeftIds:
                [
                    "78139f32-94b1-4dc6-9ef0-374c3c8863a7", // Lv6 Bronze
                    "0f15f62d-67c4-4564-91a3-c78a05ddce1d", // Lv6 Bronze
                    "a9b27854-6f5c-4414-b458-90c54c444171", // Lv6 Bronze
                    "7294ff99-ca2b-409e-838f-0efdb10dded0", // Lv6 Bronze
                ],
                MidIds:
                [
                    "962bf2b2-fc1b-4ad3-9526-15d76d3bb0de", // Lv7 Silver
                    "4a6fcebd-0957-448a-8903-fe4a370a9f38", // Lv7 Silver
                    "e8782aea-3511-4a9d-98d4-e25c62120547", // Lv7 Silver
                ],
                RightIds:
                [
                    "bc5fa0a4-8ca9-4384-a283-9c7c84b27169", // Dire Mosquito (Lv8, 1500HP, Gold)
                    "d50543f3-132c-4c43-9230-50a5e96087d9", // Loan Shark (Lv8, 1550HP, Gold)
                    "91c233ed-b88c-4ec9-b3ee-cec880a14213", // Scovyle (Lv8, 1550HP, Diamond)
                    "eccecbd7-1695-49a8-b82b-6e9e974c634a", // Slumcee (Lv8, 1550HP, Diamond)
                ]),

            [8] = new(
                Day: 8,
                LeftIds:
                [
                    "946eb8b4-7712-4377-b2b8-50d826b37d2f", // Lv7 Bronze
                    "224ba8a9-3781-4403-8bfc-527b4e64b8aa", // Lv7 Bronze
                ],
                MidIds:
                [
                    "f79e72dc-7f9b-4e7e-b14e-d4896fcf004f", // Lv8 Silver
                    "25a7a800-1c64-4728-a4fe-eb5a3f3394a0", // Lv8 Silver
                    "7b768d49-b2cc-40f8-ab0f-2d0ed36a0c56", // Lv8 Silver
                ],
                RightIds:
                [
                    "e5e65711-035e-4e95-9017-4110aad06f9a", // Capital Punisher (Lv9, 1800HP)
                    "d9e56b4c-8ad2-465d-b44c-45a7f2562bde", // Annex Trooper (Lv9, 1925HP)
                    "23a21c0f-e163-4817-ba15-0e899d19d387", // Chilly Charles (Lv9, 1925HP)
                    "0ad744f1-6b36-4f7b-a5b7-a5f5e6b855a0", // Cortek Music Generator (Lv9, 1925HP)
                    "7f95be65-d032-42d2-9e4b-ca2a579a380d", // Infernal Gold (Lv9, 1925HP)
                    "e3a8efa1-1dfc-4dd5-a1fe-7daaa8315e18", // Infernal Diamond (Lv9, 1925HP)
                ]),

            [9] = new(
                Day: 9,
                LeftIds:
                [
                    "723f8e17-6885-45b4-b38f-186abb3277c8", // Lv8 Bronze
                    "90aa768f-4a8d-4087-ad5a-5f44efe28e3a", // Lv8 Bronze
                    "78c335a6-42bc-45e2-a8e7-e9a1dce7502d", // Lv8 Bronze
                    "4b69c9fd-cf2d-4197-9b06-0327a5849041", // Lv8 Bronze
                ],
                MidIds:
                [
                    "5fa789a5-211c-46ba-891f-7467f387e37c", // Lv9 Silver
                    "a1c3df15-1058-44ff-bee9-0991cfba2068", // Lv9 Silver
                    "76c8dc41-96c9-4c05-acb1-5eaab4ea8e17", // Lv9 Silver
                    "e82f2b78-f445-4782-b35b-7d6d4058f3be", // Lv9 Silver
                ],
                RightIds:
                [
                    "3836d71e-c6ea-4ef7-a9c7-ce698001e991", // Burninator Bot (Lv10, 2325HP)
                    "93d7bb74-2d23-4ccc-a99f-9380afaf738a", // Dr. Vortex (Lv10, 2325HP)
                    "87ab01e1-55a7-4934-8201-b005b540616f", // Oasis Guardian Gold (Lv10, 2325HP)
                    "2b0437f4-6977-4f84-aeb6-6545cbf5632d", // Thug (Lv10, 2550HP)
                    "a04b33a7-eb29-456a-9a7c-63dd8553b872", // Wandering Shoal Diamond (Lv10, 2550HP)
                ]),

            [10] = new(
                Day: 10,
                LeftIds:
                [
                    "7590dbeb-8087-4464-90f0-96aeee910186", // Lv9 Bronze
                    "338f3c4b-9238-41d2-a0e0-0d825cded307", // Lv9 Bronze
                    "2c059647-c6ca-49ae-ac76-80eadad3fb77", // Lv9 Bronze
                    "f4bc9c3d-ce22-4fd8-9b7f-27214fed9f1d", // Lv9 Bronze
                ],
                MidIds:
                [
                    "852c1d17-e82f-49b6-ade5-59328d5b2e99", // Lv10 Silver
                    "0b3c79ba-ff7f-40e6-b10d-98ab71716304", // Lv10 Silver
                    "254b6aca-26a9-4014-86c3-0da827519c73", // Lv10 Silver
                    "3e80e4e9-13e6-441c-b770-a82a0297ad47", // Lv10 Silver
                    "639c5575-5078-4ff8-8100-e73dce6f5400", // Lv10 Silver
                ],
                RightIds:
                [
                    "21a485af-8811-4d38-bccb-be03a2dcca7f", // Ferros Khan Gold (Lv11, 2750HP)
                    "d63f985a-2960-408e-97ea-f023d4afd440", // Weapons Platform (Lv11, 2750HP)
                    "a44c3d18-8968-4956-b3a2-5cbf3b4c12c2", // Yerdan (Lv11, 2750HP)
                ]),

            [11] = new(
                Day: 11,
                LeftIds:
                [
                    "e54908bc-bf2d-4aac-93c3-393be464558a", // Lv10 Bronze
                    "492c7f84-3cb2-40bb-8a55-29355143bee2", // Lv10 Bronze
                    "a871d83c-fbe0-4b38-b004-6cc598b31239", // Lv10 Bronze
                    "c7405d35-b9fd-49bb-8dcd-a412d7cb9022", // Lv10 Bronze
                ],
                MidIds:
                [
                    "64d6f9df-9003-44f8-bad6-ae9f81f1f7bf", // Lv11 Silver
                    "fc59e9b3-88be-450c-b2dc-65d58a33d1d0", // Lv11 Silver
                    "1e1f7b6d-746b-44d5-9906-fff137c75ef1", // Lv11 Silver
                    "3c433f32-5070-4f57-82d3-9b4347da7aa3", // Lv11 Silver
                    "2b68a81d-ea7e-401b-a374-77529470383f", // Lv11 Silver
                ],
                RightIds:
                [
                    "58477f3f-70f1-4000-9ad0-9e0f44b42349", // Lv12 Gold
                    "e51634b6-ce99-4038-97e4-b9d86bdddae0", // Lv12 Gold
                    "25f54463-2cca-419a-b0e8-8f5297896992", // Lv12 Gold
                    "6727fa2f-f378-49c7-a839-86bac0fec9a6", // Lv12 Gold
                    "27f9cc4c-3f9e-494a-a13d-99797114c53c", // Lv12 Gold
                    "fc3dee8e-aef5-41c9-95a8-d91ad54adede", // Lv12 Diamond
                    "4cebbbdc-db0c-4159-b651-1bb79c264bc9", // Lv12 Diamond
                    "608eb88e-c6d8-41c3-ac88-c20aeb185664", // Lv12 Legendary
                    "b3c0c6f2-4f33-48cd-933f-53a54751d92b", // Lv12 Legendary
                ]),

            [12] = new(
                Day: 12,
                LeftIds:
                [
                    "64d6f9df-9003-44f8-bad6-ae9f81f1f7bf", // Industry Plant (Lv11, 2750HP, Silver)
                    "fc59e9b3-88be-450c-b2dc-65d58a33d1d0", // Lord of the Wastes (Lv11, 2750HP, Silver)
                    "1e1f7b6d-746b-44d5-9906-fff137c75ef1", // Trash Titan (Lv11, 2750HP, Silver)
                    "3c433f32-5070-4f57-82d3-9b4347da7aa3", // Infernal Dreadnought (Lv11, 3050HP, Silver)
                    "2b68a81d-ea7e-401b-a374-77529470383f", // Tortuga (Lv11, 3050HP, Silver)
                ],
                MidIds:
                [
                    "4ccdd72f-cfce-4ea6-9bda-caabd5b0fbf8", // Boss Harrow (Lv12, 2800HP, Gold)
                    "58477f3f-70f1-4000-9ad0-9e0f44b42349", // Volkas Enforcer (Lv12, 3200HP, Gold)
                    "e51634b6-ce99-4038-97e4-b9d86bdddae0", // Awakened Primordial (Lv12, 3600HP, Gold)
                    "25f54463-2cca-419a-b0e8-8f5297896992", // Dooltron (Lv12, 3600HP, Gold)
                    "6727fa2f-f378-49c7-a839-86bac0fec9a6", // Outlands Terror (Lv12, 3600HP, Gold)
                    "27f9cc4c-3f9e-494a-a13d-99797114c53c", // Rex Spex (Lv12, 3600HP, Gold)
                ],
                RightIds:
                [
                    "921d3a60-82d7-445f-8ff0-0d4bccdc6c1e", // Lv13 Diamond
                    "45de3316-8847-4321-ba1c-83a808bad369", // Lv13 Diamond
                    "96c9ede1-34f3-442c-89d3-befbef196729", // Lv13 Diamond
                    "d3439b21-bde3-47cc-9f4e-d92987b942aa", // Lv13 Diamond
                    "776c224d-1e4e-4a20-8516-550b94c05ff1", // Lv13 Diamond
                    "d0202706-46dc-4c65-bf87-5e4ab63632ec", // Lv13 Legendary
                    "be75b034-f986-4cdd-b871-5e8258865c29", // Lv13 Legendary
                    "96349563-bfb5-46ef-8dff-dbf1ff0d4196", // Lv13 Legendary
                ]),
        };

    private static readonly DayMonsterRoster _lateGameRoster = new(
        Day: 13,
        LeftIds:
        [
            "58477f3f-70f1-4000-9ad0-9e0f44b42349", // Lv12 Gold
            "e51634b6-ce99-4038-97e4-b9d86bdddae0", // Lv12 Gold
            "25f54463-2cca-419a-b0e8-8f5297896992", // Lv12 Gold
            "6727fa2f-f378-49c7-a839-86bac0fec9a6", // Lv12 Gold
            "27f9cc4c-3f9e-494a-a13d-99797114c53c", // Lv12 Gold
        ],
        MidIds:
        [
            "921d3a60-82d7-445f-8ff0-0d4bccdc6c1e", // Lv13 Diamond
            "45de3316-8847-4321-ba1c-83a808bad369", // Lv13 Diamond
            "96c9ede1-34f3-442c-89d3-befbef196729", // Lv13 Diamond
            "d3439b21-bde3-47cc-9f4e-d92987b942aa", // Lv13 Diamond
            "776c224d-1e4e-4a20-8516-550b94c05ff1", // Lv13 Diamond
        ],
        RightIds:
        [
            "fb2e242e-daf3-4771-bede-21b3adf03739", // Awakened District (Lv15, 4700HP)
            "8f73d2d6-6071-4464-bb19-8e0783de4df0", // Lord Arken (Lv15, 4700HP)
            "20863550-960f-4713-99eb-2ff2e4cfb3ef", // Morguloth (Lv15, 4700HP)
            "568d3090-6703-4c42-bd65-ba50f883e869", // Void Golem (Lv15, 4700HP)
            "2048fa32-9254-4858-8ec4-ad2815977e0b", // Bananniboss (Lv15, 5500HP)
        ]);

    /// <summary>Day 1..12 的官方三槽 PvE 精选怪表。</summary>
    public static IReadOnlyDictionary<int, DayMonsterRoster> CuratedDayRosters => _curatedRosters;

    /// <summary>Day 13+ 后期/加时天数的官方 PvE 精选怪表。</summary>
    public static DayMonsterRoster LateGameRoster => _lateGameRoster;

    /// <summary>获取指定天数对应的官方 PvE 精选名册（Day &lt;= 1 返回 Day 1，Day &gt;= 13 返回 LateGameRoster）。</summary>
    public static DayMonsterRoster GetRosterForDay(int day)
    {
        if (day <= 1) return _curatedRosters[1];
        if (_curatedRosters.TryGetValue(day, out var roster)) return roster;
        return _lateGameRoster;
    }

    /// <summary>返回指定天数与槽位在官方梯度下期望的合理血量区间（用于校验与精选筛选）。</summary>
    public static (int MinHp, int MaxHp) GetRecommendedHealthBand(int day, EMonsterSlotTier slot)
    {
        int d = Math.Max(1, day);
        return (d, slot) switch
        {
            (1, EMonsterSlotTier.LeftBronze) => (75, 150),
            (1, EMonsterSlotTier.MidSilver) => (100, 200),
            (1, EMonsterSlotTier.RightGold) => (200, 300),

            (2, EMonsterSlotTier.LeftBronze) => (100, 200),
            (2, EMonsterSlotTier.MidSilver) => (200, 350),
            (2, EMonsterSlotTier.RightGold) => (350, 600),

            (3, EMonsterSlotTier.LeftBronze) => (200, 450),
            (3, EMonsterSlotTier.MidSilver) => (200, 450),
            (3, EMonsterSlotTier.RightGold) => (400, 700),

            (4, EMonsterSlotTier.LeftBronze) => (250, 450),
            (4, EMonsterSlotTier.MidSilver) => (400, 700),
            (4, EMonsterSlotTier.RightGold) => (550, 900),

            (5, EMonsterSlotTier.LeftBronze) => (350, 650),
            (5, EMonsterSlotTier.MidSilver) => (500, 850),
            (5, EMonsterSlotTier.RightGold) => (750, 1200),

            (6, EMonsterSlotTier.LeftBronze) => (550, 900),
            (6, EMonsterSlotTier.MidSilver) => (750, 1200),
            (6, EMonsterSlotTier.RightGold) => (1000, 1500),

            (7, EMonsterSlotTier.LeftBronze) => (750, 1200),
            (7, EMonsterSlotTier.MidSilver) => (1100, 1600),
            (7, EMonsterSlotTier.RightGold) => (1300, 1900),

            (8, EMonsterSlotTier.LeftBronze) => (1000, 1500),
            (8, EMonsterSlotTier.MidSilver) => (1400, 2000),
            (8, EMonsterSlotTier.RightGold) => (1600, 2400),

            (9, EMonsterSlotTier.LeftBronze) => (1300, 1900),
            (9, EMonsterSlotTier.MidSilver) => (1600, 2300),
            (9, EMonsterSlotTier.RightGold) => (2000, 2800),

            (10, EMonsterSlotTier.LeftBronze) => (1700, 2400),
            (10, EMonsterSlotTier.MidSilver) => (2100, 2800),
            (10, EMonsterSlotTier.RightGold) => (2400, 3400),

            (11, EMonsterSlotTier.LeftBronze) => (2100, 2900),
            (11, EMonsterSlotTier.MidSilver) => (2500, 3400),
            (11, EMonsterSlotTier.RightGold) => (3000, 4500),

            (12, EMonsterSlotTier.LeftBronze) => (2400, 3300),
            (12, EMonsterSlotTier.MidSilver) => (2800, 4000),
            (12, EMonsterSlotTier.RightGold) => (3400, 9000),

            (_, EMonsterSlotTier.LeftBronze) => (3000, 4500),
            (_, EMonsterSlotTier.MidSilver) => (3400, 5200),
            _ => (4000, 9999),
        };
    }

    /// <summary>
    /// 按正式服 Day 1..10+ 每日名册选出 3 只不重复的 PvE 野怪遭遇（左青铜 / 中白银 / 右黄金）。
    /// 先从当日三槽精选列表中筛选通过 DB、白名单资产与非空怪兽盘面校验的候选；
    /// 若任一槽位在过滤禁用词条或资产检查后无可选项，则无缝回退至 <see cref="MonsterPool"/> 按等级分桶补齐，确保绝不出现重复、调试怪或少于 3 只怪。
    /// </summary>
    public static List<CardPool.CardTemplate> SelectDayMonsters(
        int day,
        int count,
        CardPool pool,
        GameDataRepository repo,
        Random rng,
        ISet<string>? visitedTodayIds = null,
        ISet<string>? bannedIds = null)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(rng);

        if (count <= 0) return new List<CardPool.CardTemplate>();

        int effDay = Math.Max(1, day);
        var roster = GetRosterForDay(effDay);

        // 全库可实战 PvE 野怪回退池（已剔除调试/占位/空板/禁用）
        var fallbackAll = MonsterPool.CollectPlayableCombatEncounters(pool, repo, bannedIds);
        if (fallbackAll.Count == 0 && bannedIds is { Count: > 0 })
            fallbackAll = MonsterPool.CollectPlayableCombatEncounters(pool, repo, bannedIds: null);

        var leftCurated = ResolveValidSlotCandidates(roster.LeftIds, effDay, EMonsterSlotTier.LeftBronze, pool, repo, bannedIds);
        var midCurated = ResolveValidSlotCandidates(roster.MidIds, effDay, EMonsterSlotTier.MidSilver, pool, repo, bannedIds);
        var rightCurated = ResolveValidSlotCandidates(roster.RightIds, effDay, EMonsterSlotTier.RightGold, pool, repo, bannedIds);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new List<CardPool.CardTemplate>(count);

        // 槽位 1：左青铜（最易）
        var left = PickCuratedWithVisitedPref(leftCurated, used, visitedTodayIds, rng)
                ?? MonsterPool.SelectSlotFallback(effDay, EMonsterSlotTier.LeftBronze, fallbackAll, used, repo, rng, visitedTodayIds, minLevel: 0, minHp: 0, preferStrictlyHigherHp: false);
        int leftLv = left != null ? MonsterPool.GetEffectiveMonsterLevel(left, repo) : 1;
        int leftHp = left != null ? MonsterPool.GetMonsterHealthMax(left, repo) : 75;
        var leftTier = left != null ? MonsterPool.GetMonsterTier(left) : BazaarCore.Models.CardTier.Bronze;
        if (left != null)
        {
            used.Add(left.Id);
            slots.Add(left);
        }

        if (slots.Count < count)
        {
            // 槽位 2：中白银（品阶、等级与血量均 >= 左槽，避免品阶或难度倒挂）
            var midMonotonic = midCurated
                .Where(c => !used.Contains(c.Id) &&
                            MonsterPool.GetMonsterTier(c) >= leftTier &&
                            MonsterPool.GetEffectiveMonsterLevel(c, repo) >= leftLv &&
                            MonsterPool.GetMonsterHealthMax(c, repo) >= leftHp)
                .ToList();
            var mid = PickCuratedWithVisitedPref(midMonotonic, used, visitedTodayIds, rng)
                   ?? PickCuratedWithVisitedPref(midCurated.Where(c => !used.Contains(c.Id) && MonsterPool.GetMonsterTier(c) >= leftTier).ToList(), used, visitedTodayIds, rng)
                   ?? MonsterPool.SelectSlotFallback(effDay, EMonsterSlotTier.MidSilver, fallbackAll, used, repo, rng, visitedTodayIds, minLevel: leftLv, minHp: leftHp, preferStrictlyHigherHp: false, minTier: leftTier)
                   ?? PickCuratedWithVisitedPref(midCurated, used, visitedTodayIds, rng);
            int midLv = mid != null ? MonsterPool.GetEffectiveMonsterLevel(mid, repo) : leftLv;
            int midHp = mid != null ? MonsterPool.GetMonsterHealthMax(mid, repo) : leftHp;
            var midTier = mid != null ? MonsterPool.GetMonsterTier(mid) : leftTier;
            if (mid != null)
            {
                used.Add(mid.Id);
                slots.Add(mid);
            }

            if (slots.Count < count)
            {
                // 槽位 3：右黄金（品阶 >= 中槽，等级 >= 中槽，血量严格 > 中槽优先）
                var rightStrict = rightCurated
                    .Where(c => !used.Contains(c.Id) &&
                                MonsterPool.GetMonsterTier(c) >= midTier &&
                                MonsterPool.GetEffectiveMonsterLevel(c, repo) >= midLv &&
                                MonsterPool.GetMonsterHealthMax(c, repo) > midHp)
                    .ToList();
                var rightGe = rightStrict.Count > 0
                    ? rightStrict
                    : rightCurated.Where(c => !used.Contains(c.Id) &&
                                              MonsterPool.GetMonsterTier(c) >= midTier &&
                                              MonsterPool.GetEffectiveMonsterLevel(c, repo) >= midLv &&
                                              MonsterPool.GetMonsterHealthMax(c, repo) >= midHp)
                                  .ToList();

                var right = PickCuratedWithVisitedPref(rightGe, used, visitedTodayIds, rng)
                         ?? PickCuratedWithVisitedPref(rightCurated.Where(c => !used.Contains(c.Id) && MonsterPool.GetMonsterTier(c) >= midTier).ToList(), used, visitedTodayIds, rng)
                         ?? MonsterPool.SelectSlotFallback(effDay, EMonsterSlotTier.RightGold, fallbackAll, used, repo, rng, visitedTodayIds, minLevel: midLv, minHp: midHp, preferStrictlyHigherHp: true, minTier: midTier)
                         ?? PickCuratedWithVisitedPref(rightCurated, used, visitedTodayIds, rng);
                if (right != null)
                {
                    used.Add(right.Id);
                    slots.Add(right);
                }
            }
        }

        // 兜底补足至 count 只（不重复，按接近当天等级补齐）
        if (slots.Count < count)
        {
            MonsterPool.FillMissingSlotsNearestDay(effDay, count, slots, used, fallbackAll, repo);
        }

        return slots
            .OrderBy(c => MonsterPool.GetMonsterTier(c))
            .ThenBy(c => MonsterPool.GetEffectiveMonsterLevel(c, repo))
            .ThenBy(c => MonsterPool.GetMonsterHealthMax(c, repo))
            .Take(count)
            .ToList();
    }

    private static List<CardPool.CardTemplate> ResolveValidSlotCandidates(
        IReadOnlyList<string> candidateIds,
        int day,
        EMonsterSlotTier slot,
        CardPool pool,
        GameDataRepository repo,
        ISet<string>? bannedIds)
    {
        var (minHp, maxHp) = GetRecommendedHealthBand(day, slot);
        var valid = new List<CardPool.CardTemplate>(candidateIds.Count);
        foreach (string id in candidateIds)
        {
            if (bannedIds != null && bannedIds.Contains(id)) continue;
            var tpl = pool.Get(id);
            if (!MonsterPool.IsPlayableCombatEncounter(tpl, pool, repo, requireAlwaysSpawning: true))
                continue;
            int hp = MonsterPool.GetMonsterHealthMax(tpl!, repo);
            if (hp < minHp || hp > maxHp) continue;
            valid.Add(tpl!);
        }
        return valid;
    }

    private static CardPool.CardTemplate? PickCuratedWithVisitedPref(
        IReadOnlyList<CardPool.CardTemplate> candidates,
        HashSet<string> used,
        ISet<string>? visitedTodayIds,
        Random rng)
    {
        var avail = candidates.Where(c => !used.Contains(c.Id)).ToList();
        if (avail.Count == 0) return null;

        if (visitedTodayIds is { Count: > 0 })
        {
            var unvisited = avail.Where(c => !visitedTodayIds.Contains(c.Id)).ToList();
            if (unvisited.Count > 0)
                avail = unvisited;
        }

        return avail[rng.Next(avail.Count)];
    }
}
