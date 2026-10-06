using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using System.Linq;
using RuntimeFlow.Internal;
using RuntimeFlow.Testing;
using RuntimeFlow.Tests.Support;
using VContainer;
using VContainer.Internal;
using VContainer.Unity;

namespace RuntimeFlow.Tests.VContainerIntegration
{
    [TestFixture]
    public sealed class InjectionResolutionContractTests
    {
        public interface IPart { }

        public sealed class ParentPart : AutoService, IPart { }

        public sealed class LocalPartsConsumer : AutoService
        {
            public LocalPartsConsumer(ContainerLocal<IReadOnlyList<IPart>> parts) => Parts = parts.Value;

            public IReadOnlyList<IPart> Parts { get; }
        }

        public interface IAuth { }
        public sealed class ParentAuth : AutoService, IAuth { }
        public sealed class ChildAuth : AutoService, IAuth { }

        public interface IParentPlain { }
        public interface IChildPlain { }

        public sealed class Plain : IParentPlain, IChildPlain
        {
            public Plain(IAuth auth) => Auth = auth;

            public IAuth Auth { get; }
        }

        public sealed class PlainConsumer : AutoService
        {
            public PlainConsumer(IParentPlain plain) => Auth = ((Plain)plain).Auth;

            public IAuth Auth { get; }
        }

        public interface IKeyedDependency { }
        public sealed class KeyedA : AutoService, IKeyedDependency { }
        public sealed class KeyedB : AutoService, IKeyedDependency { }

        private readonly RunTracker _tracker = new RunTracker();
        private RuntimeFlowOptions _options = null!;

        [SetUp]
        public void SetUp() => _options = TestScope.Options(new CapturingLogger());

        [TearDown]
        public void TearDown() => _tracker.DisposeAll();

        [Test]
        public void ContainerLocalCollectionExcludesParentSingletonRegistrations()
        {
            var parent = _tracker.Build(b =>
                b.Register<ParentPart>(Lifetime.Singleton).As<IPart>().As<IAsyncInitializable>());
            var parentRun = _tracker.Create(parent, "parent", _options);
            var child = _tracker.Track(parent.CreateScope(b => b.Add<LocalPartsConsumer>()));

            Assert.That(child.Resolve<LocalPartsConsumer>().Parts, Is.Empty,
                "VContainer injects only the child's collection through ContainerLocal.");

            var childRun = _tracker.Create(child, "child", _options, new[] { parentRun });

            Assert.That(childRun.GetStatus().Service("LocalPartsConsumer").Dependencies, Is.Empty,
                childRun.Describe());
        }

        [Test]
        public void ParentSingletonUsesChildAuthWhenChildRegistersItsImplementation()
        {
            var parent = _tracker.Build(b =>
            {
                b.Add<ParentAuth>();
                b.Register<Plain>(Lifetime.Singleton).As<IParentPlain>();
            });
            var parentRun = _tracker.Create(parent, "parent", _options);
            var child = _tracker.Track(parent.CreateScope(b =>
            {
                b.Add<ChildAuth>();
                b.Register<Plain>(Lifetime.Singleton).As<IChildPlain>();
                b.Add<PlainConsumer>();
            }));

            Assert.That(child.Resolve<PlainConsumer>().Auth, Is.SameAs(child.Resolve<ChildAuth>()),
                "The child implementation registration keeps inherited Plain singleton construction in the child.");

            var childRun = _tracker.Create(child, "child", _options, new[] { parentRun });

            Assert.That(childRun.GetStatus().Service("PlainConsumer").Dependencies,
                Is.EqualTo(new[] { "ChildAuth" }), childRun.Describe());
        }

        public interface IParentService { }
        public interface IChildService { }

        public sealed class InheritedService : AutoService, IParentService, IChildService
        {
            public static int Constructions;
            public InheritedService() => Constructions++;
        }

