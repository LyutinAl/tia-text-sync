using TiaTextSync;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return;
}

string? projectPath = null;
string? outputDir   = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" or "-p" when i + 1 < args.Length:
            projectPath = args[++i]; break;
        case "--output" or "-o" when i + 1 < args.Length:
            outputDir = args[++i]; break;
    }
}

if (projectPath is null)
{
    Console.Error.WriteLine("Ошибка: укажите путь к проекту через --project <path>");
    PrintUsage();
    Environment.Exit(1);
    return;
}

using var session = TiaSession.Connect(withUi: true);

Console.WriteLine($"Открытие проекта: {projectPath}");

var reader = new ProjectReader(session);

// ── Режим экспорта ────────────────────────────────────────────────────────
if (outputDir is not null)
{
    var project  = reader.OpenProject(projectPath);
    var progress = new Progress<string>(Console.WriteLine);

    foreach (var plc in ProjectReader.GetAllPlc(project))
        new ExportService(plc, outputDir, progress).Export();

    new HwExportService(project, outputDir, progress).Export();

    Console.WriteLine($"\nЭкспорт завершён → {outputDir}");
    return;
}

// ── Режим списка блоков ────────────────────────────────────────────────────
var blocks = reader.ReadBlocks(projectPath);

Console.WriteLine();
Console.WriteLine($"Найдено блоков: {blocks.Count}");
Console.WriteLine();
Console.WriteLine($"{"ПЛК",-20} {"Вид",-12} {"Язык",-8} {"№",-6} {"Группа",-30} {"Имя",-40}");
Console.WriteLine(new string('-', 120));

foreach (var b in blocks)
    Console.WriteLine($"{b.PlcName,-20} {b.BlockKind,-12} {b.Language,-8} {b.Number,-6} {b.GroupPath,-30} {b.Name,-40}");

static void PrintUsage()
{
    Console.WriteLine("Использование: tia-text-sync --project <путь> [--output <папка>]");
    Console.WriteLine();
    Console.WriteLine("Параметры:");
    Console.WriteLine("  -p, --project <path>   Путь к файлу проекта TIA Portal");
    Console.WriteLine("  -o, --output  <dir>    Папка для экспорта (если не задана — список блоков)");
    Console.WriteLine("  -h, --help             Показать эту справку");
}
