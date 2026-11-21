using BazaarCore.Data;
using BazaarCore.Models;
using BazaarServer.Game;

namespace BazaarServer.Data;

/// <summary>
/// Hour 3 PvE 野怪三槽位难度层级（左青铜 / 中白银 / 右黄金+）。
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
/// V13.1 官方权威每日（Day 1..12+）PvE 野怪调度表（100% 覆盖 GameData.db 全库 136 只可实战野怪）。
///
/// 权威映射规律（经 BazaarDB / Mobalytics / SteamDB Patch 14.0 与 GameData.db 三方核验）：
///   1. 标准公式（Day 2..10）：左 = Lv(D) Bronze / 中 = Lv(D+1) Silver / 右 = Lv(D+2) Gold+；
///   2. Day 1 特例：左 + 中从 4 只 Lv1 Bronze 中不重复抽取 2 只，右从 3 只 Lv2 Silver（Viper / Happy Camper / Haunted Kimono）抽取 1 只；
///   3. Day 11：左 = Lv11 Silver / 中 = Lv12 Gold / 右 = Lv13 Diamond+；Day 12+：左 = Lv12 Gold / 中 = Lv13 Diamond / 右 = Lv15 Legendary；
///   4. 废除误杀低血高阶怪（Viper 75HP / Drone Operator 850HP / Sergeant Suds 1200HP / Lich 1800HP）的伪血量带宽与伪单调血量过滤，
///      仅执行合法资产与实战校验，最终按槽位品阶与等级顺序展示。
/// </summary>
public static class OfficialDaySchedule
{
    private static readonly DayMonsterRoster _lateGameRoster = new(
        Day: 12,
        LeftIds:
        [
            "4ccdd72f-cfce-4ea6-9bda-caabd5b0fbf8", // Boss Harrow（Lv12 Gold, 2800HP）
            "58477f3f-70f1-4000-9ad0-9e0f44b42349", // Volkas Enforcer（Lv12 Gold, 3200HP）
            "e51634b6-ce99-4038-97e4-b9d86bdddae0", // Awakened Primordial（Lv12 Gold, 3600HP）
            "25f54463-2cca-419a-b0e8-8f5297896992", // Dooltron (Monster)（Lv12 Gold, 3600HP）
            "6727fa2f-f378-49c7-a839-86bac0fec9a6", // Outlands Terror (Monster)（Lv12 Gold, 3600HP）
            "27f9cc4c-3f9e-494a-a13d-99797114c53c", // Rex Spex (Monster)（Lv12 Gold, 3600HP）
        ],
        MidIds:
        [
            "265d1068-e90d-49c4-8013-ed2e856ead8f", // Property Baron（Lv13 Diamond, 3000HP）
            "921d3a60-82d7-445f-8ff0-0d4bccdc6c1e", // Annexed Army（Lv13 Diamond, 3600HP）
            "45de3316-8847-4321-ba1c-83a808bad369", // Frost Street Champion（Lv13 Diamond, 3675HP）
            "96c9ede1-34f3-442c-89d3-befbef196729", // Gibbus（Lv13 Diamond, 4000HP）
            "d3439b21-bde3-47cc-9f4e-d92987b942aa", // Mad Diver（Lv13 Diamond, 4000HP）
            "776c224d-1e4e-4a20-8516-550b94c05ff1", // Void Colossus（Lv13 Diamond, 4000HP）
        ],
        RightIds:
        [
            "fb2e242e-daf3-4771-bede-21b3adf03739", // Awakened District（觉醒街区, Lv15 Legendary, 4700HP）
            "8f73d2d6-6071-4464-bb19-8e0783de4df0", // Lord Arken（阿肯领主, Lv15 Legendary, 4700HP）
            "20863550-960f-4713-99eb-2ff2e4cfb3ef", // Morguloth（魔古洛斯, Lv15 Legendary, 4700HP）
            "568d3090-6703-4c42-bd65-ba50f883e869", // Void Golem（虚空魔像, Lv15 Legendary, 4700HP）
            "2048fa32-9254-4858-8ec4-ad2815977e0b", // Bananniboss（食蕉首领, Lv15 Legendary, 5500HP）
        ]);

