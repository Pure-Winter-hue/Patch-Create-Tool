using System;
using System.IO;
using System.Text.Json;

namespace VSPatchGen.Gui;

/// <summary>
/// Tiny persistence layer for user preferences.
/// </summary>
public static class UserSettings
{
    private sealed class SettingsModel
    {
        public string? Language { get; set; }
    }

    private static string SettingsPath
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(baseDir, "VSPatchGen");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static string? LoadPreferredLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var json = File.ReadAllText(SettingsPath);
            var model = JsonSerializer.Deserialize<SettingsModel>(json);
            return string.IsNullOrWhiteSpace(model?.Language) ? null : model!.Language;
        }
        catch
        {
            return null;
        }
    }

    public static void SavePreferredLanguage(string languageCode)
    {
        try
        {
            var model = new SettingsModel { Language = languageCode };
            var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Non-fatal: failing to write settings should never prevent patch generation.
        }
    }
}
