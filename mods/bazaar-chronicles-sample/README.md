# 大巴扎编年史：星港奇物志（`bazaar-chronicles-sample`）

> **官方风格原创示例 Mod 扩展包 & 社区 Mod 创作完整指南**  
> 遵循 `BazaarModKit` 协议规范（`docs/audit/wave1_l10n_mod_compat.md` 第 2.3 节 & `docs/audit/wave1_core_parity_perf.md` 第 5.3 节），纯 JSON 数据驱动 + 高清 PNG 贴图，**零白块、零客户端崩溃、未装视觉插件亦可优雅降级运行**。

---

## 1. 本示例包内容一览

| 分类 | ID | 中文名 | 规格 / 定位 | 白名单视觉载体 (`baseVisualTemplateId`) |
| :--- | :--- | :--- | :--- | :--- |
| **物品** | `starport_compass_blade` | 星港罗盘短刃 | Small · 青铜起 · 武器/工具/经济 | `Kukri` (`03b1fd16-...`, Item/Small) |
| **物品** | `aether_bulwark_lantern` | 以太壁垒提灯 | Medium · 白银起 · 护盾/加速/经济 | `Globe` (`07df297d-...`, Item/Medium) |
| **物品** | `solar_kiln_furnace` | 日耀熔炉 | Medium · 青铜起 · 灼烧/充能核心 | `Welding Helmet` (`08630fca-...`, Item/Medium) |
| **物品** | `starport_dreadnought_vault` | 星港无畏宝库舰 | Large · 黄金起 · 经济+武盾烧旗舰 | `Lightning Rod` (`0930fbea-...`, Item/Large) |
| **技能** | `starport_trade_charter` | 星港贸易特许状 | Skill · 白银起 · 经济转武器伤害 | `Backroom Dealings` (`0045d15e-...`, Skill) |
| **技能** | `ember_tide_resonance` | 余烬潮汐共鸣 | Skill · 青铜起 · 灼烧与护盾互促 | `Buffed and Polished` (`0283b102-...`, Skill) |
| **奇遇链** | `event_smuggler_vessel` → `event_smuggler_vault` | 星港黑市走私船 → 黑市船舱密室 | 2 步联动事件链（奇物三选一 / 改装 / 技能 / 经济） | `The Docks` / `Apothecary` + 6 个白名单 `EncounterStep` |
| **词条** | `mutator_starport_boom` / `mutator_ember_tempest` | 星港商潮 / 余烬风暴 | 2 条原创扩展肉鸽规则词条 | 纯规则配置 |

---

## 2. 标准目录结构规范

所有社区 Mod 统一放置于仓库或服务端运行目录的 `mods/<mod-id>/` 下（`<mod-id>` 仅允许小写字母、数字与连字符 `-`）：

```text
mods/bazaar-chronicles-sample/
├── mod.json                     # Mod 清单（ID、版本、加载顺序、幽灵池隔离声明）
├── cards/
│   └── custom_cards.json        # 自定义物品与技能定义（仅在服务端执行规则）
├── events/
│   └── custom_events.json       # 自定义奇遇事件与多步事件链定义
├── modes/
│   └── custom_mutators.json     # 自定义肉鸽词条/模式规则
├── localization/
│   └── zh-CN.json               # 简体中文本地化覆盖表
├── assets/
│   ├── art_manifest.json        # PNG 尺寸与 SHA-256 哈希清单
│   ├── starport_compass_blade.png
│   ├── aether_bulwark_lantern.png
│   ├── solar_kiln_furnace.png
│   ├── starport_dreadnought_vault.png
│   ├── starport_trade_charter.png
│   └── ember_tide_resonance.png
└── README.md
```

---

## 3. 创作安全铁律（严防白块与序列化崩溃）

1. **必须绑定合法白名单 `baseVisualTemplateId`**：
   - 原版客户端的 MessagePack 协议在编译期固化且 `DTOUtils` / `AssetLoader` 会按 `TemplateId` 查本地数据库与 `catalog.bin`。
   - 因此每张自定义卡/事件都必须指定一个真实存在于 `dist/data/valid_card_ids.json` 与 `GameData.db` 的原版 `baseVisualTemplateId`，且必须严格满足：
     - **同 `Type`**（`Item` 只能用 `Item`，`Skill` 只能用 `Skill`，`EventEncounter` 只能用 `EventEncounter`）；
     - **物品必须同 `Size`**（`Small` 用 `Small`、`Medium` 用 `Medium`、`Large` 用 `Large`），避免 3D 卡框穿模与格子错位。
2. **自定义卡图贴图规范**：
   - 仅支持 `PNG` 或 `WebP` 格式，单图体积 `<= 2 MB`，分辨率上限 `1024×1024`；
   - 推荐尺寸：竖幅物品卡 `512×1024` 或方幅 `512×512`，圆形技能徽章 `512×512`（或 `256×256`）；
   - 通过 `python tools/gen_sample_mod_art.py` 可一键重新生成本示例包的全部 6 张鎏金卡图并校验 SHA-256。
3. **动态 Tooltip 占位符规范**：
   - 自定义卡牌的 Tooltip 请使用 `{attr.DamageAmount}`、`{attr.ShieldApplyAmount}`、`{attr.BurnApplyAmount}`、`{attr.Custom_0}` 等直接读取实例同步属性的模板，切勿依赖载体原版卡的 `{ability.*}` 索引。
4. **幽灵池安全隔离**：
   - 包含非标准平衡卡牌/词条的 Mod 对局请将 `affectsGhostPool` 设为 `false`（服务端会自动隔离至自定义幽灵池，不污染标准天梯镜像）。

---

## 4. 校验与本地测试步骤

1. **重新生成并校验卡图与 SHA-256**：
   ```powershell
   python tools/gen_sample_mod_art.py
   ```
2. **核验所有 `baseVisualTemplateId` 均命中权威数据库且同 `Type`/同 `Size`**：
   - 启动 `BazaarServer` 时 `BazaarModKit` 会自动校验 `mod.json`、`cards/`、`events/`、`assets/` 的路径安全、PNG 魔数与白名单载体匹配，并通过 `/api/mods/manifest` 与 `/api/mods/bundle` 向 `BazaarHub` 与 `BazaarLauncher` 增量下发自定义贴图与中文文案。
3. **基于本示例创建你自己的 Mod**：
   - 复制整个 `mods/bazaar-chronicles-sample/` 文件夹并重命名为 `mods/your-mod-id/`；
   - 修改 `mod.json` 中的 `id`、`displayNameZh` 与全部卡牌的 `customId` 前缀；
   - 替换 `assets/*.png` 为你的原创插画并更新 `sha256` 哈希，即可即插即用！
