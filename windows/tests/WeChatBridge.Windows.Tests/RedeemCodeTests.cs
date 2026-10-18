using System.Security.Cryptography;
using WeChatBridge.Windows.Core.Licensing;
using WeChatBridge.Windows.Core.Redeem;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The normalisation table, which is the one part of this flow the server has to share
/// verbatim. If the two tables disagree every code is invalid and nothing anywhere
/// throws — so this is pinned as a contract rather than exercised as a convenience.
/// </summary>
public sealed class RedeemCodeTests
{
    /// <summary>
    /// Twenty Crockford characters in four groups of five.
    /// <para>
    /// Note that this is not the shape the design doc's illustrative example uses
    /// (<c>WCB-7K3M-9XQ2-H4TB-6PWN</c>, four groups of four). A real code has twenty
    /// body characters — that is what makes brute force infeasible and what the
    /// hundred-bit entropy argument in §3.3 is about — so the example is read as
    /// showing the grouping convention rather than the length. The endpoint and the
    /// server's generator have to agree with this file, not with the example.
    /// </para>
    /// </summary>
    private const string Sample = "WCB-7K3MQ-9XQ2H-4TB6P-WN3DP";

    [Fact]
    public void TheCanonicalFormIsTheDocumentedShape()
    {
        Assert.True(RedeemCode.TryParse(Sample, out var code));
        Assert.Equal(Sample, code!.Canonical);
        Assert.Equal(4, RedeemCode.GroupCount);
        Assert.Equal(5, RedeemCode.GroupLength);
        Assert.Equal(20, RedeemCode.BodyLength);
        Assert.Equal(23, RedeemCode.NormalizedLength);
        // PrefixLength is spelled out so the arithmetic above stays a constant
        // expression; this is the test that stops the two from drifting.
        Assert.Equal(RedeemCode.PrefixLength, RedeemCode.Prefix.Length);
        // Four groups, four separators, prefix outside them.
        Assert.Equal(RedeemCode.GroupCount, Sample.Count(c => c == RedeemCode.Separator[0]));
    }

    /// <summary>
    /// Every spelling a human produces maps to one canonical string. This is the vector
    /// the server's implementation has to reproduce.
    /// </summary>
    [Theory]
    [InlineData("WCB-7K3MQ-9XQ2H-4TB6P-WN3DP")]
    [InlineData("wcb-7k3mq-9xq2h-4tb6p-wn3dp")]
    [InlineData("  WCB7K3MQ9XQ2H4TB6PWN3DP  ")]
    [InlineData("WCB 7K3MQ 9XQ2H 4TB6P WN3DP")]
    [InlineData("WCB_7K3MQ_9XQ2H_4TB6P_WN3DP")]
    [InlineData("wcb-7k3mq 9xq2h-4tb6p wn3dp")]
    [InlineData("WCB-7K3MQ-9XQ2H-4TB6P-WN3DP-")]
    public void EveryReasonableSpellingNormalisesToOneCanonicalString(string spelling)
    {
        Assert.True(RedeemCode.TryParse(spelling, out var code));
        Assert.Equal(Sample, code!.Canonical);
    }

    /// <summary>
    /// Crockford's three anti-confusion folds. These are the substitutions people
    /// actually make copying a code by hand, and accepting them is the difference
    /// between a code that works and a support ticket.
    /// <para>
    /// The fold is one-directional, which is the whole point of it: a code that reaches
    /// the canonical form never contains <c>O</c>, <c>I</c> or <c>L</c>, so a server
    /// that burns the canonical form can never disagree with a reader who typed the
    /// letter by mistake. The server's generator therefore has to emit digits and the
    /// remaining twenty-one letters — there is no code whose canonical form contains an
    /// O.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("WCB-7K3MQ-9XQ20-4TB6P-WN3DP", "WCB-7K3MQ-9XQ20-4TB6P-WN3DP")] // a digit is already canonical
    [InlineData("WCB-7K3MQ-9XQ20-4TB6P-WN3DP", "WCB-7K3MQ-9XQ2O-4TB6P-WN3DP")] // O typed as zero
    [InlineData("WCB-7K3MQ-9XQ21-4TB6P-WN3DP", "WCB-7K3MQ-9XQ2I-4TB6P-WN3DP")] // I typed as one
    [InlineData("WCB-7K3MQ-9XQ21-4TB6P-WN3DP", "WCB-7K3MQ-9XQ2L-4TB6P-WN3DP")] // L typed as one
    public void ConfusableLettersFoldToTheCharacterTheServerBurns(string canonical, string typed)
    {
        Assert.True(RedeemCode.TryParse(typed, out var code));
        Assert.Equal(canonical, code!.Canonical);
    }