        public sealed class UsesInheritedService : AutoService
        {
            public static int Constructions;
            public UsesInheritedService(IParentService service) => Constructions++;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RecreatedParentServiceIsRejectedBeforeChildConstruction(bool linkParentRun)
        {
            InheritedService.Constructions = 0;
            UsesInheritedService.Constructions = 0;
            var parent = _tracker.Build(b => b.Register<InheritedService>(Lifetime.Singleton)
                .As<IParentService>().As<IAsyncInitializable>());
            var parentRun = _tracker.Create(parent, "parent", _options);
            var child = _tracker.Track(parent.CreateScope(b =>
            {
                b.Register<InheritedService>(Lifetime.Singleton).As<IChildService>();
                b.Add<UsesInheritedService>();
            }));

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(child, "child", _options,
                linkParentRun ? new[] { parentRun } : null));

            Assert.That(error!.Message, Does.Contain("new instance of parent service").And.Contain(nameof(InheritedService))
                .And.Contain("Remove the child registration"));
            Assert.That(InheritedService.Constructions, Is.EqualTo(1));
            Assert.That(UsesInheritedService.Constructions, Is.Zero);
        }

        [Test]
        public void InheritedRegisteredInstanceKeepsItsInitializedIdentityDespiteAChildImplementationGuard()
        {
            var instance = new InheritedService();
            var parent = _tracker.Build(b => b.RegisterInstance(instance).As<IParentService>().As<IAsyncInitializable>());
            var parentRun = _tracker.Create(parent, "parent", _options);
            var child = _tracker.Track(parent.CreateScope(b =>
            {
                b.Register<InheritedService>(Lifetime.Singleton).As<IChildService>();
                b.Add<UsesInheritedService>();
            }));

            var run = _tracker.Create(child, "child", _options, new[] { parentRun });

            Assert.That(child.Resolve<IParentService>(), Is.SameAs(instance));
            Assert.That(run.GetStatus().Service(nameof(UsesInheritedService)).Dependencies,
                Is.EqualTo(new[] { nameof(InheritedService) }));
        }

        public sealed class Reinjectable : AutoService, IParentService, IChildService
        {
            public IAuth? Auth { get; private set; }
            public int Injections { get; private set; }

            [Inject]
            public void Construct(IAuth auth)
            {
                Auth = auth;
                Injections++;
            }
        }

        [Test]
        public void AChildImplementationGuardCannotReinjectAnInitializedParentComponent()
        {
            var component = new Reinjectable();
            UsesInheritedService.Constructions = 0;
            var parent = _tracker.Build(b =>
            {
                b.Add<ParentAuth>();
                b.RegisterComponent<IParentService>(component).As<IAsyncInitializable>();
            });
            var parentRun = _tracker.Create(parent, "parent", _options);
            var parentAuth = parent.Resolve<ParentAuth>();
            Assert.That(component.Injections, Is.EqualTo(1));
            Assert.That(component.Auth, Is.SameAs(parentAuth));
            var child = _tracker.Track(parent.CreateScope(b =>
            {
                b.Add<ChildAuth>();
                b.Register<Reinjectable>(Lifetime.Singleton).As<IChildService>();
                b.Add<UsesInheritedService>();
            }));

            var error = Assert.Throws<InitGraphException>(() => _tracker.Create(child, "child", _options, new[] { parentRun }));

            Assert.That(error!.Message, Does.Contain("re-injects parent component").And.Contain(nameof(Reinjectable))
                .And.Contain("Remove the child registration"));
            Assert.That(component.Injections, Is.EqualTo(1));
            Assert.That(component.Auth, Is.SameAs(parentAuth));
            Assert.That(UsesInheritedService.Constructions, Is.Zero);
        }

        [Test]
        public void AnInheritedSingletonComponentKeepsItsParentInjectionWithoutAChildImplementationGuard()
        {
            var component = new Reinjectable();
            var parent = _tracker.Build(b =>
            {
                b.Add<ParentAuth>();
                b.RegisterComponent<IParentService>(component).As<IAsyncInitializable>();
            });
            var parentRun = _tracker.Create(parent, "parent", _options);
            var parentAuth = parent.Resolve<ParentAuth>();
            var child = _tracker.Track(parent.CreateScope(b =>
            {
                b.Add<ChildAuth>();
                b.Add<UsesInheritedService>();
            }));
            var run = _tracker.Create(child, "child", _options, new[] { parentRun });

            Assert.That(child.Resolve<IParentService>(), Is.SameAs(component));
            Assert.That(component.Injections, Is.EqualTo(1));
            Assert.That(component.Auth, Is.SameAs(parentAuth));
            Assert.That(run.GetStatus().Service(nameof(UsesInheritedService)).Dependencies,
                Is.EqualTo(new[] { nameof(Reinjectable) }), run.Describe());
        }

