using System.Security.Cryptography;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// The production <see cref="ISecretProtector"/>: Windows DPAPI.
/// <para>
/// This class lives in the WPF project and nowhere else, and that placement is not a
/// style choice. <c>ProtectedData</c> does not resolve under Core's plain
/// <c>net10.0</c> target framework — it is <c>CS0103: The name 'ProtectedData' does
/// not exist in the current context</c>, measured, not assumed — because
/// <c>System.Security.Cryptography.ProtectedData</c> is only referenced by the SDK
/// once <c>UseWPF</c> (or <c>net10.0-windows</c>) is in effect. Core therefore
/// declares the capability and this project supplies the one implementation. The
/// AppTests project is likewise the only place a real round trip can be exercised.
/// </para>
/// <para>
/// DPAPI needs no key of our own: the user's master key is derived by Windows from
/// the user's credential and the machine's secrets, and only this user's processes
/// on this machine can reach it. That property is the entire reason the licence
/// layer stores the install id protected rather than in the clear.
/// </para>
/// </summary>
internal sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>
    /// Per-user scope, never <see cref="DataProtectionScope.LocalMachine"/>.
    /// <para>
    /// This is the load-bearing decision in the class, so the reason is spelled out
    /// rather than left in the code. A <c>LocalMachine</c> blob is decryptable by
    /// <em>any</em> account on the machine: another local user — a shared or
    /// family PC, a colleague at a hot-desk, a low-privilege account that walks in
    /// from a USB stick — could copy the Config directory out, and each of those
    /// blobs would open for them. The damage is not a stolen licence but a stolen
    /// install identity, which is the thing a copied Config directory is supposed to
    /// be unable to carry to another machine. Worse, it would be silent: the copy
    /// looks like a working install until the point where it matters.
    /// </para>
    /// <para>
    /// The cost of <c>CurrentUser</c> is that a Windows account change or a profile
    /// reset makes the blob unopenable, and the store reports that as "no identity
    /// here" rather than as an error. That is an acceptable trade: the user
    /// re-enters their key once, and <c>LicenseResolver</c> recovers the licence from
    /// <c>license.json</c> and the hardware anchor without consulting the blob at
    /// all. An inconvenience that a reinstall already causes anyway is a far smaller
    /// hole than every local account on the machine being able to lift the licence
    /// out of the install.
    /// </para>
    /// <para>
    /// Worth being precise about what the scope does and does not buy, because it was
    /// measured: on the machine that sealed it, under the same Windows account, a
    /// blob opens under either scope. So this defends against other local accounts and
    /// against another machine — not against another process of the same user, which
    /// could read <c>install-identity.dat</c> to memory anyway if it got that far.
    /// </para>
    /// </summary>
    public byte[] Protect(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> entropy) =>
        ProtectedData.Protect(plain.ToArray(), Optional(entropy), DataProtectionScope.CurrentUser);

    /// <summary>
    /// Throws <see cref="CryptographicException"/> for a blob this user on this
    /// machine cannot open — a wrong entropy, a blob from another account, a profile
    /// reset, or an edited file. <see cref="InstallIdentityStore"/> catches exactly
    /// that and answers "no identity here", which is the correct answer in every one
    /// of those cases.
    /// </summary>
    public byte[] Unprotect(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> entropy) =>
        ProtectedData.Unprotect(blob.ToArray(), Optional(entropy), DataProtectionScope.CurrentUser);

    /// <summary>
    /// DPAPI takes <c>byte[]?</c> and treats an empty array as real entropy, which is
    /// not what "no entropy" means to its caller. The install identity's entropy is a
    /// non-empty constant, so this only matters for a test probing the boundary.
    /// </summary>
    private static byte[]? Optional(ReadOnlySpan<byte> entropy) =>
        entropy.IsEmpty ? null : entropy.ToArray();
}
