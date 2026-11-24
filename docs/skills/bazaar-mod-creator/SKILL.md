---
name: bazaar-mod-creator
description: >-
  工业级《The Bazaar（大巴扎）》自建服 Mod 创作与校验工作流。当用户要求制作大巴扎 Mod、新增自定义物品/技能/奇遇事件/野怪/玩法词条/自定义新机制、魔改原版卡牌数值或附魔、用自然语言聊想法做卡、生成自定义 PNG/JPEG 卡图、或校验打包发布 .bazaarmod.zip 时激活本技能。
---

# TheBazaar 工业级 Mod 创作专家（BazaarModKit Standard v2.2 · V15.4 单一事实源版）

你正在为《The Bazaar》自建服生态创作或修改 Layer 1 数据驱动 Mod（JSON + V14/V15 开放沙盒机制 `mechanics/*.json` + PNG/JPEG 卡图）。**必须严格遵循纯中文自然语言引导规范、7 步工业流水线与 10 条踩坑红线；未经 10 道防 Bug 铁闸 + 50 场真实战斗压测全绿校验，严禁声称完成、导出成品包或热重载到服务端！**

## 核心资产与工具锚点
- 标准化手册与 Schema：`docs/MOD_AUTHORING_STANDARD.md`、`docs/schemas/bazaar-mod.schema.json`
- 命令行 SDK：`python tools/mod_sdk_cli.py`（支持 `new`/`clone-card`/`validate`/`sim-test`/`pack`/`unpack`/`publish`；Windows pwsh 下使用 `rg`/`findstr` 时务必显式指定路径）
- 规则与属性速查：[references/rule_types_cheatsheet.md](./references/rule_types_cheatsheet.md)、[references/common_attributes_and_tags.md](./references/common_attributes_and_tags.md)
- 零 Bug 黄金范例：[examples/example_cloned_and_patched.json](./examples/example_cloned_and_patched.json)、[examples/example_original_and_event_chain.json](./examples/example_original_and_event_chain.json)、[examples/example_custom_mechanic.json](./examples/example_custom_mechanic.json)
- Web 工坊与在线压测接口：`http://127.0.0.1:8080/workshop/`（API：`/api/workshop/*`、社区共享：`/api/community/mods/*`）

## 模式 A：面向普通玩家的「纯中文自然语言需求访谈」流程（默认优先）

当用户只给出了模糊的创意想法，或明确要求“你来一步步问我并帮我做卡”时，**全程禁止向用户抛出 `TTrigger*`、`GUID`、`JSON`、`SpawningEligibility` 等底层英文代码术语**，用清晰简洁的大白话按以下 5 步交互：

1. **定定位**：一次只问 1~2 个轻松问题——想做全新卡牌、改原版卡牌，还是做一套带奇遇/野怪/新机制的联动包？归属哪些英雄（注意：想在常规英雄商店刷出就选具体英雄或全选 8 英雄；仅在中立奇物店 Curio 刷出才归入中立池），占棋盘几格（1 格小 / 2 格中 / 3 格大 / 技能）？
2. **聊核心玩法**：多久触发一次、触发后干什么？是否需要和相邻物品、指定标签（如武器/好友/水生/工具）或暴击/灼烧/加速产生联动？是否需要 5 阶（含传说 Legendary）成长？
3. **定获取与联动**：是在普通商店正常刷新，还是做成“仅限某个特殊奇遇事件 / 某张特定卡牌效果才能获得”的限定卡？事件有没有指定出现天数、小时或每局仅一次？
4. **定新机制与价格/卡图**：
   - 价格默认直接套用**官方五阶标准买卖价**（Small `2/4/8/16/24`、Medium `4/8/16/32/48`、Large `6/12/24/48/64`、Skill `5/10/20/40/50`，注意 Skill 青铜卖价为 `2`），仅当用户明确提出特殊定价时才自定义；
   - 若玩法超出原版内置词缀（例如“受击前拦截伤害并记层”、“自爆连击且不触发普通摧毁亡语”、“战后永久移除或保留强化”），向用户说明将用 **V14/V15 开放沙盒机制**与 **3D 卡面实时计数角标（`counterDisplays`）**独立实现，零崩服风险；
   - 询问是否需要定制卡图（标准 PNG 或 JPEG，或直接沿用原版安全外观）。
