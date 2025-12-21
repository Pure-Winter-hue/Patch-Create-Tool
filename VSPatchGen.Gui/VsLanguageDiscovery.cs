using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VSPatchGen.Gui;

public static class VsLanguageDiscovery
{
    public sealed record VsLang(string Code, string DisplayName);

    /// <summary>
    /// Tries to read Vintage Story's languages.json and returns a list for the language menu.
    /// The menu only includes languages that this app actually has translations for.
    /// </summary>
    public static IReadOnlyList<VsLang> GetLanguagesForMenu()
    {
        var supported = new HashSet<string>(Localization.BuiltInLanguageNativeNames.Keys, StringComparer.OrdinalIgnoreCase);

        var fromGame = TryLoadFromGameLanguagesJson();
        if (fromGame is not null)
        {
            // Only keep those we can fully translate.
            return fromGame
                .Where(l => supported.Contains(l.Code))
                .Select(l => new VsLang(l.Code, string.IsNullOrWhiteSpace(l.DisplayName)
                    ? Localization.BuiltInLanguageNativeNames[l.Code]
                    : l.DisplayName))
                .ToList();
        }

        // Fall back to what we ship.
        return Localization.BuiltInLanguageNativeNames
            .Select(kv => new VsLang(kv.Key, kv.Value))
            .ToList();
    }

    private static List<VsLang>? TryLoadFromGameLanguagesJson()
    {
        foreach (var path in CandidatePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var json = File.ReadAllText(path);
                var doc = JsonDocument.Parse(json);

                var list = new List<VsLang>();
                ExtractLanguages(doc.RootElement, list);
                if (list.Count > 0)
                    return list;
            }
            catch
            {
                // ignore and continue
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        // Also check in/near current folder for dev setups.
        var cwd = Directory.GetCurrentDirectory();
        yield return Path.Combine(cwd, "assets", "game", "lang", "languages.json");
        yield return Path.Combine(AppContext.BaseDirectory, "assets", "game", "lang", "languages.json");

        // User directories.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(appdata, "Vintagestory", "assets", "game", "lang", "languages.json");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Vintagestory", "assets", "game", "lang", "languages.json");
        }
        else
        {
            yield return Path.Combine(home, ".config", "Vintagestory", "assets", "game", "lang", "languages.json");
            yield return Path.Combine(home, ".local", "share", "Vintagestory", "assets", "game", "lang", "languages.json");
        }
    }

    private static void ExtractLanguages(JsonElement root, List<VsLang> outList)
    {
        // Handle the common shapes:
        // 1) Array of objects: [{"code":"de","name":"German","nativeName":"Deutsch"}, ...]
        // 2) Object mapping: {"de": {"name":..., "nativeName":...}, "en": "English", ...}

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
                TryReadLanguageObject(item, outList);
            return;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                var code = prop.Name;
                var val = prop.Value;

                if (val.ValueKind == JsonValueKind.String)
                {
                    outList.Add(new VsLang(code, val.GetString() ?? code));
                    continue;
                }

                if (val.ValueKind == JsonValueKind.Object)
                {
                    // Try to read a name out of the object.
                    var display = GetStringAny(val, "nativeName", "native", "nameNative", "name", "english");
                    outList.Add(new VsLang(code, display ?? code));
                }
            }
        }
    }

    private static void TryReadLanguageObject(JsonElement item, List<VsLang> outList)
    {
        if (item.ValueKind != JsonValueKind.Object) return;

        var code = GetStringAny(item, "code", "langCode", "key", "id");
        if (string.IsNullOrWhiteSpace(code)) return;

        var display = GetStringAny(item, "nativeName", "native", "nameNative", "name", "english");
        outList.Add(new VsLang(code, display ?? code));
    }

    private static string? GetStringAny(JsonElement obj, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (obj.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        }
        return null;
    }
}
