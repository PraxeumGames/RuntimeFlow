using RuntimeFlow.Contexts;
namespace RuntimeFlow.Loading
{
    internal interface IRuntimeScopeLifecycleProgressNotifier
    {
        void OnScopeActivationStarted(GameContextType scope, int currentStep, int totalSteps);
        void OnScopeActivationCompleted(GameContextType scope, int currentStep, int totalSteps);
        void OnScopeDeactivationStarted(GameContextType scope);
        void OnScopeDeactivationCompleted(GameContextType scope);
    }
}
