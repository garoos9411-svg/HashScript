using System;
using System.Windows.Forms;

// ============================================================================
//  ТОЧКА ВХОДА приложения «Сбор форензик-артефактов»
//  Запуск: двойным кликом по ForensicCollector.exe
//  Требования: Windows 10/11 x64, .NET Framework 4.7.2+ (есть в Windows из коробки)
// ============================================================================
namespace ForensicCollector
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Включаем визуальные стили Win32-элементов (современный вид контролов)
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Аргумент /selfelev — признак того, что нас уже перезапустил UAC.
            // Обычный запуск (двойной клик): если прав администратора нет —
            // показываем окно с предложением перезапуститься через ShellExecute RunAs.
            bool selfElevated = args.Length > 0 && args[0] == "/selfelev";
            if (!selfElevated && !Native.IsRunAsAdmin())
            {
                using (var dlg = new ElevationPromptForm())
                {
                    if (dlg.ShowDialog() == DialogResult.Yes)
                    {
                        try
                        {
                            Native.RunElevated(Application.ExecutablePath, "/selfelev");
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show(
                                "Не удалось перезапустить приложение с правами администратора:\n" + ex.Message +
                                "\n\nЗапустите файл вручную через «ПКМ → Запуск от имени администратора».",
                                "Ошибка запуска", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        return; // текущий экземпляр закрываем
                    }
                    // Пользователь нажал «Нет» — продолжаем работу без прав администратора.
                    // Часть этапов (журнал Security, реестр HKLM) может завершиться предупреждением.
                }
            }

            Application.Run(new MainForm(selfElevated || Native.IsRunAsAdmin()));
        }
    }
}
