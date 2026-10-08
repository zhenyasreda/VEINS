using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace VpnProvisioner;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Localization.DetectSystemLanguage();

        var theme = GetSystemTheme();
        ApplicationThemeManager.Apply(theme);
    }

    private static ApplicationTheme GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i && i == 0)
                return ApplicationTheme.Dark;
        }
        catch
        {
        }
        return ApplicationTheme.Light;
    }
}
