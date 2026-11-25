using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Sims4ModSieve.Desktop.Services;

public sealed record UpdateInstaller(string DownloadUrl, string ChecksumUrl, long? Size = null);
public sealed record AvailableUpdate(string Version, string ReleaseUrl, UpdateInstaller? Installer = null);
public enum UpdateCheckStatus { Available, UpToDate, Failed }
public sealed record UpdateCheckResult(UpdateCheckStatus Status, AvailableUpdate? Update = null, string? FailureReason = null);

public sealed partial class UpdateCheckService(HttpClient client)
{
    public const string ReleasePrefix = "https://github.com/stella-wood/Sims4ModSieve/releases/";
    public const string LatestReleaseUrl = ReleasePrefix + "latest";
    private const string ReleaseTagPrefix = ReleasePrefix + "tag/";

    public static bool IsReleaseUrl(string url) => url.StartsWith(ReleasePrefix, StringComparison.Ordinal)
        && Uri.TryCreate(url, UriKind.Absolute, out _);

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.UserAgent.ParseAdd("Sims4ModSieve/" + currentVersion);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (response.StatusCode != HttpStatusCode.Found)
            {
                return new(UpdateCheckStatus.Failed, FailureReason: response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => "GitHub 拒绝了请求",
                    HttpStatusCode.TooManyRequests => "GitHub 请求过于频繁",
                    _ => $"GitHub 返回了意外响应（{(int)response.StatusCode}）",
                });
            }

            if (response.Headers.Location is not { } location || !Uri.TryCreate(request.RequestUri!, location, out var release)
                || GetReleaseTag(release.AbsoluteUri) is not { } tag || !TryCompareVersions(tag, currentVersion, out var comparison))
                return new(UpdateCheckStatus.Failed, FailureReason: "无法读取发布页版本");

            if (comparison <= 0) return new(UpdateCheckStatus.UpToDate);

            var version = tag.TrimStart('v');
            var url = GetAssetUrl(tag, GetInstallerName(version));
            return new(UpdateCheckStatus.Available, new(version, release.AbsoluteUri, new(url, url + ".sha256")));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(UpdateCheckStatus.Failed, FailureReason: "连接 GitHub 超时");
        }
        catch (HttpRequestException)
        {
            return new(UpdateCheckStatus.Failed, FailureReason: "无法连接 GitHub");
        }
    }

    private static string? GetReleaseTag(string url) => url.StartsWith(ReleaseTagPrefix, StringComparison.Ordinal)
        ? Uri.UnescapeDataString(url[ReleaseTagPrefix.Length..]) : null;

    private static string GetInstallerName(string version) => $"Sims4ModSieve-{version}-setup.exe";

    private static string GetAssetUrl(string tag, string name) => ReleasePrefix + "download/"
        + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name);

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