        public sealed class InitializableStrings : List<string>, IAsyncInitializable
        {
            public bool Initialized { get; private set; }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                Initialized = true;
                return Task.CompletedTask;
            }
        }

        public sealed class StringCollectionConsumer : IAsyncInitializable
        {
            public StringCollectionConsumer(IReadOnlyList<string> strings) => Strings = (InitializableStrings)strings;
            public InitializableStrings Strings { get; }
            public bool SawInitializedStrings { get; private set; }

            public Task InitializeAsync(InitContext context, CancellationToken cancellationToken)
            {
                SawInitializedStrings = Strings.Initialized;
                return Task.CompletedTask;
            }
        }

        [Test]
        public async Task AnExplicitInitializableCollectionIsOrderedEvenWhenItsElementsAreFrameworkTypes()
        {
            var container = _tracker.Build(b =>
            {
                b.Register<InitializableStrings>(Lifetime.Singleton).AsSelf().As<IReadOnlyList<string>>().As<IAsyncInitializable>();
                b.Add<StringCollectionConsumer>();
            });
            var run = _tracker.Create(container, "scope", _options);
            var consumer = container.Resolve<StringCollectionConsumer>();

            Assert.That(consumer.Strings, Is.SameAs(container.Resolve<InitializableStrings>()));
            Assert.That(run.GetStatus().Service(nameof(StringCollectionConsumer)).Dependencies,
                Is.EqualTo(new[] { nameof(InitializableStrings) }), run.Describe());
            Assert.That((await run.RunAsync()).Outcome, Is.EqualTo(StartupOutcome.Completed));
            Assert.That(consumer.SawInitializedStrings, Is.True);
        }

        [Test]
        public void SingletonWalkMemoizationFollowsTheEffectiveConstructionResolver()
        {
            var parent = _tracker.Build(b =>
            {
                b.Add<ParentAuth>();
                b.Register<Plain>(Lifetime.Singleton).As<IParentPlain>();
            });
            var ordinaryChild = _tracker.Track(parent.CreateScope(b => b.Add<ChildAuth>()));
            var guardedChild = _tracker.Track(parent.CreateScope(b =>
            {
                b.Add<ChildAuth>();
                b.Register<Plain>(Lifetime.Singleton).As<IChildPlain>();
            }));
            Assert.That(parent.TryGetRegistration(typeof(ParentAuth), out var parentAuth), Is.True);
            Assert.That(guardedChild.TryGetRegistration(typeof(ChildAuth), out var childAuth), Is.True);
            var parentNode = new ServiceNode(0, NodeKind.Service, nameof(ParentAuth), typeof(ParentAuth), "parent") { Registration = parentAuth };
            var childNode = new ServiceNode(1, NodeKind.Service, nameof(ChildAuth), typeof(ChildAuth), "child") { Registration = childAuth };
            var edges = new InjectionEdges(guardedChild, new[] { parentNode, childNode });

            Assert.That(edges.Targets(typeof(IParentPlain), parent, null).Select(t => t.Target), Is.EqualTo(new[] { parentNode }));
            Assert.That(edges.Targets(typeof(IParentPlain), guardedChild, null).Select(t => t.Target), Is.EqualTo(new[] { childNode }));
            Assert.That(edges.Targets(typeof(IParentPlain), ordinaryChild, null).Select(t => t.Target), Is.EqualTo(new[] { parentNode }));
            Assert.That(((Plain)ordinaryChild.Resolve<IParentPlain>()).Auth, Is.SameAs(parent.Resolve<ParentAuth>()));
            Assert.That(((Plain)guardedChild.Resolve<IParentPlain>()).Auth, Is.SameAs(guardedChild.Resolve<ChildAuth>()));
        }

        public sealed class LocalPart : AutoService, IPart { }
        public sealed class ScopedPart : IPart
        {
            public ScopedPart(IAuth auth) => Auth = auth;
            public IAuth Auth { get; }
        }
        public sealed class TransientPart : IPart
        {
            public TransientPart(IAuth auth) => Auth = auth;
            public IAuth Auth { get; }
        }

