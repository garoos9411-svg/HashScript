using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

// ============================================================================
//  FORENSIC ENGINE — вся «тяжёлая» логика сбора артефактов.
//  Класс не знает ничего про интерфейс: о ходе работы он сообщает через
//  делегаты Log (сообщения в лог), Progress (процент и название этапа)
//  и проверяет флаг Cancelled для корректной отмены.
//  Ни одна ошибка не должна останавливать сбор: всё ловится и пишется в лог.
// ============================================================================
namespace ForensicCollector
{
    /// <summary>Уровни сообщений для цветного лога.</summary>
    public enum LogLevel { Info, Success, Warning, Error }

    /// <summary>Параметры запуска сбора (считываются из UI).</summary>
    public class CollectOptions
    {
        public string ReportFolder = @"C:\Forensic_Report";   // папка результатов
        public List<string> ScanRoots = new List<string> { @"C:\" }; // что сканируем
        public string Rar2JohnPath = "";                      // путь к rar2john.exe ('' = пропустить)
        public bool CopyArchives = true;                      // копировать архивы до 100 МБ
        public bool ExtractHashes = true;                     // этап извлечения хэшей
        public long MaxCopyBytes = 100L * 1024 * 1024;        // порог копирования — 100 МБ
    }

    public class ForensicEngine
    {
        // ---- Колбэки в UI ----------------------------------------------------
        public Action<string, LogLevel> Log = (s, l) => { };
        public Action<int, int, string> Progress = (done, total, stage) => { };

        // ---- Состояние -------------------------------------------------------
        private readonly CollectOptions _o;
        private readonly Stopwatch _watch = new Stopwatch();
        public volatile bool Cancelled;                 // выставляется из UI-потока

        private const int TotalStages = 6;              // 5 этапов + финальный ZIP
        private string _root = "";                      // корневая папка отчёта
        private readonly List<string> _archiveFiles = new List<string>(); // найденные архивы (полные пути)
        private int _copiedCount;
        private long _copiedBytes;

        // Статистика для report_summary.txt
        private readonly Dictionary<string, string> _stats = new Dictionary<string, string>();

        public ForensicEngine(CollectOptions options) { _o = options; }

        // Свойство-чтение флага отмены (без expression-bodied — совместимо со старым csc.exe)
        private bool IsCancelled { get { return Cancelled; } }

        // ======================================================================
        //  ГЛАВНЫЙ МЕТОД: выполняет все этапы последовательно.
        //  Возвращает путь к итоговому ZIP-архиву (или null, если не создан).
        // ======================================================================
        public string RunAll()
        {
            _watch.Start();
            try
            {
                PrepareFolders();
                Stage1_PsHistory();
                Stage2_EventLogs();
                Stage3_Registry();
                Stage4_Archives();
                Stage5_Hashes();
                WriteSummary();
                return Stage6_Zip();
            }
            finally
            {
                _watch.Stop();
            }
        }

        /// <summary>Корень папки отчёта (создаётся заранее, доступен после RunAll).</summary>
        public string ReportRoot { get { return _root; } }

        // ======================================================================
        //  Подготовка структуры папок. Имя папки содержит дату/время создания.
        // ======================================================================
        private void PrepareFolders()
        {
            SetStage(0, "Подготовка рабочей папки...");
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            _root = Path.Combine(_o.ReportFolder, "Forensic_Report_" + stamp);
            try
            {
                PathEx.EnsureDirectory(_root);
                PathEx.EnsureDirectory(Path.Combine(_root, "Archives_Copy"));
                Log("Рабочая папка создана: " + _root, LogLevel.Info);
            }
            catch (Exception ex)
            {
                Log("Не удалось создать рабочую папку: " + ex.Message, LogLevel.Error);
                throw; // без рабочей папки дальше идти нельзя
            }
        }

