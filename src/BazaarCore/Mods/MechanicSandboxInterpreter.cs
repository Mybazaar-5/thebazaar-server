using System.Globalization;
using System.Text.Json;
using BazaarCore.Runtime;

namespace BazaarCore.Mods;

/// <summary>
/// 声明式自定义机制安全沙盒解释器（零反射、零 OS/IO 权限、强制步数预算与深度上限）：
/// 负责静态校验 <see cref="MechanicDefinition"/> 及在战斗管线内按受控门面 <see cref="CombatMechanicRuntime"/> 执行步骤流。
/// </summary>
public static class MechanicSandboxInterpreter
{
    public const int MaxAstDepth = 8;

    /// <summary>执行步骤列表（受单钩子/单帧步数硬预算与嵌套深度上限保护）。</summary>
    public static void ExecuteSteps(
        IReadOnlyList<MechanicStep>? steps,
        MechanicDefinition def,
        CardMechanicAttachment attachment,
        CardInstance selfCard,
        PlayerState ownerPlayer,
        PlayerState opponentPlayer,
        CombatPipelineEvent pipeEvent,
        CombatMechanicRuntime runtime,
        int depth)
    {
        if (steps == null || steps.Count == 0 || depth > MaxAstDepth) return;

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step == null || string.IsNullOrWhiteSpace(step.Op)) continue;
            if (!runtime.TryConsumeStep(def.Id, selfCard, pipeEvent.HookName))
                return;

