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
        private const string GraphRulesVersion = "compiled-constructor-v3";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) =>
            {
                var model = BuildModel(compilation, spc);
                spc.AddSource("CompiledInitializationGraph.g.cs", SourceText.From(Render(model), Encoding.UTF8));
            });
        }
    }
}
