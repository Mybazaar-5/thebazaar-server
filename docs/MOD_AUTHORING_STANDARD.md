# TheBazaar V15.4 标准化工业级 Mod 创作手册

> **适用版本**：BazaarServer / BazaarCore V15.4 · BazaarModKit Standard v2.2  
> **单一事实源契约**：`BazaarCore/Mods/*.cs` ≡ `docs/schemas/bazaar-mod.schema.json` ≡ `ModRuleCatalog.cs` ≡ `.agents/skills/bazaar-mod-creator/`（由 `Wave3A_ContractSsotTests.cs` 反射单测永久锁死）。

---

## 一、三层能力边界与生态定位

| 层次 | 面向人群 | 100% 支持的能力 | 联机分发与稳定性 |
| :--- | :--- | :--- | :--- |
| **Layer 1：数据驱动 + V14/V15 开放沙盒机制**（纯 JSON + PNG/JPEG） | **95% 社区玩家 / 策划 / AI** | ① 全新物品（Small/Medium/Large，含 5 阶 `Legendary`）与技能；② **原位魔改原版卡**（`patches/*.json` 差量修改属性/附魔/能力/光环/双语文案/多槽计数角标/立绘）；③ **开放声明式沙盒自定义机制**（`mechanics/*.json`：10 大管线钩子 × 18 种安全操作 × 7 作用域 × `grantIfMissing` × 实时 3D `counterDisplays` 角标）；④ 多类型奇遇事件链（`EventEncounter`/`PedestalEncounter`/`EncounterStep`/`CombatEncounter` + 小时调度/单局唯一/链式限定/背景板）；⑤ 自定义野怪与 Boss（含野怪预览面板）；⑥ 自定义玩法模式与肉鸽突变词条（`modes/custom_mutators.json`）；⑦ 独立四层汉化覆盖包；⑧ 2D 高清 PNG/JPEG 贴图。 | **★★★★★**：单点发布热重载后，客户端自动按 SHA-256 增量拉取贴图、双语文案、实时 3D 计数徽章与附魔比例预览；未装视觉插件的客户端凭合法白名单底座自动优雅降级。 |
| **Layer 2：C# 代码扩展** | **4% 硬核开发者** | ① 服务端注册 `TActionMod_*` / `TTriggerMod_*` / `IGameModeHook`；② 客户端通过 `BazaarModSdk` 扩展原生菜单与卡图解析器。 | **★★★☆☆**：Web 工坊与社区库严禁上传 `.dll`/可执行脚本，代码 Mod 仅由服主显式信任安装。 |
| **Layer 3：原版闭源 Unity 资产边界** | **1% 资产研究者** | 3D 棋盘皮肤模型、Spine 骨骼立绘、自定义 URP Shader、FMOD `.bank` 与第 9 位原生英雄枚举。 | 通过 3,368 白名单底座复用 + 133 官方 Addressable VFX + 高清 2D PNG/JPEG 覆盖实现零崩服体验。 |

---

## 二、三种创作路径

| 创作路径 | 入口 / 命令 | 核心能力 |
| :--- | :--- | :--- |
| **路径 A：零代码 Web 创意工坊（最推荐）** | 启动器点 **「创意工坊编辑器 (Web)」** 或访问 `http://127.0.0.1:8080/workshop/` | 搜索 3,368 原版卡 → 克隆/原位 Patch → 5 阶数值与权威标准价自动推算 → 积木词缀/光环拼装（无损回填）→ 附魔比例数值实时预览 → 开放沙盒机制实验室（10 钩子/18 操作/`counterDisplays`/`grantIfMissing`）→ 怪物/模式/突变词条/汉化结构化编辑 → 10 道防 Bug 铁闸 + 50 场真实战斗压测 → 全绿发布或上传社区库。 |
| **路径 B：AI Skill 自然语言创作** | 仓库内置 `.agents/skills/bazaar-mod-creator/SKILL.md` 与 `docs/skills/bazaar-mod-creator/SKILL.md`（逐字节同步） | 自然语言需求访谈 → 自动查白名单底板 → 生成合规 JSON/机制/贴图 → 调用 `mod_sdk_cli.py` 完成全套校验与压测。 |
| **路径 C：CLI + Git 工程化协作** | `python tools/mod_sdk_cli.py` + `docs/schemas/bazaar-mod.schema.json` | 完整支持 `new` / `clone-card` / `validate` / `sim-test` / `pack` / `unpack` / `publish`（支持 `--token` 与 `--uploader-secret`），优先桥接 `BazaarCli` 10 闸门权威引擎。 |

