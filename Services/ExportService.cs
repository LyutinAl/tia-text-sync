using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;
using System;
using System.IO;

namespace TiaTextSync.Services;

/// <summary>
/// Экспортирует содержимое ПЛК (блоки, таблицы тегов, UDT) в структуру папок для Git.
///
/// Структура вывода:
///   &lt;deviceDir&gt;/&lt;PlcName&gt;/src/   — блоки и UDT с ST-исходником (.scl / .awl)
///   &lt;deviceDir&gt;/&lt;PlcName&gt;/xml/   — блоки LAD/FBD без ST-исходника (.xml)
///   &lt;deviceDir&gt;/&lt;PlcName&gt;/tags/  — таблицы тегов (.csv)
///
/// Должен выполняться на потоке <see cref="TiaTextSync.Core.StaWorker"/>.
/// </summary>
internal sealed class ExportService
{
    private readonly PlcSoftware       _plc;
    private readonly IProgress<string> _progress;

    // Пути папок кешируются в конструкторе — одинаковы для всех файлов одного ПЛК
    private readonly string _srcDir;
    private readonly string _xmlDir;
    private readonly string _tagsDir;

    public ExportService(PlcSoftware plc, string outputDir, IProgress<string> progress)
    {
        _plc      = plc;
        _progress = progress;

        var root  = Path.Combine(Path.GetFullPath(outputDir), plc.Name);  // Openness требует абсолютные пути
        _srcDir   = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        _xmlDir   = Directory.CreateDirectory(Path.Combine(root, "xml")).FullName;
        _tagsDir  = Directory.CreateDirectory(Path.Combine(root, "tags")).FullName;
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
                ExportBlockAsSrc(block, src);
            else
                ExportBlockAsXml(block);
        }
        catch (Exception ex)
        {
            _progress.Report($"  WARN {block.Name}: {ex.Message}");
        }
    }

    private void ExportBlockAsSrc(PlcBlock block, IGenerateSource src)
    {
        var dir = SrcDir();
        // STL-блоки требуют расширение .awl — TIA Portal отклоняет .scl для STL
        var ext  = block.ProgrammingLanguage == ProgrammingLanguage.STL ? "awl" : "scl";
        var file = new FileInfo(Path.Combine(dir, $"{block.Name}.{ext}"));
        _plc.ExternalSourceGroup.GenerateSource(new[] { src }, file);
        _progress.Report($"  {ext.ToUpperInvariant()}  {_plc.Name}/src/{block.Name}.{ext}");
    }

    private void ExportBlockAsXml(PlcBlock block)
    {
        var dir  = XmlDir();
        var path = Path.Combine(dir, $"{block.Name}.xml");
        block.Export(new FileInfo(path), ExportOptions.None);
        XmlNormalizer.Normalize(path);
        _progress.Report($"  XML  {_plc.Name}/xml/{block.Name}.xml");
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
            var dir     = TagsDir();
            var xmlPath = Path.Combine(dir, $"{table.Name}.xml");
            var csvPath = Path.Combine(dir, $"{table.Name}.csv");

            table.Export(new FileInfo(xmlPath), ExportOptions.None);
            TagCsvConverter.Convert(xmlPath, csvPath);
            File.Delete(xmlPath);   // XML промежуточный — оставляем только CSV

            _progress.Report($"  CSV  {_plc.Name}/tags/{table.Name}.csv");
        }
        catch (Exception ex)
        {
            _progress.Report($"  WARN {table.Name}: {ex.Message}");
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
            if (type is IGenerateSource src)
            {
                var file = new FileInfo(Path.Combine(SrcDir(), $"{type.Name}.scl"));
                _plc.ExternalSourceGroup.GenerateSource(new[] { src }, file);
                _progress.Report($"  SCL  {_plc.Name}/src/{type.Name}.scl");
            }
            else
            {
                var path = Path.Combine(XmlDir(), $"{type.Name}.xml");
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

    private string SrcDir()  => _srcDir;
    private string XmlDir()  => _xmlDir;
    private string TagsDir() => _tagsDir;
}
