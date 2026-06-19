using System.Net;
using System.Security.Cryptography;
using System.Text;
using EasyOcrSharp.Internal;
using EasyOcrSharp.Services;
using Xunit;

namespace EasyOcrSharp.Tests;

/// <summary>
/// CI-safe tests (stub HttpClient, temp cache, no network) for the hardened model-download path:
/// checksum verification, retry/backoff, HTTP-range resume, HTTPS-only enforcement, fail-closed on a
/// missing checksum, and file-name traversal rejection.
/// </summary>
public sealed class ReviewDownloadHardeningTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "eos-dl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Ok(byte[] body)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static ModelDownloadOptions OptionsWith(HttpMessageHandler handler) => new()
    {
        HttpClientFactory = () => new HttpClient(handler),
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task DownloadsAndVerifiesChecksum()
    {
        var content = Encoding.UTF8.GetBytes("a-valid-model-blob");
        var asset = new ModelAsset("good.onnx", "https://example.test/good.onnx", Sha(content));

        var path = await ModelDownloadManager.EnsureModelAsync(asset, _cacheDir, OptionsWith(new StubHandler(_ => Ok(content))), null, default);

        Assert.True(File.Exists(path));
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ChecksumMismatchThrowsAndDeletesPartFile()
    {
        var expected = Encoding.UTF8.GetBytes("expected");
        var served = Encoding.UTF8.GetBytes("tampered-bytes");
        var asset = new ModelAsset("bad.onnx", "https://example.test/bad.onnx", Sha(expected));

        await Assert.ThrowsAsync<ModelChecksumException>(() =>
            ModelDownloadManager.EnsureModelAsync(asset, _cacheDir, OptionsWith(new StubHandler(_ => Ok(served))), null, default));

        Assert.False(File.Exists(Path.Combine(_cacheDir, "bad.onnx")));
        Assert.False(File.Exists(Path.Combine(_cacheDir, "bad.onnx.part")));
    }

    [Fact]
    public async Task RetriesTransientFailuresThenSucceeds()
    {
        var content = Encoding.UTF8.GetBytes("blob");
        var asset = new ModelAsset("retry.onnx", "https://example.test/retry.onnx", Sha(content));
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            if (++calls < 3) throw new HttpRequestException("transient");
            return Ok(content);
        });

        var path = await ModelDownloadManager.EnsureModelAsync(asset, _cacheDir, OptionsWith(handler), null, default);

        Assert.True(File.Exists(path));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ResumesPartialDownloadViaHttpRange()
    {
        var content = Encoding.UTF8.GetBytes("0123456789ABCDEF");
        const int split = 8;
        Directory.CreateDirectory(_cacheDir);
        await File.WriteAllBytesAsync(Path.Combine(_cacheDir, "resume.onnx.part"), content[..split]);
        var asset = new ModelAsset("resume.onnx", "https://example.test/resume.onnx", Sha(content));

        var handler = new StubHandler(req => req.Headers.Range is not null
            ? new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(content[split..]) }
            : Ok(content));

        var path = await ModelDownloadManager.EnsureModelAsync(asset, _cacheDir, OptionsWith(handler), null, default);

        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task RejectsNonHttpsOverrideUnlessAllowed()
    {
        var content = Encoding.UTF8.GetBytes("blob");
        var asset = new ModelAsset("m.onnx", "https://example.test/m.onnx", Sha(content));

        var insecure = OptionsWith(new StubHandler(_ => Ok(content)));
        insecure.BaseUrlOverride = "http://insecure.test/models";
        await Assert.ThrowsAsync<ModelDownloadException>(() =>
            ModelDownloadManager.EnsureModelAsync(asset, _cacheDir, insecure, null, default));

        var allowed = OptionsWith(new StubHandler(_ => Ok(content)));
        allowed.BaseUrlOverride = "http://insecure.test/models";
        allowed.AllowInsecureModelSource = true;
        var path = await ModelDownloadManager.EnsureModelAsync(asset, _cacheDir, allowed, null, default);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task FailsClosedWhenChecksumUnknownUnlessOptedIn()
    {
        var content = Encoding.UTF8.GetBytes("unverified-blob");
        var asset = new ModelAsset("nochk.onnx", "https://example.test/nochk.onnx", Sha256: null);

        await Assert.ThrowsAsync<ModelChecksumException>(() =>
            ModelDownloadManager.EnsureModelAsync(asset, _cacheDir, OptionsWith(new StubHandler(_ => Ok(content))), null, default));

        var opts = OptionsWith(new StubHandler(_ => Ok(content)));
        opts.AllowUnverifiedModels = true;
        var path = await ModelDownloadManager.EnsureModelAsync(asset, Path.Combine(_cacheDir, "ok"), opts, null, default);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RejectsFileNameTraversal()
    {
        var content = Encoding.UTF8.GetBytes("blob");
        var asset = new ModelAsset("../evil.onnx", "https://example.test/evil.onnx", Sha(content));

        await Assert.ThrowsAsync<ModelDownloadException>(() =>
            ModelDownloadManager.EnsureModelAsync(asset, _cacheDir, OptionsWith(new StubHandler(_ => Ok(content))), null, default));
    }
}
