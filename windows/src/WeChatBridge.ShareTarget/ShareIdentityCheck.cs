using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WeChatBridge.ShareTarget;

internal static class ShareIdentityCheck
{
    public static int Run()
    {
        try
        {
            var package = ReadIdentity(GetCurrentPackageFullName);
            var appId = ReadIdentity(GetCurrentApplicationUserModelId);
            Console.WriteLine(JsonSerializer.Serialize(new { PackageFullName = package, ApplicationUserModelId = appId }));
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { Error = error.Message }));
            return 1;
        }
    }

    private delegate int IdentityReader(ref uint length, StringBuilder? buffer);

    private static string ReadIdentity(IdentityReader reader)
    {
        uint length = 0;
        var result = reader(ref length, null);
        if (result != 122 || length == 0)
            throw new InvalidOperationException($"Package identity query failed: {result}");
        var buffer = new StringBuilder(checked((int)length));
        result = reader(ref length, buffer);
        if (result != 0)
            throw new InvalidOperationException($"Package identity query failed: {result}");
        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentApplicationUserModelId(ref uint length, StringBuilder? buffer);
}
