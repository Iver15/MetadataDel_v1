using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Cli;

internal sealed class MainForm : Form
{
    private static readonly Color Accent = Color.FromArgb(177, 70, 35);
    private readonly ListView files = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ShowItemToolTips = true };
    private readonly Button choose = Button("Выбрать файлы…");
    private readonly Button clean = Button("Очистить файлы", true);
    private readonly Button clear = Button("Убрать выбранные");
    private readonly Button reveal = Button("Показать в папке");
    private readonly Button settings = Button("Настройки");
    private readonly CheckBox backup = new() { Text = "Создавать резервные копии", Checked = true, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
    private readonly Label summary = Label("Добавьте документы — обработка начнётся по кнопке.", 10);
    private readonly Label detail = Label("Правый клик в Проводнике по-прежнему работает отдельно.", 9);
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 4, Margin = new Padding(0, 8, 0, 8) };
    private readonly Panel drop = new() { Dock = DockStyle.Fill, Padding = new Padding(24), BackColor = Color.FromArgb(247, 245, 241) };
    private bool busy, closeAfterWork, dragOver;
    private sealed class Entry(string path)
    {
        public string Path { get; } = path;
        public string? Output { get; set; }
        public bool Attempted { get; set; }
    }

    public MainForm()
    {
        Text = "MetadataDel"; StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10);
        ClientSize = new Size(760, 640); MinimumSize = new Size(700, 610);
        BackColor = SystemColors.Window; ForeColor = SystemColors.WindowText;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        detail.AutoSize = summary.AutoSize = false; detail.Dock = summary.Dock = DockStyle.Fill; detail.AutoEllipsis = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new(SizeType.Absolute, 74)); layout.RowStyles.Add(new(SizeType.Absolute, 156));
        layout.RowStyles.Add(new(SizeType.Absolute, 36)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 52)); layout.RowStyles.Add(new(SizeType.Absolute, 62)); layout.RowStyles.Add(new(SizeType.Absolute, 50));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(Label("MetadataDel", 23, FontStyle.Bold), 0, 0);
        header.Controls.Add(Label("Документы без лишних метаданных", 10), 0, 1);
        header.Controls.Add(settings, 1, 0); layout.Controls.Add(header, 0, 0);
        var dropLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        dropLayout.Controls.Add(Label("Перетащите сюда документы", 16, FontStyle.Bold));
        dropLayout.Controls.Add(Label("PDF · DOCX · XLSX · DOC · XLS", 10));
        dropLayout.Controls.Add(choose); drop.Controls.Add(dropLayout);
        drop.Paint += (_, e) => { using var pen = new Pen(dragOver ? Accent : Color.FromArgb(190, 184, 175), dragOver ? 2 : 1) { DashStyle = DashStyle.Dash }; e.Graphics.DrawRectangle(pen, 1, 1, drop.Width - 3, drop.Height - 3); };
        layout.Controls.Add(drop, 0, 1); layout.Controls.Add(summary, 0, 2);
        files.Columns.Add("Документ", 350); files.Columns.Add("Результат", 280);
        files.Resize += (_, _) => { files.Columns[0].Width = Math.Max(220, files.ClientSize.Width / 2); files.Columns[1].Width = Math.Max(220, files.ClientSize.Width - files.Columns[0].Width - 8); };
        layout.Controls.Add(files, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        actions.Controls.AddRange([clear, reveal]); layout.Controls.Add(actions, 0, 4);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        options.Controls.Add(backup); options.Controls.Add(Label("Исходные файлы будут заменены после очистки.", 9)); layout.Controls.Add(options, 0, 5);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        bottom.ColumnStyles.Add(new(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new(SizeType.AutoSize));
        bottom.Controls.Add(progress, 0, 0); bottom.Controls.Add(detail, 0, 1); bottom.Controls.Add(clean, 1, 0); bottom.SetRowSpan(clean, 2);
        layout.Controls.Add(bottom, 0, 6); Controls.Add(layout);
        choose.Click += (_, _) => PickFiles(); clean.Click += async (_, _) => await CleanAsync();
        clear.Click += (_, _) => { foreach (ListViewItem item in files.SelectedItems.Cast<ListViewItem>().ToArray()) files.Items.Remove(item); RefreshActions(); };
        reveal.Click += (_, _) => Reveal(); settings.Click += (_, _) => ShowSettings();
        files.SelectedIndexChanged += (_, _) => { RefreshActions(); if (files.SelectedItems.Count == 1) detail.Text = files.SelectedItems[0].ToolTipText; };
        files.DoubleClick += (_, _) => Reveal();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; closeAfterWork = true; detail.Text = "Окно закроется после завершения обработки."; } };
        KeyPreview = true; KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.O) { PickFiles(); e.Handled = true; } };
        RegisterDrop(this); RefreshActions();
    }

    private static Label Label(string text, float size, FontStyle style = FontStyle.Regular) => new()
    { Text = text, AutoSize = true, Font = new Font("Segoe UI", size, style), Margin = new Padding(0, 3, 0, 3), ForeColor = SystemColors.WindowText };
    private static Button Button(string text, bool primary = false) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(0, 36), Padding = new Padding(12, 5, 12, 5), Margin = new Padding(0, 3, 10, 3),
        UseVisualStyleBackColor = !primary, BackColor = primary ? Accent : SystemColors.Control,
        ForeColor = primary ? Color.White : SystemColors.ControlText, FlatStyle = primary ? FlatStyle.Flat : FlatStyle.Standard
    };
    private void RegisterDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => { dragOver = !busy && e.Data?.GetDataPresent(DataFormats.FileDrop) == true; e.Effect = dragOver ? DragDropEffects.Copy : DragDropEffects.None; drop.Invalidate(); };
        control.DragLeave += (_, _) => { dragOver = false; drop.Invalidate(); };
        control.DragDrop += (_, e) => { dragOver = false; drop.Invalidate(); if (!busy && e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths); };
        foreach (Control child in control.Controls) RegisterDrop(child);
    }
    private void PickFiles()
    {
        if (busy) return;
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "Выберите документы", Filter = "Документы|*.pdf;*.docx;*.xlsx;*.doc;*.xls|Все файлы|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddFiles(dialog.FileNames);
    }
    private void AddFiles(IEnumerable<string> paths)
    {
        var known = files.Items.Cast<ListViewItem>().Select(i => ((Entry)i.Tag!).Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            var path = Path.GetFullPath(raw); if (!known.Add(path)) continue;
            var entry = new Entry(path); var status = "Готов к очистке";
            if (Directory.Exists(path)) { entry.Attempted = true; status = "Для папок используйте правый клик"; }
            else if (!new[] { ".pdf", ".docx", ".xlsx", ".doc", ".xls" }.Contains(Path.GetExtension(path).ToLowerInvariant())) { entry.Attempted = true; status = "Формат не поддерживается"; }
            var item = new ListViewItem([Path.GetFileName(path), status]) { Tag = entry, ToolTipText = path }; files.Items.Add(item);
        }
        RefreshActions();
    }
    private void RefreshActions()
    {
        var pending = files.Items.Cast<ListViewItem>().Count(i => !((Entry)i.Tag!).Attempted);
        clean.Enabled = !busy && pending > 0; clean.Text = busy ? "Обработка…" : pending > 0 ? $"Очистить · {pending}" : "Очистить файлы";
        choose.Enabled = backup.Enabled = settings.Enabled = !busy;
        clear.Enabled = !busy && files.SelectedItems.Count > 0;
        reveal.Enabled = !busy && files.SelectedItems.Count == 1;
        if (!busy) summary.Text = files.Items.Count == 0 ? "Добавьте документы — обработка начнётся по кнопке." : $"В списке: {files.Items.Count} · Готовы к очистке: {pending}";
    }
    private async Task CleanAsync()
    {
        if (busy) return;
        var pending = files.Items.Cast<ListViewItem>().Where(i => !((Entry)i.Tag!).Attempted).ToArray();
        busy = true; RefreshActions(); var options = new CleanOptions(Backup: backup.Checked);
        progress.Maximum = Math.Max(1, pending.Length); progress.Value = 0; int ok = 0, warnings = 0;
        foreach (var item in pending)
        {
            var entry = (Entry)item.Tag!; entry.Attempted = true; item.SubItems[1].Text = "Обрабатывается…";
            summary.Text = $"Обработка {progress.Value + 1} из {pending.Length}";
            var result = await Task.Run(() => new SingleFileCleaningService().CleanAsync(entry.Path, options));
            entry.Output = result.Success ? result.Path : null;
            if (result.Success) ok++;
            bool warning = result.Success && !string.IsNullOrWhiteSpace(result.Message); if (warning) warnings++;
            item.SubItems[1].Text = result.Success ? warning ? "Очищен с предупреждениями" : "Очищен" : "Не обработан";
            item.ToolTipText = result.Message ?? result.Path;
            item.ForeColor = result.Success && !warning ? Color.FromArgb(34, 110, 75) : SystemColors.WindowText;
            detail.Text = result.Message ?? "Готово. Результат можно показать в папке."; progress.Value++;
        }
        busy = false; RefreshActions(); summary.Text = $"Очищено: {ok} из {pending.Length} · С предупреждениями: {warnings}";
        if (closeAfterWork) Close();
    }
    private void Reveal()
    {
        if (files.SelectedItems.Count != 1) return;
        var entry = (Entry)files.SelectedItems[0].Tag!;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.Output ?? entry.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть папку"); }
    }
    private void ShowSettings()
    {
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => menu.Dispose();
        menu.Items.Add("Восстановить очистку правым кликом", null, (_, _) =>
        {
            try
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (Environment.ProcessPath?.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true)
                { MessageBox.Show(this, "Для корпоративной установки используйте восстановление MSI через администратора.", "Интеграция с Проводником"); return; }
                var result = SelfInstaller.InstallShellIntegration();
                MessageBox.Show(this, "Контекстное меню настроено для: " + string.Join(", ", result.RegisteredExtensions), "Готово");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Ошибка настройки"); }
        });
        menu.Items.Add("Управление установленными приложениями", null, (_, _) => Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true }));
        menu.Items.Add("О программе", null, (_, _) => MessageBox.Show(this, "MetadataDel\nУдаление метаданных из документов.\n\nПравый клик в Проводнике работает независимо от этого окна.\nФлажок резервных копий относится только к этому окну.", "MetadataDel"));
        menu.Items.Add("Диагностика", null, (_, _) => { var result = SelfInstaller.GetDiagnostics(); MessageBox.Show(this, "Форматы: " + string.Join(", ", result.RegisterableExtensions) + "\n" + string.Join("\n", result.Warnings), "Диагностика"); });
        menu.Show(settings, new Point(0, settings.Height));
    }
}
