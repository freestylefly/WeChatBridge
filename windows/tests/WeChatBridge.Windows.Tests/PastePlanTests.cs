using WeChatBridge.Windows.Core.Delivery;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Ported from PastePlanTests.swift. Windows paths and Windows quoting:
/// double quotes are the one spelling cmd and PowerShell both read, where
/// macOS used POSIX single quotes.
/// </summary>
public sealed class PastePlanTests
{
    private const string A = @"C:\tmp\聊天记录 a.zip";
    private const string B = @"C:\tmp\b.zip";

    [Fact]
    public void NoPromptMeansJustTheFiles()
    {
        var plan = PastePlan.Make([A], pathOnly: false, prompt: null);
        var files = Assert.IsType<PastePayload.Files>(Assert.Single(plan));
        Assert.Equal([A], files.Paths);

        plan = PastePlan.Make([A], pathOnly: false, prompt: "  \n");
        Assert.IsType<PastePayload.Files>(Assert.Single(plan));

        plan = PastePlan.Make([A], pathOnly: true, prompt: null);
        var text = Assert.IsType<PastePayload.Text>(Assert.Single(plan));
        Assert.Equal("\"C:\\tmp\\聊天记录 a.zip\" ", text.Value);
    }

    [Fact]
    public void APromptBeforeTheFilesIsTwoPastes()
    {
        var plan = PastePlan.Make([A, B], pathOnly: false, prompt: "总结一下");
        Assert.Collection(plan,
            payload => Assert.Equal("总结一下", Assert.IsType<PastePayload.Text>(payload).Value),
            payload => Assert.Equal([A, B], Assert.IsType<PastePayload.Files>(payload).Paths));

        // A trailing newline is formatting, not a second payload.
        plan = PastePlan.Make([A, B], pathOnly: false, prompt: "总结一下\n");
        Assert.Equal("总结一下", Assert.IsType<PastePayload.Text>(plan[0]).Value);
    }

    [Fact]
    public void APromptForATerminalIsOneLine()
    {
        // A newline pasted into a shell is Return; the prompt folds into the
        // paths line instead of becoming a second payload.
        var plan = PastePlan.Make([A], pathOnly: true, prompt: "读一下\n再总结  ");
        var text = Assert.IsType<PastePayload.Text>(Assert.Single(plan));
        Assert.Equal("读一下 再总结 \"C:\\tmp\\聊天记录 a.zip\" ", text.Value);

        plan = PastePlan.Make([A, B], pathOnly: true, prompt: "总结");
        text = Assert.IsType<PastePayload.Text>(Assert.Single(plan));
        Assert.Equal("总结 \"C:\\tmp\\聊天记录 a.zip\" \"C:\\tmp\\b.zip\" ", text.Value);
    }

    [Fact]
    public void AManualPasteGetsTheFilesNotTheBarePrompt()
    {
        var plan = PastePlan.Make([A], pathOnly: false, prompt: "p");
        var files = Assert.IsType<PastePayload.Files>(PastePlan.ManualPayload(plan));
        Assert.Equal([A], files.Paths);

        var line = PastePlan.Make([A], pathOnly: true, prompt: "p");
        Assert.Equal(line[0], PastePlan.ManualPayload(line));

        Assert.Null(PastePlan.ManualPayload([]));
    }

    [Fact]
    public void ShellLineQuotesEveryPath()
    {
        Assert.Equal(
            "\"C:\\tmp\\聊天记录 a.zip\" \"C:\\tmp\\b.zip\" ",
            WindowsClipboard.ShellLine([A, B]));
    }
}
