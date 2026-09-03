using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace NodePilot.Switcher.Services;

internal sealed class ThemeService
{
    private const string PreferenceKey = @"Software\NodePilot\Switcher";
    private bool? _lastLight;
    private bool? _selectedLight;

    public bool ApplyCurrentTheme(Window? window = null)
    {
        var light = _selectedLight ??= ReadThemePreference() ?? IsLightTheme();
        if (_lastLight != light)
        {
            ApplyPalette(light);
            _lastLight = light;
        }
        if (window is not null) ApplyTitleBar(window, !light);
        return light;
    }

    public bool ToggleTheme(Window? window = null)
    {
        var light = !(_selectedLight ?? ReadThemePreference() ?? IsLightTheme());
        _selectedLight = light;
        SaveThemePreference(light);
        return ApplyCurrentTheme(window);
    }

    private static bool? ReadThemePreference()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreferenceKey, writable: false);
            return key?.GetValue("Theme") switch
            {
                "Light" => true,
                "Dark" => false,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static void SaveThemePreference(bool light)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PreferenceKey, writable: true);
            key?.SetValue("Theme", light ? "Light" : "Dark", RegistryValueKind.String);
        }
        catch
        {
            // Theme switching remains available for the current session if persistence fails.
        }
    }

    private static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static void ApplyPalette(bool light)
    {
        var resources = Application.Current.Resources;
        var colors = light
            ? new Dictionary<string, string>
            {
                ["PageBrush"] = "#F8F9FF",
                ["HeaderBrush"] = "#F8F9FF",
                ["SurfaceContainerLowBrush"] = "#EFF4FF",
                ["SurfaceContainerBrush"] = "#E5EEFF",
                ["SurfaceContainerHighBrush"] = "#DCE9FF",
                ["SurfaceVariantBrush"] = "#D3E4FE",
                ["ControlBrush"] = "#EFF4FF",
                ["BorderBrush"] = "#C2C6D4",
                ["DividerBrush"] = "#D9DFEB",
                ["TextBrush"] = "#0B1C30",
                ["MutedTextBrush"] = "#424752",
                ["AccentBrush"] = "#003F87",
                ["AccentTextBrush"] = "#0056B3",
                ["OnAccentBrush"] = "#FFFFFF",
                ["SuccessBrush"] = "#16A34A",
                ["WarningBrush"] = "#D97706",
                ["ErrorBrush"] = "#BA1A1A",
            }
            : new Dictionary<string, string>
            {
                ["PageBrush"] = "#0B1422",
                ["HeaderBrush"] = "#0E1929",
                ["SurfaceContainerLowBrush"] = "#111E31",
                ["SurfaceContainerBrush"] = "#17263B",
                ["SurfaceContainerHighBrush"] = "#1C2F49",
                ["SurfaceVariantBrush"] = "#243B5C",
                ["ControlBrush"] = "#17263B",
                ["BorderBrush"] = "#40516A",
                ["DividerBrush"] = "#273A54",
                ["TextBrush"] = "#EAF1FF",
                ["MutedTextBrush"] = "#AEBBCB",
                ["AccentBrush"] = "#6CA9F2",
                ["AccentTextBrush"] = "#9CC4FA",
                ["OnAccentBrush"] = "#06264A",
                ["SuccessBrush"] = "#34D399",
                ["WarningBrush"] = "#FBBF24",
                ["ErrorBrush"] = "#F87171",
            };

        foreach (var (key, value) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            resources[key] = brush;
        }

        resources["BrandIcon"] = new BitmapImage(new Uri(
            "pack://application:,,,/Assets/switcher.ico",
            UriKind.Absolute));
    }

    private static void ApplyTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
