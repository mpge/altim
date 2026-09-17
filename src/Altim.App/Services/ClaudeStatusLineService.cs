using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Altim.App.Diagnostics;
using Altim.Core.Abstractions;
using Altim.Providers.Claude.StatusLine;

namespace Altim.App.Services;

/// <summary>
/// The composition root's half of the status-line switch: <see cref="StatusLineInstaller"/>
/// behind <see cref="IStatusLineService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The adapter exists because <c>Altim.UI</c> must not reference a provider assembly. What it
/// adds on top of the installer is the three things only this project knows: where this
/// executable is, that the call must not run on the dispatcher thread, and that an outcome is
/// worth nothing unless it is read back.
/// </para>
/// <para>
/// <b>Every path reads the state back with a dry run.</b> The installer reports what it did,
/// but what the interface has to show is what the settings file now holds, and the two are
/// not the same thing after a refusal or a failed write. The dry run is free and cannot
/// change anything, which is exactly the property wanted here.
/// </para>
/// <para>
/// Failures are states, not exceptions. A settings file that will not parse and a disk that
/// will not take a write both arrive at the settings page as a sentence the user can read.
/// </para>
/// </remarks>
internal sealed class ClaudeStatusLineService : IStatusLineService
{
    private readonly string? _command;
    private readonly string? _configRootOverride;

    /// <summary>
    /// Creates the service over this process's own executable.
    /// </summary>
    /// <param name="configRootOverride">
    /// The Claude Code config root to write to, or null to resolve one. Nothing in the
    /// application passes this; it is the seam tests use to stay off a real settings file.
    /// </param>
    public ClaudeStatusLineService(string? configRootOverride = null)
        : this(ResolveCommand(), configRootOverride)
    {
    }

    /// <summary>
    /// Creates the service over a given command line.
    /// </summary>
    /// <param name="command">
    /// The command Claude Code should run, or null when this process cannot say where it
    /// lives. It must contain <see cref="StatusLineInstaller.CommandMarker"/>.
    /// </param>
    /// <param name="configRootOverride">The config root to write to, or null to resolve one.</param>
    public ClaudeStatusLineService(string? command, string? configRootOverride)
    {
        _command = command;
        _configRootOverride = configRootOverride;
    }

    /// <inheritdoc />
    public ValueTask<StatusLineInstallState> InspectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Inspect());
    }

    /// <inheritdoc />
    public ValueTask<StatusLineInstallState> SetAsync(bool install, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!TryBuildInstaller(out StatusLineInstaller? installer))
        {
            return ValueTask.FromResult(StatusLineInstallState.NoConfiguration);
        }

        try
        {
            // The one call in Altim that writes to a file the user owns, and the only place
            // dryRun is false.
            StatusLineInstallResult result = install
                ? installer.Install(dryRun: false)
                : installer.Revert(dryRun: false);

            if (result.Outcome is StatusLineInstallOutcome.WriteFailed or StatusLineInstallOutcome.SettingsUnreadable)
            {
                // The path is the user's own settings file, so it is not logged. The type of
                // failure is all the log is allowed to carry and all it needs.
                AltimLog.Write("statusline", install
                    ? "The status-line entry could not be written."
                    : "The status-line entry could not be removed.");
            }
        }
        catch (Exception ex)
        {
            AltimLog.Write("statusline", "Changing the status-line entry failed", ex);
            return ValueTask.FromResult(StatusLineInstallState.Failed);
        }

        // Not the outcome of the write: what the file holds now.
        return ValueTask.FromResult(Inspect());
    }

    /// <summary>
    /// This executable plus the marker argument, quoted, or null when the running program
    /// cannot say where it lives.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.ProcessPath"/> is the shipped case: a Velopack install has its
    /// own host executable and that is what Claude Code should run. A development run through
    /// <c>dotnet</c> is the other case, and it needs the assembly named as well, because the
    /// host on its own would start the SDK rather than Altim. Nothing is hard coded either
    /// way, so a moved or renamed install registers the path it actually has.
    /// </remarks>
    public static string? ResolveCommand()
    {
        string? host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host))
        {
            return null;
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return Quote(host) + " " + ClaudeStatusLineHelper.Argument;
        }

        // Built from the base directory and the assembly's simple name rather than read off
        // Assembly.Location, which is empty in a single-file publish and is an analyser error
        // to ask for in an AOT-ready project.
        string? name = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        string assembly = Path.Combine(AppContext.BaseDirectory, name + ".dll");
        return File.Exists(assembly)
            ? Quote(host) + " " + Quote(assembly) + " " + ClaudeStatusLineHelper.Argument
            : null;
    }

    private static string Quote(string path) => "\"" + path + "\"";

    private StatusLineInstallState Inspect()
    {
        if (!TryBuildInstaller(out StatusLineInstaller? installer))
        {
            return StatusLineInstallState.NoConfiguration;
        }

        try
        {
            // A dry run plans and writes nothing, so this is a read however often it is
            // called.
            StatusLineInstallResult result = installer.Install();

            return result.Outcome switch
            {
                StatusLineInstallOutcome.WouldInstall => StatusLineInstallState.NotInstalled,
                StatusLineInstallOutcome.AlreadyInstalled => StatusLineInstallState.Installed,
                StatusLineInstallOutcome.RefusedExistingStatusLine => StatusLineInstallState.AnotherStatusLine,
                StatusLineInstallOutcome.NoConfigDirectory => StatusLineInstallState.NoConfiguration,
                _ => StatusLineInstallState.Failed,
            };
        }
        catch (Exception ex)
        {
            AltimLog.Write("statusline", "Reading the status-line entry failed", ex);
            return StatusLineInstallState.Failed;
        }
    }

    private bool TryBuildInstaller([NotNullWhen(true)] out StatusLineInstaller? installer)
    {
        installer = null;

        if (_command is null)
        {
            return false;
        }

        try
        {
            installer = new StatusLineInstaller(_command, _configRootOverride);
            return true;
        }
        catch (ArgumentException ex)
        {
            AltimLog.Write("statusline", "The status-line command could not be built", ex);
            return false;
        }
    }
}
