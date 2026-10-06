using System;
using NUnit.Framework;
using RuntimeFlow.Tests.Lifecycle.Chaos;

namespace RuntimeFlow.Tests.Lifecycle
{
    [TestFixture]
    public sealed class ChaosErrorClassifierTests
    {
        [TestCase(ChaosDisposal.Sync, true, true)]
        [TestCase(ChaosDisposal.Both, true, true)]
        [TestCase(ChaosDisposal.Async, true, false)]
        [TestCase(ChaosDisposal.None, true, false)]
        [TestCase(ChaosDisposal.Both, false, false)]
        public void SynchronousDiagnosticRequiresPlannedSynchronousFailure(ChaosDisposal disposal, bool syncThrows, bool expected)
        {
            // An intentionally throwing async disposer must never authorize an unexpected sync failure.
            var world = World(disposal, syncThrows, asyncThrows: true);
            Assert.That(ChaosRunner.ExpectedError(world, Message("session", "S1", synchronous: true)), Is.EqualTo(expected));
        }

        [TestCase("global", "S1", "InvalidOperationException")]
        [TestCase("session", "S2", "InvalidOperationException")]
        [TestCase("session", "S1", "ObjectDisposedException")]
        public void SynchronousDiagnosticRequiresActualScopeServiceAndException(string scope, string service, string exception)
        {
            var world = World(ChaosDisposal.Both, syncThrows: true, asyncThrows: true);
            Assert.That(ChaosRunner.ExpectedError(world, Message(scope, service, synchronous: true, exception)), Is.False);
        }

        [TestCase(true, true)]
        [TestCase(false, false)]
        public void AsynchronousDiagnosticRequiresItsOwnPlannedFailure(bool asyncThrows, bool expected)
        {
            var world = World(ChaosDisposal.Both, syncThrows: true, asyncThrows);
            Assert.That(ChaosRunner.ExpectedError(world, Message("session", "S1", synchronous: false)), Is.EqualTo(expected));
        }

        [Test]
        public void SynchronousDiagnosticCannotMatchAnUnconstructedPlannedSlot()
        {
            var world = World(ChaosDisposal.Sync, syncThrows: true, asyncThrows: false);
            world.Instances.Clear();
            Assert.That(ChaosRunner.ExpectedError(world, Message("session", "S1", synchronous: true)), Is.False);
        }

        private static ChaosWorld World(ChaosDisposal disposal, bool syncThrows, bool asyncThrows)
        {
            var slot = new ChaosSlot
            {
                Scope = ChaosScope.Session, Name = "S1", Type = typeof(ChaosErrorClassifierTests),
                Disposal = disposal, SyncDisposeThrows = syncThrows, AsyncDisposeThrows = asyncThrows
            };
            var scenario = new ChaosScenario();
            scenario.Session.Add(slot);
            var world = new ChaosWorld(scenario);
            world.Instances.Add(new ChaosInstance { Slot = slot, ScopeName = "session" });
            return world;
        }

        private static string Message(string scope, string service, bool synchronous, string exception = nameof(InvalidOperationException))
            => $"[RuntimeFlow] {scope}: disposing {service}{(synchronous ? " synchronously" : "")} threw {exception}; continuing teardown.";
    }
}
