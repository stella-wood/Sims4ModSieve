using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sims4ModSieve.Desktop.Services;

public sealed record UpdateInstaller(string DownloadUrl, string ChecksumUrl, long Size);
public sealed record AvailableUpdate(string Version, string ReleaseUrl, UpdateInstaller? Installer = null);

public sealed partial class UpdateCheckService(HttpClient client)
{
    public const string ReleasePrefix = "https://github.com/stella-wood/Sims4ModSieve/releases/";

    public static bool IsReleaseUrl(string url) => url.StartsWith(ReleasePrefix, StringComparison.Ordinal)
        && Uri.TryCreate(url, UriKind.Absolute, out _);

    public async Task<AvailableUpdate?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/stella-wood/Sims4ModSieve/releases/latest");
            request.Headers.UserAgent.ParseAdd("Sims4ModSieve/" + currentVersion);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var tag = json.RootElement.GetProperty("tag_name").GetString();
            var url = json.RootElement.GetProperty("html_url").GetString();
            if (tag is null || url is null || !IsReleaseUrl(url)
                || !TryCompareVersions(tag, currentVersion, out var comparison) || comparison <= 0) return null;
            timeout.Token.ThrowIfCancellationRequested();
            var version = tag.TrimStart('v');
            var name = $"Sims4ModSieve-{version}-setup.exe";
            UpdateInstaller? installer = null;
            if (json.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var matches = assets.EnumerateArray().Where(asset => asset.TryGetProperty("name", out var assetName)
                    && assetName.ValueKind == JsonValueKind.String && assetName.GetString() == name).ToArray();
                var hashes = assets.EnumerateArray().Where(asset => asset.TryGetProperty("name", out var assetName)
                    && assetName.ValueKind == JsonValueKind.String && assetName.GetString() == name + ".sha256").ToArray();
                if (matches.Length == 1 && hashes.Length == 1
                    && matches[0].TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes)
                    && bytes is > 0 and <= MaximumInstallerBytes
                    && matches[0].TryGetProperty("browser_download_url", out var download)
                    && hashes[0].TryGetProperty("browser_download_url", out var checksum)
                    && download.ValueKind == JsonValueKind.String && checksum.ValueKind == JsonValueKind.String
                    && IsAssetUrl(download.GetString()!, tag, name) && IsAssetUrl(checksum.GetString()!, tag, name + ".sha256"))
                    installer = new(download.GetString()!, checksum.GetString()!, bytes);
            }
            return new AvailableUpdate(version, url, installer);
        }
        catch (Exception)
        {
            // 更新检查是可选的网络操作，失败不进入应用错误诊断。
            return null;
        }
    }

    public static bool TryCompareVersions(string left, string right, out int comparison)
    {
        comparison = 0;
        var a = SemVersionPattern().Match(left);
        var b = SemVersionPattern().Match(right);
        if (!a.Success || !b.Success) return false;
        var ap = a.Groups[4].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var bp = b.Groups[4].Value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (ap.Concat(bp).Any(p => IsNumeric(p) && p.Length > 1 && p[0] == '0')) return false;
        for (var i = 1; i <= 3; i++)
        {
            comparison = BigInteger.Parse(a.Groups[i].Value).CompareTo(BigInteger.Parse(b.Groups[i].Value));
            if (comparison != 0) return true;
        }
        if (ap.Length == 0 || bp.Length == 0)
        {
            comparison = (ap.Length == 0).CompareTo(bp.Length == 0);
            return true;
        }
        for (var i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            var an = IsNumeric(ap[i]);
            var bn = IsNumeric(bp[i]);
            comparison = an && bn ? BigInteger.Parse(ap[i]).CompareTo(BigInteger.Parse(bp[i]))
                : an != bn ? (an ? -1 : 1) : string.CompareOrdinal(ap[i], bp[i]);
            if (comparison != 0) return true;
        }
        comparison = ap.Length.CompareTo(bp.Length);
        return true;
    }

    private static bool IsNumeric(string text) => text.All(c => c is >= '0' and <= '9');

    [GeneratedRegex(@"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SemVersionPattern();
}
