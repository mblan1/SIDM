using System.Diagnostics;
using System.Windows.Forms;

namespace SIDM.SetupLauncher;

internal static class Program
{
    private const string BootstrapExeName = "SIDMSetup-Bootstrap.exe";

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SIDM");

        var bootstrap = ResolveBootstrap();
        if (bootstrap is null)
        {
            MessageBox.Show(
                $"{BootstrapExeName} was not found next to this launcher. " +
                "Please re-download the SIDM installer.",
                "SIDM Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }

        using var form = new LauncherForm(defaultPath);
        var result = form.ShowDialog();
        if (result != DialogResult.OK || string.IsNullOrWhiteSpace(form.ChosenPath))
        {
            return 1;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = bootstrap,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("--installto");
            psi.ArgumentList.Add(form.ChosenPath);

            using var proc = Process.Start(psi);
            if (proc is null) return 3;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start the installer:\n{ex.Message}",
                "SIDM Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 4;
        }
    }

    private static string? ResolveBootstrap()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, BootstrapExeName);
        return File.Exists(candidate) ? candidate : null;
    }
}

internal sealed class LauncherForm : Form
{
    private readonly TextBox _pathBox;

    public string? ChosenPath { get; private set; }

    public LauncherForm(string defaultPath)
    {
        Text = "Install SIDM";
        Width = 520;
        Height = 220;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        var heading = new Label
        {
            Text = "Where should SIDM be installed?",
            Font = new System.Drawing.Font(Font.FontFamily, 11, System.Drawing.FontStyle.Bold),
            AutoSize = true,
            Left = 16,
            Top = 16,
        };

        var hint = new Label
        {
            Text = "The default puts SIDM in your user profile so no admin prompt is needed.",
            AutoSize = true,
            Left = 16,
            Top = 44,
            ForeColor = System.Drawing.Color.DimGray,
        };

        _pathBox = new TextBox
        {
            Left = 16,
            Top = 76,
            Width = 380,
            Text = defaultPath,
        };

        var browse = new Button
        {
            Text = "Browse...",
            Left = 404,
            Top = 75,
            Width = 90,
        };
        browse.Click += OnBrowse;

        var install = new Button
        {
            Text = "Install",
            Left = 318,
            Top = 130,
            Width = 80,
            DialogResult = DialogResult.OK,
        };
        install.Click += OnInstall;

        var cancel = new Button
        {
            Text = "Cancel",
            Left = 406,
            Top = 130,
            Width = 80,
            DialogResult = DialogResult.Cancel,
        };

        Controls.Add(heading);
        Controls.Add(hint);
        Controls.Add(_pathBox);
        Controls.Add(browse);
        Controls.Add(install);
        Controls.Add(cancel);
        AcceptButton = install;
        CancelButton = cancel;
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose where to install SIDM",
            UseDescriptionForTitle = true,
            SelectedPath = _pathBox.Text,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dlg.SelectedPath;
        }
    }

    private void OnInstall(object? sender, EventArgs e)
    {
        var path = _pathBox.Text?.Trim();
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show(this, "Please choose an install folder.", "SIDM Setup",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }
        ChosenPath = path;
    }
}
