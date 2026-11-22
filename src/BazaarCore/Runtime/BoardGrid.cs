using BazaarCore.Models;

namespace BazaarCore.Runtime;

/// <summary>
/// 10 格棋盘容器（支持 Hand 手牌区与 Stash 背包区），精确支持大/中/小物品占位、解锁槽掩码、邻接查询与挤位算法。
/// </summary>
public sealed class BoardGrid
{
    public const int Capacity = 10;

    /// <summary>已解锁槽位位掩码（bit 0..9，初始通常 4/6 格，随着升级按左右对称或中心向外解锁至 1023）。</summary>
    public int UnlockedMask { get; set; } = 1023;

    public List<CardInstance> Cards { get; } = new();

    public BoardGrid(int unlockedMask = 1023)
    {
        UnlockedMask = unlockedMask;
    }

    public bool IsSlotUnlocked(int slot) => slot >= 0 && slot < Capacity && (UnlockedMask & (1 << slot)) != 0;

    public int UnlockedCount
    {
        get
        {
            int c = 0;
            for (int i = 0; i < Capacity; i++)
                if (IsSlotUnlocked(i)) c++;
            return c;
        }
    }

    /// <summary>统计当前棋盘中尚未被卡牌占用的已解锁槽位数。</summary>
    public int CountFreeUnlockedSlots()
    {
        var occupied = new bool[Capacity];
        foreach (var c in Cards)
        {
            if (c.StartSlot < 0) continue;
            for (int s = c.StartSlot; s < c.StartSlot + c.SlotWidth && s < Capacity; s++)
            {
                if (s >= 0) occupied[s] = true;
            }
        }
        int free = 0;
        for (int s = 0; s < Capacity; s++)
        {
            if (IsSlotUnlocked(s) && !occupied[s]) free++;
        }
        return free;
    }

    /// <summary>
    /// 按官方 SocketOperations.ModifySockets / BazaarCardMovementManager.ModifySockets 规则，
    /// 将请求起始槽位钳制至当前已解锁槽位范围内，并确保覆盖的全部槽位均已解锁。
    /// </summary>
    public bool TryNormalizeSlotToUnlocked(int desiredStartSlot, int width, out int normalizedStartSlot)
    {
        normalizedStartSlot = -1;
        if (width <= 0 || width > Capacity) return false;

        int lowestUnlocked = -1;
        int highestUnlocked = -1;
        for (int s = 0; s < Capacity; s++)
        {
            if (!IsSlotUnlocked(s)) continue;
            if (lowestUnlocked < 0) lowestUnlocked = s;
            highestUnlocked = s;
        }
        if (lowestUnlocked < 0) return false;

        int slot = desiredStartSlot;
        if (slot < lowestUnlocked)
        {
            slot = lowestUnlocked;
        }
        if (slot + width - 1 > highestUnlocked)
        {
            slot = highestUnlocked - width + 1;
        }
        if (slot < 0 || slot + width > Capacity) return false;

        for (int s = slot; s < slot + width; s++)
        {
            if (!IsSlotUnlocked(s)) return false;
        }

        normalizedStartSlot = slot;
        return true;
    }

    /// <summary>校验当前网格是否存在越界、未解锁占用、重叠或重复实例引用。</summary>
    public bool ValidateInvariants(out string error)
    {
        var seenCards = new HashSet<CardInstance>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var slotOwners = new CardInstance?[Capacity];

        foreach (var c in Cards)
        {
            if (!seenCards.Add(c))
            {
                error = $"duplicate CardInstance ref {c.InstanceId}";
                return false;
            }
            if (!string.IsNullOrEmpty(c.InstanceId) && !seenIds.Add(c.InstanceId))
            {
                error = $"duplicate InstanceId {c.InstanceId}";
                return false;
            }

            int width = c.SlotWidth;
            if (width <= 0)
            {
                error = $"invalid SlotWidth={width} on {c.InstanceId}";
                return false;
            }
            if (c.StartSlot < 0 || c.StartSlot + width > Capacity)
            {
                error = $"out-of-bounds StartSlot={c.StartSlot}, width={width} on {c.InstanceId}";
                return false;
            }

            for (int s = c.StartSlot; s < c.StartSlot + width; s++)
            {
                if (!IsSlotUnlocked(s))
                {
                    error = $"locked slot {s} (mask=0x{UnlockedMask:X3}) occupied by {c.InstanceId}";
                    return false;
                }
                if (slotOwners[s] != null)
                {
                    error = $"slot {s} overlap between {slotOwners[s]!.InstanceId} and {c.InstanceId}";
                    return false;
                }
                slotOwners[s] = c;
            }
        }

        error = string.Empty;
        return true;
    }

    /// <summary>生成便于 [BoardAudit] 诊断的 10 格占用图（X=锁定，.=空闲，卡ID首字母/短ID=占用）。</summary>
    public string FormatOccupancyDiagram()
    {
        var cells = new string[Capacity];
        for (int s = 0; s < Capacity; s++)
        {
            if (!IsSlotUnlocked(s))
            {
                cells[s] = "X";
                continue;
            }
            var card = GetCardAtSlot(s);
            if (card == null)
            {
                cells[s] = ".";
            }
            else
            {
                string id = string.IsNullOrEmpty(card.InstanceId) ? "?" : card.InstanceId;
                cells[s] = id.Length <= 8 ? id : id[^8..];
            }
        }
        return "[" + string.Join("|", cells) + "]";
    }

    /// <summary>
    /// 按游戏升级节奏解锁指定数量的中置扩展槽位（例如 4→6→8→10 格：中心 3..6 起步向外对称解锁）。
    /// </summary>
    public void UnlockCountCentered(int count)
    {
        count = Math.Clamp(count, 0, Capacity);
        int[] order = [4, 5, 3, 6, 2, 7, 1, 8, 0, 9];
        int mask = 0;
        for (int i = 0; i < count; i++)
            mask |= (1 << order[i]);
        UnlockedMask |= mask;
    }

    public CardInstance? GetCardAtSlot(int slot)
    {
        foreach (var c in Cards)
        {
            if (c.StartSlot >= 0 && slot >= c.StartSlot && slot < c.StartSlot + c.SlotWidth)
                return c;
        }
        return null;
    }

