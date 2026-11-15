using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Umbraco.Cms.Web.Common.Hosting;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>
/// Checks the patched file provider's bytes and metadata, and its responses through the static-file middleware.
/// </summary>
public sealed class AssetFileProviderTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    private const string Prefix = "/portal/nested";
    private const string Login = "/umbraco/login/login.js";
    private const string WebsiteCss = "/umbraco/website/nonodes.css";

    /// <summary>
    /// Checks that each patched file has its own bytes, length and read-only streams, no physical path, and a
    /// <c>Last-Modified</c> time that a second provider repeats, while the original and unpatched files stay as they are.
    /// </summary>
    [Fact]
    public async Task Prepared_assets_have_their_own_bytes_and_metadata_and_leave_originals_intact()
    {
        var provider = CreateProvider();
        // Another server, or the same one after a restart, must offer the same cache validators.
        var restarted = CreateProvider();
        // The build records the time of the commit the package was built from.
        Assert.NotEqual(DateTimeOffset.MinValue, BackOfficeAssetFileProvider.SourceTimestamp);
        var paths = assets.PatchedPaths();
        Assert.Equal(UmbracoUnderTest.Patches.Select(patch => patch.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(), paths.Length);
        foreach (var path in paths)
        {
            var original = assets.Files.GetFileInfo(path);
            var originalBytes = await ReadAsync(original);
            var file = provider.GetFileInfo(path);
            var bytes = await ReadAsync(file);
            Assert.NotEqual(originalBytes, bytes);
            Assert.Equal(originalBytes, await ReadAsync(original));
            Assert.True(file.Exists);
            Assert.False(file.IsDirectory);
            Assert.Equal(original.Name, file.Name);
            Assert.Null(file.PhysicalPath); // SendFile must read the patched stream, not the original disk file.
            Assert.Equal(bytes.LongLength, file.Length);
            // New bytes come from a newer Umbraco file or package, so the later of the two times changes with them.
            Assert.Equal(original.LastModified > BackOfficeAssetFileProvider.SourceTimestamp ? original.LastModified
                : BackOfficeAssetFileProvider.SourceTimestamp, file.LastModified);
            Assert.Equal(file.LastModified, restarted.GetFileInfo(path).LastModified);
            Assert.Equal(file.Length, restarted.GetFileInfo(path).Length);
            Assert.Same(file, provider.GetFileInfo(path.TrimStart('/')));
            await using var first = file.CreateReadStream();
            await using var second = file.CreateReadStream();
            Assert.False(first.CanWrite);
            Assert.Equal(bytes[0], first.ReadByte());
            Assert.Equal(bytes[0], second.ReadByte());
        }

        const string untouched = "/umbraco/backoffice/packages/core/auth/index.js";
        Assert.Equal(await ReadAsync(assets.Files.GetFileInfo(untouched)), await ReadAsync(provider.GetFileInfo(untouched)));
        Assert.Equal(assets.Files.GetFileInfo(untouched).PhysicalPath, provider.GetFileInfo(untouched).PhysicalPath);
        Assert.False(provider.GetFileInfo("/missing.js").Exists);
    }

    /// <summary>
    /// Checks that the provider reads each Umbraco file and logs each change it cannot apply once, while preparing the
    /// file; later requests neither read the file again nor log more warnings.
    /// </summary>
    [Fact]
    public async Task Files_are_read_and_missing_patterns_reported_only_during_preparation()
    {
        var login = new ChangedLogin();
        var logger = new RecordedWarnings();
        var provider = new BackOfficeAssetFileProvider(new LoginOverride(assets.Files, login), new TestBackOfficePaths(Prefix), Prefix,
            UmbracoUnderTest.Release, logger);
        Assert.Equal(1, login.Reads);
        Assert.Equal(3, logger.Messages.Count);
        Assert.Equal(3, logger.Messages.Distinct().Count());
        Assert.All(logger.Messages, message => Assert.Contains("login.js", message));
        for (var attempt = 0; attempt < 3; attempt++)
            await ReadAsync(provider.GetFileInfo(Login));
        Assert.Equal(1, login.Reads);
        Assert.Equal(3, logger.Messages.Count);
    }

    /// <summary>
    /// Checks that the static-file middleware serves patched files with their patched lengths and validators, and
    /// answers HEAD, conditional and range requests from the patched bytes.
    /// </summary>
    [Fact]
    public async Task Static_file_responses_use_patched_lengths_validators_and_ranges()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IBackOfficePathGenerator>(new TestBackOfficePaths(Prefix));
        await using var app = builder.Build();
        var provider = CreateProvider();
        app.Environment.WebRootFileProvider = provider;
        app.UsePathBase(Prefix);
        app.UseUmbracoPathsUnderPathBase();
        app.UseUmbracoBackOfficeRewrites();
        app.UseStaticFiles();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        foreach (var path in new[] { Login, WebsiteCss, "/umbraco/backoffice/packages/core/manifests.js", "/umbraco/backoffice/css/umb-css.css" })
        {
            var expected = await ReadAsync(provider.GetFileInfo(path));
            var url = path.StartsWith(AssetPatcher.AssetsRoot, StringComparison.Ordinal)
                ? new TestBackOfficePaths(Prefix).BackOfficeAssetsPath + path[AssetPatcher.AssetsRoot.Length..] : Prefix + path;
            using var response = await http.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal(expected.LongLength, response.Content.Headers.ContentLength);
            Assert.NotNull(response.Headers.ETag);
            Assert.NotNull(response.Content.Headers.LastModified);

            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var headers = await http.SendAsync(head, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, headers.StatusCode);
            Assert.Equal(expected.LongLength, headers.Content.Headers.ContentLength);
            Assert.Equal(response.Headers.ETag, headers.Headers.ETag);
            Assert.Empty(await headers.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            using var conditional = new HttpRequestMessage(HttpMethod.Get, url);
            conditional.Headers.IfNoneMatch.Add(response.Headers.ETag);
            using var cached = await http.SendAsync(conditional, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            Assert.Empty(await cached.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            using var range = new HttpRequestMessage(HttpMethod.Get, url);
            range.Headers.Range = new RangeHeaderValue(0, 31);
            using var partial = await http.SendAsync(range, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.Equal(expected[..32], await partial.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal(expected.LongLength, partial.Content.Headers.ContentRange?.Length);
        }
        await app.StopAsync(CancellationToken.None);
    }

    /// <summary>Creates a provider that patches the restored assets for a site under <see cref="Prefix"/>.</summary>
    private BackOfficeAssetFileProvider CreateProvider()
        => new(assets.Files, new TestBackOfficePaths(Prefix), Prefix, UmbracoUnderTest.Release, NullLogger.Instance);

    /// <summary>Reads every byte of <paramref name="file"/>.</summary>
    private static async Task<byte[]> ReadAsync(IFileInfo file)
    {
        await using var source = file.CreateReadStream();
        using var output = new MemoryStream();
        await source.CopyToAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    /// <summary>
    /// Serves <paramref name="login"/> as Umbraco's <c>login.js</c>, and every other file from <paramref name="inner"/>.
    /// </summary>
    private sealed class LoginOverride(IFileProvider inner, IFileInfo login) : IFileProvider
    {
        /// <summary>
        /// Returns the replacement for <c>login.js</c>, and the inner provider's file for any other path.
        /// </summary>
        public IFileInfo GetFileInfo(string subpath) => subpath == Login ? login : inner.GetFileInfo(subpath);
        /// <summary>Lists the inner provider's directory.</summary>
        public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);
        /// <summary>Watches the inner provider's files.</summary>
        public IChangeToken Watch(string filter) => inner.Watch(filter);
    }

    /// <summary>A <c>login.js</c> whose code Umbraco has changed, which counts how often it is read.</summary>
    private sealed class ChangedLogin : IFileInfo
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes("/* upstream changed */");
        public int Reads { get; private set; }
        public bool Exists => true;
        public bool IsDirectory => false;
        public string Name => "login.js";
        public long Length => _bytes.LongLength;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public string? PhysicalPath => null;
        /// <summary>Counts the read and opens a read-only stream over the file's bytes.</summary>
        public Stream CreateReadStream() { Reads++; return new MemoryStream(_bytes, writable: false); }
    }

    /// <summary>A logger that keeps the text of each warning.</summary>
    private sealed class RecordedWarnings : ILogger
    {
        public List<string> Messages { get; } = [];
        /// <summary>Starts no scope and returns <see langword="null"/>.</summary>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <summary>Returns whether <paramref name="level"/> is <see cref="LogLevel.Warning"/>.</summary>
        public bool IsEnabled(LogLevel level) => level == LogLevel.Warning;
        /// <summary>Keeps the formatted message of each warning.</summary>
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning) Messages.Add(formatter(state, exception));
        }
    }
}
