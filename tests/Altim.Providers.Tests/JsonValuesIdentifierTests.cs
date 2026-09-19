using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Altim.Core.Models;
using Altim.Providers.Codex.Limits;
using Altim.Providers.Io;
using Xunit;

namespace Altim.Providers.Tests;

/// <summary>
/// The one string path out of a vendor's files, tested as the privacy boundary it is.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JsonValues.ReadIdentifier"/> is the only accessor in the solution that hands a
/// reader a string it did not author, and the files it reads hold prompts, source code,
/// command output and repository URLs. Loosening the character set or raising the cap would
/// leave every other test in this suite green while arbitrary vendor text reached the screen
/// and the settings database, because the shape tests only check that a reader has no
/// <em>unexpected</em> string property, never that a permitted one is safe.
/// </para>
/// <para>
/// So the rules are pinned against literals here, not against
/// <see cref="JsonValues.MaxIdentifierLength"/> and not against the implementation's own
/// vocabulary. A test that derives its expectation from the constant it is guarding moves
/// when the constant moves and proves nothing.
/// </para>
/// <para>
/// Two admissions are wider than the class documentation reads. They are pinned as gaps at
/// the bottom of this file rather than folded into the general cases.
/// </para>
/// </remarks>
public sealed class JsonValuesIdentifierTests
{
    private static string? Read(string json, string name, string? alternateName = null)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return JsonValues.ReadIdentifier(document.RootElement, name, alternateName);
    }

    /// <summary>
    /// Puts <paramref name="text"/> through a real JSON document as a string value, letting
    /// the writer do the escaping so the test cannot get its own input wrong.
    /// </summary>
    private static string? ReadAsJsonString(string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", text);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return JsonValues.ReadIdentifier(document.RootElement, "id");
    }

    private static IReadOnlyList<CodexLimitWindow> ParseWindows(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return CodexRateLimitParser.ReadWindows(document.RootElement);
    }

    /// <summary>
    /// Builds the app-server shape with <paramref name="familyKey"/> as the vendor-supplied
    /// map key and reads the windows back, letting the writer do the escaping.
    /// </summary>
    private static IReadOnlyList<CodexLimitWindow> ParseFamilyMap(string familyKey)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("rateLimitsByLimitId");
            writer.WriteStartObject(familyKey);
            writer.WriteStartObject("primary");
            writer.WriteNumber("usedPercent", 22);
            writer.WriteNumber("windowDurationMins", 300);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return CodexRateLimitParser.ReadWindows(document.RootElement);
    }

    private static string Describe(int codePoint) =>
        "U+" + codePoint.ToString("X4", CultureInfo.InvariantCulture);

    // ---- The length cap -----------------------------------------------------------

    [Fact]
    public void TheCapIsNinetySixCharacters() =>
        // Spelled out so raising the budget is a visible decision. Every length assertion
        // below uses a literal for the same reason.
        Assert.Equal(96, JsonValues.MaxIdentifierLength);

    [Fact]
    public void NinetySixPermittedCharactersAreAdmittedWhole()
    {
        string atCap = new('a', 96);

        Assert.True(JsonValues.IsIdentifier(atCap));
        Assert.Equal(atCap, ReadAsJsonString(atCap));
    }

    [Fact]
    public void NinetySevenCharactersAreRejectedRatherThanTruncated()
    {
        string onePast = new('a', 97);

        Assert.False(JsonValues.IsIdentifier(onePast));

        // The whole field goes unavailable. It does not arrive clipped to ninety-six, which
        // would put the first ninety-six characters of whatever this was on screen.
        Assert.Null(ReadAsJsonString(onePast));
    }

    [Fact]
    public void AnExtremelyLongSingleTokenIsRejectedWhole()
    {
        // A base64 blob, a minified line or a pasted key: one token, no separators, so the
        // character set alone would admit it and only the cap refuses it.
        string blob = new('A', 20_000);

        Assert.False(JsonValues.IsIdentifier(blob));
        Assert.Null(ReadAsJsonString(blob));
    }

    // ---- The character set --------------------------------------------------------

    [Fact]
    public void ExactlyTheDocumentedAsciiSetIsPermittedAndNothingElseIs()
    {
        // The expectation is spelled out rather than borrowed from the implementation's
        // char.IsAsciiLetterOrDigit, so adding one character to the permitted punctuation
        // fails here instead of moving both sides together.
        const string Punctuation = "-_.+@[]";

        var admittedButShouldNotBe = new List<string>();
        var refusedButShouldNotBe = new List<string>();

        for (int codePoint = 0; codePoint <= 0x7F; codePoint++)
        {
            char c = (char)codePoint;
            bool documented = (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9')
                || Punctuation.Contains(c, StringComparison.Ordinal);

            // Leading, interior and trailing: an implementation that inspected only the
            // middle of the string would otherwise pass.
            string[] placements = [c + "xx", "x" + c + "x", "xx" + c];
            foreach (string candidate in placements)
            {
                bool admitted = JsonValues.IsIdentifier(candidate);
                if (admitted && !documented)
                {
                    admittedButShouldNotBe.Add(Describe(codePoint));
                    break;
                }

                if (!admitted && documented)
                {
                    refusedButShouldNotBe.Add(Describe(codePoint));
                    break;
                }
            }
        }

        Assert.Equal(string.Empty, string.Join(", ", admittedButShouldNotBe));
        Assert.Equal(string.Empty, string.Join(", ", refusedButShouldNotBe));
    }

    [Fact]
    public void NoCharacterAboveAsciiIsEverPermitted()
    {
        // char.IsAsciiLetterOrDigit is the whole reason a Cyrillic homoglyph, a combining
        // mark, a right-to-left override and an emoji are all refused. Swapping it for
        // char.IsLetterOrDigit admits most of this range and breaks nothing else.
        var permitted = new List<string>();

        for (int codePoint = 0x80; codePoint <= 0xFFFF && permitted.Count < 8; codePoint++)
        {
            if (JsonValues.IsIdentifier("x" + (char)codePoint + "x"))
            {
                permitted.Add(Describe(codePoint));
            }
        }

        Assert.Equal(string.Empty, string.Join(", ", permitted));
    }

    [Fact]
    public void TheNamedUnicodeHazardsAreAllRefused()
    {
        // Written as escapes so this test's own display name stays ASCII: the Windows
        // console cannot encode most of them.
        Assert.False(JsonValues.IsIdentifier("cafe\u0301"));        // combining acute accent
        Assert.False(JsonValues.IsIdentifier("\u0301"));            // a combining mark alone
        Assert.False(JsonValues.IsIdentifier("\u202Egnp.exe"));     // right-to-left override
        Assert.False(JsonValues.IsIdentifier("\u200Fcodex"));       // right-to-left mark
        Assert.False(JsonValues.IsIdentifier("\u0441odex"));        // Cyrillic es: looks like codex
        Assert.False(JsonValues.IsIdentifier("\u200Bcodex"));       // zero-width space
        Assert.False(JsonValues.IsIdentifier("\u00A0codex"));       // non-breaking space
        Assert.False(JsonValues.IsIdentifier("\uFEFFcodex"));       // byte order mark
        Assert.False(JsonValues.IsIdentifier("\uD83D\uDE00"));      // emoji, a surrogate pair

        Assert.Null(ReadAsJsonString("\u202Egnp.exe"));
        Assert.Null(ReadAsJsonString("\u0441odex"));
        Assert.Null(ReadAsJsonString("cafe\u0301"));
    }

    [Fact]
    public void EveryControlCharacterAndEveryFormOfAsciiWhitespaceIsRefused()
    {
        foreach (char c in "\0\a\b\t\n\v\f\r\u001B\u007F ")
        {
            string described = Describe(c);
            Assert.False(JsonValues.IsIdentifier(c + "codex"), described + " leading");
            Assert.False(JsonValues.IsIdentifier("co" + c + "dex"), described + " interior");
            Assert.False(JsonValues.IsIdentifier("codex" + c), described + " trailing");
            Assert.False(JsonValues.IsIdentifier(c + string.Empty), described + " alone");
        }
    }

    [Fact]
    public void AControlCharacterWrittenAsAJsonEscapeIsDecodedBeforeTheCheckAndRefused()
    {
        // The guard sees the decoded character, which is the only version that matters: a
        // reader that checked the raw JSON text would pass every one of these.
        Assert.Null(Read("""{"id":"codex\u0000drop"}""", "id"));
        Assert.Null(Read("""{"id":"codex\nprompt"}""", "id"));
        Assert.Null(Read("""{"id":"codex\tprompt"}""", "id"));
        Assert.Null(Read("""{"id":"codex\r\nprompt"}""", "id"));
        Assert.Null(Read("""{"id":"say \"hello\""}""", "id"));
        Assert.Null(Read("""{"id":"\u202Ecodex"}""", "id"));
        Assert.Null(Read("""{"id":"C:\\Users\\someone"}""", "id"));

        // And the escape spelled out literally is just a backslash, which is refused too.
        Assert.Null(Read("""{"id":"codex\\u0000drop"}""", "id"));
    }

    [Theory]
    [InlineData("C:\\Users\\someone\\projects\\private-client")]
    [InlineData("C:/Users/someone/projects/private-client")]
    [InlineData("/home/someone/projects/private-client")]
    [InlineData("./src/Altim.Core/Settings/AltimSettings.cs")]
    [InlineData("~/statements/downloads/2026-q3.pdf")]
    [InlineData("\\\\server\\share\\private-client")]
    [InlineData("https://github.com/someone/private-client")]
    [InlineData("git@github.com:someone/private-client.git")]
    [InlineData("codex.example.com/v1/usage?account=42")]
    [InlineData("Summarise the auth flow, then fix the failing test.")]
    [InlineData("read the file and tell me what it does")]
    [InlineData("dotnet test Altim.sln -c Release")]
    [InlineData("say \"hello\"")]
    [InlineData("back\\slash")]
    [InlineData("codex:300")]
    [InlineData(" codex")]
    [InlineData("codex ")]
    [InlineData("a b")]
    [InlineData("{\"json\":1}")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("50%")]
    [InlineData("a/b")]
    [InlineData("a#b")]
    [InlineData("a?b")]
    [InlineData("a&b")]
    [InlineData("a=b")]
    [InlineData("a,b")]
    [InlineData("a;b")]
    [InlineData("a'b")]
    [InlineData("a`b")]
    [InlineData("a|b")]
    [InlineData("a(b)")]
    [InlineData("a$b")]
    [InlineData("a!b")]
    [InlineData("a*b")]
    [InlineData("a~b")]
    [InlineData("a^b")]
    public void RefusesTheShapesTheseFilesActuallyContain(string text)
    {
        Assert.False(JsonValues.IsIdentifier(text));
        Assert.Null(ReadAsJsonString(text));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("premium")]
    [InlineData("codex_bengalfox")]
    [InlineData("prolite")]
    [InlineData("claude-opus-4-5-20260101")]
    [InlineData("claude-opus-5[1m]")]
    [InlineData("gpt-5.3-codex-high")]
    [InlineData("msg_01ABCdefGHIjkl")]
    [InlineData("req_1")]
    [InlineData("0199aaaa-bbbb-cccc-dddd-eeeeffff0000")]
    [InlineData("2026-09-19")]
    [InlineData("a+b")]
    [InlineData("A")]
    [InlineData("5")]
    public void AdmitsTheIdentifierShapesTheReadersActuallyNeed(string text)
    {
        // The other half of the guard: tightening it until a real model id or the [1m]
        // long-context suffix stops arriving turns a working meter into an unavailable one.
        Assert.True(JsonValues.IsIdentifier(text));

        // Byte-identical. Nothing is trimmed, lower-cased or otherwise normalised.
        Assert.Equal(text, ReadAsJsonString(text));
    }

    // ---- Absent, empty and the wrong JSON kind ------------------------------------

    [Fact]
    public void NullEmptyAndWhitespaceOnlyAreAllRefused()
    {
        Assert.False(JsonValues.IsIdentifier(null));
        Assert.False(JsonValues.IsIdentifier(string.Empty));
        Assert.False(JsonValues.IsIdentifier(" "));
        Assert.False(JsonValues.IsIdentifier("        "));

        Assert.Null(Read("""{"id":""}""", "id"));
        Assert.Null(Read("""{"id":"   "}""", "id"));
    }

    [Theory]
    [InlineData("""{"other":"codex"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"id":null}""")]
    [InlineData("""{"id":42}""")]
    [InlineData("""{"id":42.5}""")]
    [InlineData("""{"id":-1}""")]
    [InlineData("""{"id":true}""")]
    [InlineData("""{"id":false}""")]
    [InlineData("""{"id":{"nested":"codex"}}""")]
    [InlineData("""{"id":{}}""")]
    [InlineData("""{"id":["codex"]}""")]
    [InlineData("""{"id":[]}""")]
    public void AnAbsentValueOrTheWrongJsonKindReadsAsUnavailable(string json) =>
        Assert.Null(Read(json, "id"));

    [Fact]
    public void ARootThatIsNotAnObjectReadsAsUnavailableRatherThanThrowing()
    {
        // JsonElement.TryGetProperty throws InvalidOperationException on anything that is
        // not an object, so the ValueKind guard is what keeps a malformed transcript line
        // from becoming an exception inside a reader.
        Assert.Null(Read("""["codex"]""", "id"));
        Assert.Null(Read("\"codex\"", "id"));
        Assert.Null(Read("42", "id"));
        Assert.Null(Read("true", "id"));
        Assert.Null(Read("null", "id"));
    }

    // ---- The alternate-name overload ----------------------------------------------

    [Fact]
    public void ThePreferredSpellingWinsWhenBothArePresent() =>
        Assert.Equal("snake", Read("""{"limit_name":"snake","limitName":"camel"}""", "limit_name", "limitName"));

    [Fact]
    public void TheAlternateSpellingIsUsedWhenThePreferredIsAbsent() =>
        Assert.Equal("camel", Read("""{"limitName":"camel"}""", "limit_name", "limitName"));

    [Fact]
    public void AJsonNullUnderThePreferredSpellingFallsThroughToTheAlternate() =>
        // Both dialects really do write one spelling as null beside the other.
        Assert.Equal("camel", Read("""{"limit_name":null,"limitName":"camel"}""", "limit_name", "limitName"));

    [Fact]
    public void ARejectedPreferredValueIsNotRescuedByTheAlternate()
    {
        // The preferred spelling is present and is not null, so it is the value that gets
        // read; it then fails the guard and the field is unavailable. The alternate is
        // never consulted. That is the safe direction, and pinning it means a later "try
        // the other spelling too" change has to be a deliberate decision.
        Assert.Null(Read("""{"limit_name":"a sentence of prose","limitName":"codex"}""", "limit_name", "limitName"));
        Assert.Null(Read("""{"limit_name":42,"limitName":"codex"}""", "limit_name", "limitName"));
    }

    [Fact]
    public void WithNoAlternateNameAMissingPreferredSpellingIsSimplyUnavailable() =>
        Assert.Null(Read("""{"limitName":"camel"}""", "limit_name"));

    [Fact]
    public void BothSpellingsAreGuardedTheSameWay()
    {
        Assert.Null(Read("""{"limitName":"C:\\work\\private-client"}""", "limit_name", "limitName"));
        Assert.Null(Read("""{"limit_name":"C:\\work\\private-client"}""", "limit_name", "limitName"));
    }

    // ---- End to end, where the guard is load-bearing ------------------------------

    [Fact]
    public void ProseInTheVendorsLimitNameNeverReachesTheVisibleMetricLabel()
    {
        // CodexRateLimitParserTests.ANonDefaultFamilyIsNamedInItsLabel proves that text
        // from limit_name reaches UsageMetric.Label through CodexMetricFactory.BuildLabel.
        // This is that same path, with the field holding what these files really contain.
        IReadOnlyList<CodexLimitWindow> windows = ParseWindows(
            """
            {
              "rateLimitsByLimitId": {
                "codex_bengalfox": {
                  "limit_name": "Summarise the auth flow in src/Auth/Login.cs and fix it",
                  "primary": { "usedPercent": 22, "windowDurationMins": 300,
                               "limit_name": "read C:\\work\\private-client" }
                }
              }
            }
            """);

        CodexLimitWindow window = Assert.Single(windows);
        Assert.Null(window.LimitName);

        UsageMetric metric = Assert.Single(CodexMetricFactory.Build(windows, MetricConfidence.BestEffort));

        Assert.Equal("codex_bengalfox 5 hour", metric.Label);
        Assert.Equal("codex_bengalfox:300", metric.Key);

        string[] fragments = ["Summarise", "auth flow", "src", "Login", ".cs", "private-client", "/", "\\"];
        foreach (string fragment in fragments)
        {
            Assert.DoesNotContain(fragment, metric.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(fragment, metric.Key, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("read the file and summarise it for me")]
    [InlineData("a sentence with a comma, and a full stop.")]
    [InlineData("C:\\work\\private-client")]
    [InlineData("/home/someone/projects/private-client")]
    [InlineData("https://github.com/someone/private-client")]
    public void VendorTextUsedAsAFamilyMapKeyNeverReachesTheVisibleMetricLabel(string familyKey)
    {
        // The map key is vendor-supplied too, and it is the other way into the label: a key
        // that passes the guard becomes the LimitId, and a non-default LimitId is printed.
        // Each case here is refused by a different part of the character set, so loosening
        // any one of spaces, backslashes or slashes fails this.
        IReadOnlyList<CodexLimitWindow> windows = ParseFamilyMap(familyKey);

        CodexLimitWindow window = Assert.Single(windows);
        Assert.Equal(CodexRateLimitParser.DefaultLimitId, window.LimitId);
        Assert.Null(window.LimitName);

        UsageMetric metric = Assert.Single(CodexMetricFactory.Build(windows, MetricConfidence.BestEffort));

        Assert.Equal("5 hour", metric.Label);
        Assert.Equal("codex:300", metric.Key);
    }

    // ---- Known gaps, pinned rather than widened -----------------------------------

    [Fact]
    public void KnownGapAnEmailAddressIsAdmittedAndDoesReachTheVisibleLabel()
    {
        // Pinned, not endorsed. "@" and "." are both permitted, so an address is a valid
        // identifier by this guard's rules and arrives on screen intact.
        //
        // The class documentation says the set "excludes whitespace, path separators,
        // quotes and URL punctuation" and that "a file path, a URL or a sentence of prose
        // cannot survive it". An email address is none of those three, and it survives.
        // ParsedResultShapeTests already lists "email" among its content-bearing words, so
        // a property named Email is refused while an email value is not.
        Assert.True(JsonValues.IsIdentifier("someone@example.com"));
        Assert.True(JsonValues.IsIdentifier("someone+tag@example.co.uk"));

        IReadOnlyList<CodexLimitWindow> windows = ParseWindows(
            """
            {
              "rateLimitsByLimitId": {
                "premium": {
                  "limit_name": "someone@example.com",
                  "primary": { "usedPercent": 22, "windowDurationMins": 300 }
                }
              }
            }
            """);

        UsageMetric metric = Assert.Single(CodexMetricFactory.Build(windows, MetricConfidence.BestEffort));

        Assert.Equal("someone@example.com 5 hour", metric.Label);
    }

    [Fact]
    public void KnownGapABareFilenameOrProjectNameIsAdmitted()
    {
        // Pinned, not endorsed. AGENTS.md rule 5 and PRIVACY.md both say filenames and
        // project names never leave the machine. The guard stops a path, because a
        // separator is refused, but the leaf on its own is letters, digits, hyphens and a
        // dot, which is exactly what an identifier looks like.
        //
        // ParsedResultShapeTests.AStatusLineStateCannotCarryTheWorkingDirectoryItWasGiven
        // passes because the reader never reads project_dir, not because the guard would
        // refuse its value. A value read from the wrong key is the case this guard exists
        // for, and a leaf segment is what it would find.
        Assert.True(JsonValues.IsIdentifier("AltimRuntime.cs"));
        Assert.True(JsonValues.IsIdentifier("private-client"));
        Assert.True(JsonValues.IsIdentifier("bank-statements"));
        Assert.True(JsonValues.IsIdentifier("2026-q3-statement.pdf"));
        Assert.True(JsonValues.IsIdentifier(".env"));
        Assert.True(JsonValues.IsIdentifier(".."));

        Assert.Equal("private-client", ReadAsJsonString("private-client"));
    }
}