    private static readonly IReadOnlyDictionary<int, DayMonsterRoster> _curatedRosters =
        new Dictionary<int, DayMonsterRoster>
        {
            [1] = new(
                Day: 1,
                LeftIds:
                [
                    "bd0711a6-d9d9-4a47-8a18-8f991057f7dd", // Kyver Drone（凯沃斯雄蜂, Lv1 Bronze, 75HP）
                    "1d24717f-7bfb-48e9-9320-6a69d72e233e", // Banannibal（蕉宝, Lv1 Bronze, 100HP）
                    "bb1e3506-3735-4669-be90-915a55a7ee05", // Fanged Inglet（獠牙英格狼, Lv1 Bronze, 100HP）
                    "45c473f5-25bd-4c1c-8357-cfe460c58f69", // Pyro（火灵, Lv1 Bronze, 100HP）
                ],
                MidIds:
                [
                    "bd0711a6-d9d9-4a47-8a18-8f991057f7dd", // Kyver Drone（Lv1 Bronze, 与左槽严格去重）
                    "1d24717f-7bfb-48e9-9320-6a69d72e233e", // Banannibal（Lv1 Bronze）
                    "bb1e3506-3735-4669-be90-915a55a7ee05", // Fanged Inglet（Lv1 Bronze）
                    "45c473f5-25bd-4c1c-8357-cfe460c58f69", // Pyro（Lv1 Bronze）
                ],
                RightIds:
                [
                    "b79845a7-f806-4e7a-9011-d98ebd22a323", // Viper（毒蛇, Lv2 Silver, 75HP）
                    "f6962ffe-efdf-4da2-9803-6671f42d9811", // Happy Camper（快乐露营者, Lv2 Silver, 100HP）
                    "035ace94-61e5-4343-8383-83044ea4e08e", // Haunted Kimono（怨念和服, Lv2 Silver, 100HP）
                ]),

            [2] = new(
                Day: 2,
                LeftIds:
                [
                    "289ff978-dd65-4484-b905-8ee16d28033c", // Coconut Crab（椰子蟹, Lv2 Bronze, 200HP）
                    "82a64f45-ca55-4a24-b1ef-18d41cc77fa5", // The Baron Boys（男爵兄弟, Lv2 Bronze, 200HP）
                    "1eabe08b-2949-407d-b22d-c6e579e0cd14", // Rogue Scrapper (Silver)（暴走破碎机, Lv2 Bronze, 300HP）
                ],
                MidIds:
                [
                    "66245d12-d5eb-4c08-9185-6ddaf626bbd3", // Covetous Thief (Silver)（贪婪窃贼, Lv3 Silver, 300HP）
                    "8cac11e0-0550-42be-bbfc-b18697e3369f", // Soda Swiller（汽水豪饮客, Lv3 Silver, 300HP）
                    "b93f5b44-ba9f-4196-a94e-c5de17ab5c5d", // Boarrior（蛮猪战士, Lv3 Silver, 350HP）
                ],
                RightIds:
                [
                    "dbf8eefa-99bd-475b-ad4d-91a9734518cf", // Calico（三花, Lv4 Gold, 450HP）
                    "41c5f103-2d76-4a51-ae28-ee294490b071", // Ventriloquist（腹语者, Lv4 Gold, 450HP）
                    "325c88c3-9496-44bf-b900-4adf1f84bf2f", // Eccentric Etherwright (Gold)（怪奇以太匠, Lv4 Gold, 600HP）
                ]),

            [3] = new(
                Day: 3,
                LeftIds:
                [
                    "8d8c7a87-404b-4bbd-82e1-3c6e3440bdd6", // Covetous Thief (Bronze)（贪婪窃贼, Lv3 Bronze, 300HP）
                    "8e389a21-9e14-49c4-bdf3-24183f9f9295", // Dabbling Apprentice（初学学徒, Lv3 Bronze, 300HP）
                    "6547a38e-d27f-4443-84bb-db8e04bae467", // Overwhelmed Student（不堪重负的学生, Lv3 Bronze, 300HP）
                    "04b60df4-91d9-4065-94e0-f9f462a3b104", // Tempest Bravo（焰风勇士, Lv3 Bronze, 300HP）
                ],
                MidIds:
                [
                    "85902df4-6926-40a3-affc-537c55e89b86", // Bloodreef Raider (D4 Bronze)（血礁奇兵, Lv4 Silver, 450HP）
                    "55046a2f-f81e-4720-aefa-e74b14a06dad", // Frost Street Challenger (Silver)（霜猴勇士, Lv4 Silver, 450HP）
                    "753ccc3c-726a-4b18-8726-621aedce54b6", // Sommelier（侍酒师, Lv4 Silver, 450HP）
                    "4101117c-9cb0-480e-bfac-814da069ec80", // Street Gamer（街头玩家, Lv4 Silver, 450HP）
                    "35d68424-87ba-4d8a-8776-8d8302a425f9", // Product Demonstrator（产品演示员, Lv4 Silver, 600HP）
                ],
                RightIds:
                [
                    "38936647-a688-49d1-8e5c-9c2c79f18a18", // Hoverbike Hooligan（飞车暴徒, Lv5 Gold, 650HP）
                    "c69bf8e8-944c-4b42-b59e-8ad568e0d3f7", // Giant Mosquito（巨蚊, Lv5 Gold, 700HP）
                    "e890b42d-43d7-4c4f-a03b-bef8f9d2e23d", // Castaway Corsair（漂流海盗, Lv5 Diamond, 700HP）
                    "be111234-2b9f-4e30-b0d6-368c49780faa", // Mutant Baker（变异烘焙师, Lv5 Diamond, 700HP）
                ]),

            [4] = new(
                Day: 4,
                LeftIds:
                [
                    "6129261a-c4e5-4ff9-b057-58ae1ef29bf7", // Bloodreef Raider（血礁奇兵, Lv4 Bronze, 450HP）
                    "4a4542cd-91d8-4463-84fc-c111fc7535ee", // Boilerroom Brawler（锅炉斗士, Lv4 Bronze, 450HP）
                    "91e59b0c-968c-4c45-9391-5104571fee28", // Outlands Dervish（荒原旋舞者, Lv4 Bronze, 450HP）
                    "6c45b720-a58c-43ba-a66e-e0ecfb1b69e2", // Overbearing Teacher（专横教师, Lv4 Bronze, 450HP）
                ],
                MidIds:
                [
                    "66e0b55f-022d-4c54-9558-ce7d077a01dd", // Eccentric Etherwright (Silver)（怪奇以太匠, Lv5 Silver, 600HP）
                    "9e637cb7-18cf-460d-9c50-5401ee4be521", // Retiree（退休人员, Lv5 Silver, 650HP）
                    "850d92a9-d1c5-4334-b046-4f2a1025b28d", // Wild Boar (monster)（野猪, Lv5 Silver, 650HP）
                    "51d0f501-2276-4b88-882b-6535ca6df1a8", // Worry Wart (monster)（忧虑疣蛙, Lv5 Silver, 650HP）
                ],
                RightIds:
                [
                    "b511bd4d-5ad6-4f54-bc74-89622da4d956", // Ahexa（阿海克萨, Lv6 Gold, 900HP）
                    "8eb6cf35-27ab-4726-b9f7-05133a4b94b8", // Hellbilly（地狱乡巴佬, Lv6 Gold, 900HP）
                    "5253c1ef-af51-43ac-a307-4e707e5747a6", // Prince Marianas（马力亚纳斯王子, Lv6 Gold, 900HP）
                ]),

            [5] = new(
                Day: 5,
                LeftIds:
                [
                    "ae118438-c54d-4129-98d1-b576e4a4d380", // Preening Duelist（精致决斗士, Lv5 Bronze, 650HP）
                    "47c042ba-707a-4e00-aea2-c1ed2ed880e5", // Sabretooth（剑齿虎, Lv5 Bronze, 650HP）
                    "de126a7a-a0bc-4a6f-96f0-442920104fcf", // Grandfather Klok（发条爷爷, Lv5 Bronze, 800HP）
                ],
                MidIds:
                [
                    "59917a2d-3253-44f4-aaa4-ca30b512eb59", // Harkuvian Rocket Trooper（哈库维火箭兵, Lv6 Silver, 900HP）
                    "a75621b9-11a2-4405-8254-0e58a300110f", // Infernal Envoy (Silver)（地狱使者, Lv6 Silver, 900HP）
                    "54bd46ca-2dec-4fbf-bae9-4cfff1c80043", // Kyver Commander（凯沃斯指挥官, Lv6 Silver, 900HP）
                ],
                RightIds:
                [
                    "6c84f0a7-4669-4eeb-bb0b-a0dce69e6a29", // Drone Operator（无人机操作员, Lv7 Gold, 850HP）
                    "dbc00a8b-7fae-4792-a9da-698d3cd4f578", // Gorgon Noble (Gold)（戈尔贡贵妇, Lv7 Gold, 1200HP）
                    "c472b821-e68b-4567-b7e1-3e63f67936b3", // Mod Squad（改装小队, Lv7 Gold, 1200HP）
                    "9c167a0c-d348-4e09-8f6b-be0ac65d3cda", // Trashtown Mayor（垃圾镇镇长, Lv7 Gold, 1200HP）
                    "cead2300-dce5-4c9c-82c4-e4c05d1923bf", // Flame Juggler (Diamond)（舞火大师, Lv7 Diamond, 1200HP）
                    "a2baf3bc-6164-4a16-ab41-48b450f694d1", // Foreman（领班, Lv7 Diamond, 1200HP）
                ]),

            [6] = new(
                Day: 6,
                LeftIds:
                [
                    "78139f32-94b1-4dc6-9ef0-374c3c8863a7", // Dire Inglet（英格恐狼, Lv6 Bronze, 900HP）
                    "0f15f62d-67c4-4564-91a3-c78a05ddce1d", // Infernal Envoy (Bronze)（地狱使者, Lv6 Bronze, 900HP）
                    "a9b27854-6f5c-4414-b458-90c54c444171", // Trash Golem（垃圾魔像, Lv6 Bronze, 900HP）
                    "7294ff99-ca2b-409e-838f-0efdb10dded0", // Zookeeper（动物饲养员, Lv6 Bronze, 900HP）
                ],
                MidIds:
                [
                    "962bf2b2-fc1b-4ad3-9526-15d76d3bb0de", // Foundation Weeper（基石泣妖, Lv7 Silver, 1350HP）
                    "4a6fcebd-0957-448a-8903-fe4a370a9f38", // Not a Spy（绝非间谍, Lv7 Silver, 1350HP）
                    "e8782aea-3511-4a9d-98d4-e25c62120547", // Sandwich Artist（三明治艺术家, Lv7 Silver, 1350HP）
                ],
                RightIds:
                [
                    "f9e17ce4-c82d-44c2-8406-ad33277a3b1c", // Sergeant Suds（皂沫中士, Lv8 Gold, 1200HP）
                    "bc5fa0a4-8ca9-4384-a283-9c7c84b27169", // Dire Mosquito（恶蚊, Lv8 Gold, 1500HP）
                    "d50543f3-132c-4c43-9230-50a5e96087d9", // Loan Shark（放贷鲨, Lv8 Gold, 1550HP）
                    "91c233ed-b88c-4ec9-b3ee-cec880a14213", // Scovyle（辣火狂徒, Lv8 Diamond, 1550HP）
                    "eccecbd7-1695-49a8-b82b-6e9e974c634a", // Slumcee（贫民说唱客, Lv8 Diamond, 1550HP）
                ]),

            [7] = new(
                Day: 7,
                LeftIds:
                [
                    "946eb8b4-7712-4377-b2b8-50d826b37d2f", // Unibou (monster)（独角驯, Lv7 Bronze, 1200HP）
                    "224ba8a9-3781-4403-8bfc-527b4e64b8aa", // Viper Tyrant（毒蛇暴君, Lv7 Bronze, 1200HP）
                ],
                MidIds:
                [
                    "f79e72dc-7f9b-4e7e-b14e-d4896fcf004f", // Ghost Pepper（幽灵辣椒, Lv8 Silver, 1700HP）
                    "25a7a800-1c64-4728-a4fe-eb5a3f3394a0", // Joyful Jack（快乐杰克南瓜, Lv8 Silver, 1700HP）
                    "7b768d49-b2cc-40f8-ab0f-2d0ed36a0c56", // Treasure Turtle (Silver)（载宝海龟, Lv8 Silver, 1700HP）
                ],
                RightIds:
                [
                    "e5e65711-035e-4e95-9017-4110aad06f9a", // Capital Punisher（资本惩罚者, Lv9 Gold, 1800HP）
                    "d9e56b4c-8ad2-465d-b44c-45a7f2562bde", // Annex Trooper（附属部队士兵, Lv9 Gold, 1925HP）
                    "23a21c0f-e163-4817-ba15-0e899d19d387", // Chilly Charles（寒霜查尔斯, Lv9 Gold, 1925HP）
                    "0ad744f1-6b36-4f7b-a5b7-a5f5e6b855a0", // Cortek Music Generator（科泰克音乐发生器, Lv9 Gold, 1925HP）
                    "7f95be65-d032-42d2-9e4b-ca2a579a380d", // Infernal (Gold)（地狱大魔神, Lv9 Gold, 1925HP）
                    "e3a8efa1-1dfc-4dd5-a1fe-7daaa8315e18", // Infernal (Diamond)（地狱大魔神, Lv9 Diamond, 1925HP）
                ]),

            [8] = new(
                Day: 8,
                LeftIds:
                [
                    "723f8e17-6885-45b4-b38f-186abb3277c8", // Cosmic Roc (Bronze)（宇宙鹏鸟, Lv8 Bronze, 1500HP）
                    "90aa768f-4a8d-4087-ad5a-5f44efe28e3a", // Wildman（野人, Lv8 Bronze, 1550HP）
                    "78c335a6-42bc-45e2-a8e7-e9a1dce7502d", // Cloudtop Officer（云顶军官, Lv8 Bronze, 1700HP）
                    "4b69c9fd-cf2d-4197-9b06-0327a5849041", // Stew（老炖, Lv8 Bronze, 1700HP）
                ],
                MidIds:
                [
                    "5fa789a5-211c-46ba-891f-7467f387e37c", // Infernal Frigate (Silver)（地狱飞艇, Lv9 Silver, 1800HP）
                    "a1c3df15-1058-44ff-bee9-0991cfba2068", // Bouncertron（门卫机甲, Lv9 Silver, 1925HP）
                    "76c8dc41-96c9-4c05-acb1-5eaab4ea8e17", // Car Conductor（驾驶督导, Lv9 Silver, 1925HP）
                    "e82f2b78-f445-4782-b35b-7d6d4058f3be", // Wolverine (monster)（狼獾, Lv9 Silver, 1925HP）
                ],
                RightIds:
                [
                    "3836d71e-c6ea-4ef7-a9c7-ce698001e991", // Burninator Bot（焚毁机器人, Lv10 Gold, 2325HP）
                    "93d7bb74-2d23-4ccc-a99f-9380afaf738a", // Dr. Vortex（漩涡博士, Lv10 Gold, 2325HP）
                    "87ab01e1-55a7-4934-8201-b005b540616f", // Oasis Guardian (Gold)（绿洲守护神, Lv10 Gold, 2325HP）
                    "2b0437f4-6977-4f84-aeb6-6545cbf5632d", // Thug（暴徒, Lv10 Diamond, 2550HP）
                    "a04b33a7-eb29-456a-9a7c-63dd8553b872", // Wandering Shoal (Diamond)（流浪暗礁, Lv10 Diamond, 2550HP）
                    "69af3106-78fd-4a74-b150-ec41442cdbba", // Lich（巫妖, Lv10 Legendary, 1800HP）
                ]),

            [9] = new(
                Day: 9,
                LeftIds:
                [
                    "7590dbeb-8087-4464-90f0-96aeee910186", // Bloodreef Captain（血礁船长, Lv9 Bronze, 1925HP）
                    "338f3c4b-9238-41d2-a0e0-0d825cded307", // Elite Duelist（精英决斗者, Lv9 Bronze, 1925HP）
                    "2c059647-c6ca-49ae-ac76-80eadad3fb77", // Surly Mechanic（粗鲁机械师, Lv9 Bronze, 1925HP）
                    "f4bc9c3d-ce22-4fd8-9b7f-27214fed9f1d", // Cloudtop Admiral（云顶海军上将, Lv9 Bronze, 2100HP）
                ],
                MidIds:
                [
                    "852c1d17-e82f-49b6-ade5-59328d5b2e99", // Anaconda (monster)（巨蟒, Lv10 Silver, 2325HP）
                    "0b3c79ba-ff7f-40e6-b10d-98ab71716304", // Oasis Guardian (Silver)（绿洲守护神, Lv10 Silver, 2325HP）
                    "254b6aca-26a9-4014-86c3-0da827519c73", // Roaming Isle（流浪岛屿, Lv10 Silver, 2325HP）
                    "3e80e4e9-13e6-441c-b770-a82a0297ad47", // Terrorform（恐怖弗尔姆, Lv10 Silver, 2325HP）
                    "639c5575-5078-4ff8-8100-e73dce6f5400", // Wandering Shoal (Silver)（流浪暗礁, Lv10 Silver, 2550HP）
                ],
                RightIds:
                [
                    "21a485af-8811-4d38-bccb-be03a2dcca7f", // Ferros Khan (Gold)（菲罗斯汗, Lv11 Gold, 2750HP）
                    "d63f985a-2960-408e-97ea-f023d4afd440", // Weapons Platform（武器平台, Lv11 Gold, 2750HP）
                    "b988bbe5-35cf-445e-bea5-ec54b8daf54a", // Aleppa Tantius（阿莱帕·坦提乌斯, Lv11 Diamond, 2500HP）
                    "a44c3d18-8968-4956-b3a2-5cbf3b4c12c2", // Yerdan（耶丹, Lv11 Diamond, 2750HP）
                ]),

            [10] = new(
                Day: 10,
                LeftIds:
                [
                    "74173ad7-8fea-442d-a7ec-ba0b604629c9", // Bloodreef Captain (Bronze)（血礁船长, Lv10 Bronze, 1925HP）
                    "e54908bc-bf2d-4aac-93c3-393be464558a", // Fairy Circle（仙女环, Lv10 Bronze, 2325HP）
                    "492c7f84-3cb2-40bb-8a55-29355143bee2", // Hulking Experiment（庞大实验体, Lv10 Bronze, 2325HP）
                    "a871d83c-fbe0-4b38-b004-6cc598b31239", // Master Alchemist（炼金大师, Lv10 Bronze, 2325HP）
                    "c7405d35-b9fd-49bb-8dcd-a412d7cb9022", // Ferros Khan (Bronze)（菲罗斯汗, Lv10 Bronze, 2750HP）
                ],
                MidIds:
                [
                    "64d6f9df-9003-44f8-bad6-ae9f81f1f7bf", // Industry Plant（工业造星, Lv11 Silver, 2750HP）
                    "fc59e9b3-88be-450c-b2dc-65d58a33d1d0", // Lord of the Wastes（废土领主, Lv11 Silver, 2750HP）
                    "1e1f7b6d-746b-44d5-9906-fff137c75ef1", // Trash Titan（垃圾泰坦, Lv11 Silver, 2750HP）
                    "3c433f32-5070-4f57-82d3-9b4347da7aa3", // Infernal Dreadnought（地狱空艇, Lv11 Silver, 3050HP）
                    "2b68a81d-ea7e-401b-a374-77529470383f", // Tortuga (Monster)（巨龟托图加, Lv11 Silver, 3050HP）
                ],
                RightIds:
                [
                    "4ccdd72f-cfce-4ea6-9bda-caabd5b0fbf8", // Boss Harrow（哈洛老大, Lv12 Gold, 2800HP）
                    "58477f3f-70f1-4000-9ad0-9e0f44b42349", // Volkas Enforcer（沃卡斯打手, Lv12 Gold, 3200HP）
                    "e51634b6-ce99-4038-97e4-b9d86bdddae0", // Awakened Primordial（觉醒元初体, Lv12 Gold, 3600HP）
                    "25f54463-2cca-419a-b0e8-8f5297896992", // Dooltron (Monster)（多尔王, Lv12 Gold, 3600HP）
                    "6727fa2f-f378-49c7-a839-86bac0fec9a6", // Outlands Terror (Monster)（荒原恐兽, Lv12 Gold, 3600HP）
                    "27f9cc4c-3f9e-494a-a13d-99797114c53c", // Rex Spex (Monster)（机甲暴龙, Lv12 Gold, 3600HP）
                    "fc3dee8e-aef5-41c9-95a8-d91ad54adede", // Foundation Revenant（基石亡灵, Lv12 Diamond, 3200HP）
                    "4cebbbdc-db0c-4159-b651-1bb79c264bc9", // Lost Hero（迷失英雄, Lv12 Diamond, 3200HP）
                    "608eb88e-c6d8-41c3-ac88-c20aeb185664", // Radiant Corsair（辉耀海盗, Lv12 Legendary, 3200HP）
                    "b3c0c6f2-4f33-48cd-933f-53a54751d92b", // Byg（拜格, Lv12 Legendary, 4000HP）
                ]),

            [11] = new(
                Day: 11,
                LeftIds:
                [
                    "64d6f9df-9003-44f8-bad6-ae9f81f1f7bf", // Industry Plant（Lv11 Silver, 2750HP）
                    "fc59e9b3-88be-450c-b2dc-65d58a33d1d0", // Lord of the Wastes（Lv11 Silver, 2750HP）
                    "1e1f7b6d-746b-44d5-9906-fff137c75ef1", // Trash Titan（Lv11 Silver, 2750HP）
                    "3c433f32-5070-4f57-82d3-9b4347da7aa3", // Infernal Dreadnought（Lv11 Silver, 3050HP）
                    "2b68a81d-ea7e-401b-a374-77529470383f", // Tortuga (Monster)（Lv11 Silver, 3050HP）
                ],
                MidIds:
                [
                    "4ccdd72f-cfce-4ea6-9bda-caabd5b0fbf8", // Boss Harrow（Lv12 Gold, 2800HP）
                    "58477f3f-70f1-4000-9ad0-9e0f44b42349", // Volkas Enforcer（Lv12 Gold, 3200HP）
                    "e51634b6-ce99-4038-97e4-b9d86bdddae0", // Awakened Primordial（Lv12 Gold, 3600HP）
                    "25f54463-2cca-419a-b0e8-8f5297896992", // Dooltron (Monster)（Lv12 Gold, 3600HP）
                    "6727fa2f-f378-49c7-a839-86bac0fec9a6", // Outlands Terror (Monster)（Lv12 Gold, 3600HP）
                    "27f9cc4c-3f9e-494a-a13d-99797114c53c", // Rex Spex (Monster)（Lv12 Gold, 3600HP）
                ],
                RightIds:
                [
                    "265d1068-e90d-49c4-8013-ed2e856ead8f", // Property Baron（地产大亨, Lv13 Diamond, 3000HP）
                    "921d3a60-82d7-445f-8ff0-0d4bccdc6c1e", // Annexed Army（已被统合的军队, Lv13 Diamond, 3600HP）
                    "45de3316-8847-4321-ba1c-83a808bad369", // Frost Street Champion（霜猴霸王, Lv13 Diamond, 3675HP）
                    "96c9ede1-34f3-442c-89d3-befbef196729", // Gibbus（月之主, Lv13 Diamond, 4000HP）
                    "d3439b21-bde3-47cc-9f4e-d92987b942aa", // Mad Diver（疯狂潜水员, Lv13 Diamond, 4000HP）
                    "776c224d-1e4e-4a20-8516-550b94c05ff1", // Void Colossus（虚空巨像, Lv13 Diamond, 4000HP）
                    "d0202706-46dc-4c65-bf87-5e4ab63632ec", // Dragon（巨龙, Lv13 Legendary, 3675HP）
                    "be75b034-f986-4cdd-b871-5e8258865c29", // Void Knight（虚空骑士, Lv13 Legendary, 3675HP）
                    "96349563-bfb5-46ef-8dff-dbf1ff0d4196", // Eight Arm Davvy (Monster)（八臂达维, Lv13 Legendary, 8888HP）
                ]),
            [12] = _lateGameRoster,
        };

