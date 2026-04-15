using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TiaTextSync.Core;

/// <summary>
/// Выделенный STA-поток с «сырым» циклом сообщений Win32.
///
/// ── ПОЧЕМУ НЕ WinForms Application.Run ────────────────────────────────────────
/// Control.BeginInvoke и WindowsFormsSynchronizationContext.Post доставляют
/// продолжения через WndProc (обработчик сообщений Windows). Когда вызовы TIA
/// Openness — Projects.Open(), block.Export() и др. — выполняются внутри WndProc,
/// внутренний COM-механизм CoWaitForMultipleHandles не может свободно прокачивать
/// очередь сообщений, так как мы уже находимся внутри обработчика. COM-колбэки от
/// TIA Portal не доставляются → дедлок. Именно поэтому зависает и запуск на
/// UI-потоке (после await Task.Yield), и использование Control.BeginInvoke.
///
/// ── ПОЧЕМУ «СЫРОЙ» WIN32-ЦИКЛ РАБОТАЕТ ───────────────────────────────────────
/// PostThreadMessage отправляет «безоконные» сообщения (hwnd = NULL). GetMessage
/// возвращает их БЕЗ диспетчеризации через WndProc — они обрабатываются прямо
/// в теле цикла. Таким образом, работа с TIA выполняется в самом цикле сообщений,
/// а не внутри обработчика. CoWaitForMultipleHandles может свободно прокачивать
/// очередь во время любого блокирующего исходящего вызова, и колбэки TIA Portal
/// доставляются без дедлока. Это структурно идентично работе в режиме CLI:
/// STA-поток, синхронные вызовы, без участия WndProc.
/// </summary>
internal sealed class StaWorker : IDisposable
{
    #region Win32

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr  hwnd;
        public uint    message;
        public UIntPtr wParam;
        public IntPtr  lParam;
        public uint    time;
        public int     ptX, ptY;
    }

    private const uint WM_QUIT     = 0x0012;
    private const uint WM_USER_TIA = 0x8001;   // WM_APP + 1 — наше служебное сообщение

    #endregion

    private uint _nativeThreadId;
    private readonly ConcurrentQueue<Action> _queue = new();

    public StaWorker()
    {
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            _nativeThreadId = GetCurrentThreadId();
            ready.Set();
            RunLoop();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "TIA-STA";
        thread.Start();

        ready.Wait();
    }

    private void RunLoop()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) != 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == WM_USER_TIA)
            {
                // Безоконное сообщение — выполняется НЕ внутри WndProc.
                // Все задачи из очереди запускаются прямо в теле цикла сообщений.
                // COM может свободно прокачивать очередь во время блокирующих вызовов.
                while (_queue.TryDequeue(out var action))
                    action();
            }
            else
            {
                // Сообщения COM-апартамента и любых других окон на этом потоке.
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
    }

    /// <summary>Ставит задачу в очередь STA-потока и возвращает Task с результатом.</summary>
    public Task<T> RunAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            try   { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        PostThreadMessage(_nativeThreadId, WM_USER_TIA, UIntPtr.Zero, IntPtr.Zero);
        return tcs.Task;
    }

    /// <summary>Ставит задачу в очередь STA-потока (без возвращаемого значения).</summary>
    public Task RunAsync(Action work) =>
        RunAsync<object?>(() => { work(); return null; });

    /// <summary>Останавливает цикл сообщений и завершает поток.</summary>
    public void Dispose() =>
        PostThreadMessage(_nativeThreadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
}
