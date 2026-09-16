using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Altim.Platform.MacOS.Interop;

/// <summary>
/// What an <see cref="ObjCCallbackTarget"/> forwards to. Implemented by the tray host and
/// the platform service so the unmanaged callbacks stay static and allocation free.
/// </summary>
internal interface IObjCCallbackSink
{
    /// <summary>The status bar button was clicked.</summary>
    /// <param name="sender">The <c>NSStatusBarButton</c>.</param>
    void OnStatusItemClicked(IntPtr sender);

    /// <summary>A native menu entry was picked.</summary>
    /// <param name="sender">The <c>NSMenuItem</c>, whose <c>tag</c> carries the index.</param>
    void OnMenuItemClicked(IntPtr sender);

    /// <summary>A notification arrived from one of the observed notification centres.</summary>
    /// <param name="notification">The <c>NSNotification</c>.</param>
    void OnNotification(IntPtr notification);
}

/// <summary>
/// One Objective-C object that receives every callback Altim needs: the status button's
/// action, the menu entries' action, notification-centre observations, and the
/// main-thread pump.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified.</b> Nothing in this file has ever run. It is written from the
/// Objective-C runtime's documented behaviour for <c>objc_allocateClassPair</c>,
/// <c>class_addMethod</c> and
/// <c>performSelectorOnMainThread:withObject:waitUntilDone:</c>, not from a test on a Mac.
/// </para>
/// <para>
/// <b>Why a dynamic class rather than a block.</b> Target/action and
/// <c>addObserver:selector:name:object:</c> both want a real Objective-C object and a
/// selector, not a block. One class is registered per process, lazily, and each host gets
/// its own instance of it; the instance pointer is the key back to the managed object, so
/// two platform services in one process do not cross wires.
/// </para>
/// <para>
/// <b>Why the callbacks are <see cref="UnmanagedCallersOnlyAttribute"/>.</b> A reverse
/// P/Invoke through a delegate would need a marshalling stub and a rooted delegate to stay
/// alive; a static function pointer needs neither and is what Native AOT emits directly.
/// The cost is that the callbacks cannot capture, hence the lookup table.
/// </para>
/// <para>
/// <b>The main-thread pump.</b> <c>NSStatusItem</c> and everything hanging off it must be
/// touched on the main thread. Work posted from elsewhere is queued and the main thread is
/// poked with <c>performSelectorOnMainThread:</c>, which needs a running main run loop —
/// under Avalonia that is <c>NSApplication</c>'s. If no run loop is running the work simply
/// waits; it is never run on the wrong thread.
/// </para>
/// </remarks>
internal sealed unsafe class ObjCCallbackTarget : IDisposable
{
    private const string ClassName = "AltimObjCCallbackTarget";

    /// <summary>Objective-C type encoding for <c>void method(id self, SEL _cmd, id sender)</c>.</summary>
    private const string VoidWithObjectEncoding = "v@:@";

    private static readonly ConcurrentDictionary<IntPtr, ObjCCallbackTarget> Targets = new();
    private static readonly Lock ClassGate = new();
    private static IntPtr _registeredClass;

    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly IObjCCallbackSink _sink;
    private readonly IntPtr _instance;
    private bool _disposed;

    private ObjCCallbackTarget(IntPtr instance, IObjCCallbackSink sink)
    {
        _instance = instance;
        _sink = sink;
    }

    /// <summary>The Objective-C object to hand to target/action and observer APIs.</summary>
    internal IntPtr Handle => _instance;

    /// <summary>The selector the status button's action should be set to.</summary>
    internal static IntPtr StatusClickedSelector => ObjC.Selector("altimStatusClicked:");

    /// <summary>The selector every native menu entry's action is set to.</summary>
    internal static IntPtr MenuClickedSelector => ObjC.Selector("altimMenuClicked:");

    /// <summary>The selector the observed notification centres deliver to.</summary>
    internal static IntPtr NotificationSelector => ObjC.Selector("altimNotification:");

    /// <summary>True when the caller is already on the Cocoa main thread.</summary>
    /// <remarks>
    /// False when the Objective-C runtime is not present at all, which sends every caller
    /// down the queued path — safe, rather than merely wrong.
    /// </remarks>
    internal static bool IsMainThread
    {
        get
        {
            IntPtr thread = ObjC.Class("NSThread");
            return thread != IntPtr.Zero && ObjC.SendBool(thread, ObjC.Selector("isMainThread")) != 0;
        }
    }

    /// <summary>
    /// Creates a target bound to a sink.
    /// </summary>
    /// <param name="sink">The managed object the callbacks are forwarded to.</param>
    /// <returns>
    /// The target, or <see langword="null"/> when the Objective-C runtime is unavailable or
    /// the class could not be registered. A null is reported by the caller as "no tray",
    /// never thrown.
    /// </returns>
    internal static ObjCCallbackTarget? TryCreate(IObjCCallbackSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        IntPtr cls = TryRegisterClass();
        if (cls == IntPtr.Zero)
        {
            return null;
        }

        IntPtr allocated = ObjC.Send(cls, ObjC.Selector("alloc"));
        if (allocated == IntPtr.Zero)
        {
            return null;
        }

        IntPtr instance = ObjC.Send(allocated, ObjC.Selector("init"));
        if (instance == IntPtr.Zero)
        {
            return null;
        }

        var target = new ObjCCallbackTarget(instance, sink);
        Targets[instance] = target;
        return target;
    }

