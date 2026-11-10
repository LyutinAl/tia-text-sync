using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TiaTextSync.Services;

namespace TiaTextSync.UI;

/// <summary>
/// Строит дерево узлов TreeView из объектной модели TIA Portal.
/// Все методы вызываются на потоке StaWorker (там живут COM-объекты TIA).
/// TreeNode — объект данных, а не UI-контрол, поэтому его безопасно создавать вне UI-потока.
/// </summary>
internal static class TreeBuilder
{
    /// <summary>Строит узлы верхнего уровня для каждого устройства в проекте.</summary>
    public static List<TreeNode> BuildDeviceNodes(Project project, Font boldFont)
    {
        var result = new List<TreeNode>();

        foreach (var device in ProjectService.GetAllDevices(project))
        {
            var deviceNode = new TreeNode(device.Name) { Tag = device, NodeFont = boldFont };

            // HW: аппаратные модули вложены под устройство
            var hwNode = new TreeNode("HW");
            foreach (DeviceItem item in device.DeviceItems)
                hwNode.Nodes.Add(BuildDeviceItemNode(item));
            if (hwNode.Nodes.Count > 0)
                deviceNode.Nodes.Add(hwNode);

            // Программное обеспечение ПЛК
            foreach (DeviceItem item in device.DeviceItems)
                if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc)
                    deviceNode.Nodes.Add(BuildPlcNode(plc));

            result.Add(deviceNode);
        }

        return result;
    }

    private static TreeNode BuildDeviceItemNode(DeviceItem item)
    {
        var node = new TreeNode($"{item.Name}  [слот {item.PositionNumber}]") { Tag = item };
        foreach (DeviceItem sub in item.DeviceItems)
            node.Nodes.Add(BuildDeviceItemNode(sub));
        return node;
    }

    private static TreeNode BuildPlcNode(PlcSoftware plc)
    {
        var plcNode = new TreeNode(plc.Name) { Tag = plc };

        var blocksNode = new TreeNode("Блоки");
        AddBlockGroupNodes(plc.BlockGroup, blocksNode.Nodes);
        plcNode.Nodes.Add(blocksNode);

        var tagsNode = new TreeNode("Теги");
        AddTagTableNodes(plc.TagTableGroup, tagsNode.Nodes);
        if (tagsNode.Nodes.Count > 0)
            plcNode.Nodes.Add(tagsNode);

        var typesNode = new TreeNode("Типы данных");
        AddTypeNodes(plc.TypeGroup, typesNode.Nodes);
        if (typesNode.Nodes.Count > 0)
            plcNode.Nodes.Add(typesNode);

        return plcNode;
    }

    private static void AddBlockGroupNodes(PlcBlockGroup group, TreeNodeCollection target)
    {
        foreach (PlcBlock block in group.Blocks)
        {
            var kind = block switch
            {
                FB         => "FB",
                FC         => "FC",
                OB         => "OB",
                InstanceDB => "iDB",
                ArrayDB    => "aDB",
                GlobalDB   => "DB",
                _          => "?"
            };
            target.Add(new TreeNode($"{block.Name}  [{kind} · {block.ProgrammingLanguage}]") { Tag = block });
        }

        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            var subNode = new TreeNode($"[{sub.Name}]") { Tag = sub };
            AddBlockGroupNodes(sub, subNode.Nodes);
            target.Add(subNode);
        }
    }

    private static void AddTagTableNodes(PlcTagTableGroup group, TreeNodeCollection target)
    {
        foreach (PlcTagTable table in group.TagTables)
            target.Add(new TreeNode(table.Name) { Tag = table });

        foreach (PlcTagTableUserGroup sub in group.Groups)
        {
            var subNode = new TreeNode($"[{sub.Name}]") { Tag = sub };
            AddTagTableNodes(sub, subNode.Nodes);
            target.Add(subNode);
        }
    }

    private static void AddTypeNodes(PlcTypeGroup group, TreeNodeCollection target)
    {
        foreach (PlcType type in group.Types)
            target.Add(new TreeNode(type.Name) { Tag = type });

        foreach (PlcTypeUserGroup sub in group.Groups)
        {
            var subNode = new TreeNode($"[{sub.Name}]") { Tag = sub };
            AddTypeNodes(sub, subNode.Nodes);
            target.Add(subNode);
        }
    }
}
