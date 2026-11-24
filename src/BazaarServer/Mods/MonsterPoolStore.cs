using System.Collections.Immutable;
using BazaarServer.Data;

namespace BazaarServer.Mods;

/// <summary>
/// 不可变野怪池原子快照存储（D8-P1-01）：
/// - 彻底替代原先被 ServerModHost 就地 <c>Clear(); AddRange();</c> 的可变全局 <c>List&lt;MonsterTemplate&gt;</c>；
/// - 每次热重载构造全新的不可变 <see cref="ImmutableArray{T}"/> 与永不就地修改的只读快照 <see cref="List{T}"/>，
///   通过 <c>volatile</c> 原子切换引用，保证在线对局并发枚举 100% 零 <see cref="InvalidOperationException"/>。
/// </summary>
public sealed class MonsterPoolStore
{
    private sealed record Snapshot(
        ImmutableArray<MonsterTemplate> Items,
        List<MonsterTemplate> ReadOnlyListSnapshot);

    private volatile Snapshot _snapshot;

    public MonsterPoolStore(IEnumerable<MonsterTemplate>? initial = null)
    {
        _snapshot = BuildSnapshot(initial);
    }

    /// <summary>当前生效的不可变野怪快照数组。</summary>
    public ImmutableArray<MonsterTemplate> Current => _snapshot.Items;

    /// <summary>当前野怪模板总数。</summary>
    public int Count => _snapshot.Items.Length;

    /// <summary>
    /// 返回当前快照对应的永不就地修改的 <see cref="List{T}"/> 视图（零拷贝、多线程只读枚举 100% 线程安全，向后兼容现有 List 签名）。
    /// </summary>
    public List<MonsterTemplate> SnapshotList() => _snapshot.ReadOnlyListSnapshot;

    /// <summary>原子替换野怪池快照（绝不修改任何旧快照实例）。</summary>
    public void Replace(IEnumerable<MonsterTemplate>? monsters)
    {
        _snapshot = BuildSnapshot(monsters);
    }

    private static Snapshot BuildSnapshot(IEnumerable<MonsterTemplate>? src)
    {
        var arr = src != null
            ? ImmutableArray.CreateRange(src)
            : ImmutableArray<MonsterTemplate>.Empty;
        var listCopy = new List<MonsterTemplate>(arr.Length);
        listCopy.AddRange(arr);
        return new Snapshot(arr, listCopy);
    }
}
