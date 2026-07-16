using System.Windows;
using Wpf.Ui.Appearance;

namespace FlowBoard.Theme;

/// <summary>
/// Keeps FlowBoard's own token dictionary in step with the active Fluent theme.
///
/// WPF-UI swaps its own brushes when the theme changes, but it knows nothing about ours,
/// so we swap the matching Tokens.*.xaml in the same beat. Tokens.Dark and Tokens.Light
/// carry an identical key set, so every DynamicResource in the app just re-resolves.
/// </summary>
public static class ThemeTokens
{
    private static ResourceDictionary? _current;

    private static readonly Uri Dark = new("pack://application:,,,/Theme/Tokens.Dark.xaml");
    private static readonly Uri Light = new("pack://application:,,,/Theme/Tokens.Light.xaml");

    public static void Initialize()
    {
        Apply(ApplicationThemeManager.GetAppTheme());
        ApplicationThemeManager.Changed += (theme, _) => Apply(theme);
    }

    private static void Apply(ApplicationTheme theme)
    {
        var next = new ResourceDictionary
        {
            Source = theme == ApplicationTheme.Light ? Light : Dark
        };

        var merged = Application.Current.Resources.MergedDictionaries;

        // Add before remove: removing first would leave DynamicResource lookups briefly
        // unresolved, which WPF renders as a flash of unstyled cards.
        merged.Add(next);
        if (_current is not null) merged.Remove(_current);
        _current = next;
    }
}
