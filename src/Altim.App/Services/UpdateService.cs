using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Altim.App.Diagnostics;
using Altim.Core.Abstractions;
using Altim.Core.Updates;
using Velopack;
using Velopack.Sources;

namespace Altim.App.Services;

/// <summary>
/// Finds out whether a newer Altim has been released, and on a Velopack install fetches
/// it.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place in Altim that opens a socket. Everything else reads files and
/// runs the provider CLIs; a usage figure has never travelled over a network Altim itself
/// opened and still does not. What goes out here is one unauthenticated HTTPS GET for the
/// newest release of a public repository. What comes back is a version string. Nothing
/// about the machine, the providers or the usage is in the request, and there is nowhere
/// in this class that could put it there.
/// </para>
/// <para>
/// The check is the same on every platform and the fetch is not, because only a Velopack
/// install can replace itself. A portable unzip, a <c>.deb</c>, an AppImage and a DMG
/// dragged into Applications all report the newer version and stop: replacing those
/// belongs to the package manager or to the person, and a tray utility that started
/// rewriting files it did not install would be doing something nobody asked for.
/// </para>
/// <para>
/// The check reads GitHub's release API rather than Velopack's feed because the feed only
/// exists for the platforms Velopack packages. A Linux user would otherwise be told
/// nothing at all, which is not the same as being told there is nothing new.
/// </para>
/// </remarks>
internal sealed class UpdateService : IUpdateService, IDisposable
{
    /// <summary>The repository releases are published from.</summary>
    private const string Repository = "mpge/altim";

