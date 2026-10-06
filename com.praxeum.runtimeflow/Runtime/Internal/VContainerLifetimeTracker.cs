using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using VContainer;

namespace RuntimeFlow.Internal
{
    /// <summary>Reads and transfers ownership of already-created, locally tracked VContainer instances.</summary>
    internal sealed class VContainerLifetimeTracker
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly List<Stack<IDisposable>> _trackers = new List<Stack<IDisposable>>();
        private readonly List<CreatedEntry> _created = new List<CreatedEntry>();
        private readonly List<object> _tracked = new List<object>();

        public readonly struct CreatedEntry
        {
            public CreatedEntry(Registration registration, object instance, IObjectResolver resolver)
            {
                Registration = registration;
                Instance = instance;
                Resolver = resolver;
            }

            public Registration Registration { get; }
            public object Instance { get; }
            public IObjectResolver Resolver { get; }
        }

        public IReadOnlyList<CreatedEntry> Created => _created;
        public IReadOnlyList<object> Tracked => _tracked;

        private VContainerLifetimeTracker() { }

        public static VContainerLifetimeTracker Capture(IObjectResolver scope, string name)
        {
            ValidateResolver(scope, name);
            var snapshot = new VContainerLifetimeTracker();
            snapshot.AddOwner(scope, name);
            if (scope is Container)
            {
                var rootScope = ReadField(scope, "rootScope", name) as IObjectResolver;
                if (!(rootScope is ScopedContainer))
                    throw Unsupported(name, "the root container's private rootScope is not a ScopedContainer");
                snapshot.AddOwner(rootScope, name);
            }
            return snapshot;
        }

        /// <summary>Checks actual tracker membership rather than inferring ownership from a cache or lifetime.</summary>
        public bool Contains(object instance)
        {
            foreach (var tracker in _trackers)
            {
                lock (tracker)
                {
                    foreach (var tracked in tracker)
                        if (ReferenceEquals(tracked, instance)) return true;
                }
            }
            return false;
        }

        /// <summary>Transfers one local tracker entry in native scope order without invoking instance code.</summary>
        public bool TryPop(out IDisposable? instance)
        {
            // A root container disposes its private scoped tracker before its singleton tracker.
            // Start over on every call so disposal callbacks that create scoped entries retain that priority.
            for (var i = _trackers.Count - 1; i >= 0; i--)
            {
                var tracker = _trackers[i];
                lock (tracker)
                {
                    if (tracker.Count == 0) continue;
                    instance = tracker.Pop();
                    return true;
                }
            }
            instance = null;
            return false;
        }

        /// <summary>Removes every matching local tracker entry without invoking disposal or other instance code.</summary>
        public bool Detach(object instance)
        {
            var removed = false;
            foreach (var tracker in _trackers)
            {
                lock (tracker)
                {
                    var found = false;
                    foreach (var entry in tracker)
                        if (ReferenceEquals(entry, instance)) { found = true; break; }
                    if (!found) continue;

                    var entries = tracker.ToArray(); // Stack snapshots run from top to bottom.
                    tracker.Clear();
                    for (var i = entries.Length - 1; i >= 0; i--)
                        if (!ReferenceEquals(entries[i], instance)) tracker.Push(entries[i]);
                    removed = true;
                }
            }
            return removed;
        }

        /// <summary>Enumerates local registration metadata, including overwritten collection members and keyed entries.</summary>
        public static IReadOnlyList<Registration> LocalRegistrations(IObjectResolver scope, string name)
        {
            ValidateResolver(scope, name);
            var registry = ReadField(scope, "registry", name);
            var hashTable = ReadField(registry, "hashTable", name);
            if (!(ReadField(hashTable, "table", name) is Array table) || table.Rank != 1)
                throw Unsupported(name, "the local registration table is not a one-dimensional bucket array");

            var result = new List<Registration>();
            var seen = new HashSet<Registration>(RegistrationIdentity.Instance);
            foreach (var bucketObject in table)
            {
                if (bucketObject == null) continue;
                if (!(bucketObject is Array bucket) || bucket.Rank != 1)
                    throw Unsupported(name, "the local registration table contains an unsupported bucket");
                foreach (var entry in bucket)
                {
                    var value = ReadNullableField(entry!, "Value", name);
                    // Null implementation guards are metadata for lookup, not registrations.
                    if (value == null) continue;
                    if (!(value is Registration registration))
                        throw Unsupported(name, "a local registration table entry has an unsupported Value");
                    AddRegistration(registration, result, seen, name);
                }
            }
            return result;
        }

        private void AddOwner(IObjectResolver owner, string name)
        {
            if (!(ReadField(owner, "sharedInstances", name) is ConcurrentDictionary<Registration, Lazy<object>> cache))
                throw Unsupported(name, "sharedInstances is not a ConcurrentDictionary<Registration, Lazy<object>>");
            var composite = ReadField(owner, "disposables", name);
            if (!(ReadField(composite, "disposables", name) is Stack<IDisposable> tracker))
                throw Unsupported(name, "the local disposal tracker is not a Stack<IDisposable>");

            _trackers.Add(tracker);
            foreach (var entry in cache)
            {
                // Reading Value is safe only after creation; capture must never spawn a service.
                if (entry.Value.IsValueCreated)
                {
                    var instance = entry.Value.Value;
                    // A successful nullable factory has no physical lifetime vertex.
                    if (instance != null) _created.Add(new CreatedEntry(entry.Key, instance, owner));
                }
            }
            lock (tracker)
            {
                foreach (var instance in tracker) _tracked.Add(instance);
            }
        }

        private static void AddRegistration(Registration registration, List<Registration> result,
            HashSet<Registration> seen, string name)
        {
            if (!seen.Add(registration)) return;
            if (registration.Provider?.GetType().Name == "CollectionInstanceProvider")
            {
                if (!(registration.Provider is IEnumerable<Registration> members))
                    throw Unsupported(name, "the collection provider cannot enumerate its registrations");
                foreach (var member in members)
                {
                    if (member == null)
                        throw Unsupported(name, "the collection provider contains a null registration");
                    AddRegistration(member, result, seen, name);
                }
            }
            else result.Add(registration);
        }

        private static void ValidateResolver(IObjectResolver scope, string name)
        {
            if (!(scope is Container) && !(scope is ScopedContainer))
                throw Unsupported(name, "the resolver must be a VContainer Container or ScopedContainer");
        }

        private static object ReadField(object source, string fieldName, string name)
            => ReadNullableField(source, fieldName, name)
               ?? throw Unsupported(name, $"{source.GetType().FullName}.{fieldName} is null");

        private static object? ReadNullableField(object source, string fieldName, string name)
        {
            if (source == null) throw Unsupported(name, $"cannot inspect {fieldName} on a null object");
            var field = source.GetType().GetField(fieldName, Fields);
            if (field == null)
                throw Unsupported(name, $"cannot inspect {source.GetType().FullName}.{fieldName}");
            return field.GetValue(source);
        }

        private static InitGraphException Unsupported(string name, string reason)
            => new InitGraphException(name, "Unsupported VContainer lifetime adapter: " + reason + ".");

        private sealed class RegistrationIdentity : IEqualityComparer<Registration>
        {
            public static readonly RegistrationIdentity Instance = new RegistrationIdentity();
            public bool Equals(Registration? x, Registration? y) => ReferenceEquals(x, y);
            public int GetHashCode(Registration obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
