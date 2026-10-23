using WeChatBridge.Windows.Core;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace WeChatBridge.Windows.Panes;

/// <summary>通用 pane: status, retention window, version.</summary>
public partial class GeneralPane : UserControl
{
    public GeneralPane()
    {
        InitializeComponent();
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = L10n.Format($"当前版本 {ShortVersion(informational)}");
    }

    /// <summary>1.0.0+commithash → 1.0.0 (short hash) — same rule as AboutPane.</summary>
    private static string ShortVersion(string? informational)
    {
        if (string.IsNullOrEmpty(informational))
            return "0.1.0";
        var plus = informational.IndexOf('+');
        if (plus < 0)
            return informational;
        var hash = informational[(plus + 1)..Math.Min(plus + 8, informational.Length)];
        return $"{informational[..plus]} ({hash})";
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private void RevealInbox_Click(object sender, RoutedEventArgs e) => Model?.RevealInbox();

    /// <summary>重新运行设置向导 — macOS SettingsActions.restartOnboarding.</summary>
    private void RestartOnboarding_Click(object sender, RoutedEventArgs e) =>
        (Application.Current as App)?.RestartOnboarding();

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/freestylefly/WeChatBridge",
                UseShellExecute = true,
            });
        }
        catch
        {
            // A missing browser is not worth an error dialog in settings.
        }
    }
}
