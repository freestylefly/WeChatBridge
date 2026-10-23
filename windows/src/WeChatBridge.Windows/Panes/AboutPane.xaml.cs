using WeChatBridge.Windows.Core;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace WeChatBridge.Windows.Panes;

public partial class AboutPane : UserControl
{
    public AboutPane()
    {
        InitializeComponent();
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        VersionText.Text = L10n.Format($"版本 {ShortVersion(informational)} · Windows");

    }

    /// <summary>1.0.0+commithash → 1.0.0 (short hash): the NuGet metadata stays, the label stays readable.</summary>
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

    private void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            // A missing browser is not worth an error dialog in settings.
        }
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e) =>
        Open("https://github.com/freestylefly/WeChatBridge");

    private void OpenWebsite_Click(object sender, RoutedEventArgs e) =>
        Open("https://render.qmuse.pub/p/muse/2413870555736078");

    private void OpenIssues_Click(object sender, RoutedEventArgs e) =>
        Open("https://github.com/freestylefly/WeChatBridge/issues/new");
}