---

## 三、标准化 Mod 目录规范（V15.4）

```text
mods/<mod-id>/                     # <mod-id> 必须匹配 ^([a-z0-9][a-z0-9-]{1,62}[a-z0-9]|[a-z0-9]{2})$
├── mod.json                       # 必需：清单、版本、作用域、依赖、幽灵池隔离与 contentRoots
├── cards/
│   └── custom_cards.json          # 可选：全新自定义物品与技能（支持 5 阶、双语、counterDisplays、customMechanics）
├── patches/
│   └── vanilla_patches.json       # 可选：原位魔改原版卡差量补丁（支持附魔/能力/光环/机制/双语/counterDisplaysOverride）
├── mechanics/
│   └── custom_mechanics.json      # 可选：V14/V15 开放声明式沙盒机制（10 钩子 × 18 操作 × 参数 Schema × counterDisplay）
├── events/
│   └── custom_events.json         # 可选：奇遇事件与子步骤（EventEncounter/PedestalEncounter/EncounterStep/CombatEncounter）
├── monsters/
│   └── custom_monsters.json       # 可选：自定义野怪 / Boss
├── modes/
│   ├── custom_modes.json          # 可选：自定义玩法模式
│   └── custom_mutators.json       # 可选：自定义肉鸽突变词条（CustomMutatorDefinition）
├── localization/
│   └── zh-CN.json                 # 可选：四层汉化覆盖词典（LocalizationOverlayDocument）
└── assets/
    ├── art_manifest.json          # 自动维护：全部 PNG/JPEG 尺寸、MIME 与 SHA-256 清单
    └── *.png / *.jpg              # 方案 A：仅限标准 PNG 或 JPEG（64~1024px，<= 2MB，严禁 WebP！）
```

> **向后兼容保护**：`SaveWorkspaceAtomic` 会保留作者自定义的合法相对路径 `contentRoots`（默认 `assets: "assets/*"` 以兼容 PNG/JPEG）以及目录中的非托管文档（如 `README.md`、`docs/`），采用同卷 `.tmp_`/`.bak_` 原子目录切换事务防止半写损坏。

---

## 四、五阶官方权威标准买卖价（`StandardPricesSettings`）

> 严格对齐 `src/BazaarCore/Models/GameModeModels.cs` 与 `/api/workshop/rule-catalog` 下发的 `standardPrices`。注意 **Skill Bronze 出售价为 `2`（非 `buy / 2`）**。

| 尺寸 / 类型 | Bronze（买 / 卖） | Silver（买 / 卖） | Gold（买 / 卖） | Diamond（买 / 卖） | Legendary（买 / 卖） |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Small（1 格）** | `2` / `1` | `4` / `2` | `8` / `4` | `16` / `8` | `24` / `12` |
| **Medium（2 格）** | `4` / `2` | `8` / `4` | `16` / `8` | `32` / `16` | `48` / `24` |
| **Large（3 格）** | `6` / `3` | `12` / `6` | `24` / `12` | `48` / `24` | `64` / `32` |
| **Skill（技能）** | `5` / `2` | `10` / `5` | `20` / `10` | `40` / `20` | `50` / `25` |

---

## 五、V14/V15 开放声明式沙盒机制契约（`mechanics/*.json`）

1. **10 大通用管线钩子（`MechanicDefinition.AllowedHooks`）**：
   `OnFightStart`、`OnCardFired`、`BeforeDamage`、`BeforeStatusApply`、`BeforeCardStateChange`、`BeforeCardFire`、`AfterCardFire`、`OnCustomEvent`、`OnTriggerEvent`、`PostCombatCommit`。
2. **7 种合法作用域（`AllowedScopes`）**：
   `SelfCard`、`OwnerBoard`、`EnemyBoard`、`OwnerPlayer`、`EnemyPlayer`、`Global`、`Any`（未知作用域静态报 Error）。
