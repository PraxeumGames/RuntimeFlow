using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests.Initialization;

public sealed class InitializationExecutionPolicyTests
{
    // -----------------------------------------------------------------------
    // Test 1: AnyThread affinity — operation is invoked and result propagates
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ExecuteAsync_AnyThread_InvokesOperationAndPropagatesResult()
    {
        var scheduler = InlineInitializationExecutionScheduler.Instance;
        var called = false;

        await scheduler.ExecuteAsync(
            InitializationThreadAffinity.AnyThread,
            ct => { called = true; return Task.CompletedTask; },
            CancellationToken.None);

        Assert.True(called);
    }

    // -----------------------------------------------------------------------
    // Test 2: MainThread affinity with no captured context — falls back inline
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ExecuteAsync_MainThread_NoContextCaptured_RunsOperationInline()
    {
        // Capture the current thread with no SynchronizationContext so that
        // MainThreadContext is null, which triggers the inline fallback path.
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        GameContextThreadDispatcher.CaptureMainThreadContext();
        try
        {
            var scheduler = InlineInitializationExecutionScheduler.Instance;
            var called = false;

            await scheduler.ExecuteAsync(
                InitializationThreadAffinity.MainThread,
                ct => { called = true; return Task.CompletedTask; },
                CancellationToken.None);

            Assert.True(called);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prev);
            GameContextThreadDispatcher.CaptureMainThreadContext();
        }
    }

    // -----------------------------------------------------------------------
    // Test 3: Exception thrown on a posted context propagates as a faulted task
    //         and does NOT escape to the unhandled-exception handler.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ExecuteAsync_MainThread_PostedContext_ExceptionPropagatesAsTaskFault()
    {
        // Install a "main thread" on a background thread so the test thread is
        // not considered the main thread.  The custom context posts via ThreadPool,
        // exercising the SynchronizationContext.Post → RunOnPostedContext path.
        var threadPoolContext = new ThreadPoolSynchronizationContext();

        await Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(threadPoolContext);
            GameContextThreadDispatcher.CaptureMainThreadContext();
        });

        try
        {
            var scheduler = InlineInitializationExecutionScheduler.Instance;
            var expected = new InvalidOperationException("boom");

            // The task must fault with the original exception — not escape
            // to the unhandled-exception handler of the sync context.
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => scheduler.ExecuteAsync(
                    InitializationThreadAffinity.MainThread,
                    _ => throw expected,
                    CancellationToken.None));

            Assert.Same(expected, ex);
        }
        finally
        {
            // Reset to test-thread baseline (null context).
            SynchronizationContext.SetSynchronizationContext(null);
            GameContextThreadDispatcher.CaptureMainThreadContext();
        }
    }

    // -----------------------------------------------------------------------
    // Test 4: Pre-cancelled token causes a cancelled (not faulted) task
    // -----------------------------------------------------------------------
    [Fact]
    public async Task ExecuteAsync_PreCancelledToken_TaskIsCancelled()
    {
        var scheduler = InlineInitializationExecutionScheduler.Instance;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var called = false;
        var task = scheduler.ExecuteAsync(
            InitializationThreadAffinity.AnyThread,
            ct => { called = true; return Task.CompletedTask; },
            cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled, "Task must be in Cancelled state, not Faulted.");
        Assert.False(called, "Operation must not be invoked when the token is already cancelled.");
    }

    // -----------------------------------------------------------------------
    // Helper — posts continuations to the ThreadPool, simulating a sync context
    // that is distinct from the current thread.
    // -----------------------------------------------------------------------
    private sealed class ThreadPoolSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
            => ThreadPool.QueueUserWorkItem(_ => d(state), null);

        public override void Send(SendOrPostCallback d, object? state)
            => throw new NotSupportedException("Send is not supported in ThreadPoolSynchronizationContext.");
    }
}
