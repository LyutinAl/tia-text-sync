using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.TechnologicalObjects;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using TiaTextSync.Services;

namespace TiaTextSync.UI;

/// <summary>
/// Строит дерево узлов TreeView из объектной модели TIA Portal и отмечает результаты
/// последнего сравнения с рабочей папкой.
/// Все методы вызываются на потоке StaWorker (там живут COM-объекты TIA).
/// TreeNode — объект данных, а не UI-контрол, поэтому его безопасно создавать вне UI-потока.
///
/// У каждого узла <see cref="TreeNode.Name"/> хранит подпись без отметки статуса:
/// по нему восстанавливаются раскрытые и выбранный узлы после перестроения дерева.
/// Узлы элементов ПЛК хранят в <see cref="TreeNode.Tag"/> объект <see cref="PlcItem"/>,
/// узлы файлов, которых нет в TIA, — <see cref="WorkspaceItem"/>, узлы групп — имя папки.
/// </summary>
internal static class TreeBuilder
{
    private static readonly Color ChangedColor = Color.FromArgb(0, 90, 200);
    private static readonly Color NewColor     = Color.FromArgb(0, 130, 0);
    private static readonly Color PendingColor = Color.FromArgb(200, 110, 0);
    private static readonly Color FailedColor  = Color.FromArgb(190, 0, 0);
    private static readonly Color MissingColor = Color.FromArgb(120, 120, 120);

    private static readonly (string Root, string Title)[] Sections =
    {
        (PlcCatalog.BlocksRoot, "Блоки"),
        (PlcCatalog.TagsRoot,   "Теги"),
        (PlcCatalog.TypesRoot,  "Типы данных"),
        (PlcCatalog.TechRoot,   "Технологические объекты")
    };

    /// <summary>
    /// Строит узлы верхнего уровня для каждого устройства в проекте.
    /// Без рабочей папки результаты сравнения не отмечаются.
    /// </summary>
    public static List<TreeNode> BuildDeviceNodes(Project project, Font boldFont, string? workspaceDir, CompareResults compare)
    {
        var result = new List<TreeNode>();

        foreach (var device in ProjectService.GetAllDevices(project))
        {
            var deviceNode = NewNode(device.Name, device);
            deviceNode.NodeFont = boldFont;
            var deviceDir = workspaceDir is null ? null : PathNames.DeviceDir(workspaceDir, device.Name);

            // HW: аппаратные модули вложены под устройство
            var hwNode = NewNode("HW", null);
            foreach (DeviceItem item in device.DeviceItems)
                hwNode.Nodes.Add(BuildDeviceItemNode(item));
            if (hwNode.Nodes.Count > 0)
            {
                if (deviceDir is not null && compare.HwChanged(deviceDir) == true)
                    ApplyStatus(hwNode, ItemStatus.Changed);
                deviceNode.Nodes.Add(hwNode);
            }

            // Программное обеспечение ПЛК
            foreach (DeviceItem item in device.DeviceItems)
                if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc)
                {
                    var comparison = deviceDir is null ? null : compare.ForPlc(PathNames.PlcDir(deviceDir, plc.Name));
                    deviceNode.Nodes.Add(BuildPlcNode(plc, comparison));
                }

            if (deviceNode.Nodes.Cast<TreeNode>().Any(n => !n.ForeColor.IsEmpty))
                deviceNode.ForeColor = ChangedColor;

            result.Add(deviceNode);
        }

