using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

// ============================================================================
//  ГЛАВНОЕ ОКНО приложения «Сбор форензик-артефактов v1.0».
//  Здесь только интерфейс: настройки, лог, прогресс, кнопки.
//  Сам сбор выполняет ForensicEngine в фоновом потоке (BackgroundWorker),
//  поэтому окно не зависает и кнопка «Отмена» всегда реагирует.
// ============================================================================
namespace ForensicCollector
{
    public class MainForm : Form
    {
        // ---- Элементы управления (создаются в BuildUi) -----------------------
        private TextBox txtReportFolder;   // папка для сохранения отчёта
        private TextBox txtScanRoots;      // диски/папки для сканирования (через ;)
        private TextBox txtRar2John;       // путь к rar2john.exe (+ drag-n-drop)
        private CheckBox chkCopyArchives;  // копировать архивы до 100 МБ
        private CheckBox chkExtractHashes; // извлекать хэши
        private Button btnStart, btnCancel, btnTicket;
        private ProgressBar progressBar;
        private Label lblStage;            // подпись «Этап N из 5: ...»
        private RichTextBox rtbLog;        // цветной лог
        private StatusStrip statusStrip;
        private ToolStripStatusLabel lblStatus, lblElapsed;

        // ---- Состояние --------------------------------------------------------
        private readonly bool _isAdmin;
        private BackgroundWorker _worker;
        private ForensicEngine _engine;
        private readonly Stopwatch _runWatch = new Stopwatch();
        private Timer _uiTimer;            // обновляет счётчик времени в статус-баре
        private string _lastZipPath;       // путь к итоговому ZIP (для кнопки «Открыть папку»)

        private const string ToolTipText =
            "«Папка для сохранения» — куда положить результаты.\n" +
            "«Диски/папки» — что сканировать на архивы; несколько путей через «;».\n" +
            "«rar2john.exe» — утилита из John the Ripper Jumbo для извлечения хэшей паролей.\n" +
            "Можно перетащить файл rar2john.exe мышью прямо в поле.";

        public MainForm(bool isAdmin)
        {
            _isAdmin = isAdmin;
            BuildUi();
            AddLog("Приложение запущено. Права администратора: " + (_isAdmin ? "ЕСТЬ" : "НЕТ (часть этапов может быть недоступна)"),
                _isAdmin ? LogLevel.Info : LogLevel.Warning);
            AddLog("Заполните настройки (обычно достаточно значений по умолчанию) и нажмите «▶ Начать сбор».", LogLevel.Info);
        }

        /// <summary>Потокобезопасная запись в лог из UI-потока.</summary>
        private void AddLog(string message, LogLevel level) { Engine_Log(message, level); }

