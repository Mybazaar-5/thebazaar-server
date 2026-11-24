# TheBazaar V15.4 属性单位、五阶标准价、标签、附魔、英雄与 30 官方 VFX 别名速查表

> 严格对齐 `BazaarCore/Models/GameModeModels.cs`、`BazaarCore/Mods/MechanicModels.cs` 与 `BazaarCore/Mods/ModRuleCatalog.cs` 单一事实源。

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
| `Multicast` | 次 | `1 ~ 10` | 多重施放次数 |
| `PercentCooldownReduction` | % | `0 ~ 90` | 冷却缩减百分比（运行期强钳制上限 90%） |
| `BuyPrice` / `SellPrice` | 金币 | `0 ~ 1000` | 商店买/卖价格（见下方五阶权威标准价表） |
| `Custom_0` ~ `Custom_8` | 自由/计数槽 | `-10000 ~ 100000` | **开放沙盒机制 `syncAttribute` 合法白名单范围（`Custom_0..Custom_8`，推荐默认计数角标槽 `Custom_3`）**，可用于 `{attr.Custom_0}` |

## 2. 官方五阶权威标准买卖价（`StandardPricesSettings`）

> 注意：**Skill 青铜（Bronze）出售价为 `2`**（并非 `buy / 2`）；其余品阶与尺寸均为下表精确值。

| 类型 / 尺寸 | Bronze（买/卖） | Silver（买/卖） | Gold（买/卖） | Diamond（买/卖） | Legendary（买/卖） |
| :--- | :---: | :---: | :---: | :---: | :---: |
| **Small（1 格 · 512×1024）** | `2 / 1` | `4 / 2` | `8 / 4` | `16 / 8` | `24 / 12` |
| **Medium（2 格 · 512×512）** | `4 / 2` | `8 / 4` | `16 / 8` | `32 / 16` | `48 / 24` |
| **Large（3 格 · 768×512）** | `6 / 3` | `12 / 6` | `24 / 12` | `48 / 24` | `64 / 32` |
| **Skill（技能 · 512×512）** | `5 / 2` | `10 / 5` | `20 / 10` | `40 / 20` | `50 / 25` |

## 3. 英雄池语义红线（`heroes`）

> ⚠️ **切勿误解 `Common`！**
> - **`Common`**：**仅代表中立 / 奇物店（Curio）专属池**，**不会**出现在瓦内莎、杜利等 8 位常规英雄商店中！
> - **8 位可游玩常规英雄**：`Vanessa`（瓦内莎）、`Dooley`（杜利）、`Pygmalien`（皮格马利翁）、`Mak`（马克）、`Karnok`（卡诺克）、`Jules`（朱尔斯）、`Stelle`（斯黛拉）、`Hero8`（第 8 位可游玩英雄）。
> - 若希望卡牌在特定英雄商店刷出，填写该英雄名；若希望全常规英雄商店都能刷到，请明确列出全部 8 位可游玩英雄；若只想在奇物店刷出才写 `["Common"]`。

## 4. 常用可见标签（`tags`）
`Weapon`、`Tool`、`Apparel`、`Aquatic`、`Friend`、`Core`、`Tech`、`Property`、`Potion`、`Relic`、`Reagent`、`Food`、`Vehicle`、`Toy`、`Merchant`、`Dragon`、`Instrument`、`Dinosaur`、`Ray`、`Drone`、`Loot`、`Quest`

## 5. 常用隐藏标签（`hiddenTags`）
`Damage`、`Shield`、`Burn`、`Poison`、`Heal`、`Regen`、`Haste`、`Slow`、`Freeze`、`Charge`、`Crit`、`Ammo`、`Economy`、`NonWeapon`、`Active`、`Passive`

## 6. 全部 13 种附魔（`enchantments`）
`Heavy`（沉重）、`Icy`（冰霜）、`Turbo`（涡轮）、`Shielded`（护盾）、`Restorative`（恢复）、`Toxic`（剧毒）、`Fiery`（烈焰）、`Shiny`（闪亮）、`Deadly`（致命）、`Radiant`（圣光）、`Obsidian`（黑曜石）、`Golden`（黄金）、`Mossy`（苔藓）

## 7. 官方 30 个友好 VFX 别名全表（`ModRuleCatalog.OfficialVfxAliases`）

