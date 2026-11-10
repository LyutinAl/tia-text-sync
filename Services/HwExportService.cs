using Siemens.Engineering;
using Siemens.Engineering.HW;
using System;
using System.IO;
using System.Xml.Linq;
using TiaTextSync.Core;

namespace TiaTextSync.Services;

/// <summary>
/// Экспортирует аппаратную конфигурацию одного устройства в XML.
///
/// Вывод: &lt;deviceDir&gt;/hw.xml
///
/// Примечание: TIA Portal Openness V17 не предоставляет метод Device.Export().
/// Сервис строит XML-снимок из свойств, доступных через API
/// (Name, TypeIdentifier, PositionNumber, IsPlugged).
/// Назначение: отслеживание изменений оборудования в Git (только diff, не для импорта).
///
/// Должен выполняться на потоке <see cref="TiaTextSync.Core.StaWorker"/>.
/// </summary>
internal sealed class HwExportService
{
    private readonly Device            _device;
    private readonly string            _deviceDir;
    private readonly IProgress<string> _progress;

    /// <summary>Количество ошибок во время <see cref="Export"/>.</summary>
    public int WarningCount { get; private set; }

    public HwExportService(Device device, string deviceDir, IProgress<string> progress)
    {
        _device    = device;
        _deviceDir = Path.GetFullPath(deviceDir);
        _progress  = progress;
    }

    public void Export()
    {
        Directory.CreateDirectory(_deviceDir);
        var path    = Path.Combine(_deviceDir, "hw.xml");
        var relPath = Path.GetFileName(_deviceDir) + "/hw.xml";
        try
        {
            BuildDeviceXml(_device).Save(path);
            _progress.Report($"  HW   {relPath}");
        }
        catch (Exception ex)
        {
            WarningCount++;
            _progress.Report($"  WARN hw ({_device.Name}): {ex.Message}");
            Logger.Error($"HwExportService: ошибка экспорта '{_device.Name}'", ex);
        }
    }

    private static XDocument BuildDeviceXml(Device device)
    {
        var deviceEl = new XElement("Device",
            new XAttribute("Name",           device.Name),
            new XAttribute("TypeIdentifier", device.TypeIdentifier ?? ""),
            new XAttribute("IsGsd",          device.IsGsd));

        foreach (DeviceItem item in device.DeviceItems)
            deviceEl.Add(BuildItemXml(item));

        return new XDocument(
            new XComment(" tia-text-sync: снимок оборудования для Git diff. Не для импорта. "),
            new XElement("HardwareConfiguration", deviceEl));
    }

    private static XElement BuildItemXml(DeviceItem item)
    {
        var el = new XElement("Module",
            new XAttribute("Name",           item.Name),
            new XAttribute("TypeIdentifier", item.TypeIdentifier ?? ""),
            new XAttribute("Slot",           item.PositionNumber),
            new XAttribute("IsPlugged",      item.IsPlugged));

        foreach (DeviceItem sub in item.DeviceItems)
            el.Add(BuildItemXml(sub));

        return el;
    }
}
