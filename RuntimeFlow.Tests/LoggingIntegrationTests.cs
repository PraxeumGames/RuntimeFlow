using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using Microsoft.Extensions.Logging;
using VContainer;

namespace RuntimeFlow.Tests;

public sealed class LoggingIntegrationTests
{
    [Fact]
    public async Task Pipeline_Initialize_LogsScopeTransitions()
    {
        var factory = new CapturingLoggerFactory();

        var pipeline = RuntimePipeline.Create(
            builder =>
            {
                builder.DefineSessionScope();
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(
                    new AttemptControlledSceneService((_, _) => Task.CompletedTask))));
                builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(
                    new AttemptControlledModuleService((_, _) => Task.CompletedTask))));
            },
            loggerFactory: factory);

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();
        await pipeline.DisposeAsync();

        var messages = factory.GetAllMessages();
        Assert.Contains(messages, m => m.Contains("Pipeline initializing"));
        Assert.Contains(messages, m => m.Contains("Loading scene"));
        Assert.Contains(messages, m => m.Contains("Loading module"));
        Assert.Contains(messages, m => m.Contains("Pipeline disposed"));
    }

    [Fact]
    public async Task Pipeline_NoLoggerFactory_WorksNormally()
    {
        var pipeline = RuntimePipeline.Create(
            builder =>
            {
                builder.DefineSessionScope();
                builder.Scene(new TestSceneScope(s => s.RegisterInstance<ITestSceneService>(
                    new AttemptControlledSceneService((_, _) => Task.CompletedTask))));
                builder.Module(new TestModuleScope(m => m.RegisterInstance<ITestModuleService>(
                    new AttemptControlledModuleService((_, _) => Task.CompletedTask))));
            });

        await pipeline.InitializeAsync();
        await pipeline.LoadSceneAsync<TestSceneScope>();
        await pipeline.LoadModuleAsync<TestModuleScope>();
        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task Pipeline_ServiceInit_LoggedWithTiming()
    {
        var factory = new CapturingLoggerFactory();

        var pipeline = RuntimePipeline.Create(
            builder =>
            {
                builder.DefineSessionScope();
            },
            loggerFactory: factory);

        await pipeline.InitializeAsync();
        await pipeline.DisposeAsync();

        var messages = factory.GetAllMessages();
        Assert.Contains(messages, m => m.Contains("BuildAsync started"));
        Assert.Contains(messages, m => m.Contains("Building scope"));
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentDictionary<string, CapturingLogger> _loggers = new();

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, _ => new CapturingLogger());
        }

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        public List<string> GetAllMessages()
        {
            return _loggers.Values
                .SelectMany(l => l.Messages)
                .ToList();
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentBag<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}
