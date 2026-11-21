# TheBazaar 常见属性单位、标签、附魔与英雄速查表

## 1. 单位铁律（毫秒 vs 点数）

| 属性名 | 单位 | 合法参考范围 | 说明 |
| :--- | :---: | :--- | :--- |
| `CooldownMax` | **毫秒 (ms)** | `1000 ~ 60000` | **4 秒必须写 `4000`！写 `4` 会被 Gate #5 拦截** |
| `HasteAmount` | **毫秒 (ms)** | `500 ~ 5000` | 加速持续时长，1 秒 = `1000` |
| `SlowAmount` | **毫秒 (ms)** | `500 ~ 5000` | 减速持续时长，1 秒 = `1000` |
| `FreezeAmount` | **毫秒 (ms)** | `500 ~ 4000` | 冻结持续时长，1 秒 = `1000` |
| `ChargeAmount` | **毫秒 (ms)** | `500 ~ 3000` | 立即推进冷却进度，禁止自触发自充 ≥90% CD |
| `DamageAmount` | 点 | `1 ~ 100000` | 基础伤害 |
| `ShieldApplyAmount` | 点 | `1 ~ 100000` | 基础护盾 |
| `BurnApplyAmount` | 层 | `1 ~ 5000` | 灼烧层数（每 0.5 秒结算） |
| `PoisonApplyAmount` | 层 | `1 ~ 5000` | 剧毒层数（每秒结算，无视护盾） |
| `HealAmount` | 点 | `1 ~ 100000` | 回复生命 |
| `RegenApplyAmount` | 层 | `1 ~ 5000` | 再生层数 |
| `CritChance` | % | `0 ~ 100` | 暴击率 |
| `BuyPrice` / `SellPrice` | 金币 | `0 ~ 1000` | 商店买/卖价格 |
| `Custom_0` ~ `Custom_9` | 自由槽 | `-10000 ~ 100000` | 自定义数值成长/经济槽位，可用于 `{attr.Custom_0}` |

## 2. 常用可见标签（`tags`）
`Weapon`、`Tool`、`Apparel`、`Aquatic`、`Friend`、`Core`、`Tech`、`Property`、`Potion`、`Relic`、`Reagent`、`Food`、`Vehicle`、`Toy`、`Merchant`、`Dragon`、`Instrument`、`Dinosaur`、`Ray`、`Drone`、`Loot`、`Quest`

## 3. 常用隐藏标签（`hiddenTags`）
`Damage`、`Shield`、`Burn`、`Poison`、`Heal`、`Regen`、`Haste`、`Slow`、`Freeze`、`Charge`、`Crit`、`Ammo`、`Economy`、`NonWeapon`、`Active`、`Passive`

## 4. 全部 13 种附魔（`enchantments`）
`Heavy`（沉重）、`Icy`（冰霜）、`Turbo`（涡轮）、`Shielded`（护盾）、`Restorative`（恢复）、`Toxic`（剧毒）、`Fiery`（烈焰）、`Shiny`（闪亮）、`Deadly`（致命）、`Radiant`（圣光）、`Obsidian`（黑曜石）、`Golden`（黄金）、`Mossy`（苔藓）

## 5. 全部英雄（`heroes`）
`Common`（全英雄通用）、`Vanessa`、`Dooley`、`Pygmalien`、`Mak`、`Karnok`、`Jules`、`Stelle`、`Hero8`
