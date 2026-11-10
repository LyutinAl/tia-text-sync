using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections.Generic;
using System.IO;
using TiaTextSync.Core;

namespace TiaTextSync.Services;

/// <summary>
/// Экспортирует содержимое ПЛК (блоки, таблицы тегов, UDT) в структуру папок для Git.
///
/// Структура вывода:
///   &lt;deviceDir&gt;/&lt;PlcName&gt;/src/   — блоки SCL/STL, DB и UDT (.scl / .awl / .db)
///   &lt;deviceDir&gt;/&lt;PlcName&gt;/xml/   — блоки на остальных языках: LAD, FBD, GRAPH, F-блоки (.xml)
///   &lt;deviceDir&gt;/&lt;PlcName&gt;/tags/  — таблицы тегов (.csv)
///
/// Файлы сначала создаются во временной папке и только после успеха заменяют файлы
/// в рабочей папке, поэтому сбой экспорта одного элемента не портит его прошлую версию.
/// После экспорта файлы элементов, которых больше нет в ПЛК, удаляются.
///
/// Должен выполняться на потоке <see cref="TiaTextSync.Core.StaWorker"/>.
/// </summary>
internal sealed class ExportService
{
    // Удаляются только файлы, которые создаёт сам экспорт
    private static readonly HashSet<string> ManagedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".scl", ".awl", ".db", ".xml", ".csv"
    };

    private readonly PlcSoftware       _plc;
    private readonly IProgress<string> _progress;

    // Пути папок кешируются в конструкторе — одинаковы для всех файлов одного ПЛК
    private readonly string _srcDir;
    private readonly string _xmlDir;
    private readonly string _tagsDir;
    private readonly string _tempDir;

    private readonly HashSet<string> _writtenFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedNames  = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Количество элементов, обработанных с ошибкой во время <see cref="Export"/>.</summary>
    public int WarningCount { get; private set; }

    public ExportService(PlcSoftware plc, string outputDir, IProgress<string> progress)
    {
        _plc      = plc;
        _progress = progress;

        var root  = Path.Combine(Path.GetFullPath(outputDir), plc.Name);  // Openness требует абсолютные пути
        _srcDir   = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        _xmlDir   = Directory.CreateDirectory(Path.Combine(root, "xml")).FullName;
        _tagsDir  = Directory.CreateDirectory(Path.Combine(root, "tags")).FullName;
        _tempDir  = Path.Combine(Path.GetTempPath(), "TiaTextSync", Guid.NewGuid().ToString("N"));
    }

    public void Export()
    {
        _progress.Report($"Экспорт ПЛК: {_plc.Name}");
        Directory.CreateDirectory(_tempDir);
        try
        {
            ExportBlockGroup(_plc.BlockGroup);
            ExportTagTableGroup(_plc.TagTableGroup);
            ExportTypeGroup(_plc.TypeGroup);

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

    private void ExportBlockGroup(PlcBlockGroup group)
    {
        foreach (PlcBlock block in group.Blocks)
            ExportBlock(block);

        foreach (PlcBlockUserGroup sub in group.Groups)
            ExportBlockGroup(sub);
    }

    private void ExportBlock(PlcBlock block)
    {
        try
        {
            // IGenerateSource реализован самим PlcBlock, поэтому проверять нужно язык блока.
            var language = block.ProgrammingLanguage;
            if (language is ProgrammingLanguage.SCL or ProgrammingLanguage.STL or ProgrammingLanguage.DB)
                ExportBlockAsSrc(block, language);
            else
                ExportBlockAsXml(block);
        }
        catch (Exception ex)
        {
            Warn(block.Name, ex);
        }
    }

    private void ExportBlockAsSrc(PlcBlock block, ProgrammingLanguage language)
    {
        // TIA Portal принимает исходник только с расширением, соответствующим типу блока
        var ext = language switch
        {
            ProgrammingLanguage.STL => "awl",
            ProgrammingLanguage.DB  => "db",
            _                       => "scl"
        };
        GenerateSourceFile(block, $"{block.Name}.{ext}");
        _progress.Report($"  {ext.ToUpperInvariant()}  {_plc.Name}/src/{block.Name}.{ext}");
    }

    private void ExportBlockAsXml(PlcBlock block)
    {
        var fileName = $"{block.Name}.xml";
        var tempPath = Path.Combine(_tempDir, fileName);
        block.Export(new FileInfo(tempPath), ExportOptions.None);
        XmlNormalizer.Normalize(tempPath);
        Publish(tempPath, _xmlDir, fileName);
        _progress.Report($"  XML  {_plc.Name}/xml/{fileName}");
    }

    // ── Таблицы тегов ────────────────────────────────────────────────────────

    private void ExportTagTableGroup(PlcTagTableGroup group)
    {
        foreach (PlcTagTable table in group.TagTables)
            ExportTagTable(table);

        foreach (PlcTagTableUserGroup sub in group.Groups)
            ExportTagTableGroup(sub);
    }

    private void ExportTagTable(PlcTagTable table)
    {
        try
        {
            var fileName = $"{table.Name}.csv";
            var xmlPath  = Path.Combine(_tempDir, $"{table.Name}.xml");
            var csvPath  = Path.Combine(_tempDir, fileName);

            table.Export(new FileInfo(xmlPath), ExportOptions.None);
            TagCsvConverter.Convert(xmlPath, csvPath);
            File.Delete(xmlPath);   // XML промежуточный — оставляем только CSV
            Publish(csvPath, _tagsDir, fileName);

            _progress.Report($"  CSV  {_plc.Name}/tags/{fileName}");
        }
        catch (Exception ex)
        {
            Warn(table.Name, ex);
        }
    }

    // ── Пользовательские типы (UDT) ──────────────────────────────────────────

    private void ExportTypeGroup(PlcTypeGroup group)
    {
        foreach (PlcType type in group.Types)
            ExportType(type);

        foreach (PlcTypeUserGroup sub in group.Groups)
            ExportTypeGroup(sub);
    }

    private void ExportType(PlcType type)
    {
        try
        {
            GenerateSourceFile(type, $"{type.Name}.scl");
            _progress.Report($"  SCL  {_plc.Name}/src/{type.Name}.scl");
        }
        catch (Exception ex)
        {
            Warn(type.Name, ex);
        }
    }

    // ── Запись файлов ────────────────────────────────────────────────────────

    private void GenerateSourceFile(IGenerateSource item, string fileName)
    {
        var tempPath = Path.Combine(_tempDir, fileName);
        _plc.ExternalSourceGroup.GenerateSource(new[] { item }, new FileInfo(tempPath));
        Publish(tempPath, _srcDir, fileName);
    }

    private void Publish(string tempPath, string targetDir, string fileName)
    {
        var targetPath = Path.Combine(targetDir, fileName);
        File.Copy(tempPath, targetPath, overwrite: true);
        File.Delete(tempPath);
        _writtenFiles.Add(targetPath);
    }

    /// <summary>
    /// Удаляет файлы элементов, которых больше нет в ПЛК.
    /// Файлы элементов, экспорт которых завершился ошибкой, сохраняются.
    /// </summary>
    private void RemoveStaleFiles()
    {
        foreach (var dir in new[] { _srcDir, _xmlDir, _tagsDir })
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (_writtenFiles.Contains(file)
                    || _failedNames.Contains(Path.GetFileNameWithoutExtension(file))
                    || !ManagedExtensions.Contains(Path.GetExtension(file)))
                    continue;

                try
                {
                    File.Delete(file);
                    _progress.Report($"  DEL  {_plc.Name}/{Path.GetFileName(dir)}/{name}");
                }
                catch (Exception ex)
                {
                    Warn(name, ex);
                }
            }
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

    private void Warn(string name, Exception ex)
    {
        WarningCount++;
        _failedNames.Add(name);
        _progress.Report($"  WARN {name}: {ex.Message}");
        Logger.Error($"ExportService: ошибка экспорта '{_plc.Name}/{name}'", ex);
    }
}