    public bool CanFitAt(CardInstance card, int startSlot, CardInstance? ignoring = null)
        => CanFitWidthAt(card.SlotWidth, startSlot, card, ignoring);

    private bool CanFitWidthAt(int width, int startSlot, CardInstance? selfCard = null, CardInstance? ignoring = null)
    {
        if (width <= 0 || startSlot < 0 || startSlot + width > Capacity) return false;
        for (int s = startSlot; s < startSlot + width; s++)
        {
            if (!IsSlotUnlocked(s)) return false;
            var occ = GetCardAtSlot(s);
            if (occ != null && occ != selfCard && occ != ignoring)
                return false;
        }
        return true;
    }

    /// <summary>
    /// 只读判定当前网格（含可挪位重排）是否能放下指定槽位宽度（1..3）的物品，不修改任何卡牌槽位。
    /// </summary>
    public bool CanFit(int width)
    {
        if (width <= 0 || width > Capacity) return false;

        int usedSlots = 0;
        for (int i = 0; i < Cards.Count; i++)
            usedSlots += Cards[i].SlotWidth;
        if (usedSlots + width > UnlockedCount)
            return false;

        for (int s = 0; s <= Capacity - width; s++)
        {
            if (CanFitWidthAt(width, s))
                return true;
        }

        var sorted = Cards.OrderBy(c => c.StartSlot).ToList();
        for (int candidateSlot = 0; candidateSlot <= Capacity - width; candidateSlot++)
        {
            if (!CanFitUnlockedOnly(candidateSlot, width)) continue;
            if (TryBacktrackFit(sorted, candidateSlot, width, out _))
                return true;
        }
        return false;
    }

    public int FindFirstAvailableSlot(CardInstance card)
    {
        for (int s = 0; s <= Capacity - card.SlotWidth; s++)
        {
            if (CanFitAt(card, s))
                return s;
        }
        return -1;
    }

    /// <summary>
    /// 尝试放置到指定起始槽位；若槽位被其他物品占用，尝试将现有物品自动挤开让道（类似原版丝滑拖拽重排）。
    /// </summary>
    public bool TryPlaceOrShift(CardInstance card, int desiredStartSlot)
        => TryPlaceOrShift(card, desiredStartSlot, out _);

    /// <summary>
    /// 尝试放置到指定起始槽位，并在成功时记录所有发生 StartSlot 位移的棋盘既有卡牌（零静默移位）。
    /// </summary>
    public bool TryPlaceOrShift(CardInstance card, int desiredStartSlot, out List<CardInstance> displacedCards)
    {
        var beforeSlots = CaptureGridSlots();
        bool ok = TryPlaceOrShiftCore(card, desiredStartSlot);
        displacedCards = ok ? CollectDisplacedCards(beforeSlots) : [];
        return ok;
    }

    private bool TryPlaceOrShiftCore(CardInstance card, int desiredStartSlot)
    {
        int width = card.SlotWidth;
        if (!TryNormalizeSlotToUnlocked(desiredStartSlot, width, out desiredStartSlot)) return false;

        if (CanFitAt(card, desiredStartSlot))
        {
            if (!Cards.Contains(card)) Cards.Add(card);
            card.StartSlot = desiredStartSlot;
            SortCards();
            return true;
        }

        // 记录原位置快照以便回滚
        var snapshot = Cards.ToDictionary(c => c, c => c.StartSlot);
        bool wasInThisGrid = Cards.Contains(card);
        if (wasInThisGrid) Cards.Remove(card);

        // 检查目标区间及现有卡牌能否在不离开棋盘的前提下重排
        var others = Cards.OrderBy(c => c.StartSlot).ToList();
        var leftGroup = others.Where(c => c.StartSlot + c.SlotWidth / 2.0 <= desiredStartSlot + width / 2.0).ToList();
        var rightGroup = others.Where(c => !leftGroup.Contains(c)).ToList();

        if (TryPackRange(leftGroup, 0, desiredStartSlot - 1, alignRight: true, out var leftPos) &&
            TryPackRange(rightGroup, desiredStartSlot + width, Capacity - 1, alignRight: false, out var rightPos))
        {
            foreach (var (c, pos) in leftPos) c.StartSlot = pos;
            foreach (var (c, pos) in rightPos) c.StartSlot = pos;
            card.StartSlot = desiredStartSlot;
            Cards.Add(card);
            SortCards();
            return true;
        }

        // 若左右分割失败，尝试全量贪心重排给新卡腾出 desiredStartSlot
        if (TryPackAroundTarget(others, desiredStartSlot, width, out var packed))
        {
            foreach (var (c, pos) in packed) c.StartSlot = pos;
            card.StartSlot = desiredStartSlot;
            Cards.Add(card);
            SortCards();
            return true;
        }

        // 回滚
        if (wasInThisGrid) Cards.Add(card);
        foreach (var (c, pos) in snapshot) c.StartSlot = pos;
        SortCards();
        return false;
    }

    private sealed class OfficialSocketNode
    {
        public CardInstance? Card { get; }
        public List<int> Sockets { get; }
        public bool Locked { get; set; }

        public OfficialSocketNode(CardInstance? card, List<int> sockets, bool locked = false)
        {
            Card = card;
            Sockets = sockets;
            Locked = locked;
        }
    }

    /// <summary>
    /// V13.2：1:1 复刻客户端官方 <c>BazaarGameShared.Domain.Cards.SocketOperations.MoveCardToSocketOnSameCarpet</c>
    /// 链表保序移牌算法，确保同盘拖拽重排（含满盘相邻互换、跨卡滑动挤位）在服务端与 Unity 客户端产生逐槽完全一致的结果，
    /// 彻底杜绝满盘拖放时把无关远端卡瞬移到最左槽位造成的前后端错位。
    /// </summary>
    public bool TryMoveSameGridSocketOps(CardInstance card, IReadOnlyList<int>? desiredSockets)
        => TryMoveSameGridSocketOps(card, desiredSockets, out _);

    public bool TryMoveSameGridSocketOps(CardInstance card, IReadOnlyList<int>? desiredSockets, out List<CardInstance> displacedCards)
    {
        var beforeSlots = CaptureGridSlots();
        bool ok = TryMoveSameGridSocketOpsCore(card, desiredSockets);
        displacedCards = ok ? CollectDisplacedCards(beforeSlots) : [];
        return ok;
    }

