using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using Microsoft.Extensions.Logging;
using VContainer;

namespace RuntimeFlow.Tests
{
    public sealed class LoggingIntegrationTests
    {
        [Test]
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
            Assert.That(messages, Has.Some.Contains("Pipeline initializing"));
            Assert.That(messages, Has.Some.Contains("Loading scene"));
            Assert.That(messages, Has.Some.Contains("Loading module"));
            Assert.That(messages, Has.Some.Contains("Pipeline disposed"));
        }

        [Test]
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

        [Test]
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
            Assert.That(messages, Has.Some.Contains("BuildAsync started"));
            Assert.That(messages, Has.Some.Contains("Building scope"));
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
}
