
namespace StaticCs.Ownership;

/// <summary>
/// There are two concepts expressed in this attribute: "resource" and "linear."
///
/// "Resource" means that variable tracks some unique, consumable resource. In
/// practice, this means that the type implements <see cref="System.IDisposable" />
/// or has a public, parameterless, void-returning Dispose/Free method.
///
/// "Linear" means that each resource is "freed" exactly once. This is enforced by
/// classifying all references to Linear Resource values as either "owned" or "borrowed".
/// "Owning" references must either transfer ownership or free the resource at the end
/// of the variable scope. "Borrowing" references are never allowed free resources.
/// A reference is assumed to be owned, unless it is marked borrowed. To mark a reference
/// as borrowed, apply the <see cref="StaticCs.Ownership.BorrowedAttribute" />.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class LinearResourceAttribute : Attribute {}

/// <summary>
/// See <see cref="StaticCs.Ownership.LinearResourceAttribute" /> for details on how and
/// why to use [Borrowed].
/// </summary>
internal sealed class BorrowedAttribute : Attribute {}