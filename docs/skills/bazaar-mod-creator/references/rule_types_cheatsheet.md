# TheBazaar V15.4 规则节点 `$type` 与开放沙盒机制权威速查表

> 所有 `$type` 与机制白名单均经 `RuleModels.cs`、`MechanicModels.cs`、`AbilityExecutor.cs` 与 `ValueAndConditionEvaluator.cs` 源码核验，100% 通过 Gate #4。

## 1. 高频触发器（`Trigger.$type`）

| `$type` | 中文含义 | 常见字段 |
| :--- | :--- | :--- |
| `TTriggerOnCardFired` | 本卡冷却条走满开火时 | 无（要求物品配 `CooldownMax` 毫秒） |
| `TTriggerOnItemUsed` | 指定物品被使用时 | `subject`: `TTargetCardPositional` / `TTargetCardSection` |
| `TTriggerOnCardSelected` | 奇遇选项 (`EncounterStep`) 被选中时 | 无（奇遇步骤唯一推荐触发器） |
| `TTriggerOnFightStarted` | 战斗开始（0ms） | 无 |
| `TTriggerOnFightEnded` | 战斗结束 | `combatOutcome`: `"Win"` / `"Lose"` |
| `TTriggerOnDayStarted` | 每日开始 | 无 |
| `TTriggerOnCardCritted` | 卡牌暴击时 | `subject` |
| `TTriggerOnCardSold` | 卡牌被卖出时 | `subject` |
| `TTriggerOnCardPurchased` | 卡牌被购买时 | `subject` |
| `TTriggerOnPlayerAttributeChanged` | 玩家属性变化时 | `attributeType` (`"Burn"`/`"Shield"`/`"Gold"`), `changeType` (`"Gain"`/`"Loss"`) |

## 2. 高频动作（`Action.$type`）

| `$type` | 中文含义 | 常用目标与字段 |
| :--- | :--- | :--- |
| `TActionPlayerDamage` | 造成伤害 | `target`: `TTargetPlayerRelative(Opponent)`，默认读本卡 `DamageAmount` |
| `TActionPlayerShieldApply` | 施加护盾 | `target`: `TTargetPlayerRelative(Self)`，默认读 `ShieldApplyAmount` |
| `TActionPlayerBurnApply` | 施加灼烧 | `target`: `TTargetPlayerRelative(Opponent)`，默认读 `BurnApplyAmount` |
| `TActionPlayerPoisonApply` | 施加剧毒 | `target`: `TTargetPlayerRelative(Opponent)`，默认读 `PoisonApplyAmount` |
| `TActionPlayerHeal` | 治疗生命 | `target`: `TTargetPlayerRelative(Self)`，默认读 `HealAmount` |
| `TActionPlayerRegenApply` | 施加再生 | `target`: `TTargetPlayerRelative(Self)`，默认读 `RegenApplyAmount` |
| `TActionPlayerModifyAttribute` | 修改玩家属性 | `attributeType` (`Gold`/`Experience`/`Prestige`/`Income`/`HealthMax`), `operation` (`Add`/`Subtract`), `value`, `target`: `TTargetPlayerAbsolute(Player)` |
| `TActionCardHaste` | 加速卡牌 | `target`: `TTargetCardPositional(origin:"Self", targetMode:"Neighbor"/"Left"/"Right")` |
| `TActionCardSlow` | 减速敌方卡牌 | `target`: `TTargetCardRandom(targetSection:"OpponentHand")` |
| `TActionCardFreeze` | 冻结敌方卡牌 | `target`: `TTargetCardRandom(targetSection:"OpponentHand")` |
| `TActionCardCharge` | 充能卡牌 | `target`: `TTargetCardSelf` / `TTargetCardPositional`（禁止自开火自充能 ≥90% 冷却！） |
| `TActionCardModifyAttribute` | 修改卡牌属性 | `attributeType`, `operation` (`Add`/`Multiply`), `value`, `target` |
| `TActionCardUpgrade` | 升级卡牌品阶 | `target`: `TTargetCardXMost(targetSection:"SelfHand", targetMode:"LeftMostCard")` |
| `TActionCardEnchantRandom` | 随机附魔 | `target` |
| `TActionGameSpawnCards` | 向玩家发放卡牌 | `target`: `TTargetPlayerAbsolute(Player)`, `spawnContext`: `TSpawnContextQuery` + `TSpawnGroup` + `TSpawnFilterIdList` |
| `TActionAnd` | 顺序组合多动作 | `actions: [ ... ]` |

## 3. 光环动作与特效配置（`Aura.Action.$type` & `vfxConfig`）

- `TAuraActionCardModifyAttribute`：持续修改卡牌属性（`operation: "Multiply"` 时值必须在 `[0.05, 10.0]`；支持 `TReferenceValueCardAttribute` + `modifier: { multiplier, addOffset }` 比例派生计算）。
- `TAuraActionPlayerModifyAttribute`：持续修改玩家属性。
- `TAuraActionCardAddTagsList` / `TAuraActionCardRemoveTagsList`。
- **`vfxConfig` 统一 camelCase 写法**（Ability 与 Aura 均支持）：
  ```json
  "vfxConfig": {
    "vfxOverrideKey": "Fireball",
    "vfxShouldPlay": true,
    "vfxIsTakeover": false
  }
  ```