        [Test]
        public void LocalCollectionRetainsParentScopedAndTransientElementsResolvedThroughTheChild()
        {
            var parent = _tracker.Build(b =>
            {
                b.Add<ParentAuth>();
                b.Register<ParentPart>(Lifetime.Singleton).As<IPart>().As<IAsyncInitializable>();
                b.Register<ScopedPart>(Lifetime.Scoped).As<IPart>();
                b.Register<TransientPart>(Lifetime.Transient).As<IPart>();
            });
            var parentRun = _tracker.Create(parent, "parent", _options);
            var child = _tracker.Track(parent.CreateScope(b =>
            {
                b.Add<ChildAuth>();
                b.Register<LocalPart>(Lifetime.Singleton).As<IPart>().As<IAsyncInitializable>();
                b.Add<LocalPartsConsumer>();
            }));

            var run = _tracker.Create(child, "child", _options, new[] { parentRun });
            var parts = child.Resolve<LocalPartsConsumer>().Parts;

            Assert.That(parts.Select(p => p.GetType()), Is.EquivalentTo(new[] { typeof(LocalPart), typeof(ScopedPart), typeof(TransientPart) }));
            Assert.That(((ScopedPart)parts.Single(p => p is ScopedPart)).Auth, Is.SameAs(child.Resolve<ChildAuth>()));
            Assert.That(((TransientPart)parts.Single(p => p is TransientPart)).Auth, Is.SameAs(child.Resolve<ChildAuth>()));
            Assert.That(run.GetStatus().Service(nameof(LocalPartsConsumer)).Dependencies,
                Is.EquivalentTo(new[] { nameof(LocalPart), nameof(ChildAuth) }), run.Describe());
        }

        public sealed class LocalAuthConsumer : AutoService
        {
            public LocalAuthConsumer(ContainerLocal<IAuth> auth) => Auth = auth.Value;
            public IAuth Auth { get; }
        }

        [Test]
        public void SingularContainerLocalStillResolvesAnInheritedSingleton()
        {
            var parent = _tracker.Build(b => b.Add<ParentAuth>());
            var parentRun = _tracker.Create(parent, "parent", _options);
            var child = _tracker.Track(parent.CreateScope(b => b.Add<LocalAuthConsumer>()));
            var run = _tracker.Create(child, "child", _options, new[] { parentRun });

            Assert.That(child.Resolve<LocalAuthConsumer>().Auth, Is.SameAs(parent.Resolve<ParentAuth>()));
            Assert.That(run.GetStatus().Service(nameof(LocalAuthConsumer)).Dependencies,
                Is.EqualTo(new[] { nameof(ParentAuth) }), run.Describe());
        }

        public sealed class ExplicitParts : List<IPart>
        {
            public ExplicitParts(IAuth auth) => Auth = auth;
            public IAuth Auth { get; }
        }

        [Test]
        public void LocalCollectionUsesAnExplicitLocalCollectionRegistrationAsOneValue()
        {
            var container = _tracker.Build(b =>
            {
                b.Add<ChildAuth>();
                b.Register<ExplicitParts>(Lifetime.Singleton).As<IReadOnlyList<IPart>>();
                b.Add<LocalPartsConsumer>();
            });
            var run = _tracker.Create(container, "local", _options);

            Assert.That(((ExplicitParts)container.Resolve<LocalPartsConsumer>().Parts).Auth, Is.SameAs(container.Resolve<ChildAuth>()));
            Assert.That(run.GetStatus().Service(nameof(LocalPartsConsumer)).Dependencies,
                Is.EqualTo(new[] { nameof(ChildAuth) }), run.Describe());
        }

        [Test]
        public void AnExplicitParentCollectionIsNotMergedIntoASyntheticChildCollection()
        {
            var parent = _tracker.Build(b =>
            {
                b.Add<ParentAuth>();
                b.Register<ExplicitParts>(Lifetime.Singleton).As<IEnumerable<IPart>>().As<IReadOnlyList<IPart>>();
            });
            var parentRun = _tracker.Create(parent, "parent", _options);
            var child = _tracker.Track(parent.CreateScope(b => b.Add<LocalPartsConsumer>()));
            var run = _tracker.Create(child, "child", _options, new[] { parentRun });

            Assert.That(child.Resolve<LocalPartsConsumer>().Parts, Is.Empty);
            Assert.That(run.GetStatus().Service(nameof(LocalPartsConsumer)).Dependencies, Is.Empty, run.Describe());
        }