    private bool TryMoveSameGridSocketOpsCore(CardInstance card, IReadOnlyList<int>? desiredSockets)
    {
        if (!Cards.Contains(card) || card.StartSlot < 0) return false;

        int width = card.SlotWidth;
        var sockets = BuildAndModifyOfficialSockets(desiredSockets, width);
        if (sockets == null || sockets.Count < width) return false;

        int curStart = card.StartSlot;
        if (curStart == sockets[0])
        {
            // 已位于目标起始槽位，保持幂等成功（不改动任何其他卡牌）
            return true;
        }

        int moveDirection = curStart > sockets[0] ? -1 : 1;
        var arrWithoutCard = BuildSocketArray(excludeCard: card);
        var list = BuildOfficialLinkedList(arrWithoutCard);
        AddOfficialCardNode(card, sockets, list, moveDirection);
        if (!RemoveOfficialNullNodes(list, card, moveDirection))
            return false;

        var newArr = BuildSocketsFromOfficialLinkedList(list);
        if (newArr == null)
            return false;

        CommitSocketArrayToCards(newArr);
        return true;
    }

    /// <summary>
    /// V13.2：1:1 复刻客户端官方 <c>SocketOperations.AddItemToContainer / MoveCardToSocketOnDifferentCarpet</c>
    /// 链表插入与平衡算法（含买卡落位、储藏箱拖入棋盘），在有足够空位时与客户端本地预览排布保持 100% 一致。
    /// </summary>
    public bool TryInsertIntoGridSocketOps(CardInstance card, IReadOnlyList<int>? desiredSockets)
        => TryInsertIntoGridSocketOps(card, desiredSockets, out _);

    public bool TryInsertIntoGridSocketOps(CardInstance card, IReadOnlyList<int>? desiredSockets, out List<CardInstance> displacedCards)
    {
        var beforeSlots = CaptureGridSlots();
        bool ok = TryInsertIntoGridSocketOpsCore(card, desiredSockets);
        displacedCards = ok ? CollectDisplacedCards(beforeSlots) : [];
        return ok;
    }

    private bool TryInsertIntoGridSocketOpsCore(CardInstance card, IReadOnlyList<int>? desiredSockets)
    {
        int width = card.SlotWidth;
        bool alreadyInGrid = Cards.Contains(card);
        int freeSlots = CountFreeUnlockedSlots() + (alreadyInGrid ? width : 0);
        if (freeSlots < width) return false;

        var sockets = BuildAndModifyOfficialSockets(desiredSockets, width);
        if (sockets == null || sockets.Count < width) return false;

        var arrWithoutCard = BuildSocketArray(excludeCard: alreadyInGrid ? card : null);
        var list = new LinkedList<OfficialSocketNode>();
        list.AddFirst(new OfficialSocketNode(card, sockets));
        BuildToOfficialLinkedList(list, arrWithoutCard);

        var newArr = BuildSocketsFromOfficialLinkedList(list);
        if (newArr == null) return false;

        CommitSocketArrayToCards(newArr);
        return true;
    }

    private List<int>? BuildAndModifyOfficialSockets(IReadOnlyList<int>? rawSockets, int width)
    {
        if (width <= 0 || width > Capacity) return null;

        int lowest = -1;
        int highest = -1;
        for (int s = 0; s < Capacity; s++)
        {
            if (IsSlotUnlocked(s))
            {
                if (lowest < 0) lowest = s;
                highest = s;
            }
        }
        if (lowest < 0 || highest - lowest + 1 < width) return null;

        var sockets = new List<int>(width);
        if (rawSockets is { Count: > 0 })
        {
            int take = Math.Min(width, rawSockets.Count);
            for (int i = 0; i < take; i++)
                sockets.Add(rawSockets[i]);
            sockets.Sort();
            while (sockets.Count < width)
                sockets.Add(sockets[^1] + 1);
        }
        else
        {
            for (int i = 0; i < width; i++)
                sockets.Add(lowest + i);
        }

        // 对齐官方 SocketOperations.ModifySockets：按最大越界偏移整体平移至已解锁区间
        int maxDist = 0;
        int shift = 0;
        for (int i = 0; i < sockets.Count; i++)
        {
            int s = sockets[i];
            int clamped = Math.Clamp(s, lowest, highest);
            int dist = Math.Abs(clamped - s);
            if (dist > maxDist)
            {
                maxDist = dist;
                shift = clamped - s;
            }
        }
        if (shift != 0)
        {
            for (int i = 0; i < sockets.Count; i++)
                sockets[i] += shift;
        }

        // 校验全部目标槽位合法且已解锁
        for (int i = 0; i < sockets.Count; i++)
        {
            if (sockets[i] < 0 || sockets[i] >= Capacity || !IsSlotUnlocked(sockets[i]))
                return null;
        }
        return sockets;
    }

    private CardInstance?[] BuildSocketArray(CardInstance? excludeCard = null)
    {
        var arr = new CardInstance?[Capacity];
        for (int i = 0; i < Cards.Count; i++)
        {
            var c = Cards[i];
            if (ReferenceEquals(c, excludeCard) || c.StartSlot < 0) continue;
            for (int s = c.StartSlot; s < c.StartSlot + c.SlotWidth && s < Capacity; s++)
                arr[s] = c;
        }
        return arr;
    }

    private LinkedList<OfficialSocketNode> BuildOfficialLinkedList(CardInstance?[] arr)
    {
        var list = new LinkedList<OfficialSocketNode>();
        for (int i = 0; i < Capacity; i++)
        {
            if (!IsSlotUnlocked(i))
            {
                list.AddLast(new OfficialSocketNode(null, new List<int>(1) { i }, locked: true));
                continue;
            }
            var c = arr[i];
            if (c == null || c.SlotWidth <= 1)
            {
                list.AddLast(new OfficialSocketNode(c, new List<int>(1) { i }));
            }
            else
            {
                int w = Math.Min(c.SlotWidth, Capacity - i);
                var occ = new List<int>(w);
                for (int k = 0; k < w; k++) occ.Add(i + k);
                list.AddLast(new OfficialSocketNode(c, occ));
                i += w - 1;
            }
        }
        return list;
    }