## 4. 仅有的 4 种合法前置条件（`Prerequisites[*].$type`）

> ⚠️ **严禁编造 `TPrerequisitePlayerAttribute`！** 引擎中不存在该类型，Gate #4 会直接阻断！

1. `TPrerequisitePlayer`：检查玩家属性（如金币 ≥ 20）的唯一权威写法：
   ```json
   {
     "$type": "TPrerequisitePlayer",
     "subject": {
       "$type": "TTargetPlayerAbsolute",
       "targetMode": "Player",
       "conditions": {
         "$type": "TPlayerConditionalAttribute",
         "attributeType": "Gold",
         "comparisonOperator": "GreaterThanOrEqual",
         "comparisonValue": { "$type": "TFixedValue", "value": 20.0 }
       }
     }
   }
   ```
2. `TPrerequisiteCardCount`：检查场上符合条件的卡牌数量。
3. `TPrerequisiteRun`：检查局内天数/小时/沙暴状态。
4. `TPrerequisiteCardAttributeComparator`：比较双卡属性值。

## 5. V14/V15 开放声明式沙盒机制白名单（`mechanics/*.json`）

### 5.1 全部 10 个管线钩子（`AllowedHooks`）
`OnFightStart`、`OnCardFired`、`BeforeDamage`、`BeforeStatusApply`、`BeforeCardStateChange`、`BeforeCardFire`、`AfterCardFire`、`OnCustomEvent`、`OnTriggerEvent`、`PostCombatCommit`

### 5.2 全部 7 个合法作用域（`AllowedScopes`）
`SelfCard`、`OwnerBoard`、`EnemyBoard`、`OwnerPlayer`、`EnemyPlayer`、`Global`、`Any`

### 5.3 全部 18 种安全操作（`AllowedOps`）
- **流程控制**：`If`、`Repeat`（硬上限 ≤20 次，支持 `initialDelayMs`/`intervalMs`）、`ScheduleDelayed`、`Stop`
- **管线改写**：`CancelHook`、`ModifyHookAmount`
- **状态黑板**：`SetFlag`、`ClearFlag`、`ModifyCounter`（`syncAttribute` 合法范围：`Custom_0` ~ `Custom_8`）
- **战斗动作**：
  - `DealDamage`（支持 `bypassShield`、`vfxKey` / `vfxKeyParam`）
  - `ApplyStatus`（9 种状态：`Shield`/`Burn`/`Poison`/`Heal`/`Regen`/`Haste`/`Slow`/`Freeze`/`Charge`）
  - `ModifyCardAttribute`（含 **`grantIfMissing`**，默认 `false` 仅增益原本拥有该输出能力的卡；需赋予新属性时显式写 `true`）
  - `ModifyPlayerAttribute`（14 种合法玩家属性：`Health`/`HealthMax`/`Shield`/`Burn`/`Poison`/`HealthRegen`/`Regen`/`Rage`/`RageMax`/`Tempo`/`Joy`/`Gold`/`Income`/`PercentDamageReduction`）
  - `ChangeCardState`（`changeKind`: `Disable`/`Destroy`/`Repair`，支持 `reason` 与 `suppressNormalDestroyTriggers`）
  - `ForceUseCard`
- **联动与局外结转**：`EmitCustomEvent`、`QueuePostCombatRemoveCard`、`QueuePostCombatModifyAttr`（支持 `grantIfMissing`）

### 5.4 目标模式、参数类型与 3D 计数徽章（`counterDisplay` / `counterDisplays`）
- **13 种目标模式（`AllowedTargetModes`）**：`Self`、`HookTarget`、`HookSource`、`AllAllies`、`OtherAllies`、`AllEnemies`、`Adjacent`、`Left`、`Right`、`LeftMostAlly`、`RightMostAlly`、`RandomAllies`、`RandomEnemies`。
- **8 种参数类型（`AllowedParamTypes`）**：`int`、`float`、`string`、`bool`、`tag`、`attribute`、`targetMode`、`vfxKey`。
- **21 种计数徽章绑定/动态上限属性（`AllowedCounterDisplayAttributes`）**：`Custom_0..Custom_8`、`Counter`、`Ammo`、`AmmoMax`、`DamageAmount`、`ShieldApplyAmount`、`BurnApplyAmount`、`PoisonApplyAmount`、`HealAmount`、`RegenApplyAmount`、`CritChance`、`Multicast`、`CooldownMax`。
- **徽章外观枚举**：
  - `style`：`auto` | `pips` | `bar` | `gem` | `pill`
  - `valueType`：`count` | `progress` | `percent` | `seconds` | `multiplier` | `delta`
  - `position`：`bottom` | `bottomRight` | `topLeft` | `topRight`
  - 动态上限字段：`maxAttr`（与 `maxConst` / `maxParam` 互补）。
