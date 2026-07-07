using System;

namespace SFS.Core.GameLoading
{
    public interface IGameRestartHandler
    {
        bool IsApplicationRestarting { get; }
        event Action<bool> ApplicationRestartingChanged;
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
