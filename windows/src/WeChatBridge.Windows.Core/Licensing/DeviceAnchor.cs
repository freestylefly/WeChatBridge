using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>
/// How much the hardware anchor says about "is this the machine the licence was
/// issued on".
/// <para>
/// Three tiers, not two. <see cref="NoEvidence"/> is not a softer
/// <see cref="DifferentMachine"/>: it is the case where the registry could not be
/// read, the adapter list came back empty, or no anchor was ever recorded. Deciding
/// that a paying customer who reinstalled Windows has lost the product because the
/// hardware query failed is the one mistake this whole layer must not make — the
/// opposite error costs one extra device against the server-side device cap.
/// </para>
/// </summary>
public enum AnchorEvidence
{
    /// <summary>Firmware matches and the two adapter sets intersect.</summary>
    SameMachine,

    /// <summary>Nothing conclusive either way. Admit and recover.</summary>
    NoEvidence,

    /// <summary>Different firmware, or two populated adapter sets that do not intersect.</summary>
    DifferentMachine,
}

/// <summary>
/// The hardware facts a licence can be recovered against: firmware model, physical
/// adapter set, and the OS product name for the server-side device list.
/// <para>
/// <c>MachineGuid</c> is deliberately absent. It is the <em>operating system</em>
/// identity, so it is the first thing a reinstall destroys — exactly the wrong field
/// to carry reinstall recovery. Firmware model and physical MACs survive a reinstall,
/// which is why they carry that job; see the design notes in §10.11.
/// </para>
/// <para>
/// Values are normalised in the constructor, so two callers that enumerate the same
/// machine in different orders produce byte-identical anchors. That is what makes the
/// stored anchor comparable after a reinstall, where adapter enumeration order and
/// OEM field casing are both free to change.
/// </para>
/// </summary>
public sealed record DeviceAnchor
{
    /// <summary>
    /// The sentinel for "this section produced nothing". Written explicitly rather than
    /// as an empty string so the canonical payload keeps a fixed line count.
    /// </summary>
    public const string Absent = "-";

    [JsonConstructor]
    public DeviceAnchor(string firmware, IEnumerable<string>? macs, string? os)
    {
        Firmware = NormalizeFirmware(firmware);
        Macs = NormalizeMacs(macs);
        Os = NormalizeOs(os);
    }

    /// <summary>Order-insensitive, case-insensitive firmware model, or <see cref="Absent"/>.</summary>
    [JsonPropertyName("fw")]
    public string Firmware { get; init; }

    /// <summary>Globally administered, unicast physical addresses, sorted and de-duplicated.</summary>
    [JsonPropertyName("mac")]
    public IReadOnlyList<string> Macs { get; init; }

    /// <summary>OS product name with the version stripped, or <see cref="Absent"/>.</summary>
    [JsonPropertyName("os")]
    public string Os { get; init; }

    /// <summary>The exact bytes the digest is taken over.</summary>
    public string Canonical =>
        $"fw={Firmware}\nmac={(Macs.Count == 0 ? Absent : string.Join(",", Macs))}\nos={Os}";

    public override string ToString() => Canonical;

    /// <summary>
    /// Builds an anchor from raw registry and adapter output. Every field is optional
    /// because every source can legitimately fail — a locked-down registry view, a
    /// virtual machine with no physical NIC, a caller that wants to hand-construct a
    /// value in a test.
    /// </summary>
    public static DeviceAnchor Create(
        IEnumerable<string>? firmwareValues = null,
        IEnumerable<string>? macAddresses = null,
        string? os = null) =>
        new(
            string.Join("|", firmwareValues ?? []),
            macAddresses ?? [],
            os ?? Absent);

    /// <summary>
    /// The three tiers, in the order the design gives them priority: a firmware
    /// mismatch is counter-evidence on its own, matching firmware still needs the
    /// adapter sets to intersect, and an unreadable side yields
    /// <see cref="AnchorEvidence.NoEvidence"/> rather than a verdict.
    /// </summary>
    public static AnchorEvidence Compare(DeviceAnchor? stored, DeviceAnchor observed)
    {
        // No recorded anchor means nothing has ever claimed this directory's licence
        // came from this machine. Absence of a claim is not a refutation of one.
        if (stored is null)
            return AnchorEvidence.NoEvidence;

        var firmwareComparable = stored.Firmware != Absent && observed.Firmware != Absent;
        if (firmwareComparable && !string.Equals(stored.Firmware, observed.Firmware, StringComparison.Ordinal))
            return AnchorEvidence.DifferentMachine;

        // A populated set on both sides that does not intersect is counter-evidence
        // regardless of firmware: it means none of the original adapters is present.
        if (stored.Macs.Count > 0 && observed.Macs.Count > 0)
            return stored.Macs.Intersect(observed.Macs, StringComparer.Ordinal).Any()
                ? AnchorEvidence.SameMachine
                : AnchorEvidence.DifferentMachine;

        // Firmware agrees but at least one side has no adapter material, so the
        // intersection test could not run. Same firmware is not enough to call it a
        // match: the adapter set is the part that survives a reinstall, and without it
        // the comparison has not actually happened.
        return AnchorEvidence.NoEvidence;
    }

