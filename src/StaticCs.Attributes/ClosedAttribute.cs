
using System;
using System.Diagnostics;

namespace StaticCs;

[AttributeUsage(AttributeTargets.Enum)]
[Conditional("EMIT_STATICCS_CLOSEDATTRIBUTE")]
public sealed class ClosedAttribute : Attribute { }