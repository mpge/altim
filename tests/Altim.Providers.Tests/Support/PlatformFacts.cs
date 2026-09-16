using System.Runtime.CompilerServices;
using Xunit;

namespace Altim.Providers.Tests.Support;

/// <summary>
/// A test of behaviour that only exists on Windows, skipped elsewhere rather than deleted.
/// </summary>
/// <remarks>
/// Command resolution is the case that needs this: on Windows a command name is not a file
/// name and the shell appends extensions from PATHEXT, while everywhere else the name is the
/// file. Asserting one rule on both platforms is how the suite came to be green on a developer
/// machine and red in CI.
/// </remarks>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows-only: a command name is not a file name there.";
        }
    }
}

/// <summary>
/// A test of behaviour that only exists away from Windows, skipped on it.
/// </summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Not Windows: a command name is exactly a file name there.";
        }
    }
}
