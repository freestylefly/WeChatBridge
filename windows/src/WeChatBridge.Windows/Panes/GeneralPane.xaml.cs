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
        RenderLicenceState();
    }

    /// <summary>
    /// Shows where this installation's licence stands.
    /// <para>
    /// Reads the gate rather than binding to it: the snapshot resolves on first read
    /// and is cached after, so this is cheap enough to call from
    /// <see cref="Loaded"/> as well as the constructor — a portable stick inserted
    /// after startup changes the answer.
    /// </para>
    /// </summary>
    private void RenderLicenceState()
    {
        var gate = Services.ActivationGate.Current;
        LicenceStatusText.Text = gate.StatusText;

        var installId = gate.Snapshot.InstallId;
        var showMachineCode = !string.IsNullOrWhiteSpace(installId)
            && gate.Snapshot.State is not (Core.Activation.GateState.Portable or Core.Activation.GateState.Licensed);
        MachineCodeHint.Visibility = showMachineCode ? Visibility.Visible : Visibility.Collapsed;
        MachineCodeHint.Text = showMachineCode ? L10n.Format($"本机机器码：{installId}") : string.Empty;

        // Nothing to open for a licensed machine or a portable stick, so the button
        // goes rather than opening a window that says "you are already done".
        OpenActivationButton.Visibility = gate.CanOpenFromSettings ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenActivation_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window)
            return;

        Services.ActivationGate.Current.OpenManually(window);

        // The page may have taken the licence it was opened to fetch.
        RenderLicenceState();
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