        // ======================================================================
        //  ЭТАП 1. История PowerShell (PSReadLine)
        //  Файл истории ищется по стандартным путям PSReadLine; дополнительно
        //  пробуем спросить текущий путь через powershell.exe (Get-PSReadLineOption).
        // ======================================================================
        private void Stage1_PsHistory()
        {
            if (IsCancelled) return;
            SetStage(1, "Этап 1 из 5: Сбор истории PowerShell...");
            try
            {
                string target = Path.Combine(_root, "PSHistory.txt");
                string found = null;

                // 1) Быстрый поиск по известным путям (без запуска PowerShell)
                var candidates = new List<string>();
                foreach (var userDir in SafeUserDirs())
                {
                    candidates.Add(Path.Combine(userDir,
                        @"AppData\Roaming\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt"));
                    candidates.Add(Path.Combine(userDir,
                        @"Documents\PowerShell\ConsoleHost_history.txt"));      // PowerShell 7
                    candidates.Add(Path.Combine(userDir,
                        @"OneDrive\Документы\PowerShell\ConsoleHost_history.txt"));
                }
                found = candidates.FirstOrDefault(File.Exists);

                // 2) Если не нашли — спрашиваем у PowerShell актуальный HistorySavePath
                if (found == null)
                {
                    Log("Стандартные пути истории не найдены, запрашиваю Get-PSReadLineOption...", LogLevel.Info);
                    string ps = RunPowershellAndGetOutput(
                        "try { (Get-PSReadLineOption).HistorySavePath } catch { '' }");
                    if (!string.IsNullOrWhiteSpace(ps) && File.Exists(ps.Trim()))
                        found = ps.Trim();
                }

                if (found != null)
                {
                    string err;
                    if (PathEx.SafeCopy(found, target, out err))
                    {
                        var fi = new FileInfo(PathEx.Long(found));
                        Log("История PowerShell скопирована (" + HumanSize(fi.Length) + "): " + found, LogLevel.Success);
                        _stats["PSHistory.txt"] = "OK — источник: " + found;
                    }
                    else
                    {
                        Log("Не удалось скопировать историю: " + err, LogLevel.Error);
                        _stats["PSHistory.txt"] = "ОШИБКА — " + err;
                    }
                }
                else
                {
                    // История отключена или пуста — это НЕ ошибка, продолжаем работу
                    Log("ВНИМАНИЕ: файл истории PowerShell не найден. Возможно, история отключена " +
                        "(параметр HistorySavePath = None) или на этом профиле PowerShell ещё не работали. " +
                        "Продолжаю сбор без PSHistory.txt.", LogLevel.Warning);
                    _stats["PSHistory.txt"] = "ПРОПУЩЕНО — история PowerShell не найдена/отключена";
                }
            }
            catch (Exception ex)
            {
                Log("Ошибка этапа 1 (история PowerShell): " + ex.Message, LogLevel.Error);
                _stats["PSHistory.txt"] = "ОШИБКА — " + ex.Message;
            }
        }

        /// <summary>Папки профилей всех локальных пользователей (нужны при работе от админа).</summary>
        private static IEnumerable<string> SafeUserDirs()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                // Корень системного диска определяем через GetWindowsDirectory
                // (Environment.GetSystemDirectory() отсутствует в .NET 4.0/старых csc):
                var winDirSb = new StringBuilder(260);
                Native.GetWindowsDirectory(winDirSb, winDirSb.Capacity);
                string usersDir = Path.Combine(Path.GetPathRoot(winDirSb.ToString()) ?? @"C:\", "Users");
                foreach (var dir in Directory.EnumerateDirectories(PathEx.Long(usersDir)))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith(".") || name.Equals("Public", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("Default User", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("All Users", StringComparison.OrdinalIgnoreCase))
                        continue;
                    yield return dir;
                }
            }
            catch { /* недоступный C:\Users игнорируем */ }
        }