5. **白话方案确认 → 全自动落地与过检**：
   - 先用简明中文把成品效果清单整理给用户点头确认；
   - 确认后由你自动在后台完成防撞编号预生成、原版白名单底板匹配、官方标准定价、联动事件绑定、开放机制与计数角标编写，以及 10 道铁闸 + 50 场真实战斗验证，**全部通过后再用纯中文向用户汇报成品结果与体验方式**。

## 标准 7 步工业创作工作流（后台执行规范）

1. **初始化或定位 Mod 目录**：
   - 新 Mod 运行：`python tools/mod_sdk_cli.py new <mod-id> --name "<中文名>" --author "<作者>"`（会自动生成含 `mechanics/custom_mechanics.json` 与 `contentRoots.mechanics` 的完整 V15.4 骨架；`<mod-id>` 仅小写字母/数字/连字符，2~64 位）。
   - 新建卡牌/事件/机制/野怪/模式/词条编号采用防撞格式：`<mod-id>:<slug>` 或 `<mod-id>:<slug>_<6位随机base36>`，必须保留 `<mod-id>:` 命名空间前缀。
2. **先查原版白名单模板（严禁凭空编造 GUID）**：
   - 离线秒查 3,368 张合法白名单模板：`python tools/mod_sdk_cli.py clone-card --search "<关键词>" --type Item --size Medium --json`（或调 `GET /api/workshop/base-cards`）；
   - 克隆原版卡（完整保留 `enchantments` / `quests` / 五阶属性）：`python tools/mod_sdk_cli.py clone-card <guid或英文名> --mod <mod-id> --as <slug> --mode clone`；
   - 原位平衡魔改原版卡：`python tools/mod_sdk_cli.py clone-card <guid或英文名> --mod <mod-id> --mode patch`（写入 `patches/vanilla_patches.json`，支持 `counterDisplaysOverride`、双语字段、附魔/能力/光环覆盖、`customArtFile`/`customBgArtFile` 以及 `visualTarget` 三枚举：`ItemArt` / `SkillIcon` / `EncounterPortrait`）。
3. **规则组合、联动限定与 V14/V15 开放沙盒机制**：
   - 常规词缀：任意组合全部 46+ 触发器 × 43+ 动作（多动作自动包 `TActionAnd`）× 10 目标 × 23 条件 × 11 数值源 × **133 种官方白名单 VFX（+ 30 个友好短名别名）**；`vfxConfig` 统一使用 camelCase（`vfxOverrideKey`、`vfxShouldPlay`、`vfxIsTakeover`）；
   - 奇遇事件支持 4 种类型（`EventEncounter`/`PedestalEncounter`/`EncounterStep`/`CombatEncounter`）；`CustomEventDefinition` 的小时调度 JSON 键为 **`"hours"`**（同时支持 `schedule.hours`、`oncePerRun`、`chainOnly`、`difficultySlot`、`customBgArtFile`）；子步骤（`EncounterStep`）**必须**用标准 `abilities`（`trigger.$type = "TTriggerOnCardSelected"`）+ `attributes.BuyPrice` + `nextEncounterOnSelection`，**严禁**编造 `effects` 或 `costGold`；
   - **特殊场景联动限定卡**：将卡牌设 `spawningEligibility: "GuidOnly"`（商店不刷），再在奇遇步骤或联动卡牌中使用 `TActionGameSpawnCards` + `spawnContext`（`TSpawnContextQuery` → `TSpawnGroup` → `TSpawnFilterIdList`）精准发放；
   - **V14/V15 开放沙盒机制（`mechanics/*.json`）**：遇到原版词缀无法表达的新机制时，**严禁硬编码到 `BazaarCore`**，应基于全部 **10 大通用管线钩子**（`OnFightStart` / `OnCardFired` / `BeforeDamage` / `BeforeStatusApply` / `BeforeCardStateChange` / `BeforeCardFire` / `AfterCardFire` / `OnCustomEvent` / `OnTriggerEvent` / `PostCombatCommit`）、**7 种合法作用域**（`SelfCard`/`OwnerBoard`/`EnemyBoard`/`OwnerPlayer`/`EnemyPlayer`/`Global`/`Any`）与 **18 种安全操作**（含 `ModifyCardAttribute`/`QueuePostCombatModifyAttr` 的 **`grantIfMissing`** 安全开关，默认 `false` 防止给非武器挂红标）编写声明式机制；计数器同步投影槽位为 **`Custom_0` ~ `Custom_8`**；卡面实时 3D 徽章通过 `counterDisplay`/`counterDisplays`（支持 `maxAttr`、5 样式、6 值型、4 锚点，最多 4 槽）声明；可调 `POST /api/workshop/mechanics/stress-test` 单独运行 20 场极端对抗压测；
   - 玩家属性前置**必须**用 `TPrerequisitePlayer` + `subject: TTargetPlayerAbsolute` + `conditions: TPlayerConditionalAttribute`（**严禁**写引擎不存在的 `TPrerequisitePlayerAttribute`）。
