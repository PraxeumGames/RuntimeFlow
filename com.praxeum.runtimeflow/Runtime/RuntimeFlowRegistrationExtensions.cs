using VContainer;

namespace RuntimeFlow
{
    /// <summary>Optional sugar for registering initializable services with VContainer.</summary>
    public static class RuntimeFlowRegistrationExtensions
    {
        /// <summary>
        /// Registers <typeparamref name="T"/> as itself and as every interface it implements,
        /// which includes <see cref="IAsyncInitializable"/> so the scope's graph picks it up.
        /// </summary>
        public static RegistrationBuilder RegisterInitializable<T>(
            this IContainerBuilder builder,
            Lifetime lifetime = Lifetime.Singleton)
            where T : class, IAsyncInitializable
            => builder.Register<T>(lifetime).AsSelf().AsImplementedInterfaces();
    }
}
