using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Types;
using System.Collections.Generic;
using System.Linq;
using static TiaTextSync.Services.PathNames;

namespace TiaTextSync.Services;

internal enum PlcItemKind { Block, TagTable, Type, TechnologicalObject }

/// <summary>Элемент ПЛК, который экспортируется в собственный файл.</summary>
internal sealed class PlcItem
{
    public PlcItem(PlcItemKind kind, IEngineeringObject engineeringObject, string name, string root,
                   IReadOnlyList<string> groups, bool isSystem = false)
    {
        Kind     = kind;
        Object   = engineeringObject;
        Name     = name;
        Root     = root;
        Groups   = groups;
        IsSystem = isSystem;
        Folder = string.Join("/", new[] { root }.Concat(groups.Select(SafeName)));
    }

    public PlcItemKind        Kind   { get; }
    public IEngineeringObject Object { get; }
    public string             Name   { get; }

    /// <summary>Корневая папка вида элемента: blocks, tags, types или tech.</summary>
    public string Root { get; }

    /// <summary>Имена групп TIA Portal от корня к элементу.</summary>
    public IReadOnlyList<string> Groups { get; }

    /// <summary>Папка файла относительно папки ПЛК, например «blocks/Motors».</summary>
    public string Folder { get; }

    /// <summary>Системный блок: его создаёт сам TIA Portal, поэтому он не импортируется.</summary>
    public bool IsSystem { get; }

    /// <summary>Имена уникальны в пределах вида элемента внутри ПЛК.</summary>
    public string Key => $"{Kind}:{Name}";
}

/// <summary>
/// Перечисляет элементы ПЛК в порядке дерева проекта.
/// Должен выполняться на потоке <see cref="TiaTextSync.Core.StaWorker"/>.
/// </summary>
internal static class PlcCatalog
{
    public const string BlocksRoot = "blocks";
    public const string TagsRoot   = "tags";
    public const string TypesRoot  = "types";
    public const string TechRoot   = "tech";

    private static readonly List<string> NoGroups = new();

    public static List<PlcItem> Collect(PlcSoftware plc)
    {
        var items = new List<PlcItem>();
        AddBlocks(plc.BlockGroup, NoGroups, items);
        AddSystemBlocks(plc.BlockGroup.SystemBlockGroups, NoGroups, items);
        AddTagTables(plc.TagTableGroup, NoGroups, items);
        AddTypes(plc.TypeGroup, NoGroups, items);
        AddTechnologicalObjects(plc.TechnologicalObjectGroup, NoGroups, items);
        return items;
    }

    private static List<string> With(List<string> groups, string name) => new(groups) { name };

    private static void AddBlocks(PlcBlockGroup group, List<string> groups, List<PlcItem> items)
    {
        foreach (PlcBlock block in group.Blocks)
            items.Add(new PlcItem(PlcItemKind.Block, block, block.Name, BlocksRoot, groups));

        foreach (PlcBlockUserGroup sub in group.Groups)
            AddBlocks(sub, With(groups, sub.Name), items);
    }

    // Системные группы не входят в PlcBlockGroup.Groups. Верхняя группа уже называется «System blocks».
    private static void AddSystemBlocks(PlcSystemBlockGroupComposition systemGroups, List<string> groups, List<PlcItem> items)
    {
        foreach (PlcSystemBlockGroup group in systemGroups)
        {
            var path = With(groups, group.Name);
            foreach (PlcBlock block in group.Blocks)
                items.Add(new PlcItem(PlcItemKind.Block, block, block.Name, BlocksRoot, path, isSystem: true));

            AddSystemBlocks(group.Groups, path, items);
        }
    }

    private static void AddTagTables(PlcTagTableGroup group, List<string> groups, List<PlcItem> items)
    {
        foreach (PlcTagTable table in group.TagTables)
            items.Add(new PlcItem(PlcItemKind.TagTable, table, table.Name, TagsRoot, groups));

        foreach (PlcTagTableUserGroup sub in group.Groups)
            AddTagTables(sub, With(groups, sub.Name), items);
    }

    private static void AddTypes(PlcTypeGroup group, List<string> groups, List<PlcItem> items)
    {
        foreach (PlcType type in group.Types)
            items.Add(new PlcItem(PlcItemKind.Type, type, type.Name, TypesRoot, groups));

        foreach (PlcTypeUserGroup sub in group.Groups)
            AddTypes(sub, With(groups, sub.Name), items);
    }

    private static void AddTechnologicalObjects(TechnologicalInstanceDBGroup group, List<string> groups, List<PlcItem> items)
    {
        foreach (TechnologicalInstanceDB technologicalObject in group.TechnologicalObjects)
            items.Add(new PlcItem(PlcItemKind.TechnologicalObject, technologicalObject, technologicalObject.Name, TechRoot, groups));

        foreach (TechnologicalInstanceDBUserGroup sub in group.Groups)
            AddTechnologicalObjects(sub, With(groups, sub.Name), items);
    }
}
