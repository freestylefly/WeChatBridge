using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Reads the current chat name out of the WeChat main window through UI
/// Automation — the Windows answer to macOS's Accessibility read.
///
/// Window titles do not work here: on Windows, WeChat 4.x (Weixin) titles its
/// main window 「微信」 regardless of which conversation is open — that is how
/// groups ended up bound to the literal name "微信". The mmui::MainWindow
/// class does expose a full UIA tree, and the header row's Text element carries
/// the live chat name, including the member count 「群名 (260)」 for group
/// chats. We read element names only — the same privacy boundary as the macOS
/// path: no capture, no injection, and only the window already on screen.
/// </summary>
public static class WeChatUiTitleReader
{
    public static async Task<GroupTitleParser.Title?> ReadWithFallbackAsync()
    {
        var title = await Task.Run(() => TryRead() ?? new WeChatWindowTitleReader().TryRead());
        if (title is not null) return title;
        try { return await WeChatOcrTitleReader.TryReadAsync(); }
        catch { return null; }
    }
    /// <summary>Weixin.exe is WeChat 4.x; WeChat.exe is the 3.x line.</summary>
    private static readonly string[] WeChatProcessNames = ["weixin", "wechat"];

    private static readonly Regex MemberCount =
        new(@"^(.+?)[（(]\s*(\d{1,5})\s*[)）]\s*$", RegexOptions.Compiled);

    /// <summary>Best-effort read; every failure mode means "no group name".</summary>
    public static GroupTitleParser.Title? TryRead()
    {
        try
        {
            return Read();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the main window's Text elements in tree order. A 「name (count)」
    /// line is preferred — it is unambiguously a group header; a plain first
    /// Text element covers direct chats, which carry no member count.
    /// </summary>
    public static GroupTitleParser.Title? Read()
    {
        var candidates = ChatTitleCandidates();
        if (candidates.Count == 0)
            return null;
        foreach (var candidate in candidates)
        {
            var match = MemberCount.Match(candidate);
            if (match.Success && int.TryParse(match.Groups[2].Value, out var count))
                return new GroupTitleParser.Title(match.Groups[1].Value.Trim(), count);
        }
        return new GroupTitleParser.Title(candidates[0], null);
    }

    /// <summary>
    /// Text-element names of the WeChat main window, in tree order. The first
    /// Text descendant is the conversation header (verified against
    /// mmui::MainWindow on Weixin 4.x); rows below it repeat the name split
    /// into 「name」 and 「(count)」 parts.
    /// </summary>
    private static IReadOnlyList<string> ChatTitleCandidates()
    {
        var window = FindMainWindow();
        if (window is null)
            return [];
        try
        {
            var texts = window.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            var names = new List<string>(texts.Count);
            foreach (AutomationElement element in texts)
            {
                try
                {
                    var name = element.Current.Name?.Trim();
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }
                catch
                {
                    // An element vanishing mid-read loses its slot, not the scan.
                }
            }
            return names;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// The WeChat main window as a UIA element: any weixin/wechat process whose
    /// main window reports the mmui::MainWindow class. Side dialogs (the 「发送给」
    /// share sheet included) fail the class check, so a share in flight cannot
    /// mislabel the group.
    /// </summary>
    private static AutomationElement? FindMainWindow()
    {
        foreach (var name in WeChatProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch
            {
                continue;
            }
            foreach (var process in processes)
            {
                using (process)
                {
                    var hwnd = process.MainWindowHandle;
                    if (hwnd == 0)
                        continue;
                    try
                    {
                        var element = AutomationElement.FromHandle(hwnd);
                        if (element.Current.ClassName is "mmui::MainWindow")
                            return element;
                    }
                    catch
                    {
                        // Not a UIA-backed window — try the next process.
                    }
                }
            }
        }
        return null;
    }
}
