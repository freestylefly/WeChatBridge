using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Panes;
using WeChatBridge.Windows.Services;
using WeChatBridge.Windows.Onboarding;
using WeChatBridge.Windows.Tests;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace WeChatBridge.Windows.AppTests;

public sealed class UiSmokeTests
{
    [Fact]
    public void LocalizedSettingsAndCollectionRenderWithoutStartingTheApp()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var originalLanguage = L10n.Language;
            try
            {
                using var fixture = new TempInbox();
                var reader = new InboxReader(fixture.Paths, InboxReader.Removal.Delete);
                var config = Path.Combine(fixture.Root, "config");
                using var scenes = new SceneService(config);
                var model = new MainViewModel(fixture.Paths, config, false, scenes: scenes, copyPayload: _ => { });
                var source = fixture.WriteSource("sample.zip", "");
                using (var zip = new System.IO.Compression.ZipArchive(File.OpenWrite(source), System.IO.Compression.ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("聊天记录.txt").Open()))
                    writer.Write("·小林\n2026年10月3日 17:46\n这一批先确认首页布局，附件里的图片也一起保留。\n\n·阿明\n2026年10月3日 17:47\n收到，下一批补上交互说明。\n\n·小林\n2026年10月3日 17:48\n收齐后统一整理成评审资料，不用单独发送。\n");
                var batch = InboxWriter.CommitAsync(fixture.Paths,
                    [new InboxSourceFile(source, "sample.zip", "application/zip", 0, 0)]).GetAwaiter().GetResult();
                reader.RecordChatName(batch.BatchId, "示例群聊");
                model.Reload();
                var collectionId = model.Collections.Append(model.Batches.Single(), null);
                var secondBatch = InboxWriter.CommitAsync(fixture.Paths,
                    [new InboxSourceFile(source, "next-part.zip", "application/zip", 0, 0)]).GetAwaiter().GetResult();
                reader.RecordChatName(secondBatch.BatchId, "示例群聊");
                model.Collections.Append(reader.LoadBatches().Single(b => b.Id == secondBatch.BatchId), null);
                model.Reload();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(app.Dispatcher));
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/WeChatBridge.Windows;component/Themes/AppTheme.xaml"),
                });
                var previewDirectory = Environment.GetEnvironmentVariable("WECHATBRIDGE_UI_PREVIEW");
                foreach (var language in new[] { "zh-Hans", "en" })
                {
                    L10n.Language = language;
                    foreach (var (name, pane) in new (string, FrameworkElement)[]
                    {
                        ("general", new GeneralPane()), ("entries", new EntriesPane()),
                        ("history", new HistoryPane()), ("scenes", new ScenesPane()), ("skills", new SkillsPane()),
                        ("about", new AboutPane()),
                    })
                    {
                        pane.DataContext = model;
                        Render(pane, name, language, previewDirectory);
                        if (pane is HistoryPane)
                        {
                            var renameButton = Descendants<Button>(pane).Single(b => b.ToolTip?.ToString() == L10n.Text("修改名称"));
                            renameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            var panel = Descendants<StackPanel>(pane).Single(p => p.Name == "CollectionRenamePanel");
                            Assert.Equal(Visibility.Visible, panel.Visibility);
                            var editor = Descendants<TextBox>(panel).Single();
                            editor.Text = "不保存";
                            Descendants<Button>(panel).Single(b => b.Content?.ToString() == L10n.Text("取消"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Assert.Equal(Visibility.Collapsed, panel.Visibility);
                            Assert.NotEqual("不保存", model.Collections.Ledger.Editable(collectionId).Name);
                            renameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            editor.Text = "示例收集";
                            Descendants<Button>(panel).Single(b => b.Content?.ToString() == L10n.Text("保存"))
                                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Assert.Equal("示例收集", model.Collections.Ledger.Editable(collectionId).Name);
                            Assert.Equal("示例收集", model.CollectionRows.Single(r => r.Id == collectionId).Title);
                        }
                    }
                    var wizardModel = new MainViewModel(fixture.Paths, Path.Combine(config, "wizard-" + language),
                        false);
                    var wizardStore = new OnboardingStateStore(Path.Combine(config, "wizard-state-" + language));
                    var wizard = new OnboardingWindow(wizardModel, wizardStore, () => Task.FromResult(false));
                    wizard.Show();
                    WaitFor(() => ((TextBlock)wizard.FindName("RegistrationTitle")).Text == L10n.Text("未检测到 Windows 分享入口"));
                    Assert.False(wizard.AllowsTransparency);
                    Assert.True(wizard.UseLayoutRounding);
                    Assert.Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(wizard));
                    Assert.Contains("Microsoft YaHei UI", wizard.FontFamily.Source);
                    Assert.Null(((Border)wizard.Content).Effect);
                    var next = (Button)wizard.FindName("NextButton");
                    for (var step = 0; step < 4; step++)
                    {
                        Assert.Equal(step, wizard.CurrentStep);
                        if (previewDirectory is not null)
                            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                            {
                                wizard.UpdateLayout();
                                var bitmap = new RenderTargetBitmap((int)(wizard.ActualWidth * scale), (int)(wizard.ActualHeight * scale),
                                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                                bitmap.Render(wizard);
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                using var output = File.Create(Path.Combine(previewDirectory, $"onboarding-{step}-{language}-{scale * 100:0}.png"));
                                encoder.Save(output);
                            }
                        if (step == 1)
                        {
                            var rows = ((ItemsControl)wizard.FindName("EntriesList")).Items.Cast<OnboardingWindow.WizardEntry>().ToArray();
                            Assert.All(rows, row => Assert.True(row.Available));
                            var clipboard = rows.Single(row => row.Action == ShareAction.Clipboard);
                            clipboard.Enabled = false;
                            Assert.False(wizardModel.IsEntryEnabled(ShareAction.Clipboard));
                            clipboard.Enabled = true;
                            Assert.True(wizardModel.IsEntryEnabled(ShareAction.Clipboard));
                        }
                        next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    Assert.True(wizardStore.Load().Completed);
                    Assert.All(((ItemsControl)wizard.FindName("EntriesList")).Items.Cast<OnboardingWindow.WizardEntry>(),
                        row => Assert.Equal(row.Enabled, wizardModel.IsEntryEnabled(row.Action)));
                    wizardStore.Save(new OnboardingState { Step = 2 });
                    var resumed = new OnboardingWindow(wizardModel, wizardStore, () => Task.FromResult(true));
                    resumed.Show();
                    Assert.Equal(2, resumed.CurrentStep);
                    WaitFor(() => ((TextBlock)resumed.FindName("RegistrationTitle")).Text == L10n.Text("Windows 分享入口已注册"));
                    resumed.Close();
                    Assert.False(wizardStore.Load().Completed);
                    Assert.Equal(2, wizardStore.Load().Step);
                    wizardModel.DisposeServices();
                    var window = new CollectionWindow(model, collectionId);
                    window.Show();
                    WaitFor(() => ((TextBlock)window.FindName("LastRange")).Text.Length > 0);
                    Assert.True(window.Topmost);
                    Assert.False(window.ShowActivated);
                    Assert.False(window.ShowInTaskbar);
                    Assert.Equal(WindowStyle.None, window.WindowStyle);
                    Assert.Null(window.FindName("ManagementPanel"));
                    var originalIds = model.Collections.Ledger.Editable(collectionId).BatchIDs.ToList();
                    using (var receipt = new CollectionImportSession(fixture.Paths))
                    {
                        model.RefreshImportProgress();
                        Assert.Equal(L10n.Format($"正在保存第 {originalIds.Count + 1} 批…"), ((TextBlock)window.FindName("Hint")).Text);
                        Assert.Equal(originalIds, model.Collections.Ledger.Editable(collectionId).BatchIDs);
                        receipt.Finish(true);
                    }
                    model.RefreshImportProgress();
                    WaitFor(() => ((TextBlock)window.FindName("SummaryText")).Text.Contains(L10n.Format($"约 {6} 条")));
                    var content = (FrameworkElement)window.Content;
                    content.Resources.MergedDictionaries.Add(window.Resources);
                    window.Content = null;
                    var compactHeight = Render(content, "collection", language, previewDirectory, width: 400);
                    Assert.InRange(compactHeight, 180, 380);
                    Render(content, "collection-320", language, previewDirectory, width: 320);
                    window.Content = content;
                    ((Button)window.FindName("ManageButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var details = Assert.IsType<CollectionDetailsWindow>(window.DetailsWindow);
                    WaitFor(() => ((CollectionDetailsWindow.CollectionBatchRow?)((FrameworkElement)details.FindName("LatestReference")).DataContext)?.Metadata is not null);
                    Assert.True(details.IsVisible);
                    Assert.Equal(WindowStyle.None, details.WindowStyle);
                    Assert.InRange(RenderDetached(window, "collection-inspecting", language, previewDirectory, 400), 180, 380);
                    var reference = (FrameworkElement)details.FindName("LatestReference");
                    Assert.Equal(2, ((CollectionDetailsWindow.CollectionBatchRow)reference.DataContext).BoundaryReferences.Count);
                    Assert.Equal(3, ((CollectionDetailsWindow.CollectionBatchRow)reference.DataContext).References.Count);
                    var tabs = Descendants<Button>((ItemsControl)details.FindName("ReferenceTabs")).ToList();
                    tabs[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(batch.BatchId, ((CollectionDetailsWindow.CollectionBatchRow)reference.DataContext).Batch.Id);
                    tabs[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(secondBatch.BatchId, ((CollectionDetailsWindow.CollectionBatchRow)reference.DataContext).Batch.Id);
                    RenderDetached(details, "collection-details", language, previewDirectory, 490);
                    var previousName = reader.StateFor(secondBatch.BatchId)?.ChatName;
                    ((Button)details.FindName("RenameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var noteEditor = (StackPanel)details.FindName("NameEditor");
                    ((TextBox)details.FindName("ChatNameInput")).Text = "不保存的备注";
                    Descendants<Button>(noteEditor).Single(b => b.Content?.ToString() == L10n.Text("取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(previousName, reader.StateFor(secondBatch.BatchId)?.ChatName);
                    ((Button)details.FindName("RenameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ((TextBox)details.FindName("ChatNameInput")).Text = "项目讨论群";
                    RenderDetached(details, "collection-note", language, previewDirectory, 490);
                    Descendants<Button>(noteEditor).Single(b => b.Content?.ToString() == L10n.Text("保存名称")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("项目讨论群", reader.StateFor(secondBatch.BatchId)?.ChatName);
                    details.Close();
                    Assert.True(window.IsVisible);
                    Assert.Null(window.DetailsWindow);
                    model.Collections.SetChatName(collectionId, batch.BatchId, "", false);
                    model.Reload();
                    var beforeIds = model.Collections.Ledger.Editable(collectionId).BatchIDs.ToList();
                    ((Button)window.FindName("DeliverButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var delivery = Assert.IsType<CollectionDeliveryWindow>(window.DeliveryWindow);
                    Assert.True(delivery.IsVisible);
                    Assert.Equal(WindowStyle.None, delivery.WindowStyle);
                    Assert.False(((Button)delivery.FindName("Confirm")).IsEnabled);
                    RenderDetached(delivery, "collection-delivery", language, previewDirectory, 440);
                    var targetList = (ListBox)delivery.FindName("Targets");
                    targetList.SelectedIndex = targetList.Items.Count - 1;
                    Assert.True(((Button)delivery.FindName("Confirm")).IsEnabled);
                    Assert.False(((ComboBox)delivery.FindName("Scenes")).IsEnabled);
                    // Cancelling the real folder flow keeps delivery open and does not freeze the collection.
                    using var folderModel = new FolderProbe(fixture.Root);
                    model.DeliveryFolderPath = folderModel.Root;
                    targetList.SelectedItem = targetList.Items.Cast<object>().Single(t => t.GetType().GetProperty("Title")!.GetValue(t)?.ToString() == L10n.Text("保存到文件夹"));
                    ((Button)delivery.FindName("Confirm")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var chooser = Assert.IsType<WeChatBridge.Windows.Components.CollectionFolderWindow>(delivery.FolderWindow);
                    WaitFor(() => Descendants<Button>(chooser).Any(b => b.Name == "FolderConfirm" && b.IsEnabled));
                    RenderDetached(chooser, "collection-folder", language, previewDirectory, 460);
                    chooser.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(chooser)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    WaitFor(() => delivery.FolderWindow is null);
                    Assert.True(delivery.IsVisible);
                    Assert.True(((Button)delivery.FindName("Confirm")).IsEnabled);
                    Assert.Equal(CollectionStatus.Collecting, model.Collections.Ledger.Editable(collectionId).Status);
                    Assert.False(File.Exists(Path.Combine(folderModel.Root,"sample.zip")));
                    var confirmation = new WeChatBridge.Windows.Components.CollectionConfirmationWindow(L10n.Text("删除整组收集"), L10n.Text("原始文件将移到回收站。")) { Owner = delivery };
                    confirmation.Show();
                    RenderDetached(confirmation, "collection-confirmation", language, previewDirectory, 460);
                    confirmation.Close();
                    Assert.Equal(beforeIds, model.Collections.Ledger.Editable(collectionId).BatchIDs);
                    delivery.Close();
                    WaitFor(() => window.DeliveryWindow is null);
                    Assert.Equal(CollectionStatus.Collecting, model.Collections.Ledger.Editable(collectionId).Status);
                    Assert.Equal(beforeIds, model.Collections.Ledger.Editable(collectionId).BatchIDs);
                    Assert.True(string.IsNullOrWhiteSpace(reader.StateFor(batch.BatchId)?.ChatName));
                    ((Button)window.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.False(window.IsVisible);
                    window.Show();
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Assert.False(window.IsVisible);
                    window.Close();
                    Assert.Equal(CollectionStatus.Collecting, model.Collections.Ledger.Editable(collectionId).Status);
                    Assert.True(model.RenameCollection(collectionId, "  示例收集  "));
                    Assert.Equal("示例收集", new CollectionService(fixture.Paths, reader).Ledger.Editable(collectionId).Name);
                    model.Collections.Ledger.ParkCurrent();
                    model.Collections.Save();
                    model.Reload();
                    Assert.Contains(L10n.Text("待发送"), model.CollectionRows.Single(r => r.Id == collectionId).Detail);
                    var draftWindow = new CollectionWindow(model, collectionId);
                    draftWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    Assert.StartsWith(L10n.Text("待发送"), ((TextBlock)draftWindow.FindName("Status")).Text);
                    Assert.Equal(Visibility.Visible, ((Button)draftWindow.FindName("ResumeButton")).Visibility);
                    draftWindow.Close();
                    model.Collections.Ledger.Resume(collectionId);
                    model.Collections.Save();
                }
                app.Shutdown();
            }
            catch (Exception error) { failure = error; }
            finally { L10n.Language = originalLanguage; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "UI render did not complete");
        Assert.Null(failure);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed class FolderProbe : IDisposable
    {
        public string Root { get; }
        public FolderProbe(string parent) { Root = Path.Combine(parent,"folder-picker"); Directory.CreateDirectory(Path.Combine(Root,"child")); }
        public void Dispose() { }
    }
    private static double RenderDetached(Window window, string name, string language, string? directory, double width)
    {
        var content = (FrameworkElement)window.Content;
        if (!content.Resources.MergedDictionaries.Contains(window.Resources)) content.Resources.MergedDictionaries.Add(window.Resources);
        window.Content = null;
        try { return Render(content, name, language, directory, width); }
        finally { window.Content = content; }
    }

    private static void WaitFor(Func<bool> ready)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (ready() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
        Assert.True(ready(), "The collection metadata did not reach the UI.");
    }

    private static double Render(FrameworkElement content, string name, string language, string? directory, double width = 710)
    {
        var canvas = new Border { Background = content.TryFindResource("BackgroundColor") as Brush
            ?? (Brush)Application.Current.Resources["BackgroundColor"], Child = content };
        var height = 650.0;
        // Reparented elements may retain their previous measurement. Resolve
        // templates and visibility changes before sizing the exported bitmap.
        for (var pass = 0; pass < 2; pass++)
        {
            content.InvalidateMeasure();
            canvas.InvalidateMeasure();
            canvas.Measure(new Size(width, double.PositiveInfinity));
            height = width < 710 ? Math.Ceiling(canvas.DesiredSize.Height) : 650;
            canvas.Arrange(new Rect(0, 0, width, height));
            canvas.UpdateLayout();
        }
        Assert.True(content.ActualWidth > 0);
        if (directory is null) { canvas.Child = null; return height; }
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, $"{name}-{language}.png"));
        encoder.Save(stream);
        canvas.Child = null;
        return height;
    }
}
