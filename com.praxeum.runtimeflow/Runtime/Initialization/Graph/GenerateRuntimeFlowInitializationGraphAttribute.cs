using System;

namespace RuntimeFlow.Contexts
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class GenerateRuntimeFlowInitializationGraphAttribute : Attribute
    {
    }
}
