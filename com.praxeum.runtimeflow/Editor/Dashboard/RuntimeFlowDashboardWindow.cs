#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.UIElements;

namespace RuntimeFlow.Editor
{
    /// <summary>
    /// Placeholder until the 1.0 dashboard lands (plan step 7). Keeps the editor assembly compiling.
    /// The 0.x window is available for reference at git tag archive/0.10.0-wip.
    /// </summary>
    public sealed class RuntimeFlowDashboardWindow : EditorWindow
    {
        [MenuItem("Window/RuntimeFlow/Dashboard")]
        public static void Open() => GetWindow<RuntimeFlowDashboardWindow>("RuntimeFlow");

        private void CreateGUI()
        {
            rootVisualElement.Add(new Label("RuntimeFlow dashboard is being rewritten for 1.0."));
        }
    }
}
#endif
