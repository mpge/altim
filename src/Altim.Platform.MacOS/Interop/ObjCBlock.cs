using System.Runtime.InteropServices;

namespace Altim.Platform.MacOS.Interop;

/// <summary>
/// Builds the one kind of Objective-C block Altim needs: a global block that captures
/// nothing and lives for the life of the process.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified.</b> This has never run. It is written from the ABI in Clang's
/// <c>Block-ABI-Apple.txt</c> and libclosure's <c>Block_private.h</c>.
/// </para>
/// <para>
/// <b>Why a block is needed at all.</b>
/// <c>UNUserNotificationCenter.requestAuthorizationWithOptions:completionHandler:</c>
/// declares its completion handler non-null, and Altim has no other way to ask for
/// permission — without permission every notification it posts is dropped without an
/// error. Everything else on the macOS surface is reachable through target/action, which is
/// why this is the only block in the codebase.
/// </para>
/// <para>
/// <b>Shape.</b> A block is a pointer to <c>{ isa, flags, reserved, invoke, descriptor }</c>.
/// <c>isa</c> points at <c>_NSConcreteGlobalBlock</c>, a data symbol exported from
/// libSystem; <c>flags</c> carries <c>BLOCK_IS_GLOBAL</c> so the runtime never tries to copy
/// or dispose it; <c>invoke</c> is a plain C function whose first argument is the block
/// itself. Because nothing is captured there is no copy or dispose helper, so the
/// descriptor is the short two-field form.
/// </para>
/// <para>
/// The memory is allocated once and never freed, which is correct for a global block: the
/// runtime may hold the pointer for as long as the call it was passed to is outstanding,
/// and there are at most two of these in the process.
/// </para>
/// </remarks>
internal static unsafe class ObjCBlock
{
    /// <summary><c>BLOCK_IS_GLOBAL</c>: never copied, never disposed, never refcounted.</summary>
    private const int BlockIsGlobal = 1 << 28;

    private static readonly Lock IsaGate = new();
    private static IntPtr _globalBlockIsa;
    private static bool _isaResolved;

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
    }

    /// <summary>
    /// Creates a global block around a static function pointer.
    /// </summary>
    /// <param name="invoke">
    /// The C function the block calls. Its first parameter must be the block pointer, and
    /// it must be an <see cref="UnmanagedCallersOnlyAttribute"/> method so no marshalling
    /// stub or rooted delegate is involved.
    /// </param>
    /// <returns>
    /// The block pointer, or <see cref="IntPtr.Zero"/> when <c>_NSConcreteGlobalBlock</c>
    /// could not be resolved — which is the case on any host that is not macOS. Callers
    /// treat a zero as "cannot ask", not as an error.
    /// </returns>
    internal static IntPtr CreateGlobal(IntPtr invoke)
    {
        IntPtr isa = ResolveGlobalBlockIsa();
        if (isa == IntPtr.Zero || invoke == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        descriptor->Reserved = 0;
        descriptor->Size = (nuint)sizeof(BlockLiteral);

        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        block->Isa = isa;
        block->Flags = BlockIsGlobal;
        block->Reserved = 0;
        block->Invoke = invoke;
        block->Descriptor = (IntPtr)descriptor;

        return (IntPtr)block;
    }

    private static IntPtr ResolveGlobalBlockIsa()
    {
        lock (IsaGate)
        {
            if (_isaResolved)
            {
                return _globalBlockIsa;
            }

            _isaResolved = true;

            // libclosure is part of libSystem on macOS. The symbol is data, so the address
            // the loader reports is the isa pointer itself.
            if (NativeLibrary.TryLoad("/usr/lib/libSystem.B.dylib", out IntPtr library) &&
                NativeLibrary.TryGetExport(library, "_NSConcreteGlobalBlock", out IntPtr symbol))
            {
                _globalBlockIsa = symbol;
            }

            return _globalBlockIsa;
        }
    }
}
