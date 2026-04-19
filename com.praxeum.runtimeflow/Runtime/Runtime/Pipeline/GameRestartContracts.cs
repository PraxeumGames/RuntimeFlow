using System;
using UniRx;

namespace SFS.Core.GameLoading
{
    public interface IGameRestartHandler
    {
        IReadOnlyReactiveProperty<bool> IsApplicationRestarting { get; }
        void Restart(string reason, bool forceSave = true, Action? callback = null);
        void RestartAndClearSecondaryUserData(string reason, bool forceSave = true);
        void HardRestart(string reason);
    }

    public interface IGameDataCleaner
    {
        void ClearSecondaryUserData();
        void ClearAllUserData();
    }

    public interface ISessionRestartAware
    {
        void BeforeSessionRestart();
    }

    public interface IGameRestartStateSaver
    {
        void SaveAppState();
    }
}
