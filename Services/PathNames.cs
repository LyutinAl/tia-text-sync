using System.IO;

namespace TiaTextSync.Services;

/// <summary>Имена файлов и папок рабочей папки, общие для экспорта и дерева проекта.</summary>
internal static class PathNames
{
    /// <summary>Приводит имя из TIA Portal к допустимому имени файла или папки Windows.</summary>
    public static string SafeName(string name)
    {
        // Windows отбрасывает завершающие точки и пробелы в именах
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).TrimEnd(' ', '.');
        return safe.Length > 0 ? safe : "_";
    }

    public static string DeviceDir(string workspaceDir, string deviceName) =>
        Path.Combine(workspaceDir, SafeName(deviceName));

    // Openness требует абсолютные пути
    public static string PlcDir(string deviceDir, string plcName) =>
        Path.Combine(Path.GetFullPath(deviceDir), SafeName(plcName));
}
