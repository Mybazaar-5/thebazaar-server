using System.Text.Json;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Engine;

/// <summary>
/// 棋盘 SocketEffect（灶台/冰格/音符）拓扑放置与音符合法性判定辅助器，
/// 复刻官方 SocketedContainer 槽位占位、孤立防相邻及音符模 7 音阶匹配规则。
/// </summary>
public static class SocketPlacementHelper
{
    public const int SocketCount = BoardGrid.Capacity;
    public const int MusicNoteCount = 7;

    public static bool IsSocketEffectTemplate(CardTemplate? tpl)
    {
        if (tpl == null) return false;
        return tpl.Type is "SocketEffect" or "MusicNoteSocketEffect"
            || string.Equals(tpl.TypeDiscriminator, "TCardSocketEffect", StringComparison.Ordinal)
            || string.Equals(tpl.TypeDiscriminator, "TCardMusicNoteSocketEffect", StringComparison.Ordinal);
    }

    public static bool TryParseMusicNote(JsonElement? el, out int noteIndex)
    {
        noteIndex = 0;
        if (!el.HasValue) return false;
        switch (el.Value.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = el.Value.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s) && s.Length == 1)
                {
                    char ch = char.ToUpperInvariant(s[0]);
                    if (ch >= 'A' && ch <= 'G')
                    {
                        noteIndex = ch - 'A';
                        return true;
                    }
                }
                break;
            }
            case JsonValueKind.Number:
            {
                if (el.Value.TryGetInt32(out int num) && num >= 0 && num < MusicNoteCount)
                {
                    noteIndex = num;
                    return true;
                }
                break;
            }
        }
        return false;
    }

    public static bool IsMusicNoteCard(CardTemplate? tpl, out int noteIndex)
    {
        noteIndex = 0;
        if (tpl == null) return false;
        if (!string.Equals(tpl.TypeDiscriminator, "TCardMusicNoteSocketEffect", StringComparison.Ordinal) && !tpl.MusicNote.HasValue)
            return false;
        return TryParseMusicNote(tpl.MusicNote, out noteIndex);
    }

    public static CardInstance? GetSocketEffectAt(PlayerState player, int slot)
    {
        if (slot < 0 || slot >= SocketCount) return null;
        for (int i = 0; i < player.SocketEffects.Count; i++)
        {
            var se = player.SocketEffects[i];
            if (se.IsDestroyed || se.StartSlot < 0) continue;
            if (slot >= se.StartSlot && slot < se.StartSlot + se.SlotWidth)
                return se;
        }
        return null;
    }

    public static bool IsSocketAvailable(PlayerState player, int slot)
        => slot >= 0
        && slot < SocketCount
        && player.Hand.IsSlotUnlocked(slot)
        && GetSocketEffectAt(player, slot) == null;

    public static bool IsSocketOverwritable(PlayerState player, int slot)
    {
        if (slot < 0 || slot >= SocketCount || !player.Hand.IsSlotUnlocked(slot))
            return false;
        var existing = GetSocketEffectAt(player, slot);
        return existing != null && !IsMusicNoteCard(existing.Template, out _);
    }

    public static bool TryFindAnchorNote(PlayerState player, out int socketIndex, out int noteIndex)
    {
        for (int i = 0; i < SocketCount; i++)
        {
            var se = GetSocketEffectAt(player, i);
            if (se != null && IsMusicNoteCard(se.Template, out noteIndex))
            {
                socketIndex = i;
                return true;
            }
        }
        socketIndex = 0;
        noteIndex = 0;
        return false;
    }

    public static List<int> GatherCandidateSocketsForMusicNote(PlayerState player, int noteIndex)
    {
        int start = 0;
        int step = 1;
        if (TryFindAnchorNote(player, out int anchorSocket, out int anchorNote))
        {
            int diff = noteIndex - anchorNote;
            start = ((anchorSocket + diff) % MusicNoteCount + MusicNoteCount) % MusicNoteCount;
            step = MusicNoteCount;
        }

        var candidates = new List<int>(SocketCount);
        for (int i = start; i < SocketCount; i += step)
        {
            if (IsSocketAvailable(player, i))
                candidates.Add(i);
        }
        if (candidates.Count > 0)
            return candidates;

        for (int i = start; i < SocketCount; i += step)
        {
            if (IsSocketOverwritable(player, i))
                candidates.Add(i);
        }
        return candidates;
    }

    public static bool CanPlaceMusicNote(PlayerState player, int noteIndex)
        => GatherCandidateSocketsForMusicNote(player, noteIndex).Count > 0;

    public static bool CanPlaceAnyMusicNote(PlayerState player)
    {
        for (int i = 0; i < SocketCount; i++)
        {
            if (IsSocketAvailable(player, i) || IsSocketOverwritable(player, i))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 在棋盘插座层放置 SocketEffect；当 <paramref name="avoidAdjacent"/> 为 true 且 <paramref name="strictIsolated"/> 为 true 时，
    /// 若无可用孤立槽位则严格返回 false（P2-37：对齐官方 SocketedContainer.GetIsolatedSocket 无解返回 null 语义）；
    /// 默认 <paramref name="strictIsolated"/> 为 false 保持兼容回退。
    /// </summary>
    public static bool TryPlaceSocketEffect(
        PlayerState receiver,
        CardInstance card,
        bool avoidAdjacent,
        Random rng,
        out CardInstance? overwrittenSocketEffect,
        bool strictIsolated = false)
    {
        overwrittenSocketEffect = null;

        if (IsMusicNoteCard(card.Template, out int noteIndex))
        {
            var candidates = GatherCandidateSocketsForMusicNote(receiver, noteIndex);
            if (candidates.Count == 0) return false;
            int chosen = candidates[rng.Next(candidates.Count)];
            var existing = GetSocketEffectAt(receiver, chosen);
            if (existing != null)
            {
                receiver.SocketEffects.Remove(existing);
                existing.IsDestroyed = true;
                overwrittenSocketEffect = existing;
            }
            card.StartSlot = chosen;
            receiver.SocketEffects.Add(card);
            return true;
        }

        int width = Math.Max(1, card.SlotWidth);
        var availableSlots = new List<int>(SocketCount);
        for (int s = 0; s <= SocketCount - width; s++)
        {
            bool ok = true;
            for (int offset = 0; offset < width; offset++)
            {
                if (!IsSocketAvailable(receiver, s + offset))
                {
                    ok = false;
                    break;
                }
            }
            if (ok) availableSlots.Add(s);
        }

        if (availableSlots.Count == 0)
            return false;

        List<int> pickFrom = availableSlots;
        if (avoidAdjacent)
        {
            var isolated = new List<int>(availableSlots.Count);
            foreach (int s in availableSlots)
            {
                bool leftFree = s == 0 || GetSocketEffectAt(receiver, s - 1) == null;
                bool rightFree = s + width >= SocketCount || GetSocketEffectAt(receiver, s + width) == null;
                if (leftFree && rightFree)
                    isolated.Add(s);
            }
            if (isolated.Count > 0)
                pickFrom = isolated;
            else if (strictIsolated)
                return false;
        }

        card.StartSlot = pickFrom[rng.Next(pickFrom.Count)];
        receiver.SocketEffects.Add(card);
        return true;
    }
}
