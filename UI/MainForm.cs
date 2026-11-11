using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TiaTextSync.Core;
using TiaTextSync.Services;
using static TiaTextSync.Core.Logger;

namespace TiaTextSync.UI;

internal sealed class MainForm : Form
{
    private enum Scope
    {
        /// <summary>Выбранная ветвь дерева: устройство, ПЛК, группа или элемент.</summary>
        Selection,
        /// <summary>Элементы, которые последнее сравнение отметило как отличающиеся.</summary>
        Differences,
        /// <summary>Устройство целиком.</summary>
        Device
    }

    /// <summary>Что загрузить в один ПЛК при импорте.</summary>
    private sealed class PlcImport
    {
        public PlcImport(PlcSoftware plc, string deviceDir, List<WorkspaceItem> items, int newCount)
        {
            Plc       = plc;
            DeviceDir = deviceDir;
            Items     = items;
            NewCount  = newCount;
        }

        public PlcSoftware         Plc       { get; }
        public string              DeviceDir { get; }
        public List<WorkspaceItem> Items     { get; }

        /// <summary>Сколько элементов из <see cref="Items"/> ещё нет в ПЛК.</summary>
        public int NewCount { get; }
    }

    /// <summary>Что обработать на одном устройстве. Определяется на UI-потоке по выбору в дереве.</summary>
    private sealed class Target
    {
        public Target(Device device, PlcSoftware? plc, HashSet<string>? keys, bool includeHw)
        {
            Device    = device;
            Plc       = plc;
            Keys      = keys;
            IncludeHw = includeHw;
        }

        public Device Device { get; }

        /// <summary>null — все ПЛК устройства.</summary>
        public PlcSoftware? Plc { get; }

        /// <summary>Ключи <see cref="PlcItem.Key"/>; null — все элементы ПЛК.</summary>
        public HashSet<string>? Keys { get; }

        public bool IncludeHw { get; }
    }

    // ── Состояние ─────────────────────────────────────────────────────────────
    private readonly StaWorker        _sta       = new();
    private readonly WorkspaceService _workspace = new();
    private TiaSession?     _session;
    private Project?        _project;
    private CompareResults  _compare = new();
    private bool            _isBusy;
    private bool            _refreshing;
    private bool            _refreshPending;

    // ── Элементы управления ───────────────────────────────────────────────────
    private readonly ToolStrip            _toolbar;
    private readonly SplitContainer       _split;
    private readonly TreeView             _tree;
    private readonly TextBox              _log;
    private readonly StatusStrip          _statusStrip;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _workspaceLabel;

    private readonly ToolStripDropDownButton _btnOpen;
    private readonly ToolStripButton         _btnWorkspace;
    private readonly ToolStripSplitButton    _btnCompare;
    private readonly ToolStripSplitButton    _btnExport;
    private readonly ToolStripSplitButton    _btnImport;
    private readonly ToolStripDropDownButton _btnTools;
    private readonly ToolStripMenuItem       _itemRefresh;
    private readonly ToolStripMenuItem       _itemCompile;
    private readonly ToolStripButton         _btnDisconnect;

