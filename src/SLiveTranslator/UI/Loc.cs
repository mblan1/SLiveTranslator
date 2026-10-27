using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Data;
using SLiveTranslator.Models;

namespace SLiveTranslator.UI;

/// <summary>
/// The app's texts in English and Vietnamese (embedded Assets/Strings/en.json and vi.json). XAML binds
/// <c>{Binding [Key], Source={x:Static l:Loc.Instance}}</c>; a language switch raises PropertyChanged for the indexer
/// (<see cref="Binding.IndexerName"/>), so every bound screen updates without a restart. A key the language lacks
/// falls back to English, then to the key itself, and is logged once. Thread-safe.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string English = "en";
    public const string Vietnamese = "vi";

    sealed record State(string Language, IReadOnlyDictionary<string, string> Strings, CultureInfo Culture);

    public static Loc Instance { get; } = new(Strings(English), Strings(Vietnamese), () => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    readonly IReadOnlyDictionary<string, string> _english;
    readonly IReadOnlyDictionary<string, string> _vietnamese;
    readonly Func<string> _windowsLanguage;
    readonly ConcurrentDictionary<string, bool> _logged = new();
    State _state;

    /// <param name="windowsLanguage">The two-letter Windows display language, for the setting "".</param>
    internal Loc(IReadOnlyDictionary<string, string> english, IReadOnlyDictionary<string, string> vietnamese, Func<string> windowsLanguage)
    {
        _english = english;
        _vietnamese = vietnamese;
        _windowsLanguage = windowsLanguage;
        _state = Resolve("");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The language in use: <see cref="English"/> or <see cref="Vietnamese"/>.</summary>
    public string Language => Volatile.Read(ref _state).Language;

    /// <summary>Formats numbers and dates for the language in use.</summary>
    public CultureInfo Culture => Volatile.Read(ref _state).Culture;

    /// <param name="setting"><see cref="UiSettings.Language"/>: "en", "vi", or "" to follow the Windows display language (Vietnamese for vi, else English).</param>
    public void SetLanguage(string? setting)
    {
        var next = Resolve(setting);
        if (Interlocked.Exchange(ref _state, next).Language == next.Language) return;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }

    public string this[string key]
    {
        get
        {
            var state = Volatile.Read(ref _state);
            if (state.Strings.TryGetValue(key, out var text) && text.Length > 0) return text;
            if (_english.TryGetValue(key, out text) && text.Length > 0)
            {
                LogOnce(key, $"loc: \"{key}\" has no {state.Language} text; the English one is shown");
                return text;
            }
            LogOnce(key, $"loc: no text for \"{key}\"");
            return key;
        }
    }

    /// <summary>The text of <paramref name="key"/> in the current language, formatted with its culture.</summary>
    public static string F(string key, params object?[] args) => Instance.Format(key, args);

    /// <inheritdoc cref="F"/>
    public string Format(string key, params object?[] args)
    {
        string template = this[key];
        try
        {
            return string.Format(Culture, template, args);
        }
        catch (FormatException)
        {
            LogOnce("format " + key, $"loc: the text of \"{key}\" is not a valid format string");
            return template;
        }
    }

    /// <summary>Whether the app has a text for <paramref name="key"/> (English holds every key).</summary>
    public bool Has(string key) => _english.ContainsKey(key);

    /// <summary>The embedded texts of a language; empty when the resource is missing or damaged (logged).</summary>
    internal static IReadOnlyDictionary<string, string> Strings(string language)
    {
        string name = $"SLiveTranslator.Assets.Strings.{language}.json";
        try
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException("missing resource " + name);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Error($"loc: the {language} texts cannot be read: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    State Resolve(string? setting)
    {
        string language = setting?.Trim().ToLowerInvariant() switch
        {
            English => English,
            Vietnamese => Vietnamese,
            _ => string.Equals(_windowsLanguage(), Vietnamese, StringComparison.OrdinalIgnoreCase) ? Vietnamese : English,
        };
        return language == Vietnamese
            ? new State(Vietnamese, _vietnamese, CultureInfo.GetCultureInfo("vi-VN"))
            : new State(English, _english, CultureInfo.GetCultureInfo("en-US"));
    }

    void LogOnce(string key, string message)
    {
        if (_logged.TryAdd(key, true)) Log.Warn(message);
    }
}
