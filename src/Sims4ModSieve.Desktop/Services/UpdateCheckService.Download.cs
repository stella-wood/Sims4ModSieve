using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Sims4ModSieve.Desktop.Services;

public sealed record DownloadedUpdate(string Version, string Path, string Sha256);

public sealed partial class UpdateCheckService
{
    private const string DownloadDirectoryPrefix = "Sims4ModSieve-update-";
    public const long MaximumInstallerBytes = 512L * 1024 * 1024;
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{C897868A-0997-463F-B377-A68A616F831E}_is1";

    private static bool IsAssetUrl(string url, string tag, string name) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.AbsoluteUri == ReleasePrefix + "download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name);

    public async Task<DownloadedUpdate> DownloadAsync(AvailableUpdate update, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default, string? temporaryRoot = null)
    {
        var installer = update.Installer ?? throw new InvalidDataException("此版本缺少安装包或校验信息，请稍后重试");
        var directory = Path.Combine(temporaryRoot ?? Path.GetTempPath(), DownloadDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "setup.exe");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using var checksumResponse = await GetAssetAsync(installer.ChecksumUrl, timeout.Token).ConfigureAwait(false);
            await using var checksumStream = await checksumResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var checksumBytes = new byte[256];
            var checksumCount = await checksumStream.ReadAtLeastAsync(checksumBytes, checksumBytes.Length, throwOnEndOfStream: false,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var hash = Encoding.ASCII.GetString(checksumBytes, 0, checksumCount).Trim();
            if (checksumCount == checksumBytes.Length || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("更新校验信息无效，请稍后重试");

            using var response = await GetAssetAsync(installer.DownloadUrl, timeout.Token).ConfigureAwait(false);
            if (installer.Size is <= 0 or > MaximumInstallerBytes
                || response.Content.Headers.ContentLength is long length && length != installer.Size)
                throw new InvalidDataException("更新包大小不匹配，请重新下载");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > installer.Size) throw new InvalidDataException("更新包大小不匹配，请重新下载");
                    sha256.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    progress?.Report(100d * received / installer.Size);
                }
                if (received != installer.Size || !Convert.ToHexString(sha256.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包校验失败，请重新下载");
            }
            timeout.Token.ThrowIfCancellationRequested();
            return new(update.Version, path, hash);
        }
        catch
        {
            DiscardDownload(path);
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetAssetAsync(string url, CancellationToken token)
    {
        var uri = new Uri(url);
        if (!uri.AbsoluteUri.StartsWith(ReleasePrefix + "download/", StringComparison.Ordinal))
            throw new InvalidDataException("更新下载来源无效");
        for (var i = 0; i < 6; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Sims4ModSieve-Updater");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                if (next.Scheme != "https" || !next.IsDefaultPort || next.UserInfo.Length != 0
                    || next.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("更新下载跳转来源无效");
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException("更新下载失败", null, status);
            }
            return response;
        }
        throw new HttpRequestException("更新下载跳转过多");
    }

    public static async Task StartInstallerAsync(DownloadedUpdate download, string applicationDirectory,
        CancellationToken token = default)
    {
        using var registration = Registry.CurrentUser.OpenSubKey(UninstallKey);
        var installed = registration?.GetValue("InstallLocation") as string;
        if (installed is null || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed))
            .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请先通过安装包安装工具，再使用应用内更新");
        // 保持下载文件不可写到安装器完成交接，避免校验后被替换。
        await using var file = new FileStream(download.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(file, token);
        if (!Convert.ToHexString(hash).Equals(download.Sha256, StringComparison.OrdinalIgnoreCase)
            || FileVersionInfo.GetVersionInfo(download.Path).ProductVersion?.Trim() != download.Version)
            throw new InvalidDataException("更新包已发生变化，请重新下载");
        var handoff = Path.Combine(Path.GetDirectoryName(download.Path)!, "handoff-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(download.Path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(download.Path)! };
        foreach (var argument in new[] { "/SILENT", "/SP-", "/NORESTART", "/SIEVEHANDOFF=" + handoff,
            "/SIEVEWAITPID=" + Environment.ProcessId, "/DIR=" + applicationDirectory })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新安装包");
        var watch = Stopwatch.StartNew();
        while (!File.Exists(handoff))
        {
            if (process.HasExited) throw new IOException("更新安装包已退出，当前版本未关闭");
            if (watch.Elapsed > TimeSpan.FromSeconds(60)) throw new IOException("更新安装包未能及时启动，请重试");
            await Task.Delay(100, token);
        }
        if (await File.ReadAllTextAsync(handoff, token) != download.Version) throw new InvalidDataException("更新交接信息无效");
        await File.WriteAllTextAsync(handoff + ".accepted", download.Version, new UTF8Encoding(false), token);
        File.Delete(handoff);
    }

    public static void DiscardDownload(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            if (!Path.GetFileName(path).Equals("setup.exe", StringComparison.OrdinalIgnoreCase)) return;
            var files = GetDownloadCleanupFiles(directory);
            if (files is null) return;
            // 先删安装包；仍被安装器占用时，交接文件也必须保留。
            File.Delete(path);
            foreach (var file in files)
                if (!Path.GetFileName(file).Equals("setup.exe", StringComparison.OrdinalIgnoreCase)) File.Delete(file);
            Directory.Delete(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public static Task CleanupDownloadsAsync(CancellationToken cancellationToken = default, string? temporaryRoot = null)
    {
        string[] directories;
        try
        {
            // 启动时先固定清理范围，后续新下载的目录不进入本次清理。
            directories = Directory.GetDirectories(temporaryRoot ?? Path.GetTempPath(), DownloadDirectoryPrefix + "*")
                .Where(directory => GetDownloadCleanupFiles(directory) is not null).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return Task.CompletedTask; }
        return Task.Run(async () =>
        {
            var pending = directories.ToList();
            for (var attempt = 0; attempt < 60 && pending.Count > 0; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pending.RemoveAll(directory =>
                {
                    DiscardDownload(Path.Combine(directory, "setup.exe"));
                    return !Directory.Exists(directory);
                });
                if (pending.Count > 0) await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken);
    }

    private static string[]? GetDownloadCleanupFiles(string directory)
    {
        var name = Path.GetFileName(directory);
        if (!name.StartsWith(DownloadDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(name[DownloadDirectoryPrefix.Length..], "N", out _)) return null;
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
            var files = Directory.GetFileSystemEntries(directory);
            return files.All(path =>
            {
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
                var file = Path.GetFileName(path);
                if (file.Equals("setup.exe", StringComparison.OrdinalIgnoreCase)) return true;
                if (!file.StartsWith("handoff-", StringComparison.OrdinalIgnoreCase)) return false;
                var id = file["handoff-".Length..];
                if (id.EndsWith(".accepted", StringComparison.OrdinalIgnoreCase)) id = id[..^".accepted".Length];
                return Guid.TryParseExact(id, "N", out _);
            }) ? files : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}
