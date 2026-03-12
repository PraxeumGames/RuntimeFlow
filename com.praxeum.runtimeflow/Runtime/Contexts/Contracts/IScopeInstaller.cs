namespace RuntimeFlow.Contexts
{
    /// <summary>
    /// Base interface for scope installers that configure their own DI registrations.
    /// Implement <see cref="ISceneScope"/> or <see cref="IModuleScope"/> instead of using this directly.
    /// </summary>
    public interface IScopeInstaller
    {
        /// <summary>Registers services into the scope's DI container.</summary>
        void Configure(IGameScopeRegistrationBuilder builder);
    }

    /// <summary>
    /// Marks a class as a scene scope installer. Implement <see cref="IScopeInstaller.Configure"/>
    /// to register services that belong to this scene scope.
    /// </summary>
    /// <example>
    /// <code>
    /// public class MainMenuScene : ISceneScope
    /// {
    ///     public void Configure(IGameScopeRegistrationBuilder builder)
    ///     {
    ///         builder.Register&lt;IMenuUI, MenuUI&gt;(Lifetime.Singleton);
    ///     }
    /// }
    /// </code>
    /// </example>
    public interface ISceneScope : IScopeInstaller { }

    /// <summary>
    /// Marks a class as a module scope installer. Implement <see cref="IScopeInstaller.Configure"/>
    /// to register services that belong to this module scope.
    /// </summary>
    /// <example>
    /// <code>
    /// public class InventoryModule : IModuleScope
    /// {
    ///     public void Configure(IGameScopeRegistrationBuilder builder)
    ///     {
    ///         builder.Register&lt;IInventorySystem, InventorySystem&gt;(Lifetime.Singleton);
    ///     }
    /// }
    /// </code>
    /// </example>
    public interface IModuleScope : IScopeInstaller { }
}