        public sealed class PlainCollectionConsumer : AutoService
        {
            public PlainCollectionConsumer(IReadOnlyList<IParentPlain> parts) => Parts = parts;
            public IReadOnlyList<IParentPlain> Parts { get; }
        }

        [Test]
        public void OrdinaryCollectionSingletonEdgesMatchTheInstalledVContainerResolutionScope()
        {
            var parent = _tracker.Build(b =>
            {
                b.Add<ParentAuth>();
                b.Register<Plain>(Lifetime.Singleton).As<IParentPlain>();
            });
            var parentRun = _tracker.Create(parent, "parent", _options);
            var child = _tracker.Track(parent.CreateScope(b =>
            {
                b.Add<ChildAuth>();
                b.Register<Plain>(Lifetime.Singleton).As<IChildPlain>();
                // Walk the inherited registration as a singular dependency before the collection, so
                // memoization must distinguish fork collection construction in the parent from the child.
                b.Add<PlainConsumer>();
                b.Add<PlainCollectionConsumer>();
            }));
            var run = _tracker.Create(child, "child", _options, new[] { parentRun });
            var actual = ((Plain)child.Resolve<PlainCollectionConsumer>().Parts.Single()).Auth;
            // Derive the required edge from the actual installed container's result, independently of
            // the graph builder's provider metadata detection (stock and the fork differ here).
            Assert.That(actual, Is.SameAs(parent.Resolve<ParentAuth>()).Or.SameAs(child.Resolve<ChildAuth>()));
            Assert.That(run.GetStatus().Service(nameof(PlainConsumer)).Dependencies, Is.EqualTo(new[] { nameof(ChildAuth) }));
            Assert.That(run.GetStatus().Service(nameof(PlainCollectionConsumer)).Dependencies,
                Is.EqualTo(new[] { actual.GetType().Name }), run.Describe());
        }

        [Test]
        public void LocalCollectionSkipsAnExplicitParentValueAndContinuesToGrandparentElements()
        {
            var grandparent = _tracker.Build(b =>
            {
                b.Add<ParentAuth>();
                b.Register<ScopedPart>(Lifetime.Scoped).As<IPart>();
            });
            var grandparentRun = _tracker.Create(grandparent, "grandparent", _options);
            var parent = _tracker.Track(grandparent.CreateScope(b =>
                b.Register<ExplicitParts>(Lifetime.Singleton).As<IEnumerable<IPart>>().As<IReadOnlyList<IPart>>()));
            var parentRun = _tracker.Create(parent, "parent", _options, new[] { grandparentRun });
            var child = _tracker.Track(parent.CreateScope(b =>
            {
                b.Add<ChildAuth>();
                b.Add<LocalPartsConsumer>();
            }));
            var run = _tracker.Create(child, "child", _options, new[] { parentRun, grandparentRun });

            var parts = child.Resolve<LocalPartsConsumer>().Parts;
            Assert.That(parts.Count, Is.EqualTo(1));
            Assert.That(((ScopedPart)parts.Single()).Auth, Is.SameAs(child.Resolve<ChildAuth>()));
            Assert.That(run.GetStatus().Service(nameof(LocalPartsConsumer)).Dependencies,
                Is.EqualTo(new[] { nameof(ChildAuth) }), run.Describe());
        }

        [Test]
        public void AnExplicitContainerLocalFactoryKeepsItsOwnValueInsteadOfAcquiringCollectionEdges()
        {
            var parent = _tracker.Build(b =>
                b.Register<ParentPart>(Lifetime.Singleton).As<IPart>().As<IAsyncInitializable>());
            var parentRun = _tracker.Create(parent, "parent", _options);
            var child = _tracker.Track(parent.CreateScope(b =>
            {
                b.Register(_ => new ContainerLocal<IReadOnlyList<IPart>>(Array.Empty<IPart>()), Lifetime.Singleton);
                b.Add<LocalPartsConsumer>();
            }));
            var run = _tracker.Create(child, "child", _options, new[] { parentRun });

            Assert.That(child.Resolve<LocalPartsConsumer>().Parts, Is.Empty);
            Assert.That(run.GetStatus().Service(nameof(LocalPartsConsumer)).Dependencies, Is.Empty, run.Describe());
        }

