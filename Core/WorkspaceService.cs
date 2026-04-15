using System;
using System.IO;

namespace TiaTextSync.Core;

/// <summary>
/// Управляет путём к рабочей папке (корень Git-репозитория).
/// Путь сохраняется в %AppData%\TiaTextSync\workspace.txt между запусками.
/// </summary>
internal sealed class WorkspaceService
{
    private static readonly string ConfigFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TiaTextSync", "workspace.txt");

    /// <summary>Текущий путь к рабочей папке или null, если не задан.</summary>
    public string? WorkspaceDir { get; private set; }

    /// <summary>Загружает сохранённый путь при старте приложения.</summary>
    public void Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var saved = File.ReadAllText(ConfigFile).Trim();
                if (Directory.Exists(saved))
                {
                    WorkspaceDir = saved;
                    Logger.Info($"WorkspaceService: загружен путь '{WorkspaceDir}'");
                }
                else
                {
                    Logger.Warn($"WorkspaceService: сохранённый путь не существует, сброс: '{saved}'");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("WorkspaceService: ошибка загрузки конфига", ex);
        }
    }

    /// <summary>Устанавливает новый путь и сохраняет его.</summary>
    public void Set(string dir)
    {
        WorkspaceDir = dir;
        Logger.Info($"WorkspaceService: установлен путь '{dir}'");
        Save();
    }

    /// <summary>Сбрасывает рабочую папку.</summary>
    public void Clear()
    {
        WorkspaceDir = null;
        Logger.Info("WorkspaceService: сброс пути");
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
            File.WriteAllText(ConfigFile, WorkspaceDir ?? "");
        }
        catch (Exception ex)
        {
            Logger.Error("WorkspaceService: ошибка сохранения конфига", ex);
        }
    }
}