    /// <summary>Day 1..12 的官方三槽 PvE 精选怪表。</summary>
    public static IReadOnlyDictionary<int, DayMonsterRoster> CuratedDayRosters => _curatedRosters;

    /// <summary>Day 12+ 后期/加时天数的官方 PvE 精选怪表。</summary>
    public static DayMonsterRoster LateGameRoster => _lateGameRoster;

    /// <summary>获取指定天数对应的官方 PvE 精选名册（Day &lt;= 1 返回 Day 1，Day &gt;= 12 返回 LateGameRoster）。</summary>
    public static DayMonsterRoster GetRosterForDay(int day)
    {
        if (day <= 1) return _curatedRosters[1];
        if (_curatedRosters.TryGetValue(day, out var roster)) return roster;
        return _lateGameRoster;
    }

    /// <summary>
    /// 返回指定天数与槽位在官方新规律（Lv(D) Bronze / Lv(D+1) Silver / Lv(D+2) Gold+）下的参考血量区间
    /// （仅作统计参考，不再作为拦截官方合法低血高阶怪的硬门槛）。
    /// </summary>
    public static (int MinHp, int MaxHp) GetRecommendedHealthBand(int day, EMonsterSlotTier slot)
    {
        int d = Math.Max(1, day);
        return (d, slot) switch
        {
            (1, EMonsterSlotTier.LeftBronze) => (75, 100),
            (1, EMonsterSlotTier.MidSilver) => (75, 100),
            (1, EMonsterSlotTier.RightGold) => (75, 100),

            (2, EMonsterSlotTier.LeftBronze) => (200, 300),
            (2, EMonsterSlotTier.MidSilver) => (300, 350),
            (2, EMonsterSlotTier.RightGold) => (450, 600),

            (3, EMonsterSlotTier.LeftBronze) => (300, 300),
            (3, EMonsterSlotTier.MidSilver) => (450, 600),
            (3, EMonsterSlotTier.RightGold) => (650, 700),

            (4, EMonsterSlotTier.LeftBronze) => (450, 450),
            (4, EMonsterSlotTier.MidSilver) => (600, 650),
            (4, EMonsterSlotTier.RightGold) => (900, 900),

            (5, EMonsterSlotTier.LeftBronze) => (650, 800),
            (5, EMonsterSlotTier.MidSilver) => (900, 900),
            (5, EMonsterSlotTier.RightGold) => (850, 1200),

            (6, EMonsterSlotTier.LeftBronze) => (900, 900),
            (6, EMonsterSlotTier.MidSilver) => (1350, 1350),
            (6, EMonsterSlotTier.RightGold) => (1200, 1550),

            (7, EMonsterSlotTier.LeftBronze) => (1200, 1200),
            (7, EMonsterSlotTier.MidSilver) => (1700, 1700),
            (7, EMonsterSlotTier.RightGold) => (1800, 1925),

            (8, EMonsterSlotTier.LeftBronze) => (1500, 1700),
            (8, EMonsterSlotTier.MidSilver) => (1800, 1925),
            (8, EMonsterSlotTier.RightGold) => (1800, 2550),

            (9, EMonsterSlotTier.LeftBronze) => (1925, 2100),
            (9, EMonsterSlotTier.MidSilver) => (2325, 2550),
            (9, EMonsterSlotTier.RightGold) => (2500, 2750),

            (10, EMonsterSlotTier.LeftBronze) => (1925, 2750),
            (10, EMonsterSlotTier.MidSilver) => (2750, 3050),
            (10, EMonsterSlotTier.RightGold) => (2800, 4000),

            (11, EMonsterSlotTier.LeftBronze) => (2750, 3050),
            (11, EMonsterSlotTier.MidSilver) => (2800, 3600),
            (11, EMonsterSlotTier.RightGold) => (3000, 8888),

            (_, EMonsterSlotTier.LeftBronze) => (2800, 3600),
            (_, EMonsterSlotTier.MidSilver) => (3000, 4000),
            _ => (4700, 5500),
        };
    }

