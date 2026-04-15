using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace TiaTextSync.Services;

/// <summary>
/// Удаляет нестабильные поля из экспортированных TIA XML-файлов,
/// чтобы Git diff оставался информативным. Поля вроде временных меток компиляции
/// и дат изменения меняются при каждой компиляции и создают шум в системе контроля версий.
/// </summary>
internal static class XmlNormalizer
{
    private static readonly HashSet<string> ElementsToRemove = new(StringComparer.OrdinalIgnoreCase)
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
        Strip(doc.Root!);
        doc.Save(xmlPath);
    }

    private static void Strip(XElement element)
    {
        element.Elements()
            .Where(e => ElementsToRemove.Contains(e.Name.LocalName))
            .ToList()
            .ForEach(e => e.Remove());

        foreach (var child in element.Elements())
            Strip(child);
    }
}
