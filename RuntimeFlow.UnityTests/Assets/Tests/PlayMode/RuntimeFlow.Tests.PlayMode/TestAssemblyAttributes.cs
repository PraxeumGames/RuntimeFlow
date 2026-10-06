using RuntimeFlow.Tests.Shared;

// Unity's runner reports an async test whose task ends Canceled as passed; this turns an escaping
// OperationCanceledException into a failure for every async test in the assembly.
[assembly: FailOnEscapedCancellation]
