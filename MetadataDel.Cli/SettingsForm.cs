using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using State = MetadataDel.Cli.SelfInstaller.ShellIntegrationState;

namespace MetadataDel.Cli;

// Настройки: интеграция с Проводником, резервные копии окна и журнал.
internal sealed class SettingsForm : Form
{
    private readonly AppSettings settings;
    private readonly Label statusGlyph = new() { AutoSize = true, Font = new Font("Segoe UI Symbol", 13), Margin = new Padding(0, 0, 8, 0) };
    private readonly Label status = Caption("", 10.5f, FontStyle.Bold);
    private readonly Label statusHint = Caption("", 9, secondary: true);
    private readonly Button install = Action("Включить");
    private readonly Button remove = Action("Отключить…");
    private readonly CheckBox backup = new() { Text = "Сохранять резервные копии", AutoSize = true, Margin = new Padding(0, 0, 0, 2) };

    public SettingsForm(AppSettings settings)
    {
        this.settings = settings;
        Text = "Настройки MetadataDel"; StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 9.5f);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SystemColors.Window; ForeColor = SystemColors.WindowText;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(24, 20, 24, 16), Dock = DockStyle.Fill };
        var version = typeof(SettingsForm).Assembly.GetName().Version;
        var brand = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 14) };
        brand.Controls.Add(Caption("MetadataDel", 15, FontStyle.Bold));
        brand.Controls.Add(Caption($"Версия {version?.ToString(3) ?? "—"} · обработка только на этом компьютере", 9, secondary: true));
        root.Controls.Add(brand);

        var statusText = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        statusText.Controls.AddRange([status, statusHint]);
        var statusRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        statusRow.Controls.AddRange([statusGlyph, statusText]);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.AddRange([install, remove]);
        var explorer = Stack(statusRow, buttons, Caption("Из Проводника файлы перезаписываются без резервных копий.\nДля важных документов используйте это окно с включёнными копиями.", 9, secondary: true));
        root.Controls.Add(Section("Правый клик в Проводнике", explorer));

        root.Controls.Add(Section("Главное окно", Stack(backup, Caption("Копия «имя.bak» появляется рядом с оригиналом. Флажок в главном окне\nменяет эту же настройку.", 9, secondary: true))));

        var logs = Action("Открыть журнал"); var apps = Action("Установленные приложения");
        var serviceButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        serviceButtons.Controls.AddRange([logs, apps]);
        root.Controls.Add(Section("Обслуживание", Stack(serviceButtons, Caption("Удалить MetadataDel можно в разделе «Установленные приложения» Windows.\nДокументы и их копии при этом не затрагиваются.", 9, secondary: true))));

        var close = new Button { Text = "Готово", AutoSize = true, MinimumSize = new Size(96, 30), DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right, Margin = new Padding(0, 6, 0, 0) };
        root.Controls.Add(close); AcceptButton = CancelButton = close;
        Controls.Add(root);

        install.Click += (_, _) => Install();
        remove.Click += (_, _) => Remove();
        backup.Checked = settings.Backup;
        backup.CheckedChanged += (_, _) => { settings.Backup = backup.Checked; settings.Save(); };
        logs.Click += (_, _) => { Directory.CreateDirectory(SelfInstaller.LogsDirectory); Open("explorer.exe", $"\"{SelfInstaller.LogsDirectory}\""); };
        apps.Click += (_, _) => Open("ms-settings:appsfeatures", null);
        RefreshState();
    }

    private static Label Caption(string text, float size, FontStyle style = FontStyle.Regular, bool secondary = false) => new()
    { Text = text, AutoSize = true, Font = new Font("Segoe UI", size, style), ForeColor = secondary ? SystemColors.GrayText : SystemColors.WindowText, Margin = new Padding(0, 1, 0, 3) };
    private static Button Action(string text) => new()
    { Text = text, AutoSize = true, MinimumSize = new Size(0, 30), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 0, 8, 6), UseVisualStyleBackColor = true };
    private static FlowLayoutPanel Stack(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Dock = DockStyle.Fill };
        panel.Controls.AddRange(controls); return panel;
    }
    // Заголовок над панелью с тонкой рамкой; в High Contrast рамка берёт системный цвет.
    private static Control Section(string title, Control content)
    {
        var box = new Panel { AutoSize = true, Padding = new Padding(14, 12, 14, 8), Margin = new Padding(0, 0, 0, 14), Dock = DockStyle.Fill, BackColor = SystemColors.Window };
        box.Paint += (_, e) => { using var pen = new Pen(SystemInformation.HighContrast ? SystemColors.WindowText : SystemColors.ControlDark); e.Graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1); };
        box.Resize += (_, _) => box.Invalidate();
        box.Controls.Add(content);
        var group = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Dock = DockStyle.Fill };
        group.Controls.Add(Caption(title, 9.5f, FontStyle.Bold)); group.Controls.Add(box);
        return group;
    }

    private void RefreshState()
    {
        var state = SelfInstaller.GetShellIntegrationState();
        (string glyph, Color color, string title, string hint) = state switch
        {
            State.On => ("✓", Color.FromArgb(16, 124, 16), "Включён", "Правый клик по PDF, Word, Excel или папке → «Удалить метаданные»."),
            State.Stale => ("!", Color.FromArgb(157, 93, 0), "Нужно обновить", "Пункт меню указывает на другую копию программы. Нажмите «Обновить»."),
            State.Managed => ("i", SystemColors.GrayText, "Управляется администратором", "Программа установлена для всех пользователей. Изменить пункт меню может администратор."),
            _ => ("○", SystemColors.GrayText, "Выключен", "Включите, чтобы очищать документы правым кликом в Проводнике."),
        };
        statusGlyph.Text = glyph; statusGlyph.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : color;
        status.Text = title; statusHint.Text = hint;
        install.Text = state == State.On ? "Восстановить" : state == State.Stale ? "Обновить" : "Включить";
        install.Enabled = state != State.Managed;
        remove.Enabled = state is State.On or State.Stale;
    }

    private void Install()
    {
        try { SelfInstaller.InstallShellIntegration(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось настроить Проводник", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        RefreshState();
    }

    private void Remove()
    {
        var answer = MessageBox.Show(this, "Пункты «Удалить метаданные» исчезнут из контекстного меню и меню «Отправить». Документы, копии и сама программа останутся на месте.",
            "Отключить правый клик?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;
        try { SelfInstaller.Uninstall(removeFiles: false); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось отключить", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        RefreshState();
    }

    private void Open(string target, string? arguments)
    {
        try { Process.Start(new ProcessStartInfo(target, arguments ?? string.Empty) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
