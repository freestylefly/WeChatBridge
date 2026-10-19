using System.Security.Cryptography;
using System.Text;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Core.Redeem;

/// <summary>
/// What the client tells the server about the machine it is redeeming on.
/// <para>
/// <c>deviceDigest</c> is a digest, never the anchor itself. The server keeps it next to
/// the burn so that a second, different machine redeeming the same code is visible in
/// the ledger as what it is. Sending the raw anchor would put every customer's MAC
/// addresses on a server for the sake of a value that is only ever compared against
/// itself, and this product's whole privacy argument is that it does not do that.
/// </para>
/// <para>
/// <see cref="InstallId"/> is not a digest and not secret either, but it is: it is the
/// identity the licence will be bound to, and it lives DPAPI-protected locally. It has
/// to travel here, because binding a licence to a machine the server has never heard of
/// is the entire point of the buy-out edition.
/// </para>
/// </summary>
/// <param name="InstallId">The local install identity, or <c>null</c> when none could be established.</param>
/// <param name="DeviceDigest">base64url over SHA-256 of the anchor's canonical text.</param>
public sealed record RedeemMachineIdentity(string? InstallId, string DeviceDigest)
{
    /// <summary>
    /// The digest form of a hardware anchor. Stable across reinstalls by construction:
    /// it is taken over <see cref="DeviceAnchor.Canonical"/>, which is the normalised,
    /// sorted, zone-free text.
    /// </summary>
    public static string DigestOf(DeviceAnchor anchor) =>
        Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(anchor.Canonical)));

    public bool CanBind => !string.IsNullOrWhiteSpace(InstallId);
}
