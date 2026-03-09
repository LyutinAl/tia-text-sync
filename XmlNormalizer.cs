using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace TiaTextSync;

/// <summary>
/// Убирает из экспортированного TIA XML нестабильные поля, которые меняются
/// при каждой компиляции и создают ложный шум в Git diff.
/// </summary>
internal static class XmlNormalizer
{
    // Элементы с датами и внутренними идентификаторами — шум для diff
    private static readonly HashSet<string> ElementsToRemove = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "ModifiedDate",
        "CreationDate",
        "CompileDate",
        "CodeModifiedDate",
        "InterfaceModifiedDate",
        "ParameterModified",
        "StructureModified",
    };

    public static void Normalize(string xmlPath)
    {
        var doc = XDocument.Load(xmlPath);
        RemoveUnstable(doc.Root!);
        doc.Save(xmlPath);
    }

    private static void RemoveUnstable(XElement element)
    {
        var toRemove = element.Elements()
            .Where(e => ElementsToRemove.Contains(e.Name.LocalName))
            .ToList();

        foreach (var e in toRemove)
            e.Remove();

        foreach (var child in element.Elements())
            RemoveUnstable(child);
    }
}
