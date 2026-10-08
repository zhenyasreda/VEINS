using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using VpnProvisioner.Core;
using VpnProvisioner.Installers;
using VpnProvisioner.Ssh;

namespace VpnProvisioner;

public partial class MainWindow : FluentWindow
{
    private ApplicationTheme _currentTheme;

    public MainWindow()
    {
        InitializeComponent();
        _currentTheme = ApplicationThemeManager.GetAppTheme();
        UpdateThemeIcon();
        ApplyLocalization();

        ProtocolCombo.ItemsSource = ProtocolRegistry.All.Select(i => i.DisplayName).ToList();
        ProtocolCombo.SelectedIndex = 0;
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        _currentTheme = _currentTheme == ApplicationTheme.Dark
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;

        ApplicationThemeManager.Apply(_currentTheme);
        UpdateThemeIcon();
    }

    private void OnLangToggleClick(object sender, RoutedEventArgs e)
    {
        Localization.SetLanguage(Localization.Language == "ru" ? "en" : "ru");
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        TxtServerConnection.Text = "  " + Localization.T("server_connection");
        HostBox.PlaceholderText = Localization.T("host_placeholder");
        UserBox.PlaceholderText = Localization.T("ssh_user_placeholder");
        PasswordBox.PlaceholderText = Localization.T("password_placeholder");
        KeyBox.PlaceholderText = Localization.T("key_placeholder");
        BrowseButton.Content = Localization.T("browse");
        KeyPassBox.PlaceholderText = Localization.T("key_pass_placeholder");

        TxtProtocolOptions.Text = "  " + Localization.T("protocol_options");
        LblProtocol.Text = Localization.T("protocol");
        LblSni.Text = Localization.T("sni_label");
        ForceCheck.Content = Localization.T("force_label");

        InstallButton.Content = Localization.T("install");
        TxtProgress.Text = "  " + Localization.T("progress");
        TxtResultLink.Text = "  " + Localization.T("result_link");
        CopyButton.Content = Localization.T("copy");

        ThemeButton.ToolTip = Localization.T("switch_theme");
        LangButton.ToolTip = Localization.T("switch_language");
    }

    private void UpdateThemeIcon()
    {
        ThemeButton.Icon = _currentTheme == ApplicationTheme.Dark
            ? new SymbolIcon { Symbol = SymbolRegular.DarkTheme24 }
            : new SymbolIcon { Symbol = SymbolRegular.BrightnessHigh24 };
    }

    private void OnProtocolChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = ProtocolCombo.SelectedIndex;
        if (idx < 0) return;

        var protocolId = ProtocolRegistry.All[idx].Id;
        bool isXray = protocolId == ProtocolRegistry.XrayReality;

        SniBox.IsEnabled = isXray;
        SniBox.Opacity = isXray ? 1.0 : 0.4;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Localization.T("key_placeholder"),
            Filter = "Все файлы (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            KeyBox.Text = dialog.FileName;
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(LinkBox.Text))
        {
            Clipboard.SetText(LinkBox.Text);
            CopyButton.Content = Localization.T("copied");
        }
    }

    private async void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (!TryBuildRequest(out var request, out var error))
        {
            AppendLog(error);
            return;
        }

        InstallButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        CopyButton.Content = Localization.T("copy");
        LinkBox.Clear();
        ResultCard.Visibility = Visibility.Collapsed;

        var progress = new Progress<string>(AppendLog);

        try
        {
            var result = await Task.Run(() => RunProvision(request, progress));
            LinkBox.Text = result.Link;
            CopyButton.IsEnabled = true;
            ResultCard.Visibility = Visibility.Visible;
            AppendLog(Localization.T("done"));
        }
        catch (ProvisionException ex)
        {
            AppendLog(Localization.T("error") + ex.Message);
        }
        catch (Exception ex)
        {
            AppendLog(Localization.T("unexpected_error") + ex.Message);
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }

    private ProvisionResult RunProvision(ProvisionRequest req, IProgress<string> log)
    {
        using var ssh = SshSession.Connect(req, log);
        var info = ServerProber.ProbeAsync(ssh, log).GetAwaiter().GetResult();
        var installer = ProtocolRegistry.Get(req.Protocol);
        return installer.ProvisionAsync(ssh, info, req, log, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private bool TryBuildRequest(out ProvisionRequest request, out string error)
    {
        request = null!;
        error = "";

        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            error = Localization.T("err_host");
            return false;
        }

        if (!int.TryParse(SshPortBox.Text.Trim(), out var sshPort) || sshPort < 1 || sshPort > 65535)
        {
            error = Localization.T("err_port");
            return false;
        }

        var user = UserBox.Text.Trim();
        if (user.Length == 0)
        {
            error = Localization.T("err_user");
            return false;
        }

        var keyPath = KeyBox.Text.Trim();
        var password = PasswordBox.Password;

        if (keyPath.Length == 0 && string.IsNullOrEmpty(password))
        {
            error = Localization.T("err_auth");
            return false;
        }

        if (keyPath.Length > 0 && !File.Exists(keyPath))
        {
            error = Localization.T("err_key_file");
            return false;
        }

        var sni = SniBox.Text.Trim();
        if (sni.Length == 0)
        {
            sni = "www.google.com";
        }

        var protocolIdx = ProtocolCombo.SelectedIndex;
        if (protocolIdx < 0)
        {
            error = Localization.T("err_protocol");
            return false;
        }

        var protocol = ProtocolRegistry.All[protocolIdx].Id;

        request = new ProvisionRequest
        {
            Host = host,
            SshPort = sshPort,
            SshUser = user,
            KeyPath = keyPath.Length > 0 ? keyPath : null,
            KeyPassphrase = string.IsNullOrEmpty(KeyPassBox.Password) ? null : KeyPassBox.Password,
            Password = string.IsNullOrEmpty(password) ? null : password,
            Sni = sni,
            ForceReconfigure = ForceCheck.IsChecked.GetValueOrDefault(),
            Protocol = protocol
        };
        return true;
    }

    private void AppendLog(string message)
    {
        LogBox.AppendText(message + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
