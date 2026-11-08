#if NETSTANDARD2_0
// Required by the compiler for C# record types on netstandard2.0.
// This is compile-time-only and does not affect the runtime protocol.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
