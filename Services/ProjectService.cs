using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TiaTextSync.Core;

namespace TiaTextSync.Services;

/// <summary>
/// Открывает проекты TIA Portal и перечисляет их содержимое.
/// Все методы должны вызываться на потоке <see cref="StaWorker"/>.
/// </summary>
internal sealed class ProjectService
{
    private readonly TiaPortal _tia;

    public ProjectService(TiaSession session) => _tia = session.Portal;

    /// <summary>Возвращает первый уже открытый в TIA Portal проект или null.</summary>
    public Project? GetOpenProject(IProgress<string>? log = null)
    {
        var project = _tia.Projects.OfType<Project>().FirstOrDefault();
        if (project is null)
            log?.Report("В TIA Portal нет открытых проектов.");
        else
            log?.Report($"Подключаемся к проекту: {project.Name}");
        return project;
    }

    /// <summary>Открывает проект по пути <paramref name="projectPath"/>, повторно использует уже открытый.</summary>
    public Project OpenProject(string projectPath, IProgress<string>? log = null) =>
        FindOpenProject(projectPath, log) ?? _tia.Projects.Open(new FileInfo(projectPath));

    /// <summary>
    /// Возвращает все устройства проекта: с верхнего уровня, из папок (на любой глубине)
    /// и из системной группы несгруппированных устройств.
    /// </summary>
    public static IReadOnlyList<Device> GetAllDevices(Project project)
    {
        var devices = new List<Device>();

        foreach (Device device in project.Devices)
            devices.Add(device);

        foreach (DeviceUserGroup group in project.DeviceGroups)
            AddGroupDevices(group, devices);

        foreach (Device device in project.UngroupedDevicesGroup.Devices)
            devices.Add(device);

        return devices.Distinct().ToList();
    }

    private static void AddGroupDevices(DeviceUserGroup group, List<Device> devices)
    {
        foreach (Device device in group.Devices)
            devices.Add(device);

        foreach (DeviceUserGroup sub in group.Groups)
            AddGroupDevices(sub, devices);
    }

    /// <summary>Возвращает все объекты ПО ПЛК по всем устройствам проекта.</summary>
    public static IEnumerable<PlcSoftware> GetAllPlc(Project project)
    {
        foreach (var device in GetAllDevices(project))
            foreach (DeviceItem item in device.DeviceItems)
                if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc)
                    yield return plc;
    }

    /// <summary>Возвращает пары (устройство, список ПЛК) для всех устройств. Используется при экспорте.</summary>
    public static IEnumerable<(Device Device, IReadOnlyList<PlcSoftware> Plcs)> GetDeviceGroups(Project project)
    {
        foreach (var device in GetAllDevices(project))
        {
            var plcs = new List<PlcSoftware>();
            foreach (DeviceItem item in device.DeviceItems)
                if (item.GetService<SoftwareContainer>()?.Software is PlcSoftware plc)
                    plcs.Add(plc);
            yield return (device, plcs);
        }
    }

    private Project? FindOpenProject(string projectPath, IProgress<string>? log)
    {
        var fullPath = Path.GetFullPath(projectPath);
        var found    = _tia.Projects.FirstOrDefault(p =>
            string.Equals(Path.GetFullPath(p.Path.FullName), fullPath, StringComparison.OrdinalIgnoreCase));

        if (found is not null)
            log?.Report("Проект уже открыт в TIA Portal, используем его.");

        return found;
    }
}
