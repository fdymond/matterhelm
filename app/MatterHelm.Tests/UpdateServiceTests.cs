using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using MatterHelm.Updates;
using Xunit;

namespace MatterHelm.Tests;

/// <summary>S10-11 updater pure-core and injected-boundary tests; no test reaches the network or real registry.</summary>
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", "1.2.2", 1)]
    [InlineData("1.2.3", "v1.2.3", 0)]
    [InlineData("1.2.3-alpha.2", "1.2.3-alpha.10", -1)]
    [InlineData("1.2.3-rc.1", "1.2.3", -1)]
    [InlineData("2.0.0+build.8", "2.0.0+other", 0)]
    public void SemanticVersionsFollowSemverPrecedence(string leftText, string rightText, int expectedSign)
    {
        Assert.True(SemanticVersion.TryParse(leftText, out SemanticVersion left));
        Assert.True(SemanticVersion.TryParse(rightText, out SemanticVersion right));

        Assert.Equal(expectedSign, Math.Sign(left.CompareTo(right)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.02.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-rc.01")]
    [InlineData("1.2.3+")]
    [InlineData("not-a-version")]
    public void SemanticVersionsRejectMalformedTags(string value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
    }

    [Fact]
    public void Sha256SumsParserAcceptsTheReleaseWorkflowFormatAndIgnoresMalformedLines()
    {
        const string InstallerHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string ZipHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        string manifest = $"{InstallerHash}  MatterHelm-Setup-0.5.0.exe\r\n"
            + "not a checksum\n"
            + $"{ZipHash} matterhelm-v0.5.0-win-x64.zip\n"
            + $"{ZipHash}  matterhelm-v0.5.0-win-x64.zip\n";

        IReadOnlyDictionary<string, string> parsed = UpdateService.ParseSha256Sums(manifest);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(InstallerHash, parsed["MatterHelm-Setup-0.5.0.exe"]);
        Assert.Equal(ZipHash, parsed["matterhelm-v0.5.0-win-x64.zip"]);
    }

    [Theory]
    [InlineData(UpdateInstallMode.Installed, "MatterHelm-Setup-0.5.0.exe")]
    [InlineData(UpdateInstallMode.Portable, "matterhelm-v0.5.0-win-x64.zip")]
    public void AssetSelectionUsesTheExactWorkflowName(UpdateInstallMode mode, string expected)
    {
        UpdateAsset[] assets =
        [
            Asset("matterhelm-v0.5.0-win-x64.zip"),
            Asset("MatterHelm-Setup-0.5.0.exe"),
            Asset("SHA256SUMS.txt"),
            Asset("MatterHelm-Setup-0.4.9.exe"),
        ];

        UpdateAsset? selected = UpdateService.SelectPackageAsset(assets, new SemanticVersion(0, 5, 0), mode);

        Assert.NotNull(selected);
        Assert.Equal(expected, selected.Name);
    }

    [Theory]
    [InlineData("matching-install-location", UpdateInstallMode.Installed)]
    [InlineData("matching-display-icon", UpdateInstallMode.Installed)]
    [InlineData("non-matching", UpdateInstallMode.Portable)]
    [InlineData("missing-values", UpdateInstallMode.Portable)]
    [InlineData("missing-key", UpdateInstallMode.Portable)]
    public void InstallModeRequiresAnInjectedRegistryPathMatchingTheRunningDirectory(
        string registrationCase,
        UpdateInstallMode expected)
    {
        UpdateUninstallRegistration[] registrations = registrationCase switch
        {
            "matching-install-location" => [new(FakeInstallationProbe.RunningDirectory + Path.DirectorySeparatorChar, null)],
            "matching-display-icon" => [new(null, $"\"{FakeInstallationProbe.RunningExecutable}\",0")],
            "non-matching" => [new(Path.Combine(Path.GetTempPath(), "another-MatterHelm"), null)],
            "missing-values" => [new(null, null)],
            _ => [],
        };
        var probe = new FakeInstallationProbe(true, registrations);

        UpdateInstallation installation = UpdateInstallationDetector.Detect(probe);

        Assert.Equal(expected, installation.Mode);
        Assert.Equal(Path.GetDirectoryName(probe.ExecutablePath), installation.InstallDirectory);
        Assert.Equal(1, probe.FileChecks);
    }

    [Fact]
    public void InstallModeRejectsAnInjectedMissingExecutable()
    {
        var probe = new FakeInstallationProbe(registered: true, exists: false);

        Assert.Throws<InvalidOperationException>(() => UpdateInstallationDetector.Detect(probe));
    }

    [Fact]
    public async Task CallerCancellationDeletesThePartialPackageAndRethrows()
    {
        const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string updateRoot = Path.Combine(Path.GetTempPath(), "MatterHelm-tests", Guid.NewGuid().ToString("N"));
        using var callerCancellation = new CancellationTokenSource();
        int responseNumber = 0;
        using var handler = new StubHttpHandler(_ =>
        {
            responseNumber++;
            return responseNumber == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{Hash}  matterhelm-v0.5.0-win-x64.zip\n",
                        Encoding.ASCII,
                        "text/plain"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new CancelingReadStream(callerCancellation)),
                };
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService(
            client,
            new FakeInstallationProbe(registered: false, exists: true),
            new SemanticVersion(0, 4, 1),
            updatesDirectory: updateRoot);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.DownloadAndVerifyAsync(
                    PortableRelease(),
                    userConsented: true,
                    cancellationToken: callerCancellation.Token));

            Assert.False(Directory.Exists(updateRoot) && Directory.EnumerateFileSystemEntries(updateRoot).Any());
        }
        finally
        {
            if (Directory.Exists(updateRoot))
            {
                Directory.Delete(updateRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FirstUpdaterUseSweepsStaleUpdateSubdirectoriesWithoutMakingARequest()
    {
        string updateRoot = Path.Combine(Path.GetTempPath(), "MatterHelm-tests", Guid.NewGuid().ToString("N"));
        string staleDirectory = Path.Combine(updateRoot, "stale-download", "nested");
        Directory.CreateDirectory(staleDirectory);
        await File.WriteAllTextAsync(Path.Combine(staleDirectory, "partial.zip"), "partial");
        using var handler = new StubHttpHandler(_ => throw new Xunit.Sdk.XunitException("The sweep must not require HTTP."));
        using var client = new HttpClient(handler);
        using var service = new UpdateService(
            client,
            new FakeInstallationProbe(registered: false, exists: true),
            new SemanticVersion(0, 4, 1),
            updatesDirectory: updateRoot);

        try
        {
            UpdateDownloadResult result = await service.DownloadAndVerifyAsync(PortableRelease(), userConsented: false);

            Assert.Equal(UpdateDownloadStatus.ConsentRequired, result.Status);
            Assert.False(Directory.Exists(Path.Combine(updateRoot, "stale-download")));
            Assert.Empty(Directory.EnumerateDirectories(updateRoot));
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(updateRoot))
            {
                Directory.Delete(updateRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadMakesNoHttpRequestWithoutExplicitConsent()
    {
        using var handler = new StubHttpHandler(_ => throw new Xunit.Sdk.XunitException("HTTP must stay gated by consent."));
        using var client = new HttpClient(handler);
        using var service = new UpdateService(
            client,
            new FakeInstallationProbe(registered: false, exists: true),
            new SemanticVersion(0, 4, 1));
        var release = new UpdateRelease(
            new SemanticVersion(0, 5, 0),
            "v0.5.0",
            UpdateInstallMode.Portable,
            Asset("matterhelm-v0.5.0-win-x64.zip"),
            Asset("SHA256SUMS.txt"));

        UpdateDownloadResult result = await service.DownloadAndVerifyAsync(release, userConsented: false);

        Assert.Equal(UpdateDownloadStatus.ConsentRequired, result.Status);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DownloadAcceptsOnlyAFileMatchingTheSameReleaseManifest()
    {
        byte[] package = Encoding.UTF8.GetBytes("verified update package");
        string hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        int responseNumber = 0;
        using var handler = new StubHttpHandler(_ =>
        {
            responseNumber++;
            return responseNumber == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{hash}  matterhelm-v0.5.0-win-x64.zip\n",
                        Encoding.ASCII,
                        "text/plain"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService(
            client,
            new FakeInstallationProbe(registered: false, exists: true),
            new SemanticVersion(0, 4, 1));
        var release = new UpdateRelease(
            new SemanticVersion(0, 5, 0),
            "v0.5.0",
            UpdateInstallMode.Portable,
            Asset("matterhelm-v0.5.0-win-x64.zip"),
            Asset("SHA256SUMS.txt"));

        UpdateDownloadResult result = await service.DownloadAndVerifyAsync(release, userConsented: true);

        Assert.Equal(UpdateDownloadStatus.Ready, result.Status);
        Assert.NotNull(result.PackagePath);
        Assert.Equal(hash, result.ExpectedSha256);
        try
        {
            Assert.Equal(package, await File.ReadAllBytesAsync(result.PackagePath));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(result.PackagePath)!, recursive: true);
        }
    }

    [Fact]
    public async Task HashMismatchHardFailsAndReturnsNoPackageForHandoff()
    {
        const string WrongHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        int responseNumber = 0;
        using var handler = new StubHttpHandler(_ =>
        {
            responseNumber++;
            return responseNumber == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{WrongHash}  matterhelm-v0.5.0-win-x64.zip\n",
                        Encoding.ASCII,
                        "text/plain"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("tampered package", Encoding.UTF8),
                };
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService(
            client,
            new FakeInstallationProbe(registered: false, exists: true),
            new SemanticVersion(0, 4, 1));
        var release = new UpdateRelease(
            new SemanticVersion(0, 5, 0),
            "v0.5.0",
            UpdateInstallMode.Portable,
            Asset("matterhelm-v0.5.0-win-x64.zip"),
            Asset("SHA256SUMS.txt"));
        string updateRoot = Path.Combine(Path.GetTempPath(), "MatterHelm", "updates");
        HashSet<string> packagesBefore = FindPackages(updateRoot, release.Package.Name);

        UpdateDownloadResult result = await service.DownloadAndVerifyAsync(release, userConsented: true);

        Assert.Equal(UpdateDownloadStatus.Failed, result.Status);
        Assert.Null(result.PackagePath);
        Assert.Null(result.ExpectedSha256);
        Assert.Contains("failed SHA-256 verification", result.Message, StringComparison.Ordinal);
        Assert.Empty(FindPackages(updateRoot, release.Package.Name).Except(packagesBefore));
    }

    [Fact]
    public async Task MissingManifestEntryFailsBeforeThePackageIsRequested()
    {
        using var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  another-file.zip\n",
                Encoding.ASCII,
                "text/plain"),
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(client);

        UpdateDownloadResult result = await service.DownloadAndVerifyAsync(PortableRelease(), userConsented: true);

        Assert.Equal(UpdateDownloadStatus.Failed, result.Status);
        Assert.Contains("no entry", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task PackageContentLengthOver500MiBIsRejectedBeforeStreaming()
    {
        const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        int responseNumber = 0;
        using var handler = new StubHttpHandler(_ =>
        {
            responseNumber++;
            if (responseNumber == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{Hash}  matterhelm-v0.5.0-win-x64.zip\n",
                        Encoding.ASCII,
                        "text/plain"),
                };
            }

            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = (500L * 1024 * 1024) + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        using var service = CreateService(client);

        UpdateDownloadResult result = await service.DownloadAndVerifyAsync(PortableRelease(), userConsented: true);

        Assert.Equal(UpdateDownloadStatus.Failed, result.Status);
        Assert.Contains("500 MiB", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CheckUsesInjectedHttpSelectsInstalledAssetAndSendsPrivateTokenAsAHeader()
    {
        const string Token = "private-test-token";
        string json = """
            {
              "tag_name": "v0.5.0",
              "assets": [
                { "name": "matterhelm-v0.5.0-win-x64.zip", "url": "https://api.github.com/assets/1" },
                { "name": "MatterHelm-Setup-0.5.0.exe", "url": "https://api.github.com/assets/2" },
                { "name": "SHA256SUMS.txt", "url": "https://api.github.com/assets/3" }
              ]
            }
            """;
        AuthenticationHeaderValueCapture? captured = null;
        using var handler = new StubHttpHandler(request =>
        {
            captured = new AuthenticationHeaderValueCapture(
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService(
            client,
            new FakeInstallationProbe(registered: true, exists: true),
            new SemanticVersion(0, 4, 1),
            token: Token);

        UpdateCheckResult result = await service.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.NotNull(result.Release);
        Assert.Equal("MatterHelm-Setup-0.5.0.exe", result.Release.Package.Name);
        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured.Scheme);
        Assert.Equal(Token, captured.Parameter);
        Assert.DoesNotContain(Token, captured.RequestUri, StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{ \"tag_name\": \"not-semver\", \"assets\": [] }")]
    [InlineData("{ \"tag_name\": \"v0.5.0\", \"assets\": [] }")]
    public async Task MalformedOrIncompleteReleaseMetadataReportsUnavailable(string json)
    {
        UpdateCheckResult result = await CheckReleaseAsync(json, new SemanticVersion(0, 4, 1));

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Null(result.Release);
    }

    [Theory]
    [InlineData("v0.4.1")]
    [InlineData("v0.4.0")]
    public async Task EqualOrOlderLatestReleaseReportsUpToDate(string tag)
    {
        string json = $$"""
            {
              "tag_name": "{{tag}}",
              "assets": []
            }
            """;

        UpdateCheckResult result = await CheckReleaseAsync(json, new SemanticVersion(0, 4, 1));

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Release);
    }

    [Theory]
    [InlineData("http://api.github.com/assets/1")]
    [InlineData("https://example.test/assets/1")]
    public async Task PackageAssetsMustUseHttpsAndTheGitHubApiHost(string packageUrl)
    {
        string json = $$"""
            {
              "tag_name": "v0.5.0",
              "assets": [
                { "name": "matterhelm-v0.5.0-win-x64.zip", "url": "{{packageUrl}}" },
                { "name": "SHA256SUMS.txt", "url": "https://api.github.com/assets/2" }
              ]
            }
            """;

        UpdateCheckResult result = await CheckReleaseAsync(json, new SemanticVersion(0, 4, 1));

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CrossHostAssetRedirectDoesNotForwardAuthorization()
    {
        const string Token = "private-test-token";
        byte[] package = Encoding.UTF8.GetBytes("redirected verified package");
        string hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        using var handler = new SimulatedRedirectHandler(hash, package);
        using var client = new HttpClient(handler);
        using var service = new UpdateService(
            client,
            new FakeInstallationProbe(registered: false, exists: true),
            new SemanticVersion(0, 4, 1),
            token: Token);

        UpdateDownloadResult result = await service.DownloadAndVerifyAsync(PortableRelease(), userConsented: true);

        Assert.NotNull(result.PackagePath);
        try
        {
            Assert.Equal(UpdateDownloadStatus.Ready, result.Status);
            Assert.Equal("Bearer", handler.ApiAuthorization?.Scheme);
            Assert.Equal(Token, handler.ApiAuthorization?.Parameter);
            Assert.Equal("objects.githubusercontent.com", handler.RedirectedHost);
            Assert.Null(handler.RedirectedAuthorization);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(result.PackagePath)!, recursive: true);
        }
    }

    [Fact]
    public void HelperArgumentsCarryExpectedHashAsAnInjectionSafeDiscreteValue()
    {
        const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string PackagePath = "C:\\Temp\\package'; Write-Host injected;.zip";
        ProcessStartInfo start = UpdateHandoff.CreateHelperStartInfo(
            "powershell.exe",
            "C:\\Temp\\handoff.ps1",
            42,
            PackagePath,
            "C:\\MatterHelm\\MatterHelm.exe",
            "C:\\MatterHelm",
            "C:\\Temp\\handoff.log",
            Hash);

        string[] arguments = [.. start.ArgumentList];
        int hashSwitch = Array.IndexOf(arguments, "-ExpectedSha256");

        Assert.True(hashSwitch >= 0);
        Assert.Equal(Hash, arguments[hashSwitch + 1]);
        Assert.Contains(PackagePath, arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("-Command", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(UpdateInstallMode.Installed, "Start-Process -FilePath $PackagePath")]
    [InlineData(UpdateInstallMode.Portable, "Expand-Archive -LiteralPath $PackagePath")]
    public void HelperRechecksHashImmediatelyBeforeUsingPackageAndRelaunchesOnFailure(
        UpdateInstallMode mode,
        string packageAction)
    {
        string script = UpdateHandoff.GetHelperScript(mode);
        int hashCheck = script.IndexOf("Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256", StringComparison.Ordinal);
        int action = script.IndexOf(packageAction, StringComparison.Ordinal);

        Assert.True(hashCheck >= 0);
        Assert.True(action > hashCheck);
        Assert.Contains("$ExpectedSha256", script, StringComparison.Ordinal);
        Assert.Contains("Update handoff failed", script, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $ExecutablePath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstalledHelperTreatsANonzeroInstallerExitAsFailure()
    {
        string script = UpdateHandoff.GetHelperScript(UpdateInstallMode.Installed);

        Assert.Contains("if ($setup.ExitCode -ne 0)", script, StringComparison.Ordinal);
        Assert.Contains("throw \"Installer exited with code $($setup.ExitCode).\"", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task PrivatePhaseResponsesAreAnUnavailableResultNotAnException(HttpStatusCode status)
    {
        using var handler = new StubHttpHandler(_ => new HttpResponseMessage(status));
        using var client = new HttpClient(handler);
        using var service = new UpdateService(
            client,
            new FakeInstallationProbe(registered: false, exists: true),
            new SemanticVersion(0, 4, 1));

        UpdateCheckResult result = await service.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Unavailable, result.Status);
        Assert.Contains("unavailable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static UpdateAsset Asset(string name) => new(name, new Uri($"https://api.github.com/assets/{name}"));

    private static UpdateRelease PortableRelease() => new(
        new SemanticVersion(0, 5, 0),
        "v0.5.0",
        UpdateInstallMode.Portable,
        Asset("matterhelm-v0.5.0-win-x64.zip"),
        Asset("SHA256SUMS.txt"));

    private static UpdateService CreateService(HttpClient client) => new(
        client,
        new FakeInstallationProbe(registered: false, exists: true),
        new SemanticVersion(0, 4, 1));

    private static async Task<UpdateCheckResult> CheckReleaseAsync(string json, SemanticVersion currentVersion)
    {
        using var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService(
            client,
            new FakeInstallationProbe(registered: false, exists: true),
            currentVersion);
        return await service.CheckForUpdatesAsync();
    }

    private static HashSet<string> FindPackages(string root, string fileName) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

    private sealed class FakeInstallationProbe : IUpdateInstallationProbe
    {
        private readonly bool _exists;

        internal static string RunningDirectory { get; } = Path.Combine(Path.GetTempPath(), "MatterHelm-tests");

        internal static string RunningExecutable { get; } = Path.Combine(RunningDirectory, "MatterHelm.exe");

        public FakeInstallationProbe(bool registered, bool exists)
            : this(
                exists,
                registered ? [new UpdateUninstallRegistration(RunningDirectory, null)] : [])
        {
        }

        public FakeInstallationProbe(bool exists, params UpdateUninstallRegistration[] registrations)
        {
            _exists = exists;
            InnoUninstallRegistrations = registrations;
        }

        public IReadOnlyList<UpdateUninstallRegistration> InnoUninstallRegistrations { get; }

        public string ExecutablePath => RunningExecutable;

        public int FileChecks { get; private set; }

        public bool FileExists(string path)
        {
            FileChecks++;
            return _exists && string.Equals(path, ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class CancelingReadStream(CancellationTokenSource cancellation) : MemoryStream([1, 2, 3, 4])
    {
        private bool _canceled;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ValueTask<int> read = base.ReadAsync(buffer, cancellationToken);
            if (!_canceled)
            {
                _canceled = true;
                cancellation.Cancel();
            }

            return read;
        }
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class SimulatedRedirectHandler(string hash, byte[] package) : HttpMessageHandler
    {
        public AuthenticationHeaderValue? ApiAuthorization { get; private set; }

        public AuthenticationHeaderValue? RedirectedAuthorization { get; private set; }

        public string? RedirectedHost { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal) == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $"{hash}  matterhelm-v0.5.0-win-x64.zip\n",
                        Encoding.ASCII,
                        "text/plain"),
                });
            }

            ApiAuthorization = request.Headers.Authorization;
            using var redirect = new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://objects.githubusercontent.com/releases/package.zip") },
            };
            using var redirectedRequest = new HttpRequestMessage(HttpMethod.Get, redirect.Headers.Location);

            // This stub models the BCL RedirectHandler cross-host rule named by CreateRequest.
            RedirectedAuthorization = redirectedRequest.Headers.Authorization;
            RedirectedHost = redirectedRequest.RequestUri?.Host;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(package),
            });
        }
    }

    private sealed record AuthenticationHeaderValueCapture(string? Scheme, string? Parameter, string? RequestUri);
}
