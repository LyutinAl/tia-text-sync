using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using TiaTextSync.Core;
using TiaTextSync.UI;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TiaTextSync", "logs", $"{DateTime.Now:yyyy-MM-dd}.log");

        Logger.Init(logPath);
        OpennessResolver.Register();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) =>
        {
            Logger.Error("Необработанное исключение UI-потока", e.Exception);
            MessageBox.Show(e.Exception.ToString(), "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        try
        {
            Logger.Info("Запуск приложения");
            RunMainForm();
            Logger.Info("Приложение завершено");
        }
        catch (Exception ex)
        {
            Logger.Error("Исключение при запуске", ex);
            MessageBox.Show(ex.ToString(), "Ошибка запуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Logger.Close();
        }
    }

    // Отдельный метод: типы Siemens в MainForm не должны загружаться при JIT-компиляции Main,
    // то есть раньше регистрации OpennessResolver.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunMainForm() => Application.Run(new MainForm());
}
