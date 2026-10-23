namespace WeChatBridge.Windows.Onboarding;

internal static class ShareRegistrationProbe
{
    public static Task<bool> CheckAsync() => Task.Run(() =>
    {
        try
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();
            return manager.FindPackagesForUser("")
                .Any(package => package.Id.Name == "WeChatBridge.Windows.ShareTarget"
                    && package.Status.VerifyIsOK());
        }
        catch { return false; }
    });
}
