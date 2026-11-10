using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using TiaTextSync.Core;
using TiaTextSync.Services;
using static TiaTextSync.Core.Logger;

namespace TiaTextSync.UI;

internal sealed class MainForm : Form
{
    // ── Состояние ─────────────────────────────────────────────────────────────
    private readonly StaWorker        _sta       = new();
    private readonly WorkspaceService _workspace = new();
    private TiaSession? _session;
    private Project?    _project;
    private bool        _isBusy;

    // ── Элементы управления ───────────────────────────────────────────────────
    private readonly ToolStrip            _toolbar;
    private readonly SplitContainer       _split;
    private readonly TreeView             _tree;
    private readonly TextBox              _log;
    private readonly StatusStrip          _statusStrip;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _workspaceLabel;

    private readonly ToolStripDropDownButton _btnOpen;
    private readonly ToolStripButton         _btnRefresh;
    private readonly ToolStripButton         _btnWorkspace;
    private readonly ToolStripButton         _btnExport;
    private readonly ToolStripButton         _btnImport;
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

        _btnRefresh = new ToolStripButton("Обновить") { Enabled = false };

        _btnWorkspace = new ToolStripButton("Рабочая папка...");

        _btnExport = new ToolStripButton("Экспорт")
        {
            Enabled     = false,
            ToolTipText = "Экспортировать выбранное устройство в рабочую папку"
        };

        _btnImport = new ToolStripButton("Импорт")
        {
            Enabled     = false,
            ToolTipText = "Импортировать изменения из рабочей папки в TIA (в разработке)"
        };

        _btnDisconnect = new ToolStripButton("Отключить") { Enabled = false };

        _toolbar = new ToolStrip
        {
            Items =
            {
                _btnOpen,
                _btnRefresh,
                new ToolStripSeparator(),
                _btnWorkspace,
                new ToolStripSeparator(),
                _btnExport,
                _btnImport,
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

        _btnRefresh.Click    += async (_, _) => await RefreshAsync();
        _btnWorkspace.Click  += (_, _) => SelectWorkspace();
        _btnExport.Click     += async (_, _) => await ExportDeviceAsync();
        _btnImport.Click     += (_, _) => MessageBox.Show(
                                    "Импорт пока в разработке.", "Импорт",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        _btnDisconnect.Click += (_, _) => Disconnect();

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

    private void SelectWorkspace()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description         = "Выберите корневую папку Git-репозитория",
            SelectedPath        = _workspace.WorkspaceDir ?? "",
            ShowNewFolderButton = true
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _workspace.Set(dlg.SelectedPath);
        UpdateWorkspaceLabel();
        Log($"Рабочая папка: {dlg.SelectedPath}");
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
        var hasDevice  = FindSelectedDevice() is not null;
        _btnOpen.Enabled       = !_isBusy;
        _btnRefresh.Enabled    = !_isBusy && _project is not null;
        _btnWorkspace.Enabled  = !_isBusy;
        _btnExport.Enabled     = !_isBusy && _project is not null && hasDevice;
        _btnDisconnect.Enabled = !_isBusy && _session is not null;
        // Импорт будет включён, когда появится реализация
        _btnImport.Enabled     = false;
    }

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

    private async Task RefreshTreeAsync()
    {
        if (_project is null) return;
        Status("Загрузка дерева...");

        var boldFont = new Font(_tree.Font, FontStyle.Bold);
        var nodes    = await _sta.RunAsync(() => TreeBuilder.BuildDeviceNodes(_project, boldFont));
        Info($"RefreshTreeAsync: устройств в дереве: {nodes.Count}");

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        foreach (var node in nodes) { _tree.Nodes.Add(node); node.Expand(); }
        _tree.EndUpdate();
        Status("Готово");
    }

    private async Task ExportDeviceAsync()
    {
        var device = FindSelectedDevice();
        if (device is null) return;

        // Читаем имя устройства на потоке StaWorker — COM-объект принадлежит ему
        var deviceName = await _sta.RunAsync(() => device.Name);
        Info($"ExportDeviceAsync: устройство '{deviceName}'");

        // Если рабочая папка не задана — предложить выбрать
        if (_workspace.WorkspaceDir is null)
        {
            var answer = MessageBox.Show(
                "Рабочая папка (Git-репозиторий) не выбрана.\nВыбрать сейчас?",
                "Рабочая папка",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer == DialogResult.Yes)
                SelectWorkspace();

            if (_workspace.WorkspaceDir is null) return;
        }

        var safeName  = string.Concat(deviceName.Split(Path.GetInvalidFileNameChars()));
        var deviceDir = Path.Combine(_workspace.WorkspaceDir, safeName);
        var log       = NewProgress();

        SetBusy(true);
        Status($"Экспорт {deviceName}...");
        try
        {
            Info($"ExportDeviceAsync: папка вывода '{deviceDir}'");

            var warnings = await _sta.RunAsync(() =>
            {
                var count = 0;
                foreach (DeviceItem item in device.DeviceItems)
                    if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc)
                    {
                        Info($"ExportDeviceAsync: экспорт PLC '{plc.Name}'");
                        var export = new ExportService(plc, deviceDir, log);
                        export.Export();
                        count += export.WarningCount;
                    }

                Info("ExportDeviceAsync: экспорт HW конфигурации");
                var hwExport = new HwExportService(device, deviceDir, log);
                hwExport.Export();
                return count + hwExport.WarningCount;
            });

            Info($"ExportDeviceAsync: завершено '{deviceName}', предупреждений: {warnings}");
            if (warnings == 0)
            {
                Status($"Готово: {deviceName}");
                Log($"Экспортировано в: {deviceDir}");
            }
            else
            {
                Status($"Готово с предупреждениями ({warnings}): {deviceName}");
                Log($"Экспортировано в: {deviceDir}. Есть ошибки ({warnings}), проверьте строки WARN.");
            }
        }
        catch (Exception ex)
        {
            Error("ExportDeviceAsync: ошибка", ex);
            Log($"Ошибка: {ex.Message}");
            Status("Ошибка экспорта");
        }
        finally { SetBusy(false); }
    }

    private Device? FindSelectedDevice()
    {
        var node = _tree.SelectedNode;
        while (node is not null)
        {
            if (node.Tag is Device d) return d;
            node = node.Parent;
        }
        return null;
    }

    private void Disconnect()
    {
        var s = _session;
        _session = null;
        _ = _sta.RunAsync(() => s?.Dispose());
        _project = null;
        _tree.Nodes.Clear();
        Log("Отключено от TIA Portal.");
        Status("Нет подключения");
        SetBusy(false);
    }
}