3. **18 种安全操作（`AllowedOps`）**：
   - 流程控制：`If`、`Repeat`（≤20 次）、`ScheduleDelayed`、`Stop`
   - 管线改写：`CancelHook`、`ModifyHookAmount`
   - 状态黑板：`SetFlag`、`ClearFlag`、`ModifyCounter`（可同步投影至 **`Custom_0` ~ `Custom_8`**）
   - 战斗动作：`DealDamage`、`ApplyStatus`（9 状态：`Shield`/`Burn`/`Poison`/`Heal`/`Regen`/`Haste`/`Slow`/`Freeze`/`Charge`）、`ModifyCardAttribute`（含 **`grantIfMissing`**，默认 `false` 严防给非武器误挂伤害红标）、`ModifyPlayerAttribute`（14 种合法玩家属性含 `PercentDamageReduction`）、`ChangeCardState`（`Disable`/`Destroy`/`Repair`）、`ForceUseCard`
   - 联动与局外结转：`EmitCustomEvent`、`QueuePostCombatRemoveCard`、`QueuePostCombatModifyAttr`（含 `grantIfMissing`）
4. **3D 卡面实时计数角标（`counterDisplay` / `counterDisplays` / `counterDisplaysOverride`）与视觉绑定目标（`visualTarget`）**：
   - 单卡最多 4 槽位；支持 21 种合法绑定属性（`Custom_0..Custom_8`、`Counter`、`Ammo`、`AmmoMax` 及核心输出/暴击/多重/冷却属性）与动态上限属性 **`maxAttr`**；
   - 5 种样式（`auto`/`pips`/`bar`/`gem`/`pill`）× 6 种值型（`count`/`progress`/`percent`/`seconds`/`multiplier`/`delta`）× 4 锚点（`bottom`/`bottomRight`/`topLeft`/`topRight`）；
   - `visualTarget` 权威实码三枚举为 **`ItemArt`**（物品立绘）、**`SkillIcon`**（技能图标）、**`EncounterPortrait`**（奇遇/野怪肖像，可搭配 `customBgArtFile` 定制背景板）。
5. **官方 133 种 VFX + 30 个友好短名别名 + camelCase 契约**：
   - 覆盖 `GameData.db` 中 `Abilities` + `Auras` + `Enchantments` 全部 **133** 个真实 Unity Addressable Prefab 键与 **30** 个友好别名（`Fireball`/`Cannonball`/`LaserRed`/`FireClaw`/`Slash` 等）；
   - `vfxConfig` 统一写入标准 camelCase：`{ "vfxOverrideKey": "...", "vfxShouldPlay": true, "vfxIsTakeover": false }`（服务端同时向后兼容历史 PascalCase）。

---

## 六、防 Bug 10 道工业级铁闸（一票否决，全绿放行）

| 闸门 | 名称 | 核心拦截图谱与阈值 |
| :---: | :--- | :--- |
| **#1** | Schema 结构、废字段与五阶绑定 | 拦 `schemaVersion != 1`、目录穿越、奇遇虚构 `effects`/`costGold`、`tiers.*.abilityIds` 断链与畸形 `mutators` JSON。 |
| **#2** | 命名空间与 Kahn 拓扑依赖 | 强制 `modId` 正则与 `<mod-id>:` 前缀；拦撞名、原位补丁目标不存在、自依赖与循环依赖环。 |
| **#3** | 白名单视觉载体与同 Type/Size | 强制 `baseVisualTemplateId` 命中 3,368 白名单且同 `Type`、Item 同 `Size`（杜绝白块与 3D 穿模）。 |
| **#4** | 规则树与机制沙盒静态白名单 | 递归校验所有 `$type`、触发器、4 种前置条件（禁 `TPrerequisitePlayerAttribute`）、拼写纠错，以及开放机制 10 钩子 / 18 操作 / 7 作用域 / 14 玩家属性 / `Custom_0..8` / 21 计数显示属性 / `*Param` 键存在性。 |
| **#5** | 冷却 / 数值 / 倍率 / 自充能防死循环 | 冷却 `[1000, 60000]` ms；数值 `[-10000, 100000]`；光环倍率 `[0.05, 10.0]`；拦截 `OnCardFired -> Charge(Self, >=90% CD)` 与 `ForceUse(Self)` 死循环。 |
| **#6** | 奇遇跳转图三色 DFS 防环与死胡同 | 拦事件链环路、悬空目标、非战斗事件空选项软锁与非法 `minDay > maxDay`。 |
| **#7** | 双语文案与 `{attr.*}` 全品阶真渲染 | 拦缺失中文标题、`TODO/TBD`、花括号错配，并对全部品阶调用 `TooltipFormatter` 真实试渲染确保 **0 个 `"?"`**。 |
| **#8** | PNG/JPEG 魔数 / 尺寸 / 体积 / SHA-256 | **方案 A**：仅放行真实 PNG 或 JPEG 魔数、`64~1024px`、`<= 2MB`（总 `<= 32MB`）、`art_manifest.json` 哈希一致；**严拒 WebP 与伪造扩展名文件**。 |
| **#9** | 50 场真实 `CombatSimulator` 确定性压测 | 隔离 Overlay 上真跑 50 场实战：硬拦异常 `>0`、数值 `NaN`、机制熔断 `>0`、预算违规 `>0`、单场超 `200ms`、僵局超 `20%`，并附带确定性胜率与平均时长参考。 |
| **#10** | 幽灵池隔离与回归不变量 | 玩法 Mod 强制 `ForcesGhostPoolIsolation == true`、`UnknownRuleTypes == 0`、Wire 映射 100% 合法、`RollbackToBase()` 零残留。 |

