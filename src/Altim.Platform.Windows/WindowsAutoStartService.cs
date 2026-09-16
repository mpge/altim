#if WINDOWS

using Altim.Core.Abstractions;
using Microsoft.Win32;

namespace Altim.Platform.Windows;

/// <summary>
/// Start with Windows, written to the per-user <c>Run</c> key and reported from
/// <c>StartupApproved</c>.
/// </summary>
/// <remarks>
/// <para>
/// The two keys are not the same question. <c>Run</c> is the request: it is what
/// Altim writes. <c>StartupApproved\Run</c> is the answer: it is where Windows records
/// the user's own decision, made in Settings ▸ Startup apps or on Task Manager's
/// Startup tab, and that decision wins. An application that reports its own
/// <c>Run</c> value back to the user will tell them it starts with Windows while Task
/// Manager has it switched off, which is exactly the bug this class exists to avoid.
/// </para>
/// <para>
/// <b>Altim never writes <c>StartupApproved</c>.</b> Writing it would let the
/// application silently overturn a decision the user made in the operating system's
/// own UI. Enabling start-up writes the <c>Run</c> value and nothing else, so
/// <see cref="IsEnabledAsync"/> can still report false immediately afterwards when
/// the user has previously disabled the entry, which is correct and is why the
/// interface tells callers to read the state back.
/// </para>
/// <para>
/// Nothing here needs elevation: both keys are under <c>HKEY_CURRENT_USER</c>.
/// </para>
/// </remarks>
public sealed class WindowsAutoStartService : IAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string StartupApprovedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly string _valueName;
    private readonly string? _executablePath;
    private readonly string _arguments;

    /// <summary>
    /// Creates the service for the running executable.
    /// </summary>
    /// <param name="valueName">
    /// The registry value name, which is also the name Task Manager and Settings show.
    /// </param>
    /// <param name="executablePath">
    /// The executable to register, or <see langword="null"/> for the running process.
    /// </param>
    /// <param name="arguments">
    /// Arguments appended after the quoted path. Empty by default.
    /// </param>
    public WindowsAutoStartService(string valueName = "Altim", string? executablePath = null, string arguments = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ArgumentNullException.ThrowIfNull(arguments);

        _valueName = valueName;
        _executablePath = executablePath ?? Environment.ProcessPath;
        _arguments = arguments;
    }

    /// <summary>
    /// The exact string written to the <c>Run</c> value: the executable path in
    /// quotes, so a path containing a space is not parsed as a command plus argument.
    /// </summary>
    public string CommandLine => BuildCommandLine();

    /// <inheritdoc />
    public ValueTask<bool> IsEnabledAsync()
    {
        // No Run value means no registration at all, whatever StartupApproved holds:
        // a stale approval record for a removed entry is common and means nothing.
        if (ReadRunValue() is null)
        {
            return ValueTask.FromResult(false);
        }

        return ValueTask.FromResult(ReadApproval() ?? true);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(bool on)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (on)
            {
                string command = BuildCommandLine();
                if (command.Length > 0)
                {
                    key.SetValue(_valueName, command, RegistryValueKind.String);
                }
            }
            else if (key.GetValue(_valueName) is not null)
            {
                key.DeleteValue(_valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A locked down hive is not a crash: the caller reads the state back and
            // will see that nothing changed.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reads the current <c>Run</c> value.
    /// </summary>
    /// <returns>The registered command line, or null when the value is absent.</returns>
    public string? ReadRunValue()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(_valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the user's own decision from <c>StartupApproved\Run</c>.
    /// </summary>
    /// <returns>
    /// True when Windows has the entry approved, false when the user switched it off,
    /// and <see langword="null"/> when Windows holds no opinion — which is the case
    /// for an entry that has never been toggled and means "runs".
    /// </returns>
    /// <remarks>
    /// The value is twelve bytes: a flags byte, three bytes of padding and, when the
    /// entry was disabled, the <c>FILETIME</c> at which that happened. Bit 0 of the
    /// flags byte is the disabled bit — Task Manager writes <c>02</c> to enable and
    /// <c>03</c> to disable, and Settings has been seen to write <c>01</c> for an
    /// entry it switched off and <c>06</c> for one it switched back on.
    /// </remarks>
    public bool? ReadApproval()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: false);
            if (key?.GetValue(_valueName) is byte[] { Length: > 0 } data)
            {
                return (data[0] & 0x01) == 0;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Treated the same as an absent value: Windows holds no opinion.
        }

        return null;
    }

    private string BuildCommandLine()
    {
        if (string.IsNullOrEmpty(_executablePath))
        {
            return string.Empty;
        }

        string quoted = "\"" + _executablePath + "\"";
        return _arguments.Length == 0 ? quoted : quoted + " " + _arguments;
    }
}

#endif
