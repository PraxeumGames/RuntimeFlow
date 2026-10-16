using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace RuntimeFlow.Tests.Support
{
    /// <summary>
    /// Awaiting replacements for NUnit's Assert.ThrowsAsync, which blocks the Unity main thread and
    /// therefore deadlocks any continuation posted to the editor's synchronization context.
    /// </summary>
    public static class AsyncTestAssert
    {
        /// <summary>Awaits the action and asserts it threw <typeparamref name="T"/>; returns the exception.</summary>
        public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try
            {
                await action();
            }
            catch (T expected)
            {
                return expected;
            }
            catch (Exception other)
            {
                Assert.Fail($"Expected {typeof(T).Name} but got {other.GetType().Name}: {other.Message}");
                throw;
            }

            Assert.Fail($"Expected {typeof(T).Name} but no exception was thrown.");
            throw new InvalidOperationException("unreachable");
        }

        /// <summary>
        /// Polls <paramref name="condition"/> until it holds, and fails the test when it never does. Use it
        /// instead of a fixed delay whenever a test waits for something the framework produces on its own
        /// schedule (a stall warning, a watch tick): the delay would either race or be needlessly slow.
        /// </summary>
        /// <param name="condition">Checked once per poll; must eventually become true.</param>
        /// <param name="timeout">How long to keep polling before failing.</param>
        /// <param name="because">Text added to the failure message.</param>
        public static async Task Until(Func<bool> condition, TimeSpan timeout, string because = "")
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"The condition was still false after {timeout.TotalSeconds:0.0}s. {because}");
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }
        }

        /// <summary>Awaits the action and fails the test if it throws.</summary>
        public static async Task DoesNotThrowAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception thrown)
            {
                Assert.Fail($"Expected no exception but got {thrown.GetType().Name}: {thrown.Message}");
            }
        }
    }
}
