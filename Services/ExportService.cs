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
using System.Linq;
using System.Text;
using TiaTextSync.Core;
using static TiaTextSync.Services.PathNames;

namespace TiaTextSync.Services;

/// <summary>
/// Экспортирует элементы ПЛК (блоки, таблицы тегов, UDT, технологические объекты) в структуру папок для Git.
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
/// После экспорта удаляются файлы элементов, которых больше нет в ПЛК.
/// Файлы элементов, не вошедших в выборку, не трогаются.
///
/// Должен выполняться на потоке <see cref="TiaTextSync.Core.StaWorker"/>.
/// </summary>
internal sealed class ExportService
{
    // Удаляются только файлы форматов экспорта (.csv и .to — прежние форматы тегов и технологических объектов)
    internal static readonly HashSet<string> ManagedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".scl", ".awl", ".db", ".xml", ".csv", ".lad", ".fbd", ".to"
    };

    // Папки прежней раскладки по языкам: очищаются при первом экспорте в новую структуру
    internal static readonly string[] LegacyDirNames = { "src", "xml" };

    internal static readonly string[] ItemRoots =
    {
        PlcCatalog.BlocksRoot, PlcCatalog.TagsRoot, PlcCatalog.TypesRoot, PlcCatalog.TechRoot
    };

    private readonly PlcSoftware       _plc;
    private readonly IProgress<string> _progress;

    private readonly string _root;
    private readonly string _tempDir;

    private readonly HashSet<string> _writtenFiles   = new(StringComparer.OrdinalIgnoreCase);
    // Имена элементов, чьи прежние файлы сохраняются: не вошли в выборку, пропущены или завершились ошибкой
    private readonly HashSet<string> _preservedNames = new(StringComparer.OrdinalIgnoreCase);

    private const string NotCompiledMessage =
        "не скомпилирован, TIA Portal не экспортирует такие элементы. " +
        "Скомпилируйте ПЛК или включите «Инструменты → Компилировать перед экспортом и сравнением»";

    private readonly HashSet<string> _failedKeys      = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notCompiledKeys = new(StringComparer.Ordinal);
    private bool _currentNotCompiled;

    /// <summary>Количество элементов, обработанных с ошибкой во время <see cref="Export"/>.</summary>
    public int WarningCount { get; private set; }

    /// <summary>Ключи элементов, экспорт которых завершился ошибкой.</summary>
    public IReadOnlyCollection<string> FailedKeys => _failedKeys;

    /// <summary>Ключи элементов из <see cref="FailedKeys"/>, которые не выгружены, потому что не скомпилированы.</summary>
    public IReadOnlyCollection<string> NotCompiledKeys => _notCompiledKeys;

    public ExportService(PlcSoftware plc, string deviceDir, IProgress<string> progress)
    {
        _plc      = plc;
        _progress = progress;
        _root     = PlcDir(deviceDir, plc.Name);
        _tempDir  = Path.Combine(Path.GetTempPath(), "TiaTextSync", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Экспортирует <paramref name="selected"/>.
    /// <paramref name="all"/> — все элементы ПЛК: по нему определяются файлы удалённых элементов.
    /// </summary>
    public void Export(IReadOnlyList<PlcItem> all, IReadOnlyCollection<PlcItem> selected)
    {
        _progress.Report(selected.Count == all.Count
            ? $"Экспорт ПЛК: {_plc.Name}"
            : $"Экспорт ПЛК: {_plc.Name}, элементов {selected.Count} из {all.Count}");

        Directory.CreateDirectory(_tempDir);
        try
        {
            var selectedKeys = new HashSet<string>(selected.Select(i => i.Key), StringComparer.Ordinal);
            foreach (var item in all.Where(i => !selectedKeys.Contains(i.Key)))
                _preservedNames.Add(SafeName(item.Name));

            foreach (var item in selected)
            {
                var warnings = WarningCount;
                _currentNotCompiled = false;
                ExportItem(item, ItemDir(_root, item));
                if (WarningCount == warnings)
                    continue;

                _failedKeys.Add(item.Key);
                if (_currentNotCompiled)
                    _notCompiledKeys.Add(item.Key);
            }

            // Выполняется, только если все элементы удалось перебрать:
            // при сбое обхода неполный список файлов не должен приводить к удалению.
            RemoveStaleFiles();
        }
        finally
        {
            DeleteTempDir();
        }
    }

    /// <summary>Папка файлов элемента внутри папки ПЛК.</summary>
    internal static string ItemDir(string plcDir, PlcItem item) =>
        Path.Combine(plcDir, item.Folder.Replace('/', Path.DirectorySeparatorChar));

    private void ExportItem(PlcItem item, string dir)
    {
        switch (item.Kind)
        {
            case PlcItemKind.Block:               ExportBlock((PlcBlock)item.Object, dir);                              break;
            case PlcItemKind.TagTable:            ExportTagTable((PlcTagTable)item.Object, dir);                        break;
            case PlcItemKind.Type:                ExportType((PlcType)item.Object, dir);                                break;
            case PlcItemKind.TechnologicalObject: ExportTechnologicalObject((TechnologicalInstanceDB)item.Object, dir); break;
        }
    }

    // ── Блоки ────────────────────────────────────────────────────────────────

    private void ExportBlock(PlcBlock block, string dir)
    {
        try
        {
            if (!block.IsConsistent)
            {
                WarnNotCompiled(block.Name);
                return;
            }

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

    private void ExportType(PlcType type, string dir)
    {
        try
        {
            if (!type.IsConsistent)
            {
                WarnNotCompiled(type.Name);
                return;
            }

            GenerateSourceFile(type, dir, $"{SafeName(type.Name)}.scl", "SCL");
        }
        catch (Exception ex)
        {
            Warn(type.Name, ex);
        }
    }

    // ── Технологические объекты ──────────────────────────────────────────────

    private void ExportTechnologicalObject(TechnologicalInstanceDB technologicalObject, string dir)
    {
        try
        {
            if (!technologicalObject.IsConsistent)
            {
                WarnNotCompiled(technologicalObject.Name);
                return;
            }

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
    /// Файлы элементов из <see cref="_preservedNames"/> сохраняются.
    /// </summary>
    private void RemoveStaleFiles()
    {
        var dirs = ItemRoots.Concat(LegacyDirNames).Select(name => Path.Combine(_root, name));

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (_writtenFiles.Contains(file)
                    || _preservedNames.Contains(Path.GetFileNameWithoutExtension(file))
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
        _preservedNames.Add(SafeName(name));
        _progress.Report($"  SKIP {name}: {reason}");
        Logger.Warn($"ExportService: пропущен '{_plc.Name}/{name}': {reason}");
    }

    // Проверяется до вызова TIA: исключение Openness не объясняет, что делать
    private void WarnNotCompiled(string name)
    {
        WarningCount++;
        _currentNotCompiled = true;
        _preservedNames.Add(SafeName(name));
        _progress.Report($"  WARN {name}: {NotCompiledMessage}");
        Logger.Warn($"ExportService: '{_plc.Name}/{name}' {NotCompiledMessage}");
    }

    private void Warn(string name, Exception ex)
    {
        WarningCount++;
        _preservedNames.Add(SafeName(name));
        _progress.Report($"  WARN {name}: {ex.Message}");
        Logger.Error($"ExportService: ошибка экспорта '{_plc.Name}/{name}'", ex);
    }

    private string DisplayPath(string path) =>
        $"{_plc.Name}/{path.Substring(_root.Length + 1).Replace('\\', '/')}";
}
