using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core;

public sealed record InboxSourceFile(
    string SourcePath,
    string DisplayName,
    string? ContentType,
    int ItemIndex,
    int AttachmentIndex);

public sealed record ManifestItem(
    Guid Id,
    string DisplayName,
    string RelativePath,
    string? ContentType,
    long ByteCount,
    int ItemIndex,
    int AttachmentIndex);

public sealed record BatchManifest(
    int SchemaVersion,
    Guid BatchId,
    DateTimeOffset CreatedAt,
    string Action,
    IReadOnlyList<ManifestItem> Items)
{
    public const int CurrentSchemaVersion = 1;

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // The type-specific converters must precede JsonStringEnumConverter: the
        // Converters collection wins over [JsonConverter] type attributes, and the
        // first matching converter in the collection claims the enum.
        Converters =
        {
            new UtcIso8601Converter(),
            new ShareActionConverter(),
            new BatchOutcomeKindConverter(),
            new AgentIdConverter(),
            new CollectionStatusConverter(),
            new JsonStringEnumConverter()
        }
    };
}

/// <summary>
/// macOS writes manifest timestamps with <c>JSONEncoder.dateEncodingStrategy = .iso8601</c>,
/// which produces <c>2026-09-23T09:04:42Z</c>. A bare <see cref="DateTimeOffset"/> would emit
/// <c>+00:00</c> instead, so a manifest written by one platform could not be read back by the
/// other. Normalising to the shared <c>Z</c> form keeps both implementations interchangeable
/// and satisfies the "ISO-8601 UTC" contract in the migration plan.
/// </summary>
public sealed class UtcIso8601Converter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString()
            ?? throw new JsonException("Manifest timestamp was null.");
        return DateTimeOffset.Parse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.RoundtripKind);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}

public sealed record BatchCommitResult(Guid BatchId, string BatchDirectory, BatchManifest Manifest);

/// <summary>
/// A share that has passed validation and whose payload is fully staged — the
/// half-commit the picker flow needs: the helper asks its question while the
/// files are being staged, then finishes in the second half of
/// <see cref="InboxWriter"/> with the intent (or a settled outcome) the answer
/// produced. Nothing is visible to the app until the rename at the end.
/// </summary>
public sealed record StagedShare(
    Guid BatchId,
    string StagingDirectory,
    string ReadyDirectory,
    BatchManifest Manifest);

