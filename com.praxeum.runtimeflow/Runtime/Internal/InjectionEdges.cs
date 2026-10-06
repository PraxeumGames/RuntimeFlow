using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using VContainer;

namespace RuntimeFlow.Internal
{
    /// <summary>
    /// Resolves an injected type to the graph nodes it really depends on, the way VContainer resolves it:
    /// a single type to the registration VContainer picks (the nearest scope, the last registration of
    /// that type there — or, for a <c>[Key(…)]</c> member on VContainer 1.19+, the registration with that
    /// key), <c>IEnumerable&lt;T&gt;</c>/<c>IReadOnlyList&lt;T&gt;</c> to the elements VContainer merges from
    /// this scope and its parents (ContainerLocal collections exclude parent singletons). A registration that is a node (own or external) ends the walk; any other one is walked
    /// through transitively — Profile(IProfileApi) → ProfileApi(Auth) → Auth gives Profile an edge to Auth
    /// "via ProfileApi". Factory and instance registrations end the walk: what a factory resolves is
    /// invisible to reflection.
    /// </summary>
    /// <remarks>
    /// A walk yields each reachable node once, with the first chain it was reached through, and results are
    /// memoised per registration and effective construction resolver, so the cost
    /// grows with the number of registrations and nodes, not with the number of paths through a lattice
    /// of plain classes.
    /// </remarks>
    internal sealed class InjectionEdges
    {
        private static readonly ConcurrentDictionary<Type, FieldInfo?> ParameterFields =
            new ConcurrentDictionary<Type, FieldInfo?>();

        /// <summary><c>TryGetRegistration(Type, out Registration, object key)</c>, present on VContainer 1.19+.</summary>
        private static readonly MethodInfo? KeyedLookup = typeof(IObjectResolver).GetMethod(
            "TryGetRegistration", new[] { typeof(Type), typeof(Registration).MakeByRefType(), typeof(object) });

        private static readonly IReadOnlyList<(ServiceNode, string)> None = Array.Empty<(ServiceNode, string)>();

        private readonly Dictionary<Registration, ServiceNode> _nodes = new Dictionary<Registration, ServiceNode>();
        private readonly Dictionary<(Registration, IObjectResolver), IReadOnlyList<(ServiceNode, string)>> _walks =
            new Dictionary<(Registration, IObjectResolver), IReadOnlyList<(ServiceNode, string)>>();
        private readonly HashSet<(Registration, IObjectResolver)> _path = new HashSet<(Registration, IObjectResolver)>();
        private readonly Dictionary<(Registration, IObjectResolver), IObjectResolver> _constructionScopes =
            new Dictionary<(Registration, IObjectResolver), IObjectResolver>();
        private static readonly FieldInfo? RegistrationKey = typeof(Registration).GetField("Key");
        private static readonly ConcurrentDictionary<Type, bool> CollectionResolutionScopes =
            new ConcurrentDictionary<Type, bool>();
        private readonly ILogger? _logger;
        private readonly string _name;

        public InjectionEdges(IObjectResolver scope, IEnumerable<ServiceNode> nodes, ILogger? logger = null, string name = "")
        {
            Scope = scope;
            _logger = logger;
            _name = name;
            foreach (var node in nodes)
            {
                if (node.Registration != null && !_nodes.ContainsKey(node.Registration)) _nodes.Add(node.Registration, node);
            }
        }

        /// <summary>The scope whose graph is being built; its services resolve their dependencies from here.</summary>
        public IObjectResolver Scope { get; }

        /// <summary>
        /// The first scene-component node (<c>RegisterComponentInHierarchy</c>, Lifetime.Scoped) a walk reached
        /// from a scope below the one registering it — where VContainer would create it anew — or null.
        /// </summary>
        public ServiceNode? RespawnedSceneComponent { get; private set; }

        /// <summary>
        /// The nodes <paramref name="point"/> leads to when injected by a registration living in
        /// <paramref name="from"/>, each once, with the chain of plain registrations it was reached through
        /// (empty for a direct edge).
        /// </summary>
        public IReadOnlyList<(ServiceNode Target, string Via)> Targets(InjectionPoint point, IObjectResolver from, Registration? owner)
            => Targets(point.Type, from, owner, point.Keyed, point.ServiceKey);

        /// <summary>The nodes an unkeyed injection of <paramref name="type"/> leads to; see the other overload.</summary>
        public IReadOnlyList<(ServiceNode Target, string Via)> Targets(Type type, IObjectResolver from, Registration? owner)
            => Targets(type, from, owner, false, null);

