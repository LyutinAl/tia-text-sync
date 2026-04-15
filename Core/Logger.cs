using System;
using System.IO;
using System.Threading;

namespace TiaTextSync.Core;

/// <summary>
/// Потокобезопасный логгер в файл с временными метками и ID потока.
/// Инициализируется один раз через <see cref="Init"/> при старте приложения.
/// </summary>
internal static class Logger
{
    private static StreamWriter? _writer;
    private static readonly object _lock = new();

    public static void Init(string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        _writer = new StreamWriter(logPath, append: true, System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };
        Info("=== Сессия начата ===");
    }

    public static void Info(string message)  => Write("INFO ", message);
    public static void Warn(string message)  => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex is null ? message : $"{message}\n  {ex}");
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [T{Thread.CurrentThread.ManagedThreadId:D2}] {message}";
        lock (_lock)
        {
            _writer?.WriteLine(line);
        }
        System.Diagnostics.Debug.WriteLine(line);
    }

    public static void Close()
    {
        Info("=== Сессия завершена ===");
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
