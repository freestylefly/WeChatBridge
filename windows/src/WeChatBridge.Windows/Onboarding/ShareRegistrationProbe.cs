namespace WeChatBridge.Windows.Onboarding;

/// <summary>What the share package looks like from this user's machine.</summary>
public enum ShareRegistrationState
{
    /// <summary>No package record, or a record whose status does not verify.</summary>
    NotRegistered,

    /// <summary>Registered and healthy.</summary>
    Registered,

    /// <summary>
    /// Registered, but pointing at a portable root that is no longer this one —
    /// the drive was re-enumerated to a different letter. Only detectable in
    /// portable mode, and only because the root travelled with the drive in a
    /// marker file; the package record itself stores the external location but
    /// reading it back was not reliable enough to depend on.
    /// </summary>
    Moved,

    /// <summary>
    /// The probe itself failed — typically <c>PackageManager</c> refusing the
    /// call. Distinct from <see cref="NotRegistered"/> on purpose: telling a user
    /// to re-register a package that is registered, because our own query threw,
    /// is a dead end for them.
    /// </summary>
    Unknown,
}

internal static class ShareRegistrationProbe
{
    public const string PackageName = "WeChatBridge.Windows.ShareTarget";

    public static Task<ShareRegistrationState> CheckAsync() => Task.Run(Check);

    public static ShareRegistrationState Check()
    {
        bool registered;
        try
        {
            var manager = new global::Windows.Management.Deployment.PackageManager();
            registered = manager.FindPackagesForUser("")
                .Any(package => package.Id.Name == PackageName
                    && package.Status.VerifyIsOK());
        }
        catch
        {
            return ShareRegistrationState.Unknown;
        }
        if (!registered)
            return ShareRegistrationState.NotRegistered;
        return ShareRegistrationMarker.IsCurrent() ? ShareRegistrationState.Registered
            : ShareRegistrationState.Moved;
    }
}