public sealed class InboxPaths
{
    public InboxPaths(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatBridge",
            "Inbox");
    }

    public string Root { get; }
    public string Staging => Path.Combine(Root, "Staging");
    public string Ready => Path.Combine(Root, "Ready");
    public string Failed => Path.Combine(Root, "Failed");
    public string Logs => Path.Combine(Root, "Logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Ready);
        Directory.CreateDirectory(Failed);
        Directory.CreateDirectory(Logs);
    }

    /// <summary>
    /// Removes staging trees that no live share can still be filling. Mirrors
    /// <c>Inbox.pruneStaging</c> on macOS, including its choice to measure age from the
    /// newest timestamp anywhere in the tree: a multi-gigabyte copy only touches the file
    /// being written, so aging a batch by its directory timestamp would delete a share
    /// that is still in progress.
    /// </summary>
    public int PruneStaging(TimeSpan? olderThan = null, DateTimeOffset? now = null)
    {
        var age = olderThan ?? TimeSpan.FromMinutes(30);
        var reference = now ?? DateTimeOffset.UtcNow;
        if (!Directory.Exists(Staging))
            return 0;

        var removed = 0;
        try
        {
            foreach (var candidate in Directory.EnumerateFileSystemEntries(Staging))
            {
                var newest = NewestWriteTime(candidate);
                if (newest is null || reference - newest.Value <= age)
                    continue;
                try
                {
                    if (Directory.Exists(candidate))
                        Directory.Delete(candidate, recursive: true);
                    else
                        File.Delete(candidate);
                    removed++;
                }
                catch (IOException)
                {
                    // Another share may still hold a handle; the next pass retries.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same: pruning is a backstop and must never fail a share.
                }
            }
        }
        catch (IOException)
        {
            return removed;
        }
        catch (UnauthorizedAccessException)
        {
            return removed;
        }
        return removed;
    }

    private static DateTimeOffset? NewestWriteTime(string path)
    {
        DateTimeOffset? newest = null;
        void Consider(string candidate)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(candidate);
                if (stamp == DateTime.MinValue)
                    return;
                var value = new DateTimeOffset(stamp, TimeSpan.Zero);
                if (newest is null || value > newest.Value)
                    newest = value;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        Consider(path);
        if (Directory.Exists(path))
        {
            try
            {
                foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                    Consider(child);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return newest;
    }
}

/// <summary>
/// Size guards applied while staging a share. The defaults are the PoC contract, and they
/// are injectable so the guards can be exercised without writing gigabyte fixtures.
/// </summary>
public sealed record InboxLimits(long MaxFileBytes, long MaxBatchBytes)
{
    public static InboxLimits Default { get; } = new(InboxWriter.MaxFileBytes, InboxWriter.MaxBatchBytes);
}

public static class InboxWriter
{
    public const long MaxFileBytes = 1_073_741_824;
    public const long MaxBatchBytes = 1_073_741_824;

    /// <param name="action">
    /// Which share-menu entry produced this batch. Written into manifest.json so the
    /// history can say what was asked for even after intent.json is consumed.
    /// </param>
    /// <param name="intent">
    /// The one-shot forward request, written as intent.json inside the staging tree so
    /// it commits atomically with the batch — a share that is durable but whose intent
    /// was lost must not exist. Only meaningful when <see cref="ShareActions.NeedsIntent"/>.
    /// </param>
    public static async Task<BatchCommitResult> CommitAsync(
        InboxPaths paths,
        IReadOnlyList<InboxSourceFile> sources,
        CancellationToken cancellationToken = default,
        InboxLimits? limits = null,
        ShareAction action = ShareAction.Clipboard,
        BatchIntent? intent = null)
    {
        var staged = await StageAsync(paths, sources, cancellationToken, limits, action);
        return await CommitAsync(paths, staged, intent, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The first half of a commit: validation, payload copy and manifest, all
    /// still inside <c>Staging/</c>. A caller that has a question left to ask —
    /// the helper's entry picker — stages here, asks in parallel, then hands the
    /// answer to <see cref="CommitAsync(InboxPaths, StagedShare, BatchIntent?, BatchOutcome?, CancellationToken)"/>.
    /// </summary>
    public static async Task<StagedShare> StageAsync(
        InboxPaths paths,
        IReadOnlyList<InboxSourceFile> sources,
        CancellationToken cancellationToken = default,
        InboxLimits? limits = null,
        ShareAction action = ShareAction.Clipboard)
    {
        if (sources.Count == 0)
            throw new InboxValidationException("分享中没有可处理的文件。");

        var effectiveLimits = limits ?? InboxLimits.Default;
        paths.EnsureCreated();
        var batchId = Guid.NewGuid();
        var staging = Path.Combine(paths.Staging, $"{batchId:N}.staging");
        var ready = Path.Combine(paths.Ready, batchId.ToString("D"));
        var filesDirectory = Path.Combine(staging, "files");
        var committedItems = new List<ManifestItem>(sources.Count);
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        try
        {
            Directory.CreateDirectory(filesDirectory);

            for (var index = 0; index < sources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                ValidateSource(source);
                var sourceInfo = new FileInfo(source.SourcePath);
                if (sourceInfo.Length > effectiveLimits.MaxFileBytes)
                    throw new InboxValidationException($"文件过大：{source.DisplayName}");

                totalBytes = checked(totalBytes + sourceInfo.Length);
                if (totalBytes > effectiveLimits.MaxBatchBytes)
                    throw new InboxValidationException("分享批次过大。");

                var safeName = MakeUniqueFileName(SanitizeFileName(source.DisplayName), usedFileNames);
                var relativePath = Path.Combine("files", safeName).Replace('\\', '/');
                var destination = Path.Combine(filesDirectory, safeName);

                await using (var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                {
                    await input.CopyToAsync(output, cancellationToken);
                }

                committedItems.Add(new ManifestItem(
                    Guid.NewGuid(),
                    safeName,
                    relativePath,
                    source.ContentType,
                    sourceInfo.Length,
                    source.ItemIndex,
                    source.AttachmentIndex));
            }

            var manifest = new BatchManifest(
                BatchManifest.CurrentSchemaVersion,
                batchId,
                DateTimeOffset.UtcNow,
                action.RawValue(),
                committedItems);
            var manifestPath = Path.Combine(staging, "manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(manifest, BatchManifest.JsonOptions),
                cancellationToken);
            return new StagedShare(batchId, staging, ready, manifest);
        }
        catch (Exception error)
        {
            TryDeleteDirectory(staging);
            TryWriteFailure(paths, batchId, error);
            throw;
        }
    }

    /// <summary>
    /// The second half: intent and — when the share is already settled — an
    /// initial state.json written inside the staged tree, then the rename that
    /// publishes the batch. An <paramref name="initialOutcome"/> (the helper's
    /// picker was cancelled) rides the same atomic commit, so the app never
    /// announces or re-asks a share that already has an answer.
    /// </summary>
    public static async Task<BatchCommitResult> CommitAsync(
        InboxPaths paths,
        StagedShare staged,
        BatchIntent? intent,
        BatchOutcome? initialOutcome = null,
        CancellationToken cancellationToken = default,
        string? chatName = null)
    {
        try
        {
            if (intent is not null)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(staged.StagingDirectory, BatchIntent.FileName),
                    JsonSerializer.Serialize(intent, BatchManifest.JsonOptions),
                    cancellationToken);
            }

            if (initialOutcome is not null || chatName is not null)
            {
                var state = BatchState.Initial(staged.Manifest, intent?.Action, intent?.TargetDisplayName);
                if (initialOutcome is not null) state = state.WithOutcome(initialOutcome);
                state = state with { ChatName = chatName,
                    UnannouncedShare = initialOutcome is null && state.Action != ShareAction.Clipboard };
                await File.WriteAllTextAsync(
                    Path.Combine(staged.StagingDirectory, BatchState.FileName),
                    JsonSerializer.Serialize(state, BatchManifest.JsonOptions),
                    cancellationToken);
            }

            Directory.Move(staged.StagingDirectory, staged.ReadyDirectory);
            return new BatchCommitResult(staged.BatchId, staged.ReadyDirectory, staged.Manifest);
        }
        catch (Exception error)
        {
            TryDeleteDirectory(staged.StagingDirectory);
            TryWriteFailure(paths, staged.BatchId, error);
            throw;
        }
    }

    private static void ValidateSource(InboxSourceFile source)
    {
        if (string.IsNullOrWhiteSpace(source.SourcePath) || !File.Exists(source.SourcePath))
            throw new InboxValidationException($"找不到分享文件：{source.DisplayName}");
        if (Directory.Exists(source.SourcePath))
            throw new InboxValidationException($"分享项不是文件：{source.DisplayName}");
        var attributes = File.GetAttributes(source.SourcePath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InboxValidationException($"不接受符号链接或重解析点：{source.DisplayName}");
        if (!source.DisplayName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InboxValidationException($"PoC 只接收 ZIP：{source.DisplayName}");
    }

    private static string SanitizeFileName(string name)
    {
        var leaf = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(leaf))
            throw new InboxValidationException("分享文件名为空。");
        if (leaf.Any(char.IsControl) || leaf is "." or "..")
            throw new InboxValidationException("分享文件名不安全。");

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(leaf.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
            throw new InboxValidationException("分享文件名不安全。");
        return sanitized;
    }

    private static string MakeUniqueFileName(string name, ISet<string> usedNames)
    {
        if (usedNames.Add(name))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 2; ; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup must never hide the original share failure.
        }
    }

    private static void TryWriteFailure(InboxPaths paths, Guid batchId, Exception error)
    {
        try
        {
            paths.EnsureCreated();
            var failurePath = Path.Combine(paths.Failed, $"{batchId:D}.json");
            var payload = new
            {
                schemaVersion = BatchManifest.CurrentSchemaVersion,
                batchId,
                createdAt = DateTimeOffset.UtcNow,
                message = error.Message
            };
            File.WriteAllText(failurePath, JsonSerializer.Serialize(payload, BatchManifest.JsonOptions));
        }
        catch
        {
            // Diagnostics are best effort; the caller still receives the original error.
        }
    }
}

public sealed class InboxValidationException(string message) : Exception(message);

public static class InboxLogger
{
    private static readonly object Gate = new();

    public static void Write(InboxPaths paths, string message, Exception? error = null)
    {
        try
        {
            paths.EnsureCreated();
            var line = $"{DateTimeOffset.UtcNow:O} {message}" + (error is null ? string.Empty : $" | {error}");
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(paths.Logs, "windows.log"), line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must not break the share path.
        }
    }
}
