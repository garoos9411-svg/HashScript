using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

// ============================================================================
//  Вспомогательные функции Windows: права администратора, длинные пути,
//  открытие проводника.
// ============================================================================
namespace ForensicCollector
{
    internal static class Native
    {
        /// <summary>Win32: получение пути папки Windows (используется вместо
        /// Environment.GetSystemDirectory(), которого нет в .NET Framework 4.0).</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern uint GetWindowsDirectory(StringBuilder buffer, int size);

        /// <summary>Проверка: запущен ли процесс с правами администратора.</summary>
        public static bool IsRunAsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(id);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Перезапуск exe с правами администратора (UAC-запрос через ShellExecute "runas").
        /// </summary>
        public static void RunElevated(string exePath, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas" // ключевой момент — запрос повышения прав
            };
            Process.Start(psi);
        }

        /// <summary>Открывает проводник Windows по указанному пути (папка или файл).</summary>
        public static void OpenInExplorer(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path))
                    Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось открыть проводник: " + ex.Message,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // ==========================================================================
    //  Утилиты для работы с путями, включая поддержку длинных путей (>260 симв.)
    // ==========================================================================
    internal static class PathEx
    {
        /// <summary>
        /// Возвращает нормализованный абсолютный путь; если он длиннее 250 символов —
        /// добавляет префикс \\?\ , который заставляет Win32 API обходить лимит MAX_PATH.
        /// Все файловые операции в приложении идут через этот метод.
        /// </summary>
        public static string Long(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\")) return path;
            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
            // Убираем завершающий слэш (кроме корня диска), чтобы не ломать \\?\C:\
            if (full.Length > 3 && full.EndsWith("\\"))
                full = full.TrimEnd('\\');
            if (full.Length <= 250) return full;
            if (full.StartsWith(@"\\"))
                return @"\\?\UNC\" + full.Substring(2); // сетевой путь \\server\share
            return @"\\?\" + full;                      // локальный путь C:\...
        }

        /// <summary>Безопасное создание каталога (с поддержкой длинных путей).</summary>
        public static void EnsureDirectory(string dir)
        {
            Directory.CreateDirectory(Long(dir));
        }

        /// <summary>Безопасное копирование файла: ошибки сети/доступа не крашат приложение.</summary>
        public static bool SafeCopy(string source, string target, out string error)
        {
            error = null;
            try
            {
                EnsureDirectory(Path.GetDirectoryName(target));
                File.Copy(Long(source), Long(target), true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
