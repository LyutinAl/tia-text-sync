using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections.Generic;
using System.IO;

namespace TiaTextSync;

/// <summary>
/// Экспортирует содержимое ПЛК в файловую структуру, пригодную для Git.
///
/// Структура вывода:
///   &lt;outputDir&gt;/&lt;PlcName&gt;/src/   — блоки и типы с ST-представлением (.scl)
///   &lt;outputDir&gt;/&lt;PlcName&gt;/xml/   — блоки без ST-представления (LAD/FBD/AWL)
///   &lt;outputDir&gt;/&lt;PlcName&gt;/tags/  — тег-таблицы (.csv)
/// </summary>
internal sealed class ExportService
{
    private readonly PlcSoftware _plc;
    private readonly string _outputDir;
    private readonly IProgress<string> _progress;

    public ExportService(PlcSoftware plc, string outputDir, IProgress<string> progress)
    {
        _plc = plc;
        _outputDir = Path.GetFullPath(outputDir); // Openness требует абсолютные пути
        _progress = progress;
    }

    public void Export()
    {
        _progress.Report($"Экспорт ПЛК: {_plc.Name}");
        ExportBlockGroup(_plc.BlockGroup);
        ExportTagTableGroup(_plc.TagTableGroup);
        ExportTypeGroup(_plc.TypeGroup);
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
            if (block is IGenerateSource src)
                ExportBlockAsSt(block, src);
            else
                ExportBlockAsXml(block);
        }
        catch (Exception ex)
        {
            _progress.Report($"  WARN {block.Name}: {ex.Message}");
        }
    }

    private void ExportBlockAsSt(PlcBlock block, IGenerateSource src)
    {
        var dir = SrcDir();
        // STL-блоки требуют расширение .awl — TIA Portal отклонит .scl
        var ext  = block.ProgrammingLanguage == ProgrammingLanguage.STL ? "awl" : "scl";
        var file = new FileInfo(Path.Combine(dir, $"{block.Name}.{ext}"));
        _plc.ExternalSourceGroup.GenerateSource(new[] { src }, file);
        _progress.Report($"  {ext.ToUpperInvariant()}  {_plc.Name}/src/{block.Name}.{ext}");
    }

    private void ExportBlockAsXml(PlcBlock block)
    {
        var dir = XmlDir();
        var path = Path.Combine(dir, $"{block.Name}.xml");
        block.Export(new FileInfo(path), ExportOptions.None);
        XmlNormalizer.Normalize(path);
        _progress.Report($"  XML  {_plc.Name}/xml/{block.Name}.xml");
    }

    // ── Тег-таблицы ──────────────────────────────────────────────────────────

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
            var dir = TagsDir();
            var xmlPath = Path.Combine(dir, $"{table.Name}.xml");
            var csvPath = Path.Combine(dir, $"{table.Name}.csv");

            table.Export(new FileInfo(xmlPath), ExportOptions.None);
            TagCsvConverter.Convert(xmlPath, csvPath);
            File.Delete(xmlPath); // XML — промежуточный, оставляем только CSV

            _progress.Report($"  CSV  {_plc.Name}/tags/{table.Name}.csv");
        }
        catch (Exception ex)
        {
            _progress.Report($"  WARN {table.Name}: {ex.Message}");
        }
    }

    // ── UDT ──────────────────────────────────────────────────────────────────

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
            if (type is IGenerateSource src)
            {
                var dir = SrcDir();
                var file = new FileInfo(Path.Combine(dir, $"{type.Name}.scl"));
                _plc.ExternalSourceGroup.GenerateSource(new[] { src }, file);
                _progress.Report($"  SCL  {_plc.Name}/src/{type.Name}.scl");
            }
            else
            {
                var dir = XmlDir();
                var path = Path.Combine(dir, $"{type.Name}.xml");
                type.Export(new FileInfo(path), ExportOptions.None);
                XmlNormalizer.Normalize(path);
                _progress.Report($"  XML  {_plc.Name}/xml/{type.Name}.xml");
            }
        }
        catch (Exception ex)
        {
            _progress.Report($"  WARN {type.Name}: {ex.Message}");
        }
    }

    // ── Вспомогательные ──────────────────────────────────────────────────────

    private string SrcDir()
    {
        var dir = Path.Combine(_outputDir, _plc.Name, "src");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string XmlDir()
    {
        var dir = Path.Combine(_outputDir, _plc.Name, "xml");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string TagsDir()
    {
        var dir = Path.Combine(_outputDir, _plc.Name, "tags");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
