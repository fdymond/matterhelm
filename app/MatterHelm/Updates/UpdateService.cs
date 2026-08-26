using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

[assembly: InternalsVisibleTo("MatterHelm.Tests")]

namespace MatterHelm.Updates;

/// <summary>The packaging mode of the currently-running copy of MatterHelm.</summary>
public enum UpdateInstallMode
{
    /// <summary>An Inno Setup uninstall registration points at the running executable's directory.</summary>
    Installed,

    /// <summary>No Inno Setup uninstall registration points at the running executable's directory.</summary>
    Portable,
}

/// <summary>The outcome of asking GitHub for the latest release.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The running version is current.</summary>
    UpToDate,

    /// <summary>A newer compatible release is available.</summary>
    UpdateAvailable,

    /// <summary>The check could not be completed.</summary>
    Unavailable,
}

/// <summary>The outcome of a consent-gated download and hash verification.</summary>
public enum UpdateDownloadStatus
{
    /// <summary>The caller has not supplied explicit user consent, so no request was made.</summary>
    ConsentRequired,

    /// <summary>The package was downloaded and verified.</summary>
    Ready,

    /// <summary>The package could not be downloaded or verified.</summary>
    Failed,
}

/// <summary>A SemVer 2.0 version used for release comparison.</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<SemanticVersion>
{
    /// <summary>Parses a three-component semantic version, tolerating one leading <c>v</c> and ignoring build metadata.</summary>
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> text = value.Trim();
        if (text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        int buildSeparator = text.IndexOf('+');
        if (buildSeparator >= 0)
        {
            if (!AreValidIdentifiers(text[(buildSeparator + 1)..], rejectNumericLeadingZero: false))
            {
                return false;
            }

            text = text[..buildSeparator];
        }

        string? preRelease = null;
        int preSeparator = text.IndexOf('-');
        if (preSeparator >= 0)
        {
            ReadOnlySpan<char> pre = text[(preSeparator + 1)..];
            if (!AreValidIdentifiers(pre, rejectNumericLeadingZero: true))
            {
                return false;
            }

            preRelease = pre.ToString();
            text = text[..preSeparator];
        }

        Span<Range> ranges = stackalloc Range[3];
        int count = text.Split(ranges, '.', StringSplitOptions.None);
        if (count != 3
            || !TryParseComponent(text[ranges[0]], out int major)
            || !TryParseComponent(text[ranges[1]], out int minor)
            || !TryParseComponent(text[ranges[2]], out int patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, preRelease);
        return true;
    }

    /// <summary>Converts an assembly version to the product's three-component SemVer.</summary>
    public static SemanticVersion FromAssemblyVersion(Version version) => new(
        version.Major,
        version.Minor,
        Math.Max(0, version.Build));

    /// <inheritdoc />
    public int CompareTo(SemanticVersion other)
    {
        int core = Major.CompareTo(other.Major);
        if (core == 0)
        {
            core = Minor.CompareTo(other.Minor);
        }

        if (core == 0)
        {
            core = Patch.CompareTo(other.Patch);
        }

        if (core != 0)
        {
            return core;
        }

        if (PreRelease is null)
        {
            return other.PreRelease is null ? 0 : 1;
        }

        if (other.PreRelease is null)
        {
            return -1;
        }

        string[] left = PreRelease.Split('.');
        string[] right = other.PreRelease.Split('.');
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            int identifier = CompareIdentifier(left[index], right[index]);
            if (identifier != 0)
            {
                return identifier;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    /// <summary>Returns whether <paramref name="left"/> precedes <paramref name="right"/>.</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>Returns whether <paramref name="left"/> follows <paramref name="right"/>.</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>Returns whether <paramref name="left"/> does not follow <paramref name="right"/>.</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Returns whether <paramref name="left"/> does not precede <paramref name="right"/>.</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    /// <inheritdoc />
    public override string ToString() => PreRelease is null
        ? FormattableString.Invariant($"{Major}.{Minor}.{Patch}")
        : FormattableString.Invariant($"{Major}.{Minor}.{Patch}-{PreRelease}");

    private static bool TryParseComponent(ReadOnlySpan<char> component, out int value)
    {
        value = 0;
        return component.Length > 0
            && (component.Length == 1 || component[0] != '0')
            && int.TryParse(component, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0;
    }

    private static bool AreValidIdentifiers(ReadOnlySpan<char> value, bool rejectNumericLeadingZero)
    {
        if (value.Length == 0)
        {
            return false;
        }

        int start = 0;
        for (int index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && value[index] != '.')
            {
                if (!char.IsAsciiLetterOrDigit(value[index]) && value[index] != '-')
                {
                    return false;
                }

                continue;
            }

            ReadOnlySpan<char> identifier = value[start..index];
            if (identifier.Length == 0
                || (rejectNumericLeadingZero
                    && identifier.Length > 1
                    && identifier[0] == '0'
                    && identifier.IndexOfAnyExceptInRange('0', '9') < 0))
            {
                return false;
            }

            start = index + 1;
        }

        return true;
    }

    private static int CompareIdentifier(string left, string right)
    {
        bool leftNumeric = left.All(char.IsAsciiDigit);
        bool rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            string normalizedLeft = left.TrimStart('0');
            string normalizedRight = right.TrimStart('0');
            normalizedLeft = normalizedLeft.Length == 0 ? "0" : normalizedLeft;
            normalizedRight = normalizedRight.Length == 0 ? "0" : normalizedRight;
            int length = normalizedLeft.Length.CompareTo(normalizedRight.Length);
            return length != 0 ? length : string.CompareOrdinal(normalizedLeft, normalizedRight);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }
}

/// <summary>One downloadable asset attached to a GitHub release.</summary>
public sealed record UpdateAsset(string Name, Uri ApiUrl);

/// <summary>A newer release and the package selected for the current install mode.</summary>
public sealed record UpdateRelease(
    SemanticVersion Version,
    string TagName,
    UpdateInstallMode InstallMode,
    UpdateAsset Package,
    UpdateAsset Checksums);

/// <summary>Result returned by <see cref="UpdateService.CheckForUpdatesAsync"/>.</summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, UpdateRelease? Release = null);

/// <summary>Result returned by <see cref="UpdateService.DownloadAndVerifyAsync"/>.</summary>
public sealed record UpdateDownloadResult(
    UpdateDownloadStatus Status,
    string Message,
    string? PackagePath = null,
    string? ExpectedSha256 = null);

/// <summary>
/// Injectable registry/filesystem view used to classify Inno-installed versus
/// portable copies without tests touching the real registry or process path.
/// </summary>
public interface IUpdateInstallationProbe
{
    /// <summary>Inno uninstall registrations found across the supported registry hives and views.</summary>
    IReadOnlyList<UpdateUninstallRegistration> InnoUninstallRegistrations { get; }

    /// <summary>The running executable's full path.</summary>
    string ExecutablePath { get; }

    /// <summary>True when <paramref name="path"/> exists as a file.</summary>
    bool FileExists(string path);
}

/// <summary>Paths recorded by one Inno Setup uninstall registration.</summary>
public sealed record UpdateUninstallRegistration(string? InstallLocation, string? DisplayIcon);

/// <summary>The updater's classification of the running app and its replacement target.</summary>
public sealed record UpdateInstallation(UpdateInstallMode Mode, string ExecutablePath, string InstallDirectory);

/// <summary>Pure install-mode classification over an injected registry/filesystem probe.</summary>
public static class UpdateInstallationDetector
{
    /// <summary>Returns installed mode iff an Inno registration points at the running executable's directory.</summary>
    public static UpdateInstallation Detect(IUpdateInstallationProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        string executable = Path.GetFullPath(probe.ExecutablePath);
        if (!probe.FileExists(executable))
        {
            throw new InvalidOperationException("The running MatterHelm executable path does not exist.");
        }

        string directory = Path.GetDirectoryName(executable)
            ?? throw new InvalidOperationException("The running MatterHelm executable has no parent directory.");
        bool installed = probe.InnoUninstallRegistrations.Any(registration =>
            DirectoryMatches(registration.InstallLocation, directory)
            || DisplayIconDirectoryMatches(registration.DisplayIcon, directory));
        return new UpdateInstallation(installed ? UpdateInstallMode.Installed : UpdateInstallMode.Portable, executable, directory);
    }

    private static bool DirectoryMatches(string? registeredPath, string executableDirectory)
    {
        if (string.IsNullOrWhiteSpace(registeredPath))
        {
            return false;
        }

        try
        {
            string candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(registeredPath.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string running = Path.GetFullPath(executableDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(candidate, running, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool DisplayIconDirectoryMatches(string? displayIcon, string executableDirectory)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return false;
        }

        string iconPath = displayIcon.Trim();
        if (iconPath.StartsWith('"'))
        {
            int closingQuote = iconPath.IndexOf('"', 1);
            iconPath = closingQuote > 1 ? iconPath[1..closingQuote] : iconPath.Trim('"');
        }
        else
        {
            int iconIndex = iconPath.LastIndexOf(',');
            if (iconIndex >= 0)
            {
                iconPath = iconPath[..iconIndex].Trim();
            }
        }

        try
        {
            string? iconDirectory = Path.GetDirectoryName(Path.GetFullPath(Environment.ExpandEnvironmentVariables(iconPath)));
            return DirectoryMatches(iconDirectory, executableDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>Production registry/filesystem probe for <see cref="UpdateInstallationDetector"/>.</summary>
public sealed class WindowsUpdateInstallationProbe : IUpdateInstallationProbe
{
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C1E9A2E-4E1B-4B5B-9A7D-3D3A9B6F51C4}_is1";

    /// <inheritdoc />
    public IReadOnlyList<UpdateUninstallRegistration> InnoUninstallRegistrations =>
        new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
        }
        .Select(entry => ReadRegistration(entry.Hive, entry.View))
        .OfType<UpdateUninstallRegistration>()
        .ToArray();

    /// <inheritdoc />
    public string ExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Windows did not report the running executable path.");

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    private static UpdateUninstallRegistration? ReadRegistration(RegistryHive hive, RegistryView view)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? key = baseKey.OpenSubKey(UninstallSubKey, writable: false);
            return key is null
                ? null
                : new UpdateUninstallRegistration(
                    key.GetValue("InstallLocation") as string,
                    key.GetValue("DisplayIcon") as string);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

/// <summary>
/// GitHub-release updater core. It checks and downloads only; execution is a
/// separate, explicit handoff after the tray has obtained user consent.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/fdymond/matterhelm/releases/latest");
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private const int MaxReleaseDocumentBytes = 1024 * 1024;
    private const int MaxChecksumDocumentBytes = 1024 * 1024;
    private const long MaxPackageBytes = 500L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly IUpdateInstallationProbe _installationProbe;
    private readonly SemanticVersion _currentVersion;
    private readonly string? _token;
    private readonly Action<string, string> _log;
    private readonly string _updatesDirectory;
    private readonly object _updatesDirectoryGate = new();
    private bool _updatesDirectorySwept;

    /// <summary>Creates a service with injectable HTTP, registry/filesystem, version, token, and log seams.</summary>
    public UpdateService(
        HttpClient httpClient,
        IUpdateInstallationProbe installationProbe,
        SemanticVersion currentVersion,
        string? token = null,
        Action<string, string>? log = null,
        string? updatesDirectory = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _installationProbe = installationProbe ?? throw new ArgumentNullException(nameof(installationProbe));
        _currentVersion = currentVersion;
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _log = log ?? DefaultLog;
        _updatesDirectory = Path.GetFullPath(updatesDirectory
            ?? Path.Combine(Path.GetTempPath(), "MatterHelm", "updates"));
    }

    private UpdateService(
        HttpClient httpClient,
        IUpdateInstallationProbe installationProbe,
        SemanticVersion currentVersion,
        string? token,
        Action<string, string> log,
        bool ownsHttpClient)
        : this(httpClient, installationProbe, currentVersion, token, log)
    {
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>Creates the production service, reading the optional private-release token from the process environment only.</summary>
    public static UpdateService CreateDefault()
    {
        Version assemblyVersion = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return new UpdateService(
            client,
            new WindowsUpdateInstallationProbe(),
            SemanticVersion.FromAssemblyVersion(assemblyVersion),
            Environment.GetEnvironmentVariable("MATTERHELM_UPDATE_TOKEN"),
            DefaultLog,
            ownsHttpClient: true);
    }

    /// <summary>Checks GitHub's latest release endpoint and selects the matching package for this installation.</summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        EnsureUpdatesDirectorySwept();
        _log("INFO", $"Update check: starting (current {_currentVersion}).");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CheckTimeout);
            using HttpRequestMessage request = CreateRequest(LatestReleaseUri, "application/vnd.github+json");
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                _log("WARN", $"Update check: unavailable (GitHub returned {(int)response.StatusCode}).");
                return new UpdateCheckResult(
                    UpdateCheckStatus.Unavailable,
                    "Update check unavailable. The release service may still be private or temporarily unreachable.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _log("WARN", $"Update check: unavailable (GitHub returned {(int)response.StatusCode}).");
                return new UpdateCheckResult(UpdateCheckStatus.Unavailable, "Update check unavailable. Please try again later.");
            }

            string json = await ReadLimitedTextAsync(response.Content, MaxReleaseDocumentBytes, timeout.Token).ConfigureAwait(false);
            (string tag, IReadOnlyList<UpdateAsset> assets) = ParseRelease(json);
            if (!SemanticVersion.TryParse(tag, out SemanticVersion latest))
            {
                throw new InvalidDataException("The latest release tag is not a semantic version.");
            }

            if (latest.CompareTo(_currentVersion) <= 0)
            {
                _log("INFO", $"Update check: up to date (latest {latest}).");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, $"MatterHelm {_currentVersion} is up to date.");
            }

            UpdateInstallation installation = UpdateInstallationDetector.Detect(_installationProbe);
            UpdateAsset package = SelectPackageAsset(assets, latest, installation.Mode)
                ?? throw new InvalidDataException("The latest release does not contain the expected Windows package.");
            UpdateAsset checksums = assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The latest release does not contain SHA256SUMS.txt.");

            var release = new UpdateRelease(latest, tag, installation.Mode, package, checksums);
            _log("INFO", $"Update check: version {latest} available for {installation.Mode.ToString().ToLowerInvariant()} mode.");
            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, $"MatterHelm {latest} is available.", release);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log("WARN", "Update check: timed out.");
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable, "Update check timed out. Please try again later.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException)
        {
            _log("WARN", $"Update check: unavailable ({ex.Message}).");
            return new UpdateCheckResult(UpdateCheckStatus.Unavailable, "Update check unavailable. Please try again later.");
        }
    }

    /// <summary>
    /// Downloads the selected package and same-release checksum manifest only
    /// when <paramref name="userConsented"/> is true, then verifies SHA-256.
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadAndVerifyAsync(
        UpdateRelease release,
        bool userConsented,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        EnsureUpdatesDirectorySwept();
        if (!userConsented)
        {
            _log("INFO", "Update download: skipped because user consent was not supplied.");
            return new UpdateDownloadResult(UpdateDownloadStatus.ConsentRequired, "User consent is required before downloading an update.");
        }

        string directory = Path.Combine(_updatesDirectory, Guid.NewGuid().ToString("N"));
        string packagePath = Path.Combine(directory, release.Package.Name);
        try
        {
            Directory.CreateDirectory(directory);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);

            _log("INFO", $"Update download: fetching checksum manifest for {release.Version}.");
            string manifest = await DownloadTextAssetAsync(
                release.Checksums,
                MaxChecksumDocumentBytes,
                timeout.Token).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> sums = ParseSha256Sums(manifest);
            if (!sums.TryGetValue(release.Package.Name, out string? expectedHash))
            {
                throw new InvalidDataException($"SHA256SUMS.txt has no entry for {release.Package.Name}.");
            }

            _log("INFO", $"Update download: fetching {release.Package.Name}.");
            await DownloadFileAssetAsync(release.Package, packagePath, timeout.Token).ConfigureAwait(false);

            _log("INFO", $"Update hash verify: checking {release.Package.Name}.");
            await using FileStream package = new(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                useAsync: true);
            byte[] actual = await SHA256.HashDataAsync(package, timeout.Token).ConfigureAwait(false);
            byte[] expected = Convert.FromHexString(expectedHash);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                package.Close();
                File.Delete(packagePath);
                TryDeleteDirectory(directory);
                _log("ERROR", $"Update hash verify: FAILED for {release.Package.Name}; downloaded file deleted.");
                return new UpdateDownloadResult(
                    UpdateDownloadStatus.Failed,
                    "The downloaded update failed SHA-256 verification and was deleted.");
            }

            _log("INFO", $"Update hash verify: passed for {release.Package.Name}.");
            return new UpdateDownloadResult(
                UpdateDownloadStatus.Ready,
                "Update downloaded and verified.",
                packagePath,
                expectedHash);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDelete(packagePath);
            TryDeleteDirectory(directory);
            _log("INFO", "Update download: canceled by caller; partial file deleted.");
            throw;
        }
        catch (OperationCanceledException)
        {
            TryDelete(packagePath);
            TryDeleteDirectory(directory);
            _log("WARN", "Update download: timed out; partial file deleted.");
            return new UpdateDownloadResult(UpdateDownloadStatus.Failed, "The update download timed out. Please try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            TryDelete(packagePath);
            TryDeleteDirectory(directory);
            _log("ERROR", $"Update download: failed ({ex.Message}); partial file deleted.");
            return new UpdateDownloadResult(UpdateDownloadStatus.Failed, $"The update could not be prepared: {ex.Message}");
        }
    }

    /// <summary>Parses the release workflow's <c>lowercase-hash__filename</c> manifest format.</summary>
    public static IReadOnlyDictionary<string, string> ParseSha256Sums(string manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in manifest.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length <= 66 || line[64] != ' ' || line[65] != ' ')
            {
                continue;
            }

            string hash = line[..64];
            string name = line[66..];
            if (name.Length == 0 || !hash.All(Uri.IsHexDigit))
            {
                continue;
            }

            result[name] = hash.ToLowerInvariant();
        }

        return result;
    }

    /// <summary>Selects the exact workflow-defined asset name for <paramref name="mode"/>.</summary>
    public static UpdateAsset? SelectPackageAsset(
        IEnumerable<UpdateAsset> assets,
        SemanticVersion version,
        UpdateInstallMode mode)
    {
        ArgumentNullException.ThrowIfNull(assets);
        string expected = mode == UpdateInstallMode.Installed
            ? $"MatterHelm-Setup-{version}.exe"
            : $"matterhelm-v{version}-win-x64.zip";
        return assets.FirstOrDefault(asset => string.Equals(asset.Name, expected, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<string> DownloadTextAssetAsync(UpdateAsset asset, int maximumBytes, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(asset.ApiUrl, "application/octet-stream");
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedTextAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadFileAssetAsync(UpdateAsset asset, string destination, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(asset.ApiUrl, "application/octet-stream");
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxPackageBytes)
        {
            throw new InvalidDataException("The update package exceeds the 500 MiB size limit.");
        }

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream target = new(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        byte[] buffer = new byte[128 * 1024];
        long totalBytes = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            totalBytes += read;
            if (totalBytes > MaxPackageBytes)
            {
                throw new InvalidDataException("The update package exceeds the 500 MiB size limit.");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage CreateRequest(Uri uri, string accept)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update requests must use HTTPS.");
        }

        if (!string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Update API requests must target api.github.com.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.ParseAdd($"MatterHelm/{_currentVersion}");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (_token is not null)
        {
            // Private assets require the PAT on api.github.com. Production relies on the BCL
            // RedirectHandler stripping Authorization when GitHub redirects the download to
            // objects.githubusercontent.com, so the PAT never reaches the asset host.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return request;
    }

    private static async Task<string> ReadLimitedTextAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException("The update service returned an unexpectedly large document.");
        }

        await using Stream source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The update service returned an unexpectedly large document.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static (string Tag, IReadOnlyList<UpdateAsset> Assets) ParseRelease(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out JsonElement tagElement)
            || tagElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(tagElement.GetString())
            || !root.TryGetProperty("assets", out JsonElement assetsElement)
            || assetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The GitHub release response has an unexpected shape.");
        }

        var assets = new List<UpdateAsset>();
        foreach (JsonElement asset in assetsElement.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out JsonElement nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || nameElement.GetString() is not { Length: > 0 } name
                || !asset.TryGetProperty("url", out JsonElement urlElement)
                || urlElement.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out Uri? uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            assets.Add(new UpdateAsset(name, uri));
        }

        return (tagElement.GetString()!, assets);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The original failure is more useful; a stale partial in a unique temp directory is harmless.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same best-effort policy as TryDelete; the directory is unique and under %TEMP%.
        }
    }

    private void EnsureUpdatesDirectorySwept()
    {
        lock (_updatesDirectoryGate)
        {
            if (_updatesDirectorySwept)
            {
                return;
            }

            try
            {
                if (Directory.Exists(_updatesDirectory))
                {
                    foreach (string directory in Directory.EnumerateDirectories(_updatesDirectory))
                    {
                        try
                        {
                            Directory.Delete(directory, recursive: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            _log("WARN", $"Update cleanup: could not remove stale directory {Path.GetFileName(directory)} ({ex.Message}).");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log("WARN", $"Update cleanup: could not inspect the updates temp directory ({ex.Message}).");
            }

            _updatesDirectorySwept = true;
        }
    }

    private static void DefaultLog(string level, string message)
    {
        switch (level)
        {
            case "ERROR":
                Log.Error(message);
                break;
            case "WARN":
                Log.Warn(message);
                break;
            default:
                Log.Info(message);
                break;
        }
    }
}

/// <summary>Creates and starts the post-exit helper that applies a verified package.</summary>
public static class UpdateHandoff
{
    /// <summary>
    /// Writes a temporary PowerShell helper, starts it, and returns only after
    /// the helper process exists. The helper uses <c>Wait-Process</c> to wait
    /// for this process ID to exit before touching the installation.
    /// </summary>
    public static async Task<bool> StartAsync(
        UpdateRelease release,
        string packagePath,
        string expectedSha256,
        IUpdateInstallationProbe installationProbe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ArgumentNullException.ThrowIfNull(installationProbe);

        try
        {
            if (!IsSha256(expectedSha256))
            {
                Log.Error("Update handoff: expected SHA-256 is malformed; refusing to apply the package.");
                return false;
            }

            UpdateInstallation installation = UpdateInstallationDetector.Detect(installationProbe);
            if (installation.Mode != release.InstallMode)
            {
                Log.Error("Update handoff: install mode changed after the release check; refusing to apply the package.");
                return false;
            }

            if (!File.Exists(packagePath))
            {
                Log.Error("Update handoff: verified package is missing.");
                return false;
            }

            if (installation.Mode == UpdateInstallMode.Portable && !IsSafePortableTarget(installation))
            {
                Log.Error("Update handoff: portable target directory failed the safety check.");
                return false;
            }

            string helperDirectory = Path.Combine(Path.GetTempPath(), "MatterHelm", "updates");
            Directory.CreateDirectory(helperDirectory);
            string id = Guid.NewGuid().ToString("N");
            string helperPath = Path.Combine(helperDirectory, $"handoff-{id}.ps1");
            string helperLogPath = Path.Combine(helperDirectory, $"handoff-{id}.log");
            string script = GetHelperScript(release.InstallMode);
            await File.WriteAllTextAsync(
                helperPath,
                script,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken).ConfigureAwait(false);

            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe");
            ProcessStartInfo start = CreateHelperStartInfo(
                powershell,
                helperPath,
                Environment.ProcessId,
                packagePath,
                installation.ExecutablePath,
                installation.InstallDirectory,
                helperLogPath,
                expectedSha256);
            using Process? helper = Process.Start(start);
            if (helper is null)
            {
                Log.Error("Update handoff: PowerShell helper did not start.");
                return false;
            }

            Log.Info($"Update handoff: helper started at '{helperPath}' (helper log '{helperLogPath}').");
            return true;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            System.Security.SecurityException)
        {
            Log.Error($"Update handoff: helper could not be prepared or started ({ex.Message}).");
            return false;
        }
    }

    internal static ProcessStartInfo CreateHelperStartInfo(
        string powershellPath,
        string helperPath,
        int processId,
        string packagePath,
        string executablePath,
        string installDirectory,
        string logPath,
        string expectedSha256)
    {
        var start = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(helperPath);
        start.ArgumentList.Add("-MatterHelmProcessId");
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("-PackagePath");
        start.ArgumentList.Add(packagePath);
        start.ArgumentList.Add("-ExecutablePath");
        start.ArgumentList.Add(executablePath);
        start.ArgumentList.Add("-InstallDirectory");
        start.ArgumentList.Add(installDirectory);
        start.ArgumentList.Add("-LogPath");
        start.ArgumentList.Add(logPath);
        start.ArgumentList.Add("-ExpectedSha256");
        start.ArgumentList.Add(expectedSha256);
        return start;
    }

    internal static string GetHelperScript(UpdateInstallMode mode) =>
        mode == UpdateInstallMode.Installed ? InstalledScript : PortableScript;

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static bool IsSafePortableTarget(UpdateInstallation installation)
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.InstallDirectory));
        string? root = Path.GetPathRoot(directory);
        return !string.IsNullOrEmpty(root)
            && !string.Equals(directory, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetDirectoryName(Path.GetFullPath(installation.ExecutablePath)),
                directory,
                StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(installation.ExecutablePath), "MatterHelm.exe", StringComparison.OrdinalIgnoreCase);
    }

    private const string InstalledScript = """
        param(
          [Parameter(Mandatory=$true)][int]$MatterHelmProcessId,
          [Parameter(Mandatory=$true)][string]$PackagePath,
          [Parameter(Mandatory=$true)][string]$ExecutablePath,
          [Parameter(Mandatory=$true)][string]$InstallDirectory,
          [Parameter(Mandatory=$true)][string]$LogPath,
          [Parameter(Mandatory=$true)][string]$ExpectedSha256
        )
        $ErrorActionPreference = 'Stop'
        try {
          Add-Content -LiteralPath $LogPath -Value "Waiting for MatterHelm process $MatterHelmProcessId to exit."
          Wait-Process -Id $MatterHelmProcessId -ErrorAction SilentlyContinue
          Add-Content -LiteralPath $LogPath -Value 'Rechecking package SHA-256 before installer launch.'
          $actualSha256 = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
          if ($actualSha256 -ine $ExpectedSha256) {
            throw 'Package SHA-256 changed after verification; refusing to start the installer.'
          }
          $setup = Start-Process -FilePath $PackagePath -ArgumentList @('/SILENT', '/NORESTART', '/SP-') -Wait -PassThru
          Add-Content -LiteralPath $LogPath -Value "Installer exited with code $($setup.ExitCode)."
          if ($setup.ExitCode -ne 0) {
            throw "Installer exited with code $($setup.ExitCode)."
          }
          Add-Content -LiteralPath $LogPath -Value 'Update installed; relaunching MatterHelm.'
          Start-Process -FilePath $ExecutablePath
          Remove-Item -LiteralPath $PackagePath -Force -ErrorAction SilentlyContinue
          Remove-Item -LiteralPath (Split-Path -Parent $PackagePath) -Force -ErrorAction SilentlyContinue
        } catch {
          Add-Content -LiteralPath $LogPath -Value "Update handoff failed: $($_.Exception.Message)"
          try {
            Add-Content -LiteralPath $LogPath -Value 'Attempting to relaunch the existing MatterHelm executable.'
            Start-Process -FilePath $ExecutablePath
          } catch {
            Add-Content -LiteralPath $LogPath -Value "MatterHelm relaunch failed: $($_.Exception.Message)"
          }
        } finally {
          Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        }
        """;

    private const string PortableScript = """
        param(
          [Parameter(Mandatory=$true)][int]$MatterHelmProcessId,
          [Parameter(Mandatory=$true)][string]$PackagePath,
          [Parameter(Mandatory=$true)][string]$ExecutablePath,
          [Parameter(Mandatory=$true)][string]$InstallDirectory,
          [Parameter(Mandatory=$true)][string]$LogPath,
          [Parameter(Mandatory=$true)][string]$ExpectedSha256
        )
        $ErrorActionPreference = 'Stop'
        $stage = Join-Path ([IO.Path]::GetTempPath()) ("MatterHelm-update-stage-" + [Guid]::NewGuid().ToString('N'))
        try {
          Add-Content -LiteralPath $LogPath -Value "Waiting for MatterHelm process $MatterHelmProcessId to exit."
          Wait-Process -Id $MatterHelmProcessId -ErrorAction SilentlyContinue
          New-Item -ItemType Directory -Path $stage | Out-Null
          Add-Content -LiteralPath $LogPath -Value 'Rechecking package SHA-256 before archive expansion.'
          $actualSha256 = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
          if ($actualSha256 -ine $ExpectedSha256) {
            throw 'Package SHA-256 changed after verification; refusing to expand the archive.'
          }
          Expand-Archive -LiteralPath $PackagePath -DestinationPath $stage -Force
          # Copy-over intentionally retains files removed by newer releases; tracked as the P3 backlog limitation.
          Get-ChildItem -LiteralPath $stage -Force | Copy-Item -Destination $InstallDirectory -Recurse -Force
          Add-Content -LiteralPath $LogPath -Value 'Portable files replaced; relaunching MatterHelm.'
          Start-Process -FilePath $ExecutablePath
          Remove-Item -LiteralPath $PackagePath -Force -ErrorAction SilentlyContinue
          Remove-Item -LiteralPath (Split-Path -Parent $PackagePath) -Force -ErrorAction SilentlyContinue
        } catch {
          Add-Content -LiteralPath $LogPath -Value "Update handoff failed: $($_.Exception.Message)"
          try {
            Add-Content -LiteralPath $LogPath -Value 'Attempting to relaunch the existing MatterHelm executable.'
            Start-Process -FilePath $ExecutablePath
          } catch {
            Add-Content -LiteralPath $LogPath -Value "MatterHelm relaunch failed: $($_.Exception.Message)"
          }
        } finally {
          Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
          Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        }
        """;
}
