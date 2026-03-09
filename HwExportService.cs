using Siemens.Engineering;
using Siemens.Engineering.HW;
using System;
using System.IO;
using System.Xml.Linq;

namespace TiaTextSync;

/// <summary>
/// Создаёт сводный XML-файл HW-конфигурации каждого устройства проекта.
///
/// Структура вывода:
///   &lt;outputDir&gt;/hw/  — по одному XML-файлу на каждое устройство
///
/// Примечание: TIA Portal Openness API V17 не предоставляет Device.Export().
/// Данный сервис строит XML-дамп из свойств, доступных через API
/// (Name, TypeIdentifier, PositionNumber, IsPlugged).
/// Назначение: отслеживание изменений оборудования в Git (diff-only).
/// </summary>
internal sealed class HwExportService
{
    private readonly Project _project;
    private readonly string _outputDir;
    private readonly IProgress<string> _progress;

    public HwExportService(Project project, string outputDir, IProgress<string> progress)
    {
        _project = project;
        _outputDir = Path.GetFullPath(outputDir);
        _progress = progress;
    }

    public void Export()
    {
        _progress.Report("Экспорт HW-конфигурации:");

        var dir = HwDir();
        var total = 0;

        foreach (Device device in _project.Devices)
        {
            total++;
            try
            {
                // Имя устройства может содержать '/' (напр. "S7-1500/ET 200MP") —
                // заменяем на '_', чтобы не создавать вложенные директории
                var safeName = string.Concat(device.Name.Split(Path.GetInvalidFileNameChars()));
                var path = Path.Combine(dir, $"{safeName}.xml");
                BuildDeviceXml(device).Save(path);
                _progress.Report($"  HW   hw/{safeName}.xml");
            }
            catch (Exception ex)
            {
                _progress.Report($"  WARN {device.Name}: {ex.Message}");
            }
        }

        if (total == 0)
            _progress.Report("  (устройств не найдено)");
    }

    private static XDocument BuildDeviceXml(Device device)
    {
        var deviceEl = new XElement("Device",
            new XAttribute("Name", device.Name),
            new XAttribute("TypeIdentifier", device.TypeIdentifier ?? ""),
            new XAttribute("IsGsd", device.IsGsd));

        foreach (DeviceItem item in device.DeviceItems)
            deviceEl.Add(BuildItemXml(item));

        return new XDocument(
            new XComment(" tia-text-sync: структура оборудования для Git-diff. Не для импорта. "),
            new XElement("HardwareConfiguration", deviceEl));
    }

    private static XElement BuildItemXml(DeviceItem item)
    {
        var el = new XElement("Module",
            new XAttribute("Name", item.Name),
            new XAttribute("TypeIdentifier", item.TypeIdentifier ?? ""),
            new XAttribute("Slot", item.PositionNumber),
            new XAttribute("IsPlugged", item.IsPlugged));

        foreach (DeviceItem sub in item.DeviceItems)
            el.Add(BuildItemXml(sub));

        return el;
    }

    private string HwDir()
    {
        var dir = Path.Combine(_outputDir, "hw");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
