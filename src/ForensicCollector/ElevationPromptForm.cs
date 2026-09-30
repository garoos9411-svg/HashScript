using System.Drawing;
using System.Windows.Forms;

// ============================================================================
//  Окно «Нужны права администратора» — показывается при запуске без elevated.
//  Кнопка «Перезапустить от имени админа» вызывает UAC (ShellExecute runas).
// ============================================================================
namespace ForensicCollector
{
    public class ElevationPromptForm : Form
    {
        public ElevationPromptForm()
        {
            Text = "Сбор форензик-артефактов — требуются права администратора";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(520, 260);

            var icon = new PictureBox
            {
                Image = SystemIcons.Shield.ToBitmap(),
                SizeMode = PictureBoxSizeMode.CenterImage,
                Location = new Point(24, 30),
                Size = new Size(64, 64)
            };

            var title = new Label
            {
                Text = "⚠ Нужны права администратора",
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                AutoSize = false,
                Location = new Point(104, 24),
                Size = new Size(400, 30)
            };

            var text = new Label
            {
                Text = "Для экспорта журнала Security и веток реестра HKLM приложению необходимы " +
                       "права администратора.\n\nНажмите «Перезапустить от имени админа» — Windows покажет " +
                       "стандартный запрос UAC.\n\nМожно продолжить и без прав, но часть данных будет недоступна.",
                Font = new Font("Segoe UI", 9.5f),
                Location = new Point(104, 58),
                Size = new Size(400, 130)
            };

            var btnRestart = new Button
            {
                Text = "🛡 Перезапустить от имени админа",
                DialogResult = DialogResult.Yes,
                Location = new Point(104, 205),
                Size = new Size(250, 38)
            };

            var btnNo = new Button
            {
                Text = "Продолжить без прав",
                DialogResult = DialogResult.No,
                Location = new Point(364, 205),
                Size = new Size(140, 38)
            };

            Controls.AddRange(new Control[] { icon, title, text, btnRestart, btnNo });
            AcceptButton = btnRestart;
            CancelButton = btnNo;

            Theme.Apply(this);
            Theme.StyleButton(btnRestart, Theme.Accent);
            Load += (s, e) => Theme.EnableDarkTitleBar(this);
        }
    }
}