        // ======================================================================
        //  ПОСТРОЕНИЕ ИНТЕРФЕЙСА (без designer.cs — всё кодом, чтобы собрать
        //  одним csc.exe без Visual Studio).
        // ======================================================================
        private void BuildUi()
        {
            Text = "Сбор форензик-артефактов v1.0";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(860, 640);
            ClientSize = new Size(900, 660);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ---------- Верхняя группа настроек ----------
            var grp = new GroupBox
            {
                Text = " Настройки сбора ",
                Location = new Point(12, 8),
                Size = new Size(876, 190),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };

            int y = 28;
            // Переменные объявляем заранее — старый csc.exe (.NET Framework) не поддерживает «out var» (C# 7)
            Button btnBrowseReport, btnBrowseScan, btnBrowseR2J;
            txtReportFolder = AddSettingRow(grp, ref y, "Папка для сохранения отчёта:",
                DefaultReportFolder(),
                "Куда сохранить результаты. Будет создана подпапка Forensic_Report_<дата_время>.",
                out btnBrowseReport);
            btnBrowseReport.Click += (s, e) => BrowseFolder(txtReportFolder);

            txtScanRoots = AddSettingRow(grp, ref y, "Диски/папки для сканирования:",
                @"C:\",
                "Где искать архивы (*.rar, *.zip, *.7z). Несколько путей через «;», например: C:\\;D:\\Work",
                out btnBrowseScan);
            btnBrowseScan.Click += (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { Description = "Выберите диск или папку для сканирования" })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        // добавляем выбранный путь к списку, если его там ещё нет
                        var list = ParseScanRoots();
                        if (!list.Any(p => string.Equals(p.TrimEnd('\\'), dlg.SelectedPath.TrimEnd('\\'),
                                StringComparison.OrdinalIgnoreCase)))
                            list.Add(dlg.SelectedPath);
                        txtScanRoots.Text = string.Join(";", list);
                    }
                }
            };

            txtRar2John = AddSettingRow(grp, ref y, "Путь к rar2john.exe (необязательно):",
                "",
                "Утилита rar2john.exe из состава John the Ripper Jumbo. Без неё этап хэшей будет пропущен. " +
                "Можно перетащить файл мышью прямо в это поле.",
                out btnBrowseR2J);
            btnBrowseR2J.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog
                {
                    Title = "Выберите rar2john.exe",
                    Filter = "rar2john.exe|rar2john.exe|Все файлы (*.*)|*.*"
                })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                        txtRar2John.Text = dlg.FileName;
                }
            };
            // Drag-n-drop файла rar2john.exe прямо в поле
            txtRar2John.AllowDrop = true;
            txtRar2John.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            txtRar2John.DragDrop += (s, e) =>
            {
                try
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        txtRar2John.Text = files[0];
                        AddLog("Файл принят через drag-n-drop: " + files[0], LogLevel.Info);
                    }
                }
                catch (Exception ex) { AddLog("Ошибка приёма файла: " + ex.Message, LogLevel.Error); }
            };

            // ---------- Чекбоксы ----------
            chkCopyArchives = new CheckBox
            {
                Text = "Копировать архивы до 100 МБ в отчёт (Archives_Copy)",
                Checked = true,
                Location = new Point(14, y + 6),
                AutoSize = true
            };
            chkExtractHashes = new CheckBox
            {
                Text = "Извлекать хэши паролей из архивов (hashes.txt)",
                Checked = true,
                Location = new Point(430, y + 6),
                AutoSize = true
            };
            grp.Controls.Add(chkCopyArchives);
            grp.Controls.Add(chkExtractHashes);

            // ---------- Кнопки запуска/отмены ----------
            btnStart = new Button
            {
                Text = "▶  НАЧАТЬ СБОР",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                Size = new Size(260, 52),
                Location = new Point((ClientSize.Width - 260) / 2, 206),
                Anchor = AnchorStyles.Top,
                Name = "btnStart",
            };
            Theme.StyleButton(btnStart, Theme.Accent);
            btnStart.Click += (s, e) => StartCollection();

            btnCancel = new Button
            {
                Text = "⏹ Отмена",
                Size = new Size(120, 52),
                Location = new Point(btnStart.Right + 12, 206),
                Enabled = false,
                Name = "btnCancel",
            };
            btnCancel.Click += (s, e) => CancelCollection();

            // Бонус: заглушка тикета в Jira/ServiceNow
            btnTicket = new Button
            {
                Text = "📋 Создать задачу в Jira/ServiceNow",
                Size = new Size(250, 52),
                Location = new Point(btnStart.Left - 262, 206),
                Visible = false, // появляется после завершения сбора
            };
            btnTicket.Click += (s, e) => ShowTicketStub();

            // ---------- Прогресс ----------
            lblStage = new Label
            {
                Text = "Готов к запуску",
                Location = new Point(14, 272),
                AutoSize = true,
                Tag = "dim",
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            progressBar = new ProgressBar
            {
                Location = new Point(14, 294),
                Size = new Size(872, 22),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0, Maximum = 100,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };

            // ---------- Лог ----------
            rtbLog = new RichTextBox
            {
                Location = new Point(14, 326),
                Size = new Size(872, 280),
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9f),
                BackColor = Theme.Input,
                ForeColor = Theme.Text,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                AllowDrop = true,
            };
            // общий drag-n-drop на окно лога тоже принимает rar2john.exe
            rtbLog.DragEnter += txtRar2John_DragEnter_Generic;
            rtbLog.DragDrop += RtbLog_DragDrop;

            // ---------- Статус-бар ----------
            statusStrip = new StatusStrip { BackColor = Theme.Panel };
            lblStatus = new ToolStripStatusLabel("Готов") { ForeColor = Theme.TextDim };
            lblElapsed = new ToolStripStatusLabel("") { ForeColor = Theme.TextDim, Spring = false, Alignment = ToolStripItemAlignment.Right };
            statusStrip.Items.AddRange(new ToolStripItem[] { lblStatus, lblElapsed });

            Controls.AddRange(new Control[] { grp, btnStart, btnCancel, btnTicket, lblStage, progressBar, rtbLog, statusStrip });

            // Общая подсказка «?» на группе настроек
            var tip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 300, ReshowDelay = 100 };
            tip.SetToolTip(grp, ToolTipText);
            tip.SetToolTip(lblStage, "Ход выполнения: текущий этап и прогресс.");

            Theme.Apply(this);
            Load += (s, e) => Theme.EnableDarkTitleBar(this);

            // Таймер обновления времени работы в статус-баре
            _uiTimer = new Timer { Interval = 1000 };
            _uiTimer.Tick += (s, e) =>
            {
                if (_runWatch.IsRunning)
                    lblElapsed.Text = "Время работы: " + _runWatch.Elapsed.ToString(@"hh\:mm\:ss");
            };
        }

        private void txtRar2John_DragEnter_Generic(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void RtbLog_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0 &&
                    files[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    txtRar2John.Text = files[0];
            }
            catch (Exception ex) { AddLog("Ошибка приёма файла: " + ex.Message, LogLevel.Error); }
        }

        /// <summary>Создаёт строку «метка | поле | кнопка Обзор | ?» внутри группы.</summary>
        private TextBox AddSettingRow(GroupBox grp, ref int y, string caption, string value,
            string help, out Button browseBtn)
        {
            var lbl = new Label
            {
                Text = caption,
                Location = new Point(14, y + 4),
                Size = new Size(230, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };
            var tb = new TextBox
            {
                Text = value,
                Location = new Point(250, y),
                Size = new Size(460, 26),   // оставляем место под иконку «?» и кнопку «Обзор…»
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            browseBtn = new Button
            {
                Text = "Обзор…",
                Location = new Point(778, y - 1),
                Size = new Size(70, 27),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            var hint = new Label
            {
                Text = "?",
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Location = new Point(720, y + 3),   // маленькая иконка «?» сразу после текстового поля
                Size = new Size(18, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Theme.Accent,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            var tip = new ToolTip { AutoPopDelay = 10000 };
            tip.SetToolTip(hint, help);
            tip.SetToolTip(tb, help);
            grp.Controls.AddRange(new Control[] { lbl, tb, browseBtn, hint });
            y += 38;
            return tb;
        }

        private static string DefaultReportFolder()
        {
            // C:\Forensic_Report — как в ТЗ; если C:\ недоступен на запись, пользователь сам выберет другое место
            return @"C:\Forensic_Report";
        }

        private List<string> ParseScanRoots()
        {
            return txtScanRoots.Text
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();
        }

        // ======================================================================
        //  ЗАПУСК СБОРА (проверки → фоновый поток)
        // ======================================================================
        private void StartCollection()
        {
            try
            {
                // --- валидация настроек --------------------------------------
                string reportFolder = txtReportFolder.Text.Trim();
                if (string.IsNullOrEmpty(reportFolder))
                {
                    MessageBox.Show(this, "Укажите папку для сохранения отчёта.", "Проверьте настройки",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                var roots = ParseScanRoots();
                if (roots.Count == 0)
                {
                    MessageBox.Show(this, "Укажите хотя бы один диск или папку для сканирования.",
                        "Проверьте настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // --- проверка свободного места ДО начала сбора ----------------
                long required = EstimateRequiredBytes(roots);
                foreach (string root in roots.Concat(new[] { reportFolder }))
                {
                    string drive = GetDriveOf(root);
                    if (drive == null) continue;
                    try
                    {
                        var di = new DriveInfo(drive);
                        if (!di.IsReady)
                        {
                            AddLog("Диск " + di.Name + " недоступен (сетевой/извлечённый?) — связанные пути будут пропущены.",
                                LogLevel.Warning);
                            continue;
                        }
                        if (di.AvailableFreeSpace < required)
                        {
                            var res = MessageBox.Show(this,
                                "На диске " + di.Name + " свободно " + HumanSize(di.AvailableFreeSpace) +
                                ", а для комфортной работы нужно примерно " + HumanSize(required) +
                                ".\n\nНачать сбор всё равно?",
                                "Не хватает места на диске",
                                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                            if (res != DialogResult.Yes) return;
                            break; // спросили один раз — не надо повторять на каждом пути
                        }
                    }
                    catch (Exception ex)
                    {
                        AddLog("Не удалось проверить диск для пути «" + root + "»: " + ex.Message, LogLevel.Warning);
                    }
                }

                // --- собираем параметры ---------------------------------------
                var options = new CollectOptions
                {
                    ReportFolder = reportFolder,
                    ScanRoots = roots,
                    Rar2JohnPath = txtRar2John.Text.Trim(),
                    CopyArchives = chkCopyArchives.Checked,
                    ExtractHashes = chkExtractHashes.Checked,
                };

                _engine = new ForensicEngine(options);
                _engine.Log += Engine_Log;          // сообщения фонового потока → в цветной лог
                _engine.Progress += Engine_Progress;

                // --- BackgroundWorker: сбор в отдельном потоке, UI не висит ---
                _worker = new BackgroundWorker { WorkerSupportsCancellation = true };
                _worker.DoWork += (s, ev) => { ev.Result = _engine.RunAll(); };
                _worker.RunWorkerCompleted += Worker_Completed;

                SetRunningState(true);
                _runWatch.Restart();
                _uiTimer.Start();
                AddLog("=== НАЧАЛО СБОРА ===", LogLevel.Success);
                _worker.RunWorkerAsync();
            }
            catch (Exception ex)
            {
                // Любая неожиданная ошибка не должна крашить приложение
                AddLog("Ошибка запуска сбора: " + ex.Message, LogLevel.Error);
                SetRunningState(false);
            }
        }

        /// <summary>Очень грубая оценка нужного места: минимум 2 ГБ запаса на evtx/копии архивов/ZIP.</summary>
        private long EstimateRequiredBytes(List<string> roots)
        {
            return 2L * 1024 * 1024 * 1024; // предупреждение показывается только при нехватке — это нормально
        }

        private static string GetDriveOf(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                return string.IsNullOrEmpty(root) ? null : root;
            }
            catch { return null; }
        }

        // ======================================================================
        //  ОТМЕНА
        // ======================================================================
        private void CancelCollection()
        {
            if (_engine == null) return;
            _engine.Cancelled = true;               // мягкий флаг — engine прервёт циклы
            if (_worker != null) _worker.CancelAsync();
            lblStatus.Text = "Отмена... уже собранные данные будут сохранены";
            AddLog("Пользователь нажал «Отмена». Прерываю сбор, собранные данные сохраняются...", LogLevel.Warning);
            btnCancel.Enabled = false;              // защита от двойного нажатия
        }

        // ======================================================================
        //  ЗАВЕРШЕНИЕ ФОНОВОГО ПОТОКА
        // ======================================================================
        private void Worker_Completed(object sender, RunWorkerCompletedEventArgs ev)
        {
            _uiTimer.Stop();
            _runWatch.Stop();
            SetRunningState(false);

            string zip = null;
            if (ev.Error != null)
            {
                // Исключение фона — пишем в лог, приложение живёт
                AddLog("Критическая ошибка сбора: " + ev.Error.Message, LogLevel.Error);
                lblStatus.Text = "Сбор завершён с ошибкой";
            }
            else
            {
                zip = ev.Result as string;
            }

            _lastZipPath = zip;
            btnTicket.Visible = true;

            if (_engine != null && Directory.Exists(PathEx.Long(_engine.ReportRoot)))
            {
                AddLog("=== СБОР ЗАВЕРШЁН за " + _runWatch.Elapsed.ToString(@"hh\:mm\:ss") + " ===", LogLevel.Success);

                if (zip != null)
                {
                    progressBar.Value = 100;
                    lblStage.Text = "Готово! Архив создан.";
                    lblStatus.Text = "Готово";

                    // Модальное окно «Готово» с кнопками «Открыть папку» / «Закрыть»
                    using (var done = new DoneForm(zip, _engine.ReportRoot))
                        done.ShowDialog(this);
                }
                else
                {
                    lblStage.Text = "Сбор прерван или ZIP не создан — данные в рабочей папке.";
                    Native.OpenInExplorer(_engine.ReportRoot);
                }
            }
            else
            {
                lblStatus.Text = "Прервано";
            }
        }

        /// <summary>Блокировка/разблокировка элементов во время работы.</summary>
        private void SetRunningState(bool running)
        {
            btnStart.Enabled = !running;
            btnCancel.Enabled = running;
            btnTicket.Visible = !running && btnTicket.Tag != null; // после первого успеха остаётся видимой
            if (!running && _lastZipPath != null) btnTicket.Visible = true;
            txtReportFolder.Enabled = !running;
            txtScanRoots.Enabled = !running;
            txtRar2John.Enabled = !running;
            chkCopyArchives.Enabled = !running;
            chkExtractHashes.Enabled = !running;
            foreach (Control c in Controls)
            {
                // «as» вместо pattern matching «is GroupBox g» — совместимо со старым csc.exe (C# 5)
                GroupBox g = c as GroupBox;
                if (g != null)
                {
                    foreach (Control gc in g.Controls)
                    {
                        Button b = gc as Button;
                        if (b != null && b.Text.StartsWith("Обзор"))
                            b.Enabled = !running;
                    }
                }
            }
            if (!running) lblStage.Text = string.IsNullOrEmpty(lblStage.Text) || lblStage.Text.StartsWith("Готов")
                ? "Готов к запуску" : lblStage.Text;
        }

        // ======================================================================
        //  ЛОГ и ПРОГРЕСС из фонового потока (InvokeRequired → Invoke)
        // ======================================================================
        private void Engine_Log(string message, LogLevel level)
        {
            if (rtbLog.InvokeRequired)
            {
                rtbLog.BeginInvoke(new Action<string, LogLevel>(Engine_Log), message, level);
                return;
            }
            Color color;
            string prefix;
            switch (level)
            {
                case LogLevel.Success: color = Theme.Success; prefix = "[OK ] "; break;
                case LogLevel.Warning: color = Theme.Warning; prefix = "[!! ] "; break;
                case LogLevel.Error:   color = Theme.Danger;  prefix = "[ERR] "; break;
                default:               color = Theme.Text;    prefix = "[....] "; break;
            }
            AppendLogLine(DateTime.Now.ToString("HH:mm:ss") + " " + prefix + message, color);
            lblStatus.Text = message.Length > 90 ? message.Substring(0, 90) + "..." : message;
        }

        private void AppendLogLine(string text, Color color)
        {
            try
            {
                int start = rtbLog.TextLength;
                rtbLog.AppendText(text + Environment.NewLine);
                rtbLog.Select(start, text.Length);
                rtbLog.SelectionColor = color;
                rtbLog.Select(rtbLog.TextLength, 0);
                rtbLog.ScrollToCaret();

                // Не даём логу разрастаться бесконечно (быстрый диск даёт тысячи строк)
                if (rtbLog.Lines.Length > 5000)
                {
                    var keep = rtbLog.Lines.Skip(rtbLog.Lines.Length - 4000).ToArray();
                    rtbLog.Clear();
                    rtbLog.AppendText(string.Join(Environment.NewLine, keep) + Environment.NewLine);
                }
            }
            catch { /* лог не должен ронять приложение никогда */ }
        }

        private void Engine_Progress(int done, int total, string stageText)
        {
            if (progressBar.InvokeRequired)
            {
                progressBar.BeginInvoke(new Action<int, int, string>(Engine_Progress), done, total, stageText);
                return;
            }
            try
            {
                if (total > 0)
                {
                    progressBar.Style = ProgressBarStyle.Continuous;
                    int percent = (int)Math.Min(100, done * 100.0 / total);
                    progressBar.Value = Math.Max(progressBar.Value, percent); // прогресс не откатывается назад
                }
                else
                {
                    progressBar.Style = ProgressBarStyle.Marquee; // фаза неизвестной длительности
                }
                if (!string.IsNullOrEmpty(stageText))
                    lblStage.Text = stageText;
            }
            catch { }
        }

        // ======================================================================
        //  Обработчики «Обзор…» и вспомогательные функции UI
        // ======================================================================
        private void BrowseFolder(TextBox target)
        {
            using (var dlg = new FolderBrowserDialog { Description = "Выберите папку для сохранения результатов" })
            {
                try { if (Directory.Exists(target.Text)) dlg.SelectedPath = target.Text; } catch { }
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    target.Text = dlg.SelectedPath;
            }
        }

        private static string HumanSize(long bytes)
        {
            double v = bytes;
            string[] u = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            int i = -1;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString("0.#") + " " + u[Math.Max(i, 0)];
        }

        // ======================================================================
        //  БОНУС: заглушка «Создать задачу в Jira/ServiceNow» — формирует шаблон
        //  тикета с данными собранного отчёта и кладёт его в буфер обмена.
        // ======================================================================
        private void ShowTicketStub()
        {
            string template =
                "Название: [IR] Сбор форензик-артефактов — " + Environment.MachineName + "\r\n\r\n" +
                "Priority: Highest\r\n" +
                "Labels: incident-response, forensics\r\n\r\n" +
                "Описание:\r\n" +
                "Машина: " + Environment.MachineName + "\r\n" +
                "ОС: " + Environment.OSVersion.VersionString + "\r\n" +
                "Пользователь: " + Environment.UserDomainName + "\\" + Environment.UserName + "\r\n" +
                "Дата сбора: " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + "\r\n" +
                "Архив с артефактами: " + (_lastZipPath ?? "(не создан)") + "\r\n\r\n" +
                "Приложить ZIP-архив из отчёта к задаче.\r\n";

            using (var dlg = new TicketStubForm(template))
                dlg.ShowDialog(this);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Если сбор идёт — предупреждаем
            if (_worker != null && _worker.IsBusy)
            {
                var r = MessageBox.Show(this,
                    "Сбор ещё выполняется. Прервать и закрыть приложение?\n" +
                    "Уже собранные данные могут не сохраниться.",
                    "Подтверждение выхода", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                try { if (_engine != null) _engine.Cancelled = true; _worker.CancelAsync(); } catch { }
            }
            base.OnFormClosing(e);
        }
    }

    // ==========================================================================
    //  Модальное окно «✅ Готово!» с кнопками «Открыть папку» и «Закрыть»
    // ==========================================================================
    public class DoneForm : Form
    {
        private readonly string _zipPath;
        private readonly string _folderPath;

        public DoneForm(string zipPath, string folderPath)
        {
            _zipPath = zipPath;
            _folderPath = folderPath;

            Text = "Сбор завершён";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 190);

            var lbl = new Label
            {
                Text = "✅  Готово! Архив сохранён:\r\n" + _zipPath,
                Font = new Font("Segoe UI", 10.5f),
                Location = new Point(20, 20),
                Size = new Size(520, 80),
            };

            var btnOpen = new Button
            {
                Text = "📂 Открыть папку",
                Size = new Size(180, 40),
                Location = new Point(20, 125),
            };
            btnOpen.Click += (s, e) => Native.OpenInExplorer(_zipPath);

            var btnClose = new Button
            {
                Text = "Закрыть",
                Size = new Size(120, 40),
                Location = new Point(420, 125),
                DialogResult = DialogResult.OK,
            };

            Controls.AddRange(new Control[] { lbl, btnOpen, btnClose });
            AcceptButton = btnOpen;
            CancelButton = btnClose;

            // Если ZIP не создан (например, ошибка упаковки) — открываем просто папку с результатами
            string openTarget = !string.IsNullOrEmpty(zipPath) && File.Exists(zipPath) ? zipPath : folderPath;
            btnOpen.Click -= null; // no-op для ясности

            Theme.Apply(this);
            Theme.StyleButton(btnOpen, Theme.Accent);
            Load += (s, e) => Theme.EnableDarkTitleBar(this);
        }
    }

    // ==========================================================================
    //  Окно-заглушка для создания тикета в Jira/ServiceNow (бонус из ТЗ):
    //  показывает готовый шаблон текста и копирует его в буфер обмена.
    // ==========================================================================
    public class TicketStubForm : Form
    {
        public TicketStubForm(string template)
        {
            Text = "Задача в Jira / ServiceNow (шаблон)";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(620, 420);

            var lbl = new Label
            {
                Text = "Скопируйте шаблон ниже и вставьте в новую задачу в Jira или ServiceNow:",
                Location = new Point(14, 10),
                AutoSize = true
            };

            var txt = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = false,
                Location = new Point(14, 38),
                Size = new Size(592, 320),
                Text = template,
                Font = new Font("Consolas", 9f),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };

            var btnCopy = new Button
            {
                Text = "📋 Скопировать в буфер",
                Size = new Size(180, 36),
                Location = new Point(14, 370),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            btnCopy.Click += (s, e) =>
            {
                try { Clipboard.SetText(txt.Text); btnCopy.Text = "✔ Скопировано!"; } catch { }
            };

            var btnClose = new Button
            {
                Text = "Закрыть",
                Size = new Size(120, 36),
                Location = new Point(486, 370),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
            };

            Controls.AddRange(new Control[] { lbl, txt, btnCopy, btnClose });
            CancelButton = btnClose;
            Theme.Apply(this);
            Theme.StyleButton(btnCopy, Theme.Accent);
            Load += (s, e) => Theme.EnableDarkTitleBar(this);
        }
    }
}
