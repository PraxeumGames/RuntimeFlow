using VContainer;

namespace RuntimeFlow
{
    /// <summary>Optional sugar for registering initializable services with VContainer.</summary>
    public static class RuntimeFlowRegistrationExtensions
    {
        /// <summary>
        /// Registers <typeparamref name="T"/> as a <see cref="Lifetime.Singleton"/>, as itself and as every
        /// interface it implements, which includes <see cref="IAsyncInitializable"/> so the scope's graph
        /// picks it up. There is no lifetime parameter: an initializable service must be a singleton
        /// (a Scoped or Transient one would be re-created uninitialized), so any other lifetime is a graph error.
        /// </summary>
        public static RegistrationBuilder RegisterInitializable<T>(this IContainerBuilder builder)
            where T : class, IAsyncInitializable
            => builder.Register<T>(Lifetime.Singleton).AsSelf().AsImplementedInterfaces();
    }
}
