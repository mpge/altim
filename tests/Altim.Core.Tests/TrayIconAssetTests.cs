using System.Buffers.Binary;
using Altim.Platform.Linux;
using Altim.Platform.MacOS;
using Xunit;

namespace Altim.Core.Tests;

/// <summary>
/// The tray icon assets, held against the code that names them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LinuxPlatformLogicTests"/> asserts that the Linux chooser asks for
/// <c>altim-white-32.png</c> and <see cref="MacOSPlatformLogicTests"/> that the macOS one
/// asks for <c>altim-template-20.png</c>. Nothing asserted that either file exists. A size
/// added to one of the arrays with no rendering produced, or a rendering deleted from
/// <c>assets/icons</c>, is invisible to every other test in the repository and shows up as a
/// missing tray icon on a machine nobody is testing on - the one platform the developer is
/// not sitting in front of.
/// </para>
/// <para>
/// Both directions are asserted. Every name a chooser can produce is a file that is there,
/// and every rendering that is there is a name a chooser can produce, so neither an array
/// that grew past the assets nor an asset that outlived its array can pass. The renderings
/// are opened as well as counted: a file can exist, be a placeholder, and leave the tray
/// just as empty.
/// </para>
/// <para>
/// This runs on every platform. The choosers are pure functions over strings and the assets
/// are files in the repository, so the Linux set is checked from Windows and the macOS set
/// from Linux, which is the only arrangement under which any of them is ever checked at all.
/// </para>
/// </remarks>
public sealed class TrayIconAssetTests
{
    /// <summary>The eight bytes every PNG begins with.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Every rendering the Linux chooser can ask for is present, square, and never smaller
    /// than the size in its name.
    /// </summary>
    /// <param name="light">Which glyph: the light one has its own set of sizes.</param>
    /// <remarks>
    /// Not smaller, rather than exactly: the chooser's own documentation says the search
    /// rounds up because "downscaling a larger rendering keeps the glyph's proportions;
    /// upscaling a smaller one throws away the hinting it was drawn with". A rendering
    /// smaller than its name is therefore the defect - it is the one thing the rounding up
    /// cannot save, because the caller has already been told it is getting that size.
    /// <see cref="TheDarkRenderingsAreExactlyTheSizeTheyAreNamed"/> is stricter, for the one
    /// family where the size is a promise to somebody else.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EverySizeTheLinuxChooserCanReturnHasARendering(bool light)
    {
        string directory = IconDirectory();

        foreach (int size in LinuxSizes(light))
        {
            string name = LinuxTrayAssets.FileName(light, size);
            int drawn = SquarePixelsOf(Path.Combine(directory, name), name);

            Assert.True(
                drawn >= size,
                $"{name} is drawn at {drawn} pixels, under the {size} its name claims, so a "
                    + "panel asking for that size is handed something it has to upscale.");
        }
    }

    /// <summary>
    /// Every rendering the macOS chooser can ask for is present, square, and never smaller
    /// than the size in its name.
    /// </summary>
    [Fact]
    public void EverySizeTheMacOSChooserCanReturnHasARendering()
    {
        string directory = IconDirectory();

        Assert.NotEmpty(MacOSTrayAssets.Sizes);
        foreach (int size in MacOSTrayAssets.Sizes)
        {
            string name = MacOSTrayAssets.FileName(size);
            int drawn = SquarePixelsOf(Path.Combine(directory, name), name);

            Assert.True(
                drawn >= size,
                $"{name} is drawn at {drawn} pixels, under the {size} its name claims, so the "
                    + "menu bar is handed something it has to upscale.");
        }
    }

    /// <summary>
    /// The dark renderings are exactly the size their names claim.
    /// </summary>
    /// <remarks>
    /// Stricter than the other two families for a reason outside this repository. The Linux
    /// package installs each one at <c>/usr/share/icons/hicolor/NxN/apps/altim.png</c>, and
    /// in an icon theme the size in that path is a declaration about the file rather than a
    /// label on it: a desktop that reads the theme index picks the directory by size and
    /// draws what it finds without measuring it. The light and template families are never
    /// installed that way - they are read straight out of the application's own directory by
    /// the choosers above - so for those two, being larger than the name is waste rather than
    /// a broken promise. Both of their 512s are in fact drawn at 1024.
    /// </remarks>
    [Fact]
    public void TheDarkRenderingsAreExactlyTheSizeTheyAreNamed()
    {
        string directory = IconDirectory();

        foreach (int size in LinuxSizes(light: false))
        {
            string name = LinuxTrayAssets.FileName(light: false, size);
            Assert.Equal(size, SquarePixelsOf(Path.Combine(directory, name), name));
        }
    }

