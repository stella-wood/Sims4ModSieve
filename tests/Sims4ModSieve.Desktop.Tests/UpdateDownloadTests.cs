using System.Net;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Sims4ModSieve.Core.Duplicates;
using Sims4ModSieve.Desktop.Services;
using static Sims4ModSieve.Desktop.Tests.SettingsViewModelTests;

namespace Sims4ModSieve.Desktop.Tests;

[TestClass]
public sealed class UpdateDownloadTests
{
    private const string Version = "99.0.0";
    private const string Name = "Sims4ModSieve-99.0.0-setup.exe";
    private const string AssetUrl = UpdateCheckService.ReleasePrefix + "download/v99.0.0/" + Name;
    private static readonly byte[] Payload = "verified installer test payload"u8.ToArray();
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Payload));

    [TestMethod]
    [DataRow("valid", true)]
    [DataRow("missing-hash", false)]
    [DataRow("wrong-version", false)]
    [DataRow("other-repo", false)]
    [DataRow("duplicate", false)]
    [DataRow("oversized", false)]
    public async Task InstallerAssetsMustMatchReleaseAndRepository(string scenario, bool expected)
    {
        using var handler = new Handler((_, _) => Task.FromResult(JsonResponse(ReleaseJson(scenario))));
        using var client = new HttpClient(handler);
        var update = await new UpdateCheckService(client).CheckAsync("0.1.0");
        Assert.IsNotNull(update);
        Assert.AreEqual(expected, update.Installer is not null);
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("hash")]
    [DataRow("truncated")]
    [DataRow("oversized")]
    [DataRow("invalid-checksum")]
    [DataRow("redirect")]
    [DataRow("offline")]
    public async Task DownloadOnlyReturnsVerifiedCompleteInstaller(string scenario)
    {
        using var files = new Scenario();
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(scenario == "invalid-checksum" ? "invalid" : Hash) });
            if (scenario == "offline") throw new HttpRequestException();
            if (scenario == "redirect") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
                { Headers = { Location = new Uri("https://example.com/setup.exe") } });
            var payload = scenario switch
            {
                "hash" => new byte[Payload.Length], "truncated" => Payload[..^1],
                "oversized" => Payload.Concat(new byte[1]).ToArray(), _ => Payload,
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        });
        using var client = new HttpClient(handler);
        var service = new UpdateCheckService(client);
        var download = service.DownloadAsync(Update(), temporaryRoot: files.Root);
        if (scenario == "valid")
        {
            var result = await download;
            CollectionAssert.AreEqual(Payload, File.ReadAllBytes(result.Path));
            Assert.AreEqual(Hash, result.Sha256);
            Assert.AreEqual(Version, result.Version);
            UpdateCheckService.DiscardDownload(result.Path);
        }
        else
        {
            await Assert.ThrowsAsync<Exception>(async () => await download);
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(files.Root).Any());
        }
    }

    [TestMethod]
    public async Task CancellationDeletesPartialDownload()
    {
        using var files = new Scenario();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent(Hash) : new StreamContent(new PausingStream(reading)),
        }));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var download = new UpdateCheckService(client).DownloadAsync(Update(), cancellationToken: cancellation.Token, temporaryRoot: files.Root);
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await download);
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(files.Root).Any());
    }

    [TestMethod]
    public async Task StartupCleanupRemovesInstallerAndInterruptedHandoffFiles()
    {
        using var files = new Scenario();
        var cache = CreateDownloadCache(files.Root);
        var handoff = Path.Combine(cache, "handoff-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(handoff, Version);
        File.WriteAllText(handoff + ".accepted", Version);
        var empty = files.Folder("Sims4ModSieve-update-" + Guid.NewGuid().ToString("N"));
        await UpdateCheckService.CleanupDownloadsAsync(temporaryRoot: files.Root);
        Assert.IsFalse(Directory.Exists(cache));
        Assert.IsFalse(Directory.Exists(empty));
    }

    [TestMethod]
    public async Task StartupCleanupWaitsForInstallerReleaseAndLeavesNewDownloadsAlone()
    {
        using var files = new Scenario();
        var cache = CreateDownloadCache(files.Root);
        Task cleanup;
        string next;
        using (var installer = new FileStream(Path.Combine(cache, "setup.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            cleanup = UpdateCheckService.CleanupDownloadsAsync(temporaryRoot: files.Root);
            next = CreateDownloadCache(files.Root);
            await Task.Delay(100);
            Assert.IsFalse(cleanup.IsCompleted);
            Assert.IsTrue(File.Exists(Path.Combine(cache, "setup.exe")));
        }
        await cleanup.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(Directory.Exists(cache));
        CollectionAssert.AreEqual(Payload, File.ReadAllBytes(Path.Combine(next, "setup.exe")));
    }

    [TestMethod]
    public async Task StartupCleanupSkipsUnrecognizedFilesAndDirectoryLinks()
    {
        using var files = new Scenario();
        var unexpected = CreateDownloadCache(files.Root);
        var note = Path.Combine(unexpected, "keep.txt");
        File.WriteAllText(note, "user data");
        var outside = files.Folder("Outside");
        File.WriteAllBytes(Path.Combine(outside, "setup.exe"), Payload);
        var invalidName = files.Folder("Sims4ModSieve-update-not-a-guid");
        File.WriteAllBytes(Path.Combine(invalidName, "setup.exe"), Payload);
        var link = Path.Combine(files.Root, "Sims4ModSieve-update-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode);
        try
        {
            await UpdateCheckService.CleanupDownloadsAsync(temporaryRoot: files.Root);
            Assert.AreEqual("user data", File.ReadAllText(note));
            Assert.IsTrue(File.Exists(Path.Combine(unexpected, "setup.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(invalidName, "setup.exe")));
            CollectionAssert.AreEqual(Payload, File.ReadAllBytes(Path.Combine(outside, "setup.exe")));
            Assert.IsTrue(Directory.Exists(link));
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public async Task CancelledStartupCleanupLeavesRunningInstallerIntact()
    {
        using var files = new Scenario();
        var cache = CreateDownloadCache(files.Root);
        using var installer = new FileStream(Path.Combine(cache, "setup.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancellation = new CancellationTokenSource();
        var cleanup = UpdateCheckService.CleanupDownloadsAsync(cancellation.Token, files.Root);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cleanup);
        Assert.IsTrue(File.Exists(Path.Combine(cache, "setup.exe")));
    }

    [TestMethod]
    public Task RestartWaitsForScanAndLaunchFailureCanRetry() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        files.Store.Save(new(files.Folder("Mods"), []));
        var runner = new WaitingRun();
        var launches = 0;
        var closed = false;
        var model = files.Model(run: runner, startUpdate: _ =>
        {
            launches++;
            return Task.FromException(new IOException("test launch failure"));
        });
        model.UpdateRestartRequested += (_, _) => closed = true;
        using var handler = ValidHandler();
        using var client = new HttpClient(handler);
        await model.CheckForUpdatesAsync(new(client));
        await model.DownloadUpdateCommand.ExecuteAsync();
        Assert.IsTrue(model.ShowUpdateRestart);
        var scan = model.StartScanCommand.ExecuteAsync();
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restart = model.RestartUpdateCommand.ExecuteAsync();
        Assert.IsFalse(restart.IsCompleted);
        Assert.AreEqual(0, launches);
        model.CancelScanCommand.Execute(null);
        await scan;
        await restart.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, launches);
        Assert.IsFalse(closed);
        Assert.IsFalse(model.IsBusy);
        Assert.IsTrue(model.RestartUpdateCommand.CanExecute(null));
        await model.RestartUpdateCommand.ExecuteAsync();
        Assert.AreEqual(2, launches);
        model.StopUpdates();
    });

    [TestMethod]
    public Task FailedDownloadCanRetryAndSuccessfulHandoffRequestsClose() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var fail = true;
        var closed = false;
        DownloadedUpdate? launched = null;
        var model = files.Model(startUpdate: download => { launched = download; return Task.CompletedTask; });
        model.UpdateRestartRequested += (_, _) => closed = true;
        using var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("latest", StringComparison.Ordinal)) return Task.FromResult(JsonResponse(ReleaseJson()));
            if (request.RequestUri.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Hash) });
            if (fail) throw new HttpRequestException();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        });
        using var client = new HttpClient(handler);
        await model.CheckForUpdatesAsync(new(client));
        await model.DownloadUpdateCommand.ExecuteAsync();
        Assert.IsTrue(model.ShowUpdateDownload);
        Assert.IsFalse(model.ShowUpdateRestart);
        Assert.IsTrue(model.DownloadUpdateCommand.CanExecute(null));
        fail = false;
        await model.DownloadUpdateCommand.ExecuteAsync();
        Assert.IsFalse(model.ShowUpdateDownload);
        Assert.IsTrue(model.ShowUpdateRestart);
        await model.RestartUpdateCommand.ExecuteAsync();
        Assert.IsNotNull(launched);
        Assert.IsTrue(closed);
        UpdateCheckService.DiscardDownload(launched.Path);
        model.StopUpdates();
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task CancelButtonAndWindowCloseCancelDownload(bool closeWindow) => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("latest", StringComparison.Ordinal) ? JsonResponse(ReleaseJson())
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal)
                    ? new StringContent(Hash) : new StreamContent(new PausingStream(reading)),
            }));
        using var client = new HttpClient(handler);
        var model = files.Model();
        var window = new MainWindow(model);
        window.Show();
        WpfTestHost.PlaceOnScreen(window);
        await model.CheckForUpdatesAsync(new(client));
        await WpfTestHost.LayoutAsync(window);
        await WpfTestHost.ClickAsync((Button)window.FindName("UpdateButton"));
        var downloading = model.DownloadUpdateCommand.ExecuteAsync();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WpfTestHost.LayoutAsync(window);
        var cancel = (Button)window.FindName("UpdateCancelButton");
        Assert.IsTrue(cancel.IsVisible && cancel.IsEnabled);
        Assert.IsFalse(model.IsBusy);
        UpdateCheckTests.SaveScreenshot(window, (Popup)window.FindName("UpdatePopup"), "home-update-downloading.png");
        if (closeWindow) window.Close();
        else await WpfTestHost.ClickAsync(cancel);
        await downloading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(model.IsUpdateDownloading);
        Assert.IsFalse(model.ShowUpdateRestart);
        Assert.AreEqual("已取消下载", model.UpdateStatusText);
        window.Close();
    });

    [TestMethod]
    public Task ChangedDownloadedPackageRequiresFreshDownload() => WpfTestHost.RunAsync(async () =>
    {
        using var files = new Scenario();
        var model = files.Model(startUpdate: _ => Task.FromException(new InvalidDataException("更新包已发生变化，请重新下载")));
        using var handler = ValidHandler();
        using var client = new HttpClient(handler);
        await model.CheckForUpdatesAsync(new(client));
        await model.DownloadUpdateCommand.ExecuteAsync();
        await model.RestartUpdateCommand.ExecuteAsync();
        Assert.IsTrue(model.ShowUpdateDownload);
        Assert.IsFalse(model.ShowUpdateRestart);
        Assert.IsTrue(model.DownloadUpdateCommand.CanExecute(null));
        model.StopUpdates();
    });

    private static string CreateDownloadCache(string root)
    {
        var directory = Path.Combine(root, "Sims4ModSieve-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "setup.exe"), Payload);
        return directory;
    }

    private static AvailableUpdate Update() => new(Version, UpdateCheckService.ReleasePrefix + "tag/v" + Version,
        new(AssetUrl, AssetUrl + ".sha256", Payload.Length));

    internal static string ReleaseJson(string scenario = "valid")
    {
        var assets = new List<object>();
        var url = scenario == "other-repo" ? AssetUrl.Replace("stella-wood", "someone", StringComparison.Ordinal) : AssetUrl;
        var name = scenario == "wrong-version" ? "Sims4ModSieve-98.0.0-setup.exe" : Name;
        var size = scenario == "oversized" ? UpdateCheckService.MaximumInstallerBytes + 1 : Payload.Length;
        assets.Add(new { name, browser_download_url = url, size });
        if (scenario == "duplicate") assets.Add(new { name, browser_download_url = url, size });
        if (scenario != "missing-hash") assets.Add(new { name = Name + ".sha256", browser_download_url = AssetUrl + ".sha256", size = 65 });
        return JsonSerializer.Serialize(new { tag_name = "v" + Version, html_url = UpdateCheckService.ReleasePrefix + "tag/v" + Version, assets });
    }

    internal static HttpMessageHandler ValidHandler() => new Handler((request, _) => Task.FromResult(
        request.RequestUri!.AbsolutePath.EndsWith("latest", StringComparison.Ordinal) ? JsonResponse(ReleaseJson())
        : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri.AbsoluteUri.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent(Hash) : new ByteArrayContent(Payload),
        }));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class PausingStream(TaskCompletionSource reading) : MemoryStream(Payload)
    {
        private bool paused;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!paused) { paused = true; return await base.ReadAsync(buffer[..5], cancellationToken); }
            reading.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class WaitingRun : IDuplicateRunService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DuplicateReport> RunAsync(DuplicateRunInput input, IProgress<DuplicateScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
