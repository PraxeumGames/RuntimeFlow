using System;
using System.Threading.Tasks;

namespace RuntimeFlow.Events
{
    /// <summary>Marker interface for events published through the scope event bus.</summary>
    public interface IScopeEvent { }

    /// <summary>Controls how far a published event propagates through the scope hierarchy.</summary>
    public enum EventPropagation
    {
        /// <summary>Deliver only to handlers registered on the publishing bus.</summary>
        Local,
        /// <summary>Deliver locally, then propagate upward through parent scopes.</summary>
        Bubble,
        /// <summary>Deliver locally, then propagate downward through child scopes.</summary>
        Broadcast
    }

    /// <summary>
    /// A hierarchical event bus that follows the Global → Session → Scene → Module scope chain.
    /// Each scope owns one bus instance; parent/child relationships are established automatically.
    /// </summary>
    public interface IScopeEventBus
    {
        /// <summary>Subscribe to events of type <typeparamref name="TEvent"/>.</summary>
        /// <returns>A disposable that removes the subscription when disposed.</returns>
        IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IScopeEvent;

        /// <summary>Subscribe to events of type <typeparamref name="TEvent"/> with an async handler.</summary>
        IDisposable Subscribe<TEvent>(Func<TEvent, System.Threading.Tasks.Task> handler) where TEvent : IScopeEvent;

        /// <summary>Publish an event with the specified propagation strategy.</summary>
        void Publish<TEvent>(TEvent evt, EventPropagation propagation = EventPropagation.Local) where TEvent : IScopeEvent;
    }
}
