using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuntimeFlow.Internal;
using NUnit.Framework;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Components;
using RuntimeFlow.Tests.Support;
using UnityEngine;
using VContainer;
using VContainer.Internal;
using VContainer.Unity;

namespace RuntimeFlow.Tests.VContainerIntegration
{
    [TestFixture]
    public sealed class HierarchySubtypeGraphTests
    {
        private readonly RunTracker _tracker = new RunTracker();
        private readonly List<GameObject> _objects = new List<GameObject>();

        public sealed class HelperConsumer : AutoService
        {
            public HelperConsumer(IGraphSubtypeHelper helper) => Helper = helper;
            public IGraphSubtypeHelper Helper { get; }
        }

        [TearDown]
        public void TearDown()
        {
            _tracker.DisposeAll();
            foreach (var created in _objects)
                if (created != null) UnityEngine.Object.DestroyImmediate(created);
            _objects.Clear();
        }

        private GraphSubtypeComponent Component(out LifetimeScope origin, out Transform parent)
        {
            var originObject = new GameObject("graph-subtype-origin");
            _objects.Add(originObject);
            origin = originObject.AddComponent<LifetimeScope>();
            var componentObject = new GameObject("graph-subtype-derived");
            _objects.Add(componentObject);
            parent = componentObject.transform;
            return componentObject.AddComponent<GraphSubtypeComponent>();
        }

