using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// ============================================================================
//  ТЁМНАЯ ТЕМА интерфейса (бонус из ТЗ).
//  Палитра в стиле VS Code Dark + хелперы для перекраски контролов.
//  Для лога используется RichTextBox: у него можно менять цвет отдельного
//  текста, чего нет у обычного TextBox.
// ============================================================================
namespace ForensicCollector
{
    internal static class Theme
    {
        // ---- Основные цвета -------------------------------------------------
        public static readonly Color Back        = Color.FromArgb(23, 23, 25);    // фон окна
        public static readonly Color Panel       = Color.FromArgb(37, 37, 40);    // фон панелей/групп
        public static readonly Color Input       = Color.FromArgb(30, 30, 32);    // фон текстовых полей
        public static readonly Color Border      = Color.FromArgb(62, 62, 66);    // рамки
        public static readonly Color Text        = Color.FromArgb(220, 220, 220); // обычный текст
        public static readonly Color TextDim     = Color.FromArgb(140, 140, 145); // второстепенный текст

        // ---- Акценты и семантические цвета ----------------------------------
        public static readonly Color Accent      = Color.FromArgb(0, 122, 204);   // синяя кнопка «Начать»
        public static readonly Color Danger      = Color.FromArgb(199, 80, 80);   // кнопка «Отмена» / ошибки
        public static readonly Color Success     = Color.FromArgb(78, 201, 176);  // зелёный — успех
        public static readonly Color Warning     = Color.FromArgb(220, 200, 80);  // жёлтый — инфо/предупреждение

        /// <summary>Перекрашивает форму и все её дочерние контролы в тёмную тему.</summary>
        public static void Apply(Control root)
        {
            foreach (Control child in root.Controls)
                ApplyRecursive(child);
            if (root is Form)
                root.BackColor = Back;
        }

        private static void ApplyRecursive(Control c)
        {
            // Все проверки типов через «as» — pattern matching (is Type name) недоступен в C# 5 / старом csc.exe
            Button b = c as Button;
            TextBox tb = c as TextBox;
            RichTextBox rtb = c as RichTextBox;
            CheckBox chk = c as CheckBox;
            Label lbl = c as Label;
            ProgressBar pb = c as ProgressBar;
            GroupBox gb = c as GroupBox;

            if (b != null)
            {
                // Кнопку «Отмена» красим красным, остальные — серым (акцент задаётся отдельно)
                StyleButton(b, b.Name == "btnCancel" ? Danger : Panel);
            }
            else if (tb != null || rtb != null)
            {
                c.BackColor = Input;
                c.ForeColor = Text;
            }
            else if (chk != null)
            {
                chk.BackColor = c.Parent != null ? c.Parent.BackColor : Back;
                chk.ForeColor = Text;
            }
            else if (lbl != null)
            {
                lbl.BackColor = Color.Transparent;
                lbl.ForeColor = (lbl.Tag as string) == "dim" ? TextDim : Text;
            }
            else if (pb != null)
            {
                SetProgressBarDark(pb);
            }
            else if (gb != null)
            {
                gb.ForeColor = Text;
            }

            foreach (Control child in c.Controls)
                ApplyRecursive(child);
        }

        /// <summary>Плоская стилизованная кнопка.</summary>
        public static void StyleButton(Button b, Color back)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(back, 0.02f);
            b.BackColor = back;
            b.ForeColor = Color.White;
            b.Cursor = Cursors.Hand;
        }

        // ---------------------------------------------------------------------
        //  Тёмный режим для системных элементов (прогресс-бар, заголовок окна).
        //  DwmSetWindowAttribute доступен на Windows 10 1809+ и Windows 11.
        //  На более старых системах вызов просто завершится ошибкой — игнорируем.
        // ---------------------------------------------------------------------
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // заголовок окна — тёмный

        /// <summary>Делает заголовок окна и прогресс-бар тёмными (best effort).</summary>
        public static void EnableDarkTitleBar(Form f)
        {
            try
            {
                int on = 1;
                // Сначала пробуем атрибут 20 (Win10 20H1+/Win11), затем 19 (1809-1909)
                if (DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            }
            catch { /* не критично */ }
        }

        private static void SetProgressBarDark(ProgressBar pb)
        {
            // Прогресс-бар рисуется системной темой; визуально приемлемо выглядит
            // на тёмном фоне панели с стандартной «зелёной/синей» полосой.
            pb.BackColor = Input;
        }
    }
}
