using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using System;
using TiaTextSync.Core;

namespace TiaTextSync.Services;

/// <summary>
/// Компилирует программу ПЛК перед экспортом, чтобы TIA Portal не отклонял несогласованные блоки.
/// Должен выполняться на потоке <see cref="TiaTextSync.Core.StaWorker"/>.
/// </summary>
internal static class PlcCompiler
{
    /// <summary>
    /// Запускает компиляцию и возвращает число ошибок.
    /// Сбой компиляции не прерывает экспорт: блоки, которые остались несогласованными, получат свои WARN.
    /// </summary>
    public static int Compile(PlcSoftware plc, IProgress<string> progress)
    {
        progress.Report($"Компиляция ПЛК: {plc.Name}");
        try
        {
            var result = plc.GetService<ICompilable>().Compile();
            progress.Report($"Компиляция завершена: ошибок {result.ErrorCount}, предупреждений {result.WarningCount}");
            ReportErrors(result.Messages, progress);
            return result.ErrorCount;
        }
        catch (Exception ex)
        {
            progress.Report($"  WARN Компиляция {plc.Name}: {ex.Message}");
            Logger.Error($"PlcCompiler: ошибка компиляции '{plc.Name}'", ex);
            return 1;
        }
    }

    // Подробности лежат в самых вложенных сообщениях, верхние уровни только суммируют их
    private static void ReportErrors(CompilerResultMessageComposition messages, IProgress<string> progress)
    {
        foreach (CompilerResultMessage message in messages)
        {
            if (message.Messages.Count > 0)
            {
                ReportErrors(message.Messages, progress);
            }
            else if (message.State == CompilerResultState.Error)
            {
                var text = $"{message.Path}: {message.Description}";
                progress.Report($"  WARN {text}");
                Logger.Warn($"PlcCompiler: {text}");
            }
        }
    }
}
