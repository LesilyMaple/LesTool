using System.IO;
using System.Text.Json;
using LesTool.Models;
using Serilog;

namespace LesTool.Services;

/// <summary>JSON 设置读写（%APPDATA%\LesTool\settings.json）。热应用后续接到设置页。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LesTool");

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public LesToolSettings Current { get; private set; } = new();

    public SettingsService() => Current = Load();

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, SerializerOptions));
        Log.Debug("设置已保存到 {Path}", FilePath);
    }

    private static LesToolSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new LesToolSettings();
            return JsonSerializer.Deserialize<LesToolSettings>(File.ReadAllText(FilePath))
                   ?? new LesToolSettings();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取设置失败，使用默认设置");
            return new LesToolSettings();
        }
    }
}
