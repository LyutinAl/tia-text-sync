using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaTextSync.Core;
using static TiaTextSync.Services.PathNames;

namespace TiaTextSync.Services;

/// <summary>
/// Загружает элементы из рабочей папки в ПЛК проекта TIA Portal.
///
///   blocks/*.xml               → PlcBlockComposition.Import
///   blocks/*.scl, *.awl, *.db  → внешний исходник + GenerateBlocksFromSource
///   types/*.udt                → внешний исходник + GenerateBlocksFromSource
///   tags/*.xml                 → PlcTagTableComposition.Import
///   tech/*.xml                 → TechnologicalInstanceDBComposition.Import
///
/// Существующие элементы перезаписываются. Группы проекта ищутся по именам папок
/// и создаются, если их нет. Элементы загружаются в порядке зависимостей:
/// UDT, таблицы тегов, кодовые блоки, DB, технологические объекты.
///
/// Должен выполняться на потоке <see cref="TiaTextSync.Core.StaWorker"/>.
/// </summary>
internal sealed class ImportService
{
    private readonly PlcSoftware       _plc;
    private readonly IProgress<string> _progress;
    private readonly List<WorkspaceItem> _imported = new();

    public ImportService(PlcSoftware plc, IProgress<string> progress)
    {
        _plc      = plc;
        _progress = progress;
    }

    /// <summary>Количество элементов, загрузка которых завершилась ошибкой.</summary>
    public int WarningCount { get; private set; }

    /// <summary>Успешно загруженные элементы.</summary>
    public IReadOnlyList<WorkspaceItem> Imported => _imported;

    public void Import(IReadOnlyCollection<WorkspaceItem> items)
    {
        _progress.Report($"Импорт в ПЛК: {_plc.Name}, элементов {items.Count}");
        foreach (var item in items.OrderBy(ImportOrder))
            ImportItem(item);
    }

    private static int ImportOrder(WorkspaceItem item) => item.Root switch
    {
        PlcCatalog.TypesRoot  => 0,
        PlcCatalog.TagsRoot   => 1,
        PlcCatalog.BlocksRoot => string.Equals(Path.GetExtension(item.ImportFile), ".db", StringComparison.OrdinalIgnoreCase) ? 3 : 2,
        _                     => 4
    };

    private void ImportItem(WorkspaceItem item)
    {
        var file = item.ImportFile;
        if (item.IsSystem || file is null)
        {
            _progress.Report($"  SKIP {Display(item)}: {(item.IsSystem ? "системные блоки создаёт TIA Portal" : "формат не импортируется")}");
            return;
        }

        try
        {
            var path = new FileInfo(file);
            switch (item.Root)
            {
                case PlcCatalog.BlocksRoot:
                    var blockGroup = BlockGroup(item.Groups);
                    if (string.Equals(path.Extension, ".xml", StringComparison.OrdinalIgnoreCase))
                        blockGroup.Blocks.Import(path, ImportOptions.Override);
                    else
                        GenerateFromSource(file, source =>
                        {
                            if (blockGroup is PlcBlockUserGroup userGroup)
                                source.GenerateBlocksFromSource(userGroup, GenerateBlockOption.None);
                            else
                                source.GenerateBlocksFromSource(GenerateBlockOption.None);
                        });
                    break;

                case PlcCatalog.TypesRoot:
                    var typeGroup = TypeGroup(item.Groups);
                    GenerateFromSource(file, source =>
                    {
                        if (typeGroup is PlcTypeUserGroup userGroup)
                            source.GenerateBlocksFromSource(userGroup, GenerateBlockOption.None);
                        else
                            source.GenerateBlocksFromSource(GenerateBlockOption.None);
                    });
                    break;

                case PlcCatalog.TagsRoot:
                    TagTableGroup(item.Groups).TagTables.Import(path, ImportOptions.Override);
                    break;

                case PlcCatalog.TechRoot:
                    TechnologicalObjectGroup(item.Groups).TechnologicalObjects.Import(path, ImportOptions.Override);
                    break;

                default:
                    _progress.Report($"  SKIP {Display(item)}: неизвестная папка");
                    return;
            }

            _imported.Add(item);
            _progress.Report($"  IMP  {Display(item)}{path.Extension}");
        }
        catch (Exception ex)
        {
            WarningCount++;
            _progress.Report($"  WARN {Display(item)}: {ex.Message}");
            Logger.Error($"ImportService: ошибка импорта '{_plc.Name}/{item.Folder}/{item.Stem}'", ex);
        }
    }

    /// <summary>
    /// Загружает исходник через временный внешний источник: TIA Portal генерирует блоки только из него.
    /// Источник удаляется в любом случае, чтобы не засорять проект.
    /// </summary>
    private void GenerateFromSource(string file, Action<PlcExternalSource> generate)
    {
        var name   = $"TiaTextSync_{Guid.NewGuid():N}{Path.GetExtension(file)}";
        var source = _plc.ExternalSourceGroup.ExternalSources.CreateFromFile(name, file);
        try
        {
            generate(source);
        }
        finally
        {
            source.Delete();
        }
    }

    // ── Группы ───────────────────────────────────────────────────────────────
    // Папка названа по имени группы через SafeName, поэтому группа ищется по тому же преобразованию.

    private PlcBlockGroup BlockGroup(IReadOnlyList<string> folders)
    {
        PlcBlockGroup group = _plc.BlockGroup;
        foreach (var folder in folders)
            group = group.Groups.Cast<PlcBlockUserGroup>().FirstOrDefault(g => SameFolder(g.Name, folder))
                    ?? CreateGroup(folder, () => group.Groups.Create(folder));
        return group;
    }

    private PlcTypeGroup TypeGroup(IReadOnlyList<string> folders)
    {
        PlcTypeGroup group = _plc.TypeGroup;
        foreach (var folder in folders)
            group = group.Groups.Cast<PlcTypeUserGroup>().FirstOrDefault(g => SameFolder(g.Name, folder))
                    ?? CreateGroup(folder, () => group.Groups.Create(folder));
        return group;
    }

    private PlcTagTableGroup TagTableGroup(IReadOnlyList<string> folders)
    {
        PlcTagTableGroup group = _plc.TagTableGroup;
        foreach (var folder in folders)
            group = group.Groups.Cast<PlcTagTableUserGroup>().FirstOrDefault(g => SameFolder(g.Name, folder))
                    ?? CreateGroup(folder, () => group.Groups.Create(folder));
        return group;
    }

    private TechnologicalInstanceDBGroup TechnologicalObjectGroup(IReadOnlyList<string> folders)
    {
        TechnologicalInstanceDBGroup group = _plc.TechnologicalObjectGroup;
        foreach (var folder in folders)
            group = group.Groups.Cast<TechnologicalInstanceDBUserGroup>().FirstOrDefault(g => SameFolder(g.Name, folder))
                    ?? CreateGroup(folder, () => group.Groups.Create(folder));
        return group;
    }

    private T CreateGroup<T>(string name, Func<T> create)
    {
        var group = create();
        _progress.Report($"  GRP  {_plc.Name}: создана группа «{name}»");
        return group;
    }

    private static bool SameFolder(string groupName, string folder) =>
        string.Equals(SafeName(groupName), folder, StringComparison.OrdinalIgnoreCase);

    private string Display(WorkspaceItem item) => $"{_plc.Name}/{item.Folder}/{item.Stem}";
}
