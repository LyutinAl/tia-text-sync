using Siemens.Engineering;
using System;

namespace TiaTextSync.Core;

/// <summary>
/// Управляет временем жизни процесса TIA Portal.
/// Приоритет: подключиться к уже запущенному процессу; если не найден — запустить новый.
///
/// Должен создаваться и использоваться исключительно на потоке <see cref="StaWorker"/>.
/// </summary>
internal sealed class TiaSession : IDisposable
{
    private readonly TiaPortal _tia;
    private readonly bool      _weStartedIt;

    public TiaPortal Portal => _tia;

    private TiaSession(TiaPortal tia, bool weStartedIt)
    {
        _tia          = tia;
        _weStartedIt  = weStartedIt;
    }

    public static TiaSession Connect(bool withUi = true, IProgress<string>? log = null)
    {
        var processes = TiaPortal.GetProcesses();

        if (processes.Count > 0)
        {
            if (processes.Count > 1)
                log?.Report($"Предупреждение: запущено {processes.Count} процессов TIA Portal, подключаемся к первому.");

            log?.Report($"Подключение к запущенному TIA Portal (PID {processes[0].Id})...");
            return new TiaSession(processes[0].Attach(), weStartedIt: false);
        }

        log?.Report("Запуск нового процесса TIA Portal...");
        var mode = withUi ? TiaPortalMode.WithUserInterface : TiaPortalMode.WithoutUserInterface;
        return new TiaSession(new TiaPortal(mode), weStartedIt: true);
    }

    /// <summary>Закрывает TIA Portal только если сессия сама его запустила.</summary>
    public void Dispose()
    {
        if (_weStartedIt)
            _tia.Dispose();
    }
}
