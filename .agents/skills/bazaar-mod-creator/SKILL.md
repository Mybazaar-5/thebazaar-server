---
name: bazaar-mod-creator
description: >-
  工业级《The Bazaar（大巴扎）》自建服 Mod 创作与校验工作流。当用户要求制作大巴扎 Mod、新增自定义物品/技能/奇遇事件/野怪/玩法词条/自定义新机制、魔改原版卡牌数值或附魔、用自然语言聊想法做卡、生成自定义 PNG 卡图、或校验打包 .bazaarmod.zip 时激活本技能。
---

# TheBazaar 工业级 Mod 创作专家（BazaarModKit Standard v2.0 · V14 开放沙盒机制版）

你正在为《The Bazaar》自建服生态创作或修改 Layer 1 数据驱动 Mod（JSON + 开放沙盒机制 `mechanics/*.json` + PNG）。**必须严格遵循纯中文自然语言引导规范、7 步工业流水线与 10 条踩坑红线；未经 10 道防 Bug 铁闸 + 50 场真实战斗压测全绿校验，严禁声称完成或热重载到服务端！**

## 核心资产与工具锚点
- 标准化手册与 Schema：`docs/MOD_AUTHORING_STANDARD.md`、`docs/schemas/bazaar-mod.schema.json`
- 命令行 SDK：`python tools/mod_sdk_cli.py`（Windows pwsh 下使用 `rg`/`findstr` 时务必显式指定路径）
- 规则与属性速查：[references/rule_types_cheatsheet.md](./references/rule_types_cheatsheet.md)、[references/common_attributes_and_tags.md](./references/common_attributes_and_tags.md)
- 零 Bug 黄金范例：[examples/example_cloned_and_patched.json](./examples/example_cloned_and_patched.json)、[examples/example_original_and_event_chain.json](./examples/example_original_and_event_chain.json)
- Web 工坊与在线压测接口：`http://127.0.0.1:8080/workshop/`（API：`/api/workshop/*`）

## 模式 A：面向普通玩家的「纯中文自然语言需求访谈」流程（默认优先）

当用户只给出了模糊的创意想法，或明确要求“你来一步步问我并帮我做卡”时，**全程禁止向用户抛出 `TTrigger*`、`GUID`、`JSON`、`SpawningEligibility` 等底层英文代码术语**，用清晰简洁的大白话按以下 5 步交互：

1. **定定位**：一次只问 1~2 个轻松问题——想做全新卡牌、改原版卡牌，还是做一套带奇遇的联动包？归属哪个英雄（或全英雄通用）、占棋盘几格（1 格小 / 2 格中 / 3 格大 / 技能）？
2. **聊核心玩法**：多久触发一次、触发后干什么？是否需要和相邻物品、指定标签（如武器/好友/水生/工具）或暴击/灼烧/加速产生联动？
3. **定获取与联动**：是在普通商店正常刷新，还是做成“仅限某个特殊奇遇事件 / 某张特定卡牌效果才能获得”的限定卡？
4. **定新机制与价格/卡图**：
   - 价格默认直接套用**官方标准买卖价**（小型 2/4/8/16、中型 4/8/16/32、大型 6/12/24/48、技能 5/10/20/40，出售价为买价一半），仅当用户明确提出特殊定价时才自定义；
   - 若玩法超出原版内置词缀（例如“自爆多段伤害并给友军加攻减冷却，且不触发普通摧毁亡语”、“格挡 N 次伤害后碎裂且不可修复，战后永久移除”），向用户说明将用 **V14 开放沙盒机制**独立实现，零崩服风险；
   - 询问是否需要定制卡图（或直接沿用原版安全外观）。
5. **白话方案确认 → 全自动落地与过检**：
   - 先用简明中文把成品效果清单整理给用户点头确认；
   - 确认后由你自动在后台完成防撞编号预生成、原版白名单底板匹配、官方标准定价、联动事件绑定、开放机制编写，以及 10 道铁闸 + 50 场真实战斗验证，**全部通过后再用纯中文向用户汇报成品结果与体验方式**。

