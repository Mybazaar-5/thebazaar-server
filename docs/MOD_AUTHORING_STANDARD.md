# TheBazaar V13.1 标准化工业级 Mod 创作手册

> **适用版本**：BazaarServer / BazaarCore V13.1 · BazaarModKit Standard v1.1  
> **核心理念**：流程化、标准化、防 Bug、傻瓜化。无论你是零编程玩家、数值策划、AI 创作者还是 C# 开发者，都可以在 5 分钟内做出绝对不白块、不崩服、不污染镜像池的高质量 Mod！

---

## 一、客观能力边界：这套生态能否达到《杀戮尖塔 / 小丑牌》级创意工坊繁荣度？

**答案是：完全可以，并且额外拥有“联机服单点发布、玩家零手工装包、自动 SHA-256 增量拉图”的独特优势。** 但我们实事求是地划清 3 个层次的能力边界：

| 层次 | 面向人群 | 100% 支持的能力 | 联机分发与稳定性 |
| :--- | :--- | :--- | :--- |
| **Layer 1：数据驱动 Mod**（纯 JSON + PNG） | **95% 社区玩家 / 策划 / AI** | ① 全新物品（Small/Medium/Large）与技能；② **原位魔改原版卡**（`patches/*.json` 改数值/冷却/标签/文案）；③ 多步奇遇事件链；④ 自定义野怪与 Boss；⑤ 自定义玩法模式与肉鸽突变词条；⑥ 独立汉化覆盖包；⑦ 自定义 2D 高清 PNG 卡图。 | **★★★★★**：服主发布热重载后，客户端自动增量拉图与汉化；即便未装客户端视觉插件也能凭借合法白名单底座**零白块优雅降级**。 |
| **Layer 2：C# 代码扩展** | **4% 硬核开发者** | ① 服务端注册 `TActionMod_*` / `TTriggerMod_*` / `IGameModeHook` 发明全新机制；② 客户端通过 `BazaarModSdk` 挂载原生菜单页签与动态卡图解析器。 | **★★★☆☆**：出于安全防 RCE，Web 创意工坊严禁上传 `.dll`，代码 Mod 由服主/玩家显式信任安装。 |
| **Layer 3：原版闭源 Unity 资产边界** | **1% 资产研究者** | 3D 棋盘皮肤、Spine 骨骼立绘、自定义 URP Shader、FMOD `.bank` 音频库与第 9 位原生英雄枚举属于原版闭源资产边界。 | 采用**白名单底座槽位复用**（3D 卡框、打击特效、原版音效）与高清 2D PNG 立绘覆盖实现零崩溃体验。 |

---

## 二、三种创作路径对比（选最顺手的一种）

| 创作方式 | 入口 / 工具 | 推荐人群 | 核心体验 |
| :--- | :--- | :--- | :--- |
| **路径 A：零代码 Web 创意工坊（最推荐）** | 启动器点 **「创意工坊编辑器 (Web)」** 或访问 `http://127.0.0.1:8080/workshop/` | 所有玩家、数值设计师 | 实时搜 3,368 原版卡 → 一键克隆/原位魔改 → 冷却填秒自动 ×1000 → 拖图自动转 PNG → 一键跑 10 道铁闸 + 50 场战斗 → 全绿热重载！ |
| **路径 B：AI Skill 一句话生成** | 仓库内置 `docs/skills/bazaar-mod-creator/SKILL.md` 与 `.agents/skills/bazaar-mod-creator/SKILL.md` | 效率创作者 | 在 Antigravity / Cursor / Claude Code 直接描述脑洞，AI 自动查白名单、写 JSON、跑完 10 闸门 + 50 场真实战斗自检后交付。 |
| **路径 C：CLI + 手写 JSON / Git 协作** | `python tools/mod_sdk_cli.py` + `docs/schemas/bazaar-mod.schema.json` | 开发者、大型 Mod 团队 | 标准脚手架 `new` / `clone-card` / `validate` / `sim-test` / `pack` / `unpack`，天然接入 CI 自动化门禁。 |

