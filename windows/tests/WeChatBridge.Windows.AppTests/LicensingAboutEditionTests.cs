using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// The About panel's version line, which now ends in the edition name.
/// <para>
/// The two editions carry different version numbers on purpose, so this line is how
/// support learns which product someone is running. Two things about it are easy to
/// break silently and impossible to notice by eye: <c>L10n.Text</c> falls back to
/// the Chinese key when a translation is missing, so a forgotten <c>en.json</c> entry
/// shows 买断版 inside an otherwise English sentence and raises nothing; and the edition
/// name is a compile-time constant, so a translation for the other edition only ever
/// matters in the other build — which is exactly the one nobody is looking at.
/// </para>
/// </summary>
public sealed class LicensingAboutEditionTests
{
    [Fact]
    public void TheVersionLineNamesTheEditionInBothLanguages()
    {
        // The expectations are literals per edition, deliberately NOT
        // L10n.Text(Edition.DisplayName): building the expectation from the same
        // call the pane makes would make this tautological and it would stop
        // catching a missing translation, which is the whole point of it. The
        // edition matrix builds this assembly twice, so each build gets the
        // branch that names its own shape.
#if WECHATBRIDGE_SUBSCRIPTION
        const string ExpectedChinese = "版本 1.0.8 (abc12345) · Windows · 订阅版";
        const string ExpectedEnglish = "Version 1.0.8 (abc12345) · Windows · Subscription";
#else
        const string ExpectedChinese = "版本 1.0.8 (abc12345) · Windows · 买断版";
        const string ExpectedEnglish = "Version 1.0.8 (abc12345) · Windows · Buyout";
#endif

        var original = L10n.Language;
        try
        {
            L10n.Language = "zh-Hans";
            Assert.Equal(ExpectedChinese, AboutVersionLine.Render("1.0.8 (abc12345)", Edition.DisplayName));

            L10n.Language = "en";
            Assert.Equal(ExpectedEnglish, AboutVersionLine.Render("1.0.8 (abc12345)", Edition.DisplayName));
        }
        finally
        {
            L10n.Language = original;
        }
    }

    /// <summary>
    /// Every edition name has a translation, including the one this build is not.
    /// <para>
    /// Constants are the problem here: only one of these two strings can appear in a
    /// given binary, so a build that is missing its own translation can still be green
    /// while the other edition has nothing at all. Checking both in both builds turns
    /// that into a red test on whichever one forgot.
    /// </para>
    /// </summary>
    [Fact]
    public void BothEditionNamesAreTranslated()
    {
        var original = L10n.Language;
        try
        {
            L10n.Language = "en";
            Assert.Equal("Buyout", L10n.Text("买断版"));
            Assert.Equal("Subscription", L10n.Text("订阅版"));
        }
        finally
        {
            L10n.Language = original;
        }
    }

    /// <summary>
    /// The pane is not constructed here. WPF allows one <see cref="Application"/> per
    /// AppDomain and this assembly already builds one for <c>UiSmokeTests</c>, so a
    /// second <c>new Application()</c> from this suite would take that test down with it.
    /// The line above is built by the exact expression the pane's constructor uses, and
    /// <c>UiSmokeTests</c> renders <c>AboutPane</c> in both languages already — together
    /// they cover the string and the pane, without a second owner of the AppDomain.
    /// </summary>
}

internal static class AboutVersionLine
{
    /// <summary>
    /// The exact expression <c>AboutPane</c> uses. Written out here rather than shared
    /// with the pane, because a shared helper would let a change to the pane's line and
    /// a change to this expectation move in step — which is precisely the regression
    /// worth failing on.
    /// </summary>
    public static string Render(string version, string edition) =>
        L10n.Format($"版本 {version} · Windows · {L10n.Text(edition)}");
}