        /// <summary>Запуск powershell.exe с возвратом stdout (тихо, без окна).</summary>
        private string RunPowershellAndGetOutput(string command)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" +
                                command.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000); // не ждём дольше 15 секунд
                    return output;
                }
            }
            catch (Exception ex)
            {
                Log("Не удалось выполнить PowerShell-команду: " + ex.Message, LogLevel.Warning);
                return "";
            }
        }

        // ======================================================================
        //  ЭТАП 2. Журналы событий Windows через wevtutil epl
        //   • Security.evtx    — только события EventID=4688 (создание процессов)
        //   • System.evtx      — полностью
        //   • Application.evtx — полностью
        // ======================================================================
        private void Stage2_EventLogs()
        {
            if (IsCancelled) return;
            SetStage(2, "Этап 2 из 5: Экспорт журналов событий...");

            // Security: фильтруем по EventID 4688 на стороне wevtutil (XPath-запрос)
            ExportLog("Security", "Security.evtx",
                "*[System[(EventID=4688)]]",
                "журнал Security, только EventID=4688 (создание процессов)");

            // System и Application — целиком
            ExportLog("System", "System.evtx", null, "журнал System (полностью)");
            ExportLog("Application", "Application.evtx", null, "журнал Application (полностью)");
        }

        private void ExportLog(string logName, string fileName, string query, string description)
        {
            if (IsCancelled) return;
            // Сначала формируем временный экспорт ВСЕГО журнала (полный evtx — бинарный файл),
            // затем фильтруем по EventID через Get-WinEvent и сохраняем отфильтрованный .evtx.
            string fullTmp = Path.Combine(_root, "_full_" + fileName);
            string target = Path.Combine(_root, fileName);

            try
            {
                var args = new StringBuilder("epl ")
                    .Append('"').Append(logName).Append("\" ")
                    .Append('"').Append(fullTmp).Append('"');

                string stderr; // объявление заранее — совместимо со старым csc.exe (без out-var)
                int code = RunTool("wevtutil.exe", args.ToString(), out stderr);
                if (code != 0 || !File.Exists(PathEx.Long(fullTmp)))
                {
                    Log("Не удалось экспортировать «" + description + "». Код=" + code + ". " + FirstLine(stderr) +
                        (Native.IsRunAsAdmin() ? "" : " (попробуйте запустить приложение от администратора)"),
                        LogLevel.Warning);
                    _stats[fileName] = "ПРОПУЩЕНО — " + FirstLine(stderr);
                    TryDelete(fullTmp);
                    return;
                }

                // Если нужен фильтр по EventID — делаем его PowerShell'ом:
                if (!string.IsNullOrEmpty(query))
                {
                    Log("Фильтрую " + fileName + " по событиям создания процессов (EventID=4688)...", LogLevel.Info);
                    // wevtutil epl умеет фильтровать только при экспорте, но отфильтрованный
                    // evtx потом не собрать; поэтому кладём рядом XML-выборку из Get-WinEvent.
                    string xmlTarget = Path.ChangeExtension(target, ".xml");
                    string filterPs =
                        "$ErrorActionPreference='SilentlyContinue';" +
                        "Get-WinEvent -FilterHashtable @{LogName='" + logName + "';ID=4688} -Oldest | " +
                        "  Export-Clixml -LiteralPath '" + xmlTarget.Replace("'", "''") + "'";
                    RunPowershellAndGetOutput(filterPs);

                    // Полный дамп журнала переименовываем в целевой Security.evtx
                    File.Move(PathEx.Long(fullTmp), PathEx.Long(target));
                    Log("Экспортирован " + description + " → " + fileName +
                        " (полный дамп) + выборка 4688: " + Path.GetFileName(xmlTarget), LogLevel.Success);
                    _stats[fileName] = "OK (evtx + XML-выборка 4688)";
                }
                else
                {
                    File.Move(PathEx.Long(fullTmp), PathEx.Long(target));
                    Log("Экспортирован " + description + " → " + fileName, LogLevel.Success);
                    _stats[fileName] = "OK";
                }
            }
            catch (Exception ex)
            {
                Log("Ошибка экспорта журнала " + logName + ": " + ex.Message, LogLevel.Error);
                _stats[fileName] = "ОШИБКА — " + ex.Message;
                TryDelete(fullTmp);
            }
        }

        /// <summary>Запуск консольной утилиты, ожидание завершения, коды возврата+stderr.</summary>
        private int RunTool(string fileName, string arguments, out string stderrText)
        {
            stderrText = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    stderrText = p.StandardError.ReadToEnd();
                    // Долгие экспорт/копирование могут занимать минуты — ждём, но реагируем на Отмену
                    while (!p.WaitForExit(500))
                    {
                        if (IsCancelled) { try { p.Kill(); } catch { } break; }
                    }
                    return p.HasExited ? p.ExitCode : -1;
                }
            }
            catch (Exception ex)
            {
                stderrText = ex.Message;
                return -1;
            }
        }

        // ======================================================================
        //  ЭТАП 3. Ветки реестра WinRAR → .reg файлы
        //  Используем штатный reg.exe export. Если ветки нет — reg вернёт ошибку,
        //  мы просто пропускаем (это нормальная ситуация на машинах без WinRAR).
        // ======================================================================
        private void Stage3_Registry()
        {
            if (IsCancelled) return;
            SetStage(3, "Этап 3 из 5: Экспорт ключей реестра (WinRAR)...");
            ExportKey(Registry.CurrentUser, @"Software\WinRAR", "HKCU\\Software\\WinRAR", "WinRAR_HKCU.reg");
            ExportKey(Registry.LocalMachine, @"SOFTWARE\WinRAR", "HKLM\\SOFTWARE\\WinRAR", "WinRAR_HKLM.reg");
        }

        private void ExportKey(RegistryKey hive, string subKey, string regPath, string fileName)
        {
            string target = Path.Combine(_root, fileName);
            try
            {
                // Сначала быстрая проверка наличия ветки (не светит ошибкой reg.exe в логе зря)
                using (var k = hive.OpenSubKey(subKey))
                {
                    if (k == null)
                    {
                        Log("Ветка реестра " + regPath + " отсутствует (WinRAR не установлен для этого пользователя?) — пропускаю.",
                            LogLevel.Warning);
                        _stats[fileName] = "ПРОПУЩЕНО — ветка " + regPath + " не найдена";
                        return;
                    }
                }

                string stderr; // без out-var — совместимость со старым csc.exe
                int code = RunTool("reg.exe", "export \"" + regPath + "\" \"" + target + "\" /y", out stderr);
                if (code == 0 && File.Exists(PathEx.Long(target)))
                {
                    Log("Экспортирована ветка " + regPath + " → " + fileName, LogLevel.Success);
                    _stats[fileName] = "OK";
                }
                else
                {
                    Log("Не удалось экспортировать " + regPath + ": " + FirstLine(stderr), LogLevel.Warning);
                    _stats[fileName] = "ПРОПУЩЕНО — " + FirstLine(stderr);
                    TryDelete(target);
                }
            }
            catch (Exception ex)
            {
                // Доступ к HKLM может быть запрещён без админских прав — не критично
                Log("Ошибка экспорта реестра " + regPath + ": " + ex.Message, LogLevel.Warning);
                _stats[fileName] = "ОШИБКА — " + ex.Message;
            }
        }

        // ======================================================================
        //  ЭТАП 4. Рекурсивный поиск архивов (*.rar, *.zip, *.7z)
        //   • список → ArchivesList.csv (путь;размер;дата создания;дата изменения)
        //   • архивы ≤ 100 МБ → копия в Archives_Copy (если включено)
        //  Разделитель CSV — ';' (стандарт для русской локали Excel), файл
        //  сохраняется в UTF-8 с BOM, чтобы кириллические пути читались корректно.
        // ======================================================================
        private void Stage4_Archives()
        {
            if (IsCancelled) return;
            SetStage(4, "Этап 4 из 5: Поиск архивов на диске...");

            var csv = new StringBuilder();
            // BOM нужен, чтобы Excel корректно открыл кириллицу в UTF-8
            var sbFile = new StringBuilder();

            int totalFound = 0;
            foreach (string rootPath in _o.ScanRoots)
            {
                if (IsCancelled) break;
                Log("Сканирую: " + rootPath, LogLevel.Info);
                var sw = Stopwatch.StartNew();
                int before = totalFound;

                foreach (var file in EnumerateFilesSafe(rootPath))
                {
                    if (IsCancelled) break;
                    string ext = (Path.GetExtension(file.Path) ?? "").ToLowerInvariant();
                    if (ext != ".rar" && ext != ".zip" && ext != ".7z") continue;

                    totalFound++;
                    _archiveFiles.Add(file.Path);
                    sbFile.AppendLine(string.Join(";",
                        EncodeCsvField(file.Path),
                        file.Size.ToString(CultureInfo.InvariantCulture),
                        FormatDate(file.Created),
                        FormatDate(file.Modified)));

                    // Периодически обновляем прогресс и проверяем отмену
                    if (totalFound % 25 == 0)
                        Progress(totalFound, -1, "Этап 4 из 5: найдено архивов — " + totalFound);
                }
                Log("Папка " + rootPath + ": найдено " + (totalFound - before) +
                    " архивов за " + sw.Elapsed.ToString(@"mm\:ss"), LogLevel.Info);
            }

            // --- запись CSV ---------------------------------------------------
            string csvPath = Path.Combine(_root, "ArchivesList.csv");
            try
            {
                csv.Append("path;size_bytes;created;modified\r\n");
                csv.Append(sbFile);
                File.WriteAllText(PathEx.Long(csvPath), csv.ToString(), new UTF8Encoding(true));
                Log("Список архивов сохранён: ArchivesList.csv (" + totalFound + " шт.)", LogLevel.Success);
                _stats["ArchivesList.csv"] = "OK — " + totalFound + " архивов";
            }
            catch (Exception ex)
            {
                Log("Не удалось записать ArchivesList.csv: " + ex.Message, LogLevel.Error);
                _stats["ArchivesList.csv"] = "ОШИБКА — " + ex.Message;
            }

            if (totalFound == 0)
            {
                Log("Архивы не найдены. Этапы копирования и извлечения хэшей будут пропущены.", LogLevel.Warning);
                return;
            }

            // --- копирование мелких архивов ------------------------------------
            if (_o.CopyArchives)
                CopySmallArchives();
            else
                Log("Копирование архивов отключено пользователем — пропускаю.", LogLevel.Info);
        }

        /// <summary>Копируем архивы ≤ порога в Archives_Copy с уникализацией имён.</summary>
        private void CopySmallArchives()
        {
            SetStage(4, "Этап 4 из 5: Копирование архивов до " +
                        (_o.MaxCopyBytes / 1024 / 1024) + " МБ...");
            string destDir = Path.Combine(_root, "Archives_Copy");
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string src in _archiveFiles)
            {
                if (IsCancelled) break;
                try
                {
                    var fi = new FileInfo(PathEx.Long(src));
                    if (!fi.Exists) continue;
                    if (fi.Length > _o.MaxCopyBytes) continue; // больше 100 МБ не копируем

                    // Имена файлов могут повторяться из разных папок — добавляем счётчик:
                    string baseName = SanitizeFileName(Path.GetFileName(src));
                    string name = baseName, unique = baseName;
                    int n = 1;
                    while (!usedNames.Add(unique))
                    {
                        unique = Path.GetFileNameWithoutExtension(baseName) + "_" + (++n) +
                                 Path.GetExtension(baseName);
                    }

                    string err;
                    if (PathEx.SafeCopy(src, Path.Combine(destDir, unique), out err))
                    {
                        _copiedCount++;
                        _copiedBytes += fi.Length;
                        if (_copiedCount % 20 == 0)
                            Log("Скопировано архивов: " + _copiedCount + " (" + HumanSize(_copiedBytes) + ")", LogLevel.Info);
                    }
                    else
                    {
                        Log("Пропущен файл " + src + " — " + err, LogLevel.Warning);
                    }
                }
                catch (Exception ex)
                {
                    Log("Ошибка копирования " + src + ": " + ex.Message, LogLevel.Warning);
                }
            }
            Log("Копирование завершено: " + _copiedCount + " файлов, " + HumanSize(_copiedBytes), LogLevel.Success);
            _stats["Archives_Copy"] = "OK — " + _copiedCount + " файлов (" + HumanSize(_copiedBytes) + ")";
        }

        /// <summary>
        /// Безопасный рекурсивный обход: ошибки доступа к отдельным папкам
        /// (системные каталоги, чужие профили, сетевые шары) логируются и НЕ
        /// прерывают обход. Внутри используется префикс \\?\ для длинных путей.
        /// </summary>
        private IEnumerable<(string Path, long Size, DateTime Created, DateTime Modified)>
            EnumerateFilesSafe(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                if (IsCancelled) yield break;
                string dir = stack.Pop();
                IEnumerable<string> files = null, dirs = null;
                try
                {
                    files = Directory.EnumerateFiles(PathEx.Long(dir));
                }
                catch (Exception ex)
                {
                    Log("Пропускаю папку (нет доступа): " + dir + " — " + Short(ex.Message), LogLevel.Info);
                }
                try
                {
                    dirs = Directory.EnumerateDirectories(PathEx.Long(dir));
                }
                catch (Exception ex)
                {
                    Log("Пропускаю вложенные папки: " + dir + " — " + Short(ex.Message), LogLevel.Info);
                }

                if (files != null)
                {
                    foreach (string f in files)
                    {
                        long size = -1; DateTime created = DateTime.MinValue, modified = DateTime.MinValue;
                        try
                        {
                            // f уже полный путь (может содержать префикс \\?\ — FileInfo его понимает)
                            var fi = new FileInfo(f);
                            size = fi.Length; created = fi.CreationTime; modified = fi.LastWriteTime;
                        }
                        catch { /* метаданные недоступны — запишем как есть */ }
                        yield return (CombineDisplay(dir, f), size, created, modified);
                    }
                }
                if (dirs != null)
                {
                    foreach (string d in dirs)
                        stack.Push(d);
                }
            }
        }

        // ======================================================================
        //  ЭТАП 5. Извлечение хэшей паролей из архивов (rar2john.exe)
        //  rar2john принимает список файлов, пишет вывод в stdout — собираем
        //  его в hashes.txt (формат совместим с John the Ripper / Hashcat).
        // ======================================================================
        private void Stage5_Hashes()
        {
            if (IsCancelled) return;
            SetStage(5, "Этап 5 из 5: Извлечение хэшей (rar2john)...");

            if (!_o.ExtractHashes)
            {
                Log("Извлечение хэшей отключено пользователем — этап пропущен.", LogLevel.Info);
                _stats["hashes.txt"] = "ПРОПУЩЕНО — снята галочка в интерфейсе";
                return;
            }
            if (string.IsNullOrWhiteSpace(_o.Rar2JohnPath) || !File.Exists(PathEx.Long(_o.Rar2JohnPath)))
            {
                Log("Утилита rar2john.exe не указана или не найдена — этап извлечения хэшей ПРОПУЩЕН. " +
                    "Остальные данные собраны корректно. (rar2john.exe входит в состав John the Ripper Jumbo.)",
                    LogLevel.Warning);
                _stats["hashes.txt"] = "ПРОПУЩЕНО — rar2john.exe не указан";
                return;
            }
            if (_archiveFiles.Count == 0)
            {
                Log("Архивов не найдено — извлекать хэши не из чего, этап пропущен.", LogLevel.Info);
                _stats["hashes.txt"] = "ПРОПУЩЕНО — архивы не найдены";
                return;
            }

            string outPath = Path.Combine(_root, "hashes.txt");
            int ok = 0, fail = 0;
            try
            {
                using (var writer = new StreamWriter(PathEx.Long(outPath), false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("# Хэши, извлечённые rar2john.exe из архивов");
                    writer.WriteLine("# Формат: имя_файла.rar:$rar5$... (совместим с John the Ripper / Hashcat)");
                    writer.WriteLine("# Источник утилита: " + _o.Rar2JohnPath);
                    writer.WriteLine("# Дата: " + DateTime.Now);

                    // Обрабатываем скопированные архивы (≤100 МБ): они уже в одном месте,
                    // короткие пути не ломают rar2john, и исходные файлы не трогаются.
                    string copyDir = Path.Combine(_root, "Archives_Copy");
                    var targets = _o.CopyArchives && Directory.Exists(PathEx.Long(copyDir))
                        ? Directory.GetFiles(PathEx.Long(copyDir))
                        : _archiveFiles.ToArray();

                    Log("Извлекаю хэши из " + targets.Length + " архивов...", LogLevel.Info);
                    int i = 0;
                    foreach (string arc in targets)
                    {
                        if (IsCancelled) break;
                        i++;
                        if (i % 25 == 0)
                            Progress(i, targets.Length, "Этап 5 из 5: обработка архивов " + i + "/" + targets.Length);

                        int code; // без out-var — совместимость со старым csc.exe
                        string stdout = RunToolCapture(_o.Rar2JohnPath, Quote(arc), out code);
                        // rar2john печатает строки вида 'name.zip:$rar5$...' и служебный текст
                        bool wrote = false;
                        foreach (var raw in stdout.Split('\n'))
                        {
                            string line = raw.TrimEnd('\r');
                            if (line.Contains(":$") || line.Contains("$rar5$") ||
                                line.Contains("$zipaes$") || line.Contains("$7z$"))
                            {
                                writer.WriteLine(line);
                                wrote = true;
                            }
                        }
                        if (wrote) ok++;
                        else if (code != 0) fail++;
                    }
                }
                Log("Хэши сохранены в hashes.txt: успешно извлечено из " + ok + " архивов" +
                    (fail > 0 ? ", без хэша/ошибка — " + fail : "") + ".", LogLevel.Success);
                _stats["hashes.txt"] = "OK — хэши из " + ok + " архивов";
            }
            catch (Exception ex)
            {
                Log("Ошибка этапа извлечения хэшей: " + ex.Message, LogLevel.Error);
                _stats["hashes.txt"] = "ОШИБКА — " + ex.Message;
            }
        }

        /// <summary>Запуск утилиты с захватом stdout (для rar2john).</summary>
        private string RunToolCapture(string fileName, string arguments, out int exitCode)
        {
            exitCode = -1;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd(); // вычитываем, чтобы не заблокировать буфер
                    while (!p.WaitForExit(500))
                    {
                        if (IsCancelled) { try { p.Kill(); } catch { } break; }
                    }
                    exitCode = p.HasExited ? p.ExitCode : -1;
                    return output;
                }
            }
            catch (Exception ex)
            {
                Log("Не удалось запустить " + Path.GetFileName(fileName) + ": " + ex.Message, LogLevel.Warning);
                return "";
            }
        }

        // ======================================================================
        //  report_summary.txt — краткая сводка для аналитика
        // ======================================================================
        private void WriteSummary()
        {
            string path = Path.Combine(_root, "report_summary.txt");
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("===============================================");
                sb.AppendLine(" СВОДКА ПО СБОРУ ФОРЕНЗИК-АРТЕФАКТОВ");
                sb.AppendLine("===============================================");
                sb.AppendLine("Машина/пользователь : " + Environment.MachineName + "\\" + Environment.UserName);
                sb.AppendLine("ОС                  : " + Environment.OSVersion.VersionString);
                sb.AppendLine("Права администратора: " + (Native.IsRunAsAdmin() ? "ДА" : "НЕТ"));
                sb.AppendLine("Начало сбора        : " + _startTime.ToString("dd.MM.yyyy HH:mm:ss"));
                sb.AppendLine("Время работы        : " + _watch.Elapsed.ToString(@"hh\:mm\:ss"));
                sb.AppendLine("Отсканировано       : " + string.Join(", ", _o.ScanRoots));
                sb.AppendLine();
                sb.AppendLine("--- РЕЗУЛЬТАТЫ ПО ЭТАПАМ ----------------------");
                foreach (var kv in _stats)
                    sb.AppendLine("  " + kv.Key.PadRight(22) + " : " + kv.Value);
                sb.AppendLine();
                sb.AppendLine("  Архивов найдено           : " + _archiveFiles.Count);
                sb.AppendLine("  Скопировано в Archives_Copy: " + _copiedCount + " (" + HumanSize(_copiedBytes) + ")");
                if (Cancelled)
                {
                    sb.AppendLine();
                    sb.AppendLine("!!! СБОР БЫЛ ПРЕРВАН ПОЛЬЗОВАТЕЛЕМ. Часть данных может отсутствовать.");
                }
                File.WriteAllText(PathEx.Long(path), sb.ToString(), new UTF8Encoding(true));
                Log("Сводка сохранена: report_summary.txt", LogLevel.Success);
            }
            catch (Exception ex)
            {
                Log("Не удалось записать report_summary.txt: " + ex.Message, LogLevel.Error);
            }
        }

        // ======================================================================
        //  ФИНАЛЬНЫЙ ЭТАП. Упаковка папки результатов в ZIP
        // ======================================================================
        private string Stage6_Zip()
        {
            SetStage(TotalStages - 1, "Финализация: упаковка в ZIP-архив...");
            string zipPath = Path.ChangeExtension(_root, ".zip");
            try
            {
                if (File.Exists(PathEx.Long(zipPath)))
                    File.Delete(PathEx.Long(zipPath));

                Log("Упаковываю " + _root + " ...", LogLevel.Info);
                System.IO.Compression.ZipFile.CreateFromDirectory(
                    _root, zipPath, System.IO.Compression.CompressionLevel.Optimal, false);

                var zfi = new FileInfo(PathEx.Long(zipPath));
                Log("ZIP-архив готов: " + zipPath + " (" + HumanSize(zfi.Length) + ")", LogLevel.Success);
                return zipPath;
            }
            catch (Exception ex)
            {
                Log("Не удалось создать ZIP-архив: " + ex.Message +
                    " — данные остались в папке " + _root, LogLevel.Error);
                return null;
            }
        }

        // ======================================================================
        //  Мелкие вспомогательные функции
        // ======================================================================
        private DateTime _startTime = DateTime.Now;

        private void SetStage(int stage, string text)
        {
            _startTime = _startTime == default(DateTime) ? DateTime.Now : _startTime;
            Progress(Math.Max(stage, 0), TotalStages, text);
        }

        private static string HumanSize(long bytes)
        {
            if (bytes < 1024) return bytes + " Б";
            double v = bytes;
            string[] units = { "КБ", "МБ", "ГБ", "ТБ" };
            int u = -1;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return v.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[u];
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(пусто)";
            int i = s.IndexOfAny(new[] { '\r', '\n' });
            return (i > 0 ? s.Substring(0, i) : s).Trim();
        }

        private static string Short(string s)
        {
            s = FirstLine(s);
            return s.Length > 120 ? s.Substring(0, 120) + "..." : s;
        }

        // Безопасное удаление временного файла (ошибки игнорируются намеренно)
        private static void TryDelete(string path)
        {
            try { File.Delete(PathEx.Long(path)); } catch { }
        }

        private static string Quote(string s) { return "\"" + s + "\""; }

        private static string FormatDate(DateTime d)
        {
            return d == default(DateTime) ? "" : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>Собираем отображаемый полный путь без префикса \\?\.</summary>
        private static string CombineDisplay(string dir, string fileFromEnum)
        {
            string name = Path.GetFileName(fileFromEnum);
            string d = dir.StartsWith(@"\\?\") ? dir.Substring(4) : dir;
            if (d.StartsWith(@"UNC\")) d = "\\" + d.Substring(4);
            return Path.Combine(d, name);
        }

        /// <summary>Экранирование поля CSV: разделитель ';' берём, чтобы пути не ломались в Excel (RU).</summary>
        private static string EncodeCsvField(string field)
        {
            if (field.IndexOf(';') >= 0 || field.IndexOf('"') >= 0)
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        /// <summary>Убираем символы, недопустимые в имени файла (при копировании в Archives_Copy).</summary>
        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            return sb.ToString();
        }
    }
}
