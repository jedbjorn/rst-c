// IsExternalInit.cs — polyfill for `record` / `init` setters on netstandard2.0.
//
// The C# compiler synthesizes a reference to System.Runtime.CompilerServices
// .IsExternalInit when emitting `init` accessors. .NET 5+ ships it; older
// targets (netstandard2.0 → net48 → Revit 2024) do not. Defining the type
// in any assembly satisfies the compiler with no runtime cost.

#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
#endif
