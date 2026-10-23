namespace WeChatBridge.Windows.Core;

/// <summary>
/// The two ways reading a WeChat export can fail, ported from
/// <c>WeChatReadError</c> in WeChatForward.swift. Kept as a value so callers can
/// branch on <see cref="WeChatReadException.Error"/> the way macOS branches on
/// the error case.
/// </summary>
public enum WeChatReadError
{
    /// <summary>The export is empty or cannot be read to completion.</summary>
    InvalidTranscript,
    /// <summary>Follow-up context for a share could no longer be located.</summary>
    TranscriptMismatch,
}

/// <summary>
/// Thrown when a WeChat-exported archive fails the closed validation in
/// <see cref="WeChatNativeArchive"/>. The original file is always preserved by
/// the inbox, so a read failure loses nothing but this delivery attempt.
/// </summary>
public sealed class WeChatReadException : Exception
{
    public WeChatReadException(WeChatReadError error)
        : base(Describe(error)) => Error = error;

    public WeChatReadException(WeChatReadError error, Exception inner)
        : base(Describe(error), inner) => Error = error;

    public WeChatReadError Error { get; }

    private static string Describe(WeChatReadError error) => error switch
    {
        WeChatReadError.InvalidTranscript => "微信导出的文件为空或无法完整读取，文件已保留。",
        WeChatReadError.TranscriptMismatch => "未能继续定位微信消息，已收到的文件会保留在记录中。",
        _ => "微信导出的文件为空或无法完整读取，文件已保留。",
    };
}
