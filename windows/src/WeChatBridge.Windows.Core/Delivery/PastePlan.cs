namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>
/// One thing the clipboard holds for one Ctrl+V.
/// Ported from <c>PastePayload</c> in PastePlan.swift. macOS carried <c>[URL]</c>;
/// Windows carries committed inbox paths, because a Ready batch is already on disk
/// and every paste destination reads plain file paths.
/// </summary>
public abstract record PastePayload
{
    private PastePayload() { }

    /// <summary>A file-drop list (CF_HDROP) — real files, as Explorer would copy them.</summary>
    public sealed record Files(IReadOnlyList<string> Paths) : PastePayload;

    /// <summary>Plain Unicode text — a scene prompt, or a quoted-paths line for a terminal.</summary>
    public sealed record Text(string Value) : PastePayload;
}

/// <summary>
/// What a forward pastes, in the order it pastes it.
///
/// A prompt and a file cannot share one Ctrl+V: a clipboard that offers both
/// text and a file list makes the target pick, and a chat app picks the files.
/// So a prompt before the files is two pastes. A terminal takes only text, so
/// there the prompt and the quoted paths become one line.
///
/// The prompt always goes first: an instruction read after its attachments is
/// an instruction the agent has already started guessing at.
/// </summary>
public static class PastePlan
{
    public static IReadOnlyList<PastePayload> Make(IReadOnlyList<string> paths, bool pathOnly, string? prompt)
    {
        var trimmed = prompt?.Trim();
        if (!pathOnly)
        {
            if (string.IsNullOrEmpty(trimmed))
                return [new PastePayload.Files(paths)];
            return [new PastePayload.Text(trimmed), new PastePayload.Files(paths)];
        }

        var line = WindowsClipboard.ShellLine(paths);
        if (string.IsNullOrEmpty(trimmed))
            return [new PastePayload.Text(line)];

        // A newline pasted into a shell is Return: it would run whatever was
        // typed so far, with the paths still to come. One line, always.
        // Split(char, int, StringSplitOptions) would silently bind '\n' as a
        // count — pass the separators as an explicit array.
        var flat = string.Join(' ', trimmed
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0));
        return [new PastePayload.Text($"{flat} {line}")];
    }

    /// <summary>
    /// What a manual Ctrl+V should produce when the automated paste is refused:
    /// the files, or the one line a terminal would have received — never the bare prompt.
    /// </summary>
    public static PastePayload? ManualPayload(IReadOnlyList<PastePayload> plan) =>
        plan.OfType<PastePayload.Files>().Cast<PastePayload>().FirstOrDefault() ?? plan.FirstOrDefault();
}
