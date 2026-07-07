using NUnit.Framework;
using System;
using System.Threading.Tasks;

namespace RuntimeFlow.Tests
{
    internal static class AsyncTestAssert
    {
        public static async Task DoesNotThrowAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                Assert.Fail($"Expected no exception, but got {exception.GetType().FullName}: {exception.Message}");
            }
        }

        public static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                Assert.That(exception, Is.TypeOf<TException>());
                return (TException)exception;
            }

            Assert.Fail($"Expected exception of type {typeof(TException).FullName}.");
            throw new InvalidOperationException("Assert.Fail should have thrown.");
        }

        public static async Task<TException> CatchAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                Assert.That(exception, Is.AssignableTo<TException>());
                return (TException)exception;
            }

            Assert.Fail($"Expected exception assignable to {typeof(TException).FullName}.");
            throw new InvalidOperationException("Assert.Fail should have thrown.");
        }
    }

    internal static class TaskTimeoutExtensions
    {
        public static async Task WaitAsync(this Task task, TimeSpan timeout)
        {
            var timeoutTask = Task.Delay(timeout);
            var completedTask = await Task.WhenAny(task, timeoutTask);
            if (!ReferenceEquals(completedTask, task))
            {
                throw new TimeoutException($"Task did not complete within {timeout}.");
            }

            await task;
        }

        public static async Task<TResult> WaitAsync<TResult>(this Task<TResult> task, TimeSpan timeout)
        {
            var timeoutTask = Task.Delay(timeout);
            var completedTask = await Task.WhenAny(task, timeoutTask);
            if (!ReferenceEquals(completedTask, task))
            {
                throw new TimeoutException($"Task did not complete within {timeout}.");
            }

            return await task;
        }
    }
}