        [Test]
        public void OverloadedInjectMethodsRetainBothKeyedDependencyEdges()
        {
            var keyAttribute = typeof(IObjectResolver).Assembly.GetType("VContainer.KeyAttribute");
            var keyed = typeof(RegistrationBuilder).GetMethod("Keyed", new[] { typeof(object) });
            if (keyAttribute == null || keyed == null)
            {
                Assert.Ignore("This VContainer does not support keyed registrations.");
                return;
            }

            var name = new AssemblyName("RuntimeFlow.Tests.OverloadedKeyed" + Guid.NewGuid().ToString("N"));
            var module = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run)
                .DefineDynamicModule(name.Name);
            var type = module.DefineType("OverloadedKeyedConsumer",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(AutoService));
            var first = type.DefineField("First", typeof(IKeyedDependency), FieldAttributes.Public);
            var second = type.DefineField("Second", typeof(IKeyedDependency), FieldAttributes.Public);
            var ctor = type.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var ctorIl = ctor.GetILGenerator();
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Call, typeof(AutoService).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!);
            ctorIl.Emit(OpCodes.Ret);

            var inject = new CustomAttributeBuilder(typeof(InjectAttribute).GetConstructor(Type.EmptyTypes)!,
                Array.Empty<object>());
            var keyCtor = keyAttribute.GetConstructor(new[] { typeof(object) })!;
            var firstMethod = type.DefineMethod("Construct", MethodAttributes.Public, typeof(void),
                new[] { typeof(IKeyedDependency) });
            firstMethod.SetCustomAttribute(inject);
            firstMethod.DefineParameter(1, ParameterAttributes.None, "dependency")
                .SetCustomAttribute(new CustomAttributeBuilder(keyCtor, new object[] { "a" }));
            var firstIl = firstMethod.GetILGenerator();
            firstIl.Emit(OpCodes.Ldarg_0);
            firstIl.Emit(OpCodes.Ldarg_1);
            firstIl.Emit(OpCodes.Stfld, first);
            firstIl.Emit(OpCodes.Ret);

            var secondMethod = type.DefineMethod("Construct", MethodAttributes.Public, typeof(void),
                new[] { typeof(IKeyedDependency), typeof(string) });
            secondMethod.SetCustomAttribute(inject);
            secondMethod.DefineParameter(1, ParameterAttributes.None, "dependency")
                .SetCustomAttribute(new CustomAttributeBuilder(keyCtor, new object[] { "b" }));
            secondMethod.DefineParameter(2, ParameterAttributes.None, "unused");
            var secondIl = secondMethod.GetILGenerator();
            secondIl.Emit(OpCodes.Ldarg_0);
            secondIl.Emit(OpCodes.Ldarg_1);
            secondIl.Emit(OpCodes.Stfld, second);
            secondIl.Emit(OpCodes.Ret);
            var consumer = type.CreateTypeInfo()!.AsType();

            var container = _tracker.Build(builder =>
            {
                keyed.Invoke(builder.Register<KeyedA>(Lifetime.Singleton)
                    .As<IKeyedDependency>().As<IAsyncInitializable>(), new object[] { "a" });
                keyed.Invoke(builder.Register<KeyedB>(Lifetime.Singleton)
                    .As<IKeyedDependency>().As<IAsyncInitializable>(), new object[] { "b" });
                builder.Register(consumer, Lifetime.Singleton).AsSelf().As(typeof(IAsyncInitializable))
                    .WithParameter("unused", "x");
            });

            var instance = container.Resolve(consumer);
            Assert.That(consumer.GetField("First")!.GetValue(instance), Is.TypeOf<KeyedA>());
            Assert.That(consumer.GetField("Second")!.GetValue(instance), Is.TypeOf<KeyedB>());

            var run = _tracker.Create(container, "session", _options);
            Assert.That(run.GetStatus().Service("OverloadedKeyedConsumer").Dependencies,
                Is.EquivalentTo(new[] { nameof(KeyedA), nameof(KeyedB) }), run.Describe());
        }
    }
}
