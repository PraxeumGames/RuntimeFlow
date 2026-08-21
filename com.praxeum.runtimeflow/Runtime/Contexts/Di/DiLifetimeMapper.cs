using VContainer;

namespace RuntimeFlow.Contexts
{
    internal static class DiLifetimeMapper
    {
        public static Lifetime ToVContainer(DiLifetime lifetime) => lifetime switch
        {
            DiLifetime.Transient => Lifetime.Transient,
            DiLifetime.Singleton => Lifetime.Singleton,
            DiLifetime.Scoped => Lifetime.Scoped,
            _ => Lifetime.Transient
        };

        public static DiLifetime FromVContainer(Lifetime lifetime) => lifetime switch
        {
            Lifetime.Transient => DiLifetime.Transient,
            Lifetime.Singleton => DiLifetime.Singleton,
            Lifetime.Scoped => DiLifetime.Scoped,
            _ => DiLifetime.Transient
        };
    }
}