    private static void AddOfficialCardNode(CardInstance card, List<int> sockets, LinkedList<OfficialSocketNode> list, int direction)
    {
        int anchorSocket = direction < 0 ? sockets[0] : sockets[^1];
        LinkedListNode<OfficialSocketNode>? node = list.First;
        while (node != null)
        {
            if (node.Value.Sockets.Contains(anchorSocket))
                break;
            node = node.Next;
        }

        var newNode = new OfficialSocketNode(card, new List<int>(sockets));
        if (node == null)
            list.AddLast(newNode);
        else if (direction >= 0)
            list.AddAfter(node, newNode);
        else
            list.AddBefore(node, newNode);
    }

    private static bool RemoveOfficialNullNodes(LinkedList<OfficialSocketNode> list, CardInstance card, int direction)
    {
        int remaining = card.SlotWidth;
        if (direction >= 0)
        {
            remaining -= RemoveOfficialNullNodesLeft(list, card, remaining);
            if (remaining > 0)
                remaining -= RemoveOfficialNullNodesRight(list, card, remaining);
        }
        else
        {
            remaining -= RemoveOfficialNullNodesRight(list, card, remaining);
            if (remaining > 0)
                remaining -= RemoveOfficialNullNodesLeft(list, card, remaining);
        }
        return remaining <= 0;
    }

    private static int RemoveOfficialNullNodesLeft(LinkedList<OfficialSocketNode> list, CardInstance card, int amountToRemove)
    {
        if (amountToRemove <= 0) return 0;
        var node = list.First;
        while (node != null && !ReferenceEquals(node.Value.Card, card))
            node = node.Next;

        int removed = 0;
        while (node != null && amountToRemove > 0)
        {
            var prev = node.Previous;
            if (node.Value.Card == null && !node.Value.Locked)
            {
                list.Remove(node);
                amountToRemove--;
                removed++;
            }
            node = prev;
        }
        return removed;
    }

    private static int RemoveOfficialNullNodesRight(LinkedList<OfficialSocketNode> list, CardInstance card, int amountToRemove)
    {
        if (amountToRemove <= 0) return 0;
        var node = list.First;
        while (node != null && !ReferenceEquals(node.Value.Card, card))
            node = node.Next;

        int removed = 0;
        while (node != null && amountToRemove > 0)
        {
            var next = node.Next;
            if (node.Value.Card == null && !node.Value.Locked)
            {
                list.Remove(node);
                amountToRemove--;
                removed++;
            }
            node = next;
        }
        return removed;
    }

    private void BuildToOfficialLinkedList(LinkedList<OfficialSocketNode> list, CardInstance?[] arr)
    {
        var cardNode = list.First!;
        var rightAnchor = cardNode;
        int targetLeft = cardNode.Value.Sockets[0];
        int targetRight = cardNode.Value.Sockets[^1];
        int leftRemain = targetLeft;
        int rightRemain = Capacity - targetRight - 1;

        bool IsRangeUnlocked(int start, int size)
        {
            int len = Math.Max(1, size);
            for (int j = start; j < start + len && j < Capacity; j++)
            {
                if (!IsSlotUnlocked(j)) return false;
            }
            return true;
        }

        for (int i = 0; i < Capacity; i++)
        {
            var c = arr[i];
            if (!IsSlotUnlocked(i))
            {
                if (i < targetLeft)
                {
                    list.AddFirst(new OfficialSocketNode(null, new List<int>(), locked: true));
                }
                else if (i > targetRight)
                {
                    list.AddLast(new OfficialSocketNode(null, new List<int>(), locked: true));
                }
                continue;
            }

            int sz = c?.SlotWidth ?? 0;
            int stepSkip = c != null ? c.SlotWidth - 1 : 0;

            if (i <= targetLeft && sz <= leftRemain && IsRangeUnlocked(i, sz))
            {
                list.AddBefore(cardNode, new OfficialSocketNode(c, new List<int>()));
                i += stepSkip;
                leftRemain -= sz;
            }
            else if (i <= targetLeft && sz <= rightRemain)
            {
                var n = new OfficialSocketNode(c, new List<int>());
                list.AddAfter(rightAnchor, n);
                rightAnchor = list.Find(n)!;
                i += stepSkip;
                rightRemain -= sz;
            }
            else if (i >= targetRight && sz <= rightRemain)
            {
                list.AddLast(new OfficialSocketNode(c, new List<int>()));
                i += stepSkip;
                rightRemain -= sz;
            }
            else if (i >= targetRight && sz <= leftRemain)
            {
                list.AddBefore(cardNode, new OfficialSocketNode(c, new List<int>()));
                i += stepSkip;
                leftRemain -= sz;
            }
            else if (i >= targetLeft && sz <= leftRemain)
            {
                list.AddBefore(cardNode, new OfficialSocketNode(c, new List<int>()));
                i += stepSkip;
                leftRemain -= sz;
            }
            else if (i >= targetLeft && sz <= rightRemain)
            {
                var n = new OfficialSocketNode(c, new List<int>());
                list.AddAfter(rightAnchor, n);
                rightAnchor = list.Find(n)!;
                i += stepSkip;
                rightRemain -= sz;
            }
            else if (i <= targetLeft)
            {
                list.AddBefore(cardNode, new OfficialSocketNode(c, new List<int>()));
                i += stepSkip;
                leftRemain -= sz;
            }
            else if (i >= targetRight)
            {
                list.AddLast(new OfficialSocketNode(c, new List<int>()));
                i += stepSkip;
                rightRemain -= sz;
            }
        }

        if (rightRemain < 0)
            RemoveOfficialNullNodesLeft(list, cardNode.Value.Card!, Math.Abs(rightRemain));
        if (leftRemain < 0)
            RemoveOfficialNullNodesLeft(list, cardNode.Value.Card!, Math.Abs(leftRemain));

        BalanceOfficialLinkedList(list, cardNode);
    }

