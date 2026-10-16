using System;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using RuntimeFlow.Tests.Shared;

namespace RuntimeFlow.Tests.PlayMode
{
    /// <summary>
    /// Proves <c>[assembly: FailOnEscapedCancellation]</c> is wired to the PlayMode assembly, so an async
    /// PlayMode test whose task ends Canceled fails instead of being reported as passed.
    /// </summary>
    [TestFixture]
    public sealed class EscapedCancellationWiringTests
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
                Is.True);
        }
    }
}
