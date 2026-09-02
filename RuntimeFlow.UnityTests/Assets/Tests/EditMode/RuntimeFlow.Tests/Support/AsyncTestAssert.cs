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
