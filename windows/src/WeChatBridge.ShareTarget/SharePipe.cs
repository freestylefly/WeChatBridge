using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using WeChatBridge.Windows.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

namespace WeChatBridge.ShareTarget;

/// <summary>One file the stub handed to the keeper — paths travel, StorageFile objects cannot.</summary>
internal sealed record StubShareItem(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("contentType")] string? ContentType);

/// <summary>What a stub sends: the share's file list, or an abort reason when validation failed.</summary>
internal sealed record StubSharePayload(
    [property: JsonPropertyName("items")] List<StubShareItem>? Items,
    [property: JsonPropertyName("abort")] string? Abort);

/// <summary>What the keeper sends back once the batch is committed (or failed).</summary>
internal sealed record StubShareReply(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error);

/// <summary>
/// The fast path for share activations once a keeper process is resident.
/// Windows still launches a fresh helper for every share — that cannot be
/// avoided without WindowsAppSDK — but the stub it launches never loads WPF:
/// it brokers the ShareOperation (ReportStarted/DataRetrieved/Completed)
/// while the warm keeper shows the picker and commits the batch.
/// </summary>
internal static class StubForwarder
{
    /// <summary>A resident keeper answers in well under this; longer means there is none.</summary>
    private const int ConnectTimeoutMs = 400;

    /// <summary>Reply deadline — the picker's own 90s budget plus slack.</summary>
    private const int ReplyTimeoutMs = 120_000;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Returns true when this activation was fully brokered through the
    /// keeper and the stub may exit; false when there is no keeper (or it
    /// wedged mid-share) and this process must handle the activation itself.
    /// </summary>
    public static bool TryForward(InboxPaths paths)
    {
        IActivatedEventArgs? activated;
        try
        {
            activated = AppInstance.GetActivatedEventArgs();
        }
        catch (Exception error)
        {
            InboxLogger.Write(paths, "读取激活参数失败。", error);
            return false;
        }

        if (activated is not { Kind: ActivationKind.ShareTarget }
            || activated is not IShareTargetActivatedEventArgs shareArgs)
            return false;

        var operation = shareArgs.ShareOperation;
        // Report before probing the pipe so WeChat's progress affordance is
        // live even while the (sub-second) keeper lookup is in flight.
        ShareReports.Started(operation);
        Trace(paths, "stub.args");

        using var pipe = new NamedPipeClientStream(
            ".", Program.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(ConnectTimeoutMs);
            Trace(paths, "stub.connected");
        }
        catch
        {
            return false; // no keeper listening — become it
        }

        try
        {
            var writer = new StreamWriter(pipe) { AutoFlush = true };
            var reader = new StreamReader(pipe);

            var abort = ValidateAndCollect(operation, out var items);
            if (abort is not null)
            {
                writer.WriteLine(JsonSerializer.Serialize(
                    new StubSharePayload(null, abort), JsonOptions));
                _ = reader.ReadLineAsync();
                ShareReports.Failed(operation, abort);
                return true;
            }

            ShareReports.DataRetrieved(operation);
            Trace(paths, $"stub.items count={items.Count}");
            writer.WriteLine(JsonSerializer.Serialize(
                new StubSharePayload(items, null), JsonOptions));
            Trace(paths, "stub.payload-sent");

            // The keeper may leave the picker open for the full 90s budget —
            // the reply can legitimately take that long. PipeStream has no
            // ReadTimeout without extra plumbing, so the wait is a task race.
            var replyTask = reader.ReadLineAsync();
            if (Task.WhenAny(replyTask, Task.Delay(ReplyTimeoutMs))
                    .GetAwaiter().GetResult() != replyTask)
                throw new TimeoutException("keeper 未在时限内应答。");
            var reply = replyTask.Result is null ? null
                : JsonSerializer.Deserialize<StubShareReply>(replyTask.Result, JsonOptions);
            Trace(paths, $"stub.reply ok={reply?.Ok}");
            if (reply is { Ok: true })
            {
                ShareReports.Completed(operation);
                InboxLogger.Write(paths, "已转交常驻 keeper 处理。");
            }
            else
            {
                ShareReports.Failed(operation, reply?.Error ?? "keeper 未确认。");
            }
            return true;
        }
        catch (Exception error)
        {
            // The keeper wedged or died mid-share. Clear it so the next
            // activation starts clean, then handle this one locally — the
            // ShareOperation is still valid in this process.
            InboxLogger.Write(paths, "keeper 转发失败，改为本进程处理。", error);
            KillOtherHelpers();
            return false;
        }
    }

    /// <summary>
    /// The stub-side copy of the share's validation: only files WeChat
    /// actually placed get a path worth forwarding.
    /// </summary>
    private static string? ValidateAndCollect(
        ShareOperation operation, out List<StubShareItem> items)
    {
        items = new List<StubShareItem>();
        if (!operation.Data.Contains(StandardDataFormats.StorageItems))
            return "PoC 只接收文件型分享，当前分享不包含 StorageItems。";

        var storageItems = operation.Data.GetStorageItemsAsync()
            .AsTask().GetAwaiter().GetResult();
        if (storageItems.Count == 0)
            return "分享中没有文件。";

        foreach (var item in storageItems)
        {
            if (item is not StorageFile file)
                return "分享项不是可读取的文件。";
            if (string.IsNullOrWhiteSpace(file.Path))
                return $"无法读取分享文件：{file.Name}";
            items.Add(new StubShareItem(file.Path, file.Name, file.ContentType));
        }
        return null;
    }

    /// <summary>
    /// ShareOperation reports are advisory and single-shot: a stub that fell
    /// back to local handling has already issued some of them, and the second
    /// call throws <see cref="InvalidOperationException"/>. Every report goes
    /// through here so a duplicate can never kill the share.
    /// </summary>
    internal static class ShareReports
    {
        public static void Started(ShareOperation operation) =>
            Try(operation.ReportStarted);

        public static void DataRetrieved(ShareOperation operation) =>
            Try(operation.ReportDataRetrieved);

        public static void Completed(ShareOperation operation) =>
            Try(operation.ReportCompleted);

        public static void Failed(ShareOperation operation, string? message) =>
            Try(() => operation.ReportError(message ?? "分享处理失败。"));

        private static void Try(Action report)
        {
            try { report(); } catch { }
        }
    }

    /// <summary>Stage-boundary marks in windows.log, same series as the keeper's.</summary>
    private static void Trace(InboxPaths paths, string stage) =>
        InboxLogger.Write(paths, $"[trace] {stage}");

    /// <summary>Keeper replacement after a mid-share wedge: sweep every other helper so the pipe name frees for the process that takes over.</summary>
    private static void KillOtherHelpers()
    {
        try
        {
            foreach (var other in Process.GetProcessesByName(
                         Path.GetFileNameWithoutExtension(Environment.ProcessPath)))
            {
                if (other.Id != Environment.ProcessId)
                {
                    try { other.Kill(); } catch { }
                }
            }
        }
        catch { }
    }
}
