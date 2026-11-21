---
name: bazaar-mod-creator
description: >-
  工业级《The Bazaar（大巴扎）》自建服 Mod 创作与校验工作流。当用户要求制作大巴扎 Mod、新增自定义物品/技能/奇遇事件/野怪/玩法词条、魔改原版卡牌数值或附魔、生成自定义 PNG 卡图、或校验打包 .bazaarmod.zip 时激活本技能。
---

# TheBazaar 工业级 Mod 创作专家（BazaarModKit Standard v1.1）

你正在为《The Bazaar》自建服生态创作或修改 Layer 1 数据驱动 Mod（JSON + PNG）。**必须严格遵循 7 步工业流水线与 10 条踩坑红线；未经 10 道防 Bug 铁闸 + 50 场真实战斗压测全绿校验，严禁声称完成或热重载到服务端！**

## 核心资产与工具锚点
- 标准化手册与 Schema：`docs/MOD_AUTHORING_STANDARD.md`、`docs/schemas/bazaar-mod.schema.json`
- 命令行 SDK：`python tools/mod_sdk_cli.py`（Windows pwsh 下使用 `rg`/`findstr` 时务必显式指定路径）
- 规则与属性速查：[references/rule_types_cheatsheet.md](./references/rule_types_cheatsheet.md)、[references/common_attributes_and_tags.md](./references/common_attributes_and_tags.md)
- 零 Bug 黄金范例：[examples/example_cloned_and_patched.json](./examples/example_cloned_and_patched.json)、[examples/example_original_and_event_chain.json](./examples/example_original_and_event_chain.json)
- Web 工坊与在线压测接口：`http://127.0.0.1:8080/workshop/`（API：`/api/workshop/*`）

## 标准 7 步工业创作工作流

1. **初始化或定位 Mod 目录**：
   - 新 Mod 运行：`python tools/mod_sdk_cli.py new <mod-id> --name "<中文名>" --author "<作者>"`（`<mod-id>` 仅小写字母/数字/连字符，2~64 位）。
2. **先查原版白名单模板（严禁凭空编造 GUID）**：
   - 离线秒查 3,368 张合法白名单模板：`python tools/mod_sdk_cli.py clone-card --search "<关键词>" --type Item --size Medium --json`（或调 `GET /api/workshop/base-cards`）；
   - 克隆原版卡为全新自定义卡：`python tools/mod_sdk_cli.py clone-card <guid或英文名> --mod <mod-id> --as <slug> --mode clone`；
   - 原位平衡魔改原版卡：`python tools/mod_sdk_cli.py clone-card <guid或英文名> --mod <mod-id> --mode patch`（写入 `patches/vanilla_patches.json`，绝不污染基础库）。
3. **对照权威 `$type` 速查表编写规则**：
   - 仅允许使用 [references/rule_types_cheatsheet.md](./references/rule_types_cheatsheet.md) 与 `GET /api/workshop/rule-catalog` 登记的 `$type`；
   - 奇遇子步骤（`EncounterStep`）**必须**用标准 `abilities`（`trigger.$type = "TTriggerOnCardSelected"`）+ `attributes.BuyPrice` + `nextEncounterOnSelection`，**严禁**编造 `effects` 或 `costGold` 字段；
   - 玩家属性前置**必须**用 `TPrerequisitePlayer` + `subject: TTargetPlayerAbsolute` + `conditions: TPlayerConditionalAttribute`（**严禁**写引擎不存在的 `TPrerequisitePlayerAttribute`）。
4. **填齐四阶数值、能力绑定与中文动态占位符**：
   - `CooldownMax`、`HasteAmount`、`SlowAmount`、`FreezeAmount`、`ChargeAmount` 单位均为**毫秒（ms）**（4 秒 = `4000`）；
   - 只要声明了 `tiers`，必须在各阶显式补齐 `abilityIds` / `auraIds`；
   - 中文文案使用 `{attr.DamageAmount}` 等真实存在于全部品阶的属性占位符，确保 `TooltipFormatter` 四阶试渲染 **0 个 `"?"`**。
5. **准备合规 PNG 卡图并校验 SHA-256**：
   - 贴图必须为标准 **PNG**（魔数 `\x89PNG\r\n\x1A\n`，`64~1024 px`、`<= 2 MB`，推荐 Small/Large 物品 `512×1024`、Medium 物品与技能 `512×512`），**严禁 WebP**（Unity 原生 `LoadImage` 不解码 WebP）；
   - 落盘至 `mods/<mod-id>/assets/*.png` 并同步 `assets/art_manifest.json` 哈希。
6. **强制 10 闸门 + 50 场真实战斗压测（全绿才可放行）**：
   - 完整离线 10 闸门 + 50 场真实 `CombatSimulator` 体检：
     ```powershell
     python tools/mod_sdk_cli.py validate mods/<mod-id> --strict --offline-sim --json
     python tools/mod_sdk_cli.py sim-test mods/<mod-id> --matches 50
     ```
   - 或当本地服务端正在运行时：
     ```powershell
     python tools/mod_sdk_cli.py validate mods/<mod-id> --strict --server http://127.0.0.1:8080 --json
     ```
   - 只要任一闸门返回 `Error`（退出码非 0），必须按中文 `fixHintZh` 修改并重跑，直到 **10/10 PASS、0 Error、50 场战斗 0 异常且单场 <= 200ms**。
7. **安全热重载或打包分发**：
   - 服务端全绿强门禁热重载：`POST http://127.0.0.1:8080/api/workshop/mods/<mod-id>/publish-reload`（有错自动返回 HTTP 422 阻止带病上线）；
   - 打包标准分享包：`python tools/mod_sdk_cli.py pack mods/<mod-id> -o dist/mods/<mod-id>.bazaarmod.zip`。

## 10 条不可触碰的 AI 创作红线（Never Do List）

1. **绝不凭空编造 `baseVisualTemplateId`**：必须来自 `dist/data/valid_card_ids.json`，且同 `Type`、物品同 `Size`。
2. **绝不省略命名空间前缀**：所有新卡/事件/步骤/野怪/模式 ID 必须以 `<mod-id>:` 开头。
3. **绝不把冷却毫秒错写成秒**：`CooldownMax: 4` 是致命 Bug，必须写 `4000`（合法范围 `1000~60000`）。
4. **绝不发明不存在的 `$type` 或奇遇 `effects`/`costGold` 字段**：一切以 `RuleTypeRegistry.KnownTypes` 为准，绝不写 `TPrerequisitePlayerAttribute`。
5. **绝不在有 `tiers` 时遗漏 `abilityIds`/`auraIds`**：否则卡进战斗 0 技能触发。
6. **绝不构造自充能/自强发死循环**：严禁 `TTriggerOnCardFired → TActionCardCharge(Self, >= 90% CD)` 或无条件 `TActionCardForceUse(Self)`。
7. **绝不制造奇遇环路或悬空跳转目标**：所有 `nextEncounterOnSelection` 目标必须真实存在且构成严格 DAG。
8. **绝不向客户端下发 WebP、超 1024px 或超 2MB 图片**：统一输出标准 PNG。
9. **绝不将玩法 Mod 的 `affectsGhostPool` 设为 `true` 或 `scope` 设为 `cosmetic-only`**：保护标准天梯镜像池零污染。
10. **绝不跳过 `mod_sdk_cli.py validate + sim-test` 直接声称完成或热重载**。
