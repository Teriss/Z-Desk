using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ZDesk.Models;

namespace ZDesk.Services;

public sealed class UpdateService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<UpdateManifest?> CheckAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("更新清单必须使用 HTTPS 地址。");
        await using var stream = await Client.GetStreamAsync(uri, cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("更新清单为空。");
        if (!Version.TryParse(manifest.Version, out _) || string.IsNullOrWhiteSpace(manifest.DownloadUrl) || manifest.Sha256.Length != 64)
            throw new InvalidDataException("更新清单格式无效。");
        return manifest;
    }

    public static bool IsNewer(UpdateManifest manifest)
    {
        var current = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);
        return Version.TryParse(manifest.Version, out var candidate) && candidate > current;
    }

    public async Task<string> DownloadAndVerifyAsync(UpdateManifest manifest, string stagingDirectory, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("更新包必须使用 HTTPS 地址。");
        Directory.CreateDirectory(stagingDirectory);
        var path = Path.Combine(stagingDirectory, $"ZDesk-{manifest.Version}.exe.download");
        await using (var source = await Client.GetStreamAsync(uri, cancellationToken))
        await using (var target = File.Create(path)) await source.CopyToAsync(target, cancellationToken);
        await using var verify = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
        if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("更新包 SHA-256 校验失败，已删除下载文件。");
        }
        return path;
    }
}
