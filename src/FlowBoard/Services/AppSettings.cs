using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlowBoard.Services;

public enum ThemePreference { System, Light, Dark }

/// <summary>
/// User preferences. Deliberately not in the database: these are per-machine chrome
/// choices, and an export that carried someone's theme override to a new machine would
/// be exporting the wrong thing.
/// </summary>
public sealed partial class AppSettings : ObservableObject
{
    [ObservableProperty] private bool _cardAgingEnabled = true;
    [ObservableProperty] private ThemePreference _theme = ThemePreference.System;
    [ObservableProperty] private bool _sidebarCollapsed;

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FlowBoard", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // A corrupt settings file should never stop the app opening.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this));
        }
        catch (IOException) { /* preferences are not worth failing over */ }
    }
}