        return result;
    }

    private static TreeNode NewNode(string text, object? tag) =>
        new(text) { Name = text, Tag = tag };

    private static TreeNode BuildDeviceItemNode(DeviceItem item)
    {
        var node = NewNode($"{item.Name}  [слот {item.PositionNumber}]", item);
        foreach (DeviceItem sub in item.DeviceItems)
            node.Nodes.Add(BuildDeviceItemNode(sub));
        return node;
    }

    private static TreeNode BuildPlcNode(PlcSoftware plc, PlcComparison? comparison)
    {
        var plcNode = NewNode(plc.Name, plc);
        var items   = PlcCatalog.Collect(plc);

        var differences = 0;
        foreach (var (root, title) in Sections)
        {
            var section = NewNode(title, null);
            foreach (var item in items.Where(i => i.Root == root))
                AddItemNode(section, item, comparison);

            foreach (var missing in comparison?.MissingInTia.Where(w => w.Root == root) ?? Enumerable.Empty<WorkspaceItem>())
                AddMissingNode(section, missing);

            if (section.Nodes.Count == 0)
                continue;

            differences += MarkGroups(section);
            plcNode.Nodes.Add(section);
        }

        if (differences > 0)
        {
            plcNode.Text      = $"{plc.Name}  (отличий: {differences})";
            plcNode.ForeColor = ChangedColor;
        }

        return plcNode;
    }

    private static void AddItemNode(TreeNode section, PlcItem item, PlcComparison? comparison)
    {
        // Узлы групп создаются по пути элемента, порядок совпадает с деревом проекта TIA
        var parent = section;
        foreach (var group in item.Groups)
            parent = GroupNode(parent, PathNames.SafeName(group), $"[{group}]");

        var node = NewNode(ItemLabel(item), item);
        if (comparison is not null && comparison.Items.TryGetValue(item.Key, out var status))
            ApplyStatus(node, status);
        parent.Nodes.Add(node);
    }

    /// <summary>Узел для файлов рабочей папки, которых нет в ПЛК. Его можно выбрать для импорта.</summary>
    private static void AddMissingNode(TreeNode section, WorkspaceItem missing)
    {
        var parent = section;
        foreach (var folder in missing.Groups)
            parent = GroupNode(parent, folder, $"[{folder}]");

        var node = NewNode(missing.Stem, missing);
        node.Text      = $"{missing.Stem}  • нет в TIA";
        node.ForeColor = MissingColor;
        parent.Nodes.Add(node);
    }

    /// <summary>
    /// Находит или создаёт узел группы. Узлы групп хранят в Tag имя папки:
    /// по нему к группе проекта присоединяются файлы, которых нет в TIA.
    /// </summary>
    private static TreeNode GroupNode(TreeNode parent, string folder, string label)
    {
        var existing = parent.Nodes.Cast<TreeNode>()
            .FirstOrDefault(n => n.Tag is string tag && string.Equals(tag, folder, System.StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var node = NewNode(label, folder);
        parent.Nodes.Add(node);
        return node;
    }

    private static string ItemLabel(PlcItem item) => item.Object switch
    {
        TechnologicalInstanceDB technologicalObject => $"{item.Name}  [{technologicalObject.OfSystemLibElement}]",
        PlcBlock block                              => $"{item.Name}  [{BlockKind(block)} · {block.ProgrammingLanguage}]",
        _                                           => item.Name
    };

    private static string BlockKind(PlcBlock block) => block switch
    {
        FB         => "FB",
        FC         => "FC",
        OB         => "OB",
        InstanceDB => "iDB",
        ArrayDB    => "aDB",
        GlobalDB   => "DB",
        _          => "?"
    };

    private static void ApplyStatus(TreeNode node, ItemStatus status)
    {
        var (note, color) = status switch
        {
            ItemStatus.Changed => ("отличается", ChangedColor),
            ItemStatus.New         => ("нет в папке", NewColor),
            ItemStatus.NotCompiled => ("не скомпилирован", PendingColor),
            ItemStatus.Failed  => ("ошибка экспорта", FailedColor),
            _                  => ((string?)null, Color.Empty)
        };
        if (note is null)
            return;

        node.Text      = $"{node.Name}  • {note}";
        node.ForeColor = color;
    }

    /// <summary>Подписывает группы числом отмеченных элементов внутри и возвращает это число.</summary>
    private static int MarkGroups(TreeNode node)
    {
        if (node.Tag is PlcItem or WorkspaceItem)
            return node.ForeColor.IsEmpty ? 0 : 1;

        var count = node.Nodes.Cast<TreeNode>().Sum(MarkGroups);
        if (count > 0)
        {
            node.Text      = $"{node.Name}  ({count})";
            node.ForeColor = ChangedColor;
        }
        return count;
    }
}
