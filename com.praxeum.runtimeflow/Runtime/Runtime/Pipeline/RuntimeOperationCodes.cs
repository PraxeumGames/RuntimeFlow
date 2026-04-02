namespace RuntimeFlow.Contexts
{
    public static class RuntimeOperationCodes
    {
        public const string ColdStart = "cold_start";
        public const string Initialize = "initialize";
        public const string LoadScene = "load_scene";
        public const string LoadSceneScope = "load_scene_scope";
        public const string LoadModule = "load_module";
        public const string LoadModuleScope = "load_module_scope";
        public const string ReloadModule = "reload_module";
        public const string ReloadModuleScope = "reload_module_scope";
        public const string ReloadScene = "reload_scene";
        public const string ReloadSceneScope = "reload_scene_scope";
        public const string RestartSession = "restart_session";
        public const string RestartSessionScope = "restart_session_scope";
        public const string RunFlow = "run_flow";
        public const string Recovery = "recovery";
    }
}
