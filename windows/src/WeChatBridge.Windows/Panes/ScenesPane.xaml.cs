using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.Panes;

/// <summary>
/// The 场景 pane — the Windows port of macOS <c>SceneSettingsView</c>: a scene
/// library with create/edit/duplicate/delete, per-scene prompt text and
/// compatible-agent selection on the left page, and per-group scene bindings on
/// the right. The pane keeps no truth of its own: every mutation goes through
/// <see cref="SceneService"/> and every refresh re-reads the stores, the same
/// way the macOS view reads <c>preferences.scenes</c> / <c>groupMemory</c>.
/// </summary>
public partial class ScenesPane : UserControl
{
    private SceneService? _service;
    private SkillService? _skills;
    private int _promptCaret;
    private SceneSettings _settings = new();
    private IReadOnlyDictionary<string, GroupMemory> _memories =
        new Dictionary<string, GroupMemory>();
    private string? _selectedSceneId;
    private string? _selectedGroupKey;
    private bool _loaded;

    public ScenesPane()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loaded = true;
            // The integrator may hand the service over through the DataContext
            // (a ScenesPane sits in PaneHost alongside panes bound to
            // MainViewModel, so it cannot inherit it — it must be set directly).
            if (DataContext is SceneService service)
                Bind(service);
            else if (_service is not null)
                // Bind ran before the pane was in the visual tree, when
                // ReloadAll skipped everything on !_loaded — catch up now.
                ReloadAll();
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SceneService service)
                Bind(service);
        };
    }

    /// <summary>
    /// Wire the pane to its service. Idempotent. <paramref name="skills"/>
    /// powers 插入技能 and the prompt preview; without it both stay hidden.
    /// </summary>
    public void Bind(SceneService service, SkillService? skills = null)
    {
        if (skills is not null && !ReferenceEquals(_skills, skills))
        {
            _skills = skills;
            BuildPreviewAgents();
        }
        if (ReferenceEquals(_service, service))
        {
            ReloadAll();
            return;
        }
        if (_service is not null)
            _service.Changed -= OnServiceChanged;
        _service = service;
        _service.Changed += OnServiceChanged;
        ReloadAll();
    }

    private SceneService? Service =>
        _service ?? DataContext as SceneService;

    /// <summary>
    /// Service writes can arrive from a background forward (a learned binding);
    /// list rebuilds must happen on the dispatcher.
    /// </summary>
    private void OnServiceChanged() =>
        Dispatcher.BeginInvoke(ReloadAll);

    // MARK: - Row shapes

    private sealed class SceneRow
    {
        public required WeChatScene Scene { get; init; }
        public string? Hotkey { get; init; }
        public bool Enabled => Scene.Enabled;
        public string NameText =>
            string.IsNullOrWhiteSpace(Scene.Name) ? L10n.Text("未命名场景") : Scene.Name;
        public string SummaryText =>
            string.IsNullOrWhiteSpace(Scene.Summary) ? L10n.Text("没有一句话说明") : Scene.Summary;
    }

    private sealed class GroupRow
    {
        public required string Key { get; init; }
        public required GroupMemory Memory { get; init; }
        public required IReadOnlyList<string> BoundNames { get; init; }
        public bool Selected { get; init; }
        public string NameText => Memory.DisplayName;
        public string BindingText =>
            BoundNames.Count == 0 ? L10n.Text("转发时选择或直接转发") : string.Join("、", BoundNames);
    }

    private sealed class AgentRow
    {
        public required AgentId Agent { get; init; }
        public bool IsSelected { get; init; }
        public bool Editable { get; init; }
        public string Name => Agent.DisplayName();

        /// <summary>One-letter stand-in under the logo when the PNG is missing.</summary>
        public string Mark => Name.Length > 0 ? Name[..1] : "?";
    }

    private sealed class BoundSceneRow
    {
        public required WeChatScene Scene { get; init; }
        public bool IsBound { get; init; }

        /// <summary>Last row in the list — hides its trailing hairline divider.</summary>
        public bool IsLast { get; init; }
        public string NameText =>
            string.IsNullOrWhiteSpace(Scene.Name) ? L10n.Text("未命名场景") : Scene.Name;
        public string SummaryText =>
            string.IsNullOrWhiteSpace(Scene.Summary) ? L10n.Text("没有一句话说明") : Scene.Summary;
        public string AgentsText =>
            string.Join(" · ", Scene.CompatibleAgents.Select(a => a.DisplayName()));
    }

    // MARK: - Reload

    private void ReloadAll()
    {
        if (Service is not { } service || !_loaded)
            return;
        CommitEdits();
        _settings = service.LoadSettings();
        _memories = service.LoadMemories();

        if (_selectedSceneId is null
            || _settings.Scenes.All(s => s.Id != _selectedSceneId))
            _selectedSceneId = _settings.Scenes.FirstOrDefault()?.Id;
        if (_selectedGroupKey is null || !_memories.ContainsKey(_selectedGroupKey))
            _selectedGroupKey = SortedGroupKeys().FirstOrDefault();

        SceneCount.Text = L10n.Format($"{_settings.Scenes.Count} 个");
        var boundCount = _memories.Values.Count(m => m.BoundSceneIDs.Count > 0);
        GroupCount.Text = L10n.Format($"{boundCount} 个已绑定");

        RebuildSceneList();
        RebuildSceneEditor();
        RebuildGroupLists();
        RebuildGroupEditor();
    }

    private IEnumerable<string> SortedGroupKeys() =>
        _memories.Keys.OrderBy(
            key => _memories[key].DisplayName,
            StringComparer.CurrentCulture);

    private IEnumerable<string> FilteredGroupKeys()
    {
        var query = GroupSearch.Text.Trim();
        var keys = SortedGroupKeys();
        return query.Length == 0
            ? keys
            : keys.Where(key => _memories[key].DisplayName.Contains(
                query, StringComparison.CurrentCultureIgnoreCase));
    }

    // MARK: - Scene page

    private void RebuildSceneList()
    {
        if (Service is not { } service)
            return;
        var query = SceneSearch.Text.Trim();
        var rows = _settings.Scenes
            .Where(s => query.Length == 0
                || s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || s.Summary.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(s => new SceneRow { Scene = s, Hotkey = service.ShortcutHint(s) })
            .ToList();
        SceneList.ItemsSource = rows;
        SceneList.SelectedItem = rows.FirstOrDefault(r => r.Scene.Id == _selectedSceneId);
    }

    private void RebuildSceneEditor()
    {
        var scene = _settings.Scenes.FirstOrDefault(s => s.Id == _selectedSceneId);
        if (scene is null)
        {
            EditorEmpty.Visibility = Visibility.Visible;
            EditorContent.Visibility = Visibility.Collapsed;
            return;
        }
        EditorEmpty.Visibility = Visibility.Collapsed;
        EditorContent.Visibility = Visibility.Visible;

        var name = string.IsNullOrWhiteSpace(scene.Name) ? L10n.Text("未命名场景") : scene.Name;
        EditorTitle.Text = name;
        EditorStatus.Text = scene.Enabled ? L10n.Text("已启用") : L10n.Text("已停用");
        EditorStatus.Foreground = scene.Enabled
            ? (Brush)FindResource("LiveInkColor")
            : (Brush)FindResource("InkTertiaryColor");
        EnableBox.IsChecked = scene.Enabled;

        var prompt = PromptText(scene);

        if (scene.IsOfficial)
        {
            EditFields.Visibility = Visibility.Collapsed;
            ReadFields.Visibility = Visibility.Visible;
            OfficialHint.Visibility = Visibility.Visible;
            ReadName.Text = string.IsNullOrWhiteSpace(scene.Name) ? L10n.Text("无") : scene.Name;
            ReadSummary.Text = string.IsNullOrWhiteSpace(scene.Summary) ? L10n.Text("无") : scene.Summary;
            ReadPrompt.Text = string.IsNullOrWhiteSpace(prompt) ? L10n.Text("无") : prompt;
        }
        else
        {
            EditFields.Visibility = Visibility.Visible;
            ReadFields.Visibility = Visibility.Collapsed;
            OfficialHint.Visibility = Visibility.Collapsed;
            EditName.Text = scene.Name;
            EditSummary.Text = scene.Summary;
            // Reassigning identical text would reset the caret 插入技能 relies on.
            if (EditPrompt.Text != prompt)
                EditPrompt.Text = prompt;
        }
        InsertSkillButton.Visibility = _skills is null ? Visibility.Collapsed : Visibility.Visible;
        RebuildSkillPanel(scene);

        AgentGrid.ItemsSource = AgentIds.All
            .Select(agent => new AgentRow
            {
                Agent = agent,
                IsSelected = scene.CompatibleAgents.Contains(agent),
                Editable = !scene.IsOfficial,
            })
            .ToList();

        // Official templates keep the read-only hint row instead of the
        // duplicate/delete/save action row.
        EditorActions.Visibility = scene.IsOfficial
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>
    /// Writes pending field edits into the store — the merged 提示词 lands in
    /// <see cref="WeChatScene.Instruction"/> and clears <see cref="WeChatScene.OutputSpec"/>,
    /// matching the macOS <c>promptBinding</c> setter.
    /// </summary>
    private void CommitEdits()
    {
        if (Service is not { } service || EditFields.Visibility != Visibility.Visible)
            return;
        var scene = _settings.Scenes.FirstOrDefault(s => s.Id == _selectedSceneId);
        if (scene is null || scene.IsOfficial)
            return;
        if (EditName.Text == scene.Name
            && EditSummary.Text == scene.Summary
            && EditPrompt.Text == PromptText(scene))
            return;
        scene.Name = EditName.Text;
        scene.Summary = EditSummary.Text;
        scene.Instruction = EditPrompt.Text;
        scene.OutputSpec = "";
        service.UpdateScene(scene);
    }

    private static string PromptText(WeChatScene scene) =>
        string.Join("\n\n",
            new[]
            {
                scene.Instruction,
                string.IsNullOrWhiteSpace(scene.OutputSpec) ? "" : L10n.Format($"输出规范：\n{scene.OutputSpec}"),
            }.Where(part => part.Trim().Length > 0));

    /// <summary>
    /// Opens the scene page on the first scene that references
    /// <paramref name="skillId"/> — the target of the skills page's 用于 N 个场景.
    /// </summary>
    public void ShowScenesReferencing(string skillId)
    {
        ShowScenesPage_Click(this, new RoutedEventArgs());
        if (Service is not { } service)
            return;
        SceneSearch.Text = "";
        var match = service.LoadSettings().Scenes
            .FirstOrDefault(s => s.EffectiveSkillIDs().Contains(skillId));
        if (match is null)
            return;
        CommitEdits();
        _selectedSceneId = match.Id;
        ReloadAll();
    }

    // MARK: - Skill references & preview

    private WeChatScene? SelectedScene =>
        _settings.Scenes.FirstOrDefault(s => s.Id == _selectedSceneId);

    private AgentId? PreviewAgent =>
        (PreviewAgentBox.SelectedItem as ComboBoxItem)?.Tag is AgentId agent ? agent : null;

    private void BuildPreviewAgents()
    {
        PreviewAgentBox.Items.Clear();
        PreviewAgentBox.Items.Add(new ComboBoxItem { Content = L10n.Text("无 Agent（剪贴板 / 自定义）") });
        foreach (var agent in AgentIds.All)
            PreviewAgentBox.Items.Add(new ComboBoxItem { Content = agent.DisplayName(), Tag = agent });
        PreviewAgentBox.SelectedIndex = 1;
    }

    /// <summary>
    /// The scene as it would be saved right now: user scenes take the unsaved
    /// prompt text (merged into Instruction, like <see cref="CommitEdits"/>).
    /// </summary>
    private WeChatScene Draft(WeChatScene scene)
    {
        if (scene.IsOfficial || EditFields.Visibility != Visibility.Visible)
            return scene;
        return new WeChatScene
        {
            Id = scene.Id,
            Instruction = EditPrompt.Text,
            RequiredSkillIDs = SkillReference.Parse(EditPrompt.Text).ToList(),
            CompatibleAgents = scene.CompatibleAgents,
        };
    }

    private void RebuildSkillPanel(WeChatScene scene)
    {
        if (_skills is not { } skills)
        {
            SkillPanel.Visibility = Visibility.Collapsed;
            return;
        }
        SkillPanel.Visibility = Visibility.Visible;
        var draft = Draft(scene);
        var agent = PreviewAgent;
        var context = skills.PromptContext(agent);

        var resolved = draft.EffectiveSkillIDs().Select(context.Resolve).ToList();
        SkillRefsText.Text = resolved.Count == 0
            ? L10n.Text("没有引用技能。点「插入技能」可在光标处插入 {{skill:id}}。")
            : string.Join("\n", resolved.Select(r => $"• {r.DisplayName}（{r.Id}）：{ModeText(r.Mode, agent)}"));

        var warnings = SkillReference.Invalid(draft.Instruction)
            .Select(id => L10n.Format($"「{id}」不是有效的技能 ID（只能使用小写字母、数字和连字符）。"))
            .Concat(resolved.Where(r => r.Mode == SkillRenderMode.Unknown)
                .Select(r => L10n.Format($"未找到技能「{r.Id}」，转发时会提示 Agent 该技能不存在。")))
            .ToList();
        SkillWarnText.Text = string.Join("\n", warnings);
        SkillWarnBar.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        PreviewPrompt.Text = agent is { } target && !draft.CompatibleAgents.Contains(target)
            ? L10n.Format($"这个场景不适用于 {target.DisplayName()}，转发时不会附加提示词。")
            : ScenePrompt.Render(draft, null, skills: context) ?? L10n.Text("（提示词为空）");
    }

    private static string ModeText(SkillRenderMode mode, AgentId? agent) => mode switch
    {
        SkillRenderMode.Native => L10n.Text("已安装到该 Agent"),
        SkillRenderMode.Path => L10n.Text("通过技能库中的 SKILL.md 引用"),
        SkillRenderMode.Missing when agent is null => L10n.Text("技能库中暂无技能包"),
        SkillRenderMode.Missing => L10n.Text("该 Agent 无法使用，转发时会要求说明未完成部分"),
        _ => L10n.Text("未找到该技能"),
    };

    private void EditPrompt_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loaded && SelectedScene is { } scene)
            RebuildSkillPanel(scene);
    }

    private void EditPrompt_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (EditPrompt.IsKeyboardFocusWithin)
            _promptCaret = EditPrompt.CaretIndex;
    }

    private void PreviewAgent_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && SelectedScene is { } scene)
            RebuildSkillPanel(scene);
    }

    /// <summary>插入技能 — a menu of referenceable skills; the pick lands at the caret.</summary>
    private void InsertSkill_Click(object sender, RoutedEventArgs e)
    {
        if (_skills is not { } skills)
            return;
        var menu = new ContextMenu();
        var choices = skills.ReferenceableSkills();
        if (choices.Count == 0)
            menu.Items.Add(new MenuItem { Header = L10n.Text("没有可引用的技能"), IsEnabled = false });
        foreach (var (id, name) in choices)
        {
            var state = skills.Library.State(id) switch
            {
                SkillLibraryState.Ready => "",
                SkillLibraryState.Conflict => L10n.Text(" · 技能库中已被修改"),
                _ => L10n.Text(" · 暂无技能包"),
            };
            AddItem(menu, $"{name}（{id}）{state}", (_, _) => InsertToken(SkillReference.Token(id)));
        }
        menu.PlacementTarget = InsertSkillButton;
        menu.IsOpen = true;
    }

    private void InsertToken(string token)
    {
        var caret = Math.Clamp(_promptCaret, 0, EditPrompt.Text.Length);
        EditPrompt.Text = EditPrompt.Text.Insert(caret, token);
        EditPrompt.Focus();
        EditPrompt.CaretIndex = caret + token.Length;
        _promptCaret = EditPrompt.CaretIndex;
    }

    private void ShowScenesPage_Click(object sender, RoutedEventArgs e)
    {
        ScenesPage.Visibility = Visibility.Visible;
        GroupsPage.Visibility = Visibility.Collapsed;
        ScenesTab.Tag = "Selected";
        GroupsTab.Tag = null;
    }

    private void ShowGroupsPage_Click(object sender, RoutedEventArgs e)
    {
        ScenesPage.Visibility = Visibility.Collapsed;
        GroupsPage.Visibility = Visibility.Visible;
        ScenesTab.Tag = null;
        GroupsTab.Tag = "Selected";
        ReloadAll();
    }

    private void SceneSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        RebuildSceneList();

    private void SceneList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SceneList.SelectedItem is not SceneRow row)
            return;
        CommitEdits();
        _selectedSceneId = row.Scene.Id;
        RebuildSceneEditor();
    }

    private void AddScene_Click(object sender, RoutedEventArgs e)
    {
        if (Service is not { } service)
            return;
        var scene = service.AddScene();
        _selectedSceneId = scene.Id;
        ReloadAll();
    }

    private void Enable_Click(object sender, RoutedEventArgs e)
    {
        if (Service is { } service && _selectedSceneId is { } id)
            service.SetSceneEnabled(id, EnableBox.IsChecked == true);
        // ReloadAll arrives through Changed.
    }

    private void EditField_LostFocus(object sender, RoutedEventArgs e) => CommitEdits();

    private void AgentCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Service is not { } service
            || sender is not CheckBox { DataContext: AgentRow row } box
            || _settings.Scenes.FirstOrDefault(s => s.Id == _selectedSceneId) is not { } scene
            || scene.IsOfficial)
            return;
        var agent = row.Agent;
        if (box.IsChecked == true)
        {
            if (!scene.CompatibleAgents.Contains(agent))
                scene.CompatibleAgents.Add(agent);
        }
        else
        {
            scene.CompatibleAgents.Remove(agent);
        }
        service.UpdateScene(scene);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Service is not { } service || _selectedSceneId is null)
            return;
        var copy = service.DuplicateScene(_selectedSceneId);
        if (copy is not null)
        {
            _selectedSceneId = copy.Id;
            ShowNotice(L10n.Text("已复制为我的场景。"), good: true);
        }
        ReloadAll();
    }

    /// <summary>The editor's 删除 — the destructive half of the bottom action row.</summary>
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Scenes.FirstOrDefault(s => s.Id == _selectedSceneId) is { } scene
            && !scene.IsOfficial)
            RemoveScene(scene);
    }

    /// <summary>保存更改 — fields already commit on LostFocus; this just forces it.</summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitEdits();
        ShowNotice(L10n.Text("已保存更改。"), good: true);
    }

    private void EditorMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Service is not { } service
            || _settings.Scenes.FirstOrDefault(s => s.Id == _selectedSceneId) is not { } scene)
            return;
        var menu = new ContextMenu();
        if (scene.IsOfficial)
            AddItem(menu, L10n.Text("复制为我的场景"), (_, _) => Duplicate_Click(sender, e));
        AddItem(menu, L10n.Text("导出场景包"), (_, _) => ExportScene(scene));
        AddSeparator(menu);
        if (scene.Enabled)
        {
            var isDefault = _settings.DefaultSceneID == scene.Id;
            AddItem(menu, isDefault ? L10n.Text("取消默认场景") : L10n.Text("设为默认场景"),
                (_, _) => service.SetDefaultScene(isDefault ? null : scene.Id));
        }
        AddItem(menu, L10n.Text("上移"), (_, _) => MoveSelected(-1));
        AddItem(menu, L10n.Text("下移"), (_, _) => MoveSelected(1));
        if (!scene.IsOfficial)
        {
            AddSeparator(menu);
            AddItem(menu, L10n.Text("删除场景"), (_, _) => RemoveScene(scene));
        }
        menu.PlacementTarget = EditorMenu;
        menu.IsOpen = true;
    }

    private static void AddItem(ContextMenu menu, string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        menu.Items.Add(item);
    }

    private static void AddSeparator(ContextMenu menu) => menu.Items.Add(new Separator());

    private void MoveSelected(int offset)
    {
        if (Service is { } service && _selectedSceneId is { } id)
            service.MoveScene(id, offset);
    }

    private void RemoveScene(WeChatScene scene)
    {
        if (Service is not { } service)
            return;
        service.RemoveScene(scene.Id);
        _selectedSceneId = _settings.Scenes
            .FirstOrDefault(s => s.Id != scene.Id)?.Id;
        ReloadAll();
    }

    private void ExportScene(WeChatScene scene)
    {
        if (Service is not { } service)
            return;
        string json;
        try
        {
            json = service.ExportPackageJson(scene);
        }
        catch (SceneService.ScenePackageException error)
        {
            ShowNotice(error.Message, good: false);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = L10n.Text("导出场景包"),
            Filter = L10n.Text("场景包 (*.json)|*.json"),
            FileName = $"{scene.Name}.wechatflow-scene.json",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        try
        {
            File.WriteAllText(dialog.FileName, json);
            ShowNotice(L10n.Text("场景包已导出。"), good: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowNotice(error.Message, good: false);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = L10n.Text("导入场景包"),
            Filter = L10n.Text("场景包 (*.json)|*.json"),
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ImportFiles(dialog.FileNames);
    }

    private void SceneList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void SceneList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            ImportFiles(paths);
    }

    private void ImportFiles(IEnumerable<string> paths)
    {
        if (Service is not { } service)
            return;
        var report = service.ImportScenePackages(paths, package =>
            MessageBox.Show(
                Window.GetWindow(this),
                L10n.Format($"「{package.Name}」已安装同版本场景。覆盖将更新场景内容，本地的启用状态和群绑定会保留。"),
                L10n.Text("场景版本已存在"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) == MessageBoxResult.OK);
        if (report.Errors.Count > 0)
        {
            ShowNotice(report.Errors[0], good: false);
        }
        else if (report.Imported > 0)
        {
            var message = L10n.Format($"已导入 {report.Imported} 个场景。");
            if (report.SkippedOlder > 0)
                message += L10n.Text("已安装的场景版本更新，未导入较旧版本。");
            ShowNotice(message, good: true);
        }
        else if (report.SkippedOlder > 0)
        {
            ShowNotice(L10n.Text("已安装的场景版本更新，未导入较旧版本。"), good: false);
        }
        else if (report.Declined > 0)
        {
            ShowNotice(L10n.Text("已取消导入。"), good: false);
        }
        ReloadAll();
    }

    // MARK: - Groups page

    private void RebuildGroupLists()
    {
        var keys = FilteredGroupKeys().ToList();
        var bound = keys.Where(k => _memories[k].BoundSceneIDs.Count > 0).ToList();
        var unbound = keys.Where(k => _memories[k].BoundSceneIDs.Count == 0).ToList();

        BoundHeader.Visibility = bound.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnboundHeader.Visibility = unbound.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BoundGroupList.ItemsSource = bound.Select(ToRow).ToList();
        UnboundGroupList.ItemsSource = unbound.Select(ToRow).ToList();
    }

    private GroupRow ToRow(string key) => new()
    {
        Key = key,
        Memory = _memories[key],
        BoundNames = _settings
            .ScenesFor(_memories[key].BoundSceneIDs)
            .Select(s => string.IsNullOrWhiteSpace(s.Name) ? L10n.Text("未命名场景") : s.Name)
            .ToList(),
        Selected = key == _selectedGroupKey,
    };

    private void RebuildGroupEditor()
    {
        if (_selectedGroupKey is not { } key
            || !_memories.TryGetValue(key, out var memory))
        {
            GroupEmpty.Visibility = Visibility.Visible;
            GroupEditor.Visibility = Visibility.Collapsed;
            return;
        }
        GroupEmpty.Visibility = Visibility.Collapsed;
        GroupEditor.Visibility = Visibility.Visible;

        GroupTitle.Text = memory.DisplayName;
        var bound = _settings.ScenesFor(memory.BoundSceneIDs);
        GroupBoundCount.Text = L10n.Format($"已关联 {bound.Count} 个可选场景");
        var boundIds = memory.BoundSceneIDs.ToHashSet(StringComparer.Ordinal);
        var enabled = _settings.EnabledScenes;
        BoundSceneList.ItemsSource = enabled
            .Select((scene, index) => new BoundSceneRow
            {
                Scene = scene,
                IsBound = boundIds.Contains(scene.Id),
                IsLast = index == enabled.Count - 1,
            })
            .ToList();

        var disabled = _settings.Scenes.Where(s => !s.Enabled).Select(s => s.Name).ToList();
        DisabledScenesNote.Visibility = disabled.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DisabledSceneNames.Text = string.Join("、", disabled);
        ClearBindingButton.Visibility = bound.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void GroupSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        RebuildGroupLists();

    private void GroupRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupRow row)
            return;
        _selectedGroupKey = row.Key;
        RebuildGroupLists();
        RebuildGroupEditor();
    }

    private void BoundSceneCheck_Click(object sender, RoutedEventArgs e)
    {
        if (Service is { } service
            && _selectedGroupKey is { } key
            && (sender as CheckBox)?.DataContext is BoundSceneRow row)
        {
            service.ToggleGroupBinding(key, row.Scene.Id);
            // Changed → ReloadAll redraws the checkbox from the store.
        }
    }

    private void ClearBinding_Click(object sender, RoutedEventArgs e)
    {
        if (Service is { } service && _selectedGroupKey is { } key)
            service.ClearGroupBinding(key);
    }

    // MARK: - Notice

    private void ShowNotice(string message, bool good)
    {
        NoticeText.Text = message;
        NoticeBar.Background = good
            ? (Brush)FindResource("LiveFillColor")
            : (Brush)FindResource("WarnFillColor");
        NoticeText.Foreground = good
            ? (Brush)FindResource("LiveInkColor")
            : (Brush)FindResource("WarnInkColor");
        NoticeBar.Visibility = Visibility.Visible;
    }
}

/// <summary>
/// Resolves an <see cref="AgentId"/> to its bundled logo image — the XAML face
/// of <see cref="AppLogos.PathFor(AgentId)"/>. A missing logo yields null, and
/// callers keep a letter block behind the image as the fallback. Shared by the
/// scenes pane's 适用 Agent tiles and the skills pane's agent rows.
/// </summary>
public sealed class AgentLogoConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<AgentId, BitmapImage?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AgentId agent)
            return null;
        return Cache.GetOrAdd(agent, a =>
        {
            var path = AppLogos.PathFor(a);
            if (path is null)
                return null;
            var image = new BitmapImage(new Uri(path, UriKind.Absolute));
            image.Freeze();
            return image;
        });
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
