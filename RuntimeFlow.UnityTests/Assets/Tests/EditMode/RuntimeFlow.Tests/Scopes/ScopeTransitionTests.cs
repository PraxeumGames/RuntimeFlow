using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests
{

public sealed class ScopeTransitionTests
{
    private sealed class TrackingTransitionHandler : IScopeTransitionHandler
    {
        public List<string> Calls { get; } = new();
        public List<ScopeTransitionContext> Contexts { get; } = new();

        public Task OnTransitionOutAsync(ScopeTransitionContext context, CancellationToken cancellationToken)
        {
            Calls.Add("Out");
            Contexts.Add(context);
            return Task.CompletedTask;
        }

        public Task OnTransitionProgressAsync(ScopeTransitionContext context, float progress, CancellationToken cancellationToken)
        {
            Calls.Add("Progress");
            Contexts.Add(context);
            return Task.CompletedTask;
        }

        public Task OnTransitionInAsync(ScopeTransitionContext context, CancellationToken cancellationToken)
        {
            Calls.Add("In");
            Contexts.Add(context);
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingTransitionHandler : IScopeTransitionHandler
    {
        public Task OnTransitionOutAsync(ScopeTransitionContext context, CancellationToken cancellationToken)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        public Task OnTransitionProgressAsync(ScopeTransitionContext context, float progress, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task OnTransitionInAsync(ScopeTransitionContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class BlockingFirstTransitionOutHandler : IScopeTransitionHandler
    {
        private int _outCalls;

        public TaskCompletionSource<bool> FirstOutStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstOut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Calls { get; } = new();

        public async Task OnTransitionOutAsync(ScopeTransitionContext context, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _outCalls);
            Calls.Add($"Out:{context.TargetScopeKey.Name}");
            if (call != 1)
                return;

            FirstOutStarted.TrySetResult(true);
            await ReleaseFirstOut.Task.ConfigureAwait(false);
        }

        public Task OnTransitionProgressAsync(ScopeTransitionContext context, float progress, CancellationToken cancellationToken)
        {
            Calls.Add($"Progress:{context.TargetScopeKey.Name}:{progress}");
            return Task.CompletedTask;
        }

        public Task OnTransitionInAsync(ScopeTransitionContext context, CancellationToken cancellationToken)
        {
            Calls.Add($"In:{context.TargetScopeKey.Name}");
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task LoadScene_WithTransitionHandler_CallsOutProgressIn()
    {
        var handler = new TrackingTransitionHandler();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(
                new AttemptControlledSceneService((_, _) => Task.CompletedTask))));
        });
        pipeline.ConfigureTransitionHandler(handler);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();

        Assert.AreEqual(new[] { "Out", "Progress", "Progress", "In" }, handler.Calls);
    }

    [Test]
    public async Task LoadScene_NullHandler_NoErrors()
    {
        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(
                new AttemptControlledSceneService((_, _) => Task.CompletedTask))));
        });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
    }

    [Test]
    public async Task TransitionHandler_ReceivesCorrectContext()
    {
        var handler = new TrackingTransitionHandler();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(
                new AttemptControlledSceneService((_, _) => Task.CompletedTask))));
        });
        pipeline.ConfigureTransitionHandler(handler);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();

        Assert.IsTrue(handler.Contexts.Count >= 1);
        var ctx = handler.Contexts[0];
        Assert.AreEqual(GameContextType.Session, ctx.SourceScope);
        Assert.IsNull(ctx.SourceScopeKey);
        Assert.AreEqual(GameContextType.Scene, ctx.TargetScope);
        Assert.AreEqual(typeof(TestSceneScope), ctx.TargetScopeKey);
    }

    [Test]
    public async Task TransitionHandler_Cancellation_Propagated()
    {
        var handler = new CancellingTransitionHandler();

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(
                new AttemptControlledSceneService((_, _) => Task.CompletedTask))));
        });
        pipeline.ConfigureTransitionHandler(handler);

        await pipeline.InitializeAsync();

        await AsyncTestAssert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.LoadSceneAsync<TestSceneScope>());
    }

    [Test]
    public async Task OverlappingSceneTransitions_SkipStalePostCallbacksAndCancelStaleCaller()
    {
        var handler = new BlockingFirstTransitionOutHandler();
        var firstSceneAttempts = 0;
        var secondSceneAttempts = 0;

        var pipeline = RuntimePipeline.Create(builder =>
        {
            builder.DefineSessionScope();
            builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(
                new AttemptControlledSceneService((_, _) =>
                {
                    Interlocked.Increment(ref firstSceneAttempts);
                    return Task.CompletedTask;
                }))));
            builder.Scene(new FallbackSceneScope(s => s.RegisterInstance<ITestSceneService>(
                new AttemptControlledSceneService((_, _) =>
                {
                    Interlocked.Increment(ref secondSceneAttempts);
                    return Task.CompletedTask;
                }))));
        });
        pipeline.ConfigureTransitionHandler(handler);

        await pipeline.InitializeAsync();

        var firstLoad = pipeline.LoadSceneAsync<TestSceneScope>();
        await handler.FirstOutStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var secondLoad = pipeline.LoadSceneAsync<FallbackSceneScope>();
        await secondLoad;

        handler.ReleaseFirstOut.TrySetResult(true);
        await AsyncTestAssert.CatchAsync<OperationCanceledException>(() => firstLoad);

        Assert.That(firstSceneAttempts, Is.EqualTo(0));
        Assert.That(secondSceneAttempts, Is.EqualTo(1));
        Assert.That(
            handler.Calls,
            Is.EqualTo(new[]
            {
                "Out:TestSceneScope",
                "Out:FallbackSceneScope",
                "Progress:FallbackSceneScope:0",
                "Progress:FallbackSceneScope:1",
                "In:FallbackSceneScope"
            }));
    }
}

}
