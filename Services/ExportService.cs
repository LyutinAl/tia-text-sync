using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TiaTextSync.Core;

namespace TiaTextSync.Services;

/// <summary>
/// Экспортирует содержимое ПЛК (блоки, таблицы тегов, UDT) в структуру папок для Git.
/// Вложенные папки повторяют группы проекта TIA Portal.
///
/// Структура вывода:
///   &lt;deviceDir&gt;/&lt;PlcName&gt;/blocks/&lt;группы&gt;/ — блоки (.scl / .awl / .db / .xml)
///   &lt;deviceDir&gt;/&lt;PlcName&gt;/tags/&lt;группы&gt;/   — таблицы тегов в SimaticML (.xml)
///   &lt;deviceDir&gt;/&lt;PlcName&gt;/types/&lt;группы&gt;/  — UDT (.scl)
///   &lt;deviceDir&gt;/&lt;PlcName&gt;/tech/&lt;группы&gt;/   — технологические объекты: SimaticML (.xml) или исходник DB (.db)
///
/// Формат файла зависит от языка блока: SCL, STL и DB экспортируются исходником,
/// остальные языки (LAD, FBD, GRAPH, F-блоки) — в XML. Рядом с XML блока LAD/FBD создаётся
/// текстовый вид (.lad / .fbd) для чтения и diff, импорт его не использует.
///
/// Файлы сначала создаются во временной папке и только после успеха заменяют файлы
/// в рабочей папке, поэтому сбой экспорта одного элемента не портит его прошлую версию.
/// После экспорта файлы элементов, которых больше нет в ПЛК, удаляются.
///
/// Должен выполняться на потоке <see cref="TiaTextSync.Core.StaWorker"/>.
/// </summary>
internal sealed class ExportService
{
    // Удаляются только файлы форматов экспорта (.csv и .to — прежние форматы тегов и технологических объектов)
    private static readonly HashSet<string> ManagedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".scl", ".awl", ".db", ".xml", ".csv", ".lad", ".fbd", ".to"
    };

    // Папки прежней раскладки по языкам: очищаются при первом экспорте в новую структуру
    private static readonly string[] LegacyDirNames = { "src", "xml" };

    private readonly PlcSoftware       _plc;
    private readonly IProgress<string> _progress;

    private readonly string _root;
    private readonly string _blocksDir;
    private readonly string _tagsDir;
    private readonly string _typesDir;
    private readonly string _techDir;
    private readonly string _tempDir;

    private readonly HashSet<string> _writtenFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedNames  = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Количество элементов, обработанных с ошибкой во время <see cref="Export"/>.</summary>
    public int WarningCount { get; private set; }

    public ExportService(PlcSoftware plc, string outputDir, IProgress<string> progress)
    {
        _plc      = plc;
        _progress = progress;

        _root      = Path.Combine(Path.GetFullPath(outputDir), SafeName(plc.Name));  // Openness требует абсолютные пути
        _blocksDir = Path.Combine(_root, "blocks");
        _tagsDir   = Path.Combine(_root, "tags");
        _typesDir  = Path.Combine(_root, "types");
        _techDir   = Path.Combine(_root, "tech");
        _tempDir   = Path.Combine(Path.GetTempPath(), "TiaTextSync", Guid.NewGuid().ToString("N"));
    }

    public void Export()
    {
        _progress.Report($"Экспорт ПЛК: {_plc.Name}");
        Directory.CreateDirectory(_tempDir);
        try
        {
            ExportBlockGroup(_plc.BlockGroup, _blocksDir);
            ExportSystemBlockGroups(_plc.BlockGroup.SystemBlockGroups, _blocksDir);
            ExportTagTableGroup(_plc.TagTableGroup, _tagsDir);
            ExportTypeGroup(_plc.TypeGroup, _typesDir);
            ExportTechnologicalObjectGroup(_plc.TechnologicalObjectGroup, _techDir);

            // Выполняется, только если все группы удалось перебрать:
            // при сбое обхода неполный список файлов не должен приводить к удалению.
            RemoveStaleFiles();
        }
        finally
        {
            DeleteTempDir();
        }
    }

    // ── Блоки ────────────────────────────────────────────────────────────────

    private void ExportBlockGroup(PlcBlockGroup group, string dir)
    {
        foreach (PlcBlock block in group.Blocks)
            ExportBlock(block, dir);

        foreach (PlcBlockUserGroup sub in group.Groups)
            ExportBlockGroup(sub, Path.Combine(dir, SafeName(sub.Name)));
    }

    // Системные группы не входят в PlcBlockGroup.Groups. Верхняя группа уже называется «System blocks».
    private void ExportSystemBlockGroups(PlcSystemBlockGroupComposition groups, string dir)
    {
        foreach (PlcSystemBlockGroup group in groups)
        {
            var groupDir = Path.Combine(dir, SafeName(group.Name));

            foreach (PlcBlock block in group.Blocks)
                ExportBlock(block, groupDir);

            ExportSystemBlockGroups(group.Groups, groupDir);
        }
    }

    private void ExportBlock(PlcBlock block, string dir)
    {
        try
        {
            // IGenerateSource реализован самим PlcBlock, поэтому проверять нужно язык блока.
            var language = block.ProgrammingLanguage;
            if (language is not (ProgrammingLanguage.SCL or ProgrammingLanguage.STL or ProgrammingLanguage.DB))
                ExportBlockAsXml(block, dir);
            else if (block.IsKnowHowProtected)
                Skip(block.Name, "блок защищён, TIA Portal не выдаёт его исходник");
            else
                ExportBlockAsSource(block, language, dir);
        }
        catch (Exception ex)
        {
            Warn(block.Name, ex);
        }
    }

    private void ExportBlockAsSource(PlcBlock block, ProgrammingLanguage language, string dir)
    {
        // TIA Portal принимает исходник только с расширением, соответствующим типу блока
        var ext = language switch
        {
            ProgrammingLanguage.STL => "awl",
            ProgrammingLanguage.DB  => "db",
            _                       => "scl"
        };
        GenerateSourceFile(block, dir, $"{SafeName(block.Name)}.{ext}", ext.ToUpperInvariant());
    }

    private void ExportBlockAsXml(PlcBlock block, string dir)
    {
        var fileName = $"{SafeName(block.Name)}.xml";
        var tempPath = Path.Combine(_tempDir, fileName);
        block.Export(new FileInfo(tempPath), ExportOptions.None);
        XmlNormalizer.Normalize(tempPath);
        Publish(tempPath, dir, fileName, "XML");

        // После Publish: сбой генерации текстового вида не должен мешать обновлению XML
        if (BlockTextRenderer.Render(Path.Combine(dir, fileName)) is not { } view)
            return;

        PublishText(view.Text, dir, $"{SafeName(block.Name)}.{view.Extension}", view.Extension.ToUpperInvariant());
    }

    // ── Таблицы тегов ────────────────────────────────────────────────────────

    private void ExportTagTableGroup(PlcTagTableGroup group, string dir)
    {
        foreach (PlcTagTable table in group.TagTables)
            ExportTagTable(table, dir);

        foreach (PlcTagTableUserGroup sub in group.Groups)
            ExportTagTableGroup(sub, Path.Combine(dir, SafeName(sub.Name)));
    }

    private void ExportTagTable(PlcTagTable table, string dir)
    {
        try
        {
            var fileName = $"{SafeName(table.Name)}.xml";
            var tempPath = Path.Combine(_tempDir, fileName);

            table.Export(new FileInfo(tempPath), ExportOptions.None);
            XmlNormalizer.Normalize(tempPath);
            Publish(tempPath, dir, fileName, "TAG");
        }
        catch (Exception ex)
        {
            Warn(table.Name, ex);
        }
    }

    // ── Пользовательские типы (UDT) ──────────────────────────────────────────

    private void ExportTypeGroup(PlcTypeGroup group, string dir)
    {
        foreach (PlcType type in group.Types)
            ExportType(type, dir);

        foreach (PlcTypeUserGroup sub in group.Groups)
            ExportTypeGroup(sub, Path.Combine(dir, SafeName(sub.Name)));
    }

    private void ExportType(PlcType type, string dir)
    {
        try
        {
            GenerateSourceFile(type, dir, $"{SafeName(type.Name)}.scl", "SCL");
        }
        catch (Exception ex)
        {
            Warn(type.Name, ex);
        }
    }

    // ── Технологические объекты ──────────────────────────────────────────────

    private void ExportTechnologicalObjectGroup(TechnologicalInstanceDBGroup group, string dir)
    {
        foreach (TechnologicalInstanceDB technologicalObject in group.TechnologicalObjects)
            ExportTechnologicalObject(technologicalObject, dir);

        foreach (TechnologicalInstanceDBUserGroup sub in group.Groups)
            ExportTechnologicalObjectGroup(sub, Path.Combine(dir, SafeName(sub.Name)));
    }

    private void ExportTechnologicalObject(TechnologicalInstanceDB technologicalObject, string dir)
    {
        try
        {
            var name     = SafeName(technologicalObject.Name);
            var tempPath = Path.Combine(_tempDir, $"{name}.xml");

            try
            {
                technologicalObject.Export(new FileInfo(tempPath), ExportOptions.None);
            }
            catch (EngineeringTargetInvocationException ex)
            {
                // SimaticML поддерживается не для всех типов объектов, например не для PID_Compact на S7-1200
                Logger.Warn($"ExportService: '{technologicalObject.Name}' не экспортируется в XML, выгружается исходник DB: {ex.Message}");
                GenerateSourceFile(technologicalObject, dir, $"{name}.db", "TO");
                return;
            }

            XmlNormalizer.Normalize(tempPath);
            Publish(tempPath, dir, $"{name}.xml", "TO");
        }
        catch (Exception ex)
        {
            Warn(technologicalObject.Name, ex);
        }
    }

    // ── Запись файлов ────────────────────────────────────────────────────────

    private void GenerateSourceFile(IGenerateSource item, string dir, string fileName, string label)
    {
        var tempPath = Path.Combine(_tempDir, fileName);
        _plc.ExternalSourceGroup.GenerateSource(new[] { item }, new FileInfo(tempPath));
        Publish(tempPath, dir, fileName, label);
    }

    private void PublishText(string text, string targetDir, string fileName, string label)
    {
        var tempPath = Path.Combine(_tempDir, fileName);
        File.WriteAllText(tempPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Publish(tempPath, targetDir, fileName, label);
    }

    private void Publish(string tempPath, string targetDir, string fileName, string label)
    {
        Directory.CreateDirectory(targetDir);
        var targetPath = Path.Combine(targetDir, fileName);
        File.Copy(tempPath, targetPath, overwrite: true);
        File.Delete(tempPath);
        _writtenFiles.Add(targetPath);
        _progress.Report($"  {label}  {DisplayPath(targetPath)}");
    }

    /// <summary>
    /// Удаляет файлы элементов, которых больше нет в ПЛК, и опустевшие папки.
    /// Файлы элементов, экспорт которых завершился ошибкой, сохраняются.
    /// </summary>
    private void RemoveStaleFiles()
    {
        var dirs = new List<string> { _blocksDir, _tagsDir, _typesDir, _techDir };
        foreach (var legacy in LegacyDirNames)
            dirs.Add(Path.Combine(_root, legacy));

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (_writtenFiles.Contains(file)
                    || _failedNames.Contains(Path.GetFileNameWithoutExtension(file))
                    || !ManagedExtensions.Contains(Path.GetExtension(file)))
                    continue;

                try
                {
                    File.Delete(file);
                    _progress.Report($"  DEL  {DisplayPath(file)}");
                }
                catch (Exception ex)
                {
                    Warn(Path.GetFileName(file), ex);
                }
            }

            RemoveEmptyDirectories(dir);
        }
    }

    private static void RemoveEmptyDirectories(string dir)
    {
        foreach (var sub in Directory.GetDirectories(dir))
            RemoveEmptyDirectories(sub);

        try
        {
            if (Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ExportService: не удалось удалить пустую папку '{dir}': {ex.Message}");
        }
    }

    private void DeleteTempDir()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.Warn($"ExportService: не удалось удалить временную папку '{_tempDir}': {ex.Message}");
        }
    }

    // Ожидаемый пропуск: не считается ошибкой, а прошлый файл элемента сохраняется
    private void Skip(string name, string reason)
    {
        _failedNames.Add(SafeName(name));
        _progress.Report($"  SKIP {name}: {reason}");
        Logger.Warn($"ExportService: пропущен '{_plc.Name}/{name}': {reason}");
    }

    private void Warn(string name, Exception ex)
    {
        WarningCount++;
        _failedNames.Add(SafeName(name));
        _progress.Report($"  WARN {name}: {ex.Message}");
        Logger.Error($"ExportService: ошибка экспорта '{_plc.Name}/{name}'", ex);
    }

    private string DisplayPath(string path) =>
        $"{_plc.Name}/{path.Substring(_root.Length + 1).Replace('\\', '/')}";

    /// <summary>Приводит имя из TIA Portal к допустимому имени файла или папки Windows.</summary>
    private static string SafeName(string name)
    {
        // Windows отбрасывает завершающие точки и пробелы в именах
        var safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).TrimEnd(' ', '.');
        return safe.Length > 0 ? safe : "_";
    }
}
