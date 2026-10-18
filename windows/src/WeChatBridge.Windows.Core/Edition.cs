namespace WeChatBridge.Windows.Core;

/// <summary>
/// Which of the two products this binary <em>is</em>.
/// <para>
/// This is a compile-time constant, not a setting and not a feature flag. The two
/// editions are separate products built from one source tree, and the symbol that
/// selects between them comes from <c>Directory.Build.props</c>
/// (<c>WeChatBridgeEditionConstant</c>) — so the other edition's code is not merely
/// switched off in the build that excludes it, it is not in that binary at all.
/// Nothing in a Perpetual binary can read a subscription endpoint, and nothing in a
/// subscription binary carries buy-out pricing, because neither one was compiled.
/// <c>Test-NoNetworkCode.ps1</c> checks the same property from the outside; this
/// constant is how the code says it to itself.
/// </para>
/// <para>
/// A feature flag was the obvious alternative and is wrong here. A flag makes the
/// other edition's code present-but-dormant: it ships, it can be set by editing a
/// file, and "this build has no network code" becomes a claim about a config file
/// rather than about the executable. The constants below are the whole mechanism.
/// </para>
/// <para>
/// Everything here is a <c>const</c>, so the compiler folds each one and drops the
/// branch it did not take. <c>#if</c> rather than a conditional expression on
/// <see cref="Name"/> for the same reason: the losing literal never reaches the IL.
/// </para>
/// </summary>
public static class Edition
{
#if WECHATBRIDGE_SUBSCRIPTION
    /// <summary>Machine-readable edition name, used in logs and diagnostics.</summary>
    public const string Name = "Subscription";

    /// <summary>
    /// Whether an account can exist. False in a buy-out build — permanently, not as a
    /// setting — so the whole account surface can be excluded with
    /// <c>#if Edition.HasAccount</c> rather than hidden at runtime.
    /// </summary>
    public const bool HasAccount = true;

    /// <summary>
    /// The trailing word on the About panel's version line. A localisation key, not a
    /// literal to display: <c>L10n.Text</c> falls back to the key itself, so the
    /// Chinese here is the source string and <c>en.json</c> carries the translation.
    /// </summary>
    public const string DisplayName = "订阅版";
#else
    /// <summary>Machine-readable edition name, used in logs and diagnostics.</summary>
    public const string Name = "Perpetual";

    /// <summary>
    /// Buy-out has no account, ever: there is nothing to log into and nothing to
    /// cancel. False as a constant so the account surface compiles out rather than
    /// being shipped and hidden.
    /// </summary>
    public const bool HasAccount = false;

    /// <summary>
    /// The trailing word on the About panel's version line. A localisation key, not a
    /// literal to display: <c>L10n.Text</c> falls back to the key itself, so the
    /// Chinese here is the source string and <c>en.json</c> carries the translation.
    /// </summary>
    public const string DisplayName = "买断版";
#endif
}
