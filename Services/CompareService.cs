using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaTextSync.Core;
using static TiaTextSync.Services.PathNames;

namespace TiaTextSync.Services;

internal enum ItemStatus
{
    /// <summary>Файлы элемента совпадают с рабочей папкой.</summary>
    Unchanged,
    /// <summary>Файлы элемента отличаются от рабочей папки.</summary>
    Changed,
    /// <summary>В рабочей папке нет файлов элемента.</summary>
    New,
    /// <summary>Элемент изменён в TIA и не скомпилирован, поэтому его содержимое недоступно.</summary>
    NotCompiled,
    /// <summary>Элемент не удалось экспортировать для сравнения.</summary>
    Failed
}

/// <summary>Результат последнего сравнения одного ПЛК с рабочей папкой.</summary>
internal sealed class PlcComparison
{
    /// <summary>Статусы сравнённых элементов по <see cref="PlcItem.Key"/>.</summary>
    public Dictionary<string, ItemStatus> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>Число элементов, файлы которых есть в рабочей папке, но которых нет в ПЛК.</summary>
    public int MissingInTia { get; set; }
}

/// <summary>
/// Результаты сравнений за сессию: по папке ПЛК и по папке устройства (для HW).
/// Используется только на потоке <see cref="StaWorker"/>.
/// </summary>
internal sealed class CompareResults
{
    private readonly Dictionary<string, PlcComparison> _plcs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool>          _hw   = new(StringComparer.OrdinalIgnoreCase);

    public PlcComparison? ForPlc(string plcDir) =>
        _plcs.TryGetValue(Path.GetFullPath(plcDir), out var result) ? result : null;

    public PlcComparison GetOrAddPlc(string plcDir)
    {
        var key = Path.GetFullPath(plcDir);
        if (!_plcs.TryGetValue(key, out var result))
            _plcs[key] = result = new PlcComparison();
        return result;
    }

    /// <summary>true — снимок HW отличается, false — совпадает, null — не сравнивался.</summary>
    public bool? HwChanged(string deviceDir) =>
        _hw.TryGetValue(Path.GetFullPath(deviceDir), out var changed) ? changed : null;

    public void SetHwChanged(string deviceDir, bool changed) =>
        _hw[Path.GetFullPath(deviceDir)] = changed;
}

/// <summary>
/// Сравнивает проект TIA Portal с рабочей папкой по содержимому: элементы экспортируются
/// во временную папку тем же <see cref="ExportService"/>, затем файлы сравниваются с рабочей папкой.
/// Должен выполняться на потоке <see cref="StaWorker"/>.
/// </summary>
internal static class CompareService
{
    /// <summary>
    /// Сравнивает <paramref name="selected"/> и записывает статусы в <paramref name="result"/>.
    /// Возвращает число ошибок, не считая нескомпилированных элементов: они получают свой статус.
    /// </summary>
    public static int ComparePlc(
        PlcSoftware plc, string deviceDir, IReadOnlyList<PlcItem> all, IReadOnlyCollection<PlcItem> selected,
        PlcComparison result, IProgress<string> progress)
    {
        progress.Report($"Сравнение ПЛК: {plc.Name}, элементов {selected.Count}");
        var tempDeviceDir = NewTempDir();
        try
        {
            var export = new ExportService(plc, tempDeviceDir, new WarningsOnly(progress));
            export.Export(all, selected);

            var tempRoot = PlcDir(tempDeviceDir, plc.Name);
            var workRoot = PlcDir(deviceDir, plc.Name);

            foreach (var item in selected)
            {
                if (export.FailedKeys.Contains(item.Key))
                {
                    result.Items[item.Key] = export.NotCompiledKeys.Contains(item.Key) ? ItemStatus.NotCompiled : ItemStatus.Failed;
                    continue;
                }

                var exported = ItemFiles(tempRoot, item);
                if (exported.Count == 0)
                {
                    // Элемент пропущен (например, защищённый блок): сравнивать нечего
                    result.Items.Remove(item.Key);
                    continue;
                }

                var existing = ItemFiles(workRoot, item);
                var status   = existing.Count == 0 ? ItemStatus.New
                             : SameFiles(exported, existing) ? ItemStatus.Unchanged
                             : ItemStatus.Changed;

                result.Items[item.Key] = status;
                if (status != ItemStatus.Unchanged)
                    progress.Report($"  {(status == ItemStatus.New ? "NEW " : "DIFF")} {plc.Name}/{item.Folder}/{item.Name}");
            }

            result.MissingInTia = CountMissingInTia(workRoot, all);
            return export.WarningCount - export.NotCompiledKeys.Count;
        }
        finally
        {
            DeleteTempDir(tempDeviceDir);
        }
    }

