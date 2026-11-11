using Siemens.Engineering;
using System;
using System.IO;
using System.Linq;
using TiaTextSync.Core;
using static TiaTextSync.Services.PathNames;

namespace TiaTextSync.Services;

/// <summary>
/// Сохраняет архив проекта TIA Portal (.zapXX) в корень рабочей папки для хранения в Git LFS.
/// Текстовые файлы показывают изменения, а архив позволяет открыть проект любой версии целиком.
///
/// Имя архива постоянное, поэтому Git видит каждую новую версию как изменение одного файла.
/// Должен выполняться на потоке <see cref="StaWorker"/>.
/// </summary>
internal static class ArchiveService
{
    // Архивы TIA Portal: .zap15_1, .zap16 ... .zap21
    private const string LfsPattern = "*.zap[0-9]*";
    private const string LfsRule    = LfsPattern + " filter=lfs diff=lfs merge=lfs -text";

    /// <summary>Создаёт архив и возвращает его путь в рабочей папке.</summary>
    public static string Archive(Project project, string workspaceDir, IProgress<string> progress)
    {
        progress.Report($"Архивирование проекта: {project.Name}");
        var tempDir = Path.Combine(Path.GetTempPath(), "TiaTextSync", "archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Восстанавливаемые данные (результаты компиляции и т. п.) TIA создаёт заново при открытии,
            // а без них архив заметно меньше: в Git LFS хранится каждая версия целиком
            // TIA Portal не добавляет расширение сам, поэтому оно передаётся в имени
            var fileName = SafeName(project.Name) + ArchiveExtension(project);
            project.Archive(new DirectoryInfo(tempDir), fileName, ProjectArchivationMode.DiscardRestorableDataAndCompressed);

            var archive = Directory.GetFiles(tempDir).SingleOrDefault()
                          ?? throw new InvalidOperationException("TIA Portal не создал файл архива");

            var target = Path.Combine(workspaceDir, Path.GetFileName(archive));
            File.Copy(archive, target, overwrite: true);
            progress.Report($"  ZAP  {Path.GetFileName(target)} ({new FileInfo(target).Length / (1024.0 * 1024.0):F1} МБ)");

            EnsureLfsRule(workspaceDir, progress);
            return target;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                Logger.Warn($"ArchiveService: не удалось удалить временную папку '{tempDir}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// TIA Portal отказал в архивировании, потому что в проекте есть несохранённые изменения.
    /// В API нет признака несохранённых изменений, поэтому причина определяется по тексту ошибки.
    /// </summary>
    public static bool IsUnsavedChangesError(Exception ex) =>
        ex is EngineeringTargetInvocationException
        && ex.Message.IndexOf("unsaved changes", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Расширение архива по расширению файла проекта: .ap21 → .zap21, .ap15_1 → .zap15_1.</summary>
    private static string ArchiveExtension(Project project)
    {
        var extension = project.Path?.Extension;
        return extension is { Length: > 3 } && extension.StartsWith(".ap", StringComparison.OrdinalIgnoreCase)
            ? ".z" + extension.Substring(1)
            : ".zap21";
    }

    /// <summary>Добавляет в .gitattributes правило Git LFS для архивов, если его там нет.</summary>
    private static void EnsureLfsRule(string workspaceDir, IProgress<string> progress)
    {
        var path = Path.Combine(workspaceDir, ".gitattributes");
        var text = File.Exists(path) ? File.ReadAllText(path) : "";

        if (text.Split('\n').Any(line => line.Trim().StartsWith(LfsPattern + " ", StringComparison.Ordinal)))
            return;

        var separator = text.Length == 0 || text.EndsWith("\n", StringComparison.Ordinal) ? "" : "\n";
        File.AppendAllText(path, $"{separator}{LfsRule}\n");
        progress.Report($"  .gitattributes: добавлено правило «{LfsRule}». Нужен установленный Git LFS (git lfs install)");
    }
}
