using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The buy-out edition's promise not to go online, asserted against the compiled
/// assembly rather than against the source tree (design doc 7.3).
/// <para>
/// Source-level checks cannot carry this. A <c>#if</c> around the cloud code
/// compiles clean, passes review, and still leaves the networking IL in the
/// binary for anyone who flips the flag; a <c>deps.json</c> says nothing at all,
/// because framework assemblies are not listed there. What is left is the
/// metadata: if the buy-out assembly has no <c>TypeRef</c> into
/// <c>System.Net.Http</c>, there is no path from its code to a socket, because a
/// <c>TypeRef</c> is the only way one assembly can name a type in another.
/// </para>
/// <para>
/// Read with <c>System.Reflection.Metadata</c>, which ships in the shared
/// framework. A NuGet package here would be a dependency the product does not
/// have, in a test whose job is to check that the product does not have one.
/// </para>
/// <para>
/// <b>Scope, and what this does not cover.</b> This test project targets
/// <c>net10.0</c> and can reference only <c>WeChatBridge.Windows.Core</c>: the WPF
/// assembly is <c>net10.0-windows</c> and cannot be referenced from here, so
/// <c>WeChatBridge.Windows.dll</c> is unchecked. That is a real gap, not a
/// formality — the subscription UI, the QR window and the settings panes live
/// there. It is covered for now only by
/// <c>windows/scripts/Test-NoNetworkCode.ps1</c>, which reads the bytes of both
/// assemblies after the build. That script is namespace-level and textual; this
/// test is a type reference and structural. When the WPF side gains its own
/// reachable test host, the same check belongs over there too.
/// </para>
/// </summary>
public sealed class EditionIsolationTests
{
    /// <summary>
    /// The complete set of <c>System.Net.*</c> types the buy-out build is allowed
    /// to name. Exact equality, not a subset: an allow-list that only has to be
    /// satisfied never rejects anything new, which is the whole failure this
    /// exists to catch.
    /// </summary>
    private static readonly string[] AllowedPerpetualNetworkTypeReferences =
    [
        // Local adapter enumeration, read by Licensing/IMachineFactsReader.cs to
        // get the MAC set for the offline licence recovery anchor. It reads what
        // the driver already holds and opens no socket, so it cannot reach the
        // network -- but it does read hardware identifiers, and that belongs in
        // the privacy wording even though it is not a network path (7.2, 10.11).
        //
        // This is the only entry. Everything else that moves bytes
        // (System.Net.Http, Sockets, WebSockets, Mail, Security) is absent on
        // purpose and must stay absent: those are the ones that can make a
        // connection, and their absence is the promise.
        //
        // The redeem client has landed and it did NOT add an entry here, which
        // is the point worth reading twice. HttpRedeemTransport lives in
        // WeChatBridge.Windows.dll, not in Core: the whole flow except the HTTP
        // verbs is here in Core.Redeem behind IRedeemTransport, precisely so
        // that this list stays empty. Adding System.Net.Http.HttpClient to this
        // array would be wrong twice over -- Core has no such TypeRef, so the
        // exact-equality assertion above would fail, and it would retire the
        // stronger claim ("the assembly that holds all the decisions names no
        // networking type at all") in exchange for a weaker one.
        //
        // The WPF assembly is genuinely unchecked here, because this project
        // targets net10.0 and cannot reference it -- see the scope note on the
        // class. That side is covered by windows/scripts/Test-NoNetworkCode.ps1,
        // which scans both assemblies and carries the one-shot redemption
        // exception in its own allow-list. Design doc 7.2 option B, 18.6.
        "System.Net.NetworkInformation.NetworkInformationException",
        "System.Net.NetworkInformation.NetworkInterface",
        "System.Net.NetworkInformation.PhysicalAddress",
    ];

    /// <summary>
    /// Asserts the buy-out invariant. Compiled out of a Subscription build, where
    /// the assertion is the opposite of the truth and running it would only
    /// produce a failure nobody should act on.
    /// </summary>
#if WECHATBRIDGE_SUBSCRIPTION
    [Fact]
    public void SubscriptionBuildIsNotHeldToTheBuyOutNetworkRule()
    {
        // Present so that an edition change cannot silently delete every test in
        // this file: if the constant stopped being defined, this fails to compile
        // rather than quietly reporting success on a build that was never checked.
        Assert.NotEmpty(AllowedPerpetualNetworkTypeReferences);
    }
#else
    [Fact]
    public void BuyOutBuildNamesOnlyTheAllowedNetworkingTypes()
    {
        var actual = NetworkTypeReferencesIn(typeof(AppLayout).Assembly);

        Assert.Equal(
            AllowedPerpetualNetworkTypeReferences.Order(StringComparer.Ordinal),
            actual.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheAllowListItselfNamesNoRealNetworkPath()
    {
        // The allow-list is the only thing standing between a future contributor
        // and a shipped "we never go online" that quietly does. If someone adds
        // System.Net.Http to it to get a build green, that is a product decision,
        // not a test fix, and this is where it stops looking like one.
        foreach (var allowed in AllowedPerpetualNetworkTypeReferences)
        {
            Assert.StartsWith("System.Net.NetworkInformation.", allowed, StringComparison.Ordinal);
        }
    }
#endif

    /// <summary>
    /// Every <c>System.Net.*</c> type the assembly names, as
    /// <c>Namespace.TypeName</c>.
    /// </summary>
    private static ImmutableArray<string> NetworkTypeReferencesIn(System.Reflection.Assembly assembly)
    {
        var path = assembly.Location;
        Assert.True(File.Exists(path), $"Core assembly is not on disk, so it cannot be inspected: {path}");

        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        Assert.True(peReader.HasMetadata, $"{path} has no metadata, so it cannot be inspected.");

        var reader = peReader.GetMetadataReader();
        var found = ImmutableArray.CreateBuilder<string>();

        foreach (var handle in reader.TypeReferences)
        {
            var reference = reader.GetTypeReference(handle);
            var typeName = reader.GetString(reference.Name);
            var typeNamespace = reader.GetString(reference.Namespace);

            // A top-level TypeRef carries its own namespace. A nested one does
            // not: its Namespace is empty and the outer type is reached through
            // the resolution scope, so the name has to be walked back up or the
            // nested form reports as an unqualified name.
            if (typeNamespace.Length == 0 &&
                reference.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                var declaring = reader.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope);
                typeNamespace = reader.GetString(declaring.Namespace);
                typeName = $"{reader.GetString(declaring.Name)}+{typeName}";
            }

            if (!typeNamespace.StartsWith("System.Net", StringComparison.Ordinal))
            {
                continue;
            }

            found.Add($"{typeNamespace}.{typeName}");
        }

        return found.ToImmutable().Sort(StringComparer.Ordinal);
    }
}