        private static void Dependencies(IContainerBuilder builder)
        {
            builder.Register<GraphBaseDependency>(Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
            builder.Register<GraphDerivedDependency>(Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
            builder.Register<GraphInheritedDependency>(Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
            builder.Register<GraphDerivedOnlyDependency>(Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
        }

        [Test]
        public void AHierarchySubtypeUsesOnlyItsRuntimeInjectorMembers()
        {
            var component = Component(out var origin, out var parent);
            var container = _tracker.Build(builder =>
            {
                builder.ApplicationOrigin = origin;
                Dependencies(builder);
                builder.RegisterComponentInHierarchy<GraphBaseComponent>()
                    .UnderTransform(parent).AsSelf().As<IAsyncInitializable>();
            });
            Assert.That(component.Dependency, Is.SameAs(container.Resolve<GraphDerivedDependency>()));
            Assert.That(((GraphBaseComponent)component).Dependency, Is.Null,
                "VContainer's runtime subtype injector skips the hidden base property.");
            Assert.That(component.InheritedDependency, Is.SameAs(container.Resolve<GraphInheritedDependency>()));
            Assert.That(component.DerivedOnlyDependency, Is.SameAs(container.Resolve<GraphDerivedOnlyDependency>()));

            var run = _tracker.Create(container, "subtype", TestScope.Options(new CapturingLogger()));

            Assert.That(run.GetStatus().Service(nameof(GraphSubtypeComponent)).Dependencies,
                Is.EquivalentTo(new[] { nameof(GraphDerivedDependency), nameof(GraphInheritedDependency), nameof(GraphDerivedOnlyDependency) }),
                run.Describe());
        }

        [Test]
        public void APlainHierarchySubtypeWalkUsesItsRuntimeInjectorMembers()
        {
            var component = Component(out var origin, out var parent);
            var container = _tracker.Build(builder =>
            {
                builder.ApplicationOrigin = origin;
                Dependencies(builder);
                builder.RegisterComponentInHierarchy<GraphBaseComponent>()
                    .UnderTransform(parent).As<IGraphSubtypeHelper>();
                builder.Add<HelperConsumer>();
            });
            Assert.That(container.Resolve<HelperConsumer>().Helper, Is.SameAs(component));
            Assert.That(component.Dependency, Is.SameAs(container.Resolve<GraphDerivedDependency>()));
            Assert.That(((GraphBaseComponent)component).Dependency, Is.Null);
            Assert.That(component.InheritedDependency, Is.SameAs(container.Resolve<GraphInheritedDependency>()));

            var run = _tracker.Create(container, "subtype", TestScope.Options(new CapturingLogger()));

            Assert.That(run.GetStatus().Service(nameof(HelperConsumer)).Dependencies,
                Is.EquivalentTo(new[] { nameof(GraphDerivedDependency), nameof(GraphInheritedDependency), nameof(GraphDerivedOnlyDependency) }), run.Describe());
        }

        private sealed class ExistingBaseInjectorBuilder : RegistrationBuilder
        {
            private readonly GraphSubtypeComponent _component;

            public ExistingBaseInjectorBuilder(GraphSubtypeComponent component)
                : base(typeof(GraphBaseComponent), Lifetime.Singleton) => _component = component;

            public override Registration Build()
            {
                var providerType = typeof(RegistrationBuilder).Assembly.GetType("VContainer.Unity.ExistingComponentProvider")!;
                var provider = (IInstanceProvider)Activator.CreateInstance(providerType,
                    new object?[] { _component, InjectorCache.GetOrBuild(ImplementationType), null, false })!;
                return new Registration(ImplementationType, Lifetime, InterfaceTypes, provider);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AnExistingComponentUsesItsBoundBaseInjectorRatherThanItsRuntimeSubtype(bool throughPlainHelper)
        {
            var component = Component(out _, out _);
            var container = _tracker.Build(builder =>
            {
                Dependencies(builder);
                var registration = builder.Register(new ExistingBaseInjectorBuilder(component));
                if (throughPlainHelper)
                {
                    registration.As<IGraphSubtypeHelper>();
                    builder.Add<HelperConsumer>();
                }
                else registration.As<IAsyncInitializable>();
            });
            var run = _tracker.Create(container, "subtype", TestScope.Options(new CapturingLogger()));

            Assert.That(((GraphBaseComponent)component).Dependency, Is.SameAs(container.Resolve<GraphBaseDependency>()));
            Assert.That(component.InheritedDependency, Is.SameAs(container.Resolve<GraphInheritedDependency>()));
            Assert.That(component.Dependency, Is.Null);
            Assert.That(component.DerivedOnlyDependency, Is.Null,
                "ExistingComponentProvider injects the bound implementation type, unlike FindComponentProvider.");
            var service = throughPlainHelper ? nameof(HelperConsumer) : nameof(GraphSubtypeComponent);
            Assert.That(run.GetStatus().Service(service).Dependencies,
                Is.EquivalentTo(new[] { nameof(GraphBaseDependency), nameof(GraphInheritedDependency) }), run.Describe());
        }

        private sealed class FixedRegistrationBuilder : RegistrationBuilder
        {
            private readonly Registration _registration;
            public FixedRegistrationBuilder(Registration registration)
                : base(registration.ImplementationType, registration.Lifetime) => _registration = registration;
            public override Registration Build() => _registration;
        }

        [Test]
        public void InspectingAnUncreatedHierarchyComponentDoesNotInjectIt()
        {
            var component = Component(out var origin, out var parent);
            Registration? hierarchy = null;
            var container = _tracker.Build(builder =>
            {
                Dependencies(builder);
                var providerBuilder = new ContainerBuilder { ApplicationOrigin = origin };
                hierarchy = providerBuilder.RegisterComponentInHierarchy<GraphBaseComponent>()
                    .UnderTransform(parent).As<IGraphSubtypeHelper>().Build();
                // Register the provider without VContainer's eager hierarchy build callback.
                builder.Register(new FixedRegistrationBuilder(hierarchy));
                builder.Add<HelperConsumer>();
            });
            var edges = new InjectionEdges(container, Array.Empty<ServiceNode>());

            Assert.That(edges.ReflectedType(hierarchy!, container), Is.EqualTo(typeof(GraphBaseComponent)));
            Assert.That(component.Dependency, Is.Null);
            Assert.That(component.InheritedDependency, Is.Null,
                "Reading the cache for preflight must not resolve or inject an uncreated hierarchy provider.");

            var run = _tracker.Create(container, "subtype", TestScope.Options(new CapturingLogger()));
            Assert.That(component.Dependency, Is.SameAs(container.Resolve<GraphDerivedDependency>()));
            Assert.That(run.GetStatus().Service(nameof(HelperConsumer)).Dependencies,
                Is.EquivalentTo(new[] { nameof(GraphDerivedDependency), nameof(GraphInheritedDependency), nameof(GraphDerivedOnlyDependency) }), run.Describe());
        }

        public sealed class ConstructorComponent : AutoService
        {
            public ConstructorComponent(GraphBaseDependency dependency) { }
        }

        [Test]
        public void AnExistingComponentDoesNotGetEdgesFromItsAlreadyExecutedConstructor()
        {
            var component = new ConstructorComponent(new GraphBaseDependency());
            var container = _tracker.Build(builder =>
            {
                Dependencies(builder);
                builder.RegisterComponent(component).As<IAsyncInitializable>();
            });
            var run = _tracker.Create(container, "subtype", TestScope.Options(new CapturingLogger()));

            Assert.That(run.GetStatus().Service(nameof(ConstructorComponent)).Dependencies, Is.Empty, run.Describe());
        }

        public sealed class AmbiguousConstructorComponent : AutoService
        {
            public AmbiguousConstructorComponent(GraphBaseDependency dependency) => SuppliedDependency = dependency;
            public AmbiguousConstructorComponent(GraphDerivedDependency dependency) => SuppliedDependency = dependency;
            public object SuppliedDependency { get; }
        }

        [Test]
        public void AnExistingComponentDoesNotWarnAboutConstructorsVContainerNeverCalls()
        {
            var supplied = new GraphBaseDependency();
            var component = new AmbiguousConstructorComponent(supplied);
            var log = new CapturingLogger();
            var container = _tracker.Build(builder =>
            {
                Dependencies(builder);
                builder.RegisterComponent(component).As<IAsyncInitializable>();
            });
            Assert.That(ConstructorEdges.Ambiguity(typeof(AmbiguousConstructorComponent)), Is.Not.Null,
                "The preconstructed component has two equal-arity constructors, but its provider only injects members.");

            var run = _tracker.Create(container, "subtype", TestScope.Options(log));

            Assert.That(component.SuppliedDependency, Is.SameAs(supplied));
            Assert.That(run.GetStatus().Service(nameof(AmbiguousConstructorComponent)).Dependencies, Is.Empty, run.Describe());
            Assert.That(log.Has(Microsoft.Extensions.Logging.LogLevel.Warning,
                nameof(AmbiguousConstructorComponent)), Is.False, log.Dump());
        }

        [Init(Optional = true)]
        public abstract class OptionalAbstractFactory : IAsyncInitializable
        {
            public abstract Task InitializeAsync(InitContext context, CancellationToken token);
        }

        public abstract class RequiredAbstractFactory : IAsyncInitializable
        {
            public abstract Task InitializeAsync(InitContext context, CancellationToken token);
        }

        public sealed class MissingDependency : AutoService { }

        [DependsOn(typeof(MissingDependency))]
        public sealed class MissingDependencyConsumer : AutoService { }

        [Test]
        public void AnOptionalAbstractFactoryFailureCannotExcuseAnUnrelatedMissingDependsOnTarget()
        {
            var container = _tracker.Build(builder =>
            {
                builder.Register<OptionalAbstractFactory>(_ => throw new InvalidOperationException("optional factory failed"),
                    Lifetime.Singleton).As<IAsyncInitializable>();
                builder.Add<MissingDependencyConsumer>();
            });

            var error = Assert.Throws<InitGraphException>(() =>
                _tracker.Create(container, "subtype", TestScope.Options(new CapturingLogger())));

            Assert.That(error!.Message, Does.Contain(nameof(MissingDependencyConsumer)).And.Contain(nameof(MissingDependency)));
            Assert.That(error.Message, Does.Contain("no initializable service"));
            Assert.That(container.Resolve<MissingDependencyConsumer>().Attempts, Is.Zero);
        }

        [Test]
        public async Task ARequiredAbstractFactoryFailureRemainsThePrimaryFailure()
        {
            var container = _tracker.Build(builder =>
            {
                builder.Register<RequiredAbstractFactory>(_ => throw new InvalidOperationException("required factory failed"),
                    Lifetime.Singleton).As<IAsyncInitializable>();
                builder.Add<MissingDependencyConsumer>();
            });
            var run = _tracker.Create(container, "subtype", TestScope.Options(new CapturingLogger()));

            var error = await AsyncTestAssert.ThrowsAsync<RuntimeFlowException>(() => run.RunAsync());

            Assert.That(error.InnerException!.Message, Does.Contain("required factory failed"));
            Assert.That(container.Resolve<MissingDependencyConsumer>().Attempts, Is.Zero);
        }

        private UnsafeHierarchySubtypeComponent UnsafeComponent(out LifetimeScope origin, out Transform helperParent,
            out SceneInitializable parentComponent, out Transform initializerParent)
        {
            var originObject = new GameObject("unsafe-subtype-origin");
            _objects.Add(originObject);
            origin = originObject.AddComponent<LifetimeScope>();
            var helperObject = new GameObject("unsafe-subtype-helper");
            _objects.Add(helperObject);
            helperParent = helperObject.transform;
            var helper = helperObject.AddComponent<UnsafeHierarchySubtypeComponent>();
            var initializerObject = new GameObject("unsafe-subtype-parent-initializer");
            _objects.Add(initializerObject);
            initializerParent = initializerObject.transform;
            parentComponent = initializerObject.AddComponent<SceneInitializable>();
            return helper;
        }

        private static void RegisterUncreatedHelper(IContainerBuilder builder, LifetimeScope origin, Transform parent)
        {
            var donor = new ContainerBuilder { ApplicationOrigin = origin };
            var registration = donor.RegisterComponentInHierarchy<UnsafeHierarchyBaseComponent>()
                .UnderTransform(parent).As<IGraphSubtypeHelper>().Build();
            builder.Register(new FixedRegistrationBuilder(registration));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AnUnknownHierarchySubtypeIsRefusedBeforeItCanMutateAnUnsafeParentIdentity(bool guardParentImplementation)
        {
            var helper = UnsafeComponent(out var origin, out var helperParent, out var parentComponent, out var initializerParent);
            var parent = _tracker.Build(builder =>
            {
                builder.ApplicationOrigin = origin;
                builder.RegisterComponentInHierarchy<SceneInitializable>().UnderTransform(initializerParent)
                    .AsSelf().As<IAsyncInitializable>();
            });
            var parentRun = _tracker.Create(parent, "parent", TestScope.Options(new CapturingLogger()));
            await parentRun.RunAsync();
            Assert.That(parent.TryGetRegistration(typeof(SceneInitializable), out var parentRegistration), Is.True);
            var unsafeIdentity = parentRegistration.Lifetime == Lifetime.Scoped || guardParentImplementation;
            var child = _tracker.Track(parent.CreateScope(builder =>
            {
                RegisterUncreatedHelper(builder, origin, helperParent);
                if (guardParentImplementation) builder.Register<SceneInitializable>(Lifetime.Singleton).AsSelf();
                builder.Add<HelperConsumer>();
            }));
            Assert.That(helper.ParentInitializer, Is.Null);

            if (unsafeIdentity)
            {
                var error = Assert.Throws<InitGraphException>(() =>
                    _tracker.Create(child, "child", TestScope.Options(new CapturingLogger()), new[] { parentRun }));
                Assert.That(error!.Message, Does.Contain("runtime subtype is unknown").And.Contain("will not resolve"));
                Assert.That(helper.ParentInitializer, Is.Null,
                    "Preflight must reject the unknown subtype before running its injector.");
            }
            else
            {
                var run = _tracker.Create(child, "child", TestScope.Options(new CapturingLogger()), new[] { parentRun });
                Assert.That(helper.ParentInitializer, Is.SameAs(parentComponent));
                Assert.That(run.GetStatus().Service(nameof(HelperConsumer)).Dependencies,
                    Is.EqualTo(new[] { nameof(SceneInitializable) }), run.Describe());
            }
            Assert.That(parentComponent.Injections, Is.EqualTo(1));
            Assert.That(parentComponent.Initializations, Is.EqualTo(1));
        }

        [Test]
        public async Task AssemblyRejectsAParentSceneReinjectionThatAnOpaqueFactoryAlreadyPerformed()
        {
            var helper = UnsafeComponent(out var origin, out var helperParent, out var parentComponent, out var initializerParent);
            var parent = _tracker.Build(builder =>
            {
                builder.ApplicationOrigin = origin;
                builder.RegisterComponentInHierarchy<SceneInitializable>().UnderTransform(initializerParent)
                    .AsSelf().As<IAsyncInitializable>();
            });
            Assert.That(parent.TryGetRegistration(typeof(SceneInitializable), out var registration), Is.True);
            if (registration.Lifetime != Lifetime.Scoped)
            {
                Assert.Ignore("This VContainer keeps inherited hierarchy components as Singletons; no Scoped reinjection occurs.");
                return;
            }
            var parentRun = _tracker.Create(parent, "parent", TestScope.Options(new CapturingLogger()));
            await parentRun.RunAsync();
            var child = _tracker.Track(parent.CreateScope(builder =>
            {
                RegisterUncreatedHelper(builder, origin, helperParent);
                builder.Register<HelperConsumer>(resolver => new HelperConsumer(resolver.Resolve<IGraphSubtypeHelper>()),
                    Lifetime.Singleton).As<IAsyncInitializable>();
            }));

            var error = Assert.Throws<InitGraphException>(() =>
                _tracker.Create(child, "child", TestScope.Options(new CapturingLogger()), new[] { parentRun }));

            Assert.That(error!.Message, Does.Contain(nameof(SceneInitializable)).And.Contain("initialized parent identity"));
            Assert.That(helper.ParentInitializer, Is.SameAs(parentComponent));
            Assert.That(parentComponent.Injections, Is.EqualTo(2),
                "An opaque factory ran before graph extraction; this defense rejects its result without claiming to prevent its mutation.");
        }

        public sealed class CapturedSceneWrapper : AutoService
        {
            public CapturedSceneWrapper(SceneInitializable component) => Component = component;
            public SceneInitializable Component { get; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ACapturedParentSceneComponentDoesNotInventChildReinjection(bool closureFactory)
        {
            UnsafeComponent(out var origin, out _, out var parentComponent, out var initializerParent);
            var parent = _tracker.Build(builder =>
            {
                builder.ApplicationOrigin = origin;
                builder.RegisterComponentInHierarchy<SceneInitializable>().UnderTransform(initializerParent)
                    .AsSelf().As<IAsyncInitializable>();
            });
            var options = TestScope.Options(new CapturingLogger());
            var parentRun = _tracker.Create(parent, "parent", options);
            await parentRun.RunAsync();
            var captured = parent.Resolve<SceneInitializable>();
            Assert.That(captured, Is.SameAs(parentComponent));
            var child = _tracker.Track(parent.CreateScope(builder =>
            {
                if (closureFactory)
                    builder.Register<CapturedSceneWrapper>(_ => new CapturedSceneWrapper(captured), Lifetime.Singleton)
                        .AsSelf().As<IAsyncInitializable>();
                else
                    builder.RegisterInstance(new CapturedSceneWrapper(captured)).AsSelf().As<IAsyncInitializable>();
            }));

            var run = _tracker.Create(child, "child", options, new[] { parentRun });
            await run.RunAsync();

            Assert.That(child.Resolve<CapturedSceneWrapper>().Component, Is.SameAs(parentComponent));
            Assert.That(run.GetStatus().Service(nameof(CapturedSceneWrapper)).Dependencies,
                Is.EqualTo(new[] { nameof(SceneInitializable) }), run.Describe());
            Assert.That(parentComponent.Injections, Is.EqualTo(1),
                "A captured parent value does not create a child Scoped cache entry or re-inject the parent component.");
            Assert.That(parentComponent.Initializations, Is.EqualTo(1));
        }

        public interface ICapturedParentService { }
        public interface IChildImplementationGuard { }

        public sealed class CapturedParentService : AutoService, ICapturedParentService, IChildImplementationGuard { }

        public sealed class CapturedSingletonWrapper : AutoService
        {
            public CapturedSingletonWrapper(ICapturedParentService service) => Service = service;
            public ICapturedParentService Service { get; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ACapturedParentSingletonDoesNotInventAChildCloneDespiteAnImplementationGuard(bool closureFactory)
        {
            var parent = _tracker.Build(builder => builder.Register<CapturedParentService>(Lifetime.Singleton)
                .As<ICapturedParentService>().As<IAsyncInitializable>());
            var options = TestScope.Options(new CapturingLogger());
            var parentRun = _tracker.Create(parent, "parent", options);
            await parentRun.RunAsync();
            var captured = parent.Resolve<ICapturedParentService>();
            var child = _tracker.Track(parent.CreateScope(builder =>
            {
                builder.Register<CapturedParentService>(Lifetime.Singleton).As<IChildImplementationGuard>();
                if (closureFactory)
                    builder.Register<CapturedSingletonWrapper>(_ => new CapturedSingletonWrapper(captured), Lifetime.Singleton)
                        .AsSelf().As<IAsyncInitializable>();
                else
                    builder.RegisterInstance(new CapturedSingletonWrapper(captured)).AsSelf().As<IAsyncInitializable>();
            }));

            var run = _tracker.Create(child, "child", options, new[] { parentRun });
            await run.RunAsync();

            Assert.That(child.Resolve<CapturedSingletonWrapper>().Service, Is.SameAs(captured));
            Assert.That(run.GetStatus().Service(nameof(CapturedSingletonWrapper)).Dependencies,
                Is.EqualTo(new[] { nameof(CapturedParentService) }), run.Describe());
            Assert.That(((CapturedParentService)captured).Attempts, Is.EqualTo(1));
        }

        [Test]
        public async Task AssemblyRejectsAParentSingletonCloneAnOpaqueFactoryActuallyResolvedThroughTheChild()
        {
            var parent = _tracker.Build(builder => builder.Register<CapturedParentService>(Lifetime.Singleton)
                .As<ICapturedParentService>().As<IAsyncInitializable>());
            var options = TestScope.Options(new CapturingLogger());
            var parentRun = _tracker.Create(parent, "parent", options);
            await parentRun.RunAsync();
            var original = parent.Resolve<ICapturedParentService>();
            var child = _tracker.Track(parent.CreateScope(builder =>
            {
                builder.Register<CapturedParentService>(Lifetime.Singleton).As<IChildImplementationGuard>();
                builder.Register<CapturedSingletonWrapper>(resolver =>
                        new CapturedSingletonWrapper(resolver.Resolve<ICapturedParentService>()), Lifetime.Singleton)
                    .AsSelf().As<IAsyncInitializable>();
            }));

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(child, "child", options, new[] { parentRun }));

            Assert.That(error!.Message, Does.Contain("resolves a new instance of parent service").And.Contain(nameof(CapturedParentService)));
            var clone = child.Resolve<ICapturedParentService>();
            Assert.That(clone, Is.Not.SameAs(original));
            Assert.That(((CapturedParentService)clone).Attempts, Is.Zero);
            Assert.That(((CapturedParentService)original).Attempts, Is.EqualTo(1));
            Assert.That(child.Resolve<CapturedSingletonWrapper>().Attempts, Is.Zero);
        }

        public sealed class FaultingComponentWrapper : AutoService
        {
            public FaultingComponentWrapper(IParentFaultingComponent component) => Component = component;
            public IParentFaultingComponent Component { get; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AFaultedParentComponentInjectionAttemptCannotBeHiddenByACapturingFactory(bool hierarchy)
        {
            var originObject = new GameObject("faulting-parent-origin");
            _objects.Add(originObject);
            var origin = originObject.AddComponent<LifetimeScope>();
            var componentObject = new GameObject("faulting-parent-component");
            _objects.Add(componentObject);
            var component = componentObject.AddComponent<FaultingParentComponent>();
            var parent = _tracker.Build(builder =>
            {
                builder.ApplicationOrigin = origin;
                builder.RegisterInstance<IFaultingComponentAuth>(new ParentComponentAuth());
                if (hierarchy)
                    builder.RegisterComponentInHierarchy<FaultingParentComponent>().UnderTransform(componentObject.transform)
                        .As<IParentFaultingComponent>().As<IAsyncInitializable>();
                else
                    builder.RegisterComponent<IParentFaultingComponent>(component).As<IAsyncInitializable>();
            });
            var options = TestScope.Options(new CapturingLogger());
            var parentRun = _tracker.Create(parent, "parent", options);
            await parentRun.RunAsync();
            Assert.That(component.Injections, Is.EqualTo(1));
            var childAuth = new ChildComponentAuth();
            Exception? injectionError = null;
            var child = _tracker.Track(parent.CreateScope(builder =>
            {
                builder.RegisterInstance<IFaultingComponentAuth>(childAuth);
                builder.Register<FaultingParentComponent>(Lifetime.Singleton).As<IChildFaultingComponent>();
                builder.Register<FaultingComponentWrapper>(resolver =>
                {
                    try { resolver.Resolve<IParentFaultingComponent>(); }
                    catch (Exception error) { injectionError = error; }
                    return new FaultingComponentWrapper(component);
                }, Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
            }));

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(child, "child", options, new[] { parentRun }));

            Assert.That(error!.Message, Does.Contain(nameof(FaultingParentComponent)).And.Contain("parent"));
            Assert.That(injectionError, Is.Not.Null,
                "The opaque factory catches the injection failure and returns a captured parent value.");
            Assert.That(component.Auth, Is.SameAs(childAuth));
            Assert.That(component.Injections, Is.EqualTo(2),
                "The faulted cache entry records an attempt that mutated the already initialized parent.");
            Assert.That(component.Initializations, Is.EqualTo(1));
            Assert.That(child.Resolve<FaultingComponentWrapper>().Attempts, Is.Zero);
        }

        public sealed class ConstructorFailureFlag
        {
            public ConstructorFailureFlag(bool fail) => Fail = fail;
            public bool Fail { get; }
        }

        public sealed class ThrowingCloneService : AutoService, ICapturedParentService, IChildImplementationGuard
        {
            public ThrowingCloneService(ConstructorFailureFlag flag)
            {
                if (flag.Fail) throw new InvalidOperationException("child clone constructor failed");
            }
        }

        [Test]
        public async Task AFailedNewSingletonConstructorDoesNotInvalidateACapturedInitializedParent()
        {
            var parent = _tracker.Build(builder =>
            {
                builder.RegisterInstance(new ConstructorFailureFlag(false));
                builder.Register<ThrowingCloneService>(Lifetime.Singleton).As<ICapturedParentService>().As<IAsyncInitializable>();
            });
            var options = TestScope.Options(new CapturingLogger());
            var parentRun = _tracker.Create(parent, "parent", options);
            await parentRun.RunAsync();
            var captured = parent.Resolve<ICapturedParentService>();
            Exception? constructionError = null;
            var child = _tracker.Track(parent.CreateScope(builder =>
            {
                builder.RegisterInstance(new ConstructorFailureFlag(true));
                builder.Register<ThrowingCloneService>(Lifetime.Singleton).As<IChildImplementationGuard>();
                builder.Register<CapturedSingletonWrapper>(resolver =>
                {
                    try { resolver.Resolve<ICapturedParentService>(); }
                    catch (Exception error) { constructionError = error; }
                    return new CapturedSingletonWrapper(captured);
                }, Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
            }));

            var run = _tracker.Create(child, "child", options, new[] { parentRun });
            await run.RunAsync();

            Assert.That(constructionError, Is.Not.Null);
            Assert.That(child.Resolve<CapturedSingletonWrapper>().Service, Is.SameAs(captured));
            Assert.That(((ThrowingCloneService)captured).Attempts, Is.EqualTo(1));
            Assert.That(run.GetStatus().Service(nameof(CapturedSingletonWrapper)).Dependencies,
                Is.EqualTo(new[] { nameof(ThrowingCloneService) }), run.Describe());
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AnUnknownFaultedHierarchyHelperCannotHideAParentComponentInjectionAttempt(bool attemptInjection)
        {
            var originObject = new GameObject("faulted-helper-origin");
            _objects.Add(originObject);
            var origin = originObject.AddComponent<LifetimeScope>();
            var componentObject = new GameObject("faulted-helper-parent-component");
            _objects.Add(componentObject);
            var component = componentObject.AddComponent<FaultingParentComponent>();
            var helperObject = new GameObject("faulted-hierarchy-helper");
            _objects.Add(helperObject);
            var helper = helperObject.AddComponent<FaultingHierarchyHelperComponent>();
            var parentAuth = new ParentComponentAuth();
            var parent = _tracker.Build(builder =>
            {
                builder.RegisterInstance<IFaultingComponentAuth>(parentAuth);
                builder.RegisterComponent<IParentFaultingComponent>(component).As<IAsyncInitializable>();
            });
            var options = TestScope.Options(new CapturingLogger());
            var parentRun = _tracker.Create(parent, "parent", options);
            await parentRun.RunAsync();
            var childAuth = new ChildComponentAuth();
            Exception? injectionError = null;
            var child = _tracker.Track(parent.CreateScope(builder =>
            {
                builder.RegisterInstance<IFaultingComponentAuth>(childAuth);
                builder.Register<FaultingParentComponent>(Lifetime.Singleton).As<IChildFaultingComponent>();
                RegisterUncreatedHelper(builder, origin, helperObject.transform);
                builder.Register<HelperConsumer>(resolver =>
                {
                    if (attemptInjection)
                    {
                        try { resolver.Resolve<IGraphSubtypeHelper>(); }
                        catch (Exception error) { injectionError = error; }
                    }
                    return new HelperConsumer(helper);
                }, Lifetime.Singleton).AsSelf().As<IAsyncInitializable>();
            }));

            if (attemptInjection)
            {
                var error = Assert.Throws<InitGraphException>(() => _tracker.Create(child, "child", options, new[] { parentRun }));
                Assert.That(error!.Message, Does.Contain("runtime subtype is unknown").And.Contain("unsafe parent service resolution"));
                Assert.That(injectionError, Is.Not.Null);
                Assert.That(component.Auth, Is.SameAs(childAuth));
                Assert.That(component.Injections, Is.EqualTo(2));
                Assert.That(child.Resolve<HelperConsumer>().Attempts, Is.Zero);
            }
            else
            {
                var run = _tracker.Create(child, "child", options, new[] { parentRun });
                await run.RunAsync();
                Assert.That(component.Auth, Is.SameAs(parentAuth));
                Assert.That(component.Injections, Is.EqualTo(1),
                    "A captured helper with no child cache entry is opaque, and does not prove a parent resolution attempt.");
            }
            Assert.That(helper.Parent, Is.Null,
                "A failed parameter resolution never entered the derived helper's injection method.");
            Assert.That(component.Initializations, Is.EqualTo(1));
        }

        [Test]
        public async Task AnInheritedFactoryReturningTheSameParentIdentityDoesNotInventAClone()
        {
            var shared = new CapturedParentService();
            var parent = _tracker.Build(builder => builder.Register<CapturedParentService>(_ => shared, Lifetime.Singleton)
                .As<ICapturedParentService>().As<IAsyncInitializable>());
            var options = TestScope.Options(new CapturingLogger());
            var parentRun = _tracker.Create(parent, "parent", options);
            await parentRun.RunAsync();
            var child = _tracker.Track(parent.CreateScope(builder =>
            {
                builder.Register<CapturedParentService>(Lifetime.Singleton).As<IChildImplementationGuard>();
                builder.Register<CapturedSingletonWrapper>(resolver =>
                        new CapturedSingletonWrapper(resolver.Resolve<ICapturedParentService>()), Lifetime.Singleton)
                    .AsSelf().As<IAsyncInitializable>();
            }));

            var run = _tracker.Create(child, "child", options, new[] { parentRun });
            await run.RunAsync();

            Assert.That(child.Resolve<ICapturedParentService>(), Is.SameAs(shared));
            Assert.That(child.Resolve<CapturedSingletonWrapper>().Service, Is.SameAs(shared));
            Assert.That(shared.Attempts, Is.EqualTo(1));
            Assert.That(run.GetStatus().Service(nameof(CapturedSingletonWrapper)).Dependencies,
                Is.EqualTo(new[] { nameof(CapturedParentService) }), run.Describe());
        }

        public sealed class DisposableSharedParent : AutoService, ICapturedParentService, IChildImplementationGuard, IDisposable
        {
            public int Disposals { get; private set; }
            public void Dispose() => Disposals++;
        }

        [Test]
        public async Task ASameIdentityInheritedFactoryCannotGiveTheChildDisposalOwnershipOfTheLiveParent()
        {
            var shared = new DisposableSharedParent();
            var parent = _tracker.Build(builder => builder.Register<DisposableSharedParent>(_ => shared, Lifetime.Singleton)
                .As<ICapturedParentService>().As<IAsyncInitializable>());
            var options = TestScope.Options(new CapturingLogger());
            var parentRun = _tracker.Create(parent, "parent", options);
            await parentRun.RunAsync();
            var child = _tracker.Track(parent.CreateScope(builder =>
            {
                builder.Register<DisposableSharedParent>(Lifetime.Singleton).As<IChildImplementationGuard>();
                builder.Register<CapturedSingletonWrapper>(resolver =>
                        new CapturedSingletonWrapper(resolver.Resolve<ICapturedParentService>()), Lifetime.Singleton)
                    .AsSelf().As<IAsyncInitializable>();
            }));

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(child, "child", options, new[] { parentRun }));

            Assert.That(error!.Message, Does.Contain("synchronous disposal ownership").And.Contain(nameof(DisposableSharedParent)));
            Assert.That(child.Resolve<ICapturedParentService>(), Is.SameAs(shared));
            Assert.That(shared.Attempts, Is.EqualTo(1));
            Assert.That(shared.Disposals, Is.Zero);
            child.Dispose();
            Assert.That(shared.Disposals, Is.EqualTo(1),
                "The graph rejects actual child tracker membership, rather than guessing from IDisposable alone.");
        }
    }
}
