using Altim.Core.Settings;
using Altim.UI.Services;

namespace Altim.UI.Tests.Fakes;

/// <summary>
/// A settings store the tests read back from.
/// </summary>
/// <remarks>
/// Saving is announced through a task, because the settings page saves itself: a change writes
/// without anything being awaited, and a test that polled for it would be a flaky test.
/// </remarks>
internal sealed class FakeSettingsStore : ISettingsStore
{
    private TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The settings a read returns.</summary>
    public AltimSettings Stored { get; set; } = AltimSettings.Default;

    /// <summary>The settings the last write carried, or null when nothing has been written.</summary>
    public AltimSettings? Saved { get; private set; }

    /// <summary>How many writes have happened.</summary>
    public int Writes { get; private set; }

    /// <summary>Whether the next call throws.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Completes when the next write lands.</summary>
    public Task Written => _written.Task;

    /// <summary>Arms <see cref="Written"/> for the write after this point.</summary>
    public void ExpectWrite() => _written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc />
    public ValueTask<AltimSettings> GetAsync(CancellationToken ct) =>
        Failure is { } failure
            ? ValueTask.FromException<AltimSettings>(failure)
            : ValueTask.FromResult(Stored);

    /// <inheritdoc />
    public ValueTask SaveAsync(AltimSettings settings, CancellationToken ct)
    {
        if (Failure is { } failure)
        {
            return ValueTask.FromException(failure);
        }

        Saved = settings;
        Stored = settings;
        Writes++;
        _written.TrySetResult();
        return ValueTask.CompletedTask;
    }
}
