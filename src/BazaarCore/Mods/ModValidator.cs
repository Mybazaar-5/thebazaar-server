using System.Text.RegularExpressions;
using BazaarCore.Data;
using BazaarCore.Models;

namespace BazaarCore.Mods;

/// <summary>
/// Mod 包强校验与拓扑排序引擎：
/// - 校验 Mod ID 格式与全局唯一性；
/// - 校验依赖完整性，执行 Kahn 算法拓扑排序并精准检测循环依赖；
/// - 校验每张自定义卡/事件的 BaseVisualTemplateId 必须存在于白名单且同 Type、Item 同 Size（防客户端白块与 3D 穿模）；
/// - 校验卡牌/事件/模式 ID 全局唯一、字段合法性及事件跳转图无循环跳转（DFS 环检测）；
/// - 校验客户端贴图资产路径无穿越、仅限 PNG/WebP、魔数合法、尺寸 &lt;= 1024x1024 且单文件 &lt;= 2MB。
/// </summary>
public static partial class ModValidator
{
    public const long MaxAssetBytes = 2L * 1024 * 1024; // 2 MB
    public const int MaxAssetWidth = 1024;
    public const int MaxAssetHeight = 1024;

    private static readonly HashSet<string> ValidSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Small", "Medium", "Large"
    };

    private static readonly HashSet<string> ValidTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bronze", "Silver", "Gold", "Diamond", "Legendary"
    };

    private static readonly HashSet<string> ValidScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        "singleplayer-only", "multiplayer-safe", "cosmetic-only"
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$|^[a-z0-9]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModIdRegex();

    /// <summary>
    /// 校验并按依赖拓扑顺序排序全部候选 ModPackage，产出通过校验的有序包列表与详细校验问题清单。
    /// </summary>
    public static (List<ModPackage> ValidOrderedPackages, List<ModValidationIssue> Issues, List<string> RejectedModIds) ValidateAndSort(
        IReadOnlyList<ModPackage> packages,
        GameDataRepository baseRepo,
        IReadOnlySet<string>? validVisualWhitelist = null)
    {
        var issues = new List<ModValidationIssue>();
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 清单基础校验与 ModId 唯一性检查
        var pkgById = new Dictionary<string, ModPackage>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < packages.Count; i++)
        {
            var pkg = packages[i];
            var m = pkg.Manifest;
            string modId = (m.Id ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(modId) || !ModIdRegex().IsMatch(modId))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    string.IsNullOrWhiteSpace(modId) ? $"<unknown-{i}>" : modId,
                    "INVALID_MOD_ID",
                    $"Mod ID '{m.Id}' 格式非法，必须为 2~64 位小写字母、数字或连字符（禁止路径穿越字符）。"));
                rejected.Add(string.IsNullOrWhiteSpace(modId) ? $"<unknown-{i}>" : modId);
                continue;
            }

            if (pkgById.ContainsKey(modId))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    modId,
                    "DUPLICATE_MOD_ID",
                    $"检测到重复的 Mod ID '{modId}'，已拒绝重复包。"));
                rejected.Add(modId);
                continue;
            }

            if (string.IsNullOrWhiteSpace(m.Name))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Warning,
                    modId,
                    "MISSING_MOD_NAME",
                    $"Mod '{modId}' 缺少 name 展示名称，将回退使用 Mod ID。"));
                m.Name = modId;
            }

            if (!ValidScopes.Contains(m.Scope ?? string.Empty))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    modId,
                    "INVALID_MOD_SCOPE",
                    $"Mod '{modId}' 的 scope '{m.Scope}' 非法，仅允许 singleplayer-only | multiplayer-safe | cosmetic-only。"));
                rejected.Add(modId);
                continue;
            }

            pkgById[modId] = pkg;
        }

        // 2. 依赖完整性检查与 Kahn 拓扑排序（含循环依赖精准检测）
        var topoOrdered = TopologicalSort(pkgById, issues, rejected);

        // 3. 逐包内容深度校验（全局 ID 唯一性、BaseVisualTemplateId 同 Type/同 Size、事件跳转防环、资产安全）
        var globalCustomCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalCustomModeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var globalCustomMonsterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validPackages = new List<ModPackage>(topoOrdered.Count);

        foreach (var pkg in topoOrdered)
        {
            string modId = pkg.Manifest.Id;
            bool pkgHasFatal = false;
            var localCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localModeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var localMonsterIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 3a. 校验资产安全（大小、尺寸、魔数头）
            foreach (var asset in pkg.AssetsByLogicalPath.Values)
            {
                if (!IsSafeRelativePath(asset.LogicalPath))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_PATH_TRAVERSAL", $"资产路径 '{asset.LogicalPath}' 包含非法目录穿越。", asset.LogicalPath));
                    pkgHasFatal = true;
                }
                if (asset.Bytes <= 0 || asset.Bytes > MaxAssetBytes)
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_SIZE_EXCEEDED", $"资产 '{asset.LogicalPath}' 体积 {asset.Bytes} 字节超出上限 {MaxAssetBytes} 字节。", asset.LogicalPath));
                    pkgHasFatal = true;
                }
                if (asset.Width <= 0 || asset.Height <= 0 || asset.Width > MaxAssetWidth || asset.Height > MaxAssetHeight)
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "ASSET_DIMENSION_INVALID", $"资产 '{asset.LogicalPath}' 分辨率 {asset.Width}x{asset.Height} 超出上限 {MaxAssetWidth}x{MaxAssetHeight}。", asset.LogicalPath));
                    pkgHasFatal = true;
                }
            }

            // 3b. 聚合所有卡牌与事件卡片进行校验
            var allCardDefs = new List<CustomCardDefinition>(pkg.Cards.Count + pkg.Events.Count * 2);
            allCardDefs.AddRange(pkg.Cards);
            foreach (var ev in pkg.Events)
            {
                allCardDefs.Add(ev.ToCardDefinition());
                foreach (var step in ev.Steps)
                    allCardDefs.Add(step);
                if (ev.CombatMonster != null)
                {
                    if (string.IsNullOrWhiteSpace(ev.CombatMonster.Id))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_MONSTER_ID", $"事件 '{ev.EventId}' 内联野怪缺少 Id。", ev.EventId));
                        pkgHasFatal = true;
                    }
                    else if (baseRepo.MonstersById.ContainsKey(ev.CombatMonster.Id)
                             || globalCustomMonsterIds.Contains(ev.CombatMonster.Id)
                             || !localMonsterIds.Add(ev.CombatMonster.Id))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "DUPLICATE_MONSTER_ID", $"自定义野怪 ID '{ev.CombatMonster.Id}' 与已有野怪冲突。", ev.CombatMonster.Id));
                        pkgHasFatal = true;
                    }
                }
            }

            // 3c. 逐卡校验
            var transitionGraph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var card in allCardDefs)
            {
                if (!ValidateSingleCard(card, modId, pkg, baseRepo, validVisualWhitelist, globalCustomCardIds, localCardIds, issues))
                    pkgHasFatal = true;

                if (card.NextEncounterOnSelection is { Count: > 0 } nexts)
                {
                    var targets = new List<string>(nexts.Count);
                    foreach (var n in nexts)
                    {
                        if (!string.IsNullOrWhiteSpace(n.Id))
                            targets.Add(n.Id.Trim());
                    }
                    if (targets.Count > 0)
                        transitionGraph[card.CustomId] = targets;
                }
            }

            // 3d. 事件跳转循环防护（DFS 环检测）
            if (TryFindEncounterCycle(transitionGraph, out var cyclePath))
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    modId,
                    "ENCOUNTER_CYCLE_DETECTED",
                    $"检测到非法事件循环跳转：{string.Join(" -> ", cyclePath)}，禁止加载以防玩家死循环软锁。"));
                pkgHasFatal = true;
            }

            // 3e. 校验自定义野怪列表
            foreach (var m in pkg.Monsters)
            {
                if (string.IsNullOrWhiteSpace(m.Id))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_MONSTER_ID", "自定义野怪缺少 Id。"));
                    pkgHasFatal = true;
                }
                else if (baseRepo.MonstersById.ContainsKey(m.Id)
                         || globalCustomMonsterIds.Contains(m.Id)
                         || !localMonsterIds.Add(m.Id))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "DUPLICATE_MONSTER_ID", $"自定义野怪 ID '{m.Id}' 冲突。", m.Id));
                    pkgHasFatal = true;
                }
            }

            // 3f. 校验自定义模式
            foreach (var mode in pkg.Modes)
            {
                if (string.IsNullOrWhiteSpace(mode.ModeId))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_MODE_ID", "自定义模式缺少 modeId。"));
                    pkgHasFatal = true;
                    continue;
                }
                if (globalCustomModeIds.Contains(mode.ModeId) || !localModeIds.Add(mode.ModeId))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "DUPLICATE_MODE_ID", $"自定义模式 ID '{mode.ModeId}' 重复。", mode.ModeId));
                    pkgHasFatal = true;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(mode.NameZh))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Warning, modId, "MISSING_MODE_NAME", $"自定义模式 '{mode.ModeId}' 缺少 nameZh，将回退使用 modeId。", mode.ModeId));
                    mode.NameZh = mode.ModeId;
                }
            }

            if (pkgHasFatal)
            {
                rejected.Add(modId);
                continue;
            }

            // 本包全部通过校验，提交到全局占用表
            foreach (var cid in localCardIds) globalCustomCardIds.Add(cid);
            foreach (var mid in localModeIds) globalCustomModeIds.Add(mid);
            foreach (var monId in localMonsterIds) globalCustomMonsterIds.Add(monId);
            validPackages.Add(pkg);
        }

        return (validPackages, issues, rejected.ToList());
    }

    private static bool ValidateSingleCard(
        CustomCardDefinition card,
        string modId,
        ModPackage pkg,
        GameDataRepository baseRepo,
        IReadOnlySet<string>? validVisualWhitelist,
        HashSet<string> globalCustomCardIds,
        HashSet<string> localCardIds,
        List<ModValidationIssue> issues)
    {
        bool ok = true;
        if (string.IsNullOrWhiteSpace(card.CustomId))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "MISSING_CUSTOM_CARD_ID", "自定义卡牌缺少 customId 字段。"));
            return false;
        }

        string cid = card.CustomId.Trim();
        card.CustomId = cid;

        if (baseRepo.CardsById.ContainsKey(cid) || globalCustomCardIds.Contains(cid) || !localCardIds.Add(cid))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "DUPLICATE_CARD_ID", $"自定义卡牌 ID '{cid}' 与官方原版卡或其他 Mod 冲突。", cid));
            ok = false;
        }

        if (string.IsNullOrWhiteSpace(card.Type) || !CustomCardDefinition.SupportedCardTypes.Contains(card.Type))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_CARD_TYPE", $"自定义卡 '{cid}' 的 type '{card.Type}' 不受支持。", cid));
            ok = false;
        }

        if (string.IsNullOrWhiteSpace(card.Size) || !ValidSizes.Contains(card.Size))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_CARD_SIZE", $"自定义卡 '{cid}' 的 size '{card.Size}' 非法（必须为 Small/Medium/Large）。", cid));
            ok = false;
        }

        if (string.IsNullOrWhiteSpace(card.StartingTier) || !ValidTiers.Contains(card.StartingTier))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_STARTING_TIER", $"自定义卡 '{cid}' 的 startingTier '{card.StartingTier}' 非法。", cid));
            ok = false;
        }

        // 强校验 BaseVisualTemplateId（防客户端白块 / InvalidKeyException / 3D 卡框尺寸穿模）
        if (string.IsNullOrWhiteSpace(card.BaseVisualTemplateId))
        {
            issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "MISSING_BASE_VISUAL_ID", $"自定义卡 '{cid}' 必须声明合法的 baseVisualTemplateId 以防客户端白块。", cid));
            ok = false;
        }
        else
        {
            string baseId = card.BaseVisualTemplateId.Trim();
            card.BaseVisualTemplateId = baseId;

            if (validVisualWhitelist != null && !validVisualWhitelist.Contains(baseId))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "BASE_VISUAL_NOT_IN_WHITELIST", $"自定义卡 '{cid}' 的 baseVisualTemplateId '{baseId}' 不在客户端合法资产白名单 valid_card_ids 中。", cid));
                ok = false;
            }

            var baseTpl = baseRepo.GetCard(baseId);
            if (baseTpl == null)
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "BASE_VISUAL_NOT_FOUND", $"自定义卡 '{cid}' 的 baseVisualTemplateId '{baseId}' 在基础数据库 GameData.db 中不存在。", cid));
                ok = false;
            }
            else
            {
                if (!string.Equals(baseTpl.Type, card.Type, StringComparison.Ordinal))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "BASE_VISUAL_TYPE_MISMATCH", $"自定义卡 '{cid}' (Type={card.Type}) 与载体卡 '{baseId}' (Type={baseTpl.Type}) 类型不一致。", cid));
                    ok = false;
                }
                if (string.Equals(card.Type, "Item", StringComparison.Ordinal)
                    && !string.Equals(baseTpl.Size, card.Size, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "BASE_VISUAL_SIZE_MISMATCH", $"自定义物品 '{cid}' (Size={card.Size}) 与载体物品 '{baseId}' (Size={baseTpl.Size}) 尺寸不一致，将导致 3D 卡框穿模。", cid));
                    ok = false;
                }
            }
        }

        // 校验引用的贴图文件是否存在且安全
        if (!string.IsNullOrWhiteSpace(card.CustomArtFile))
        {
            string artPath = NormalizeLogicalPath(card.CustomArtFile);
            if (!IsSafeRelativePath(artPath))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "CARD_ART_PATH_TRAVERSAL", $"自定义卡 '{cid}' 贴图路径 '{card.CustomArtFile}' 非法。", cid));
                ok = false;
            }
            else if (!pkg.AssetsByLogicalPath.ContainsKey(artPath))
            {
                issues.Add(new ModValidationIssue(ModValidationSeverity.Warning, modId, "CARD_ART_FILE_MISSING", $"自定义卡 '{cid}' 引用的贴图 '{artPath}' 未在 assets 目录中找到，客户端将回退使用载体原图。", cid));
            }
        }

        // 校验数值字段防 NaN / Infinity
        if (card.Attributes != null)
        {
            foreach (var (ak, av) in card.Attributes)
            {
                if (!double.IsFinite(av))
                {
                    issues.Add(new ModValidationIssue(ModValidationSeverity.Error, modId, "INVALID_ATTR_VALUE", $"自定义卡 '{cid}' 属性 '{ak}' 包含非有限数值。", cid));
                    ok = false;
                }
            }
        }

        return ok;
    }

    private static List<ModPackage> TopologicalSort(
        Dictionary<string, ModPackage> pkgById,
        List<ModValidationIssue> issues,
        HashSet<string> rejected)
    {
        // 先剔除依赖缺失或依赖已拒绝包的 Mod（迭代收敛）
        var active = new Dictionary<string, ModPackage>(pkgById, StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            var toRemove = new List<string>();
            foreach (var (id, pkg) in active)
            {
                if (pkg.Manifest.Dependencies == null) continue;
                foreach (var depRaw in pkg.Manifest.Dependencies)
                {
                    if (string.IsNullOrWhiteSpace(depRaw)) continue;
                    string dep = depRaw.Trim();
                    if (string.Equals(dep, id, StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, id, "SELF_DEPENDENCY", $"Mod '{id}' 声明了自身循环依赖。"));
                        toRemove.Add(id);
                        break;
                    }
                    if (!active.ContainsKey(dep))
                    {
                        issues.Add(new ModValidationIssue(ModValidationSeverity.Error, id, "MISSING_DEPENDENCY", $"Mod '{id}' 依赖的 Mod '{dep}' 缺失或校验失败，已跳过加载。"));
                        toRemove.Add(id);
                        break;
                    }
                }
            }
            foreach (var r in toRemove)
            {
                active.Remove(r);
                rejected.Add(r);
                changed = true;
            }
        } while (changed);

        // Kahn 拓扑排序（按 ModId 字典序确保跨平台 100% 确定性加载顺序）
        var inDegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in active.Keys)
        {
            inDegree[id] = 0;
            dependents[id] = new List<string>();
        }

        foreach (var (id, pkg) in active)
        {
            if (pkg.Manifest.Dependencies == null) continue;
            var distinctDeps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var depRaw in pkg.Manifest.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(depRaw)) continue;
                string dep = depRaw.Trim();
                if (!distinctDeps.Add(dep) || !active.ContainsKey(dep)) continue;
                inDegree[id]++;
                dependents[dep].Add(id);
            }
        }

        int CompareReady(string a, string b)
        {
            int cmp = active[a].Manifest.LoadOrder.CompareTo(active[b].Manifest.LoadOrder);
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a, b);
        }

        var ready = inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
        ready.Sort(CompareReady);
        var ordered = new List<ModPackage>(active.Count);

        while (ready.Count > 0)
        {
            string current = ready[0];
            ready.RemoveAt(0);
            ordered.Add(active[current]);

            foreach (var next in dependents[current])
            {
                inDegree[next]--;
                if (inDegree[next] == 0)
                {
                    ready.Add(next);
                    ready.Sort(CompareReady);
                }
            }
        }

        // 任何剩余入度 > 0 的节点均属于循环依赖环
        if (ordered.Count < active.Count)
        {
            var cyclicIds = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            string cycleSummary = string.Join(", ", cyclicIds);
            foreach (var cid in cyclicIds)
            {
                issues.Add(new ModValidationIssue(
                    ModValidationSeverity.Error,
                    cid,
                    "CIRCULAR_DEPENDENCY",
                    $"检测到 Mod 循环依赖环（涉及：{cycleSummary}），环内全部 Mod 均被拒绝加载。"));
                rejected.Add(cid);
            }
        }

        return ordered;
    }

    private static bool TryFindEncounterCycle(
        Dictionary<string, List<string>> graph,
        out List<string> cyclePath)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=未访问, 1=递归栈中, 2=已完成
        var stack = new List<string>();

        bool Dfs(string node, out List<string> foundCycle)
        {
            state[node] = 1;
            stack.Add(node);
            if (graph.TryGetValue(node, out var neighbors))
            {
                for (int i = 0; i < neighbors.Count; i++)
                {
                    var next = neighbors[i];
                    if (!graph.ContainsKey(next))
                    {
                        if (string.Equals(next, node, StringComparison.OrdinalIgnoreCase))
                        {
                            foundCycle = [node, next];
                            return true;
                        }
                        continue;
                    }
                    int st = state.GetValueOrDefault(next, 0);
                    if (st == 1)
                    {
                        int idx = stack.FindIndex(x => string.Equals(x, next, StringComparison.OrdinalIgnoreCase));
                        foundCycle = stack.Skip(Math.Max(0, idx)).Append(next).ToList();
                        return true;
                    }
                    if (st == 0 && Dfs(next, out foundCycle))
                        return true;
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
            foundCycle = [];
            return false;
        }

        foreach (var node in graph.Keys)
        {
            if (state.GetValueOrDefault(node, 0) != 0) continue;
            if (Dfs(node, out cyclePath))
                return true;
        }

        cyclePath = [];
        return false;
    }

    /// <summary>校验相对路径不含盘符、绝对根路径或 `..` 目录穿越段。</summary>
    public static bool IsSafeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        string normalized = NormalizeLogicalPath(relativePath);
        if (Path.IsPathRooted(normalized) || normalized.StartsWith('/') || normalized.Contains(':'))
            return false;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i] == ".." || segments[i] == ".")
                return false;
        }
        return segments.Length > 0;
    }

    public static string NormalizeLogicalPath(string path)
        => path.Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>
    /// 校验图片字节流的 PNG / WebP 魔数头并无依赖解析宽高像素（防畸形文件与非图像伪装上传）。
    /// </summary>
    public static bool TryValidateImageHeader(ReadOnlySpan<byte> bytes, out int width, out int height, out string contentType)
    {
        width = 0;
        height = 0;
        contentType = string.Empty;

        // PNG: 8 字节签名 \x89PNG\r\n\x1A\n + IHDR 块在偏移 16..23 存大端宽高
        if (bytes.Length >= 24
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A
            && bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R')
        {
            width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            contentType = "image/png";
            return width > 0 && height > 0;
        }

        // WebP: RIFF....WEBP + VP8X / VP8L / VP8 块
        if (bytes.Length >= 30
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            contentType = "image/webp";
            if (bytes[12] == (byte)'V' && bytes[13] == (byte)'P' && bytes[14] == (byte)'8' && bytes[15] == (byte)'X')
            {
                width = 1 + (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16));
                height = 1 + (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16));
                return width > 0 && height > 0;
            }
            if (bytes[12] == (byte)'V' && bytes[13] == (byte)'P' && bytes[14] == (byte)'8' && bytes[15] == (byte)'L'
                && bytes[20] == 0x2F)
            {
                uint bits = (uint)(bytes[21] | (bytes[22] << 8) | (bytes[23] << 16) | (bytes[24] << 24));
                width = (int)(bits & 0x3FFF) + 1;
                height = (int)((bits >> 14) & 0x3FFF) + 1;
                return width > 0 && height > 0;
            }
            if (bytes[12] == (byte)'V' && bytes[13] == (byte)'P' && bytes[14] == (byte)'8' && bytes[15] == (byte)' '
                && bytes[23] == 0x9D && bytes[24] == 0x01 && bytes[25] == 0x2A)
            {
                width = (bytes[26] | (bytes[27] << 8)) & 0x3FFF;
                height = (bytes[28] | (bytes[29] << 8)) & 0x3FFF;
                return width > 0 && height > 0;
            }
        }

        return false;
    }
}
