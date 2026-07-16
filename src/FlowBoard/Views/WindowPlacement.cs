using System.IO;
using System.Text.Json;
using System.Windows;

namespace FlowBoard.Views;

/// <summary>
/// Window geometry lives in a tiny JSON file, not the database: it's per-machine chrome
/// state, and it must never be part of an export that moves to another machine.
/// </summary>
public static class WindowPlacement
{
    private sealed record Placement(double Left, double Top, double Width, double Height, bool Maximized);

    private static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FlowBoard", "window.json");

    public static void Save(Window w)
    {
        var p = w.WindowState == WindowState.Maximized
            ? new Placement(w.RestoreBounds.Left, w.RestoreBounds.Top,
                            w.RestoreBounds.Width, w.RestoreBounds.Height, true)
            : new Placement(w.Left, w.Top, w.Width, w.Height, false);

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(p));
        }
        catch (IOException) { /* geometry is not worth failing a shutdown over */ }
    }

    public static void Restore(Window w)
    {
        try
        {
            if (!File.Exists(Path)) return;
            var p = JsonSerializer.Deserialize<Placement>(File.ReadAllText(Path));
            if (p is null) return;

            w.Width = Math.Max(p.Width, w.MinWidth);
            w.Height = Math.Max(p.Height, w.MinHeight);

            // A monitor may have been unplugged since last run — don't restore off-screen.
            var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (virt.Contains(new Rect(p.Left, p.Top, w.Width, w.Height)))
            {
                w.Left = p.Left;
                w.Top = p.Top;
                w.WindowStartupLocation = WindowStartupLocation.Manual;
            }

            if (p.Maximized) w.WindowState = WindowState.Maximized;
        }
        catch (Exception e) when (e is IOException or JsonException) { /* fall back to defaults */ }
    }
}
