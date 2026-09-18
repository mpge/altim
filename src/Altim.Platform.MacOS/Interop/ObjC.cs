using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Altim.Platform.MacOS.Interop;

/// <summary>
/// The Objective-C runtime surface Altim uses on macOS: class and selector lookup, a
/// dynamically registered callback class, and the message sends needed to drive
/// <c>NSStatusItem</c>, <c>NSMenu</c>, <c>NSWorkspace</c>, <c>UNUserNotificationCenter</c>
/// and <c>SMAppService</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of this has ever been executed.</b> There is no macOS host in the development
/// environment, so every entry point below is written from the published Objective-C
/// headers and the platform ABI rules rather than from a run. Each type that calls into
/// this file repeats the warning where the call is made.
/// </para>
/// <para>
/// <b>Why one declaration per call shape.</b> <c>objc_msgSend</c> is declared variadic in
/// the headers but is never actually called that way: the compiler casts it to the exact
/// signature of the method being sent. Apple's ARM64 ABI passes variadic arguments
/// differently from fixed ones, so a single "generic" declaration would be wrong on Apple
/// silicon. Every shape Altim sends therefore gets its own non-variadic declaration.
/// </para>
/// <para>
/// <b>Why there are two ways to read a rectangle.</b> A four-double <c>CGRect</c> is 32
/// bytes. On x86-64 the System V ABI returns it through a hidden pointer, and the
/// Objective-C runtime has a separate entry point, <c>objc_msgSend_stret</c>, whose first
/// argument is that pointer; calling plain <c>objc_msgSend</c> for such a return puts the
/// receiver in the wrong register and corrupts the call. On ARM64 a four-double struct is
/// a homogeneous floating-point aggregate returned in <c>v0</c>–<c>v3</c>, plain
/// <c>objc_msgSend</c> is correct, and <c>objc_msgSend_stret</c> does not exist at all.
/// <see cref="SendRect"/> picks between them by process architecture, and the
/// <c>objc_msgSend_stret</c> import is only resolved on the architecture that has it,
/// because P/Invoke binds an entry point on first call rather than at load.
/// </para>
/// <para>
/// <b>Strings.</b> Everything here that takes text takes an owned <c>NSString</c> created
/// by <see cref="CreateString"/> and released by the caller. Autoreleased strings are
/// avoided deliberately: Altim calls in from worker threads where there is no autorelease
/// pool in scope, and an autoreleased object created off the main thread has no defined
/// point at which it is drained.
/// </para>
/// </remarks>
internal static unsafe partial class ObjC
{
    /// <summary>The Objective-C runtime. Present on every macOS since 10.0.</summary>
    private const string LibObjC = "/usr/lib/libobjc.dylib";

    /// <summary><c>NSVariableStatusItemLength</c>: size the item to its content.</summary>
    internal const double VariableStatusItemLength = -1d;

    /// <summary><c>NSEventTypeLeftMouseUp</c>.</summary>
    internal const long EventTypeLeftMouseUp = 2;

    /// <summary><c>NSEventTypeRightMouseUp</c>.</summary>
    internal const long EventTypeRightMouseUp = 4;

    /// <summary><c>NSEventMaskLeftMouseUp</c>, which is <c>1 &lt;&lt; NSEventTypeLeftMouseUp</c>.</summary>
    internal const nuint EventMaskLeftMouseUp = 1 << 2;

    /// <summary><c>NSEventMaskRightMouseUp</c>.</summary>
    internal const nuint EventMaskRightMouseUp = 1 << 4;

    /// <summary><c>NSEventModifierFlagControl</c>. A control-click is a secondary click.</summary>
    internal const nuint EventModifierFlagControl = 1 << 18;

    /// <summary><c>NSControlStateValueOff</c>.</summary>
    internal const long ControlStateOff = 0;

    /// <summary><c>NSControlStateValueOn</c>.</summary>
    internal const long ControlStateOn = 1;

    private static readonly ConcurrentDictionary<string, IntPtr> SelectorCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, IntPtr> ClassCache = new(StringComparer.Ordinal);

    /// <summary>
    /// True only on macOS. Everything below is gated on it, which is what keeps this
    /// assembly — compiled into the Windows build, because there is no macOS-flavoured target
    /// framework to hide it behind — from ever trying to resolve <c>libobjc</c>.
    /// </summary>
    private static readonly bool RuntimeAvailable = OperatingSystem.IsMacOS();

