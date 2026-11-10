using System;
using System.IO;
using System.Reflection;

namespace TiaTextSync.Core;

/// <summary>
/// Загружает сборки Openness из установки TIA Portal V21.
/// Сборки Siemens.Engineering.* намеренно не копируются рядом с exe.
///
/// <see cref="Register"/> нужно вызвать до первого обращения к типам Siemens.
/// </summary>
internal static class OpennessResolver
{
    private static readonly string ApiDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Siemens", "Automation", "Portal V21", "PublicAPI", "V21", "net48");

    public static void Register()
    {
        if (Directory.Exists(ApiDir))
            Logger.Info($"OpennessResolver: папка API '{ApiDir}'");
        else
            Logger.Error($"OpennessResolver: папка API не найдена: '{ApiDir}'");

        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (!name.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = Path.Combine(ApiDir, $"{name}.dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
}
