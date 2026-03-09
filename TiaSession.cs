using Siemens.Engineering;
using System;
using System.Linq;

namespace TiaTextSync;

/// <summary>
/// Управляет соединением с TIA Portal.
/// Приоритет: подключение к уже запущенному процессу, иначе — запуск нового.
/// </summary>
internal sealed class TiaSession : IDisposable
{
    private readonly TiaPortal _tia;
    private readonly bool _weStartedIt;

    public TiaPortal Portal => _tia;

    private TiaSession(TiaPortal tia, bool weStartedIt)
    {
        _tia = tia;
        _weStartedIt = weStartedIt;
    }

    /// <summary>
    /// Подключается к уже запущенному TIA Portal.
    /// Если запущенных процессов нет — стартует новый.
    /// </summary>
    public static TiaSession Connect(bool withUi = true)
    {
        var processes = TiaPortal.GetProcesses();

        if (processes.Count > 0)
        {
            if (processes.Count > 1)
                Console.WriteLine($"Предупреждение: запущено {processes.Count} процессов TIA Portal, подключаемся к первому.");

            Console.WriteLine($"Подключение к запущенному TIA Portal (PID {processes[0].Id})...");
            return new TiaSession(processes[0].Attach(), weStartedIt: false);
        }

        Console.WriteLine("Запуск нового процесса TIA Portal...");
        var mode = withUi ? TiaPortalMode.WithUserInterface : TiaPortalMode.WithoutUserInterface;
        return new TiaSession(new TiaPortal(mode), weStartedIt: true);
    }

    /// <summary>Dispose закрывает TIA Portal только если мы его сами запустили.</summary>
    public void Dispose()
    {
        if (_weStartedIt)
            _tia.Dispose();
    }
}