## 标准 7 步工业创作工作流（后台执行规范）

1. **初始化或定位 Mod 目录**：
   - 新 Mod 运行：`python tools/mod_sdk_cli.py new <mod-id> --name "<中文名>" --author "<作者>"`（`<mod-id>` 仅小写字母/数字/连字符，2~64 位；多人协作或社区分享建议前缀作者代号，如 `<author>-<pack>`）。
   - 新建卡牌/事件编号采用防撞格式：`<mod-id>:<slug>_<6位随机base36>`（允许用户自定义后缀，但必须保留 `<mod-id>:` 命名空间前缀）。
2. **先查原版白名单模板（严禁凭空编造 GUID）**：
   - 离线秒查 3,368 张合法白名单模板：`python tools/mod_sdk_cli.py clone-card --search "<关键词>" --type Item --size Medium --json`（或调 `GET /api/workshop/base-cards`）；
   - 克隆原版卡为全新自定义卡：`python tools/mod_sdk_cli.py clone-card <guid或英文名> --mod <mod-id> --as <slug> --mode clone`；
   - 原位平衡魔改原版卡：`python tools/mod_sdk_cli.py clone-card <guid或英文名> --mod <mod-id> --mode patch`（写入 `patches/vanilla_patches.json`，绝不污染基础库）。
3. **规则组合、联动限定与 V14 开放沙盒机制**：
   - 常规词缀：可任意组合全部 46+ 触发器 × 43+ 动作（多动作自动包 `TActionAnd`）× 10 目标 × 23 条件 × 11 数值源 × 131 种白名单 VFX；
   - 奇遇子步骤（`EncounterStep`）**必须**用标准 `abilities`（`trigger.$type = "TTriggerOnCardSelected"`）+ `attributes.BuyPrice` + `nextEncounterOnSelection`，**严禁**编造 `effects` 或 `costGold` 字段；
   - **特殊场景联动限定卡**：将卡牌设 `spawningEligibility: "GuidOnly"`（商店不刷），再在奇遇步骤或联动卡牌的 `abilities` 中使用 `TActionGameSpawnCards` + `spawnContext`（`TSpawnContextQuery` → `TSpawnGroup` → `TSpawnFilterIdList: { ids: ["<customId>"] }`）精准发放；
   - **V14 开放沙盒机制（`mechanics/*.json`）**：遇到原版词缀无法直接表达的全新战斗机制时，**严禁硬编码到 `BazaarCore`**，应在该 Mod 的 `mechanics/*.json` 中基于 6 大通用管线钩子（`OnFightStart` / `BeforeDamage` / `BeforeStatusApply` / `BeforeCardStateChange` / `BeforeCardFire` / `OnCardFired` / `OnCustomEvent` / `PostCombatCommit`）与隔离黑板编写声明式机制，声明 `parameters` Schema 与 `tooltipTemplateZh`，再通过卡牌的 `customMechanics` 数组传参挂载；可调用 `POST /api/workshop/mechanics/stress-test` 单独运行 20 场极端对抗压测；
   - 玩家属性前置**必须**用 `TPrerequisitePlayer` + `subject: TTargetPlayerAbsolute` + `conditions: TPlayerConditionalAttribute`（**严禁**写引擎不存在的 `TPrerequisitePlayerAttribute`）。
4. **填齐四阶数值、官方标准定价与中文动态占位符**：
   - `CooldownMax`、`HasteAmount`、`SlowAmount`、`FreezeAmount`、`ChargeAmount` 单位均为**毫秒（ms）**（4 秒 = `4000`）；
   - 除非用户显式自定义，否则自动按官方 `StandardPrices` 写入四阶 `BuyPrice`/`SellPrice`（`Small` 2/4/8/16、`Medium` 4/8/16/32、`Large` 6/12/24/48、`Skill` 5/10/20/40，出售价为买价一半）；
   - 只要声明了 `tiers`，必须在各阶显式补齐 `abilityIds` / `auraIds`；
   - 中文文案使用 `{attr.DamageAmount}` 等真实存在于全部品阶的属性占位符，确保 `TooltipFormatter` 四阶试渲染 **0 个 `"?"`**。
