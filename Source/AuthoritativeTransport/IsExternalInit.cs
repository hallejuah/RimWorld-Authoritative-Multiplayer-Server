#if NETSTANDARD2_0
// netstandard2.0 lacks the compiler support type for record init-only properties.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