    /// <summary>
    /// Uppercases, trims and collapses internal whitespace, so an OEM that pads a
    /// firmware string or varies its casing between reboots does not read as a
    /// different machine. Empty inputs become <see cref="Absent"/>.
    /// </summary>
    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Absent;
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim().ToUpperInvariant())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = true;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    /// <summary>
    /// The firmware fields are treated as an unordered set. Their registry roles differ
    /// between OEMs and a machine that reports the same three values in a different
    /// order is the same machine; pinning them to slots would make a vendor quirk read
    /// as a hardware swap.
    /// </summary>
    private static string NormalizeFirmware(string? firmware)
    {
        if (string.IsNullOrWhiteSpace(firmware))
            return Absent;

        var parts = firmware
            .Split(['|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeText)
            .Where(part => part != Absent)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        var joined = string.Join("|", parts);
        return joined.Length == 0 ? Absent : joined;
    }

    /// <summary>
    /// Drops locally administered and multicast addresses, then sorts.
    /// <para>
    /// The filter is not paranoia: a machine enumeration here returned 31 adapters of
    /// which 10 had empty addresses (WAN miniports, loopback) and one real adapter had
    /// a locally administered MAC because it was virtualised. Keeping those would let a
    /// single Wi-Fi Direct connection invent a "new adapter" and change the fingerprint.
    /// </para>
    /// <para>
    /// The result is a set, not a digest, because a USB adapter, a Wi-Fi Direct
    /// session or a Bluetooth hotspot all change the aggregate digest while meaning
    /// nothing. Comparison is set intersection, which only fails when none of the
    /// original adapters is present.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> NormalizeMacs(IEnumerable<string>? macAddresses)
    {
        if (macAddresses is null)
            return [];

        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var candidate in macAddresses)
        {
            var mac = NormalizeMac(candidate);
            if (mac is not null)
                normalized.Add(mac);
        }
        return [.. normalized];
    }

    private static string? NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        Span<char> digits = stackalloc char[12];
        var count = 0;
        foreach (var character in value)
        {
            if (character is '-' or ':' or '.' or ' ')
                continue;
            if (!Uri.IsHexDigit(character) || count == digits.Length)
                return null;
            digits[count++] = char.ToUpperInvariant(character);
        }
        if (count != digits.Length)
            return null;

        // Bit 0 of the first octet is the multicast flag; bit 1 is the locally
        // administered (randomised or virtual) flag. Neither identifies hardware.
        var first = Convert.ToInt32(new string(digits[..2]), 16);
        if ((first & 0x03) != 0)
            return null;
        return new string(digits);
    }

    /// <summary>
    /// Keeps the product name and drops the version. The OS version moves on every
    /// feature update, and nothing in the comparison reads this field, so a version
    /// here would only churn the fingerprint the server stores as a device label.
    /// </summary>
    private static string NormalizeOs(string? os)
    {
        var text = NormalizeText(os);
        if (text == Absent)
            return Absent;

        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsAsciiDigit(text[index]))
                continue;
            if (index > 0 && !char.IsWhiteSpace(text[index - 1]))
                continue;
            return index == 0 ? Absent : text[..index].TrimEnd();
        }
        return text;
    }
}

/// <summary>
/// Stable, server-comparable digest of a <see cref="DeviceAnchor"/>.
/// <para>
/// The domain separator is not decoration: this digest and the licence payload hash
/// are both SHA-256 over attacker-influenced text, and without a separator the two
/// domains could be confused for each other. It is a constant, so the server has to
/// use the same string — it is published with the format, not derived.
/// </para>
/// </summary>
public static class AnchorDigest
{
    public const string DomainSeparator = "wechatbridge.device-anchor.v1";

    public static string Compute(DeviceAnchor anchor) =>
        Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(DomainSeparator + "\n" + anchor.Canonical)));
}
