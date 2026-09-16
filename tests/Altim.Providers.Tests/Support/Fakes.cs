using Altim.Core.Abstractions;
using Altim.Core.Models;
using Altim.Providers.Cli;
using Altim.Providers.Codex.AppServer;

namespace Altim.Providers.Tests.Support;

/// <summary>
/// An <see cref="ICliRunner"/> that answers from a table instead of starting a process.
/// </summary>
internal sealed class FakeCliRunner : ICliRunner
{
    private readonly Dictionary<string, CliRunResult> _responses = new(StringComparer.Ordinal);

    /// <summary>Whether the command is considered installed.</summary>
    public bool CommandExists { get; set; } = true;

    /// <summary>Every argument list this runner was asked for, joined by spaces.</summary>
    public List<string> Invocations { get; } = [];

    public void Respond(string argumentKey, CliRunResult result) => _responses[argumentKey] = result;

    public void RespondWithJson(string argumentKey, string json) =>
        Respond(argumentKey, new CliRunResult(CliRunOutcome.Completed, 0, json));

    public bool Exists(string command) => CommandExists;

    public Task<CliRunResult> RunAsync(string command, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        string key = string.Join(" ", arguments);
        Invocations.Add(key);

        if (!CommandExists)
        {
            return Task.FromResult(CliRunResult.NotDetected);
        }

        return Task.FromResult(_responses.TryGetValue(key, out CliRunResult? result) ? result : CliRunResult.Failed);
    }
}

/// <summary>
/// An <see cref="ICodexAppServerClient"/> that returns a canned outcome.
/// </summary>
internal sealed class StubAppServerClient : ICodexAppServerClient
{
    public StubAppServerClient(CodexLiveResult result, bool isAvailable = true)
    {
        Result = result;
        IsAvailable = isAvailable;
    }

    public bool IsAvailable { get; }

    public int CallCount { get; private set; }

    /// <summary>What the next call answers, so a test can make a working call start failing.</summary>
    public CodexLiveResult Result { get; set; }

    public Task<CodexLiveResult> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(Result);
    }
}

/// <summary>
/// An <see cref="IRefreshGate"/> a test opens and closes by hand.
/// </summary>
internal sealed class SwitchableRefreshGate : IRefreshGate
{
    public bool IsOpen { get; set; } = true;

    /// <summary>Every key the gate was asked about, in order.</summary>
    public List<string> Requested { get; } = [];

    public bool TryAcquire(string key)
    {
        Requested.Add(key);
        return IsOpen;
    }

    public TimeSpan TimeUntilAvailable(string key) => IsOpen ? TimeSpan.Zero : TimeSpan.FromSeconds(60);
}

/// <summary>
/// An <see cref="IProcessMonitor"/> that reports a fixed list.
/// </summary>
internal sealed class FakeProcessMonitor : IProcessMonitor
{
    private readonly IReadOnlyList<DetectedProcess> _processes;

    public FakeProcessMonitor(params DetectedProcess[] processes) => _processes = processes;

    public ValueTask<IReadOnlyList<DetectedProcess>> ScanAsync(CancellationToken ct) =>
        ValueTask.FromResult(_processes);
}

/// <summary>
/// An <see cref="IProcessMonitor"/> that fails the way a denied or broken process table
/// does, so the provider's own failure path can be exercised.
/// </summary>
internal sealed class ThrowingProcessMonitor : IProcessMonitor
{
    public ValueTask<IReadOnlyList<DetectedProcess>> ScanAsync(CancellationToken ct) =>
        throw new IOException("the process table could not be read from C:\\Users\\someone\\secret-project");
}

/// <summary>
/// A clock that does not move, so staleness assertions are exact.
/// </summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>
/// A clock a test moves forward on purpose, for behaviour that turns on elapsed time.
/// </summary>
internal sealed class MovableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public MovableTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}

/// <summary>
/// The user's live network permission, as a test can move it.
/// </summary>
/// <remarks>
/// The production implementation is <c>Altim.App.Services.LiveNetworkPolicy</c>, which the
/// composition root keeps in step with the stored setting. Nothing here is a stand-in for
/// behaviour: the point of the seam is that the flag can change while a provider is alive,
/// so a test of it has to be able to change the flag while the provider is alive.
/// </remarks>
internal sealed class MutableNetworkPolicy : INetworkPolicy
{
    /// <inheritdoc />
    public bool AllowsNetworkCalls { get; set; } = true;
}