> 全库 `GameData.db` 共含 **133** 个原生 Unity Addressable Prefab 键（覆盖 `Abilities`/`Auras`/`Enchantments`）；除原生路径外，可直接使用下列全部 **30 个友好短名别名**（推荐统一写在 camelCase `vfxConfig.vfxOverrideKey` 或机制步骤 `vfxKey`）：

| 类别 | 友好短名别名 | 对应官方 Prefab 键 |
| :--- | :--- | :--- |
| 火焰/爆炸 | `Fireball` / `Fire` | `Assets/TheBazaar/Projectiles/Fireworks/Projectile_HarkurvianLauncher_PV.prefab` |
| 火焰/爆炸 | `Flamethrower` | `Projectile_Flamethrower_PV` |
| 火焰/爆炸 | `Firecracker` | `Assets/TheBazaar/Projectiles/Fireworks/Projectile_Firecracker_PV.prefab` |
| 火焰/爆炸 | `PowderKeg` / `Explosion` | `Assets/TheBazaar/Projectiles/PowderKeg/Projectile_Powderkeg_PV.prefab` |
| 火焰/爆炸 | `FireSlash` | `Assets/TheBazaar/Projectiles/FireSlashEpic/Projectile_Fireslashepic_PV.prefab` |
| 火焰/爆炸 | `FireClaw` | `Assets/TheBazaar/Projectiles/FireClaw/Projectile_FireClaw_PV.prefab` |
| 火焰/爆炸 | `BurnThrow` / `Burn` | `Assets/TheBazaar/Projectiles/PotionsThrowing/Burn/Projectile_PotionsThrowing_Burn_PV.prefab` |
| 弹道/重火 | `Cannonball` | `Assets/TheBazaar/Projectiles/Cannonball/Projectile_Cannonball_PV.prefab` |
| 弹道/重火 | `Missile` | `Assets/TheBazaar/Projectiles/Missile/Projectile_Missile_PV.prefab` |
| 弹道/重火 | `VortexCannon` | `Assets/TheBazaar/Projectiles/VortexCannon/Projectile_VortexCannon_PV.prefab` |
| 激光/电击 | `LaserRed` | `Projectile_LaserBeam_Red_PV` |
| 激光/电击 | `LaserOrange` | `Projectile_LaserBeam_Orange_PV` |
| 激光/电击 | `LaserBlue` | `Projectile_LaserBeam_Blue_PV` |
| 激光/电击 | `LaserGreen` | `Projectile_LaserBeam_Green_PV` |
| 激光/电击 | `SpaceLaser` | `Assets/TheBazaar/Projectiles/SpaceLaser/Projectile_SpaceLaser_PV.prefab` |
| 激光/电击 | `Electric` / `Lightning` | `Projectile_Electric_PV` |
| 斩击/射击 | `DragonSlash` | `Assets/TheBazaar/Projectiles/SlashesDragons/Projectile_DragonSlash_M_PV.prefab` |
| 斩击/射击 | `Slash` | `Assets/TheBazaar/Emitters/Slash/VFX_Slash_Tier2_PV.prefab` |
| 斩击/射击 | `Arrow` | `Assets/TheBazaar/Projectiles/Arrow/Projectile_Damage_Arrow_PV.prefab` |
| 斩击/射击 | `Gunshot` | `Assets/TheBazaar/Projectiles/ProjectilesForDerek/Projectile_ShootSingle_PV.prefab` |
| 异常/辅助 | `IceBurst` / `Freeze` | `Assets/TheBazaar/Projectiles/PotionsThrowing/Freeze/Projectile_PotionsThrowing_Freeze_PV.prefab` |
| 异常/辅助 | `PoisonThrow` / `Poison` | `Assets/TheBazaar/Projectiles/PotionsThrowing/Poison/Projectile_PotionsThrowing_Poison_PV.prefab` |
| 异常/辅助 | `ShieldOrb` | `Assets/TheBazaar/Projectiles/PigglesLauncher/Shield/Projectile_PigglesShield_PV.prefab` |
| 异常/辅助 | `HealOrb` | `Assets/TheBazaar/Projectiles/PigglesLauncher/Heal/Projectile_PigglesHeal_PV.prefab` |
