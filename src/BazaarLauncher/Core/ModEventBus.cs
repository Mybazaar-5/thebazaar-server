using System;
using System.Collections.Generic;
using BazaarModSdk;

namespace BazaarLauncher.Core;

/// <summary>
/// 强类型、零装箱、异常自动隔离的客户端事件总线实现（IModEventBus）。
/// </summary>
internal sealed class ModEventBus : IModEventBus
{
    private readonly object _sync = new();
    private readonly Dictionary<Type, object> _channels = new();

    private sealed class SubscriptionEntry<TEvent> where TEvent : struct
    {
        public Action<TEvent> Handler;
        public int Priority;
        public long Order;
    }

    private sealed class Channel<TEvent> where TEvent : struct
    {
        public readonly List<SubscriptionEntry<TEvent>> Entries = new();
        public SubscriptionEntry<TEvent>[] Snapshot = Array.Empty<SubscriptionEntry<TEvent>>();
        public long NextOrder;

        public void RebuildSnapshotLocked()
        {
            Entries.Sort((a, b) =>
            {
                int cmp = b.Priority.CompareTo(a.Priority);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });
            Snapshot = Entries.ToArray();
        }
    }

    private sealed class Unsubscriber<TEvent> : IDisposable where TEvent : struct
    {
        private readonly ModEventBus _bus;
        private readonly SubscriptionEntry<TEvent> _entry;
        private bool _disposed;

        public Unsubscriber(ModEventBus bus, SubscriptionEntry<TEvent> entry)
        {
            _bus = bus;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bus.Unsubscribe(_entry);
        }
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler, int priority = 0) where TEvent : struct
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        SubscriptionEntry<TEvent> entry;
        lock (_sync)
        {
            var channel = GetOrCreateChannelLocked<TEvent>();
            entry = new SubscriptionEntry<TEvent>
            {
                Handler = handler,
                Priority = priority,
                Order = ++channel.NextOrder,
            };
            channel.Entries.Add(entry);
            channel.RebuildSnapshotLocked();
        }

        return new Unsubscriber<TEvent>(this, entry);
    }

    public void Publish<TEvent>(in TEvent evt) where TEvent : struct
    {
        SubscriptionEntry<TEvent>[] snapshot;
        lock (_sync)
        {
            if (!_channels.TryGetValue(typeof(TEvent), out object raw))
            {
                return;
            }
            snapshot = ((Channel<TEvent>)raw).Snapshot;
        }

        for (int i = 0; i < snapshot.Length; i++)
        {
            try
            {
                snapshot[i].Handler(evt);
            }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning($"[EventBus:{typeof(TEvent).Name}] 订阅回调异常已隔离: {ex.Message}");
            }
        }
    }

    internal void ClearAll()
    {
        lock (_sync)
        {
            _channels.Clear();
        }
    }

    private void Unsubscribe<TEvent>(SubscriptionEntry<TEvent> entry) where TEvent : struct
    {
        lock (_sync)
        {
            if (_channels.TryGetValue(typeof(TEvent), out object raw))
            {
                var channel = (Channel<TEvent>)raw;
                if (channel.Entries.Remove(entry))
                {
                    channel.RebuildSnapshotLocked();
                }
            }
        }
    }

    private Channel<TEvent> GetOrCreateChannelLocked<TEvent>() where TEvent : struct
    {
        if (_channels.TryGetValue(typeof(TEvent), out object existing))
        {
            return (Channel<TEvent>)existing;
        }
        var created = new Channel<TEvent>();
        _channels[typeof(TEvent)] = created;
        return created;
    }
}
