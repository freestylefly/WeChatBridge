namespace WeChatBridge.Windows.Core.Activation;

/// <summary>
/// How often to ask again while waiting for a human.
/// <para>
/// Two different waits need this. Waiting for a phone scan is short — the user is
/// looking at a QR code they just opened — so it starts eager and settles at a
/// couple of seconds. Waiting for a seller to look at a payment and confirm it is
/// long: minutes to hours. Both need the same property, which is that the wait
/// <i>stops talking</i>: a fixed one-second poll against a service that is not
/// answering is 3600 pointless requests an hour, and it looks from the outside
/// exactly like something is stuck in a loop.
/// </para>
/// <para>
/// Pure arithmetic with no clock and no await, so the shape of the wait is a unit
/// test rather than a sleep.
/// </para>
/// </summary>
/// <param name="First">The pause before the second attempt.</param>
/// <param name="Ceiling">The longest the wait ever gets.</param>
/// <param name="Multiplier">Growth per attempt, above 1.</param>
public sealed record PollSchedule(TimeSpan First, TimeSpan Ceiling, double Multiplier)
{
    /// <summary>
    /// For the QR login. Five minutes is the server's ticket TTL, and the schedule
    /// reaches the ceiling in about thirty seconds — so by the time a user has
    /// finished fumbling with their phone, the app has settled into one request every
    /// ten seconds instead of hammering.
    /// </summary>
    public static PollSchedule Login { get; } =
        new(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(10), 1.5);

    /// <summary>
    /// For the activation wait, where a seller may take an hour to check a payment.
    /// The ceiling is ten seconds: frequent enough that the moment they click 确认 is
    /// the moment the user's window changes, rare enough that a seller who walks away
    /// does not keep a customer's laptop pinging all afternoon.
    /// </summary>
    public static PollSchedule Activation { get; } =
        new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), 1.4);

    /// <summary>How long to wait before attempt number <paramref name="attempt"/> (0-based).</summary>
    public TimeSpan DelayBefore(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        if (attempt == 0 || First <= TimeSpan.Zero)
            return First < TimeSpan.Zero ? TimeSpan.Zero : First;
        if (Multiplier <= 1.0)
            return First;

        // Doubling the exponent rather than multiplying out the series: at attempt
        // 200 the naive loop is a long chain of TimeSpan multiplications, and the
        // ceiling check is only worth having if it cannot itself overflow.
        var scale = Math.Pow(Multiplier, attempt);
        var milliseconds = First.TotalMilliseconds * scale;
        if (double.IsNaN(milliseconds) || milliseconds >= Ceiling.TotalMilliseconds)
            return Ceiling;
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>How many attempts fit inside <paramref name="budget"/> at this cadence.</summary>
    public int AttemptsWithin(TimeSpan budget)
    {
        var total = TimeSpan.Zero;
        var attempts = 0;
        for (var attempt = 0; attempt < 10_000; attempt++)
        {
            total += DelayBefore(attempt);
            if (total > budget)
                return attempts;
            attempts++;
        }
        return attempts;
    }
}
