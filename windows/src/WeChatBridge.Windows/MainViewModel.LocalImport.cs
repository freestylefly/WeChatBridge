using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

public sealed partial class MainViewModel
{
    private bool _isImporting;
    public bool CanImport => !_isImporting;

    public Task ImportLocalFilesAsync(IReadOnlyList<string> files) =>
        ImportLocalAsync(() => LocalChatImporter.ImportFilesAsync(_paths, files));

    public Task ImportWorkBuddyArchiveAsync(string file) =>
        ImportLocalAsync(() => LocalChatImporter.ImportFilesAsync(_paths, [file]), archiveToObsidian: true);

    public Task ImportLocalTextAsync(string text, string? chatName = null) =>
        ImportLocalAsync(() => LocalChatImporter.ImportTextAsync(_paths, text, chatName));

    private async Task ImportLocalAsync(Func<Task<BatchCommitResult>> import, bool archiveToObsidian = false)
    {
        if (_isImporting) return;
        _isImporting = true;
        OnPropertyChanged(nameof(CanImport));
        try
        {
            var result = await Task.Run(import);
            var archived = false;
            if (archiveToObsidian && _settings.AutoArchiveWorkBuddyToObsidian
                && !string.IsNullOrWhiteSpace(_settings.ObsidianVaultPath))
            {
                try
                {
                    var batch = _reader.BatchAt(result.BatchDirectory)
                        ?? throw new InvalidOperationException(L10n.Text("导入记录无法读取，原始文件仍已保留。"));
                    await Task.Run(() => KnowledgeDelivery.Deliver(
                        batch.Items.Select(item => item.FullPath).ToList(),
                        _settings.ObsidianVaultPath!, _settings.ObsidianSubfolder,
                        batch.ChatName, sceneName: null));
                    Record(BatchOutcomeKind.Delivered, batch.Id, targetName: "Obsidian");
                    archived = true;
                }
                catch (Exception error)
                {
                    InboxLogger.Write(_paths, L10n.Text("WorkBuddy 归档到 Obsidian 失败"), error);
                    ShowToast(L10n.Format($"已导入，但保存到 Obsidian 失败：{error.Message}"), warning: true);
                }
            }
            Query = "";
            Reload();
            Navigate(AppTab.History);
            if (archived)
                ShowToast(L10n.Text("聊天记录已保存到 Obsidian 的「微信会话」栏目。"));
            else
                ShowToast(L10n.Text("已导入。点击记录中的「发给…」选择目标。"));
        }
        catch (Exception error)
        {
            InboxLogger.Write(_paths, "本地聊天导入失败", error);
            var message = error switch
            {
                System.Text.DecoderFallbackException => L10n.Text("无法读取文字编码，请将 TXT 保存为 UTF-8 后重试。"),
                System.IO.InvalidDataException => L10n.Text("ZIP 文件无效或已损坏。"),
                _ => error.Message,
            };
            ShowToast(L10n.Format($"导入失败：{message}"), warning: true);
        }
        finally
        {
            _isImporting = false;
            OnPropertyChanged(nameof(CanImport));
        }
    }
}
