using System.Text;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Writes a WeChat archive into an Obsidian vault as Markdown plus a durable
/// copy of the original ZIP. Ported from <c>KnowledgeDelivery.swift</c>.
/// Keeping the archive makes a parsing change or a future converter able to
/// rebuild the note without asking WeChat again.
/// </summary>
public static class KnowledgeDelivery
{
    public enum FailureKind
    {
        NotConfigured,
        UnreadableArchive,
    }

    public sealed class FailureException : Exception
    {
        public FailureException(FailureKind kind, Exception? inner = null)
            : base(Describe(kind), inner) => Kind = kind;

        public FailureKind Kind { get; }

        private static string Describe(FailureKind kind) => kind switch
        {
            FailureKind.NotConfigured => L10n.Text("还没有选择 Obsidian 知识库文件夹。"),
            FailureKind.UnreadableArchive => L10n.Text("微信导出的文件无法读取，原始文件已保留。"),
            _ => L10n.Text("微信导出的文件无法读取，原始文件已保留。"),
        };
    }

    /// <summary>
    /// Delivers using the vault configured in
    /// <c>%LOCALAPPDATA%\WeChatBridge\Config\obsidian.json</c> (or
    /// <paramref name="configDirectory"/> in tests). No vaultPath means the
    /// user never picked a vault — <see cref="FailureKind.NotConfigured"/>.
    /// </summary>
    public static List<string> Deliver(
        IReadOnlyList<string> paths,
        string? chatName,
        string? sceneName,
        string? configDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var settings = new ObsidianSettingsStore(configDirectory).Load();
        if (string.IsNullOrEmpty(settings.VaultPath))
            throw new FailureException(FailureKind.NotConfigured);
        return Deliver(paths, settings.VaultPath, settings.Subfolder, chatName, sceneName, cancellationToken);
    }

    public static List<string> Deliver(
        IReadOnlyList<string> paths,
        string vaultPath,
        string subfolder,
        string? chatName,
        string? sceneName,
        CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0)
            throw new FailureException(FailureKind.UnreadableArchive);
        FolderDelivery.ValidateFolder(vaultPath);

        var folderName = DisplayName.SubfolderPath(subfolder);
        if (folderName.Length == 0) folderName = "微信流";
        var root = Path.Combine(vaultPath, folderName);
        var attachments = Path.Combine(root, "附件");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(attachments);

        var written = new List<string>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = File.ReadAllBytes(path);
            WeChatNativeArchive.Transcript? transcript;
            try
            {
                transcript = WeChatNativeArchive.GetTranscript(data, cancellationToken);
            }
            catch
            {
                // macOS `try?`: an unreadable transcript never blocks delivery —
                // the note still gets written and points at the intact archive.
                transcript = null;
            }
            var archive = FolderDelivery.Save([path], attachments, cancellationToken).FirstOrDefault()
                ?? throw new FailureException(FailureKind.UnreadableArchive);
            var media = ExtractMedia(data, transcript, attachments);

            var title = ObsidianNote.Title(
                chatName,
                transcript,
                archiveName: Path.GetFileName(path));
            var markdown = ObsidianNote.Render(
                title,
                chatName,
                sceneName,
                createdAt: DateTimeOffset.Now,
                transcript,
                archiveName: Path.GetFileName(archive),
                attachments: media);
            // One note per conversation: re-forwarding a chat re-exports the
            // same messages, so a same-named note merges rather than spawning
            // 「… 2.md」 copies of them.
            var noteName = DisplayName.Sanitize(title) + ".md";
            var preferred = Path.Combine(root, noteName);
            if (transcript is not null && File.Exists(preferred))
            {
                var merge = ObsidianNote.TryMerge(
                    File.ReadAllText(preferred), transcript, media,
                    Path.GetFileName(archive), chatName, sceneName,
                    DateTimeOffset.Now, out var mergedMarkdown);
                if (merge == ObsidianNote.MergeOutcome.Merged)
                    WriteAtomic(preferred, mergedMarkdown!);
                if (merge != ObsidianNote.MergeOutcome.NotApplicable)
                {
                    written.Add(preferred);
                    continue;
                }
            }
            var note = UniquePath(root, noteName);
            WriteAtomic(note, markdown);
            written.Add(note);
        }
        return written;
    }

    /// <summary>Write beside the note then move: a reader never sees half a file.</summary>
    private static void WriteAtomic(string note, string markdown)
    {
        var staging = note + ".tmp";
        File.WriteAllText(staging, markdown, new UTF8Encoding(false));
        File.Move(staging, note, overwrite: true);
    }

    /// <summary>
    /// Media extracted next to the archive copy, so the note can embed
    /// referenced attachments. Any failure simply yields no media — the ZIP is
    /// already preserved whole.
    /// </summary>
    private static Dictionary<string, string> ExtractMedia(
        byte[] data,
        WeChatNativeArchive.Transcript? transcript,
        string attachments)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (transcript is null)
            return result;
        var staging = Path.Combine(
            Path.GetTempPath(),
            "wechatbridge-media-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var sources = WeChatNativeArchive.Extract(data, staging)
                .Where(name => name != transcript.Path)
                .Select(name => Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)))
                .ToList();
            var saved = FolderDelivery.Save(sources, attachments);
            for (var index = 0; index < sources.Count && index < saved.Count; index++)
                result[Path.GetFileName(sources[index])] = Path.GetFileName(saved[index]);
            return result;
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // A leftover temp folder is debris, not a delivery failure.
            }
        }
    }

    private static string UniquePath(string folder, string name)
    {
        var extension = Path.GetExtension(name);
        var stem = name[..^extension.Length];
        var number = 1;
        while (true)
        {
            var suffix = number == 1 ? string.Empty : $" {number}";
            var candidate = Path.Combine(folder, $"{stem}{suffix}{extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
            number++;
        }
    }
}
