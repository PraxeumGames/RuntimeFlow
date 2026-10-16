using System;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace RuntimeFlow.Tests.Shared
{
    /// <summary>
    /// Makes every <c>async Task</c> test in the assembly fail when its returned task ends
    /// <see cref="TaskStatus.Canceled"/>, i.e. when an <see cref="OperationCanceledException"/> escapes the
    /// test body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity Test Framework (1.4.x, <c>TestTaskWrapper</c>) polls the task an async test returns and rethrows
    /// only when it is <see cref="Task.IsFaulted"/>. A cancelled task is neither faulted nor reported, so the
    /// test is recorded as passed — a cancelled startup or restart chain would look green. Synchronous
    /// tests and <c>[UnityTest]</c> coroutines are not affected: there the exception propagates and NUnit
    /// records it as an error.
    /// </para>
    /// <para>
    /// Apply it once per test assembly (<c>[assembly: FailOnEscapedCancellation]</c>). NUnit runs assembly
    /// level <see cref="IApplyToTest"/> attributes after the whole fixture tree is built, so the attribute
    /// walks the tree and swaps the <see cref="Test.Method"/> of every <see cref="Task"/>-returning test for
    /// a wrapper whose invocation turns a cancelled task into an <see cref="AssertionException"/>. The rest
    /// of Unity's command chain (log checks, timeouts, set-up and tear-down) is untouched. Tests that expect
    /// a cancellation must observe it explicitly, e.g. with <c>AsyncTestAssert.ThrowsAsync</c>.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class FailOnEscapedCancellationAttribute : NUnitAttribute, IApplyToTest
    {
        /// <inheritdoc />
        public void ApplyToTest(Test test) => Rewrite(test);

        /// <summary>
        /// Returns a task that mirrors <paramref name="testTask"/>, except that a cancellation becomes a
        /// failure carrying the escaped exception. Faults and successes pass through unchanged.
        /// </summary>
        public static Task Surface(Task testTask)
        {
            if (testTask == null) throw new ArgumentNullException(nameof(testTask));
            return testTask.ContinueWith(
                    completed => completed.IsCanceled
                        ? Task.FromException(new AssertionException(Describe(completed)))
                        : completed,
                    TaskContinuationOptions.ExecuteSynchronously)
                .Unwrap();
        }

        /// <summary>True when <paramref name="method"/> is already routed through <see cref="Surface"/>.</summary>
        public static bool IsSurfaced(IMethodInfo? method) => method is SurfacingMethod;

        private static void Rewrite(ITest test)
        {
            if (test is TestMethod testMethod
                && testMethod.Method != null
                && !(testMethod.Method is SurfacingMethod)
                && testMethod.Method.ReturnType.Type == typeof(Task))
            {
                testMethod.Method = new SurfacingMethod(testMethod.Method);
            }

            foreach (var child in test.Tests)
            {
                Rewrite(child);
            }
        }

        private static string Describe(Task cancelled)
        {
            string escaped;
            try
            {
                cancelled.GetAwaiter().GetResult();
                escaped = "(no exception)";
            }
            catch (OperationCanceledException exception)
            {
                escaped = exception.ToString();
            }

            return "The test's task ended Canceled: an OperationCanceledException escaped the test body. "
                + "Unity's runner would report this as passed. Assert an expected cancellation explicitly "
                + "(AsyncTestAssert.ThrowsAsync).\n" + escaped;
        }

        private static object? SurfaceResult(object? result) => result is Task task ? Surface(task) : result;

        /// <summary>Forwards everything to the original method; only invocation is wrapped.</summary>
        private sealed class SurfacingMethod : IMethodInfo
        {
            private readonly IMethodInfo _inner;
            private readonly SurfacingMethodInfo _methodInfo;

            public SurfacingMethod(IMethodInfo inner)
            {
                _inner = inner;
                _methodInfo = new SurfacingMethodInfo(inner.MethodInfo);
            }

            public ITypeInfo TypeInfo => _inner.TypeInfo;
            public MethodInfo MethodInfo => _methodInfo;
            public string Name => _inner.Name;
            public bool IsAbstract => _inner.IsAbstract;
            public bool IsPublic => _inner.IsPublic;
            public bool ContainsGenericParameters => _inner.ContainsGenericParameters;
            public bool IsGenericMethod => _inner.IsGenericMethod;
            public bool IsGenericMethodDefinition => _inner.IsGenericMethodDefinition;
            public ITypeInfo ReturnType => _inner.ReturnType;
            public IParameterInfo[] GetParameters() => _inner.GetParameters();
            public Type[] GetGenericArguments() => _inner.GetGenericArguments();
            public IMethodInfo MakeGenericMethod(params Type[] typeArguments) =>
                new SurfacingMethod(_inner.MakeGenericMethod(typeArguments));
            public object? Invoke(object fixture, params object[] args) => SurfaceResult(_inner.Invoke(fixture, args));
            public T[] GetCustomAttributes<T>(bool inherit) where T : class => _inner.GetCustomAttributes<T>(inherit);
            public bool IsDefined<T>(bool inherit) => _inner.IsDefined<T>(inherit);
            public override string ToString() => _inner.ToString();
        }

        /// <summary>
        /// Unity's <c>TestTaskWrapper</c> invokes <c>Test.Method.MethodInfo</c> directly, so the reflection
        /// object itself must wrap the returned task. Every other member forwards to the real method.
        /// </summary>
        private sealed class SurfacingMethodInfo : MethodInfo
        {
            private readonly MethodInfo _inner;

            public SurfacingMethodInfo(MethodInfo inner) => _inner = inner;

            public override object? Invoke(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? parameters,
                CultureInfo? culture) => SurfaceResult(_inner.Invoke(obj, invokeAttr, binder, parameters, culture));

            public override string Name => _inner.Name;
            public override Type? DeclaringType => _inner.DeclaringType;
            public override Type? ReflectedType => _inner.ReflectedType;
            public override MethodAttributes Attributes => _inner.Attributes;
            public override RuntimeMethodHandle MethodHandle => _inner.MethodHandle;
            public override CallingConventions CallingConvention => _inner.CallingConvention;
            public override Type ReturnType => _inner.ReturnType;
            public override ParameterInfo ReturnParameter => _inner.ReturnParameter;
            public override ICustomAttributeProvider ReturnTypeCustomAttributes => _inner.ReturnTypeCustomAttributes;
            public override int MetadataToken => _inner.MetadataToken;
            public override Module Module => _inner.Module;
            public override bool IsGenericMethod => _inner.IsGenericMethod;
            public override bool IsGenericMethodDefinition => _inner.IsGenericMethodDefinition;
            public override bool ContainsGenericParameters => _inner.ContainsGenericParameters;
            public override Type[] GetGenericArguments() => _inner.GetGenericArguments();
            public override MethodInfo GetBaseDefinition() => _inner.GetBaseDefinition();
            public override MethodImplAttributes GetMethodImplementationFlags() => _inner.GetMethodImplementationFlags();
            public override ParameterInfo[] GetParameters() => _inner.GetParameters();
            public override object[] GetCustomAttributes(bool inherit) => _inner.GetCustomAttributes(inherit);
            public override object[] GetCustomAttributes(Type attributeType, bool inherit) =>
                _inner.GetCustomAttributes(attributeType, inherit);
            public override System.Collections.Generic.IList<CustomAttributeData> GetCustomAttributesData() =>
                _inner.GetCustomAttributesData();
            public override bool IsDefined(Type attributeType, bool inherit) => _inner.IsDefined(attributeType, inherit);
            public override string? ToString() => _inner.ToString();
        }
    }
}