    /// <summary>
    /// Long enough for a slow link, short enough that nothing waits on it. A check that
    /// times out is a check that failed, and the next one is a day away.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateStatus _current = UpdateStatus.Unknown;
    private UpdateInfo? _pending;
    private bool _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="handler">
    /// The message handler, so a test can answer without a network. Null takes the
    /// default, which is the only thing the application itself passes.
    /// </param>
    public UpdateService(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = RequestTimeout;

        // GitHub refuses a request with no user agent. Naming the product and version is
        // the convention and is also the whole of what this request says about the caller.
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Altim", Running?.Text ?? "0.0.0"));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public UpdateStatus Current => _current;

    /// <inheritdoc />
    public ReleaseVersion? Running { get; } = ResolveRunningVersion();

    /// <inheritdoc />
    public Uri ReleasesPage { get; } = new("https://github.com/" + Repository + "/releases");

    /// <inheritdoc />
    public bool CanApplyUpdates => TryOpenManager(out UpdateManager? manager) && manager!.IsInstalled;

    /// <inheritdoc />
    public async ValueTask<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ReleaseVersion? running = Running;
            if (running is null)
            {
                // A build with no usable version cannot be compared with anything, and
                // guessing which way the comparison went is exactly the failure this
                // whole class exists to avoid.
                return Publish(new UpdateStatus(UpdateState.Unsupported, null, DateTimeOffset.UtcNow));
            }

            ReleaseVersion? latest;
            try
            {
                latest = await ReadLatestTagAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // HttpClient turns its own timeout into a cancellation that did not come
                // from the caller. That is a failed check, not a cancelled one.
                AltimLog.Write("updates", "The update check timed out");
                return Publish(new UpdateStatus(UpdateState.Failed, null, DateTimeOffset.UtcNow));
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
            {
                // Rule 5: the type, never the message. A failed request carries a URL and
                // sometimes a proxy's hostname, and altim.log is a file people attach to
                // bug reports.
                AltimLog.Write("updates", "The update check did not answer (" + ex.GetType().Name + ")");
                return Publish(new UpdateStatus(UpdateState.Failed, null, DateTimeOffset.UtcNow));
            }

            if (latest is null || latest <= running)
            {
                return Publish(new UpdateStatus(UpdateState.UpToDate, null, DateTimeOffset.UtcNow));
            }

            AltimLog.Write(
                "updates",
                "A newer release is available: " + latest.Text + " (running " + running.Text + ")");

            return Publish(new UpdateStatus(UpdateState.Available, latest, DateTimeOffset.UtcNow));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<UpdateStatus> FetchAsync(CancellationToken ct)
    {
        if (!_current.HasUpdate)
        {
            return _current;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!TryOpenManager(out UpdateManager? manager) || !manager!.IsInstalled)
            {
                return Publish(_current with { State = UpdateState.Available });
            }

            if (manager.UpdatePendingRestart is not null)
            {
                return Publish(_current with { State = UpdateState.Ready });
            }

            UpdateInfo? info = _pending ??= manager.CheckForUpdates();
            if (info is null)
            {
                // Velopack's own feed disagrees with the release page. That happens when a
                // release was published without the Windows artefacts, and the honest
                // reading is that there is something newer that this copy cannot fetch.
                return Publish(_current with { State = UpdateState.Available });
            }

            await manager.DownloadUpdatesAsync(info, progress: null, cancelToken: ct)
                .ConfigureAwait(false);

            AltimLog.Write("updates", "Fetched " + info.TargetFullRelease.Version + "; it applies on the next start");
            return Publish(_current with { State = UpdateState.Ready });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AltimLog.Write("updates", "Fetching the update failed (" + ex.GetType().Name + ")");
            return Publish(_current with { State = UpdateState.Failed });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// Reads the newest release's tag.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The version, or null when there is no release or the tag is not one.</returns>
    /// <remarks>
    /// <c>releases/latest</c> rather than the full list on purpose: GitHub excludes drafts
    /// and prereleases from it, so a release candidate is never offered to somebody running
    /// a stable build, and the page cannot grow past one response.
    /// </remarks>
    private async Task<ReleaseVersion?> ReadLatestTagAsync(CancellationToken ct)
    {
        using HttpResponseMessage response = await _http
            .GetAsync(
                new Uri("https://api.github.com/repos/" + Repository + "/releases/latest"),
                HttpCompletionOption.ResponseHeadersRead,
                ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 404 is the ordinary answer for a repository with no published release, and
            // it is not a failure worth a log line on every check.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            AltimLog.Write(
                "updates",
                "GitHub answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            throw new HttpRequestException("Unexpected status code.");
        }

        byte[] body = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return ReleaseFeed.ReadLatestTag(body);
    }

    /// <summary>
    /// The version this build was stamped with.
    /// </summary>
    /// <returns>The version, or null when the assembly carries nothing usable.</returns>
    /// <remarks>
    /// The informational version is the one the release workflow sets, and it is the only
    /// one that keeps a prerelease suffix — <see cref="AssemblyName.Version"/> is four
    /// numbers and would report <c>0.2.0-rc1</c> as <c>0.2.0</c>, which is how a release
    /// candidate ends up believing it is the release.
    /// </remarks>
    private static ReleaseVersion? ResolveRunningVersion()
    {
        Assembly assembly = typeof(UpdateService).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // The SDK appends "+<commit sha>" to the informational version. ReleaseVersion
        // discards build metadata, so this is left alone rather than trimmed here.
        return ReleaseVersion.TryParse(informational)
            ?? ReleaseVersion.TryParse(assembly.GetName().Version?.ToString(3));
    }

    /// <summary>
    /// Opens a Velopack update manager, or reports that this is not a Velopack install.
    /// </summary>
    /// <param name="manager">The manager, when one could be built.</param>
    /// <returns>True when <paramref name="manager"/> is usable.</returns>
    private static bool TryOpenManager(out UpdateManager? manager)
    {
        try
        {
            manager = new UpdateManager(
                new GithubSource("https://github.com/" + Repository, accessToken: null, prerelease: false));
            return true;
        }
        catch (Exception ex)
        {
            AltimLog.Write("updates", "No Velopack install here (" + ex.GetType().Name + ")");
            manager = null;
            return false;
        }
    }

    private UpdateStatus Publish(UpdateStatus status)
    {
        _current = status;
        Changed?.Invoke(this, EventArgs.Empty);
        return status;
    }
}
