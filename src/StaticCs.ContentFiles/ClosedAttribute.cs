using System;

namespace StaticCs
{
    [AttributeUsage(AttributeTargets.Enum | AttributeTargets.Class)]
    internal sealed class ClosedAttribute : Attribute { }
}
