using System.Diagnostics;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// The keeper election is decided by a named mutex, so it can only be observed
/// across processes: one instance wins and stays resident serving the share
/// pipe, the loser must shut its own dispatcher down and exit. A loser that
/// lingers pins ~25 MB of WinRT projections per stray copy — and the helper is
/// launched once per share, so the population is unbounded.
/// </summary>
public sealed class ShareTargetKeeperTests
{
    /// <summary>Time the first instance needs to win (or lose) the election before a second one races it.</summary>
    private const int SettleMilliseconds = 3_000;

    /// <summary>
    /// Generous against the exit itself — the fixed bug left losers running
    /// indefinitely — while staying well under the runner's own patience.
    /// </summary>
    private const int ExitBudgetMilliseconds = 20_000;

    private static string HelperPath =>
        Path.Combine(AppContext.BaseDirectory, "share-target", "WeChatBridge.ShareTarget.exe");

    [Fact]
    public void LoserOfKeeperElectionExitsInsteadOfLingering()
    {
        Assert.True(File.Exists(HelperPath), $"分享入口产物缺失：{HelperPath}");

        var incumbent = Start();
        Process? contender = null;
        try
        {
            // Let the first instance settle before the second starts, so the
            // contender can never win the race and the assertion holds whether
            // the incumbent took the mutex or an already-running keeper did.
            incumbent.WaitForExit(SettleMilliseconds);
            contender = Start();

            Assert.True(contender.WaitForExit(ExitBudgetMilliseconds),
                "落选的分享入口没有退出：dispatcher 的关闭请求发到了线程池上的另一个 dispatcher。");
        }
        finally
        {
            contender?.Kill(entireProcessTree: true);
            incumbent.Kill(entireProcessTree: true);
        }
    }

    private static Process Start() => Process.Start(new ProcessStartInfo(HelperPath)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(HelperPath)!,
    })!;
}