    /// <summary>
    /// 按官方权威 Day 1..12+ 每日名册选出 3 只不重复的 PvE 野怪遭遇：
    ///   · Day 1：2 只不重复 Lv1 Bronze + 1 只 Lv2 Silver（Viper / Happy Camper / Haunted Kimono）；
    ///   · Day 2..10：左 Lv(D) Bronze / 中 Lv(D+1) Silver / 右 Lv(D+2) Gold+；
    ///   · Day 11：Lv11 Silver / Lv12 Gold / Lv13 Diamond+；Day 12+：Lv12 Gold / Lv13 Diamond / Lv15 Legendary。
    /// 若任一槽位因自定义禁用或精简数据库无可选项，则平滑回退至 <see cref="MonsterPool"/> 按官方等级公式补齐。
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

        // 懒加载全库可实战 PvE 野怪回退池：当精选三槽均命中时实现零全表扫描
        List<CardPool.CardTemplate>? fallbackCache = null;
        List<CardPool.CardTemplate> GetFallbackAll()
        {
            if (fallbackCache != null) return fallbackCache;
            fallbackCache = MonsterPool.CollectPlayableCombatEncounters(pool, repo, bannedIds);
            if (fallbackCache.Count == 0 && bannedIds is { Count: > 0 })
                fallbackCache = MonsterPool.CollectPlayableCombatEncounters(pool, repo, bannedIds: null);
            return fallbackCache;
        }