---

## 三、标准化 Mod 目录规范（V13.1）

```text
mods/<mod-id>/                     # <mod-id> 必须匹配 ^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$
├── mod.json                       # 必需：清单、版本、作用域、依赖与幽灵池隔离声明
├── cards/
│   └── custom_cards.json          # 可选：全新自定义物品与技能（customId = <mod-id>:<slug>）
├── patches/
│   └── vanilla_patches.json       # 可选（V13.1 新增）：原位魔改原版卡差量补丁
├── events/
│   └── custom_events.json         # 可选：奇遇事件与子步骤（严格使用 TTriggerOnCardSelected）
├── monsters/
│   └── custom_monsters.json       # 可选：自定义野怪 / Boss
├── modes/
│   ├── custom_modes.json          # 可选：自定义玩法模式
│   └── custom_mutators.json       # 可选：自定义肉鸽词条
├── localization/
│   └── zh-CN.json                 # 可选：额外汉化覆盖词典
└── assets/
    ├── art_manifest.json          # 自动维护：全部 PNG 尺寸与 SHA-256 清单
    └── *.png                      # 仅限标准 PNG（64~1024px，<= 2MB，禁止 WebP！）
```

### 原位魔改原版卡契约（`patches/vanilla_patches.json`）

只需填写你要修改的差量字段，其他属性 100% 继承原版卡；禁用或卸载 Mod 后无损复原：

```json
{
  "$schema": "../../docs/schemas/bazaar-mod.schema.json#/$defs/CardPatchFile",
  "schemaVersion": 1,
  "modId": "classic-rebalance",
  "patches": [
    {
      "targetTemplateId": "03b1fd16-6b0d-4847-a76f-6c586f7c5958",
      "note": "原版弯刀 Kukri 平衡性微调：缩短冷却 0.5 秒并追加 Tool 标签",
      "startingTier": "Bronze",
      "addTags": ["Tool"],
      "removeTags": [],
      "tierAttributeOverrides": {
        "Bronze": { "CooldownMax": 3500, "DamageAmount": 16 },
        "Silver": { "CooldownMax": 3500, "DamageAmount": 32 },
        "Gold": { "CooldownMax": 3000, "DamageAmount": 64 },
        "Diamond": { "CooldownMax": 3000, "DamageAmount": 128 }
      }
    }
  ]
}
```

---

## 四、防 Bug 10 道工业级铁闸（一票否决，全绿才允许上线）

无论是 Web 工坊点击 **「安全发布并热重载」**，还是执行 `python tools/mod_sdk_cli.py validate`，都会按顺序穿过以下 10 道硬性门禁：