    private static void BalanceOfficialLinkedList(LinkedList<OfficialSocketNode> list, LinkedListNode<OfficialSocketNode> cardNode)
    {
        int totalSlots = 0;
        for (var n = list.First; n != null; n = n.Next)
            totalSlots += n.Value.Card?.SlotWidth ?? 1;

        int beforeSlots = 0;
        var cur = list.First;
        while (cur != null && cur != cardNode)
        {
            beforeSlots += cur.Value.Card?.SlotWidth ?? 1;
            cur = cur.Next;
        }
        if (cur == null) return;

        int desiredLeft = cardNode.Value.Sockets[0];
        if (beforeSlots >= desiredLeft)
            totalSlots -= RemoveOfficialNullNodesLeft(list, cardNode.Value.Card!, beforeSlots - desiredLeft);
        if (totalSlots > Capacity)
            totalSlots -= RemoveOfficialNullNodesRight(list, cardNode.Value.Card!, totalSlots - Capacity);
        if (totalSlots > Capacity)
            totalSlots -= RemoveOfficialNullNodesLeft(list, cardNode.Value.Card!, totalSlots - Capacity);
        while (totalSlots < Capacity)
        {
            list.AddAfter(cardNode, new OfficialSocketNode(null, new List<int>()));
            totalSlots++;
        }
    }

    private CardInstance?[]? BuildSocketsFromOfficialLinkedList(LinkedList<OfficialSocketNode> list)
    {
        var arr = new CardInstance?[Capacity];
        int cursor = 0;
        for (var n = list.First; n != null; n = n.Next)
        {
            int w = n.Value.Card?.SlotWidth ?? 1;
            if (cursor + w > Capacity) return null;
            for (int k = 0; k < w; k++)
            {
                int slot = cursor + k;
                if (n.Value.Card != null && !IsSlotUnlocked(slot))
                    return null;
                arr[slot] = n.Value.Card;
            }
            cursor += w;
        }
        if (cursor != Capacity) return null;
        return arr;
    }

    private void CommitSocketArrayToCards(CardInstance?[] arr)
    {
        var seen = new HashSet<CardInstance>();
        var nextCards = new List<CardInstance>(Cards.Count + 1);
        for (int s = 0; s < Capacity; s++)
        {
            var c = arr[s];
            if (c != null && seen.Add(c))
            {
                c.StartSlot = s;
                nextCards.Add(c);
            }
        }
        Cards.Clear();
        Cards.AddRange(nextCards);
        SortCards();
    }

    /// <summary>
    /// 拖拽或投放卡牌到 <paramref name="targetSlot"/>：优先直接放置，冲突时依次尝试同盘原位换位、
    /// 最小位移左右让位挤开或无损回溯重排，严格保留锁定槽位并在无解时原子回滚。
    /// </summary>
    public bool TryPlaceWithDisplaceOrSwap(CardInstance card, int targetSlot)
        => TryPlaceWithDisplaceOrSwap(card, targetSlot, out _);

    /// <summary>
    /// 拖拽或投放卡牌到 <paramref name="targetSlot"/>，并在成功时记录所有发生 StartSlot 位移的棋盘既有卡牌（零静默移位）。
    /// </summary>
    public bool TryPlaceWithDisplaceOrSwap(CardInstance card, int targetSlot, out List<CardInstance> displacedCards)
    {
        var beforeSlots = CaptureGridSlots();
        bool ok = TryPlaceWithDisplaceOrSwapCore(card, targetSlot);
        displacedCards = ok ? CollectDisplacedCards(beforeSlots) : [];
        return ok;
    }

    private bool TryPlaceWithDisplaceOrSwapCore(CardInstance card, int targetSlot)
    {
        int width = card.SlotWidth;
        if (!TryNormalizeSlotToUnlocked(targetSlot, width, out targetSlot)) return false;

        if (CanFitAt(card, targetSlot))
        {
            if (!Cards.Contains(card)) Cards.Add(card);
            card.StartSlot = targetSlot;
            SortCards();
            return true;
        }

        var snapshot = Cards.ToDictionary(c => c, c => c.StartSlot);
        bool wasInGrid = Cards.Contains(card);
        int oldSlot = card.StartSlot;
        if (wasInGrid) Cards.Remove(card);

        // 前置容量校验：若总占用槽位加新卡宽度已超过当前已解锁槽数，必然无解，避免进入回溯搜索
        int usedByOthers = 0;
        for (int i = 0; i < Cards.Count; i++)
            usedByOthers += Cards[i].SlotWidth;
        if (usedByOthers + width > UnlockedCount)
        {
            if (wasInGrid)
            {
                Cards.Add(card);
                card.StartSlot = oldSlot;
                SortCards();
            }
            return false;
        }

        var others = Cards.OrderBy(c => c.StartSlot).ToList();
        var blockers = others.Where(c => OverlapsRange(c.StartSlot, c.SlotWidth, targetSlot, width)).ToList();
        var nonBlockers = others.Where(c => !blockers.Contains(c)).ToList();

        Dictionary<CardInstance, int>? resolved = null;
        if (wasInGrid && oldSlot >= 0 && TrySwapBlockersIntoVacatedSlots(blockers, nonBlockers, targetSlot, width, oldSlot, out var swapPos))
        {
            resolved = swapPos;
        }
        else if (TryMinimalOrderPreservingShift(others, targetSlot, width, out var shiftPos))
        {
            resolved = shiftPos;
        }
        else if (TryDisplaceBlockersToNearestFreeSlots(blockers, nonBlockers, targetSlot, width, oldSlot, wasInGrid, out var nearestPos))
        {
            resolved = nearestPos;
        }
        else if (TryPackAroundTarget(others, targetSlot, width, out var packedPos))
        {
            resolved = packedPos;
        }
        else if (TryBacktrackFit(others, targetSlot, width, out var backtrackPos))
        {
            resolved = backtrackPos;
        }

        if (resolved != null)
        {
            foreach (var (c, pos) in resolved) c.StartSlot = pos;
            card.StartSlot = targetSlot;
            Cards.Add(card);
            SortCards();
            return true;
        }

        if (wasInGrid) Cards.Add(card);
        foreach (var (c, pos) in snapshot) c.StartSlot = pos;
        SortCards();
        return false;
    }

    private static bool OverlapsRange(int startA, int widthA, int startB, int widthB)
        => startA >= 0 && startA < startB + widthB && startA + widthA > startB;

