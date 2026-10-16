using UnityEngine;
using UnityEngine.Scripting;

namespace RuntimeFlow.Internal
{
    /// <summary>
    /// Forwards <c>OnApplicationPause</c> to <see cref="AppSuspension"/> on a mobile platform. Unity delivers
    /// that message to MonoBehaviours only, so a hidden one lives on its own GameObject for the lifetime of
    /// the player: <see cref="HideFlags.HideAndDontSave"/>, kept across scene loads, never shown or saved.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class AppSuspensionHook : MonoBehaviour
    {
        private static AppSuspensionHook? _instance;

        /// <summary>
        /// Creates the hook unless one exists. Called once per Play Mode session or player start; the guard
        /// also covers a disabled domain reload, where the static survives while the object may not, and an
        /// enabled one, where the object (never unloaded in the editor) survives while the static does not.
        /// </summary>
        internal static void Install()
        {
            if (_instance != null) return;
            var existing = Resources.FindObjectsOfTypeAll<AppSuspensionHook>();
            if (existing.Length > 0)
            {
                _instance = existing[0];
                return;
            }

            var host = new GameObject("RuntimeFlow.AppSuspensionHook") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<AppSuspensionHook>();

            // The editor keeps HideAndDontSave objects when Play Mode ends; release the hook with the session.
            if (!Application.isEditor) return;
            Application.quitting -= Release;
            Application.quitting += Release;
        }

        private static void Release()
        {
            Application.quitting -= Release;
            var instance = _instance;
            _instance = null;
            if (instance != null) Destroy(instance.gameObject);
        }

        [Preserve]
        private void OnApplicationPause(bool paused) => AppSuspension.ApplyPause(paused);

        [Preserve]
        private void OnDestroy()
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        }
    }
}
