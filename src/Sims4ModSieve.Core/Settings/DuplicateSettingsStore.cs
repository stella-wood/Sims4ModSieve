using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sims4ModSieve.Core.Duplicates;

namespace Sims4ModSieve.Core.Settings;

public sealed record SavedScanSource(string Path, bool Enabled);

public sealed record DuplicateSettings(string? ModsRoot, IReadOnlyList<SavedScanSource> Sources, string? TrayRoot = null)
{
    public DuplicateHashConcurrency HashConcurrency { get; init; }
}

public interface IDuplicateSettingsStore
{
    DuplicateSettings? Load();

    void Save(DuplicateSettings settings);
}

public sealed class DuplicateSettingsStore : IDuplicateSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string settingsPath;
    private readonly string[] legacySettingsPaths;
    public string DataDirectory => Path.GetDirectoryName(settingsPath)!;

    public DuplicateSettingsStore(string? localApplicationData = null)
    {
        var directory = localApplicationData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        settingsPath = Path.GetFullPath(Path.Combine(directory, "Sims4ModSieve", "duplicate-settings.json"));
        legacySettingsPaths =
        [
            Path.Combine(directory, "Sim4ModSieve", "duplicate-settings.json"),
            Path.Combine(directory, "Sims4ModDoctor", "duplicate-settings.json"),
        ];
    }

    public DuplicateSettings? Load()
    {
        foreach (var path in new[] { settingsPath }.Concat(legacySettingsPaths))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var settings = JsonSerializer.Deserialize<DuplicateSettings>(reader.ReadToEnd(), JsonOptions);
                if (settings?.Sources is null || settings.Sources.Any(source => source is null || !IsPath(source.Path))
                    || settings.ModsRoot is { } mods && !IsPath(mods)
                    || settings.TrayRoot is { } tray && !IsPath(tray))
                    throw new InvalidDataException($"设置格式无效：{path}");
                return settings;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"设置格式无效：{path}", exception);
            }
        }
        return null;
    }

    public void Save(DuplicateSettings settings) => Update(_ => settings);

    public DuplicateSettings Update(Func<DuplicateSettings, DuplicateSettings> update)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settingsPath.ToUpperInvariant())));
        using var mutex = new Mutex(false, "Sims4ModSieve.Settings." + hash);
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }

        try
        {
            var next = update(Load() ?? new DuplicateSettings(null, []));
            Directory.CreateDirectory(DataDirectory);
            var temporaryPath = Path.Combine(DataDirectory, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(next, JsonOptions));
                File.Move(temporaryPath, settingsPath, overwrite: true);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
            return next;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    public static string? FindDefaultFolder(string kind, string? documentsDirectory = null)
    {
        if (kind is not ("Mods" or "Tray")) throw new ArgumentException("未知的文件夹类型", nameof(kind));
        var documents = documentsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents)) return null;
        var candidate = Path.Combine(documents, "Electronic Arts", "The Sims 4", kind);
        return Directory.Exists(candidate) ? candidate : null;
    }

    private static bool IsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { _ = Path.GetFullPath(path); return true; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
