using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sable.App.Localization;

/// <summary>
/// Singleton localization service (ported from the Novalist i18n system, see
/// docs/i18n-decision.md). Locale files are nested JSON flattened to dotted keys
/// (e.g. <c>menu.file.open</c>) with <c>{0}</c> positional format args; <c>en</c>
/// is the always-loaded fallback and the active language overlays it.
///
/// XAML:  <c>{loc:Loc menu.file.open}</c>  (auto-refreshes on language change), or
///        <c>{Binding [menu.file.open], Source={x:Static loc:Loc.Instance}}</c>.
/// Code:  <c>Loc.T("key")</c> / <c>Loc.T("key", arg0, arg1)</c>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private Dictionary<string, string> _fallback = new(StringComparer.Ordinal);
    private string _currentLanguage = "en";
    private string _localesDirectory = string.Empty;

    /// <summary>Directory holding the locale JSON files. Empty until <see cref="Initialize"/> runs.</summary>
    public string LocalesDirectory => _localesDirectory;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    private Loc() { }

    /// <summary>The currently active language code (e.g. "en", "de").</summary>
    public string CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (string.Equals(_currentLanguage, value, StringComparison.Ordinal)) return;
            _currentLanguage = value;
            LoadLanguage(value);
            // refresh every binding + every {loc:Loc} handler
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Indexer for XAML compiled bindings.</summary>
    public string this[string key] => Resolve(key);

    /// <summary>Initialize the localization system. Call once at startup, before any UI loads.</summary>
    public void Initialize(string localesDirectory, string language)
    {
        _localesDirectory = localesDirectory;
        _fallback = LoadFile(Path.Combine(localesDirectory, "en.json"));
        _currentLanguage = language;
        LoadLanguage(language);
    }

    /// <summary>Get a translated string by key.</summary>
    public static string T(string key) => Instance.Resolve(key);

    /// <summary>Get a translated string with format arguments.</summary>
    public static string T(string key, params object[] args)
    {
        var template = Instance.Resolve(key);
        try { return string.Format(CultureInfo.CurrentCulture, template, args); }
        catch (FormatException) { return template; }
    }

    /// <summary>
    /// Available language codes discovered from JSON files in the locales directory
    /// (each "xx.json" → code "xx"), alphabetically sorted.
    /// </summary>
    public List<string> GetAvailableLanguages()
    {
        if (string.IsNullOrWhiteSpace(_localesDirectory) || !Directory.Exists(_localesDirectory))
            return new List<string> { "en" };

        return Directory.GetFiles(_localesDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Display name for a language code, from its "language.name" key; falls back to the code.</summary>
    public string GetLanguageDisplayName(string code)
    {
        var data = LoadFile(Path.Combine(_localesDirectory, $"{code}.json"));
        return data.TryGetValue("language.name", out var name) ? name : code;
    }

    private void LoadLanguage(string language)
    {
        if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)) { _strings = _fallback; return; }
        _strings = LoadFile(Path.Combine(_localesDirectory, $"{language}.json"));
    }

    private string Resolve(string key)
    {
        if (_strings.TryGetValue(key, out var value)) return value;
        if (_fallback.TryGetValue(key, out var fallback)) return fallback;
        Debug.WriteLine($"[Loc] MISSING key: '{key}'");
        return key;
    }

    private static Dictionary<string, string> LoadFile(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            FlattenJson(doc.RootElement, string.Empty, result);
            return result;
        }
        catch { return new Dictionary<string, string>(StringComparer.Ordinal); }
    }

    private static void FlattenJson(JsonElement element, string prefix, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
                    FlattenJson(property.Value, key, result);
                }
                break;
            case JsonValueKind.String:
                result[prefix] = element.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                result[prefix] = element.GetRawText();
                break;
        }
    }
}