    /// <summary>
    /// Looks up a registered Objective-C class, caching the answer.
    /// </summary>
    /// <param name="name">The class name, for example <c>NSStatusBar</c>.</param>
    /// <returns>
    /// The class, or <see cref="IntPtr.Zero"/> when the runtime does not know it — which is
    /// the normal answer for a class whose framework has not been loaded, and for every
    /// class on a host that is not macOS.
    /// </returns>
    internal static IntPtr Class(string name) =>
        RuntimeAvailable ? ClassCache.GetOrAdd(name, static n => GetClassNative(n)) : IntPtr.Zero;

    /// <summary>Registers a selector, caching the answer.</summary>
    /// <param name="name">The selector name including its colons, for example <c>setTitle:</c>.</param>
    /// <returns>The selector, or <see cref="IntPtr.Zero"/> off macOS.</returns>
    internal static IntPtr Selector(string name) =>
        RuntimeAvailable ? SelectorCache.GetOrAdd(name, static n => RegisterSelector(n)) : IntPtr.Zero;

    /// <summary>
    /// Creates an owned <c>NSString</c> from managed text.
    /// </summary>
    /// <param name="value">The text. Null is treated as empty.</param>
    /// <returns>
    /// A retained <c>NSString</c> the caller releases with <see cref="Release"/>, or
    /// <see cref="IntPtr.Zero"/> when the runtime is unavailable.
    /// </returns>
    internal static IntPtr CreateString(string? value)
    {
        IntPtr cls = Class("NSString");
        if (cls == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr allocated = Send(cls, Selector("alloc"));
        if (allocated == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value ?? string.Empty);
        byte[] utf8 = new byte[byteCount + 1];
        _ = Encoding.UTF8.GetBytes(value ?? string.Empty, utf8);
        utf8[byteCount] = 0;

        fixed (byte* text = utf8)
        {
            return Send(allocated, Selector("initWithUTF8String:"), (IntPtr)text);
        }
    }

    /// <summary>
    /// Reads an <c>NSString</c> back into managed text.
    /// </summary>
    /// <param name="nsString">The string object, or <see cref="IntPtr.Zero"/>.</param>
    /// <returns>The text, or null for a nil string.</returns>
    internal static string? ReadString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
        {
            return null;
        }

        IntPtr utf8 = Send(nsString, Selector("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>Sends <c>retain</c>, ignoring nil.</summary>
    /// <param name="handle">The object.</param>
    /// <returns>The same object, for chaining.</returns>
    internal static IntPtr Retain(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = Send(handle, Selector("retain"));
        }

        return handle;
    }

    /// <summary>Sends <c>release</c>, ignoring nil.</summary>
    /// <param name="handle">The object.</param>
    internal static void Release(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _ = Send(handle, Selector("release"));
        }
    }

    /// <summary>
    /// Reads a rectangle-returning message through whichever entry point this
    /// architecture's ABI requires.
    /// </summary>
    /// <param name="receiver">The object to message.</param>
    /// <param name="selector">A selector returning <c>NSRect</c>.</param>
    /// <returns>
    /// The rectangle, or <see langword="default"/> for a nil receiver. Messaging nil in
    /// Objective-C yields a zeroed return for a struct, so this matches the runtime.
    /// </returns>
    internal static CGRect SendRect(IntPtr receiver, IntPtr selector)
    {
        if (receiver == IntPtr.Zero)
        {
            return default;
        }

        // See the class remarks: x86-64 returns a 32-byte struct through a hidden pointer
        // and needs the _stret entry point; ARM64 returns it in v0-v3 and has no _stret.
        return RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? SendRectStret(receiver, selector)
            : SendRectDirect(receiver, selector);
    }

    [LibraryImport(LibObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetClassNative(string name);

    [LibraryImport(LibObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RegisterSelector(string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr AllocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [LibraryImport(LibObjC, EntryPoint = "objc_registerClassPair")]
    internal static partial void RegisterClassPair(IntPtr cls);

    [LibraryImport(LibObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial byte AddMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string typeEncoding);

    /// <summary>Sends a message taking no arguments and returning an object or integer.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The returned pointer, or nil for a nil receiver.</returns>
    internal static IntPtr Send(IntPtr receiver, IntPtr selector) =>
        receiver == IntPtr.Zero ? IntPtr.Zero : SendNative(receiver, selector);

    /// <summary>Sends a message taking one pointer argument.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="argument">The argument.</param>
    /// <returns>The returned pointer, or nil for a nil receiver.</returns>
    internal static IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr argument) =>
        receiver == IntPtr.Zero ? IntPtr.Zero : SendNative(receiver, selector, argument);

    /// <summary>Sends a message taking two pointer arguments.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="first">First argument.</param>
    /// <param name="second">Second argument.</param>
    /// <returns>The returned pointer, or nil for a nil receiver.</returns>
    internal static IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second) =>
        receiver == IntPtr.Zero ? IntPtr.Zero : SendNative(receiver, selector, first, second);

    /// <summary>Sends a message taking three pointer arguments.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="first">First argument.</param>
    /// <param name="second">Second argument.</param>
    /// <param name="third">Third argument.</param>
    /// <returns>The returned pointer, or nil for a nil receiver.</returns>
    internal static IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second, IntPtr third) =>
        receiver == IntPtr.Zero ? IntPtr.Zero : SendNative(receiver, selector, first, second, third);

    /// <summary>Sends a message whose single argument is a <c>CGFloat</c>.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="argument">The argument.</param>
    /// <returns>The returned pointer, or nil for a nil receiver.</returns>
    internal static IntPtr SendDoubleArgument(IntPtr receiver, IntPtr selector, double argument) =>
        receiver == IntPtr.Zero ? IntPtr.Zero : SendDoubleArgumentNative(receiver, selector, argument);

    /// <summary>Sends a message whose single argument is an <c>NSUInteger</c> index.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="argument">The index.</param>
    /// <returns>The returned pointer, or nil for a nil receiver.</returns>
    internal static IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint argument) =>
        receiver == IntPtr.Zero ? IntPtr.Zero : SendIndexNative(receiver, selector, argument);

    /// <summary>Sends a message returning <c>BOOL</c>.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>Non-zero for YES; zero for a nil receiver.</returns>
    internal static byte SendBool(IntPtr receiver, IntPtr selector) =>
        receiver == IntPtr.Zero ? (byte)0 : SendBoolNative(receiver, selector);

    /// <summary>Sends a message taking one pointer argument and returning <c>BOOL</c>.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="argument">The argument, for example the selector <c>respondsToSelector:</c> asks about.</param>
    /// <returns>Non-zero for YES; zero for a nil receiver.</returns>
    internal static byte SendBool(IntPtr receiver, IntPtr selector, IntPtr argument) =>
        receiver == IntPtr.Zero ? (byte)0 : SendBoolArgumentNative(receiver, selector, argument);

    /// <summary>Sends a message whose single argument is a <c>BOOL</c>.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="argument">1 for YES, 0 for NO.</param>
    internal static void SendVoidBool(IntPtr receiver, IntPtr selector, byte argument)
    {
        if (receiver != IntPtr.Zero)
        {
            SendVoidBoolNative(receiver, selector, argument);
        }
    }

    /// <summary>Sends a message returning <c>NSInteger</c>.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The integer, or zero for a nil receiver.</returns>
    internal static long SendLong(IntPtr receiver, IntPtr selector) =>
        receiver == IntPtr.Zero ? 0 : SendLongNative(receiver, selector);

    /// <summary>Sends a message returning <c>NSUInteger</c>.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The integer, or zero for a nil receiver.</returns>
    internal static nuint SendUnsignedLong(IntPtr receiver, IntPtr selector) =>
        receiver == IntPtr.Zero ? 0 : SendUnsignedLongNative(receiver, selector);

    /// <summary>Sends a message whose single argument is an <c>NSInteger</c>.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="argument">The argument.</param>
    internal static void SendVoidLong(IntPtr receiver, IntPtr selector, long argument)
    {
        if (receiver != IntPtr.Zero)
        {
            SendVoidLongNative(receiver, selector, argument);
        }
    }

    /// <summary>Sends a message whose single argument is an <c>NSUInteger</c> bit mask.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="argument">The mask.</param>
    internal static void SendVoidMask(IntPtr receiver, IntPtr selector, nuint argument)
    {
        if (receiver != IntPtr.Zero)
        {
            SendVoidMaskNative(receiver, selector, argument);
        }
    }

    /// <summary>Sends a message returning <c>CGFloat</c>.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The value, or zero for a nil receiver.</returns>
    internal static double SendDouble(IntPtr receiver, IntPtr selector) =>
        receiver == IntPtr.Zero ? 0d : SendDoubleNative(receiver, selector);

    /// <summary>Sends a message whose single argument is an <c>NSSize</c>.</summary>
    /// <param name="receiver">The object or class to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="size">The size.</param>
    internal static void SendVoidSize(IntPtr receiver, IntPtr selector, CGSize size)
    {
        if (receiver != IntPtr.Zero)
        {
            SendVoidSizeNative(receiver, selector, size);
        }
    }

    /// <summary>
    /// Sends <c>performSelectorOnMainThread:withObject:waitUntilDone:</c>.
    /// </summary>
    /// <param name="receiver">The object to message.</param>
    /// <param name="selector">Always <c>performSelectorOnMainThread:withObject:waitUntilDone:</c>.</param>
    /// <param name="action">The selector to run on the main thread.</param>
    /// <param name="argument">The argument to pass it, normally nil.</param>
    /// <param name="waitUntilDone">1 to block until the main thread has run it.</param>
    internal static void SendPerformOnMainThread(
        IntPtr receiver, IntPtr selector, IntPtr action, IntPtr argument, byte waitUntilDone)
    {
        if (receiver != IntPtr.Zero)
        {
            SendPerformOnMainThreadNative(receiver, selector, action, argument, waitUntilDone);
        }
    }

    /// <summary>
    /// Sends <c>addObserver:selector:name:object:</c> to a notification centre.
    /// </summary>
    /// <param name="receiver">The notification centre.</param>
    /// <param name="selector">Always <c>addObserver:selector:name:object:</c>.</param>
    /// <param name="observer">The observing object.</param>
    /// <param name="action">The selector it should receive.</param>
    /// <param name="name">The notification name.</param>
    /// <param name="sender">The sender to filter on, normally nil.</param>
    internal static void SendAddObserver(
        IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr action, IntPtr name, IntPtr sender)
    {
        if (receiver != IntPtr.Zero)
        {
            SendAddObserverNative(receiver, selector, observer, action, name, sender);
        }
    }

    /// <summary>
    /// Sends <c>requestAuthorizationWithOptions:completionHandler:</c>.
    /// </summary>
    /// <param name="receiver">The notification centre.</param>
    /// <param name="selector">Always <c>requestAuthorizationWithOptions:completionHandler:</c>.</param>
    /// <param name="options">The <c>UNAuthorizationOptions</c> mask.</param>
    /// <param name="completionHandler">A block. Never nil; the parameter is declared non-null.</param>
    internal static void SendRequestAuthorization(
        IntPtr receiver, IntPtr selector, nuint options, IntPtr completionHandler)
    {
        if (receiver != IntPtr.Zero && completionHandler != IntPtr.Zero)
        {
            SendRequestAuthorizationNative(receiver, selector, options, completionHandler);
        }
    }

    /// <summary>
    /// Sends a message taking a single out-<c>NSError</c> parameter and returning <c>BOOL</c>,
    /// which is the shape of <c>SMAppService</c>'s register and unregister methods.
    /// </summary>
    /// <param name="receiver">The object to message.</param>
    /// <param name="selector">The selector.</param>
    /// <param name="error">An <c>NSError**</c>, or <see cref="IntPtr.Zero"/> to discard it.</param>
    /// <returns>Non-zero for YES; zero for a nil receiver.</returns>
    internal static byte SendBoolWithError(IntPtr receiver, IntPtr selector, IntPtr error) =>
        receiver == IntPtr.Zero ? (byte)0 : SendBoolWithErrorNative(receiver, selector, error);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendNative(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendNative(IntPtr receiver, IntPtr selector, IntPtr argument);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendNative(IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendNative(
        IntPtr receiver, IntPtr selector, IntPtr first, IntPtr second, IntPtr third);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendDoubleArgumentNative(IntPtr receiver, IntPtr selector, double argument);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendIndexNative(IntPtr receiver, IntPtr selector, nuint argument);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBoolNative(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBoolArgumentNative(IntPtr receiver, IntPtr selector, IntPtr argument);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendVoidBoolNative(IntPtr receiver, IntPtr selector, byte argument);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial long SendLongNative(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial nuint SendUnsignedLongNative(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendVoidLongNative(IntPtr receiver, IntPtr selector, long argument);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendVoidMaskNative(IntPtr receiver, IntPtr selector, nuint argument);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial double SendDoubleNative(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendVoidSizeNative(IntPtr receiver, IntPtr selector, CGSize size);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendPerformOnMainThreadNative(
        IntPtr receiver, IntPtr selector, IntPtr action, IntPtr argument, byte waitUntilDone);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendAddObserverNative(
        IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr action, IntPtr name, IntPtr sender);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendRequestAuthorizationNative(
        IntPtr receiver, IntPtr selector, nuint options, IntPtr completionHandler);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial byte SendBoolWithErrorNative(IntPtr receiver, IntPtr selector, IntPtr error);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial CGRect SendRectDirect(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend_stret")]
    private static partial CGRect SendRectStret(IntPtr receiver, IntPtr selector);
}
