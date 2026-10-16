using System.IO;
using System.Text.Json;

namespace Sims4ModSieve.Desktop.Services;

public sealed record SavedScanSource(string Path, bool Enabled);

public sealed record DuplicateSettings(string? ModsRoot, IReadOnlyList<SavedScanSource> Sources, string? TrayRoot = null);

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

    public DuplicateSettingsStore(string? localApplicationData = null)
    {
        var directory = localApplicationData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        settingsPath = Path.Combine(directory, "Sims4ModSieve", "duplicate-settings.json");
        legacySettingsPaths =
        [
            Path.Combine(directory, "Sim4ModSieve", "duplicate-settings.json"),
            Path.Combine(directory, "Sims4ModDoctor", "duplicate-settings.json"),
        ];
    }

    public DuplicateSettings? Load()
    {
        try
        {
            var path = File.Exists(settingsPath) ? settingsPath : legacySettingsPaths.FirstOrDefault(File.Exists);
            return path is not null
                ? JsonSerializer.Deserialize<DuplicateSettings>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(DuplicateSettings settings)
    {
        var directory = Path.GetDirectoryName(settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
