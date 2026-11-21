using System.IO;
using System.Text.Json;

namespace Ymuse.Core;

public sealed class SettingsData
{
    public string? Token { get; set; }
    public string? DeviceId { get; set; }

    /// <summary>Адрес JSON-манифеста обновлений. Пусто — автообновление выключено.</summary>
    public string? UpdateUrl { get; set; }

    public string? PendingVersion { get; set; }
    public string? PendingDir { get; set; }
}

/// <summary>Хранит токен и device_id в %APPDATA%\Ymuse\settings.json.</summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ymuse");

    string FilePath => Path.Combine(_dir, "settings.json");

    public SettingsData Data { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Data = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath)) ?? new SettingsData();
            }
        }
        catch
        {
            Data = new SettingsData();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Opts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // сохранение настроек не должно валить приложение
        }
    }
}
