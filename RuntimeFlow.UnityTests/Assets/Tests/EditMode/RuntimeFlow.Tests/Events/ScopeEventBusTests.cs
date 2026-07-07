using NUnit.Framework;
using System.Collections.Generic;
using System.Threading.Tasks;
using RuntimeFlow.Events;

namespace RuntimeFlow.Tests
{
    public sealed class ScopeEventBusTests
    {
        private sealed class TestEvent : IScopeEvent
        {
            public TestEvent(string message)
            {
                Message = message;
            }

            public string Message { get; }
        }

        private sealed class OtherEvent : IScopeEvent
        {
            public OtherEvent(int value)
            {
                Value = value;
            }

            public int Value { get; }
        }

        [Test]
        public void Publish_Local_InvokesLocalHandlersOnly()
        {
            var parent = new ScopeEventBus();
            var child = new ScopeEventBus(parent);
            var parentReceived = new List<string>();
            var childReceived = new List<string>();

            parent.Subscribe<TestEvent>(e => parentReceived.Add(e.Message));
            child.Subscribe<TestEvent>(e => childReceived.Add(e.Message));

            child.Publish(new TestEvent("hello"), EventPropagation.Local);

            Assert.That(parentReceived, Is.Empty);
            Assert.That(childReceived, Is.EqualTo(new[] { "hello" }));
        }

        [Test]
        public void Publish_Bubble_PropagatesUpward()
        {
            var global = new ScopeEventBus();
            var session = new ScopeEventBus(global);
            var scene = new ScopeEventBus(session);
            var received = new List<string>();

            global.Subscribe<TestEvent>(e => received.Add("global:" + e.Message));
            session.Subscribe<TestEvent>(e => received.Add("session:" + e.Message));
            scene.Subscribe<TestEvent>(e => received.Add("scene:" + e.Message));

            scene.Publish(new TestEvent("up"), EventPropagation.Bubble);

            Assert.That(received, Is.EqualTo(new[] { "scene:up", "session:up", "global:up" }));
        }

        [Test]
        public void Publish_Broadcast_PropagatesDownward()
        {
            var global = new ScopeEventBus();
            var session = new ScopeEventBus(global);
            var scene = new ScopeEventBus(session);
            var received = new List<string>();

            global.Subscribe<TestEvent>(e => received.Add("global:" + e.Message));
            session.Subscribe<TestEvent>(e => received.Add("session:" + e.Message));
            scene.Subscribe<TestEvent>(e => received.Add("scene:" + e.Message));

            global.Publish(new TestEvent("down"), EventPropagation.Broadcast);

            Assert.That(received, Is.EqualTo(new[] { "global:down", "session:down", "scene:down" }));
        }

        [Test]
        public void Subscribe_ReturnsDisposable_ThatRemovesHandler()
        {
            var bus = new ScopeEventBus();
            var received = new List<string>();

            var sub = bus.Subscribe<TestEvent>(e => received.Add(e.Message));
            bus.Publish(new TestEvent("first"));
            sub.Dispose();
            bus.Publish(new TestEvent("second"));

            Assert.That(received, Is.EqualTo(new[] { "first" }));
        }

        [Test]
        public void Dispose_DetachesFromParent_AndDisposesChildren()
        {
            var parent = new ScopeEventBus();
            var child = new ScopeEventBus(parent);
            var grandchild = new ScopeEventBus(child);
            var grandchildReceived = new List<string>();

            grandchild.Subscribe<TestEvent>(e => grandchildReceived.Add(e.Message));

            child.Dispose();

            // After disposing child, broadcasting from parent should not reach grandchild
            parent.Publish(new TestEvent("test"), EventPropagation.Broadcast);
            Assert.That(grandchildReceived, Is.Empty);
        }

        [Test]
        public void Subscribe_DifferentEventTypes_AreIndependent()
        {
            var bus = new ScopeEventBus();
            var testEvents = new List<string>();
            var otherEvents = new List<int>();

            bus.Subscribe<TestEvent>(e => testEvents.Add(e.Message));
            bus.Subscribe<OtherEvent>(e => otherEvents.Add(e.Value));

            bus.Publish(new TestEvent("hello"));
            bus.Publish(new OtherEvent(42));

            Assert.That(testEvents, Is.EqualTo(new[] { "hello" }));
            Assert.That(otherEvents, Is.EqualTo(new[] { 42 }));
        }

        [Test]
        public void Subscribe_AsyncHandler_IsInvoked()
        {
            var bus = new ScopeEventBus();
            var received = new List<string>();

            bus.Subscribe<TestEvent>(e =>
            {
                received.Add(e.Message);
                return Task.CompletedTask;
            });

            bus.Publish(new TestEvent("async"));

            Assert.That(received, Is.EqualTo(new[] { "async" }));
        }
    }
}
