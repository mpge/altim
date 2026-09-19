using System.Text.RegularExpressions;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// Nothing in the product reads an exception's message.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExceptionSummaryTests"/> proves that <c>ExceptionSummary.Describe</c> emits a type
/// name and nothing else. This proves that nothing goes round it. The two together are the whole
/// argument: there is one function that turns an exception into text, it is proven to carry only
/// types, and no other code under <c>src/</c> reaches for the sentence the framework already wrote.
/// </para>
/// <para>
/// The second half is the part a behaviour test cannot supply, because the defect was never a
/// decision. A catch block reached for <c>ex.Message</c> because it was right there and it read
/// well, and seven of them accumulated. Two of the seven reached <c>altim.log</c>: a Windows
/// notification registration failure wrote the platform's own <c>Class not registered
/// (0x80040154 (REGDB_E_CLASSNOTREG))</c> into the file on every single start, and a refused D-Bus
/// delivery would have written the session bus socket path, which names the user's account id. The
/// other five sat in fields that nothing logged yet, one wiring line away from the same file. A
/// behaviour test can only assert about the paths somebody thought to exercise, and the field that
/// has not been wired up is exactly the one it misses.
/// </para>
/// <para>
/// <c>altim.log</c> is the file people attach to bug reports, so this is the promise PRIVACY.md
/// makes about paths and file contents, asserted in the only place it can actually be kept.
/// </para>
/// </remarks>
public sealed partial class LogPrivacyShapeTests
{
    /// <summary>
    /// Receivers of a <c>.Message</c> that are not exceptions, and why each one is safe.
    /// </summary>
    /// <remarks>
    /// <c>message</c> is a <c>WindowMessage</c>, whose <c>Message</c> is the Win32 message id and
    /// therefore a <c>uint</c>. <c>GeminiLineKind</c> is an enum with a <c>Message</c> member.
    /// <c>Exception</c> occurs only in prose: the doc comments on <c>AltimLog</c> and
    /// <c>ExceptionSummary</c> name the property they exist to keep out, and have to spell it in
    /// full to do that. Adding a name here is meant to be a decision somebody makes on purpose.
    /// </remarks>
    private static readonly string[] SafeMessageReceivers = ["message", "GeminiLineKind", "Exception"];

    /// <summary>An exception's message, spelled the way all seven of the defects spelled it.</summary>
    [GeneratedRegex(@"\b(ex|error|e)\.Message\b")]
    private static partial Regex ExceptionMessageRead { get; }

    /// <summary>Any read of a <c>Message</c> member, whatever the receiver happens to be called.</summary>
    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\.Message\b")]
    private static partial Regex AnyMessageRead { get; }

    /// <summary>The operating system's own localised error sentence.</summary>
    [GeneratedRegex(@"\bGet(?:Last)?PInvokeErrorMessage\b")]
    private static partial Regex NativeErrorSentence { get; }

    /// <summary>
    /// The direct form. Every one of the seven sites this test was written for was spelled
    /// <c>ex.Message</c>, and so is the one somebody adds next.
    /// </summary>
    /// <remarks>
    /// This also guards <c>ExceptionSummary</c> itself, whose parameter is named <c>error</c>: the
    /// obvious way to break <c>Describe</c> is to have it append the message, and that is
    /// <c>error.Message</c>, which is caught here.
    /// </remarks>
    [Fact]
    public void NoSourceFileReadsAnExceptionsMessage()
    {
        string root = SourceDirectory();
        List<string> offences = [];
        int scanned = 0;

        foreach (string file in ProductSource(root))
        {
            scanned++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (ExceptionMessageRead.IsMatch(lines[i]))
                {
                    offences.Add($"{Path.GetRelativePath(root, file)}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        AssertTheSourceTreeWasActuallyRead(scanned);
        Assert.True(
            offences.Count == 0,
            "An exception message is written by the framework or by another vendor's library, and "
                + "names whatever file, directory or socket the call failed on. Use "
                + "ExceptionSummary.Describe(ex), which carries the type:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// The same rule, reached from the other side, so that renaming the variable does not defeat
    /// it. <c>exception.Message</c>, <c>failure.Message</c> and <c>err.Message</c> are all the same
    /// defect and none of them is spelled <c>ex</c>.
    /// </summary>
    [Fact]
    public void EveryMessageMemberReadInTheProductIsAKnownSafeOne()
    {
        string root = SourceDirectory();
        List<string> offences = [];
        int scanned = 0;

        foreach (string file in ProductSource(root))
        {
            scanned++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in AnyMessageRead.Matches(lines[i]))
                {
                    string receiver = match.Groups[1].Value;
                    if (!SafeMessageReceivers.Contains(receiver, StringComparer.Ordinal))
                    {
                        offences.Add(
                            $"{Path.GetRelativePath(root, file)}:{i + 1} reads {receiver}.Message - {lines[i].Trim()}");
                    }
                }
            }
        }

        AssertTheSourceTreeWasActuallyRead(scanned);
        Assert.True(
            offences.Count == 0,
            "A Message member is either an exception's, which may not be read, or something else, "
                + "which belongs on SafeMessageReceivers with a note saying what it is:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// The same defect wearing a different coat. <c>Marshal.GetLastPInvokeErrorMessage()</c> is not
    /// an exception, but it returns the operating system's own localised sentence, and three of
    /// them were being interpolated into the message of the exception that became the Windows
    /// tray host's start-up error. The numeric code says the same thing, is Altim's to write, and
    /// reads the same in every language.
    /// </summary>
    [Fact]
    public void NoSourceFileBorrowsTheOperatingSystemsOwnErrorSentence()
    {
        string root = SourceDirectory();
        List<string> offences = [];
        int scanned = 0;

        foreach (string file in ProductSource(root))
        {
            scanned++;
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (NativeErrorSentence.IsMatch(lines[i]))
                {
                    offences.Add($"{Path.GetRelativePath(root, file)}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        AssertTheSourceTreeWasActuallyRead(scanned);
        Assert.True(
            offences.Count == 0,
            "The P/Invoke error message is the operating system's sentence, not Altim's. Use "
                + "Marshal.GetLastWin32Error() and write the number:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, offences));
    }

    /// <summary>
    /// A shape test that finds no files passes. This is what stops that being mistaken for the
    /// product being clean.
    /// </summary>
    /// <param name="scanned">How many files the test actually opened.</param>
    private static void AssertTheSourceTreeWasActuallyRead(int scanned) =>
        Assert.True(
            scanned > 200,
            $"Only {scanned} files were scanned. The source tree was not found, so this test proved nothing.");

    /// <summary>
    /// Every C# file Altim ships, which is everything under <c>src/</c> that is not build output.
    /// </summary>
    /// <param name="root">The <c>src</c> directory.</param>
    /// <returns>Absolute paths, sorted.</returns>
    /// <remarks>
    /// <c>obj</c> holds generated files - <c>AssemblyInfo</c>, the regex source generator's own
    /// output - that nobody edits and that would report offences nobody can fix.
    /// </remarks>
    private static IEnumerable<string> ProductSource(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Order();

    /// <summary>
    /// Walks up from the test binary to <c>src</c>. The runner's working directory is not the
    /// repository, and a relative path from it would break the moment the suite ran elsewhere.
    /// </summary>
    /// <returns>The absolute path of <c>src</c>.</returns>
    private static string SourceDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src");
            if (File.Exists(Path.Combine(candidate, "Altim.Core", "Altim.Core.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Could not find src above {AppContext.BaseDirectory}.");
        return string.Empty;
    }
}
