using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using RuntimeFlow.Internal;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Components;
using RuntimeFlow.Tests.Support;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace RuntimeFlow.Tests.VContainerIntegration
{
    /// <summary>
    /// Discovery at the edges of what VContainer does: scene components in parent scopes, lattices of plain
    /// registrations, keyed injection (VContainer 1.19), registrations that throw while being looked up, and
    /// nodes VContainer never injects (instances and factories).
    /// </summary>
    [TestFixture]
    public sealed class GraphDiscoveryEdgeCaseTests
    {
        private readonly RunTracker _tracker = new RunTracker();
        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();
        private CapturingLogger _log = null!;
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp()
        {
            _log = new CapturingLogger();
            _options = TestScope.Options(_log);
        }

        [TearDown]
        public void TearDown()
        {
            _tracker.DisposeAll();
            foreach (var created in _objects)
            {
                if (created != null) UnityEngine.Object.DestroyImmediate(created);
            }
            _objects.Clear();
        }

        // ------------------------------------------------------------------ D2: RegisterComponentInHierarchy

        public sealed class UsesTheSceneComponent : AutoService
        {
            public UsesTheSceneComponent(SceneInitializable component) { }
        }

        /// <summary>
        /// A scene component below a parent object: <c>RegisterComponentInHierarchy</c> reads the scene of
        /// the builder's LifetimeScope, and <c>UnderTransform</c> makes the search independent of the
        /// editor's scene state in batch mode.
        /// </summary>
        private SceneInitializable SceneComponent(out LifetimeScope origin, out Transform parent)
        {
            var scopeObject = new GameObject("runtimeflow-test-scope");
            _objects.Add(scopeObject);
            origin = scopeObject.AddComponent<LifetimeScope>();
            var parentObject = new GameObject("runtimeflow-test-parent");
            _objects.Add(parentObject);
            parent = parentObject.transform;
            var componentObject = new GameObject("runtimeflow-test-scene-component");
            componentObject.transform.SetParent(parent);
            return componentObject.AddComponent<SceneInitializable>();
        }

        [Test]
        [Timeout(10000)]
        public async Task ASceneComponentIsInitializedOnceInTheScopeThatRegistersIt()
        {
            var component = SceneComponent(out var origin, out var parent);
            var container = _tracker.Build(builder =>
            {
                builder.ApplicationOrigin = origin;
                builder.RegisterComponentInHierarchy<SceneInitializable>().UnderTransform(parent).As<IAsyncInitializable>().AsSelf();
            });

            var result = await _tracker.Create(container, "global", _options).RunAsync();

            Assert.That(result.Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(component.Initializations, Is.EqualTo(1));
            Assert.That(component.Injections, Is.EqualTo(1));
        }

        /// <summary>
        /// Upstream VContainer 1.15.3 registers a hierarchy component as Lifetime.Scoped, so a child scope
        /// resolving it creates it anew — finds the same scene component, injects it again and disposes it
        /// with the child. That is a graph error. The 1.19 fork registers it as a singleton, which the child
        /// resolves from the parent like any other: nothing to refuse there.
        /// </summary>
        [Test]
        [Timeout(10000)]
        public async Task AChildScopeDependingOnAScopedSceneComponentOfItsParentIsAGraphError()
        {
            var component = SceneComponent(out var origin, out var parent);
            var probe = new ContainerBuilder { ApplicationOrigin = origin };
            var scoped = probe.RegisterComponentInHierarchy<SceneInitializable>().UnderTransform(parent).Build().Lifetime == Lifetime.Scoped;

            await using var host = new RuntimeFlowHost(
                builder =>
                {
                    builder.ApplicationOrigin = origin;
                    builder.RegisterComponentInHierarchy<SceneInitializable>().UnderTransform(parent).As<IAsyncInitializable>().AsSelf();
                },
                builder => builder.Add<UsesTheSceneComponent>(),
                _options);

            if (scoped)
            {
                var failure = await AsyncTestAssert.ThrowsAsync<InitGraphException>(() => host.StartAsync());

                Assert.That(failure.Message, Does.Contain("UsesTheSceneComponent"), failure.Message);
                Assert.That(failure.Message, Does.Contain("RegisterComponentInHierarchy"), failure.Message);
                Assert.That(failure.Message, Does.Contain("RegisterComponent("), failure.Message);
            }
            else
            {
                Assert.That((await host.StartAsync()).Outcome, Is.EqualTo(StartupOutcome.Completed), _log.Dump());
            }
            Assert.That(component.Injections, Is.EqualTo(1),
                "the session re-created the parent's registration and re-injected the scene component");
            Assert.That(component.Initializations, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ D3: diamond lattices

        public sealed class LatticeLeaf : AutoService { }

        /// <summary>
        /// Emits <paramref name="layers"/> × <paramref name="width"/> plain classes; every class of layer k
        /// takes all classes of layer k+1, the last layer takes <see cref="LatticeLeaf"/>; the root takes
        /// layer 0 and is initializable. Every path from the root reaches the leaf: width^layers of them.
        /// </summary>
        private static (Type Root, Type[][] Layers) Lattice(int layers, int width)
        {
            var name = new AssemblyName("RuntimeFlow.Tests.Lattice" + Guid.NewGuid().ToString("N"));
            var module = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run).DefineDynamicModule(name.Name);
            var result = new Type[layers][];
            var below = new[] { typeof(LatticeLeaf) };
            for (var layer = layers - 1; layer >= 0; layer--)
            {
                result[layer] = new Type[width];
                for (var i = 0; i < width; i++)
                    result[layer][i] = Emit(module, $"L{layer}_{i}", typeof(object), below);
                below = result[layer];
            }
            var root = Emit(module, "LatticeRoot", typeof(AutoService), result[0]);
            return (root, result);
        }

        private static Type Emit(ModuleBuilder module, string name, Type baseType, Type[] parameters)
        {
            var type = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, baseType);
            var baseCtor = baseType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!;
            var il = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, parameters).GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, baseCtor);
            il.Emit(OpCodes.Ret);
            return type.CreateTypeInfo()!.AsType();
        }

        private static void RegisterLattice(IContainerBuilder builder, (Type Root, Type[][] Layers) lattice)
        {
            builder.Register<LatticeLeaf>(Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
            foreach (var layer in lattice.Layers)
            foreach (var type in layer)
                builder.Register(type, Lifetime.Singleton);
            builder.Register(lattice.Root, Lifetime.Singleton).AsSelf().As(typeof(IAsyncInitializable));
        }

        [Test]
        public void AWalkThroughADiamondYieldsOneEntryPerTargetNode()
        {
            var lattice = Lattice(3, 4);
            var container = _tracker.Build(builder => RegisterLattice(builder, lattice));
            Assert.That(container.TryGetRegistration(typeof(LatticeLeaf), out var registration), Is.True);
            var leaf = new ServiceNode(0, NodeKind.Service, nameof(LatticeLeaf), typeof(LatticeLeaf), "session") { Registration = registration };

            var edges = new InjectionEdges(container, new[] { leaf });
            var targets = edges.Targets(lattice.Layers[0][0], container, null);

            Assert.That(targets.Select(t => t.Target), Is.EqualTo(new[] { leaf }),
                $"one entry per reachable node, not one per path ({targets.Count} entries)");
            Assert.That(targets[0].Via, Is.EqualTo("L0_0 > L1_0 > L2_0"));
        }

        /// <summary>
        /// 12 layers × 4 is 4^12 paths from the root to the leaf. VContainer's own circular-dependency check
        /// walks every path of a container's registrations, so the lattice spreads its layers over nested
        /// scopes (each scope's check stops at its parent): what is measured is the graph builder's walk,
        /// which crosses all of them.
        /// </summary>
        [Test]
        [Timeout(60000)]
        public async Task ADeepDiamondLatticeBuildsQuicklyWithTheRightEdges()
        {
            const int layers = 12;
            var lattice = Lattice(layers, 4);
            IObjectResolver scope = _tracker.Build(builder =>
            {
                builder.Register<LatticeLeaf>(Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                foreach (var type in lattice.Layers[layers - 1]) builder.Register(type, Lifetime.Singleton);
            });
            for (var layer = layers - 2; layer >= 0; layer--)
            {
                var current = layer;
                scope = _tracker.Track(scope.CreateScope(builder =>
                {
                    foreach (var type in lattice.Layers[current]) builder.Register(type, Lifetime.Singleton);
                    if (current == 0) builder.Register(lattice.Root, Lifetime.Singleton).AsSelf().As(typeof(IAsyncInitializable));
                }));
            }

            var clock = Stopwatch.StartNew();
            var run = _tracker.Create(scope, "session", _options);
            clock.Stop();

            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "graph building is exponential in the number of paths");
            var root = run.GetStatus().Service("LatticeRoot");
            Assert.That(root.Dependencies, Is.EqualTo(new[] { nameof(LatticeLeaf) }));
            Assert.That(run.Describe(), Does.Contain("via L0_0 > L1_0 > L2_0"), run.Describe());
            Assert.That((await run.RunAsync()).Outcome, Is.EqualTo(StartupOutcome.Completed));
        }

        // ------------------------------------------------------------------ D4: keyed injection (VContainer 1.19)

        public interface IKeyedDependency { }

        public sealed class KeyedA : AutoService, IKeyedDependency { }

        public sealed class KeyedB : AutoService, IKeyedDependency { }

        [Test]
        public void AKeyedParameterDependsOnTheRegistrationWithThatKey()
        {
            var keyAttribute = typeof(IObjectResolver).Assembly.GetType("VContainer.KeyAttribute");
            var keyed = typeof(RegistrationBuilder).GetMethod("Keyed", new[] { typeof(object) });
            if (keyAttribute == null || keyed == null)
            {
                Assert.Ignore("This VContainer has no keyed registrations (upstream 1.15.3); the 1.19 fork does.");
                return;
            }

            // KeyedConsumer(IKeyedDependency [Key("b")] dependency) : AutoService
            var name = new AssemblyName("RuntimeFlow.Tests.Keyed" + Guid.NewGuid().ToString("N"));
            var module = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run).DefineDynamicModule(name.Name);
            var type = module.DefineType("KeyedConsumer", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(AutoService));
            var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(IKeyedDependency) });
            ctor.DefineParameter(1, ParameterAttributes.None, "dependency")
                .SetCustomAttribute(new CustomAttributeBuilder(keyAttribute.GetConstructor(new[] { typeof(object) })!, new object[] { "b" }));
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(AutoService).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!);
            il.Emit(OpCodes.Ret);
            var consumer = type.CreateTypeInfo()!.AsType();

            var container = _tracker.Build(builder =>
            {
                keyed.Invoke(builder.Register<KeyedA>(Lifetime.Singleton).As<IKeyedDependency>().As<IAsyncInitializable>(), new object[] { "a" });
                keyed.Invoke(builder.Register<KeyedB>(Lifetime.Singleton).As<IKeyedDependency>().As<IAsyncInitializable>(), new object[] { "b" });
                builder.Register(consumer, Lifetime.Singleton).As(typeof(IAsyncInitializable));
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(run.GetStatus().Service("KeyedConsumer").Dependencies, Is.EqualTo(new[] { nameof(KeyedB) }), run.Describe());
        }

        // ------------------------------------------------------------------ D5: lookups that throw

        public interface IRepository<T> { }

        public sealed class Repository<T> : IRepository<T> where T : class { }

        public sealed class UsesAnImpossibleRepository : AutoService
        {
            public UsesAnImpossibleRepository(IRepository<int> repository) { }
        }

        [Test]
        [Timeout(10000)]
        public async Task ALookupThatThrowsDuringEdgeDiscoveryLeavesTheConstructionErrorInCharge()
        {
            // In one container VContainer's own build-time check trips over the lookup first; below a parent
            // that registers the open generic, only the graph builder's walk up the scopes reaches it.
            var parent = _tracker.Build(builder => builder.Register(typeof(Repository<>), Lifetime.Singleton).As(typeof(IRepository<>)));
            var container = _tracker.Track(parent.CreateScope(builder => builder.Add<UsesAnImpossibleRepository>()));

            ScopeRun? run = null;
            Assert.DoesNotThrow(() => run = _tracker.Create(container, "session", _options),
                "an exception of the edge lookup escaped ScopeRun.Create");
            var failure = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => run!.RunAsync());

            Assert.That(failure.InnerException, Is.TypeOf<InitGraphException>());
            Assert.That(failure.InnerException!.Message, Does.StartWith("Could not construct UsesAnImpossibleRepository"));
        }

        // ------------------------------------------------------------------ D6: nodes VContainer never injects

        public sealed class InstanceWithAnInjectField : AutoService
        {
            [Inject] public NeedsTheInstance? Later;
        }

        public sealed class NeedsTheInstance : AutoService
        {
            public NeedsTheInstance(InstanceWithAnInjectField instance) { }
        }

        [Test]
        [Timeout(10000)]
        public async Task AnInstanceRegistrationGetsNoEdgesFromInjectMembersVContainerNeverInjects()
        {
            var container = _tracker.Build(builder =>
            {
                builder.RegisterInstance(new InstanceWithAnInjectField()).AsSelf().As<IAsyncInitializable>();
                builder.Add<NeedsTheInstance>();
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(run.GetStatus().Service(nameof(InstanceWithAnInjectField)).Dependencies, Is.Empty, run.Describe());
            Assert.That((await run.RunAsync()).Outcome, Is.EqualTo(StartupOutcome.Completed));
        }

        [Test]
        [Timeout(10000)]
        public async Task AFactoryRegistrationGetsNoEdgesFromInjectMembersVContainerNeverInjects()
        {
            var container = _tracker.Build(builder =>
            {
                builder.Register(_ => new InstanceWithAnInjectField(), Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
                builder.Add<NeedsTheInstance>();
            });

            var run = _tracker.Create(container, "session", _options);

            Assert.That(run.GetStatus().Service(nameof(InstanceWithAnInjectField)).Dependencies, Is.Empty, run.Describe());
            Assert.That((await run.RunAsync()).Outcome, Is.EqualTo(StartupOutcome.Completed));
        }
    }
}