    private bool[] BuildBaseOccupancyMask(int targetSlot, int width, IEnumerable<CardInstance> fixedCards)
    {
        var occupied = new bool[Capacity];
        for (int s = 0; s < Capacity; s++)
        {
            if (!IsSlotUnlocked(s)) occupied[s] = true;
        }
        for (int s = targetSlot; s < targetSlot + width; s++)
            occupied[s] = true;
        foreach (var c in fixedCards)
        {
            for (int s = c.StartSlot; s < c.StartSlot + c.SlotWidth; s++)
            {
                if (s >= 0 && s < Capacity) occupied[s] = true;
            }
        }
        return occupied;
    }

    private static bool CanOccupyRange(bool[] occupied, int start, int width)
    {
        if (start < 0 || start + width > Capacity) return false;
        for (int s = start; s < start + width; s++)
        {
            if (occupied[s]) return false;
        }
        return true;
    }

    private static void SetRangeOccupied(bool[] occupied, int start, int width, bool state)
    {
        for (int s = start; s < start + width; s++)
            occupied[s] = state;
    }

    private bool TrySwapBlockersIntoVacatedSlots(
        List<CardInstance> blockers,
        List<CardInstance> nonBlockers,
        int targetSlot,
        int width,
        int oldSlot,
        out Dictionary<CardInstance, int> positions)
    {
        positions = new Dictionary<CardInstance, int>();
        var occupied = BuildBaseOccupancyMask(targetSlot, width, nonBlockers);
        int vacatedEnd = oldSlot + width;

        // 仅尝试把阻挡卡干净放回原卡腾出的 [oldSlot, oldSlot + width) 槽段内，不动任何无关卡
        int cursor = oldSlot;
        foreach (var b in blockers)
        {
            while (cursor + b.SlotWidth <= vacatedEnd && !CanOccupyRange(occupied, cursor, b.SlotWidth))
                cursor++;
            if (cursor + b.SlotWidth > vacatedEnd)
            {
                positions.Clear();
                return false;
            }
            SetRangeOccupied(occupied, cursor, b.SlotWidth, true);
            positions[b] = cursor;
            cursor += b.SlotWidth;
        }

        foreach (var nb in nonBlockers)
            positions[nb] = nb.StartSlot;
        return true;
    }

    private bool TryDisplaceBlockersToNearestFreeSlots(
        List<CardInstance> blockers,
        List<CardInstance> nonBlockers,
        int targetSlot,
        int width,
        int oldSlot,
        bool wasInGrid,
        out Dictionary<CardInstance, int> positions)
    {
        positions = new Dictionary<CardInstance, int>();
        var occupied = BuildBaseOccupancyMask(targetSlot, width, nonBlockers);
        bool preferRightOnTie = wasInGrid && oldSlot > targetSlot;

        foreach (var b in blockers)
        {
            int bestSlot = -1;
            int bestDist = int.MaxValue;
            for (int s = 0; s <= Capacity - b.SlotWidth; s++)
            {
                if (!CanOccupyRange(occupied, s, b.SlotWidth)) continue;
                int dist = Math.Abs(s - b.StartSlot);
                bool better = dist < bestDist || (dist == bestDist && preferRightOnTie && s > bestSlot);
                if (better)
                {
                    bestDist = dist;
                    bestSlot = s;
                }
            }
            if (bestSlot < 0)
            {
                positions.Clear();
                return false;
            }
            SetRangeOccupied(occupied, bestSlot, b.SlotWidth, true);
            positions[b] = bestSlot;
        }

        foreach (var nb in nonBlockers)
            positions[nb] = nb.StartSlot;
        return true;
    }

    private bool TryMinimalOrderPreservingShift(
        List<CardInstance> others,
        int targetSlot,
        int width,
        out Dictionary<CardInstance, int> bestPositions)
    {
        bestPositions = new Dictionary<CardInstance, int>();
        int bestCost = int.MaxValue;
        int bestMovedCount = int.MaxValue;
        bool found = false;

        for (int split = 0; split <= others.Count; split++)
        {
            var left = others.GetRange(0, split);
            var right = others.GetRange(split, others.Count - split);
            if (!TryPackLeftAnchored(left, targetSlot - 1, out var leftPos)) continue;
            if (!TryPackRightAnchored(right, targetSlot + width, out var rightPos)) continue;

            int totalCost = 0;
            int movedCount = 0;
            foreach (var (c, pos) in leftPos)
            {
                int diff = Math.Abs(pos - c.StartSlot);
                totalCost += diff;
                if (diff > 0) movedCount++;
            }
            foreach (var (c, pos) in rightPos)
            {
                int diff = Math.Abs(pos - c.StartSlot);
                totalCost += diff;
                if (diff > 0) movedCount++;
            }

            if (totalCost < bestCost || (totalCost == bestCost && movedCount < bestMovedCount))
            {
                bestCost = totalCost;
                bestMovedCount = movedCount;
                bestPositions = new Dictionary<CardInstance, int>(leftPos);
                foreach (var (c, pos) in rightPos) bestPositions[c] = pos;
                found = true;
            }
        }
        return found;
    }

    private bool TryPackLeftAnchored(List<CardInstance> group, int maxSlot, out Dictionary<CardInstance, int> positions)
    {
        positions = new Dictionary<CardInstance, int>();
        int cursor = maxSlot;
        for (int i = group.Count - 1; i >= 0; i--)
        {
            var c = group[i];
            int start = c.StartSlot >= 0
                ? Math.Min(c.StartSlot, cursor - c.SlotWidth + 1)
                : cursor - c.SlotWidth + 1;
            while (start >= 0 && !CanFitUnlockedOnly(start, c.SlotWidth))
                start--;
            if (start < 0) return false;
            positions[c] = start;
            cursor = start - 1;
        }
        return true;
    }

    private bool TryPackRightAnchored(List<CardInstance> group, int minSlot, out Dictionary<CardInstance, int> positions)
    {
        positions = new Dictionary<CardInstance, int>();
        int cursor = minSlot;
        for (int i = 0; i < group.Count; i++)
        {
            var c = group[i];
            int start = c.StartSlot >= 0 ? Math.Max(c.StartSlot, cursor) : cursor;
            while (start + c.SlotWidth <= Capacity && !CanFitUnlockedOnly(start, c.SlotWidth))
                start++;
            if (start + c.SlotWidth > Capacity) return false;
            positions[c] = start;
            cursor = start + c.SlotWidth;
        }
        return true;
    }