        var leftCurated = ResolveValidSlotCandidates(roster.LeftIds, pool, repo, bannedIds);
        var midCurated = ResolveValidSlotCandidates(roster.MidIds, pool, repo, bannedIds);
        var rightCurated = ResolveValidSlotCandidates(roster.RightIds, pool, repo, bannedIds);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new List<CardPool.CardTemplate>(count);

        // 槽位 1：左槽（Day 1..10 为 Lv(D) Bronze，Day 11 为 Lv11 Silver，Day 12+ 为 Lv12 Gold）
        var left = PickCuratedWithVisitedPref(leftCurated, used, visitedTodayIds, rng)
                ?? MonsterPool.SelectSlotFallback(effDay, EMonsterSlotTier.LeftBronze, GetFallbackAll(), used, repo, rng, visitedTodayIds, minLevel: 0, minHp: 0, preferStrictlyHigherHp: false);
        var leftTier = left != null ? MonsterPool.GetMonsterTier(left) : CardTier.Bronze;
        int leftLv = left != null ? MonsterPool.GetEffectiveMonsterLevel(left, repo) : effDay;
        if (left != null)
        {
            used.Add(left.Id);
            slots.Add(left);
        }

        if (slots.Count < count)
        {
            // 槽位 2：中槽（Day 1 为第 2 只不重复 Lv1 Bronze，Day 2..10 为 Lv(D+1) Silver，Day 11 为 Lv12 Gold，Day 12+ 为 Lv13 Diamond）
            var mid = PickCuratedWithVisitedPref(midCurated, used, visitedTodayIds, rng)
                   ?? MonsterPool.SelectSlotFallback(effDay, EMonsterSlotTier.MidSilver, GetFallbackAll(), used, repo, rng, visitedTodayIds, minLevel: leftLv, minHp: 0, preferStrictlyHigherHp: false, minTier: leftTier);
            var midTier = mid != null ? MonsterPool.GetMonsterTier(mid) : leftTier;
            int midLv = mid != null ? MonsterPool.GetEffectiveMonsterLevel(mid, repo) : leftLv;
            if (mid != null)
            {
                used.Add(mid.Id);
                slots.Add(mid);
            }

            if (slots.Count < count)
            {
                // 槽位 3：右槽（Day 1 为 Lv2 Silver，Day 2..10 为 Lv(D+2) Gold+，Day 11 为 Lv13 Diamond+，Day 12+ 为 Lv15 Legendary）
                // 彻底废除误杀低血高阶怪（Viper 75HP / Drone Operator 850HP / Sergeant Suds 1200HP / Lich 1800HP）的伪血量递增约束
                var right = PickCuratedWithVisitedPref(rightCurated, used, visitedTodayIds, rng)
                         ?? MonsterPool.SelectSlotFallback(effDay, EMonsterSlotTier.RightGold, GetFallbackAll(), used, repo, rng, visitedTodayIds, minLevel: midLv, minHp: 0, preferStrictlyHigherHp: false, minTier: midTier);
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
            MonsterPool.FillMissingSlotsNearestDay(effDay, count, slots, used, GetFallbackAll(), repo);
        }

        // 按槽位官方难度顺序（品阶升序 → 等级升序）排列展示，不再按血量重排打乱高阶低血怪的右槽位置
        return slots
            .OrderBy(c => MonsterPool.GetMonsterTier(c))
            .ThenBy(c => MonsterPool.GetEffectiveMonsterLevel(c, repo))
            .Take(count)
            .ToList();
    }

    private static List<CardPool.CardTemplate> ResolveValidSlotCandidates(
        IReadOnlyList<string> candidateIds,
        CardPool pool,
        GameDataRepository repo,
        ISet<string>? bannedIds)
    {
        var valid = new List<CardPool.CardTemplate>(candidateIds.Count);
        foreach (string id in candidateIds)
        {
            if (bannedIds != null && bannedIds.Contains(id)) continue;
            var tpl = pool.Get(id);
            if (!MonsterPool.IsPlayableCombatEncounter(tpl, pool, repo, requireAlwaysSpawning: true))
                continue;
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