            string op = step.Op.Trim();
            switch (op.ToLowerInvariant())
            {
                case "stop":
                    return;

                case "if":
                {
                    bool cond = EvaluateCondition(step.Condition, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime, depth + 1);
                    ExecuteSteps(cond ? step.Then : step.Else, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime, depth + 1);
                    break;
                }

                case "repeat":
                {
                    int rawCount = (int)Math.Round(EvaluateValue(step.Count, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime));
                    int count = Math.Clamp(rawCount, 0, CombatMechanicRuntime.MaxRepeatCount);
                    if (count <= 0 || step.Steps == null || step.Steps.Count == 0) break;

                    int initialDelayMs = step.InitialDelayMs != null
                        ? Math.Clamp((int)Math.Round(EvaluateValue(step.InitialDelayMs, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime)), 0, 10000)
                        : 0;
                    int intervalMs = step.IntervalMs != null
                        ? Math.Clamp((int)Math.Round(EvaluateValue(step.IntervalMs, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime)), 0, 10000)
                        : 0;

                    for (int k = 0; k < count; k++)
                    {
                        int totalDelay = initialDelayMs + intervalMs * k;
                        if (totalDelay > 0)
                        {
                            int delay = Math.Clamp(totalDelay, 20, 60000);
                            var capturedSteps = step.Steps;
                            runtime.ScheduleDelayed(delay, def.Id, selfCard, _ =>
                            {
                                ExecuteSteps(capturedSteps, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime, depth + 1);
                            });
                        }
                        else
                        {
                            if (!runtime.TryConsumeStep(def.Id, selfCard, pipeEvent.HookName)) return;
                            ExecuteSteps(step.Steps, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime, depth + 1);
                        }
                    }
                    break;
                }

                case "scheduledelayed":
                {
                    int delayMs = Math.Clamp(
                        (int)Math.Round(EvaluateValue(step.DelayMs, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime)),
                        20,
                        60000);
                    if (step.Steps == null || step.Steps.Count == 0) break;
                    var capturedSteps = step.Steps;
                    runtime.ScheduleDelayed(delayMs, def.Id, selfCard, _ =>
                    {
                        ExecuteSteps(capturedSteps, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime, depth + 1);
                    });
                    break;
                }

                case "cancelhook":
                    pipeEvent.Cancelled = true;
                    break;

                case "modifyhookamount":
                {
                    double val = EvaluateValue(step.Value, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime);
                    double next = (step.Operation ?? "Set").ToLowerInvariant() switch
                    {
                        "add" => pipeEvent.Amount + val,
                        "subtract" => pipeEvent.Amount - val,
                        "multiply" => pipeEvent.Amount * Math.Clamp(val, 0, 100),
                        _ => val
                    };
                    if (double.IsNaN(next) || double.IsInfinity(next)) next = 0;
                    pipeEvent.Amount = Math.Clamp(next, 0, 1_000_000);
                    pipeEvent.AmountModified = true;
                    break;
                }

                case "setflag":
                {
                    if (string.IsNullOrWhiteSpace(step.Flag)) break;
                    if (string.Equals(step.Target, "Combat", StringComparison.OrdinalIgnoreCase))
                        runtime.SetCombatFlag(step.Flag);
                    else
                    {
                        var c = ResolveSingleCardRef(step.Target, selfCard, pipeEvent);
                        if (c != null) runtime.SetCardFlag(c, step.Flag);
                    }
                    break;
                }

                case "clearflag":
                {
                    if (string.IsNullOrWhiteSpace(step.Flag)) break;
                    if (string.Equals(step.Target, "Combat", StringComparison.OrdinalIgnoreCase))
                        runtime.ClearCombatFlag(step.Flag);
                    else
                    {
                        var c = ResolveSingleCardRef(step.Target, selfCard, pipeEvent);
                        if (c != null) runtime.ClearCardFlag(c, step.Flag);
                    }
                    break;
                }

                case "modifycounter":
                {
                    if (string.IsNullOrWhiteSpace(step.Key)) break;
                    double val = EvaluateValue(step.Value, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime);
                    if (string.Equals(step.Target, "Combat", StringComparison.OrdinalIgnoreCase))
                        runtime.ModifyCombatCounter(step.Key, step.Operation ?? "Add", val);
                    else
                    {
                        var c = ResolveSingleCardRef(step.Target, selfCard, pipeEvent);
                        if (c != null) runtime.ModifyCardCounter(c, step.Key, step.Operation ?? "Add", val, step.SyncAttribute);
                    }
                    break;
                }

                case "dealdamage":
                {
                    double amt = EvaluateValue(step.Amount ?? step.Value, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime);
                    var targetPlayer = string.Equals(step.PlayerTarget, "Self", StringComparison.OrdinalIgnoreCase)
                        ? ownerPlayer
                        : opponentPlayer;
                    string? vfx = (!string.IsNullOrWhiteSpace(step.VfxKeyParam) ? ResolveParamString(step.VfxKeyParam!, def, attachment) : null)
                        ?? step.VfxKey;
                    runtime.DealDamage(targetPlayer, selfCard, amt, step.BypassShield, vfx);
                    break;
                }

                case "applystatus":
                {
                    if (string.IsNullOrWhiteSpace(step.Status)) break;
                    double amt = EvaluateValue(step.Amount ?? step.Value, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime);
                    bool isCardStatus = step.Status.Equals("Haste", StringComparison.OrdinalIgnoreCase)
                        || step.Status.Equals("Slow", StringComparison.OrdinalIgnoreCase)
                        || step.Status.Equals("Freeze", StringComparison.OrdinalIgnoreCase)
                        || step.Status.Equals("Charge", StringComparison.OrdinalIgnoreCase);

                    if (isCardStatus)
                    {
                        var targets = runtime.ResolveTargets(
                            step.TargetSpec ?? new MechanicTargetSpec { Mode = "Self" },
                            selfCard, ownerPlayer, opponentPlayer, pipeEvent,
                            k => ResolveParamString(k, def, attachment),
                            k => ResolveParamNumber(k, def, attachment));
                        for (int t = 0; t < targets.Count; t++)
                            runtime.ApplyStatus(step.Status, ownerPlayer, selfCard, targets[t], null, amt);
                    }
                    else
                    {
                        bool defaultEnemy = step.Status.Equals("Burn", StringComparison.OrdinalIgnoreCase)
                            || step.Status.Equals("Poison", StringComparison.OrdinalIgnoreCase);
                        var targetPlayer = string.IsNullOrWhiteSpace(step.PlayerTarget)
                            ? (defaultEnemy ? opponentPlayer : ownerPlayer)
                            : (step.PlayerTarget.Equals("Opponent", StringComparison.OrdinalIgnoreCase) ? opponentPlayer : ownerPlayer);
                        runtime.ApplyStatus(step.Status, ownerPlayer, selfCard, null, targetPlayer, amt);
                    }
                    break;
                }

                case "modifycardattribute":
                {
                    string? attr = (!string.IsNullOrWhiteSpace(step.AttributeParam) ? ResolveParamString(step.AttributeParam!, def, attachment) : null)
                        ?? step.Attribute;
                    if (string.IsNullOrWhiteSpace(attr)) break;
                    double val = EvaluateValue(step.Value ?? step.Amount, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime);
                    bool permanent = string.Equals(step.Duration, "Permanent", StringComparison.OrdinalIgnoreCase);
                    var targets = runtime.ResolveTargets(
                        step.TargetSpec ?? new MechanicTargetSpec { Mode = "Self" },
                        selfCard, ownerPlayer, opponentPlayer, pipeEvent,
                        k => ResolveParamString(k, def, attachment),
                        k => ResolveParamNumber(k, def, attachment));
                    for (int t = 0; t < targets.Count; t++)
                        runtime.ModifyCardAttribute(targets[t], attr, step.Operation ?? "Add", val, permanent);
                    break;
                }

                case "modifyplayerattribute":
                {
                    string? attr = (!string.IsNullOrWhiteSpace(step.AttributeParam) ? ResolveParamString(step.AttributeParam!, def, attachment) : null)
                        ?? step.Attribute;
                    if (string.IsNullOrWhiteSpace(attr)) break;
                    double val = EvaluateValue(step.Value ?? step.Amount, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime);
                    var targetPlayer = string.Equals(step.PlayerTarget, "Opponent", StringComparison.OrdinalIgnoreCase)
                        ? opponentPlayer
                        : ownerPlayer;
                    runtime.ModifyPlayerAttribute(targetPlayer, attr, step.Operation ?? "Add", val);
                    break;
                }

                case "changecardstate":
                {
                    if (string.IsNullOrWhiteSpace(step.ChangeKind)) break;
                    string? vfx = (!string.IsNullOrWhiteSpace(step.VfxKeyParam) ? ResolveParamString(step.VfxKeyParam!, def, attachment) : null)
                        ?? step.VfxKey;
                    var targets = runtime.ResolveTargets(
                        step.TargetSpec ?? new MechanicTargetSpec { Mode = "Self", IncludeDisabled = step.ChangeKind.Equals("Repair", StringComparison.OrdinalIgnoreCase) },
                        selfCard, ownerPlayer, opponentPlayer, pipeEvent,
                        k => ResolveParamString(k, def, attachment),
                        k => ResolveParamNumber(k, def, attachment));
                    for (int t = 0; t < targets.Count; t++)
                        runtime.ChangeCardState(targets[t], selfCard, step.ChangeKind, step.Reason, step.SuppressNormalDestroyTriggers, vfx);
                    break;
                }

                case "forceusecard":
                {
                    var targets = runtime.ResolveTargets(
                        step.TargetSpec ?? new MechanicTargetSpec { Mode = "Self" },
                        selfCard, ownerPlayer, opponentPlayer, pipeEvent,
                        k => ResolveParamString(k, def, attachment),
                        k => ResolveParamNumber(k, def, attachment));
                    for (int t = 0; t < targets.Count; t++)
                        runtime.ForceUseCard(targets[t], selfCard);
                    break;
                }

                case "emitcustomevent":
                {
                    if (string.IsNullOrWhiteSpace(step.EventName)) break;
                    double payload = step.PayloadValue != null
                        ? EvaluateValue(step.PayloadValue, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime)
                        : 0;
                    runtime.EmitCustomEvent(step.EventName, ownerPlayer, selfCard, selfCard, payload);
                    break;
                }

                case "queuepostcombatremovecard":
                {
                    var spec = step.TargetSpec ?? new MechanicTargetSpec { Mode = "Self", IncludeDisabled = true };
                    var targets = runtime.ResolveTargets(
                        spec, selfCard, ownerPlayer, opponentPlayer, pipeEvent,
                        k => ResolveParamString(k, def, attachment),
                        k => ResolveParamNumber(k, def, attachment));
                    for (int t = 0; t < targets.Count; t++)
                        runtime.QueuePostCombatRemoveCard(targets[t], step.Reason, def.Id);
                    break;
                }

                case "queuepostcombatmodifyattr":
                {
                    string? attr = (!string.IsNullOrWhiteSpace(step.AttributeParam) ? ResolveParamString(step.AttributeParam!, def, attachment) : null)
                        ?? step.Attribute;
                    if (string.IsNullOrWhiteSpace(attr)) break;
                    double val = EvaluateValue(step.Value ?? step.Amount, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime);
                    var spec = step.TargetSpec ?? new MechanicTargetSpec { Mode = "Self", IncludeDisabled = true };
                    var targets = runtime.ResolveTargets(
                        spec, selfCard, ownerPlayer, opponentPlayer, pipeEvent,
                        k => ResolveParamString(k, def, attachment),
                        k => ResolveParamNumber(k, def, attachment));
                    for (int t = 0; t < targets.Count; t++)
                        runtime.QueuePostCombatModifyAttr(targets[t], attr, step.Operation ?? "Add", val, def.Id);
                    break;
                }
            }
        }
    }

    private static CardInstance? ResolveSingleCardRef(string? targetRef, CardInstance selfCard, CombatPipelineEvent pipeEvent)
        => (targetRef ?? "Self").ToLowerInvariant() switch
        {
            "hooktarget" => pipeEvent.TargetCard,
            "hooksource" => pipeEvent.SourceCard,
            _ => selfCard
        };

    private static bool EvaluateCondition(
        MechanicCondition? cond,
        MechanicDefinition def,
        CardMechanicAttachment attachment,
        CardInstance selfCard,
        PlayerState ownerPlayer,
        PlayerState opponentPlayer,
        CombatPipelineEvent pipeEvent,
        CombatMechanicRuntime runtime,
        int depth)
    {
        if (cond == null || depth > MaxAstDepth) return true;

        if (cond.All is { Count: > 0 })
        {
            for (int i = 0; i < cond.All.Count; i++)
                if (!EvaluateCondition(cond.All[i], def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime, depth + 1))
                    return false;
        }
        if (cond.Any is { Count: > 0 })
        {
            bool matched = false;
            for (int i = 0; i < cond.Any.Count; i++)
            {
                if (EvaluateCondition(cond.Any[i], def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime, depth + 1))
                {
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }
        if (cond.Not != null &&
            EvaluateCondition(cond.Not, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime, depth + 1))
            return false;

        if (!string.IsNullOrWhiteSpace(cond.HasFlag))
        {
            bool has = string.Equals(cond.FlagTarget, "Combat", StringComparison.OrdinalIgnoreCase)
                ? runtime.HasCombatFlag(cond.HasFlag)
                : ResolveSingleCardRef(cond.FlagTarget, selfCard, pipeEvent) is { } fc && runtime.HasCardFlag(fc, cond.HasFlag);
            if (!has) return false;
        }

        if (!string.IsNullOrWhiteSpace(cond.ParamBool))
        {
            if (!ResolveParamBool(cond.ParamBool, def, attachment))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(cond.CardHasTag))
        {
            var c = ResolveSingleCardRef(cond.CardTarget, selfCard, pipeEvent);
            if (c == null || !c.HasTag(cond.CardHasTag)) return false;
        }

        if (!string.IsNullOrWhiteSpace(cond.CardHasHiddenTag))
        {
            var c = ResolveSingleCardRef(cond.CardTarget, selfCard, pipeEvent);
            if (c == null || !c.HasHiddenTag(cond.CardHasHiddenTag)) return false;
        }

        if (!string.IsNullOrWhiteSpace(cond.HookReasonEquals) &&
            !string.Equals(pipeEvent.Reason, cond.HookReasonEquals, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(cond.HookChangeKindEquals) &&
            !string.Equals(pipeEvent.ChangeKind, cond.HookChangeKindEquals, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(cond.HookDamageTypeEquals) &&
            !string.Equals(pipeEvent.DamageType, cond.HookDamageTypeEquals, StringComparison.OrdinalIgnoreCase))
            return false;

        if (cond.Left != null && cond.Right != null)
        {
            double l = EvaluateValue(cond.Left, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime);
            double r = EvaluateValue(cond.Right, def, attachment, selfCard, ownerPlayer, opponentPlayer, pipeEvent, runtime);
            bool cmp = (cond.Compare ?? "Eq").ToLowerInvariant() switch
            {
                "neq" => Math.Abs(l - r) > 1e-6,
                "gt" => l > r + 1e-6,
                "gte" => l >= r - 1e-6,
                "lt" => l < r - 1e-6,
                "lte" => l <= r + 1e-6,
                _ => Math.Abs(l - r) <= 1e-6
            };
            if (!cmp) return false;
        }

        return true;
    }

    private static double EvaluateValue(
        MechanicValueExpr? expr,
        MechanicDefinition def,
        CardMechanicAttachment attachment,
        CardInstance selfCard,
        PlayerState ownerPlayer,
        PlayerState opponentPlayer,
        CombatPipelineEvent pipeEvent,
        CombatMechanicRuntime runtime)
    {
        if (expr == null) return 0;
        double raw = 0;

        if (expr.Const.HasValue)
        {
            raw = expr.Const.Value;
        }
        else if (!string.IsNullOrWhiteSpace(expr.Param))
        {
            raw = ResolveParamNumber(expr.Param, def, attachment) ?? 0;
        }
        else if (!string.IsNullOrWhiteSpace(expr.Counter))
        {
            if (string.Equals(expr.Source, "Combat", StringComparison.OrdinalIgnoreCase))
                raw = runtime.GetCombatCounter(expr.Counter);
            else
            {
                var c = ResolveSingleCardRef(expr.Source, selfCard, pipeEvent);
                raw = c != null ? runtime.GetCardCounter(c, expr.Counter) : 0;
            }
        }
        else if (!string.IsNullOrWhiteSpace(expr.CardAttr))
        {
            var c = ResolveSingleCardRef(expr.Source, selfCard, pipeEvent);
            raw = c != null ? c.GetAttribute(expr.CardAttr) : 0;
        }
        else if (!string.IsNullOrWhiteSpace(expr.PlayerAttr))
        {
            var p = string.Equals(expr.PlayerSide, "Opponent", StringComparison.OrdinalIgnoreCase)
                ? opponentPlayer
                : ownerPlayer;
            raw = expr.PlayerAttr.ToLowerInvariant() switch
            {
                "health" => p.CurrentHealth,
                "healthmax" => p.HealthMax,
                "shield" => p.Shield,
                "burn" => p.Burn,
                "poison" => p.Poison,
                "regen" or "healthregen" => p.GetAttribute("HealthRegen"),
                _ => p.GetAttribute(expr.PlayerAttr)
            };
        }
        else if (!string.IsNullOrWhiteSpace(expr.HookField))
        {
            raw = expr.HookField.ToLowerInvariant() switch
            {
                "amount" => pipeEvent.Amount,
                "payloadvalue" => pipeEvent.PayloadValue,
                "timems" => pipeEvent.TimeMs,
                _ => 0
            };
        }

        if (expr.Scale.HasValue) raw *= expr.Scale.Value;
        if (expr.Offset.HasValue) raw += expr.Offset.Value;
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0;
        return Math.Clamp(raw, -1_000_000, 1_000_000);
    }

    private static JsonElement? ResolveRawParam(string key, MechanicDefinition def, CardMechanicAttachment attachment)
    {
        if (attachment.Params != null && attachment.Params.TryGetValue(key, out var bound))
            return bound;
        for (int i = 0; i < def.Parameters.Count; i++)
        {
            if (string.Equals(def.Parameters[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return def.Parameters[i].DefaultValue;
        }
        return null;
    }

    private static double? ResolveParamNumber(string key, MechanicDefinition def, CardMechanicAttachment attachment)
    {
        var raw = ResolveRawParam(key, def, attachment);
        if (!raw.HasValue) return null;
        var el = raw.Value;
        double? val = el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String when double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null
        };
        if (!val.HasValue || double.IsNaN(val.Value) || double.IsInfinity(val.Value)) return null;

        // 按参数定义强钳制 min/max，杜绝恶意输入爆表数值
        for (int i = 0; i < def.Parameters.Count; i++)
        {
            var p = def.Parameters[i];
            if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                double clamped = val.Value;
                if (p.Min.HasValue) clamped = Math.Max(p.Min.Value, clamped);
                if (p.Max.HasValue) clamped = Math.Min(p.Max.Value, clamped);
                return clamped;
            }
        }
        return Math.Clamp(val.Value, -1_000_000, 1_000_000);
    }

    private static string? ResolveParamString(string key, MechanicDefinition def, CardMechanicAttachment attachment)
    {
        var raw = ResolveRawParam(key, def, attachment);
        if (!raw.HasValue) return null;
        var el = raw.Value;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static bool ResolveParamBool(string key, MechanicDefinition def, CardMechanicAttachment attachment)
    {
        var raw = ResolveRawParam(key, def, attachment);
        if (!raw.HasValue) return false;
        var el = raw.Value;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => Math.Abs(el.GetDouble()) > 1e-9,
            JsonValueKind.String => bool.TryParse(el.GetString(), out bool b) && b,
            _ => false
        };
    }

    /// <summary>
    /// 静态安全审计（由 ModValidator Gate #11 调用）：在加载与上线前彻底拦截非法 Hook、非法 Op、超深嵌套、缺失参数与无界连击。
    /// </summary>
    public static void ValidateStatic(MechanicDefinition def, string modId, List<ModValidationIssue> issues)
    {
        string targetId = string.IsNullOrWhiteSpace(def.Id) ? "<empty-mechanic>" : def.Id;
        if (string.IsNullOrWhiteSpace(def.Id) || def.Id.Length > 96)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error, modId, "MECHANIC_INVALID_ID",
                "自定义机制缺少合法 id（长度需在 1~96 字符内）。", targetId, GateId: 11,
                FixHintZh: "为机制指定唯一 slug，例如 'my-mod:custom-mechanic'。"));
        }

        if (string.IsNullOrWhiteSpace(def.NameZh))
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Warning, modId, "MECHANIC_MISSING_NAME",
                $"机制 '{targetId}' 缺少中文展示名 nameZh。", targetId, GateId: 11));
        }

        var paramKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in def.Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Key))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error, modId, "MECHANIC_PARAM_EMPTY_KEY",
                    $"机制 '{targetId}' 存在空 key 的参数定义。", targetId, GateId: 11));
                continue;
            }
            if (!paramKeys.Add(p.Key))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error, modId, "MECHANIC_PARAM_DUP_KEY",
                    $"机制 '{targetId}' 存在重复的参数键 '{p.Key}'。", targetId, GateId: 11));
            }
            if (!MechanicDefinition.AllowedParamTypes.Contains(p.Type))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error, modId, "MECHANIC_PARAM_INVALID_TYPE",
                    $"机制 '{targetId}' 参数 '{p.Key}' 使用了非法类型 '{p.Type}'。", targetId, GateId: 11,
                    FixHintZh: "允许的参数类型：int / float / string / bool / tag / attribute / targetMode / vfxKey。"));
            }
            if (p.Min.HasValue && p.Max.HasValue && p.Min.Value > p.Max.Value)
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error, modId, "MECHANIC_PARAM_INVALID_RANGE",
                    $"机制 '{targetId}' 参数 '{p.Key}' 的 min({p.Min}) 大于 max({p.Max})。", targetId, GateId: 11));
            }
        }

        if (def.Hooks.Count == 0)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error, modId, "MECHANIC_NO_HOOKS",
                $"机制 '{targetId}' 未绑定任何管线钩子 hooks。", targetId, GateId: 11,
                FixHintZh: "至少添加一个管线钩子（如 OnFightStart / BeforeDamage / OnCardFired / BeforeCardStateChange）。"));
            return;
        }

        for (int h = 0; h < def.Hooks.Count; h++)
        {
            var hook = def.Hooks[h];
            if (string.IsNullOrWhiteSpace(hook.Hook) || !MechanicDefinition.AllowedHooks.Contains(hook.Hook))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error, modId, "MECHANIC_UNKNOWN_HOOK",
                    $"机制 '{targetId}' 钩子 #{h} 使用了未授权的管线名称 '{hook.Hook}'。", targetId, GateId: 11,
                    FixHintZh: $"允许的管线钩子：{string.Join(", ", MechanicDefinition.AllowedHooks)}。"));
            }
            ValidateStepsStatic(hook.Steps, targetId, modId, paramKeys, depth: 0, issues);
        }
    }

    private static void ValidateStepsStatic(
        IReadOnlyList<MechanicStep>? steps,
        string mechanicId,
        string modId,
        HashSet<string> paramKeys,
        int depth,
        List<ModValidationIssue> issues)
    {
        if (steps == null) return;
        if (depth > MaxAstDepth)
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error, modId, "MECHANIC_NESTING_TOO_DEEP",
                $"机制 '{mechanicId}' 步骤嵌套深度超过上限 {MaxAstDepth}。", mechanicId, GateId: 11));
            return;
        }

        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            if (s == null || string.IsNullOrWhiteSpace(s.Op) || !MechanicDefinition.AllowedOps.Contains(s.Op))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error, modId, "MECHANIC_UNKNOWN_OP",
                    $"机制 '{mechanicId}' 使用了未授权的操作指令 '{s?.Op}'。", mechanicId, GateId: 11,
                    FixHintZh: $"允许的安全操作指令：{string.Join(", ", MechanicDefinition.AllowedOps)}。"));
                continue;
            }

            if (s.Op.Equals("Repeat", StringComparison.OrdinalIgnoreCase) && s.Count?.Const > CombatMechanicRuntime.MaxRepeatCount)
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error, modId, "MECHANIC_REPEAT_TOO_HIGH",
                    $"机制 '{mechanicId}' 的 Repeat 次数 {s.Count.Const} 超过硬上限 {CombatMechanicRuntime.MaxRepeatCount}。", mechanicId, GateId: 11));
            }
            if (s.Op.Equals("ApplyStatus", StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(s.Status) || !MechanicDefinition.AllowedStatuses.Contains(s.Status)))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error, modId, "MECHANIC_INVALID_STATUS",
                    $"机制 '{mechanicId}' 的 ApplyStatus 使用了未知状态 '{s.Status}'。", mechanicId, GateId: 11));
            }
            if (!string.IsNullOrWhiteSpace(s.SyncAttribute) && !MechanicDefinition.AllowedSyncAttributes.Contains(s.SyncAttribute))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error, modId, "MECHANIC_INVALID_SYNC_ATTR",
                    $"机制 '{mechanicId}' 的 syncAttribute='{s.SyncAttribute}' 仅允许投影至 Custom_0..Custom_3。", mechanicId, GateId: 11));
            }
            if (s.TargetSpec != null && !string.IsNullOrWhiteSpace(s.TargetSpec.Mode)
                && !MechanicDefinition.AllowedTargetModes.Contains(s.TargetSpec.Mode))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error, modId, "MECHANIC_INVALID_TARGET_MODE",
                    $"机制 '{mechanicId}' 使用了未知目标模式 '{s.TargetSpec.Mode}'。", mechanicId, GateId: 11));
            }

            CheckValueExprParams(s.Value, mechanicId, modId, paramKeys, issues);
            CheckValueExprParams(s.Amount, mechanicId, modId, paramKeys, issues);
            CheckValueExprParams(s.Count, mechanicId, modId, paramKeys, issues);
            CheckValueExprParams(s.IntervalMs, mechanicId, modId, paramKeys, issues);
            CheckValueExprParams(s.DelayMs, mechanicId, modId, paramKeys, issues);
            CheckValueExprParams(s.PayloadValue, mechanicId, modId, paramKeys, issues);
            CheckConditionParams(s.Condition, mechanicId, modId, paramKeys, depth + 1, issues);

            ValidateStepsStatic(s.Then, mechanicId, modId, paramKeys, depth + 1, issues);
            ValidateStepsStatic(s.Else, mechanicId, modId, paramKeys, depth + 1, issues);
            ValidateStepsStatic(s.Steps, mechanicId, modId, paramKeys, depth + 1, issues);
        }
    }

    private static void CheckValueExprParams(MechanicValueExpr? expr, string mechanicId, string modId, HashSet<string> paramKeys, List<ModValidationIssue> issues)
    {
        if (expr == null || string.IsNullOrWhiteSpace(expr.Param)) return;
        if (!paramKeys.Contains(expr.Param))
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error, modId, "MECHANIC_MISSING_PARAM_REF",
                $"机制 '{mechanicId}' 引用了未声明的参数 '{expr.Param}'。", mechanicId, GateId: 11,
                FixHintZh: $"请在 parameters 列表中声明 key='{expr.Param}' 的参数定义。"));
        }
    }

    private static void CheckConditionParams(MechanicCondition? cond, string mechanicId, string modId, HashSet<string> paramKeys, int depth, List<ModValidationIssue> issues)
    {
        if (cond == null || depth > MaxAstDepth) return;
        CheckValueExprParams(cond.Left, mechanicId, modId, paramKeys, issues);
        CheckValueExprParams(cond.Right, mechanicId, modId, paramKeys, issues);
        if (!string.IsNullOrWhiteSpace(cond.ParamBool) && !paramKeys.Contains(cond.ParamBool))
        {
            issues.Add(new ModValidationIssue(
                ModValidationSeverity.Error, modId, "MECHANIC_MISSING_PARAM_REF",
                $"机制 '{mechanicId}' 条件引用了未声明的布尔参数 '{cond.ParamBool}'。", mechanicId, GateId: 11));
        }
        if (cond.All != null) foreach (var c in cond.All) CheckConditionParams(c, mechanicId, modId, paramKeys, depth + 1, issues);
        if (cond.Any != null) foreach (var c in cond.Any) CheckConditionParams(c, mechanicId, modId, paramKeys, depth + 1, issues);
        if (cond.Not != null) CheckConditionParams(cond.Not, mechanicId, modId, paramKeys, depth + 1, issues);
    }
}
