using Altim.Core.Abstractions;
using Altim.Core.Accessibility;
using Altim.Core.Models;
using Altim.Platform.MacOS.Interop;

namespace Altim.Platform.MacOS;

/// <summary>
/// The macOS implementation of <see cref="IMotionPreferenceService"/>: System Settings &gt;
/// Accessibility &gt; Display &gt; Reduce motion, read once and then only when macOS says it
/// moved.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unverified.</b> No part of this has run on macOS. It is written from Apple's
/// documentation for <c>NSWorkspace</c>, and every failure degrades to
/// <see cref="MotionPreference.Unknown"/> rather than to an exception.
/// </para>
/// <para>
/// <b>The value is <c>NSWorkspace.shared.accessibilityDisplayShouldReduceMotion</c>.</b> It
/// is a <c>BOOL</c> and it is the property Apple's own frameworks consult, so it already
/// accounts for however the user arrived at the setting.
/// </para>
/// <para>
/// <b>The change notification is <c>NSWorkspace</c>'s, which means the workspace centre.</b>
/// <see cref="MacOSPlatformService"/> exists partly to record that macOS has three
/// notification centres and that observing the wrong one is accepted in silence and then
/// receives nothing at all.
/// <c>NSWorkspaceAccessibilityDisplayOptionsDidChangeNotification</c> is posted by
/// <c>NSWorkspace</c>, so it is observed on <c>NSWorkspace.shared.notificationCenter</c> —
/// not the default centre, and not the distributed centre that carries the appearance
/// broadcast.
/// </para>
/// <para>
/// <b>The notification covers more than motion</b>, because it is the one macOS posts for
/// every accessibility display option: reduce transparency, increase contrast and
/// differentiate without colour arrive here too. The property is therefore read again on
/// each one and <see cref="Changed"/> is raised only when the answer differs, so the other
/// options cost a <c>BOOL</c> read and nothing else.
/// </para>
/// <para>
/// <b>Unknown is reported honestly.</b> A host with no Objective-C runtime, an
/// <c>NSWorkspace</c> that could not be reached, or a system too old to carry the property
/// all answer <see cref="MotionPreference.Unknown"/>. The last is checked with
/// <c>respondsToSelector:</c> rather than assumed, because messaging a selector an object
/// does not implement raises an Objective-C exception, which ends the process.
/// </para>
/// </remarks>
public sealed class MacOSMotionPreferenceService : IMotionPreferenceService, IObjCCallbackSink, IDisposable
{
    /// <summary>Posted by <c>NSWorkspace</c> when any accessibility display option changes.</summary>
    private const string AccessibilityOptionsChangedNotification =
        "NSWorkspaceAccessibilityDisplayOptionsDidChangeNotification";

    private readonly ObjCCallbackTarget? _observer;

    private volatile MotionPreference _current;
    private bool _disposed;

    /// <summary>Reads the preference and starts observing the workspace centre.</summary>
    public MacOSMotionPreferenceService()
    {
        _observer = OperatingSystem.IsMacOS() ? ObjCCallbackTarget.TryCreate(this) : null;
        _current = Read();

        RegisterObserver();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public MotionPreference Current => _current;

    /// <summary>
    /// True when the Objective-C runtime was reachable <em>and</em> <c>NSWorkspace</c>
    /// answered the question, which is what the composition root reports on.
    /// </summary>
    /// <remarks>
    /// False means the preference was <see cref="MotionPreference.Unknown"/> at start-up, and
    /// with no observer there is nothing that could ever move it off that.
    /// </remarks>
    public bool IsSupported => _observer is not null && _current != MotionPreference.Unknown;

    /// <summary>Removes the observation and releases the callback object.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        RemoveObserver();
        _observer?.Dispose();
    }

    void IObjCCallbackSink.OnStatusItemClicked(IntPtr sender)
    {
        // This service owns no status item; the callback object exists only to observe.
    }

    void IObjCCallbackSink.OnMenuItemClicked(IntPtr sender)
    {
        // As above.
    }

    void IObjCCallbackSink.OnNotification(IntPtr notification)
    {
        if (_disposed || notification == IntPtr.Zero)
        {
            return;
        }

        string? name = ObjC.ReadString(ObjC.Send(notification, ObjC.Selector("name")));
        if (!string.Equals(name, AccessibilityOptionsChangedNotification, StringComparison.Ordinal))
        {
            return;
        }

        MotionPreference preference = Read();
        if (preference == _current)
        {
            return;
        }

        _current = preference;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Asks <c>NSWorkspace</c> whether the user has asked for reduced motion.
    /// </summary>
    /// <returns>
    /// The preference, or <see cref="MotionPreference.Unknown"/> when there is no workspace
    /// to ask or it does not carry the property.
    /// </returns>
    private static MotionPreference Read()
    {
        IntPtr workspace = SharedWorkspace();
        if (workspace == IntPtr.Zero)
        {
            return MotionPreference.Unknown;
        }

        IntPtr selector = ObjC.Selector("accessibilityDisplayShouldReduceMotion");
        if (selector == IntPtr.Zero ||
            ObjC.SendBool(workspace, ObjC.Selector("respondsToSelector:"), selector) == 0)
        {
            return MotionPreference.Unknown;
        }

        // The one place the sense is flipped: macOS asks whether motion should be reduced,
        // where Windows and the desktop portal both ask whether animations are wanted.
        bool shouldReduce = ObjC.SendBool(workspace, selector) != 0;
        return MotionPolicy.FromAnimationsEnabled(animationsEnabled: !shouldReduce);
    }

    private static IntPtr SharedWorkspace()
    {
        IntPtr workspaceClass = ObjC.Class("NSWorkspace");
        return workspaceClass == IntPtr.Zero
            ? IntPtr.Zero
            : ObjC.Send(workspaceClass, ObjC.Selector("sharedWorkspace"));
    }

    private static IntPtr WorkspaceNotificationCentre()
    {
        IntPtr workspace = SharedWorkspace();
        return workspace == IntPtr.Zero
            ? IntPtr.Zero
            : ObjC.Send(workspace, ObjC.Selector("notificationCenter"));
    }

    private void RegisterObserver()
    {
        ObjCCallbackTarget? observer = _observer;
        if (observer is null)
        {
            return;
        }

        observer.Post(() =>
        {
            IntPtr centre = WorkspaceNotificationCentre();
            if (centre == IntPtr.Zero)
            {
                return;
            }

            IntPtr name = ObjC.CreateString(AccessibilityOptionsChangedNotification);
            try
            {
                ObjC.SendAddObserver(
                    centre,
                    ObjC.Selector("addObserver:selector:name:object:"),
                    observer.Handle,
                    ObjCCallbackTarget.NotificationSelector,
                    name,
                    IntPtr.Zero);
            }
            finally
            {
                ObjC.Release(name);
            }
        });
    }

    private void RemoveObserver()
    {
        ObjCCallbackTarget? observer = _observer;
        if (observer is null)
        {
            return;
        }

        IntPtr handle = observer.Handle;

        // Synchronous, for the reason MacOSPlatformService gives: the observer must be
        // unregistered before it is released, or a later notification reaches a freed object.
        try
        {
            observer.InvokeAsync(() =>
            {
                IntPtr centre = WorkspaceNotificationCentre();
                if (centre != IntPtr.Zero && handle != IntPtr.Zero)
                {
                    _ = ObjC.Send(centre, ObjC.Selector("removeObserver:"), handle);
                }
            }).AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The main run loop has already stopped; the registration dies with it.
        }
    }
}
