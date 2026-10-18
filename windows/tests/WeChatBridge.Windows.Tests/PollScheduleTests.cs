using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The backoff arithmetic, pinned: the two shipped schedules, the ceiling, the
/// degenerate multipliers, and the counting helper.
/// <para>
/// This is pure arithmetic with no clock and no await — which is exactly why it is
/// worth testing. The failure it guards against is a schedule that grows without
/// bound (a customer's laptop pinging a silent server all afternoon) or one that
/// never grows (a phone-scan wait hammering at one request per second). Neither is
/// visible in a test that sleeps.
/// </para>
/// </summary>
public sealed class PollScheduleTests
{
    [Fact]
    public void TheFirstAttemptWaitsExactlyTheFirstDelay()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.5), PollSchedule.Login.DelayBefore(0));
        Assert.Equal(TimeSpan.FromSeconds(5), PollSchedule.Activation.DelayBefore(0));
    }

    /// <summary>
    /// The login schedule settles at its ceiling in about thirty seconds, so a user
    /// fumbling with their phone leaves the app quiet rather than loud: 1.5s, 2.25s,
    /// 3.375s, 5.0625s, 7.59375s, then ten seconds forever.
    /// </summary>
    [Fact]
    public void TheLoginScheduleGrowsUntilTheCeilingAndStopsThere()
    {
        var schedule = PollSchedule.Login;

        Assert.Equal(TimeSpan.FromSeconds(2.25), schedule.DelayBefore(1));
        Assert.Equal(TimeSpan.FromSeconds(3.375), schedule.DelayBefore(2));
        Assert.Equal(TimeSpan.FromSeconds(5.0625), schedule.DelayBefore(3));
        Assert.Equal(TimeSpan.FromSeconds(7.59375), schedule.DelayBefore(4));
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.DelayBefore(5));
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.DelayBefore(50));
        // Far past the point where the naive multiply-out would overflow TimeSpan.
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.DelayBefore(10_000));
    }

    /// <summary>
    /// The activation wait is longer to begin with and grows more slowly, because
    /// the thing being waited for is a human reading a payment notification.
    /// </summary>
    [Fact]
    public void TheActivationScheduleStartsHigherAndCeilingsAtTenSeconds()
    {
        var schedule = PollSchedule.Activation;

        Assert.Equal(7000, schedule.DelayBefore(1).TotalMilliseconds, 3);
        Assert.Equal(9800, schedule.DelayBefore(2).TotalMilliseconds, 3);
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.DelayBefore(3));
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.DelayBefore(100));
    }

    /// <summary>
    /// A multiplier at or below one means "no growth" — a constant poll — which is
    /// the shape a caller gets when it passes a schedule it built by hand. It must
    /// not silently become zero, and it must not grow.
    /// </summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.0)]
    public void AMultiplierThatDoesNotGrowKeepsTheFirstDelay(double multiplier)
    {
        var schedule = new PollSchedule(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), multiplier);

        Assert.Equal(TimeSpan.FromSeconds(2), schedule.DelayBefore(0));
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.DelayBefore(1));
        Assert.Equal(TimeSpan.FromSeconds(2), schedule.DelayBefore(500));
    }

    /// <summary>
    /// A non-positive first delay is a caller that wants "ask immediately, every
    /// time" — zero, not a negative wait, and not the ceiling.
    /// </summary>
    [Fact]
    public void ANonPositiveFirstDelayWaitsNothingRatherThanGoingBackwards()
    {
        var zero = new PollSchedule(TimeSpan.Zero, TimeSpan.FromSeconds(10), 1.5);
        Assert.Equal(TimeSpan.Zero, zero.DelayBefore(0));
        Assert.Equal(TimeSpan.Zero, zero.DelayBefore(7));

        var negative = new PollSchedule(TimeSpan.FromSeconds(-3), TimeSpan.FromSeconds(10), 1.5);
        Assert.Equal(TimeSpan.Zero, negative.DelayBefore(0));
        Assert.Equal(TimeSpan.Zero, negative.DelayBefore(7));
    }

    [Fact]
    public void ANegativeAttemptIsAProgrammingError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PollSchedule.Login.DelayBefore(-1));
    }

    /// <summary>
    /// The counting helper sums the schedule it is given — including the growth —
    /// and stops at the first delay that would overrun the budget. For the login
    /// schedule inside twenty seconds: 1.5 + 2.25 + 3.375 + 5.0625 + 7.59375 =
    /// 19.78, and the sixth would push past 29.
    /// </summary>
    [Fact]
    public void AttemptsWithinCountsTheGrowingWaitsItWouldActuallySpend()
    {
        Assert.Equal(5, PollSchedule.Login.AttemptsWithin(TimeSpan.FromSeconds(20)));
        Assert.Equal(0, PollSchedule.Login.AttemptsWithin(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, PollSchedule.Login.AttemptsWithin(TimeSpan.FromSeconds(1.5)));

        // Activation starts at five seconds, so the same twenty-second budget fits
        // fewer attempts even though the ceiling is the same.
        Assert.Equal(2, PollSchedule.Activation.AttemptsWithin(TimeSpan.FromSeconds(12)));
    }

    /// <summary>
    /// The two shipped schedules are separate values, not one schedule read twice:
    /// a change to the login cadence must not silently move the activation cadence.
    /// </summary>
    [Fact]
    public void TheTwoShippedSchedulesAreDistinctValues()
    {
        Assert.NotEqual(PollSchedule.Login, PollSchedule.Activation);
        Assert.NotSame(PollSchedule.Login, PollSchedule.Activation);
    }
}
