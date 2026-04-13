using System;
using System.Threading;
using System.Threading.Tasks;

namespace RuntimeFlow.Contexts
{
    internal sealed class EnsureSceneLoadedThenInitializeScenario : IRuntimeFlowScenario
    {
        private readonly string _sceneName;

        public EnsureSceneLoadedThenInitializeScenario(string sceneName)
        {
            _sceneName = sceneName ?? throw new ArgumentNullException(nameof(sceneName));
        }

        public async Task ExecuteAsync(IRuntimeFlowContext context, CancellationToken cancellationToken = default)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            if (!await RuntimeFlowSceneUtilities
                    .IsSceneLoadedAsync(_sceneName, cancellationToken)
                    .ConfigureAwait(false))
            {
                await context.LoadSceneAdditiveAsync(_sceneName, cancellationToken).ConfigureAwait(false);
            }

            await context.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
