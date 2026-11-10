using System.IO;
using System.Text.Json;

namespace Sims4ModSieve.Desktop.Services;

public sealed partial class SettingsSupportService
{
    public IReadOnlyList<string> ReadSupporters()
    {
        try
        {
            using var stream = openSupporters();
            return stream is null ? [] : (JsonSerializer.Deserialize<string?[]>(stream) ?? [])
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!.Trim()).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
