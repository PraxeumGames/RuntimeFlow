using System;
using RuntimeFlow.Contexts;

namespace RuntimeFlow.Initialization.Planning
{
    /// <summary>
    /// Declares the relative progress weight of a load-graph node. A long content download
    /// deserves more of the loading bar than a cheap registry warm-up: default weight is 1,
    /// set higher for heavy nodes. Applied by the weighted progress pipeline when the active
    /// notifier supports <c>IWeightedInitializationProgressNotifier</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class LoadWeightAttribute : Attribute
    {
        public LoadWeightAttribute(double weight)
        {
            if (weight <= 0 || double.IsNaN(weight) || double.IsInfinity(weight))
                throw new ArgumentOutOfRangeException(nameof(weight), weight, "Weight must be a positive finite number.");
            Weight = weight;
        }

        public double Weight { get; }
    }

    /// <summary>Cached reader of <see cref="LoadWeightAttribute"/> for node implementations.</summary>
    internal static class LoadNodeWeights
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, double> Cache = new();

        /// <summary>
        /// Runtime-bound weights for sources whose type cannot carry attributes
        /// (delegate content sources); GoldenPath populates this at composition time.
        /// </summary>
        public static System.Collections.Concurrent.ConcurrentDictionary<Type, double> RuntimeOverrides { get; }
            = new();

        public static double Resolve(Type implementationType)
        {
            if (RuntimeOverrides.TryGetValue(implementationType, out var bound))
                return bound;
            return Cache.GetOrAdd(implementationType, static t =>
            {
                var attr = (LoadWeightAttribute?)Attribute.GetCustomAttribute(t, typeof(LoadWeightAttribute), inherit: true);
                return attr?.Weight ?? 1.0;
            });
        }
    }

    /// <summary>
    /// Optional richer progress surface: carries fractional, weight-based completion so
    /// loading screens can show true remaining work instead of equal service steps.
    /// Implementations that do not support it simply keep receiving the classic integer
    /// step notifications on <see cref="Contexts.IInitializationProgressNotifier"/>.
    /// </summary>
    public interface IWeightedInitializationProgressNotifier
    {
        /// <summary>Called when a scope begins initializing.</summary>
        void OnScopeStarted(GameContextType scope, double totalWeight, int totalServices);

        /// <summary>Called just before a node starts; <paramref name="completedWeight"/> excludes it.</summary>
        void OnServiceStarted(GameContextType scope, Type serviceType, double completedWeight, double totalWeight);

        /// <summary>Sub-progress of a single node: <paramref name="nodeProgress"/> is 0..1.</summary>
        void OnServiceProgress(GameContextType scope, Type serviceType, float nodeProgress, string? message, double completedWeight, double nodeWeight, double totalWeight);

        /// <summary>Called after a node finished; <paramref name="completedWeight"/> includes it.</summary>
        void OnServiceCompleted(GameContextType scope, Type serviceType, double completedWeight, double totalWeight);
    }

    /// <summary>
    /// Optional notifications about player-interaction gates opened/closed during loading so
    /// the UI can show "waiting for you" instead of an endless spinner.
    /// </summary>
    public interface IUserGateProgressNotifier
    {
        void OnGateOpened(GameContextType scope, Type serviceType, string prompt);
        void OnGateClosed(GameContextType scope, Type serviceType);
    }
}
