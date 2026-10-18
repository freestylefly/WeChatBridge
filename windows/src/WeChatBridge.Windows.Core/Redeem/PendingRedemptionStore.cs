using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core.Redeem;

/// <summary>
/// The redemption this installation started and has not yet seen the answer to.
/// <para>
/// <b>This file is the whole answer to "the response was lost".</b> The server burns
/// the code, issues the licence and writes its idempotency table, and then the 200
/// goes missing on a train, a flaky hotel Wi-Fi or a lid closing. The client cannot
/// tell that from a code that was never sent. If the only thing it remembered were the
/// code, the user's next move — press Retry — would come back <c>409, already used on
/// another device</c>, for a machine he is sitting at. That is the "I paid, you say
/// I'm lying" moment, and it is the reason this record is written <em>before</em> the
/// first byte goes out.
/// </para>
/// <para>
/// With the request id on disk, every later attempt carries the same key, and the
/// server — which checks its idempotency table <em>before</em> it answers 409 — hands
/// back the original envelope bytes. The retry stops being "again" and becomes "what
/// did you decide".
/// </para>
/// <para>
/// The code is stored as <see cref="RedeemCode.Digest"/>, never in the clear: on a
/// portable build this file is plain text on a removable drive that travels, and a
/// redemption code sitting next to the licence it buys is the one credential worth
/// stealing from a bag.
/// </para>
/// </summary>
public sealed record PendingRedemption(
    [property: JsonPropertyName("v")] int SchemaVersion,
    [property: JsonPropertyName("code")] string CodeDigest,
    [property: JsonPropertyName("rid")] string RequestId,
    [property: JsonPropertyName("at")] DateTimeOffset CreatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static PendingRedemption Create(string codeDigest, DateTimeOffset now, string? requestId = null) =>
        new(
            CurrentSchemaVersion,
            codeDigest,
            requestId ?? RedeemCode.NewRequestId(),
            now);
}

/// <summary>
/// Reads and writes <c>redeem-pending.json</c>, the idempotency record described on
/// <see cref="PendingRedemption"/>.
/// <para>
/// It takes its directory rather than reading <see cref="AppLayout.Current"/>, for the
/// same reason <see cref="Licensing.LicenseStore"/> does: a default would pull the
/// process-wide layout into this layer and every test would write into the developer's
/// real profile.
/// </para>
/// </summary>
public sealed class PendingRedemptionStore
{
    public const string FileName = "redeem-pending.json";

    private readonly string _directory;

    public PendingRedemptionStore(string configDirectory) => _directory = configDirectory;

    public string FilePath => Path.Combine(_directory, FileName);

    /// <summary>
    /// The pending record, or <c>null</c> when there is none this build can trust.
    /// <para>
    /// Missing, unreadable, malformed and written by a newer format all collapse to
    /// the same answer, and deliberately so. The only thing a pending record is for is
    /// to make a retry reuse a request id; if it cannot be read, there is no id to
    /// reuse, and the correct behaviour is the plain one — send the code and let the
    /// server burn it. Failing closed here instead would strand a customer who lost a
    /// recoverable redemption to a corrupted file, and the server's <c>409</c> is the
    /// honest answer in that case anyway.
    /// </para>
    /// </summary>
    public PendingRedemption? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            var record = JsonSerializer.Deserialize<PendingRedemption>(File.ReadAllText(FilePath), JsonOptions);
            return record is null
                || record.SchemaVersion != PendingRedemption.CurrentSchemaVersion
                || string.IsNullOrWhiteSpace(record.CodeDigest)
                || string.IsNullOrWhiteSpace(record.RequestId)
                    ? null
                    : record;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the record and swaps the file in atomically.</summary>
    /// <remarks>
    /// Staged and renamed rather than written in place, and that is not tidiness: the
    /// record is the difference between a recoverable retry and a customer arguing
    /// with support. A power cut in the middle of a <c>WriteAllText</c> would leave
    /// this file truncated, and a truncated idempotency key is worse than none.
    /// </remarks>
    public void Save(PendingRedemption pending)
    {
        Directory.CreateDirectory(_directory);
        var staging = FilePath + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(pending, JsonOptions));
        File.Move(staging, FilePath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
        // A staging file left by an interrupted save is debris, not state.
        if (File.Exists(FilePath + ".tmp"))
            File.Delete(FilePath + ".tmp");
    }

    private static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