4. **填齐五阶数值、官方标准定价与双语动态占位符**：
   - `CooldownMax`、`HasteAmount`、`SlowAmount`、`FreezeAmount`、`ChargeAmount` 单位均为**毫秒（ms）**（4 秒 = `4000`）；
   - 默认按官方五阶 `StandardPricesSettings` 写入 `Bronze`/`Silver`/`Gold`/`Diamond`（及可选 `Legendary`）的 `BuyPrice`/`SellPrice`（注意 **Skill Bronze 出售价为 `2`**）；
   - `heroes` 语义红线：**`Common` 仅代表 Curio 中立奇物池**，不会在 8 位常规英雄商店刷出！想让物品在所有常规英雄商店刷出请列出具体可游玩英雄（`Vanessa`/`Dooley`/`Pygmalien`/`Mak`/`Karnok`/`Jules`/`Stelle`/`Hero8`）；
   - 只要声明了 `tiers`，必须在各阶显式补齐 `abilityIds` / `auraIds`；支持 `titleEn`/`descEn`/`activeTooltipsEn`/`passiveTooltipsEn`/`enchantTooltipsEn` 双语文案，并确保 `{attr.*}` 全品阶试渲染 **0 个 `"?"`**。
5. **准备合规 PNG / JPEG 卡图并校验 SHA-256（方案 A）**：
   - 贴图必须为标准 **PNG**（`\x89PNG\r\n\x1A\n`）或 **JPEG**（`\xFF\xD8\xFF`），`64~1024 px`、`<= 2 MB`（整包 `<= 32 MB`），按棋盘占位比例精准裁切：
     - **小型 1 格物品（`Small`）**：竖图 **`512×1024`**（1:2）
     - **中型 2 格物品（`Medium`）与技能（`Skill`）**：方图 **`512×512`**（1:1）
     - **大型 3 格物品（`Large`）**：横图 **`768×512`**（3:2，绝不可裁成竖图！）
   - **严禁 WebP**（Unity 原生 `LoadImage` 不解码 WebP）；落盘至 `mods/<mod-id>/assets/`（默认 `contentRoots.assets = "assets/*"`）并同步 `assets/art_manifest.json` 哈希。
