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
    private readonly CodexLiveResult _result;

    public StubAppServerClient(CodexLiveResult result, bool isAvailable = true)
    {
        _result = result;
        IsAvailable = isAvailable;
    }

    public bool IsAvailable { get; }

    public int CallCount { get; private set; }

    public Task<CodexLiveResult> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        CallCount++;
        return Task.FromResult(_result);
    }
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
/// A clock that does not move, so staleness assertions are exact.
/// </summary>
internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
