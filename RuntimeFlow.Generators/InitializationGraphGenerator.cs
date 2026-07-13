using System;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RuntimeFlow.Generators
{
    [Generator(LanguageNames.CSharp)]
    public sealed partial class InitializationGraphGenerator : IIncrementalGenerator
    {
        private const string AsyncInitInterface = "RuntimeFlow.Contexts.IAsyncInitializableService";
        private const string GlobalMarkerInterface = "RuntimeFlow.Contexts.IGlobalInitializableService";
        private const string SessionMarkerInterface = "RuntimeFlow.Contexts.ISessionInitializableService";
        private const string SceneMarkerInterface = "RuntimeFlow.Contexts.ISceneInitializableService";
        private const string ModuleMarkerInterface = "RuntimeFlow.Contexts.IModuleInitializableService";
        private const string StartupStageMarkerInterface = "RuntimeFlow.Contexts.IStartupStageInitializableService";
        private const string PreBootstrapStartupStageMarkerInterface = "RuntimeFlow.Contexts.IPreBootstrapStartupInitializableService";
        private const string PlatformStartupStageMarkerInterface = "RuntimeFlow.Contexts.IPlatformStartupInitializableService";
        private const string ContentStartupStageMarkerInterface = "RuntimeFlow.Contexts.IContentStartupInitializableService";
        private const string SessionStartupStageMarkerInterface = "RuntimeFlow.Contexts.ISessionStartupInitializableService";
        private const string UiStartupStageMarkerInterface = "RuntimeFlow.Contexts.IUiStartupInitializableService";
        private const string GameContextType = "RuntimeFlow.Contexts.GameContextType";
        private const string DependsOnAttribute = "RuntimeFlow.Contexts.DependsOnAttribute";
        private const string GenerateGraphAttributeMetadataName = "RuntimeFlow.Contexts.GenerateRuntimeFlowInitializationGraphAttribute";
        private const string EntryPointsStartupPhaseType = "RuntimeFlow.Contexts.RuntimeFlowVContainerEntryPointsStartupPhase";
        private const string SessionSyncEntryPointsBootstrapServiceType = "RuntimeFlow.Contexts.IRuntimeFlowSessionSyncEntryPointsBootstrapService";
        private const string GraphRulesVersion = "compiled-explicit-dependencies-v4";

        private static readonly string[] MarkerOnlyAsyncContractInterfaces =
        {
            AsyncInitInterface,
            GlobalMarkerInterface,
            SessionMarkerInterface,
            SceneMarkerInterface,
            ModuleMarkerInterface,
            StartupStageMarkerInterface,
            PreBootstrapStartupStageMarkerInterface,
            PlatformStartupStageMarkerInterface,
            ContentStartupStageMarkerInterface,
            SessionStartupStageMarkerInterface,
            UiStartupStageMarkerInterface
        };

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) =>
            {
                var model = BuildModel(compilation, spc);
                if (model == null)
                    return;

                spc.AddSource("CompiledInitializationGraph.g.cs", SourceText.From(Render(model), Encoding.UTF8));
            });
        }
    }
}
