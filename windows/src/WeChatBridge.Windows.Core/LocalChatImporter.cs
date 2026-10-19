using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace WeChatBridge.Windows.Core;

/// <summary>User-selected files enter the same inbox without package activation or clipboard work.</summary>
public static class LocalChatImporter
{
    public const int MaxTextBytes = 16 * 1024 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static Task<BatchCommitResult> ImportFilesAsync(InboxPaths paths, IReadOnlyList<string> files,
        CancellationToken cancellationToken = default) =>
        ImportAsync(paths, files, null, null, cancellationToken);

    public static Task<BatchCommitResult> ImportTextAsync(InboxPaths paths, string text, string? chatName = null,
        CancellationToken cancellationToken = default) =>
        ImportAsync(paths, [], text, chatName, cancellationToken);

    private static async Task<BatchCommitResult> ImportAsync(InboxPaths paths, IReadOnlyList<string> files,
        string? text, string? chatName, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "WeChatBridge-LocalImport-" + Guid.NewGuid().ToString("N"));
        StagedShare? staged = null;
        try
        {
            Directory.CreateDirectory(temporary);
            var sources = new List<InboxSourceFile>();
            if (text is not null)
            {
                var archive = await WriteTextArchive(temporary, text, cancellationToken);
                sources.Add(new(archive, "粘贴的聊天记录.zip", "application/zip", 0, 0));
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = new FileInfo(file);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InboxValidationException(L10n.Text("请选择真实存在的 ZIP 或 TXT 文件。"));
                if (info.Length > InboxWriter.MaxFileBytes)
                    throw new InboxValidationException(L10n.Text("单个文件不能超过 1 GiB。"));
                var extension = info.Extension.ToLowerInvariant();
                var source = info.FullName;
                var displayName = info.Name;
                if (extension == ".txt")
                {
                    if (info.Length > MaxTextBytes)
                        throw new InboxValidationException(L10n.Text("聊天文字不能超过 16 MiB。"));
                    using var input = new StreamReader(source, Utf8, detectEncodingFromByteOrderMarks: true);
                    var body = await input.ReadToEndAsync(cancellationToken);
                    var directory = Path.Combine(temporary, sources.Count.ToString());
                    Directory.CreateDirectory(directory);
                    source = await WriteTextArchive(directory, body, cancellationToken);
                    displayName = Path.GetFileNameWithoutExtension(info.Name) + ".zip";
                }
                else if (extension == ".zip")
                {
                    // Validate the container without extracting user-supplied paths.
                    using var archive = ZipFile.OpenRead(source);
                    _ = archive.Entries.Count;
                }
                else
                    throw new InboxValidationException(L10n.Text("只支持 ZIP 或 TXT 文件。"));
                sources.Add(new(source, displayName, "application/zip", sources.Count, 0));
            }

            staged = await InboxWriter.StageAsync(paths, sources, cancellationToken, action: ShareAction.Hub);
            // An already acknowledged state has no one-shot intent: reload/relaunch must
            // never forward a local import or claim the clipboard was already written.
            var state = BatchState.Initial(staged.Manifest) with
            {
                Outcome = null,
                UnannouncedShare = false,
                ChatName = string.IsNullOrWhiteSpace(chatName) ? null : chatName.Trim(),
            };
            await File.WriteAllTextAsync(Path.Combine(staged.StagingDirectory, BatchState.FileName),
                JsonSerializer.Serialize(state, BatchManifest.JsonOptions), cancellationToken);
            return await InboxWriter.CommitAsync(paths, staged, null, cancellationToken: cancellationToken);
        }
        finally
        {
            // Both paths are generated here, never supplied by the caller. A committed
            // batch has already moved out of Staging and is deliberately kept.
            if (staged is not null) Cleanup(staged.StagingDirectory);
            Cleanup(temporary);
        }
    }

    private static async Task<string> WriteTextArchive(string directory, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InboxValidationException(L10n.Text("请先输入或粘贴聊天文字。"));
        if (Utf8.GetByteCount(text) > MaxTextBytes)
            throw new InboxValidationException(L10n.Text("聊天文字不能超过 16 MiB。"));
        var path = Path.Combine(directory, "text.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        await using var writer = new StreamWriter(archive.CreateEntry("聊天记录.txt").Open(), Utf8);
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
        return path;
    }

    private static void Cleanup(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
