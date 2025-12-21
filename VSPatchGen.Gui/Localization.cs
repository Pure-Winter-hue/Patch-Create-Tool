// LOCALIZATION_FIX2 2025-12-21

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;

namespace VSPatchGen.Gui;

public static class Localization
{
    private static IResourceProvider? _activeDict;

    public static string CurrentLanguage { get; private set; } = "en";

    /// <summary>
    /// Languages this app ships translations for. Codes are expected to match Vintage Story language codes.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> BuiltInLanguageNativeNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English",
            ["de"] = "Deutsch",
            ["fr"] = "Français",
            ["it"] = "Italiano",
            ["es-es"] = "Español (España)",
            ["es-419"] = "Español (Latinoamérica)",
            ["pt-br"] = "Português (Brasil)",
            ["nl"] = "Nederlands",
            ["sv"] = "Svenska",
            ["fi"] = "Suomi",
            ["da"] = "Dansk",
            ["no"] = "Norsk",
            ["pl"] = "Polski",
            ["cs"] = "Čeština",
            ["sk"] = "Slovenčina",
            ["hu"] = "Magyar",
            ["ro"] = "Română",
            ["bg"] = "Български",
            ["ru"] = "Русский",
            ["uk"] = "Українська",
            ["el"] = "Ελληνικά",
            ["tr"] = "Türkçe",
            ["he"] = "עברית",
            ["ar"] = "العربية",
            ["ja-jp"] = "日本語",
            ["ko-kr"] = "한국어",
            ["zh-cn"] = "简体中文",
            ["zh-tw"] = "繁體中文",
            ["eo"] = "Esperanto",
        };

    public static void Initialize(string? preferredLanguage = null)
    {
        // Prefer explicit argument, else OS UI language, else English.
        var lang = preferredLanguage;
        if (string.IsNullOrWhiteSpace(lang))
        {
            // Map two-letter culture where we can.
            var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            lang = two switch
            {
                "pt" => "pt-br",
                "ja" => "ja-jp",
                "ko" => "ko-kr",
                "zh" => "zh-cn",
                _ => two
            };
        }

        if (!BuiltInLanguageNativeNames.ContainsKey(lang))
            lang = "en";

        Load(lang);
    }

    public static string T(string key)
    {
        if (Application.Current?.Resources.TryGetResource(key, Application.Current?.RequestedThemeVariant, out var value) == true)
            return value?.ToString() ?? key;
        return key;
    }

    public static void Load(string languageCode)
    {
        if (!BuiltInLanguageNativeNames.ContainsKey(languageCode))
            languageCode = "en";

        var dict = LoadEmbeddedDictionary(languageCode) ?? LoadEmbeddedDictionary("en") ?? new Dictionary<string, string>();
        ApplyDictionary(dict);
        CurrentLanguage = languageCode;

        ApplyFlowDirection(languageCode);
    }

    private static Dictionary<string, string>? LoadEmbeddedDictionary(string languageCode)
    {
        try
        {
            var uri = new Uri($"avares://VSPatchGen.Gui/Assets/i18n/{languageCode}.json");
            if (!AssetLoader.Exists(uri)) return null;

            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();

            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyDictionary(Dictionary<string, string> dict)
    {
        if (Application.Current is null) return;

        var rd = new ResourceDictionary();
        foreach (var (k, v) in dict)
            rd[k] = v;

        if (_activeDict is not null)
            Application.Current.Resources.MergedDictionaries.Remove(_activeDict);

        Application.Current.Resources.MergedDictionaries.Add(rd);
        _activeDict = rd;
    }

    private static void ApplyFlowDirection(string languageCode)
    {
        // Minimal RTL support.
        var rtl = languageCode.Equals("ar", StringComparison.OrdinalIgnoreCase) ||
                  languageCode.Equals("he", StringComparison.OrdinalIgnoreCase);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (desktop.MainWindow is not null)
                desktop.MainWindow.FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
    }
}