| 闸门 | 名称 | 核心拦截图谱与阈值 |
| :---: | :--- | :--- |
| **#1** | Schema 结构、废字段与四阶绑定 | 拦 `schemaVersion != 1`、目录穿越、奇遇虚构 `effects`/`costGold`、`tiers.*.abilityIds` 引用不存在的 ID。 |
| **#2** | 命名空间与 Kahn 拓扑依赖 | 强制 `modId` 正则与 `<mod-id>:` ID 前缀；拦撞名、原位补丁目标不存在、自依赖与循环依赖环。 |
| **#3** | 白名单视觉载体与同 Type/Size | 强制 `baseVisualTemplateId` 命中 3,368 白名单且同 `Type`、Item 同 `Size`（100% 杜绝白块与 3D 穿模）。 |
| **#4** | 全量 `$type` / Trigger / Prerequisite 递归白名单 | 递归校验所有规则树 `$type`、已支持触发器、4 种合法前置条件（直接拦 `TPrerequisitePlayerAttribute`）及拼写错误属性（附自动纠错建议）。 |
| **#5** | 冷却 / 数值 / 倍率 / 自充能防死循环 | 主动冷却必须 `[1000, 60000]` ms（拦截 `CooldownMax=4` 秒当毫秒误写）；单卡数值 `[-10000, 100000]`；光环倍率 `[0.05, 10.0]`；拦截 `OnCardFired -> Charge(Self, >=90% CD)` 与 `ForceUse(Self)` 死循环。 |
| **#6** | 奇遇跳转图三色 DFS 防环与死胡同 | 拦事件链自环/多跳环、悬空不存在目标、空选项软锁与非法 `minDay > maxDay`。 |
| **#7** | 中文描述与 `{attr.*}` 四阶真渲染 | 拦缺失中文标题、未完成 `TODO/TBD`、花括号错配，并对全部品阶调用 `TooltipFormatter` 真实试渲染确认 **0 个 `"?"`**。 |
| **#8** | PNG 魔数 / 尺寸 / 体积 / SHA-256 | 仅放行真实 PNG 魔数、`64~1024px`、`<= 2MB`（总 `<= 32MB`）、`art_manifest.json` 哈希一致；**严拒 Unity 原生不解码的 WebP**。 |
| **#9** | 50 场真实 `CombatSimulator` 压力体检 | 隔离 Overlay 上真跑 50 场四阶 + 13 附魔 + 相邻联动实战：硬拦异常 `>0`、单场 `>200ms`、数值 `NaN`、超 20% 场次 300s 僵局，并给出胜率调平参考。 |
| **#10** | 幽灵池隔离与回归不变量 | 确认玩法 Mod `ForcesGhostPoolIsolation == true`（永不写 `standard` 池）、`UnknownRuleTypes == 0`、Wire 映射 100% 合法、`RollbackToBase()` 零残留。 |

---

## 五、高频易错字段与单位速查表

| 字段 / 概念 | 正确写法 | 常见致命错误 |
| :--- | :--- | :--- |
| `CooldownMax` / `HasteAmount` / `SlowAmount` / `FreezeAmount` / `ChargeAmount` | **毫秒 (ms)**：如 `4000` 表示 4 秒 | ❌ 写成 `4`（秒当毫秒，直接被 Gate #5 拦截） |
| `tiers.*.abilityIds` / `auraIds` | 显式列出各阶激活的能力 ID，如 `["0", "1"]` | ❌ 留空导致旧版战斗 0 触发（V13.1 引擎已兜底，但仍建议显式填写） |
| 玩家属性前置条件（如金币 ≥ 20） | `TPrerequisitePlayer` + `subject: TTargetPlayerAbsolute` + `conditions: TPlayerConditionalAttribute` | ❌ 写引擎不存在的 `TPrerequisitePlayerAttribute` |
| 奇遇步骤选项（`EncounterStep`） | `attributes: { "BuyPrice": 6 }` + `abilities`（`TTriggerOnCardSelected`）+ `nextEncounterOnSelection` | ❌ 编造 `effects` 或 `costGold` 字段 |
| 卡图格式与推荐尺寸 | **标准 PNG**：Small/Large 物品 `512×1024`，Medium 物品与技能 `512×512` | ❌ 传 WebP（Unity `LoadImage` 不解码）或超 1024px |
| 动态文案占位符 | `{attr.DamageAmount}`、`{attr.HasteAmount}`、`{attr.Custom_0}` | ❌ 引用该品阶未定义的属性名导致渲染出 `"?"` |

---

## 六、Web 工坊公网安全模式说明

- **本机单机访问（`127.0.0.1` / `::1` 且无外部代理头）**：默认全功能开放读写与发布热重载。
- **公网服务器部署**：默认自动切换为**只读安全沙盒**（允许浏览 3,368 原版卡、查阅规则目录、在线编辑与调用内存 10 道铁闸压测、导出 `.bazaarmod.zip`，但拦截写入磁盘与热重载返回 `403`）。
- **服主远程解锁写入**：启动服务端时设置环境变量 `BAZAAR_WORKSHOP_TOKEN=<强口令>`，在网页右上角填入该口令即可通过 `X-Workshop-Token`（常量时间比对）安全解锁远程创作与热发布；或在受控内网设置 `BAZAAR_WORKSHOP_MODE=open`。
