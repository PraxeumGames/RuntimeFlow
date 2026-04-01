using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RuntimeFlow.Events
{
    /// <summary>
    /// Thread-safe implementation of <see cref="IScopeEventBus"/> with parent/child linking.
    /// Disposing a bus automatically detaches it from its parent and disposes all children.
    /// </summary>
    public sealed class ScopeEventBus : IScopeEventBus, IDisposable
    {
        private readonly object _sync = new();
        private readonly ScopeEventBus? _parent;
        private readonly List<ScopeEventBus> _children = new();
        private readonly List<SubscriptionEntry> _subscriptions = new();
        private bool _disposed;

        public ScopeEventBus(ScopeEventBus? parent = null)
        {
            _parent = parent;
            _parent?.AddChild(this);
        }

        public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IScopeEvent
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var entry = new SubscriptionEntry(typeof(TEvent), evt => { handler((TEvent)evt); return Task.CompletedTask; });
            lock (_sync) _subscriptions.Add(entry);
            return new Unsubscriber(this, entry);
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : IScopeEvent
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var entry = new SubscriptionEntry(typeof(TEvent), evt => handler((TEvent)evt));
            lock (_sync) _subscriptions.Add(entry);
            return new Unsubscriber(this, entry);
        }

        public void Publish<TEvent>(TEvent evt, EventPropagation propagation = EventPropagation.Local) where TEvent : IScopeEvent
        {
            if (evt == null) throw new ArgumentNullException(nameof(evt));
            switch (propagation)
            {
                case EventPropagation.Local:
                    InvokeLocal(evt);
                    break;
                case EventPropagation.Bubble:
                    InvokeLocal(evt);
                    _parent?.Publish(evt, EventPropagation.Bubble);
                    break;
                case EventPropagation.Broadcast:
                    InvokeLocal(evt);
                    ScopeEventBus[] children;
                    lock (_sync) children = _children.ToArray();
                    foreach (var child in children)
                        child.Publish(evt, EventPropagation.Broadcast);
                    break;
            }
        }

        public void Dispose()
        {
            ScopeEventBus[] childrenSnapshot;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                childrenSnapshot = _children.ToArray();
                _children.Clear();
                _subscriptions.Clear();
            }

            foreach (var child in childrenSnapshot)
                child.Dispose();

            _parent?.RemoveChild(this);
        }

        private void InvokeLocal<TEvent>(TEvent evt) where TEvent : IScopeEvent
        {
            SubscriptionEntry[] snapshot;
            lock (_sync) snapshot = _subscriptions.ToArray();

            var eventType = typeof(TEvent);
            foreach (var entry in snapshot)
            {
                if (entry.EventType == eventType)
                    entry.Handler(evt!).GetAwaiter().GetResult();
            }
        }

        private void AddChild(ScopeEventBus child)
        {
            lock (_sync) _children.Add(child);
        }

        private void RemoveChild(ScopeEventBus child)
        {
            lock (_sync) _children.Remove(child);
        }

        private void RemoveSubscription(SubscriptionEntry entry)
        {
            lock (_sync) _subscriptions.Remove(entry);
        }

        private sealed class SubscriptionEntry
        {
            public Type EventType { get; }
            public Func<object, Task> Handler { get; }

            public SubscriptionEntry(Type eventType, Func<object, Task> handler)
            {
                EventType = eventType;
                Handler = handler;
            }
        }

        private sealed class Unsubscriber : IDisposable
        {
            private readonly ScopeEventBus _bus;
            private readonly SubscriptionEntry _entry;

            public Unsubscriber(ScopeEventBus bus, SubscriptionEntry entry)
            {
                _bus = bus;
                _entry = entry;
            }

            public void Dispose() => _bus.RemoveSubscription(_entry);
        }
    }
}
