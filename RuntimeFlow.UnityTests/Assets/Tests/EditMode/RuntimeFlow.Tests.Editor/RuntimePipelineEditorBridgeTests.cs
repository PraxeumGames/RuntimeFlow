using NUnit.Framework;
using System;
using System.Threading.Tasks;
using RuntimeFlow.Contexts;
using RuntimeFlow.Editor.Dashboard.Adapters;

namespace RuntimeFlow.Tests.Editor
{
    public sealed class RuntimePipelineEditorBridgeTests
    {
        [Test]
        public void TryGetActivePipeline_WithoutPipeline_ReturnsFalse()
        {
            var previous = RuntimePipeline.ActivePipeline;
            RuntimePipeline.ActivePipeline = null;
            try
            {
                Assert.IsFalse(RuntimePipelineEditorBridge.TryGetActivePipeline(out var pipeline));
                Assert.IsNull(pipeline);
            }
            finally
            {
                RuntimePipeline.ActivePipeline = previous;
            }
        }

        [Test]
        public async Task TryGetActivePipeline_WithActivePipeline_ReturnsIt()
        {
            var previous = RuntimePipeline.ActivePipeline;
            var pipeline = CreatePipeline();
            try
            {
                RuntimePipeline.ActivePipeline = pipeline;

                Assert.IsTrue(RuntimePipelineEditorBridge.TryGetActivePipeline(out var active));
                Assert.AreSame(pipeline, active);
            }
            finally
            {
                RuntimePipeline.ActivePipeline = previous;
                await pipeline.DisposeAsync();
            }
        }

        [Test]
        public async Task GenerateDiagnosticsDumpJson_ProducesValidJsonShape()
        {
            var previous = RuntimePipeline.ActivePipeline;
            var pipeline = CreatePipeline();
            try
            {
                await pipeline.InitializeAsync();
                RuntimePipeline.ActivePipeline = pipeline;

                var json = RuntimePipelineEditorBridge.GenerateDiagnosticsDumpJson();

                StringAssert.Contains("\"timestampUtc\"", json);
                StringAssert.Contains("\"isPlaying\"", json);
                StringAssert.Contains("\"pipeline\"", json);
                StringAssert.Contains("\"scopes\"", json);
                StringAssert.Contains("\"globalInitialized\": true", json);
            }
            finally
            {
                RuntimePipeline.ActivePipeline = previous;
                await pipeline.DisposeAsync();
            }
        }

        [Test]
        public void GenerateDiagnosticsDumpJson_WithoutPipeline_ReportsNullPipeline()
        {
            var previous = RuntimePipeline.ActivePipeline;
            RuntimePipeline.ActivePipeline = null;
            try
            {
                var json = RuntimePipelineEditorBridge.GenerateDiagnosticsDumpJson();
                StringAssert.Contains("\"pipeline\": null", json);
            }
            finally
            {
                RuntimePipeline.ActivePipeline = previous;
            }
        }

        private static RuntimePipeline CreatePipeline()
        {
            return RuntimePipeline.Create(builder =>
            {
                builder.DefineGlobalScope();
                builder.DefineSessionScope();
            });
        }
    }
}
