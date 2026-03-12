using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Tests;

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

    [Fact]
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

        Assert.Equal(new[] { "Out", "Progress", "Progress", "In" }, handler.Calls);
    }

    [Fact]
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

    [Fact]
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

        Assert.True(handler.Contexts.Count >= 1);
        var ctx = handler.Contexts[0];
        Assert.Equal(GameContextType.Session, ctx.SourceScope);
        Assert.Null(ctx.SourceScopeKey);
        Assert.Equal(GameContextType.Scene, ctx.TargetScope);
        Assert.Equal(typeof(TestSceneScope), ctx.TargetScopeKey);
    }

    [Fact]
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

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => pipeline.LoadSceneAsync<TestSceneScope>());
    }
}