        private IReadOnlyList<(ServiceNode Target, string Via)> Targets(Type type, IObjectResolver from, Registration? owner,
            bool keyed, object? key)
        {
            var valueType = ConstructorEdges.Unwrap(type);
            if (ConstructorEdges.IsIgnored(valueType)) return None;
            if (!Find(from, type, keyed, key, out var registration, out var foundIn)) return None;
            if (owner != null && ReferenceEquals(registration, owner)) return None;

            var result = new List<(ServiceNode, string)>();
            var seen = new HashSet<ServiceNode>();
            AddResolved(result, seen, registration, foundIn, from);
            return result;
        }

        // Preserve the provider rather than stripping ContainerLocal from the requested type: explicit
        // wrapper registrations are ordinary registrations, and its collection provider filters parents.
        private void AddResolved(List<(ServiceNode, string)> result, HashSet<ServiceNode> seen,
            Registration registration, IObjectResolver foundIn, IObjectResolver requester)
        {
            if (registration.Provider?.GetType().Name == "ContainerLocalInstanceProvider")
            {
                var value = registration.Provider.GetType().GetField("valueRegistration",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(registration.Provider) as Registration;
                if (value == null)
                    throw new InitGraphException(_name, "Unsupported VContainer ContainerLocal provider: cannot inspect its value registration.");
                if (IsCollection(value) && requester is ScopedContainer)
                    AddCollection(result, seen, value, requester, true);
                else
                    AddResolved(result, seen, value, foundIn, requester);
                return;
            }
            if (IsCollection(registration))
                AddCollection(result, seen, registration, requester, false);
            else
                Add(result, seen, registration, foundIn, requester);
        }

        /// <summary>The WithParameter values of a registration, which VContainer injects instead of resolving.</summary>
        public static IReadOnlyList<IInjectParameter>? CustomParameters(Registration? registration)
        {
            var provider = registration?.Provider;
            if (provider == null) return null;
            var field = ParameterFields.GetOrAdd(provider.GetType(),
                t => t.GetField("customParameters", BindingFlags.Instance | BindingFlags.NonPublic));
            try
            {
                return field?.GetValue(provider) as IReadOnlyList<IInjectParameter>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>True when a WithParameter value supplies <paramref name="point"/>, so it is not resolved at all.</summary>
        public static bool Supplied(IReadOnlyList<IInjectParameter>? parameters, InjectionPoint point)
        {
            if (parameters == null) return false;
            for (var i = 0; i < parameters.Count; i++)
            {
                if (parameters[i].Match(point.Type, point.Name)) return true;
            }
            return false;
        }

        /// <summary>
        /// True for registrations VContainer constructs or injects by reflection: plain types and scene
        /// components. Instances, factories and the container itself are not injected by VContainer.
        /// </summary>
        public static bool IsReflected(Registration registration)
        {
            var provider = registration.Provider?.GetType().Name;
            return provider == "InstanceProvider"
                   || provider == "ExistingComponentProvider"
                   || provider == "FindComponentProvider"
                   || provider == "NewGameObjectProvider"
                   || provider == "PrefabComponentProvider";
        }

        private void Add(List<(ServiceNode, string)> result, HashSet<ServiceNode> seen, Registration registration,
            IObjectResolver foundIn, IObjectResolver requester)
        {
            if (_nodes.TryGetValue(registration, out var node))
            {
                if (node.Kind == NodeKind.External && registration.Lifetime == Lifetime.Singleton
                    && !HasExistingIdentity(registration)
                    && !ReferenceEquals(ConstructionScope(registration, requester), ConstructionScope(registration, NodeScope(node, foundIn))))
                {
                    var reinjects = registration.Provider?.GetType().Name == "ExistingComponentProvider";
                    throw new InitGraphException(_name,
                        $"Scope '{_name}' {(reinjects ? "re-injects parent component" : "resolves a new instance of parent service")} {node.Name} from scope '{node.Scope}': " +
                        $"VContainer keeps its inherited Singleton registration in a child that registers {registration.ImplementationType.Name} " +
                        "under any contract with the same key. " +
                        (reinjects ? "Child-scope injection would replace dependencies on an already initialized parent object. "
                            : "The initialized parent instance cannot satisfy this dependency. ") +
                        "Remove the child registration of that implementation, use a distinct implementation, or register the required " +
                        "child service explicitly as IAsyncInitializable and inject its child contract.");
                }
                if (!seen.Add(node)) return;
                result.Add((node, string.Empty));
                // A Scoped registration is created anew by the scope that resolves it: for a scene component
                // of a parent scope that means injecting the very same component again.
                if (RespawnedSceneComponent == null && !ReferenceEquals(foundIn, requester) && GraphBuilder.IsSceneComponent(registration))
                    RespawnedSceneComponent = node;
                return;
            }

            var name = Fmt.Type(registration.ImplementationType);
            foreach (var (target, via) in Through(registration, requester))
            {
                if (!seen.Add(target)) continue;
                result.Add((target, via.Length == 0 ? name : name + " > " + via));
            }
        }

        /// <summary>Nodes reached by walking into what VContainer injects into a plain (non-node) registration.</summary>
        private IReadOnlyList<(ServiceNode, string)> Through(Registration registration, IObjectResolver requester)
        {
            if (!IsReflected(registration)) return None;
            var from = ConstructionScope(registration, requester);
            var walk = (registration, from);
            if (_walks.TryGetValue(walk, out var known)) return known;

            // A cycle among plain registrations makes VContainer throw at resolution; it is not an edge.
            if (!_path.Add(walk)) return None;
            var result = new List<(ServiceNode, string)>();
            var seen = new HashSet<ServiceNode>();
            try
            {
                var custom = CustomParameters(registration);
                foreach (var point in ConstructorEdges.InjectionPoints(registration.ImplementationType))
                {
                    if (Supplied(custom, point) || ConstructorEdges.IsLazy(point.Type)) continue;
                    foreach (var entry in Targets(point.Type, from, registration, point.Keyed, point.ServiceKey))
                    {
                        if (seen.Add(entry.Target)) result.Add(entry);
                    }
                }
            }
            finally
            {
                _path.Remove(walk);
            }

            _walks[walk] = result;
            return result;
        }

        /// <summary>
        /// VContainer's FindRegistration: the nearest scope that registers <paramref name="type"/> (with
        /// <paramref name="key"/> when the member is keyed). A lookup that throws — a constructed generic
        /// whose open-generic registration cannot be closed, say — makes VContainer throw at resolution too,
        /// where the construction error explains it: here it is simply no edge.
        /// </summary>
        private bool Find(IObjectResolver from, Type type, bool keyed, object? key, out Registration registration, out IObjectResolver scope)
        {
            registration = null!;
            scope = null!;
            if (keyed && KeyedLookup == null) return false; // [Key] without keyed lookups: use [DependsOn]

            try
            {
                IObjectResolver? current = from;
                while (current != null)
                {
                    if (TryGet(current, type, keyed, key, out var found) && found != null)
                    {
                        registration = found;
                        scope = current;
                        return true;
                    }
                    current = (current as IScopedObjectResolver)?.Parent;
                }
            }
            catch (Exception exception)
            {
                Report(type, exception);
            }
            return false;
        }

        private static bool TryGet(IObjectResolver scope, Type type, bool keyed, object? key, out Registration? registration)
        {
            if (!keyed)
            {
                var found = scope.TryGetRegistration(type, out var plain);
                registration = plain;
                return found;
            }

            var arguments = new object?[] { type, null, key };
            try
            {
                var found = (bool)KeyedLookup!.Invoke(scope, arguments)!;
                registration = arguments[1] as Registration;
                return found;
            }
            catch (TargetInvocationException invocation) when (invocation.InnerException != null)
            {
                throw invocation.InnerException;
            }
        }

        private static bool IsCollection(Registration registration)
            => registration.Provider?.GetType().Name == "CollectionInstanceProvider"
               && registration.Provider is IEnumerable<Registration>;

        private static bool HasExistingIdentity(Registration registration)
            => registration.Provider?.GetType().Name == "ExistingInstanceProvider";

        private IObjectResolver NodeScope(ServiceNode node, IObjectResolver fallback)
        {
            IObjectResolver? current = (Scope as IScopedObjectResolver)?.Parent;
            while (current != null)
            {
                if (current.TryGetRegistration(typeof(IReadOnlyList<IAsyncInitializable>), out var collection)
                    && collection.Provider is IEnumerable<Registration> registrations)
                {
                    foreach (var registration in registrations)
                        if (ReferenceEquals(registration, node.Registration)) return current;
                }
                current = (current as IScopedObjectResolver)?.Parent;
            }
            return fallback;
        }

        // ScopedContainer.ResolveCore checks Registry.Exists, including its null implementation guards.
        // TryGetRegistration cannot substitute for Exists: a guard deliberately returns no registration.
        private IObjectResolver ConstructionScope(Registration registration, IObjectResolver requester)
        {
            if (registration.Lifetime != Lifetime.Singleton) return requester;
            var cacheKey = (registration, requester);
            if (_constructionScopes.TryGetValue(cacheKey, out var known)) return known;
            IObjectResolver current = requester;
            while (current is IScopedObjectResolver scoped)
            {
                if (scoped.Parent == null)
                {
                    current = scoped.Root;
                    break;
                }
                var registry = current.GetType().GetField("registry", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(current);
                var exists = registry?.GetType().GetMethod("Exists", RegistrationKey == null
                    ? new[] { typeof(Type) } : new[] { typeof(Type), typeof(object) });
                if (exists == null)
                    throw new InitGraphException(_name,
                        "Unsupported VContainer resolver: cannot inspect its local implementation guard for Singleton construction.");
                var arguments = RegistrationKey == null
                    ? new object?[] { registration.ImplementationType }
                    : new object?[] { registration.ImplementationType, RegistrationKey.GetValue(registration) };
                if ((bool)exists.Invoke(registry, arguments)!) break;
                current = scoped.Parent;
            }
            _constructionScopes[cacheKey] = current;
            return current;
        }

        private bool UsesRegisteredCollectionScope(Registration collection)
            => CollectionResolutionScopes.GetOrAdd(collection.Provider.GetType(), providerType =>
            {
                // Inspect the provider's actual SpawnInstance overload. Stock takes registrations;
                // the fork takes records carrying both a registration and its owning resolver.
                foreach (var method in providerType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (method.Name != "SpawnInstance") continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length != 2 || parameters[0].ParameterType != typeof(IObjectResolver)) continue;
                    var list = parameters[1].ParameterType;
                    if (!list.IsGenericType || list.GetGenericTypeDefinition() != typeof(IReadOnlyList<>)) continue;
                    var element = list.GetGenericArguments()[0];
                    if (element == typeof(Registration)) return false;
                    if (element.GetField("RegisteredContainer")?.FieldType == typeof(IObjectResolver)
                        && element.GetField("Registration")?.FieldType == typeof(Registration)) return true;
                }
                throw new InitGraphException(_name,
                    "Unsupported VContainer collection provider: cannot determine its element resolution scope.");
            });

        private void AddCollection(List<(ServiceNode, string)> result, HashSet<ServiceNode> seen,
            Registration collection, IObjectResolver requester, bool localScopeOnly)
        {
            var element = collection.ImplementationType.GetElementType();
            if (element == null)
                throw new InitGraphException(_name, "Unsupported VContainer collection provider: cannot determine its element type.");
            // Ignore framework elements only for synthetic collections. An explicitly registered
            // collection value may itself be an initializable node, irrespective of its element type.
            if (ConstructorEdges.IsIgnored(element)) return;
            // The provider's own registrations are tagged with the requester, even if the collection
            // registration was found in a parent. Explicit parent collection values are not merged.
            foreach (var registration in (IEnumerable<Registration>)collection.Provider)
                Add(result, seen, registration, requester, requester);
            var usesRegisteredScope = UsesRegisteredCollectionScope(collection);
            var finderType = typeof(IEnumerable<>).MakeGenericType(element);
            IObjectResolver? current = (requester as IScopedObjectResolver)?.Parent;
            while (current != null)
            {
                Registration? found;
                try
                {
                    if (!current.TryGetRegistration(finderType, out found)) found = null;
                }
                catch (Exception exception)
                {
                    Report(finderType, exception);
                    return; // construction reports this lookup failure; preserve the edges already found
                }
                if (found != null && IsCollection(found) && found.Provider is IEnumerable<Registration> registrations)
                {
                    foreach (var registration in registrations)
                    {
                        if (localScopeOnly && registration.Lifetime == Lifetime.Singleton) continue;
                        var resolver = usesRegisteredScope && registration.Lifetime == Lifetime.Singleton
                            ? current : requester;
                        Add(result, seen, registration, current, resolver);
                    }
                }
                current = (current as IScopedObjectResolver)?.Parent;
            }
        }

        private void Report(Type type, Exception exception)
        {
            _logger?.Debug($"[RuntimeFlow] {_name}: looking up {Fmt.Type(type)} for the graph edges threw " +
                           $"{exception.GetType().Name} ({exception.Message}); no edge. Resolving it fails the same way.");
        }
    }
}