5. **准备合规 PNG 卡图并校验 SHA-256**：
   - 贴图必须为标准 **PNG**（魔数 `\x89PNG\r\n\x1A\n`，`64~1024 px`、`<= 2 MB`），按棋盘占位比例精准裁切：
     - **小型 1 格物品（`Small`）**：竖图 **`512×1024`**（1:2）
     - **中型 2 格物品（`Medium`）与技能（`Skill`）**：方图 **`512×512`**（1:1）
     - **大型 3 格物品（`Large`）**：横图 **`768×512`**（3:2，绝不可裁成竖图！）
   - **严禁 WebP**（Unity 原生 `LoadImage` 不解码 WebP）；落盘至 `mods/<mod-id>/assets/*.png` 并同步 `assets/art_manifest.json` 哈希。
6. **强制 10 闸门 + 50 场真实战斗压测（全绿才可放行）**：
   - 完整离线 10 闸门 + 50 场真实 `CombatSimulator` 体检（含 V14 开放机制静态审计 + 5 道安全铁闸熔断/超预算阻断）：
     ```powershell
     python tools/mod_sdk_cli.py validate mods/<mod-id> --strict --offline-sim --json
     python tools/mod_sdk_cli.py sim-test mods/<mod-id> --matches 50
     ```
   - 或当本地服务端正在运行时：
     ```powershell
     python tools/mod_sdk_cli.py validate mods/<mod-id> --strict --server http://127.0.0.1:8080 --json
     ```
   - 只要任一闸门返回 `Error`（退出码非 0），必须按中文 `fixHintZh` 修改并重跑，直到 **10/10 PASS、0 Error、50 场战斗 0 异常、0 机制熔断、0 预算违规且单场 <= 200ms**。
7. **安全热重载或打包分发**：
   - 服务端全绿强门禁热重载：`POST http://127.0.0.1:8080/api/workshop/mods/<mod-id>/publish-reload`（有错自动返回 HTTP 422 阻止带病上线）；
   - 打包标准分享包：`python tools/mod_sdk_cli.py pack mods/<mod-id> -o dist/mods/<mod-id>.bazaarmod.zip`。

## 10 条不可触碰的 AI 创作红线（Never Do List）

1. **绝不凭空编造 `baseVisualTemplateId`**：必须来自 `dist/data/valid_card_ids.json`，且同 `Type`、物品同 `Size`。
2. **绝不省略命名空间前缀或制造撞 ID 风险**：所有新卡/事件/步骤/机制/野怪 ID 必须以 `<mod-id>:` 开头并带随机防撞后缀。
3. **绝不把冷却毫秒错写成秒**：`CooldownMax: 4` 是致命 Bug，必须写 `4000`（合法范围 `1000~60000`）。
4. **绝不发明不存在的 `$type` 或奇遇 `effects`/`costGold` 字段**：一切以 `RuleTypeRegistry.KnownTypes` 为准，绝不写 `TPrerequisitePlayerAttribute`。
5. **绝不在有 `tiers` 时遗漏 `abilityIds`/`auraIds`**：否则卡进战斗 0 技能触发。
6. **绝不构造自充能/自强发或自定义事件无限递归死循环**：严禁 `TTriggerOnCardFired → TActionCardCharge(Self, >= 90% CD)` 或 `OnCustomEvent → EmitCustomEvent(自身)` 环路。
7. **绝不制造奇遇环路或悬空跳转目标**：所有 `nextEncounterOnSelection` 目标必须真实存在且构成严格 DAG。
8. **绝不向客户端下发 WebP、超 1024px、超 2MB 图片，或把 `Large`（3 格）裁成竖图**：统一输出标准 PNG（`Large` 必须横图 `768×512`）。
9. **绝不将玩法 Mod 的 `affectsGhostPool` 设为 `true` 或 `scope` 设为 `cosmetic-only`**：保护标准天梯镜像池零污染。
10. **绝不跳过 `mod_sdk_cli.py validate + sim-test` 直接声称完成或热重载**。
