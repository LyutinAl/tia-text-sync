using Siemens.Engineering.SW.Blocks;

namespace TiaTextSync;

/// <summary>Метаданные блока, собранные из проекта.</summary>
internal sealed record BlockInfo(
    string PlcName,
    string GroupPath,
    string Name,
    string BlockKind,              // "FB" | "FC" | "OB" | "GlobalDB" | "InstanceDB" | "ArrayDB"
    ProgrammingLanguage Language,  // SCL | LAD | FBD | STL | GRAPH | DB | ...
    int Number);
