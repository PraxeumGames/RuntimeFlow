using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using RuntimeFlow.Tests.Shared;

namespace RuntimeFlow.Tests.Support
{
    /// <summary>
    /// Self-tests of <see cref="FailOnEscapedCancellationAttribute"/>: Unity's runner reports an async test
    /// whose task ends Canceled as passed, so the attribute must be wired to this assembly and must turn a
    /// cancellation into a failure while leaving faults and successes alone.
    /// </summary>
    [TestFixture]
    public sealed class EscapedCancellationSelfTests
    {
        /// <summary>
        /// Hands the running test to the test body: Unity runs async tests outside NUnit's execution
        /// context, so <c>TestExecutionContext.CurrentContext</c> is unavailable there.
        /// </summary>
        [AttributeUsage(AttributeTargets.Method)]
        private sealed class CaptureTestAttribute : Attribute, ITestAction
        {
            public static ITest? Captured { get; private set; }

            public ActionTargets Targets => ActionTargets.Test;

            public void BeforeTest(ITest test) => Captured = test;

            public void AfterTest(ITest test) => Captured = null;
        }

        [Test, CaptureTest]
        public async Task ThisAsyncTestIsRoutedThroughTheMechanism()
        {
            await Task.Yield();
            Assert.That(CaptureTestAttribute.Captured, Is.Not.Null);
            Assert.That(FailOnEscapedCancellationAttribute.IsSurfaced(CaptureTestAttribute.Captured!.Method),
                Is.True, "[assembly: FailOnEscapedCancellation] must rewrite every Task-returning test");
        }

        [Test]
        public async Task AnEscapingCancellationBecomesAFailureCarryingTheOriginalException()
        {
            async Task Body()
            {
                await Task.Yield();
                throw new OperationCanceledException("escaped from the body");
            }

            var body = Body();
            var surfaced = FailOnEscapedCancellationAttribute.Surface(body);

            var failure = await AsyncTestAssert.ThrowsAsync<AssertionException>(() => surfaced);
            Assert.That(body.IsCanceled, Is.True, "precondition: the runner sees a cancelled task, not a fault");
            Assert.That(surfaced.IsFaulted, Is.True);
            Assert.That(failure.Message, Does.Contain("ended Canceled").And.Contain("escaped from the body"));
        }

        [Test]
        public async Task AnAwaitedCancelledTaskBecomesAFailure()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var surfaced = FailOnEscapedCancellationAttribute.Surface(Task.Delay(1000, cts.Token));

            await AsyncTestAssert.ThrowsAsync<AssertionException>(() => surfaced);
        }

        [Test]
        public async Task FaultsAndSuccessesPassThroughUnchanged()
        {
            var fault = new InvalidOperationException("boom");

            var faulted = await AsyncTestAssert.ThrowsAsync<InvalidOperationException>(
                () => FailOnEscapedCancellationAttribute.Surface(Task.FromException(fault)));
            Assert.That(faulted, Is.SameAs(fault));

            await AsyncTestAssert.DoesNotThrowAsync(() => FailOnEscapedCancellationAttribute.Surface(Task.CompletedTask));
        }
    }
}
