using System.Text.Json;

namespace Altim.Core.Updates;

/// <summary>
/// Reads the one field Altim wants out of a GitHub release document.
/// </summary>
/// <remarks>
/// <para>
/// Separate from whatever fetched the bytes, and pure, because this is the part that can
/// be wrong in a way nobody would notice: a reader that picked up an asset's name instead
/// of the release's tag would find a version on most documents and the wrong one on all of
/// them.
/// </para>
/// <para>
/// A forward-only reader rather than a deserialiser, for the same reason the provider
/// readers use one. It reads the single field it wants and never materialises the rest of
/// somebody else's document, and it needs no reflection, so the ahead-of-time build behaves
/// identically to the framework-dependent one.
/// </para>
/// </remarks>
public static class ReleaseFeed
{
    /// <summary>
    /// Pulls <c>tag_name</c> from a single release document.
    /// </summary>
    /// <param name="utf8">The response body.</param>
    /// <returns>
    /// The version, or null when the field is absent, is not a string, is not a version,
    /// or the document is not JSON at all.
    /// </returns>
    /// <remarks>
    /// Only the top-level <c>tag_name</c>. A release's assets carry names of their own and
    /// a nested match would read one of those as the version — which is the specific
    /// mistake a shape-blind search for the key would make on every real response, because
    /// GitHub nests an <c>uploader</c> object inside every asset.
    /// </remarks>
    public static ReleaseVersion? ReadLatestTag(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        int depth = -1;

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        depth = depth < 0 ? 0 : depth + 1;
                        continue;

                    case JsonTokenType.EndObject:
                        depth--;
                        continue;

                    case JsonTokenType.PropertyName
                        when depth == 0 && reader.ValueTextEquals("tag_name"u8):
                        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                        {
                            return null;
                        }

                        return ReleaseVersion.TryParse(reader.GetString());

                    default:
                        continue;
                }
            }
        }
        catch (JsonException)
        {
            // Truncated or malformed. The caller has learned that it does not know of a
            // newer version, which is the safe answer and the same one an absent field
            // gives.
            return null;
        }

        return null;
    }
}
