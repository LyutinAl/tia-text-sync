using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TiaTextSync;

/// <summary>
/// Читает блоки из открытого TIA-проекта.
/// </summary>
internal sealed class ProjectReader
{
    private readonly TiaPortal _tia;

    public ProjectReader(TiaSession session) => _tia = session.Portal;

    /// <summary>Открывает проект (или возвращает уже открытый).</summary>
    public Project OpenProject(string projectPath) =>
        FindOpenProject(projectPath) ?? _tia.Projects.Open(new FileInfo(projectPath));

    /// <summary>Возвращает все ПЛК из проекта.</summary>
    public static IEnumerable<PlcSoftware> GetAllPlc(Project project)
    {
        foreach (Device device in project.Devices)
            foreach (DeviceItem item in device.DeviceItems)
                if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc)
                    yield return plc;
    }

    /// <summary>Открывает проект (или использует уже открытый) и возвращает все блоки всех ПЛК.</summary>
    public IReadOnlyList<BlockInfo> ReadBlocks(string projectPath)
    {
        var project = OpenProject(projectPath);
        var result = new List<BlockInfo>();

        foreach (var plc in GetAllPlc(project))
            CollectBlocks(plc.Name, plc.BlockGroup, groupPath: "", result);

        return result;
    }

    /// <summary>Ищет уже открытый в TIA Portal проект по пути к файлу.</summary>
    private Project? FindOpenProject(string projectPath)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var found = _tia.Projects
            .FirstOrDefault(p => string.Equals(
                Path.GetFullPath(p.Path.FullName),
                fullPath,
                StringComparison.OrdinalIgnoreCase));

        if (found is not null)
            Console.WriteLine("Проект уже открыт в TIA Portal, используем его.");

        return found;
    }

    private static void CollectBlocks(
        string plcName,
        PlcBlockGroup group,
        string groupPath,
        List<BlockInfo> result)
    {
        foreach (PlcBlock block in group.Blocks)
        {
            result.Add(new BlockInfo(
                PlcName: plcName,
                GroupPath: groupPath,
                Name: block.Name,
                BlockKind: GetBlockKind(block),
                Language: block.ProgrammingLanguage,
                Number: block.Number));
        }

        foreach (PlcBlockUserGroup sub in group.Groups)
        {
            var subPath = string.IsNullOrEmpty(groupPath)
                ? sub.Name
                : $"{groupPath}/{sub.Name}";

            CollectBlocks(plcName, sub, subPath, result);
        }
    }

    private static string GetBlockKind(PlcBlock block) => block switch
    {
        FB         => "FB",
        FC         => "FC",
        OB         => "OB",
        InstanceDB => "InstanceDB",
        ArrayDB    => "ArrayDB",
        GlobalDB   => "GlobalDB",
        _          => block.GetType().Name
    };
}
