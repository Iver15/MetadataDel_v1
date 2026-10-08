using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MetadataDel.Core.Cleaning;

namespace MetadataDel.Cli;

internal sealed class MainForm : Form
{
    private static readonly Color Accent = Color.FromArgb(177, 70, 35);
    private readonly ListView files = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.None, OwnerDraw = true };
    private readonly Button choose = Button("Выбрать файлы…");
    private readonly Button clean = Button("Очистить файлы", true);
    private readonly Button clear = Button("Убрать выбранные");
    private readonly Button clearList = Button("Очистить список");
    private readonly Button reveal = Button("Показать в папке");
    private readonly Button settings = Button("Настройки");
    private readonly AppSettings appSettings = AppSettings.Load();
    private readonly CheckBox backup = new() { Text = "Сохранять резервные копии", AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
    private readonly Label integrationStatus = Label("", 10);
    private readonly Label backupHint = Label("Копии .bak — рядом. Оригиналы будут заменены.", 9);
    private readonly Label summary = Label("", 10);
    private readonly TextBox detail = new() { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window, ForeColor = SystemColors.WindowText, AccessibleName = "Подробности выбранного документа" };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly Panel drop = new() { Dock = DockStyle.Fill, Padding = new Padding(20) };
    private readonly Label dropTitle = Label("Перетащите документы сюда", 20, FontStyle.Bold);
    private readonly Label dropHint = Label("Обработка на вашем компьютере. Файлы никуда не отправляются.", 9);
    private readonly Label formats = Label("PDF  ·  Word  ·  Excel", 10);
    private readonly TableLayoutPanel layout = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 18), ColumnCount = 1, RowCount = 9 };
    private readonly FlowLayoutPanel dropContent = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly TableLayoutPanel queueHeader = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
    private readonly FlowLayoutPanel actions = new() { Dock = DockStyle.Fill, WrapContents = false };
    private readonly ImageList rowHeight = new() { ImageSize = new Size(1, 52) };
    private readonly Font filenameFont = new("Segoe UI", 10, FontStyle.Bold);
    private readonly Font folderFont = new("Segoe UI", 9);
    private bool busy, closeAfterWork, dragOver;
    private sealed class Entry(string path)
    {
        public string Path { get; } = path;
        public string? Output { get; set; }
        public string Message { get; set; } = "Обработка начнётся после нажатия «Очистить».";
        public string Kind { get; set; } = "pending";
        public bool Attempted { get; set; }
    }

    public MainForm()
    {
        Text = "MetadataDel"; StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; Font = new Font("Segoe UI", 10);
        ClientSize = new Size(800, 700); MinimumSize = new Size(780, 700);
        BackColor = SystemColors.Window; ForeColor = SystemColors.WindowText;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        for (int i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.Add(Label("MetadataDel", 23, FontStyle.Bold), 0, 0);
        header.Controls.Add(Label("Подготовьте документы к отправке", 10), 0, 1);
        header.Controls.Add(settings, 1, 0); layout.Controls.Add(header, 0, 0);
        var integration = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        integration.ColumnStyles.Add(new(SizeType.Percent, 100)); integration.ColumnStyles.Add(new(SizeType.AutoSize));
        integration.Controls.Add(integrationStatus, 0, 0);
        var configure = new LinkLabel { Text = "Настроить", AutoSize = true, AccessibleName = "Настроить очистку правым кликом", Margin = new Padding(0, 6, 0, 0) };
        configure.LinkColor = SystemColors.HotTrack; configure.Click += (_, _) => { if (!busy) ShowSettings(); };
        integration.Controls.Add(configure, 1, 0); layout.Controls.Add(integration, 0, 1);
        dropContent.Controls.AddRange([dropTitle, dropHint, choose, formats]); drop.Controls.Add(dropContent);
        drop.Resize += (_, _) => CenterDrop();
        dropContent.SizeChanged += (_, _) => CenterDrop();
        drop.Paint += (_, e) => {
            using var pen = new Pen(dragOver ? SystemColors.Highlight : SystemColors.ControlDark, dragOver ? 2 : 1) { DashStyle = DashStyle.Dash };
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect(new Rectangle(1, 1, drop.Width - 3, drop.Height - 3), LogicalToDeviceUnits(16));
            e.Graphics.DrawPath(pen, path);
        };
        layout.Controls.Add(drop, 0, 2);
        queueHeader.ColumnStyles.Add(new(SizeType.Percent, 100)); queueHeader.ColumnStyles.Add(new(SizeType.AutoSize));
        summary.AutoSize = false; summary.Dock = DockStyle.Fill; summary.TextAlign = ContentAlignment.MiddleLeft;
        queueHeader.Controls.Add(summary, 0, 0); queueHeader.Controls.Add(clearList, 1, 0); layout.Controls.Add(queueHeader, 0, 3);
        files.Columns.Add("Документ", 455); files.Columns.Add("Результат", 205); files.SmallImageList = rowHeight;
        files.Resize += (_, _) => { files.Columns[1].Width = LogicalToDeviceUnits(195); files.Columns[0].Width = Math.Max(LogicalToDeviceUnits(260), files.ClientSize.Width - files.Columns[1].Width - 4); };
        files.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        files.DrawSubItem += DrawFile;
        layout.Controls.Add(files, 0, 4);
        actions.Controls.AddRange([clear, reveal]); layout.Controls.Add(actions, 0, 5);
        layout.Controls.Add(detail, 0, 6); layout.Controls.Add(progress, 0, 7);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        footer.Paint += (_, e) => { using var pen = new Pen(SystemColors.ControlLight); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        options.Controls.AddRange([backup, backupHint]); footer.Controls.Add(options, 0, 0); footer.Controls.Add(clean, 1, 0);
        layout.Controls.Add(footer, 0, 8); Controls.Add(layout);
        choose.Click += (_, _) => PickFiles(); clean.Click += async (_, _) => await CleanAsync();
        clear.Click += (_, _) => RemoveSelected();
        clearList.Click += (_, _) => { if (!busy) { files.Items.Clear(); RefreshActions(); UpdateDetail(); } };
        reveal.Click += (_, _) => Reveal(); settings.Click += (_, _) => ShowSettings();
        backup.Checked = appSettings.Backup;
        backup.CheckedChanged += (_, _) => { UpdateBackupHint(); if (appSettings.Backup != backup.Checked) { appSettings.Backup = backup.Checked; appSettings.Save(); } };
        files.SelectedIndexChanged += (_, _) => { RefreshActions(); UpdateDetail(); };
        files.DoubleClick += (_, _) => Reveal();
        files.KeyDown += (_, e) => {
            if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem item in files.Items) item.Selected = true; e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Delete && !busy) { RemoveSelected(); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Enter && !e.Control) { Reveal(); e.SuppressKeyPress = true; }
        };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; closeAfterWork = true; detail.Text = "Окно закроется после завершения обработки."; } };
        KeyPreview = true; KeyDown += async (_, e) => {
            if (e.Control && e.KeyCode == Keys.O) { PickFiles(); e.SuppressKeyPress = true; }
            if (e.Control && e.KeyCode == Keys.Enter && clean.Enabled) { e.SuppressKeyPress = true; await CleanAsync(); }
        };
        DpiChanged += (_, _) => { rowHeight.ImageSize = new Size(1, LogicalToDeviceUnits(52)); RefreshActions(); };
        SystemColorsChanged += (_, _) => ApplyPalette();
        RegisterDrop(this); ApplyPalette(); RefreshActions(); UpdateIntegrationStatus();
    }

    private static Label Label(string text, float size, FontStyle style = FontStyle.Regular) => new()
    { Text = text, AutoSize = true, Font = new Font("Segoe UI", size, style), Margin = new Padding(0, 3, 0, 5), ForeColor = SystemColors.WindowText };
    private static Button Button(string text, bool primary = false) => new()
    {
        Text = text, AutoSize = true, MinimumSize = new Size(0, 32), Padding = new Padding(10, 4, 10, 4), Margin = new Padding(0, 3, 8, 3),
        UseVisualStyleBackColor = !primary, BackColor = primary ? Accent : SystemColors.Control,
        ForeColor = primary ? Color.White : SystemColors.ControlText, FlatStyle = primary ? FlatStyle.Flat : FlatStyle.Standard
    };
    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath(); var d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0) return path;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90); path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90); path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
    }
    private void CenterDrop()
    {
        if (dropContent == null) return;
        dropContent.Left = Math.Max(0, (drop.ClientSize.Width - dropContent.Width) / 2);
        dropContent.Top = Math.Max(0, (drop.ClientSize.Height - dropContent.Height) / 2);
        foreach (Control child in dropContent.Controls) child.Margin = new Padding(Math.Max(0, (dropContent.Width - child.Width) / 2), 4, 0, 7);
    }
    private void ApplyPalette()
    {
        drop.BackColor = SystemInformation.HighContrast ? SystemColors.Window : SystemColors.ControlLightLight; detail.BackColor = files.BackColor = BackColor = SystemColors.Window;
        detail.ForeColor = files.ForeColor = ForeColor = SystemColors.WindowText;
        clean.BackColor = SystemInformation.HighContrast ? SystemColors.Highlight : Accent;
        clean.ForeColor = SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White;
        clean.FlatAppearance.BorderColor = clean.BackColor;
        clean.FlatAppearance.MouseDownBackColor = SystemInformation.HighContrast ? SystemColors.ControlDark : Color.FromArgb(137, 48, 23);
        clean.FlatAppearance.MouseOverBackColor = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(158, 59, 28);
        UpdateBackupHint(); files.Invalidate(); drop.Invalidate();
    }
    private void UpdateBackupHint()
    {
        backupHint.Text = backup.Checked ? "Копии .bak — рядом. Оригиналы будут заменены." : "Без резервных копий. Оригиналы будут заменены.";
        backupHint.ForeColor = backup.Checked || SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(153, 76, 0);
    }
    private void DrawFile(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item?.Tag is not Entry entry || e.SubItem == null) return;
        bool selected = e.Item.Selected;
        Color background = selected ? SystemColors.Highlight : SystemColors.Window;
        Color foreground = selected ? SystemColors.HighlightText : SystemColors.WindowText;
        using var brush = new SolidBrush(background); e.Graphics.FillRectangle(brush, e.Bounds);
        int pad = LogicalToDeviceUnits(10);
        var bounds = Rectangle.Inflate(e.Bounds, -pad, 0);
        var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter;
        if (e.ColumnIndex == 0)
        {
            var top = new Rectangle(bounds.X, bounds.Y + LogicalToDeviceUnits(5), bounds.Width, LogicalToDeviceUnits(22));
            TextRenderer.DrawText(e.Graphics, e.Item.Text, filenameFont, top, foreground, flags);
            var bottom = new Rectangle(bounds.X, bounds.Y + LogicalToDeviceUnits(27), bounds.Width, LogicalToDeviceUnits(19));
            TextRenderer.DrawText(e.Graphics, Path.GetDirectoryName(entry.Path), folderFont, bottom, selected ? foreground : SystemColors.GrayText, flags | TextFormatFlags.PathEllipsis);
        }
        else TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, bounds, foreground, flags);
        if (e.Item.Focused && files.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, foreground, background);
    }
    private void RemoveSelected()
    {
        if (busy) return;
        foreach (ListViewItem item in files.SelectedItems.Cast<ListViewItem>().ToArray()) files.Items.Remove(item);
        RefreshActions(); UpdateDetail();
    }
    private void UpdateDetail()
    {
        if (files.SelectedItems.Count == 1)
        {
            var item = files.SelectedItems[0]; var entry = (Entry)item.Tag!;
            detail.Text = $"{Path.GetFileName(entry.Path)} — {item.SubItems[1].Text}\r\n{entry.Message}\r\n{entry.Output ?? entry.Path}";
        }
        else detail.Text = files.SelectedItems.Count > 1 ? "Выбрано несколько документов. Удаляются только строки из списка; файлы остаются на диске." : "Выберите документ, чтобы увидеть путь и подробности результата.";
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { rowHeight.Dispose(); filenameFont.Dispose(); folderFont.Dispose(); }
        base.Dispose(disposing);
    }
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
            var entry = new Entry(path); var status = "○ Готов к очистке";
            if (Directory.Exists(path)) { entry.Attempted = true; entry.Kind = "unsupported"; entry.Message = "Для папок используйте очистку правым кликом в Проводнике."; status = "— Папка пропущена"; }
            else if (!new[] { ".pdf", ".docx", ".xlsx", ".doc", ".xls" }.Contains(Path.GetExtension(path).ToLowerInvariant())) { entry.Attempted = true; entry.Kind = "unsupported"; entry.Message = "Поддерживаются PDF, DOCX, XLSX, DOC и XLS. Этот файл не будет изменён."; status = "— Другой формат"; }
            var item = new ListViewItem([Path.GetFileName(path), status]) { Tag = entry, ToolTipText = entry.Message + "\n" + path }; files.Items.Add(item);
        }
        RefreshActions();
    }
    private void RefreshActions()
    {
        var entries = files.Items.Cast<ListViewItem>().Select(i => (Entry)i.Tag!).ToArray();
        int pending = entries.Count(i => !i.Attempted), ok = entries.Count(i => i.Kind is "success" or "warning"), failed = entries.Count(i => i.Kind == "error"), skipped = entries.Count(i => i.Kind == "unsupported");
        int warnings = entries.Count(i => i.Kind == "warning"); bool hasFiles = entries.Length > 0;
        clean.Enabled = !busy && pending > 0; clean.Text = busy ? "Обработка…" : pending > 0 ? $"Очистить · {pending}" : "Очистить файлы";
        choose.Enabled = backup.Enabled = settings.Enabled = !busy;
        clear.Enabled = !busy && files.SelectedItems.Count > 0; clearList.Enabled = !busy && hasFiles;
        reveal.Enabled = !busy && files.SelectedItems.Count == 1;
        layout.SuspendLayout();
        float[] heights = [68, 36, hasFiles ? 104 : 0, hasFiles ? 40 : 0, 0, hasFiles ? 42 : 0, hasFiles ? 56 : 0, busy && hasFiles ? 6 : 0, 72];
        for (int i = 0; i < heights.Length; i++) { layout.RowStyles[i].SizeType = i == (hasFiles ? 4 : 2) ? SizeType.Percent : SizeType.Absolute; layout.RowStyles[i].Height = i == (hasFiles ? 4 : 2) ? 100 : LogicalToDeviceUnits((int)heights[i]); }
        queueHeader.Visible = files.Visible = actions.Visible = detail.Visible = hasFiles; progress.Visible = busy && hasFiles;
        dropHint.Visible = formats.Visible = !hasFiles;
        dropTitle.Text = busy ? "Дождитесь завершения обработки" : hasFiles ? "Добавьте ещё документы" : "Перетащите документы сюда";
        float titleSize = hasFiles ? 14 : 20;
        if (Math.Abs(dropTitle.Font.Size - titleSize) > 0.1) { var previous = dropTitle.Font; dropTitle.Font = new Font("Segoe UI", titleSize, FontStyle.Bold); previous.Dispose(); }
        layout.ResumeLayout(); CenterDrop();
        if (!busy)
        {
            var parts = new List<string>();
            if (pending > 0) parts.Add($"Готово к очистке: {pending}");
            if (ok > 0) parts.Add($"Очищено: {ok}");
            if (warnings > 0) parts.Add($"С замечаниями: {warnings}");
            if (failed > 0) parts.Add($"Не обработано: {failed}");
            if (skipped > 0) parts.Add($"Пропущено: {skipped}");
            summary.Text = string.Join(" · ", parts);
            if (!hasFiles) progress.Value = 0;
        }
    }
    private async Task CleanAsync()
    {
        if (busy || !clean.Enabled) return;
        var pending = files.Items.Cast<ListViewItem>().Where(i => !((Entry)i.Tag!).Attempted).ToArray();
        busy = true; RefreshActions(); var options = new CleanOptions(Backup: backup.Checked);
        progress.Maximum = Math.Max(1, pending.Length); progress.Value = 0;
        foreach (var item in pending)
        {
            var entry = (Entry)item.Tag!; entry.Attempted = true; entry.Kind = "processing"; item.SubItems[1].Text = "Обрабатывается…";
            summary.Text = $"Обработка {progress.Value + 1} из {pending.Length}";
            var result = await Task.Run(() => new SingleFileCleaningService().CleanAsync(entry.Path, options));
            entry.Output = result.Success ? result.Path : null;
            bool warning = result.Success && !string.IsNullOrWhiteSpace(result.Message);
            entry.Kind = result.Success ? warning ? "warning" : "success" : "error";
            item.SubItems[1].Text = result.Success ? warning ? "! Есть замечания" : "✓ Очищен" : "× Не обработан";
            entry.Message = result.Message ?? (result.Success ? "Готово. Очищенный файл сохранён." : "Не удалось обработать файл.");
            item.ToolTipText = entry.Message + "\n" + (entry.Output ?? entry.Path);
            UpdateDetail(); progress.Value++;
        }
        busy = false; RefreshActions();
        if (files.SelectedItems.Count == 0 && files.Items.Count > 0)
            (files.Items.Cast<ListViewItem>().FirstOrDefault(i => ((Entry)i.Tag!).Kind == "error") ?? files.Items[0]).Selected = true;
        UpdateDetail();
        if (closeAfterWork) Close();
    }
    private void Reveal()
    {
        if (busy || files.SelectedItems.Count != 1) return;
        var entry = (Entry)files.SelectedItems[0].Tag!;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.Output ?? entry.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Не удалось открыть папку"); }
    }
    private void ShowSettings()
    {
        if (busy) return;
        using (var dialog = new SettingsForm(appSettings)) dialog.ShowDialog(this);
        backup.Checked = appSettings.Backup; UpdateIntegrationStatus();
    }
    private void UpdateIntegrationStatus() => integrationStatus.Text = SelfInstaller.GetShellIntegrationState() switch
    {
        SelfInstaller.ShellIntegrationState.On => "✓  Правый клик в Проводнике включён",
        SelfInstaller.ShellIntegrationState.Stale => "!  Правый клик в Проводнике нужно обновить",
        SelfInstaller.ShellIntegrationState.Managed => "Правый клик в Проводнике настроен администратором",
        _ => "Очистка правым кликом в Проводнике выключена",
    };
}
