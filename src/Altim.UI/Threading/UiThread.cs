using Avalonia.Threading;

namespace Altim.UI.Threading;

/// <summary>
/// The one place a view model touches the dispatcher.
/// </summary>
/// <remarks>
/// Providers raise <c>UsageChanged</c> from whatever thread took the reading, so a handler that
/// writes to a bound property has to come back to the dispatcher first. Work already on the
/// dispatcher runs inline, which keeps the common case free of a queued frame.
/// </remarks>
internal static class UiThread
{
    /// <summary>Runs an action on the dispatcher thread, inline when already on it.</summary>
    /// <param name="action">The work to run.</param>
    public static void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }
}