    /// <summary>
    /// Runs work on the Cocoa main thread, inline when the caller is already there.
    /// </summary>
    /// <param name="work">
    /// The work. Anything it throws is swallowed: an exception unwinding into the
    /// Objective-C runtime is undefined behaviour and in practice ends the process.
    /// </param>
    internal void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (_disposed)
        {
            return;
        }

        if (IsMainThread)
        {
            RunSafely(work);
            return;
        }

        _pending.Enqueue(work);
        ObjC.SendPerformOnMainThread(
            _instance,
            ObjC.Selector("performSelectorOnMainThread:withObject:waitUntilDone:"),
            ObjC.Selector("altimDrainQueue:"),
            IntPtr.Zero,
            0);
    }

    /// <summary>
    /// Runs work on the Cocoa main thread and completes when it has run.
    /// </summary>
    /// <param name="work">The work.</param>
    /// <param name="ct">Cancels before the work is queued.</param>
    /// <returns>
    /// A task that completes once the work has run, or immediately when the target is
    /// already disposed.
    /// </returns>
    internal ValueTask InvokeAsync(Action work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        if (IsMainThread)
        {
            RunSafely(work);
            return ValueTask.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                work();
            }
            finally
            {
                _ = completion.TrySetResult();
            }
        });

        return new ValueTask(completion.Task);
    }

    /// <summary>Unbinds the target and releases the Objective-C instance.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = Targets.TryRemove(_instance, out _);
        ObjC.Release(_instance);
    }

    [UnmanagedCallersOnly]
    private static void StatusClicked(IntPtr self, IntPtr command, IntPtr sender)
    {
        if (Targets.TryGetValue(self, out ObjCCallbackTarget? target))
        {
            IObjCCallbackSink sink = target._sink;
            RunSafely(() => sink.OnStatusItemClicked(sender));
        }
    }

    [UnmanagedCallersOnly]
    private static void MenuClicked(IntPtr self, IntPtr command, IntPtr sender)
    {
        if (Targets.TryGetValue(self, out ObjCCallbackTarget? target))
        {
            IObjCCallbackSink sink = target._sink;
            RunSafely(() => sink.OnMenuItemClicked(sender));
        }
    }

    [UnmanagedCallersOnly]
    private static void NotificationPosted(IntPtr self, IntPtr command, IntPtr notification)
    {
        if (Targets.TryGetValue(self, out ObjCCallbackTarget? target))
        {
            IObjCCallbackSink sink = target._sink;
            RunSafely(() => sink.OnNotification(notification));
        }
    }

    [UnmanagedCallersOnly]
    private static void DrainQueue(IntPtr self, IntPtr command, IntPtr argument)
    {
        if (Targets.TryGetValue(self, out ObjCCallbackTarget? target))
        {
            target.Drain();
        }
    }

    private static void RunSafely(Action work)
    {
        try
        {
            work();
        }
        catch (Exception)
        {
            // Deliberately swallowed: this frame was entered from Objective-C.
        }
    }

    private static IntPtr TryRegisterClass()
    {
        lock (ClassGate)
        {
            if (_registeredClass != IntPtr.Zero)
            {
                return _registeredClass;
            }

            IntPtr root = ObjC.Class("NSObject");
            if (root == IntPtr.Zero)
            {
                // Not macOS, or the runtime is not loaded. The caller degrades to no tray.
                return IntPtr.Zero;
            }

            IntPtr cls = ObjC.AllocateClassPair(root, ClassName, 0);
            if (cls == IntPtr.Zero)
            {
                // Nil means the name is already taken, which happens when an earlier host in
                // this process registered it. Look it up instead of failing.
                _registeredClass = ObjC.Class(ClassName);
                return _registeredClass;
            }

            _ = ObjC.AddMethod(
                cls,
                ObjC.Selector("altimStatusClicked:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&StatusClicked,
                VoidWithObjectEncoding);
            _ = ObjC.AddMethod(
                cls,
                ObjC.Selector("altimMenuClicked:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&MenuClicked,
                VoidWithObjectEncoding);
            _ = ObjC.AddMethod(
                cls,
                ObjC.Selector("altimNotification:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&NotificationPosted,
                VoidWithObjectEncoding);
            _ = ObjC.AddMethod(
                cls,
                ObjC.Selector("altimDrainQueue:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&DrainQueue,
                VoidWithObjectEncoding);

            ObjC.RegisterClassPair(cls);
            _registeredClass = cls;
            return cls;
        }
    }

    private void Drain()
    {
        while (_pending.TryDequeue(out Action? work))
        {
            RunSafely(work);
        }
    }
}