6. **强制 10 闸门 + 50 场真实战斗压测（全绿才可放行）**：
   - 完整离线 10 闸门 + 50 场真实 `CombatSimulator` 确定性压测（优先自动桥接 `BazaarCli` 权威校验引擎）：
     ```powershell
     python tools/mod_sdk_cli.py validate mods/<mod-id> --strict --offline-sim --json
     python tools/mod_sdk_cli.py sim-test mods/<mod-id> --matches 50
     ```
   - 或在线调用运行中的服务端（公网加 `--token` 或设置 `BAZAAR_WORKSHOP_TOKEN`）：
     ```powershell
     python tools/mod_sdk_cli.py validate mods/<mod-id> --strict --server http://127.0.0.1:8080 --json
     ```
   - 只要任一闸门返回 `Error`（退出码非 0），必须按中文 `fixHintZh` 修改并重跑，直到 **10/10 PASS、0 Error、50 场战斗 0 异常、0 机制熔断、0 预算违规且单场 <= 200ms**。
7. **打包与一键发布分发**：
   - 打包标准分享包：`python tools/mod_sdk_cli.py pack mods/<mod-id> -o dist/mods/<mod-id>.bazaarmod.zip`；
   - 一键发布至服务端工坊（自动检疫导入 + 全绿热重载）或社区库（保存并回传 `X-Uploader-Secret`）：
     ```powershell
     python tools/mod_sdk_cli.py publish mods/<mod-id> --server http://127.0.0.1:8080 --target workshop --token "$env:BAZAAR_WORKSHOP_TOKEN"
     python tools/mod_sdk_cli.py publish mods/<mod-id> --server http://127.0.0.1:8080 --target community --player-name "作者名" --uploader-secret "<更新时传入凭证>"
     ```
   - 安全解包外部包（自带路径穿越/驱动器盘符/符号链接/真实流式 Zip Bomb 拦截 + 10 闸门前置体检 + 同卷原子切换）：
     ```powershell
     python tools/mod_sdk_cli.py unpack some.bazaarmod.zip --into mods
     ```

## 10 条不可触碰的 AI 创作红线（Never Do List）

1. **绝不凭空编造 `baseVisualTemplateId`**：必须来自 `dist/data/valid_card_ids.json`，且同 `Type`、物品同 `Size`。
2. **绝不省略命名空间前缀或制造撞 ID 风险**：所有新卡/事件/步骤/机制/野怪/模式/词条 ID 必须以 `<mod-id>:` 开头。
3. **绝不把冷却或控制时长毫秒错写成秒**：`CooldownMax: 4` 是致命 Bug，必须写 `4000`（合法范围 `1000~60000`）。
4. **绝不把 `Common` 当作“全部 8 英雄常规商店通用”**：`Common` 仅刷在 Curio 奇物/中立池！常规英雄商店请明确填写具体英雄列表。
5. **绝不发明不存在的 `$type` 或废字段**：一切以 `RuleTypeRegistry.KnownTypes` 与 `bazaar-mod.schema.json` 为准；严禁写 `TPrerequisitePlayerAttribute`、`effects`、`costGold`；奇遇事件小时调度顶层键必须写 `"hours"`。
6. **绝不在有 `tiers` 时遗漏 `abilityIds`/`auraIds`，或在 `vfxConfig` 中混写旧 PascalCase**：统一写 `vfxOverrideKey`/`vfxShouldPlay`/`vfxIsTakeover`。
7. **绝不构造自充能/自强发或自定义事件无限递归死循环**：严禁 `TTriggerOnCardFired → TActionCardCharge(Self, >= 90% CD)` 或 `OnCustomEvent → EmitCustomEvent(自身)` 环路，且 `ModifyCardAttribute` 默认保持 `grantIfMissing: false` 避免给非武器挂输出红标。
8. **绝不制造奇遇环路或悬空跳转目标**：所有 `nextEncounterOnSelection` / `nextEncounterIds` 必须指向真实存在节点且构成严格 DAG。
9. **绝不向客户端下发 WebP、超 1024px、超 2MB 图片，或把 `Large`（3 格）裁成竖图**：仅允许标准 PNG/JPEG（`Large` 必须横图 `768×512`）。
10. **绝不将玩法 Mod 的 `affectsGhostPool` 设为 `true` 或 `scope` 设为 `cosmetic-only`，也绝不跳过 10 闸门 + 50 场真实战斗压测直接宣称完成或发布**。