    private bool CanFitUnlockedOnly(int start, int width)
    {
        if (start < 0 || start + width > Capacity) return false;
        for (int s = start; s < start + width; s++)
        {
            if (!IsSlotUnlocked(s)) return false;
        }
        return true;
    }

    private bool TryBacktrackFit(List<CardInstance> others, int targetSlot, int width, out Dictionary<CardInstance, int> positions)
    {
        positions = new Dictionary<CardInstance, int>();
        var occupied = BuildBaseOccupancyMask(targetSlot, width, Array.Empty<CardInstance>());
        var ordered = others.OrderByDescending(c => c.SlotWidth).ThenBy(c => c.StartSlot).ToList();
        if (ordered.Count == 0) return true;

        // 快速剪枝：总所需宽度大于空闲槽位，或最宽待放卡牌大于最大连续空段时，直接返回无解
        int needed = 0;
        for (int i = 0; i < ordered.Count; i++) needed += ordered[i].SlotWidth;
        int free = 0, run = 0, maxRun = 0, occupiedMask = 0;
        for (int s = 0; s < Capacity; s++)
        {
            if (occupied[s])
            {
                occupiedMask |= 1 << s;
                run = 0;
            }
            else
            {
                free++;
                if (++run > maxRun) maxRun = run;
            }
        }
        if (needed > free || ordered[0].SlotWidth > maxRun)
            return false;

        var failedStates = new HashSet<int>();
        return BacktrackPlace(ordered, 0, 0, occupiedMask, positions, failedStates);
    }

    private static bool BacktrackPlace(
        List<CardInstance> cards,
        int index,
        int prevSlot,
        int occupiedMask,
        Dictionary<CardInstance, int> positions,
        HashSet<int> failedStates)
    {
        if (index >= cards.Count) return true;
        var card = cards[index];
        // 等宽卡牌相互可交换，强制递增起始槽位以消除 N! 等价排列搜索爆炸
        int startFrom = (index > 0 && card.SlotWidth == cards[index - 1].SlotWidth)
            ? prevSlot + cards[index - 1].SlotWidth
            : 0;
        int stateKey = (index << 14) | (startFrom << 10) | occupiedMask;
        if (failedStates.Contains(stateKey)) return false;

        int slotMaskBase = (1 << card.SlotWidth) - 1;
        for (int s = startFrom; s <= Capacity - card.SlotWidth; s++)
        {
            int placeMask = slotMaskBase << s;
            if ((occupiedMask & placeMask) != 0) continue;
            positions[card] = s;
            if (BacktrackPlace(cards, index + 1, s, occupiedMask | placeMask, positions, failedStates))
                return true;
            positions.Remove(card);
        }

        failedStates.Add(stateKey);
        return false;
    }

    public bool TryAddAuto(CardInstance card)
        => TryAddAuto(card, out _);

    /// <summary>
    /// 自动寻找空闲或紧凑重排后的位置放置卡牌，并在成功时记录所有发生 StartSlot 位移的棋盘既有卡牌。
    /// </summary>
    public bool TryAddAuto(CardInstance card, out List<CardInstance> displacedCards)
    {
        var beforeSlots = CaptureGridSlots();
        bool ok = TryAddAutoCore(card);
        displacedCards = ok ? CollectDisplacedCards(beforeSlots) : [];
        return ok;
    }

    private bool TryAddAutoCore(CardInstance card)
    {
        if (Cards.Contains(card) && card.StartSlot >= 0 && CanFitAt(card, card.StartSlot))
            return true;

        bool wasInGrid = Cards.Remove(card);
        int oldSlot = card.StartSlot;

        // 满格/总空闲槽位不足时 O(1) 立即失败，绝不触发后续排布与回溯搜索
        int usedSlots = 0;
        for (int i = 0; i < Cards.Count; i++)
            usedSlots += Cards[i].SlotWidth;
        if (usedSlots + card.SlotWidth > UnlockedCount)
        {
            if (wasInGrid)
            {
                Cards.Add(card);
                card.StartSlot = oldSlot;
                SortCards();
            }
            return false;
        }

        var slot = FindFirstAvailableSlot(card);
        if (slot >= 0 && TryPlaceOrShiftCore(card, slot))
            return true;

        // 若因为碎片化放不下，依次尝试向左紧缩、向右紧缩或回溯紧凑重排以腾出连续槽位
        var snapshot = Cards.ToDictionary(c => c, c => c.StartSlot);
        var sorted = Cards.OrderBy(c => c.StartSlot).ToList();
        foreach (bool alignRight in new[] { false, true })
        {
            if (TryPackRange(sorted, 0, Capacity - 1, alignRight, out var packed))
            {
                foreach (var (c, pos) in packed) c.StartSlot = pos;
                slot = FindFirstAvailableSlot(card);
                if (slot >= 0)
                {
                    card.StartSlot = slot;
                    Cards.Add(card);
                    SortCards();
                    return true;
                }
            }
            foreach (var (c, pos) in snapshot) c.StartSlot = pos;
        }

        // 兜底：若存在非连续解锁掩码或大/中卡碎片空隙，在全部已解锁槽位上回溯寻找合法排布
        for (int candidateSlot = 0; candidateSlot <= Capacity - card.SlotWidth; candidateSlot++)
        {
            if (!CanFitUnlockedOnly(candidateSlot, card.SlotWidth)) continue;
            if (TryBacktrackFit(sorted, candidateSlot, card.SlotWidth, out var btPositions))
            {
                foreach (var (c, pos) in btPositions) c.StartSlot = pos;
                card.StartSlot = candidateSlot;
                Cards.Add(card);
                SortCards();
                return true;
            }
        }

        if (wasInGrid)
        {
            Cards.Add(card);
            card.StartSlot = oldSlot;
            SortCards();
        }
        foreach (var (c, pos) in snapshot) c.StartSlot = pos;
        return false;
    }

    public bool Remove(CardInstance card)
    {
        if (!Cards.Remove(card)) return false;
        card.StartSlot = -1;
        return true;
    }

