// IsExternalInit.cs — polyfill for `record` / `init` setters on net48.
//
// RST.Core has its own copy gated on NETSTANDARD2_0; RST.Engine compiles
// directly to net48 under Debug R24, so it needs the shim here too.
// Defining the type satisfies the compiler with no runtime cost.

#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit { }
}
#endif
