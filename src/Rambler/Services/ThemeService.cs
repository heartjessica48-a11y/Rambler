using System.Windows;
using Microsoft.Win32;
using Rambler.Core.Settings;

namespace Rambler.Services;

/// <summary>
/// Applies light/dark theme: WPF's Fluent theme for standard controls plus Rambler's own color
/// dictionary (Themes/Light.xaml or Themes/Dark.xaml) for the popup. Follows Windows when set to System.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private ThemePreference _preference = ThemePreference.System;
    private ResourceDictionary? _palette;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool IsDark { get; private set; }

    public event Action? ThemeChanged;

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        IsDark = preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemUsesDarkTheme(),
        };

        var app = Application.Current;
        app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;

        var palette = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "pack://application:,,,/Themes/Dark.xaml" : "pack://application:,,,/Themes/Light.xaml"),
        };
        if (_palette is not null) app.Resources.MergedDictionaries.Remove(_palette);
        app.Resources.MergedDictionaries.Add(palette);
        _palette = palette;
        ThemeChanged?.Invoke();
    }

    private static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_preference != ThemePreference.System || e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemePreference.System));
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
}