    /// <summary>获取严格相邻的左侧卡牌（右边界紧贴 origin.StartSlot）。</summary>
    public CardInstance? GetLeftNeighbor(CardInstance origin, bool includeDestroyed = false)
    {
        if (origin.StartSlot <= 0) return null;
        return Cards
            .FirstOrDefault(c =>
                c != origin
                && c.StartSlot >= 0
                && (includeDestroyed || !c.IsDestroyed)
                && c.StartSlot + c.SlotWidth == origin.StartSlot);
    }

    /// <summary>获取严格相邻的右侧卡牌（左边界紧贴 origin.StartSlot + origin.SlotWidth）。</summary>
    public CardInstance? GetRightNeighbor(CardInstance origin, bool includeDestroyed = false)
    {
        if (origin.StartSlot < 0) return null;
        int rightEdge = origin.StartSlot + origin.SlotWidth;
        return Cards
            .FirstOrDefault(c =>
                c != origin
                && c.StartSlot >= 0
                && (includeDestroyed || !c.IsDestroyed)
                && c.StartSlot == rightEdge);
    }

    /// <summary>获取严格相邻的左右两侧卡牌。</summary>
    public IEnumerable<CardInstance> GetNeighbors(CardInstance origin, bool includeDestroyed = false)
    {
        var l = GetLeftNeighbor(origin, includeDestroyed);
        if (l != null) yield return l;
        var r = GetRightNeighbor(origin, includeDestroyed);
        if (r != null) yield return r;
    }

    /// <summary>获取同一网格内 origin 左侧的所有已放置卡牌。</summary>
    public IEnumerable<CardInstance> GetAllLeftCards(CardInstance origin, bool includeDestroyed = false)
    {
        if (origin.StartSlot <= 0) return Enumerable.Empty<CardInstance>();
        return Cards
            .Where(c =>
                c != origin
                && c.StartSlot >= 0
                && (includeDestroyed || !c.IsDestroyed)
                && c.StartSlot + c.SlotWidth <= origin.StartSlot)
            .OrderBy(c => c.StartSlot);
    }

    /// <summary>获取同一网格内 origin 右侧的所有已放置卡牌。</summary>
    public IEnumerable<CardInstance> GetAllRightCards(CardInstance origin, bool includeDestroyed = false)
    {
        if (origin.StartSlot < 0) return Enumerable.Empty<CardInstance>();
        int rightEdge = origin.StartSlot + origin.SlotWidth;
        return Cards
            .Where(c =>
                c != origin
                && c.StartSlot >= rightEdge
                && (includeDestroyed || !c.IsDestroyed))
            .OrderBy(c => c.StartSlot);
    }

    public CardInstance? GetLeftMostCard(bool includeDestroyed = false)
        => Cards.Where(c => c.StartSlot >= 0 && (includeDestroyed || !c.IsDestroyed))
                .OrderBy(c => c.StartSlot)
                .FirstOrDefault();

    public CardInstance? GetRightMostCard(bool includeDestroyed = false)
        => Cards.Where(c => c.StartSlot >= 0 && (includeDestroyed || !c.IsDestroyed))
                .OrderByDescending(c => c.StartSlot)
                .FirstOrDefault();

    private void SortCards() => Cards.Sort((a, b) => a.StartSlot.CompareTo(b.StartSlot));

    private Dictionary<CardInstance, int> CaptureGridSlots()
    {
        var snap = new Dictionary<CardInstance, int>(Cards.Count);
        for (int i = 0; i < Cards.Count; i++)
        {
            var c = Cards[i];
            snap[c] = c.StartSlot;
        }
        return snap;
    }

    private List<CardInstance> CollectDisplacedCards(Dictionary<CardInstance, int> beforeSlots)
    {
        var displaced = new List<CardInstance>();
        for (int i = 0; i < Cards.Count; i++)
        {
            var c = Cards[i];
            if (beforeSlots.TryGetValue(c, out int prevSlot) && c.StartSlot != prevSlot)
                displaced.Add(c);
        }
        return displaced;
    }

    private bool TryPackRange(List<CardInstance> group, int minSlot, int maxSlot, bool alignRight, out Dictionary<CardInstance, int> positions)
    {
        positions = new Dictionary<CardInstance, int>();
        if (group.Count == 0) return true;

        if (!alignRight)
        {
            int cursor = minSlot;
            foreach (var c in group)
            {
                while (cursor + c.SlotWidth - 1 <= maxSlot)
                {
                    bool ok = true;
                    for (int s = cursor; s < cursor + c.SlotWidth; s++)
                        if (!IsSlotUnlocked(s)) { ok = false; break; }
                    if (ok) break;
                    cursor++;
                }
                if (cursor + c.SlotWidth - 1 > maxSlot) return false;
                positions[c] = cursor;
                cursor += c.SlotWidth;
            }
            return true;
        }
        else
        {
            int cursor = maxSlot;
            for (int i = group.Count - 1; i >= 0; i--)
            {
                var c = group[i];
                int start = cursor - c.SlotWidth + 1;
                while (start >= minSlot)
                {
                    bool ok = true;
                    for (int s = start; s < start + c.SlotWidth; s++)
                        if (!IsSlotUnlocked(s)) { ok = false; break; }
                    if (ok) break;
                    start--;
                }
                if (start < minSlot) return false;
                positions[c] = start;
                cursor = start - 1;
            }
            return true;
        }
    }

    private bool TryPackAroundTarget(List<CardInstance> others, int targetStart, int targetWidth, out Dictionary<CardInstance, int> positions)
    {
        positions = new Dictionary<CardInstance, int>();
        int idx = 0;
        int s = 0;
        while (idx < others.Count && s < Capacity)
        {
            if (s >= targetStart && s < targetStart + targetWidth)
            {
                s = targetStart + targetWidth;
                continue;
            }
            var card = others[idx];
            bool fits = s + card.SlotWidth <= Capacity;
            if (fits)
            {
                for (int k = s; k < s + card.SlotWidth; k++)
                {
                    if (!IsSlotUnlocked(k) || (k >= targetStart && k < targetStart + targetWidth))
                    {
                        fits = false;
                        break;
                    }
                }
            }
            if (fits)
            {
                positions[card] = s;
                s += card.SlotWidth;
                idx++;
            }
            else
            {
                s++;
            }
        }
        return idx == others.Count;
    }
}
