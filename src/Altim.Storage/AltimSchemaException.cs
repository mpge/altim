using System.Globalization;

namespace Altim.Storage;

/// <summary>
/// The database on disk cannot be used by this build. Thrown instead of touching a
/// file whose shape is not understood, because a half-understood write is how a
/// history database gets corrupted.
/// </summary>
/// <remarks>
/// Messages carry version numbers only. No file path, project name or other content
/// from the user's machine appears in them, so the exception is safe to show.
/// </remarks>
public sealed class AltimSchemaException : InvalidOperationException
{
    /// <summary>
    /// Creates an exception with a generic message.
    /// </summary>
    public AltimSchemaException()
        : base("The usage database has a schema this build cannot use.")
    {
    }

    /// <summary>
    /// Creates an exception with an explicit message.
    /// </summary>
    /// <param name="message">The message. Must not contain a path or any user content.</param>
    public AltimSchemaException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an exception with an explicit message and an underlying cause.
    /// </summary>
    /// <param name="message">The message. Must not contain a path or any user content.</param>
    /// <param name="innerException">The cause.</param>
    public AltimSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private AltimSchemaException(string message, int foundVersion, int supportedVersion)
        : base(message)
    {
        FoundVersion = foundVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>
    /// The schema version found in the file, when the failure was a version mismatch.
    /// <see langword="null"/> for every other failure.
    /// </summary>
    public int? FoundVersion { get; }

    /// <summary>
    /// The newest schema version this build knows how to apply, when the failure was a
    /// version mismatch. <see langword="null"/> for every other failure.
    /// </summary>
    public int? SupportedVersion { get; }

    /// <summary>
    /// Builds the exception raised when a database was written by a newer build. Altim
    /// refuses to open it: migrations are forward only, so an older build has no way to
    /// interpret, let alone preserve, whatever the newer one added.
    /// </summary>
    /// <param name="foundVersion">The version recorded in the file.</param>
    /// <param name="supportedVersion">The newest version this build can apply.</param>
    /// <returns>The exception to throw.</returns>
    public static AltimSchemaException FromNewerVersion(int foundVersion, int supportedVersion)
    {
        string message = string.Create(
            CultureInfo.InvariantCulture,
            $"The usage database is at schema version {foundVersion}, but this build of Altim "
            + $"supports version {supportedVersion}. Update Altim to open it.");

        return new AltimSchemaException(message, foundVersion, supportedVersion);
    }
}