> **导出与导入硬门禁**：`/api/workshop/mods/import` 对含 `Error` 病包一律返回 HTTP `422` 拒收（`allowDraftWithWarnings` 仅允许 0 Error 含 Warning）；`/api/workshop/mods/{modId}/export` 默认阻断含 `Error` 病包返回 HTTP `422`（仅显式追加 `?allowDraft=true` 才允许导出带 `isDraftExport: true` 标记的草稿备份）。

---

## 七、高频易错红线速查

| 概念 | 正确写法 | 常见致命错误 |
| :--- | :--- | :--- |
| **`Common` 英雄语义** | `Common` 仅在 **Curio 奇物店 / 中立池**刷出；若想让卡在常规英雄商店出现，必须明确列出对应可游玩英雄（`Vanessa`、`Dooley`、`Pygmalien`、`Mak`、`Karnok`、`Jules`、`Stelle`、`Hero8`）！ | ❌ 误以为 `["Common"]` 会出现在全部 8 个英雄商店 |
| **奇遇事件小时调度键** | `CustomEventDefinition` 顶层字段名为 **`"hours"`**（同时支持 `schedule.hours`）；`CustomCardDefinition` 上对应字段为 `"allowedHours"` | ❌ 在事件 JSON 顶层混写错字段名导致不生效 |
| **冷却与控制时长单位** | 全部为 **毫秒 (ms)**：4 秒 = `4000`（合法范围 `1000~60000`） | ❌ 写成 `4`（秒当毫秒，直接被 Gate #5 拦截） |
| **卡图格式与比例** | 标准 **PNG / JPEG**（严禁 WebP）：`Small` `512×1024`、`Medium`/`Skill` `512×512`、`Large` `768×512` | ❌ 传 WebP、超 1024px 或把 Large 裁成竖图 |
| **玩家属性前置条件** | `TPrerequisitePlayer` + `subject: TTargetPlayerAbsolute` + `conditions: TPlayerConditionalAttribute` | ❌ 写引擎不存在的 `TPrerequisitePlayerAttribute` |
| **特效配置键大小写** | `{ "vfxOverrideKey": "Fireball", "vfxShouldPlay": true, "vfxIsTakeover": false }` | ❌ 混写旧 PascalCase 导致工具链解析分裂 |

---

## 八、安全与认证机制（`AdminAccessGuard` & `X-Uploader-Secret`）

- **全服统一管理员守卫（`AdminAccessGuard`）**：
  - `BAZAAR_WORKSHOP_MODE=readonly` 具有最高优先级，强制拒绝所有写操作与服主管理端点（覆盖 `WorkshopEndpoints`、`ModEndpoints` 与 `CommunityModEndpoints`）；
  - 本机回环默认可读写；公网部署默认只读，服主需配置 `BAZAAR_WORKSHOP_TOKEN=<强口令>` 并通过请求头 `X-Workshop-Token`（常量时间比对，严禁通过 URL `?token=` 传输）解锁；Web 工坊前端仅将该口令存入 `sessionStorage`，关闭标签页自动销毁，绝不写入 `localStorage`。
- **社区匿名上传凭证（`X-Uploader-Secret`）**：
  - 玩家通过 `/api/community/mods/upload` 首次匿名上传社区 Mod 时，服务端生成高熵凭证并通过响应头 `X-Uploader-Secret` 与 JSON 字段 `uploaderSecret` 下发；后续更新或删除同名作品需在请求头回传 `X-Uploader-Secret`（或使用已认证账号 Bearer Token）。Web 工坊自动按 `modId` 暂存该凭证至 `sessionStorage` 并提供一键复制与手动输入栏，CLI 支持 `--uploader-secret` 参数。
