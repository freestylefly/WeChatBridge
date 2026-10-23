using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;

namespace WeChatBridge.Windows.Tests;

public sealed class ShareEntryAvailabilityTests
{
    [Theory]
    [InlineData(ShareAction.Codex)]
    [InlineData(ShareAction.Claude)]
    [InlineData(ShareAction.Doubao)]
    [InlineData(ShareAction.Qwen)]
    [InlineData(ShareAction.WorkBuddy)]
    [InlineData(ShareAction.WeSight)]
    [InlineData(ShareAction.DeepSeekHarness)]
    [InlineData(ShareAction.Obsidian)]
    public void AppEntryRequiresFreshInstallationEvidence(ShareAction action)
    {
        Assert.False(ShareEntryAvailability.CanEnable(action, [], _ => false));
        Assert.True(ShareEntryAvailability.CanEnable(action, [], _ => true));
    }

    [Theory]
    [InlineData(ShareAction.Folder)]
    [InlineData(ShareAction.Collect)]
    [InlineData(ShareAction.Clipboard)]
    public void LocalEntryNeedsNoApp(ShareAction action)
        => Assert.True(ShareEntryAvailability.CanEnable(action, [], _ => throw new Exception("Unexpected app check")));

    [Fact]
    public void CustomEntryNeedsAtLeastOneInstalledTarget()
    {
        var targets = new[] {
            new ForwardTarget("missing.exe", "Missing", DateTimeOffset.UtcNow),
            new ForwardTarget("installed.exe", "Installed", DateTimeOffset.UtcNow) };
        Assert.False(ShareEntryAvailability.CanEnable(ShareAction.Custom, [], _ => true));
        Assert.False(ShareEntryAvailability.CanEnable(ShareAction.Custom, targets, _ => false));
        Assert.True(ShareEntryAvailability.CanEnable(ShareAction.Custom, targets, s => s.DisplayName == "Installed"));
    }

    [Fact]
    public void ArbitraryAumidIsNotInstallationEvidence()
    {
        var spec = new WindowsForwardTarget("Missing", [], [], "WeChatBridge.Missing_" + Guid.NewGuid() + "!App");
        Assert.Null(WindowsTargetResolver.Resolve(spec));
    }

    [Fact]
    public void ExplicitMissingPathCannotBorrowAnUnrelatedRunningProcess()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var spec = new WindowsForwardTarget("Missing", [process.ProcessName],
            [Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), process.ProcessName + ".exe")]);
        Assert.False(WindowsTargetResolver.IsInstalled(spec));
    }

    [Fact]
    public void ActualAppsFolderRegistrationIsRecognized()
    {
        if (!OperatingSystem.IsWindows()) return;
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        object? folder = null, items = null, item = null;
        try
        {
            folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
            items = ((dynamic)folder!).Items();
            Assert.True((int)((dynamic)items).Count > 0);
            item = ((dynamic)items).Item(0);
            string id = ((dynamic)item!).ExtendedProperty("System.AppUserModel.ID");
            Assert.False(string.IsNullOrEmpty(id));
            Assert.NotNull(WindowsTargetResolver.Resolve(new WindowsForwardTarget("Installed", [], [], id)));
        }
        finally
        {
            foreach (var value in new[] { item, items, folder, shell })
                if (value is not null && System.Runtime.InteropServices.Marshal.IsComObject(value))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(value);
        }
    }
}