    public MainForm()
    {
        Text        = "TIA Text Sync";
        Size        = new Size(1024, 660);
        MinimumSize = new Size(700, 440);

        // ── Строка состояния ──────────────────────────────────────────────────
        _statusLabel = new ToolStripStatusLabel("Нет подключения")
            { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

        _workspaceLabel = new ToolStripStatusLabel("Папка: не выбрана")
            { TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.Gray };

        _statusStrip = new StatusStrip { Items = { _statusLabel, _workspaceLabel } };

        // ── Панель инструментов ───────────────────────────────────────────────
        var itemConnect  = new ToolStripMenuItem("Подключиться к открытому в TIA");
        var itemOpenFile = new ToolStripMenuItem("Открыть проект...");

        _btnOpen = new ToolStripDropDownButton("TIA Portal")
            { DropDownItems = { itemConnect, itemOpenFile } };

        _btnWorkspace = new ToolStripButton("Рабочая папка...");

        var itemCompareSelection = new ToolStripMenuItem("Выбранное в дереве");
        var itemCompareProject   = new ToolStripMenuItem("Весь проект");

        _btnCompare = new ToolStripSplitButton("Сравнить")
        {
            Enabled       = false,
            ToolTipText   = "Сравнить выбранное в дереве с рабочей папкой",
            DropDownItems = { itemCompareSelection, itemCompareProject }
        };

        var itemExportSelection   = new ToolStripMenuItem("Выбранное в дереве");
        var itemExportDifferences = new ToolStripMenuItem("Отличия по последнему сравнению");
        var itemExportDevice      = new ToolStripMenuItem("Устройство целиком");

        _btnExport = new ToolStripSplitButton("Экспорт")
        {
            Enabled       = false,
            ToolTipText   = "Экспортировать выбранное в дереве: устройство, ПЛК, группу или элемент",
            DropDownItems = { itemExportSelection, itemExportDifferences, itemExportDevice }
        };

        var itemImportSelection   = new ToolStripMenuItem("Выбранное в дереве");
        var itemImportDifferences = new ToolStripMenuItem("Отличия по последнему сравнению");
        var itemImportDevice      = new ToolStripMenuItem("Устройство целиком");

        _btnImport = new ToolStripSplitButton("Импорт")
        {
            Enabled       = false,
            ToolTipText   = "Загрузить в TIA из рабочей папки выбранное в дереве: устройство, ПЛК, группу или элемент",
            DropDownItems = { itemImportSelection, itemImportDifferences, itemImportDevice }
        };

        _itemRefresh = new ToolStripMenuItem("Обновить дерево") { Enabled = false };

        _itemCompile = new ToolStripMenuItem("Компилировать ПЛК при экспорте, сравнении и импорте")
        {
            CheckOnClick = true,
            ToolTipText  = "Компилировать программу ПЛК перед экспортом и сравнением и после импорта. Изменяет состояние проекта в TIA"
        };

        _btnTools = new ToolStripDropDownButton("Инструменты")
            { DropDownItems = { _itemRefresh, _itemCompile } };
        _btnTools.DropDown.ShowItemToolTips = true;

        _btnDisconnect = new ToolStripButton("Отключить") { Enabled = false };

        _toolbar = new ToolStrip
        {
            Items =
            {
                _btnOpen,
                new ToolStripSeparator(),
                _btnWorkspace,
                new ToolStripSeparator(),
                _btnCompare,
                _btnExport,
                _btnImport,
                new ToolStripSeparator(),
                _btnTools,
                new ToolStripSeparator(),
                _btnDisconnect
            }
        };

        // ── Дерево (левая панель) ─────────────────────────────────────────────
        _tree = new TreeView
        {
            Dock          = DockStyle.Fill,
            ShowLines     = true,
            ShowPlusMinus = true,
            HideSelection = false,
            Font          = new Font("Segoe UI", 9f)
        };

        // ── Лог (правая панель) ───────────────────────────────────────────────
        _log = new TextBox
        {
            Dock       = DockStyle.Fill,
            Multiline  = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly   = true,
            Font       = new Font("Consolas", 9f),
            BackColor  = Color.FromArgb(30, 30, 30),
            ForeColor  = Color.FromArgb(200, 220, 200)
        };

        // ── Компоновка ────────────────────────────────────────────────────────
        _split = new SplitContainer { Dock = DockStyle.Fill };
        _split.Panel1.Controls.Add(_tree);
        _split.Panel2.Controls.Add(_log);

        Controls.Add(_split);
        Controls.Add(_toolbar);
        Controls.Add(_statusStrip);

        // ── События ───────────────────────────────────────────────────────────
        // BeginInvoke откладывает выполнение до завершения цикла сообщений выпадающего меню,
        // иначе ShowDialog конфликтует с модальным циклом ToolStrip и вызывает зависание.
        itemConnect.Click  += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await ConnectToOpenAsync(); });
        itemOpenFile.Click += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await OpenFileAsync(); });

        itemCompareSelection.Click += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await CompareAsync(wholeProject: false); });
        itemCompareProject.Click   += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await CompareAsync(wholeProject: true); });

        itemExportSelection.Click   += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await ExportAsync(Scope.Selection); });
        itemExportDifferences.Click += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await ExportAsync(Scope.Differences); });
        itemExportDevice.Click      += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await ExportAsync(Scope.Device); });

        _itemRefresh.Click     += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await RefreshAsync(); });
        _btnWorkspace.Click    += async (_, _) => { if (SelectWorkspace()) await RefreshTreeQuietlyAsync(); };
        _btnCompare.ButtonClick += async (_, _) => await CompareAsync(wholeProject: false);
        _btnExport.ButtonClick  += async (_, _) => await ExportAsync(Scope.Selection);
        _btnImport.ButtonClick  += async (_, _) => await ImportAsync(Scope.Selection);

        itemImportSelection.Click   += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await ImportAsync(Scope.Selection); });
        itemImportDifferences.Click += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await ImportAsync(Scope.Differences); });
        itemImportDevice.Click      += (_, _) => BeginInvoke(async () => { await Task.Delay(0); await ImportAsync(Scope.Device); });
        _btnDisconnect.Click   += (_, _) => Disconnect();

        _tree.AfterSelect += (_, _) => UpdateButtons();

        Load += (_, _) =>
        {
            _split.Panel1MinSize    = 180;
            _split.Panel2MinSize    = 180;
            _split.SplitterDistance = _split.Width / 3;

            _workspace.Load();
            UpdateWorkspaceLabel();
        };

        FormClosing += (_, _) =>
        {
            var s = _session;
            _sta.RunAsync(() => s?.Dispose());
            _sta.Dispose();
        };
    }

    // ── Рабочая папка ─────────────────────────────────────────────────────────

    /// <summary>Показывает выбор рабочей папки. Возвращает true, если папка выбрана.</summary>
    private bool SelectWorkspace()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description         = "Выберите корневую папку Git-репозитория",
            SelectedPath        = _workspace.WorkspaceDir ?? "",
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;

        _workspace.Set(dlg.SelectedPath);
        _compare = new CompareResults();  // Результаты относятся к прежней папке
        UpdateWorkspaceLabel();
        Log($"Рабочая папка: {dlg.SelectedPath}");
        return true;
    }

    /// <summary>Возвращает рабочую папку, при необходимости предлагая её выбрать.</summary>
    private string? EnsureWorkspace()
    {
        if (_workspace.WorkspaceDir is null)
        {
            var answer = MessageBox.Show(
                "Рабочая папка (Git-репозиторий) не выбрана.\nВыбрать сейчас?",
                "Рабочая папка",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer == DialogResult.Yes)
                SelectWorkspace();
        }
        return _workspace.WorkspaceDir;
    }

    private void UpdateWorkspaceLabel()
    {
        if (_workspace.WorkspaceDir is { } dir)
        {
            _workspaceLabel.Text        = $"Папка: {Path.GetFileName(dir)}";
            _workspaceLabel.ForeColor   = SystemColors.ControlText;
            _workspaceLabel.ToolTipText = dir;
            Text = $"TIA Text Sync — {Path.GetFileName(dir)}";
        }
        else
        {
            _workspaceLabel.Text        = "Папка: не выбрана";
            _workspaceLabel.ForeColor   = Color.Gray;
            _workspaceLabel.ToolTipText = "";
            Text = "TIA Text Sync";
        }
    }

    // ── Вспомогательные методы ────────────────────────────────────────────────

    private void Log(string msg)
    {
        if (!IsDisposed) _log.AppendText(msg + "\r\n");
    }

    private void Status(string text) => _statusLabel.Text = text;

    /// <summary>
    /// Progress&lt;T&gt; создаётся на UI-потоке, поэтому Report() отправляет сообщения
    /// обратно в UI SynchronizationContext.
    /// </summary>
    private IProgress<string> NewProgress() => new Progress<string>(Log);

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        UpdateButtons();
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void UpdateButtons()
    {
        var hasDevice  = FindAncestorTag<Device>(_tree.SelectedNode) is not null;
        _btnOpen.Enabled       = !_isBusy;
        _btnTools.Enabled      = !_isBusy;
        _itemRefresh.Enabled   = _project is not null;
        _btnWorkspace.Enabled  = !_isBusy;
        _btnCompare.Enabled    = !_isBusy && _project is not null;
        _btnExport.Enabled     = !_isBusy && _project is not null && hasDevice;
        _btnImport.Enabled     = !_isBusy && _project is not null && hasDevice;
        _btnDisconnect.Enabled = !_isBusy && _session is not null;
    }

    private static T? FindAncestorTag<T>(TreeNode? node) where T : class
    {
        while (node is not null)
        {
            if (node.Tag is T tag) return tag;
            node = node.Parent;
        }
        return null;
    }

    private static IEnumerable<TreeNode> AllNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (var child in AllNodes(node.Nodes))
                yield return child;
        }
    }

    // Путь по подписям без отметок статуса: не меняется, когда меняется статус узла
    private static string NodePath(TreeNode node) =>
        node.Parent is null ? node.Name : $"{NodePath(node.Parent)}\n{node.Name}";

    // ── Выбор целей ───────────────────────────────────────────────────────────

    /// <summary>Цель по выбранному узлу: устройство целиком, ПЛК или набор элементов.</summary>
    private Target? SelectionTarget()
    {
        var node   = _tree.SelectedNode;
        var device = FindAncestorTag<Device>(node);
        if (node is null || device is null)
            return null;

        var plc = FindAncestorTag<PlcSoftware>(node);
        if (plc is null)
            return new Target(device, null, null, includeHw: true);  // Выбрано устройство или его HW

        if (node.Tag is PlcSoftware)
            return new Target(device, plc, null, includeHw: false);

        // Элементы ПЛК и файлы рабочей папки, которых нет в TIA
        var keys = new HashSet<string>(
            AllNodes(node.Nodes).Prepend(node).Select(n => n.Tag switch
            {
                PlcItem item          => item.Key,
                WorkspaceItem missing => missing.Key,
                _                     => null
            }).OfType<string>(),
            StringComparer.Ordinal);

        if (keys.Count == 0)
        {
            Log("В выбранной ветви нет элементов.");
            return null;
        }
        return new Target(device, plc, keys, includeHw: false);
    }

    private List<Target> ResolveTargets(Scope scope)
    {
        switch (scope)
        {
            case Scope.Differences:
                return ProjectTargets();
            case Scope.Device:
                var device = FindAncestorTag<Device>(_tree.SelectedNode);
                return device is null ? new List<Target>() : new List<Target> { new(device, null, null, includeHw: true) };
            default:
                return SelectionTarget() is { } target ? new List<Target> { target } : new List<Target>();
        }
    }

    private List<Target> ProjectTargets() =>
        _tree.Nodes.Cast<TreeNode>()
            .Select(n => n.Tag).OfType<Device>()
            .Select(d => new Target(d, null, null, includeHw: true))
            .ToList();

    /// <summary>ПЛК цели. Перечисляется на потоке StaWorker.</summary>
    private static IEnumerable<PlcSoftware> TargetPlcs(Target target)
    {
        foreach (DeviceItem item in target.Device.DeviceItems)
            if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc
                && (target.Plc is null || plc.Equals(target.Plc)))
                yield return plc;
    }

    private static List<PlcItem> Filter(List<PlcItem> all, HashSet<string>? keys) =>
        keys is null ? all : all.Where(i => keys.Contains(i.Key)).ToList();

    private static List<WorkspaceItem> Filter(List<WorkspaceItem> items, HashSet<string> keys) =>
        items.Where(i => keys.Contains(i.Key)).ToList();

    // ── Действия ──────────────────────────────────────────────────────────────

    private async Task ConnectToOpenAsync()
    {
        Info("ConnectToOpenAsync: начало");
        SetBusy(true);
        var log = NewProgress();
        try
        {
            Status("Подключение к TIA Portal...");
            _session ??= await _sta.RunAsync(() => TiaSession.Connect(withUi: true, log: log));
            Info("ConnectToOpenAsync: сессия получена");

            Status("Чтение проекта...");
            var svc = new ProjectService(_session);

            // Читаем имя проекта внутри RunAsync — все COM-свойства принадлежат потоку StaWorker
            string? projectName = null;
            _project = await _sta.RunAsync(() =>
            {
                var p = svc.GetOpenProject(log: log);
                projectName = p?.Name;
                return p;
            });

            if (_project is null) { Status("В TIA Portal нет открытых проектов"); return; }

            _compare = new CompareResults();
            Info($"ConnectToOpenAsync: проект '{projectName}'");
            Status($"Проект: {projectName}");
            await RefreshTreeAsync();
        }
        catch (Exception ex)
        {
            Error("ConnectToOpenAsync: ошибка", ex);
            Log($"Ошибка: {ex.Message}");
            Status("Ошибка подключения");
            if (_project is null) { await _sta.RunAsync(() => _session?.Dispose()); _session = null; }
        }
        finally { SetBusy(false); }
    }

    private async Task OpenFileAsync()
    {
        Info("OpenFileAsync: начало, показываем диалог выбора файла");
        using var dlg = new OpenFileDialog
        {
            Title  = "Открыть проект TIA Portal",
            Filter = "TIA Portal Project|*.ap15;*.ap15_1;*.ap16;*.ap17;*.ap18;*.ap19;*.ap20;*.ap21|Все файлы|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            Info("OpenFileAsync: диалог отменён");
            return;
        }

        var path = dlg.FileName;
        Info($"OpenFileAsync: выбран файл '{path}'");
        SetBusy(true);
        var log = NewProgress();
        try
        {
            Status("Подключение к TIA Portal...");
            _session ??= await _sta.RunAsync(() => TiaSession.Connect(withUi: true, log: log));
            Info("OpenFileAsync: сессия получена");

            Status($"Открытие {Path.GetFileName(path)}...");
            var svc = new ProjectService(_session);

            // Читаем имя проекта внутри RunAsync — все COM-свойства принадлежат потоку StaWorker
            string? projectName = null;
            _project = await _sta.RunAsync(() =>
            {
                var p = svc.OpenProject(path, log: log);
                projectName = p?.Name;
                return p;
            });
            Info($"OpenFileAsync: проект открыт '{projectName}'");

            _compare = new CompareResults();
            Status($"Проект: {projectName ?? Path.GetFileNameWithoutExtension(path)}");
            await RefreshTreeAsync();
        }
        catch (Exception ex)
        {
            Error("OpenFileAsync: ошибка", ex);
            Log($"Ошибка: {ex.Message}");
            Status("Ошибка");
            if (_project is null) { await _sta.RunAsync(() => _session?.Dispose()); _session = null; }
        }
        finally { SetBusy(false); }
    }

    private async Task RefreshAsync()
    {
        SetBusy(true);
        try   { await RefreshTreeAsync(); }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// Перестраивает дерево, не меняя строку состояния. Ошибки только пишутся в лог.
    /// Запрос во время идущего обновления выполняется сразу после него.
    /// </summary>
    private async Task RefreshTreeQuietlyAsync()
    {
        if (_project is null) return;
        if (_refreshing) { _refreshPending = true; return; }

        _refreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                await RefreshTreeAsync(quiet: true);
            }
            while (_refreshPending);
        }
        catch (Exception ex) { Error("RefreshTreeQuietlyAsync: ошибка", ex); }
        finally { _refreshing = false; }
    }

    private async Task RefreshTreeAsync(bool quiet = false)
    {
        var project = _project;
        if (project is null) return;
        if (!quiet) Status("Загрузка дерева...");

        var boldFont  = new Font(_tree.Font, FontStyle.Bold);
        var workspace = _workspace.WorkspaceDir;
        var compare   = _compare;
        var nodes     = await _sta.RunAsync(() => TreeBuilder.BuildDeviceNodes(project, boldFont, workspace, compare));
        if (_project != project) return;  // За время построения проект отключили
        if (!quiet) Info($"RefreshTreeAsync: устройств в дереве: {nodes.Count}");

        // Перестроение не должно сбрасывать раскрытые узлы, выбор и прокрутку
        var firstLoad = _tree.Nodes.Count == 0;
        var expanded  = new HashSet<string>(AllNodes(_tree.Nodes).Where(n => n.IsExpanded).Select(NodePath));
        var selected  = _tree.SelectedNode is { } s ? NodePath(s) : null;
        var top       = _tree.TopNode is { } t ? NodePath(t) : null;
        TreeNode? topNode = null;

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var node in nodes)
            _tree.Nodes.Add(node);

        foreach (var node in AllNodes(_tree.Nodes).ToList())
        {
            var path = NodePath(node);
            if (expanded.Contains(path) || (firstLoad && node.Parent is null))
                node.Expand();
            if (path == selected)
                _tree.SelectedNode = node;
            if (path == top)
                topNode = node;
        }
        _tree.EndUpdate();

        if (topNode is not null)
            _tree.TopNode = topNode;

        UpdateButtons();
        if (!quiet) Status("Готово");
    }

    private async Task CompareAsync(bool wholeProject)
    {
        var targets = wholeProject
            ? ProjectTargets()
            : SelectionTarget() is { } target ? new List<Target> { target } : new List<Target>();
        if (targets.Count == 0) return;

        var workspace = EnsureWorkspace();
        if (workspace is null) return;

        var compile = _itemCompile.Checked;  // Читается здесь: контрол принадлежит UI-потоку
        var compare = _compare;
        var log     = NewProgress();

        SetBusy(true);
        Status("Сравнение...");
        try
        {
            var (differences, notCompiled, warnings) = await _sta.RunAsync(() =>
            {
                int differences = 0, notCompiled = 0, warnings = 0;
                foreach (var target in targets)
                {
                    var deviceDir = PathNames.DeviceDir(workspace, target.Device.Name);
                    foreach (var plc in TargetPlcs(target))
                    {
                        if (compile)
                            warnings += PlcCompiler.Compile(plc, log);

                        var all      = PlcCatalog.Collect(plc);
                        var selected = Filter(all, target.Keys);
                        var result   = compare.GetOrAddPlc(PathNames.PlcDir(deviceDir, plc.Name));

                        warnings    += CompareService.ComparePlc(plc, deviceDir, all, selected, result, log);
                        differences += selected.Count(i => result.Items.TryGetValue(i.Key, out var status) && status != ItemStatus.Unchanged)
                                     + result.MissingInTia.Count;
                        notCompiled += selected.Count(i => result.Items.TryGetValue(i.Key, out var status) && status == ItemStatus.NotCompiled);
                    }

                    if (target.IncludeHw)
                    {
                        var hwChanged = CompareService.CompareHw(target.Device, deviceDir, log);
                        compare.SetHwChanged(deviceDir, hwChanged);
                        if (hwChanged) differences++;
                    }
                }
                return (differences, notCompiled, warnings);
            });

            Info($"CompareAsync: отличий {differences}, не скомпилировано {notCompiled}, ошибок {warnings}");
            Log(differences == 0 ? "Сравнение завершено: отличий нет." : $"Сравнение завершено: отличий {differences}.");
            if (notCompiled > 0)
                Log($"Не скомпилировано: {notCompiled}. Их содержимое недоступно до компиляции, " +
                    "скомпилируйте ПЛК в TIA или включите «Инструменты → Компилировать ПЛК при экспорте, сравнении и импорте».");

            var summary = differences == 0 ? "Отличий нет" : $"Отличий: {differences}";
            if (notCompiled > 0) summary += $", не скомпилировано: {notCompiled}";
            if (warnings > 0)    summary += $", ошибок: {warnings}";
            Status(summary);
        }
        catch (Exception ex)
        {
            Error("CompareAsync: ошибка", ex);
            Log($"Ошибка: {ex.Message}");
            Status("Ошибка сравнения");
        }
        finally { SetBusy(false); }

        await RefreshTreeQuietlyAsync();
    }

    private async Task ExportAsync(Scope mode)
    {
        var targets = ResolveTargets(mode);
        if (targets.Count == 0) return;

        var workspace = EnsureWorkspace();
        if (workspace is null) return;

        var compile = _itemCompile.Checked;  // Читается здесь: контрол принадлежит UI-потоку
        var compare = _compare;
        var log     = NewProgress();

        Info($"ExportAsync: режим {mode}, папка '{workspace}'");
        SetBusy(true);
        Status("Экспорт...");
        try
        {
            var (exported, warnings, compared) = await _sta.RunAsync(() =>
            {
                int exported = 0, warnings = 0;
                var compared = false;
                foreach (var target in targets)
                {
                    var deviceDir = PathNames.DeviceDir(workspace, target.Device.Name);
                    foreach (var plc in TargetPlcs(target))
                    {
                        var plcDir     = PathNames.PlcDir(deviceDir, plc.Name);
                        var comparison = compare.ForPlc(plcDir);
                        if (mode == Scope.Differences && comparison is null)
                            continue;  // ПЛК не сравнивался
                        compared |= comparison is not null;

                        var all      = PlcCatalog.Collect(plc);
                        var selected = mode == Scope.Differences
                            ? all.Where(i => comparison!.Items.TryGetValue(i.Key, out var status)
                                             && status is ItemStatus.Changed or ItemStatus.New).ToList()
                            : Filter(all, target.Keys);

                        if (mode == Scope.Differences && selected.Count == 0 && comparison!.MissingInTia.Count == 0)
                            continue;

                        if (compile)
                            warnings += PlcCompiler.Compile(plc, log);

                        Info($"ExportAsync: PLC '{plc.Name}', элементов {selected.Count} из {all.Count}");
                        var export = new ExportService(plc, deviceDir, log);
                        export.Export(all, selected);
                        warnings += export.WarningCount;
                        exported += selected.Count - export.FailedKeys.Count;

                        // Выгруженное теперь совпадает с рабочей папкой, а файлы удалённых элементов убраны
                        var result = compare.GetOrAddPlc(plcDir);
                        foreach (var item in selected.Where(i => !export.FailedKeys.Contains(i.Key)))
                            result.Items[item.Key] = ItemStatus.Unchanged;
                        result.MissingInTia = new List<WorkspaceItem>();
                    }

                    var exportHw = mode == Scope.Differences
                        ? compare.HwChanged(deviceDir) == true
                        : target.IncludeHw;
                    if (exportHw)
                    {
                        Info("ExportAsync: экспорт HW конфигурации");
                        var hwExport = new HwExportService(target.Device, deviceDir, log);
                        hwExport.Export();
                        warnings += hwExport.WarningCount;
                        compare.SetHwChanged(deviceDir, false);
                    }
                }
                return (exported, warnings, compared);
            });

            Info($"ExportAsync: завершено, элементов {exported}, предупреждений {warnings}");
            if (mode == Scope.Differences && !compared)
            {
                Log("Нет результатов сравнения. Сначала выполните «Сравнить».");
                Status("Нет результатов сравнения");
            }
            else if (warnings == 0)
            {
                Log($"Экспортировано элементов: {exported}. Папка: {workspace}");
                Status($"Готово: элементов {exported}");
            }
            else
            {
                Log($"Экспортировано элементов: {exported}. Есть ошибки ({warnings}), проверьте строки WARN.");
                Status($"Готово с предупреждениями ({warnings})");
            }
        }
        catch (Exception ex)
        {
            Error("ExportAsync: ошибка", ex);
            Log($"Ошибка: {ex.Message}");
            Status("Ошибка экспорта");
        }
        finally { SetBusy(false); }

        await RefreshTreeQuietlyAsync();
    }

    private async Task ImportAsync(Scope scope)
    {
        var targets = ResolveTargets(scope);
        if (targets.Count == 0) return;

        var workspace = EnsureWorkspace();
        if (workspace is null) return;

        var compile = _itemCompile.Checked;  // Читается здесь: контрол принадлежит UI-потоку
        var compare = _compare;
        var log     = NewProgress();

        Info($"ImportAsync: режим {scope}, папка '{workspace}'");
        SetBusy(true);
        Status("Подготовка импорта...");
        try
        {
            var plan  = await _sta.RunAsync(() => BuildImportPlan(targets, scope, workspace, compare));
            var total = plan.Sum(p => p.Items.Count);
            if (total == 0)
            {
                Log(scope == Scope.Differences
                    ? "Нечего импортировать: нет отличий по последнему сравнению. Сначала выполните «Сравнить»."
                    : "Нечего импортировать: в рабочей папке нет файлов выбранных элементов.");
                Status("Нечего импортировать");
                return;
            }

            var created = plan.Sum(p => p.NewCount);
            var answer  = MessageBox.Show(this,
                $"Загрузить в TIA Portal элементов из рабочей папки: {total}.\n" +
                $"Новых: {created}, будет перезаписано: {total - created}.\n\n" +
                "Изменения этих элементов в TIA, которых нет в рабочей папке, будут потеряны.",
                "Импорт", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer != DialogResult.OK)
            {
                Status("Импорт отменён");
                return;
            }

            Status("Импорт...");
            var (imported, differences, warnings) = await _sta.RunAsync(() =>
            {
                int imported = 0, differences = 0, warnings = 0;
                foreach (var import in plan)
                {
                    var importer = new ImportService(import.Plc, log);
                    importer.Import(import.Items);
                    warnings += importer.WarningCount;
                    imported += importer.Imported.Count;

                    if (compile)
                        warnings += PlcCompiler.Compile(import.Plc, log);

                    // Проверка: загруженное выгружается обратно и сравнивается с рабочей папкой
                    var all   = PlcCatalog.Collect(import.Plc);
                    var check = all.Where(i => importer.Imported.Any(w => w.Matches(i))).ToList();
                    if (check.Count == 0)
                        continue;

                    var result = compare.GetOrAddPlc(PathNames.PlcDir(import.DeviceDir, import.Plc.Name));
                    warnings    += CompareService.ComparePlc(import.Plc, import.DeviceDir, all, check, result, log);
                    differences += check.Count(i => result.Items.TryGetValue(i.Key, out var status) && status != ItemStatus.Unchanged);
                }
                return (imported, differences, warnings);
            });

            Info($"ImportAsync: загружено {imported}, отличается после импорта {differences}, ошибок {warnings}");
            Log($"Импорт завершён: загружено {imported} из {total}.");
            if (differences > 0)
                Log($"После импорта отличается от рабочей папки: {differences}. " +
                    "TIA сохранил эти элементы не так, как в файле, или они не скомпилированы.");

            var summary = $"Загружено: {imported}";
            if (differences > 0) summary += $", отличается после импорта: {differences}";
            if (warnings > 0)    summary += $", ошибок: {warnings}";
            Status(summary);
        }
        catch (Exception ex)
        {
            Error("ImportAsync: ошибка", ex);
            Log($"Ошибка: {ex.Message}");
            Status("Ошибка импорта");
        }
        finally { SetBusy(false); }

        await RefreshTreeQuietlyAsync();
    }

    /// <summary>
    /// Определяет, какие файлы загрузить в каждый ПЛК. Выполняется на потоке StaWorker.
    /// Для существующих элементов берутся их файлы, для отсутствующих в TIA — файлы рабочей папки
    /// без пары в ПЛК. Системные блоки и форматы, которые не импортируются, отбрасываются.
    /// </summary>
    private static List<PlcImport> BuildImportPlan(List<Target> targets, Scope scope, string workspace, CompareResults compare)
    {
        var plan = new List<PlcImport>();
        foreach (var target in targets)
        {
            var deviceDir = PathNames.DeviceDir(workspace, target.Device.Name);
            foreach (var plc in TargetPlcs(target))
            {
                var plcDir     = PathNames.PlcDir(deviceDir, plc.Name);
                var all        = PlcCatalog.Collect(plc);
                var comparison = compare.ForPlc(plcDir);
                var keys       = target.Keys;

                List<PlcItem>       existing;
                List<WorkspaceItem> missing;
                if (scope == Scope.Differences)
                {
                    if (comparison is null)
                        continue;  // ПЛК не сравнивался
                    existing = ItemsWithStatus(all, comparison, ItemStatus.Changed);
                    missing  = comparison.MissingInTia;
                }
                else if (keys is null)
                {
                    existing = all;
                    missing  = WorkspaceItem.CollectOrphans(plcDir, all);
                }
                else
                {
                    existing = Filter(all, keys);
                    missing  = comparison is null ? new List<WorkspaceItem>() : Filter(comparison.MissingInTia, keys);
                }

                var updates = existing.Where(i => !i.IsSystem)
                                      .Select(i => WorkspaceItem.ForItem(plcDir, i))
                                      .Where(w => w.ImportFile is not null)
                                      .ToList();
                var creates = missing.Where(w => !w.IsSystem && w.ImportFile is not null).ToList();

                if (updates.Count + creates.Count > 0)
                    plan.Add(new PlcImport(plc, deviceDir, updates.Concat(creates).ToList(), creates.Count));
            }
        }
        return plan;
    }

    private static List<PlcItem> ItemsWithStatus(List<PlcItem> all, PlcComparison comparison, ItemStatus status) =>
        all.Where(i => comparison.Items.TryGetValue(i.Key, out var s) && s == status).ToList();

    private void Disconnect()
    {
        var s = _session;
        _session = null;
        _ = _sta.RunAsync(() => s?.Dispose());
        _project = null;
        _compare = new CompareResults();
        _tree.Nodes.Clear();
        Log("Отключено от TIA Portal.");
        Status("Нет подключения");
        SetBusy(false);
    }
}
