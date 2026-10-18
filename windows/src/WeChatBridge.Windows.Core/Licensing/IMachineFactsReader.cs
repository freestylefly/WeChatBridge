using System.Net.NetworkInformation;
using System.Runtime.Versioning;

namespace WeChatBridge.Windows.Core.Licensing;

/// <summary>
/// Source of the hardware facts a licence recovers against. An interface because the
/// recovery state machine has to be exercisable without a registry: every interesting
/// case in it is a case where the facts are wrong, missing, or belong to another
/// machine.
/// </summary>
public interface IMachineFactsReader
{
    /// <summary>
    /// Reads the anchor for the current machine. Must not throw: a registry view that
    /// cannot be opened is <see cref="AnchorEvidence.NoEvidence"/> waiting to happen,
    /// not an error.
    /// </summary>
    DeviceAnchor Read();
}

/// <summary>
/// The production reader: <c>HKLM\HARDWARE\DESCRIPTION\System\BIOS</c> for the
/// firmware model and <see cref="NetworkInterface"/> for the adapter set.
/// <para>
/// WMI is out by design. <c>Win32_ComputerSystemProduct.UUID</c> answers the same
/// question and <c>System.Management</c> is a separate NuGet package, which would
/// cost the runtime's zero-dependency guarantee for no extra stability — the UUID
/// changes on reinstall exactly like <c>MachineGuid</c> does.
/// </para>
/// <para>
/// The whole class is annotated <c>windows</c> because <c>Registry.LocalMachine</c>
/// trips CA1416, and warnings are errors in this repository. Consumers reach it
/// through <see cref="IMachineFactsReader"/>, which carries no such requirement, so
/// the resolver stays portable and testable.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryMachineFacts : IMachineFactsReader
{
    public const string BiosKeyPath = @"HARDWARE\DESCRIPTION\System\BIOS";

    /// <summary>
    /// The three model fields, and only these. <c>BIOSVersion</c> and
    /// <c>BaseBoardVersion</c> change on a firmware update, which would read as a
    /// hardware swap, and <c>SystemProductId</c> measured as an empty string on the
    /// reference machine — it carries nothing.
    /// </summary>
    private static readonly string[] FirmwareValueNames =
    [
        "SystemManufacturer",
        "SystemProductName",
        "BaseBoardProduct"
    ];

    /// <summary>Entry point for the WPF layer, which is the only place that may name this type.</summary>
    public static IMachineFactsReader CreateDefault() => new RegistryMachineFacts();

    public DeviceAnchor Read()
    {
        var firmware = new List<string>();
        try
        {
            using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(BiosKeyPath);
            if (bios is not null)
            {
                foreach (var name in FirmwareValueNames)
                {
                    var value = bios.GetValue(name) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                        firmware.Add(value);
                }
            }
        }
        catch (System.Security.SecurityException)
        {
            // An unprivileged reader can be refused; that is NoEvidence, not a failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        return DeviceAnchor.Create(firmware, PhysicalMacs(), System.Runtime.InteropServices.RuntimeInformation.OSDescription);
    }

    private static IEnumerable<string> PhysicalMacs()
    {
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        var addresses = new List<string>();
        foreach (var adapter in adapters)
        {
            try
            {
                var address = adapter.GetPhysicalAddress();
                if (address.GetAddressBytes().Length == 0)
                    continue;
                addresses.Add(string.Join("", address.GetAddressBytes().Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))));
            }
            catch (PlatformNotSupportedException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }
        return addresses;
    }
}
