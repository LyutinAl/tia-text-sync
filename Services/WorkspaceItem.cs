using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static TiaTextSync.Services.PathNames;

namespace TiaTextSync.Services;

/// <summary>
/// Файлы одного элемента ПЛК в рабочей папке: одна папка и одно имя без расширения,
/// например blocks/Motors/Motor_FB.xml и Motor_FB.lad.
/// </summary>
internal sealed class WorkspaceItem
{
    private const string SystemBlocksFolder = PlcCatalog.BlocksRoot + "/System blocks";

    private readonly Dictionary<string, string> _files;

    private WorkspaceItem(string folder, string stem, Dictionary<string, string> files)
    {
        Folder = folder;
        Stem   = stem;
        _files = files;
    }

    /// <summary>Папка относительно папки ПЛК, например «blocks/Motors».</summary>
    public string Folder { get; }

    /// <summary>Имя файлов без расширения.</summary>
    public string Stem { get; }

    /// <summary>Полные пути файлов по расширению.</summary>
    public IReadOnlyDictionary<string, string> Files => _files;

    /// <summary>Ключ узла дерева. Отличается от <see cref="PlcItem.Key"/>: такого элемента может не быть в TIA.</summary>
    public string Key => $"File:{Folder}/{Stem}";

    /// <summary>Корневая папка вида элемента: blocks, tags, types или tech.</summary>
    public string Root => Folder.Split('/')[0];

    /// <summary>Имена папок групп от корня к элементу.</summary>
    public IReadOnlyList<string> Groups => Folder.Split('/').Skip(1).ToList();

    /// <summary>Системные блоки создаёт сам TIA Portal, их не импортируют.</summary>
    public bool IsSystem =>
        Folder.Equals(SystemBlocksFolder, StringComparison.OrdinalIgnoreCase)
        || Folder.StartsWith(SystemBlocksFolder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Файл, из которого элемент загружается в TIA Portal, или null, если формат не импортируется:
    /// текстовые виды .lad/.fbd только для чтения, а технологические объекты принимаются только в XML.
    /// </summary>
    public string? ImportFile
    {
        get
        {
            var extensions = Root switch
            {
                PlcCatalog.BlocksRoot => new[] { ".xml", ".scl", ".awl", ".db" },
                PlcCatalog.TagsRoot   => new[] { ".xml" },
                PlcCatalog.TypesRoot  => new[] { ".udt", ".scl" },  // .scl — прежний формат
                PlcCatalog.TechRoot   => new[] { ".xml" },
                _                     => Array.Empty<string>()
            };
            return extensions.Select(ext => _files.TryGetValue(ext, out var path) ? path : null)
                             .FirstOrDefault(path => path is not null);
        }
    }

    /// <summary>Относятся ли эти файлы к элементу ПЛК.</summary>
    public bool Matches(PlcItem item) =>
        string.Equals(Folder, item.Folder, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Stem, SafeName(item.Name), StringComparison.OrdinalIgnoreCase);

    /// <summary>Файлы элемента ПЛК в папке <paramref name="plcDir"/>. Если файлов нет, <see cref="Files"/> пуст.</summary>
    public static WorkspaceItem ForItem(string plcDir, PlcItem item)
    {
        var stem  = SafeName(item.Name);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dir   = ExportService.ItemDir(plcDir, item);

        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.GetFiles(dir, stem + ".*"))
            {
                var ext = Path.GetExtension(file);
                if (string.Equals(Path.GetFileNameWithoutExtension(file), stem, StringComparison.OrdinalIgnoreCase)
                    && ExportService.ManagedExtensions.Contains(ext))
                    files[ext] = file;
            }
        }
        return new WorkspaceItem(item.Folder, stem, files);
    }

    /// <summary>
    /// Элементы рабочей папки, которых нет среди <paramref name="all"/>:
    /// удалены в TIA Portal или добавлены в рабочую папку через Git.
    /// </summary>
    public static List<WorkspaceItem> CollectOrphans(string plcDir, IReadOnlyList<PlcItem> all)
    {
        var owned  = new HashSet<string>(all.Select(i => $"{i.Folder}/{SafeName(i.Name)}"), StringComparer.OrdinalIgnoreCase);
        var result = new List<WorkspaceItem>();
        var byKey  = new Dictionary<string, WorkspaceItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ExportService.ItemRoots)
        {
            var dir = Path.Combine(plcDir, root);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var ext = Path.GetExtension(file);
                if (!ExportService.ManagedExtensions.Contains(ext))
                    continue;

                var folder = Path.GetDirectoryName(file)!.Substring(plcDir.Length + 1).Replace('\\', '/');
                var stem   = Path.GetFileNameWithoutExtension(file);
                var key    = $"{folder}/{stem}";
                if (owned.Contains(key))
                    continue;

                if (!byKey.TryGetValue(key, out var item))
                {
                    item = new WorkspaceItem(folder, stem, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    byKey[key] = item;
                    result.Add(item);
                }
                item._files[ext] = file;
            }
        }
        return result;
    }
}
