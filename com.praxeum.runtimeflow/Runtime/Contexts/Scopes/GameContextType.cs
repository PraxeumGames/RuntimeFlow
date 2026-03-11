namespace RuntimeFlow.Contexts
{
    public enum GameContextType
    {
        Global,
        Session,
        Scene,
        Module
    }

    /// <summary>Built-in marker for the global root scope.</summary>
    public sealed class GlobalScope { }

    /// <summary>Built-in marker for the session root scope.</summary>
    public sealed class SessionScope { }
}
