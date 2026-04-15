using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace TiaTextSync.Services;

/// <summary>
/// Конвертирует экспорт таблицы тегов из формата TIA ML XML в CSV,
/// совместимый с нативным импортом тегов TIA Portal (Файл → Импорт тегов).
/// </summary>
internal static class TagCsvConverter
{
    public static void Convert(string xmlPath, string csvPath)
    {
        var doc = XDocument.Load(xmlPath);
        var sb  = new StringBuilder();
        sb.AppendLine("Name,Data Type,Logical Address,Comment");

        var tags = doc.Descendants()
            .Where(e => e.Name.LocalName == "SW.Tags.PlcTag");

        foreach (var tag in tags)
        {
            var attrs = tag.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "AttributeList");

            if (attrs is null) continue;

            var name    = GetAttr(attrs, "Name");
            var type    = GetAttr(attrs, "DataTypeName");
            var address = GetAttr(attrs, "LogicalAddress");
            var comment = GetComment(attrs);

            sb.AppendLine($"{Escape(name)},{Escape(type)},{Escape(address)},{Escape(comment)}");
        }

        File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
    }

    private static string GetAttr(XElement parent, string localName) =>
        parent.Elements()
              .FirstOrDefault(e => e.Name.LocalName == localName)?.Value ?? string.Empty;

    private static string GetComment(XElement attrs)
    {
        // <Comment><MultiLanguageText Lang="ru-RU">…</MultiLanguageText></Comment>
        var commentEl = attrs.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Comment");

        return commentEl?.Descendants()
                         .FirstOrDefault(e => e.Name.LocalName == "MultiLanguageText")?.Value
               ?? string.Empty;
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