    /// <summary>Сравнивает снимок HW устройства с hw.xml рабочей папки. Возвращает true, если они отличаются.</summary>
    public static bool CompareHw(Device device, string deviceDir, IProgress<string> progress)
    {
        var tempDeviceDir = NewTempDir();
        try
        {
            new HwExportService(device, tempDeviceDir, new WarningsOnly(progress)).Export();

            var exported = Path.Combine(tempDeviceDir, "hw.xml");
            var existing = Path.Combine(deviceDir, "hw.xml");
            var changed  = !File.Exists(existing) || !SameContent(exported, existing);
            if (changed)
                progress.Report($"  DIFF {Path.GetFileName(deviceDir)}/hw.xml");
            return changed;
        }
        finally
        {
            DeleteTempDir(tempDeviceDir);
        }
    }

    /// <summary>Файлы элемента в его папке, по расширению.</summary>
    private static Dictionary<string, string> ItemFiles(string plcDir, PlcItem item)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir   = ExportService.ItemDir(plcDir, item);
        if (!Directory.Exists(dir))
            return files;

        var name = SafeName(item.Name);
        foreach (var file in Directory.GetFiles(dir, name + ".*"))
        {
            var ext = Path.GetExtension(file);
            if (string.Equals(Path.GetFileNameWithoutExtension(file), name, StringComparison.OrdinalIgnoreCase)
                && ExportService.ManagedExtensions.Contains(ext))
                files[ext] = file;
        }
        return files;
    }

    private static bool SameFiles(Dictionary<string, string> exported, Dictionary<string, string> existing) =>
        exported.Count == existing.Count
        && exported.All(e => existing.TryGetValue(e.Key, out var other) && SameContent(e.Value, other));

    /// <summary>Сравнение без учёта CR: Git может менять окончания строк при checkout.</summary>
    private static bool SameContent(string left, string right) =>
        WithoutCarriageReturns(File.ReadAllBytes(left)).SequenceEqual(WithoutCarriageReturns(File.ReadAllBytes(right)));

    private static IEnumerable<byte> WithoutCarriageReturns(byte[] bytes) =>
        bytes.Where(b => b != (byte)'\r');

    /// <summary>Число элементов, файлы которых лежат в рабочей папке, но которых нет в ПЛК.</summary>
    private static int CountMissingInTia(string workRoot, IReadOnlyList<PlcItem> all)
    {
        var owned = new HashSet<string>(
            all.Select(i => Path.Combine(ExportService.ItemDir(workRoot, i), SafeName(i.Name))),
            StringComparer.OrdinalIgnoreCase);

        var orphans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ExportService.ItemRoots.Concat(ExportService.LegacyDirNames))
        {
            var dir = Path.Combine(workRoot, root);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!ExportService.ManagedExtensions.Contains(Path.GetExtension(file)))
                    continue;

                var stem = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
                if (!owned.Contains(stem))
                    orphans.Add(stem);
            }
        }
        return orphans.Count;
    }

    private static string NewTempDir() =>
        Path.Combine(Path.GetTempPath(), "TiaTextSync", "compare-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTempDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"CompareService: не удалось удалить временную папку '{dir}': {ex.Message}");
        }
    }

    /// <summary>Пропускает в лог только предупреждения: строки экспорта во временную папку не нужны.</summary>
    private sealed class WarningsOnly : IProgress<string>
    {
        private readonly IProgress<string> _inner;

        public WarningsOnly(IProgress<string> inner) => _inner = inner;

        public void Report(string value)
        {
            if (value.StartsWith("  WARN", StringComparison.Ordinal))
                _inner.Report(value);
        }
    }
}
