using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace MetadataDel.Cli;

internal static class SetupLauncher
{
    public static int Run()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new SetupLauncherForm();
        Application.Run(form);
        return 0;
    }
}

internal sealed class SetupLauncherForm : Form
{
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly TextBox _statusTextBox;
    private readonly Button _installButton;
    private readonly Button _uninstallButton;
    private readonly Button _diagnosticsButton;
    private readonly Button _closeButton;

    public SetupLauncherForm()
    {
        Text = "MetadataDel";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 380);
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        _titleLabel = new Label
        {
            AutoSize = false,
            Location = new Point(24, 20),
            Size = new Size(512, 32),
            Font = new Font("Segoe UI", 16F, FontStyle.Bold, GraphicsUnit.Point),
            Text = "Установка и удаление MetadataDel"
        };

        _subtitleLabel = new Label
        {
            AutoSize = false,
            Location = new Point(24, 58),
            Size = new Size(512, 44),
            Text = "Используйте это окно, чтобы установить программу, удалить её или проверить, какие форматы доступны на этом компьютере."
        };

        _installButton = CreateButton("Установить", 24, 118);
        _installButton.Click += (_, _) => RunAction(InstallAsync);

        _uninstallButton = CreateButton("Удалить", 196, 118);
        _uninstallButton.Click += (_, _) => RunAction(UninstallAsync);

        _diagnosticsButton = CreateButton("Диагностика", 368, 118);
        _diagnosticsButton.Click += (_, _) => ShowDiagnostics();

        _statusTextBox = new TextBox
        {
            Location = new Point(24, 176),
            Size = new Size(512, 150),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Text = "Нажмите «Диагностика», чтобы увидеть доступные форматы и зависимости Office."
        };

        _closeButton = new Button
        {
            Text = "Закрыть",
            Size = new Size(120, 36),
            Location = new Point(416, 336)
        };
        _closeButton.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            _titleLabel,
            _subtitleLabel,
            _installButton,
            _uninstallButton,
            _diagnosticsButton,
            _statusTextBox,
            _closeButton
        });
    }

    private Button CreateButton(string text, int x, int y) =>
        new()
        {
            Text = text,
            Size = new Size(152, 40),
            Location = new Point(x, y)
        };

    private async void RunAction(Func<Task<string>> action)
    {
        ToggleButtons(enabled: false);
        try
        {
            _statusTextBox.Text = await action();
        }
        catch (Exception ex)
        {
            _statusTextBox.Text = "Ошибка: " + ex.Message;
            MessageBox.Show(this, ex.Message, "MetadataDel", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ToggleButtons(enabled: true);
        }
    }

    private Task<string> InstallAsync()
    {
        var summary = SelfInstaller.Install();
        var builder = new StringBuilder();
        builder.AppendLine("Программа установлена.");
        builder.AppendLine("Папка установки: " + summary.InstallDirectory);
        builder.AppendLine("Контекстное меню добавлено для: " + string.Join(", ", summary.RegisteredExtensions));

        if (summary.Warnings.Count > 0)
        {
            builder.AppendLine();
            foreach (var warning in summary.Warnings)
            {
                builder.AppendLine("Предупреждение: " + warning);
            }
        }

        builder.AppendLine();
        builder.AppendLine("Удаление доступно через «Установленные приложения» Windows.");
        return Task.FromResult(builder.ToString().Trim());
    }

    private Task<string> UninstallAsync()
    {
        var summary = SelfInstaller.Uninstall(removeFiles: true);
        var builder = new StringBuilder();
        builder.AppendLine("Контекстное меню удалено.");

        if (summary.FilesRemoved)
        {
            builder.AppendLine("Файлы приложения удалены.");
        }
        else if (summary.RemovalScheduled)
        {
            builder.AppendLine("Удаление файлов запланировано после закрытия программы.");
        }
        else
        {
            builder.AppendLine("Файлы приложения не найдены.");
        }

        return Task.FromResult(builder.ToString().Trim());
    }

    private void ShowDiagnostics()
    {
        var diagnostics = SelfInstaller.GetDiagnostics();
        var builder = new StringBuilder();
        builder.AppendLine("Контекстное меню может быть добавлено для: " + string.Join(", ", diagnostics.RegisterableExtensions));

        if (diagnostics.Warnings.Count > 0)
        {
            builder.AppendLine();
            foreach (var warning in diagnostics.Warnings)
            {
                builder.AppendLine("Предупреждение: " + warning);
            }
        }
        else
        {
            builder.AppendLine();
            builder.AppendLine("Все необходимые зависимости найдены.");
        }

        _statusTextBox.Text = builder.ToString().Trim();
    }

    private void ToggleButtons(bool enabled)
    {
        _installButton.Enabled = enabled;
        _uninstallButton.Enabled = enabled;
        _diagnosticsButton.Enabled = enabled;
        _closeButton.Enabled = enabled;
    }
}