    /// <summary>
    /// The Windows fallback. The loader builds its icon from the renderings like the others
    /// and reaches for <c>altim.ico</c> when it cannot, which makes that file the last thing
    /// standing between a failed load and an empty notification area.
    /// </summary>
    [Fact]
    public void TheWindowsFallbackIconIsPresent()
    {
        var file = new FileInfo(Path.Combine(IconDirectory(), "altim.ico"));

        Assert.True(file.Exists, $"{file.FullName} is missing, so a failed PNG load has nothing to fall back to.");
        Assert.True(file.Length > 0L, $"{file.Name} is empty.");

        using FileStream stream = file.OpenRead();
        Span<byte> header = stackalloc byte[4];
        Assert.Equal(4, stream.ReadAtLeast(header, 4, throwOnEndOfStream: false));

        // An icon directory: reserved 0, type 1, then the image count.
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(header[..2]));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(header[2..]));
    }

    /// <summary>
    /// The other direction: no rendering sits in <c>assets/icons</c> that no chooser will
    /// ever ask for.
    /// </summary>
    /// <remarks>
    /// A rendering nothing names is either a size somebody meant to add to an array and did
    /// not, or the leftover of one removed from an array and not deleted. Both are the same
    /// defect seen from the other side, and neither is visible anywhere else: the packaging
    /// scripts copy what they are told to copy and say nothing about what they were not.
    /// </remarks>
    [Fact]
    public void NoRenderingIsPresentThatNoChooserWillAskFor()
    {
        string directory = IconDirectory();

        HashSet<string> named = new(StringComparer.Ordinal);
        foreach (bool light in new[] { true, false })
        {
            foreach (int size in LinuxSizes(light))
            {
                _ = named.Add(LinuxTrayAssets.FileName(light, size));
            }
        }

        foreach (int size in MacOSTrayAssets.Sizes)
        {
            _ = named.Add(MacOSTrayAssets.FileName(size));
        }

        List<string> orphans = [];
        foreach (string path in Directory.EnumerateFiles(directory, "altim*.png"))
        {
            string name = Path.GetFileName(path);
            if (!named.Contains(name))
            {
                orphans.Add(name);
            }
        }

        Assert.True(
            orphans.Count == 0,
            "These renderings are in assets/icons and no chooser can ask for them, so they "
                + "ship in every package and reach nothing: " + string.Join(", ", orphans.Order()));
    }

    /// <summary>
    /// The resolvers find the real directory from the real running binary, and hand back the
    /// size they preferred rather than falling back to another one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The assertions above hold the file names against the repository. This holds the walk
    /// the running program actually does: both resolvers climb from the directory the binary
    /// is in looking for <c>assets/icons</c>, and what they find there is whatever the build
    /// copied next to the binary rather than what is in the repository. A project that
    /// stopped copying the renderings would leave every assertion above green and every tray
    /// without an icon.
    /// </para>
    /// <para>
    /// The resolvers fall back to any other size when the preferred one is missing, which is
    /// the right behaviour at run time and the wrong result here, so what comes back is
    /// compared against the name the chooser asked for rather than merely checked for being
    /// non-null.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheResolversFindThePreferredRenderingBesideTheRunningBinary()
    {
        string? linux = LinuxTrayAssets.DiscoverAssetDirectory(AppContext.BaseDirectory);
        Assert.True(
            linux is not null,
            $"The Linux walk found no assets/icons at or above {AppContext.BaseDirectory}.");

        foreach (bool light in new[] { true, false })
        {
            int wanted = LinuxTrayAssets.PreferredPixelSize;
            string expected = Path.Combine(
                linux!, LinuxTrayAssets.FileName(light, LinuxTrayAssets.ChooseAssetSize(light, wanted)));

            Assert.Equal(expected, LinuxTrayAssets.Resolve(linux!, light, wanted));
        }

        string? mac = MacOSTrayAssets.DiscoverAssetDirectory(Environment.ProcessPath, AppContext.BaseDirectory);
        Assert.True(
            mac is not null,
            $"The macOS walk found no template renderings at or above {AppContext.BaseDirectory}.");

        foreach (double scale in new[] { 1d, 2d })
        {
            int wanted = MacOSTrayAssets.PixelSizeForScale(scale);
            string expected = Path.Combine(
                mac!, MacOSTrayAssets.FileName(MacOSTrayAssets.ChooseAssetSize(wanted)));

            Assert.Equal(expected, MacOSTrayAssets.Resolve(mac!, wanted));
        }
    }

    /// <summary>
    /// Every size the Linux chooser can return for one glyph.
    /// </summary>
    /// <param name="light">Which glyph.</param>
    /// <returns>The sizes, ascending.</returns>
    /// <remarks>
    /// The arrays themselves are private, so they are read out through the only door there
    /// is: the chooser returns the smallest size that is not below what was asked for, so
    /// sweeping the whole range of wanted sizes visits every entry. Reflecting over the
    /// fields would read the arrays rather than what the chooser does with them, and it is
    /// the chooser that names the file.
    /// </remarks>
    private static IReadOnlyList<int> LinuxSizes(bool light)
    {
        SortedSet<int> sizes = [];
        for (int wanted = 1; wanted <= 1024; wanted++)
        {
            _ = sizes.Add(LinuxTrayAssets.ChooseAssetSize(light, wanted));
        }

        Assert.NotEmpty(sizes);
        return [.. sizes];
    }

    /// <summary>
    /// Opens a rendering, reads its own header, and reports the size it is really drawn at.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="name">The file name, for the failure messages.</param>
    /// <returns>The square's side in pixels.</returns>
    /// <remarks>
    /// Read rather than trusted, because the file name is the only other thing carrying the
    /// size and it is the thing under suspicion. A 32 pixel image saved as
    /// <c>altim-white-44.png</c> satisfies every assertion about paths and is upscaled on the
    /// panel it was added for, which is the failure the larger renderings exist to prevent.
    /// A file that exists and is not an image fails the same way and is caught here too.
    /// </remarks>
    private static int SquarePixelsOf(string path, string name)
    {
        var file = new FileInfo(path);
        Assert.True(
            file.Exists,
            $"{name} is named by a chooser and is not in assets/icons, so that size has no icon.");

        // Signature, then the IHDR length and tag, then the width and height. IHDR is the
        // first chunk in every PNG, by the format's own rule.
        Span<byte> header = stackalloc byte[24];
        using (FileStream stream = file.OpenRead())
        {
            Assert.True(
                stream.ReadAtLeast(header, 24, throwOnEndOfStream: false) == 24,
                $"{name} is {file.Length} bytes, which is not even a PNG header.");
        }

        Assert.True(header[..8].SequenceEqual(PngSignature), $"{name} is not a PNG.");
        Assert.True(header[12..16].SequenceEqual("IHDR"u8), $"{name} does not start with an IHDR chunk.");

        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);

        Assert.True(
            width == height,
            $"{name} is {width}x{height}. A tray glyph is square, and a host handed an oblong "
                + "one either stretches it or letterboxes it.");

        return (int)width;
    }

    /// <summary>
    /// The repository's <c>assets/icons</c>, found by walking up from the test binary.
    /// </summary>
    /// <returns>The absolute path.</returns>
    /// <remarks>
    /// Anchored on the solution file rather than on the icons themselves, so a run from a
    /// directory that happens to contain an <c>assets/icons</c> of its own cannot be taken
    /// for the repository. The test runner's working directory is not the repository and a
    /// relative path from it would break the moment the suite ran from anywhere else.
    /// </remarks>
    private static string IconDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Altim.sln")))
            {
                string icons = Path.Combine(directory.FullName, "assets", "icons");
                Assert.True(Directory.Exists(icons), $"{icons} does not exist.");
                return icons;
            }

            directory = directory.Parent;
        }

        Assert.Fail($"Could not find Altim.sln above {AppContext.BaseDirectory}.");
        return string.Empty;
    }
}