    [Fact]
    public void UIsRejectedRatherThanFolded()
    {
        // Crockford's U is not in the alphabet at all. Folding it to V would turn a
        // typo into a different, valid-looking code instead of into a rejection, and
        // the code the user typed is not the code the server will burn.
        Assert.False(RedeemCode.TryParse("WCB-7K3MQ-9XQ2H-4TB6P-WN3UP", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("WCB-7K3MQ-9XQ2H-4TB6P")]            // three groups
    [InlineData("WCB-7K3MQ-9XQ2H-4TB6P-WN3DPX")]      // one character too many
    [InlineData("WCB-7K3M-9XQ2H-4TB6P-WN3")]         // one character too few
    [InlineData("XYZ-7K3MQ-9XQ2H-4TB6P-WN3DP")]       // wrong prefix
    [InlineData("WCB-7K3MQ-9XQ2H-4TB6P-WN3D$")]       // a character the alphabet lacks
    [InlineData("WCB-7K3M-9XQ2-H4TB-6PWN")]           // the doc's four-by-four example: 16 body characters
    public void TextThatIsNotACodeIsRejected(string? input) => Assert.False(RedeemCode.TryParse(input, out _));

    /// <summary>
    /// The digest is what lands on disk, so its inputs are pinned rather than merely
    /// "some hash of the canonical string".
    /// </summary>
    [Fact]
    public void TheDigestIsTakenOverTheCanonicalForm()
    {
        Assert.True(RedeemCode.TryParse("wcb 7k3mq 9xq2h 4tb6p wn3dp", out var code));
        var expected = Base64Url.Encode(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Sample)));

        Assert.Equal(expected, code!.Digest);
        Assert.Equal(RedeemCode.DigestOf(Sample), code.Digest);
    }

    /// <summary>
    /// The display formatter runs on every keystroke and on every paste, so it has to be
    /// a fixed point: formatting what it produced must not change it again, or the text
    /// box would rewrite itself under the caret.
    /// </summary>
    [Fact]
    public void FormattingForDisplayIsABoundedFixedPoint()
    {
        Assert.Equal(Sample, RedeemCode.FormatForDisplay(Sample));
        Assert.Equal(Sample, RedeemCode.FormatForDisplay("wcb7k3mq9xq2h4tb6pwn3dp"));
        Assert.Equal(Sample, RedeemCode.FormatForDisplay(Sample + "-MORE-TYPING"));
        Assert.Equal(string.Empty, RedeemCode.FormatForDisplay(""));
        Assert.Equal(string.Empty, RedeemCode.FormatForDisplay("$%^"));
        Assert.Equal("WCB", RedeemCode.FormatForDisplay("wcb"));
        Assert.Equal("WCB-7K3M", RedeemCode.FormatForDisplay("wcb7k3m"));

        foreach (var spelling in new[] { Sample, "wcb7k3mq9xq2h4tb6pwn3dp", "WCB 7K3MQ", "wcb7k3mq9xq2h4tb6pwn3dpmore" })
            Assert.Equal(RedeemCode.FormatForDisplay(spelling), RedeemCode.FormatForDisplay(RedeemCode.FormatForDisplay(spelling)));
    }

    [Fact]
    public void RequestIdsAreUnpaddedBase64UrlAndDistinct()
    {
        var first = RedeemCode.NewRequestId();
        var second = RedeemCode.NewRequestId();

        Assert.NotEqual(first, second);
        Assert.DoesNotContain('=', first);
        Assert.DoesNotContain('+', first);
        Assert.DoesNotContain('/', first);
        Assert.Equal(22, first.Length);
    }
}
