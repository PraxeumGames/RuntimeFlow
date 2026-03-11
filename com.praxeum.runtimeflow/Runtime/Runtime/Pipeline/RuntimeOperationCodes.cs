namespace RuntimeFlow.Contexts
{
    public static class RuntimeOperationCodes
    {
        public const string ColdStart = "cold_start";
        public const string Initialize = "initialize";
        public const string LoadScene = "load_scene";
        public const string LoadModule = "load_module";
        public const string ReloadModule = "reload_module";
        public const string ReloadScene = "reload_scene";
        public const string RestartSession = "restart_session";
        public const string RunFlow = "run_flow";
        public const string Recovery = "recovery";
    }
}
