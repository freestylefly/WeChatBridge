using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;
using WeChatBridge.Windows.Tests;

namespace WeChatBridge.Windows.AppTests;

public sealed class LocalImportTests
{
    [Fact]
    public async Task ImportWaitsForExplicitDeliveryAndSurvivesReloadAndRestart()
    {
        using var fixture = new TempInbox();
        var copied = new List<PastePayload>();
        var config = Path.Combine(fixture.Root, "config");
        var model = new MainViewModel(fixture.Paths, config, false, copyPayload: copied.Add);
        try
        {
            model.Query = "will not match";
            await model.ImportLocalTextAsync("本地聊天内容", "测试群");
            Assert.Empty(copied);
            Assert.Equal("", model.Query);
            Assert.Null(Assert.Single(model.Batches).Outcome);
            Assert.Equal(L10n.Text("待发送"), Assert.Single(model.Groups.SelectMany(g => g.Rows)).StatusText);
            model.Reload();
            Assert.Empty(copied);
        }
        finally { model.DisposeServices(); }
        var restarted = new MainViewModel(fixture.Paths, config, false, copyPayload: copied.Add);
        try
        {
            restarted.Reload();
            Assert.Empty(copied);
            var batch = Assert.Single(restarted.Batches);
            Assert.Equal("测试群", batch.ChatName);
            await restarted.PerformForward(batch, ShareAction.Clipboard, null);
            Assert.Single(copied);
            Assert.Equal(BatchOutcomeKind.Copied, Assert.Single(restarted.Batches).Outcome?.Kind);
        }
        finally { restarted.DisposeServices(); }
    }

    [Fact]
    public async Task FailedImportRestoresControlsAndReportsFailure()
    {
        using var fixture = new TempInbox();
        var model = new MainViewModel(fixture.Paths, Path.Combine(fixture.Root, "config"), false);
        try
        {
            var warnings = new List<string>();
            model.ToastRequested += (message, _, _, warning) => { if (warning) warnings.Add(message); };
            await model.ImportLocalFilesAsync([fixture.WriteSource("broken.zip", "invalid")]);
            Assert.True(model.CanImport);
            Assert.Empty(new InboxReader(fixture.Paths).LoadBatches());
            Assert.Single(warnings);
        }
        finally { model.DisposeServices(); }
    }
}
