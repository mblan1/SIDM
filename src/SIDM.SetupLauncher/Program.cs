using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SIDM.SetupLauncher;

internal static class Program
{
    private const string BootstrapExeName = "SIDMSetup-Bootstrap.exe";

    /// <summary>
    /// Velopack writes this when it installs. <c>packId</c> in publish.ps1 is
    /// <c>SIDM</c>, so the key name is <c>SIDM</c>. Per-user installs land in
    /// HKCU; an admin/MSI install would land in HKLM — we check both.
    /// </summary>
    private const string UninstallSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SIDM";

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();

        var existingInstall = TryFindExistingInstall();
        var defaultPath = existingInstall ?? Path.Combine(
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

        using var form = new LauncherForm(defaultPath, isUpdate: existingInstall is not null);
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

    /// <summary>
    /// Locates the Velopack bootstrap installer that this launcher spawns.
    /// Priority:
    ///   1) embedded resource (release builds bundle the bootstrap inside
    ///      SIDMSetup.exe so a standalone download just works) — extracted
    ///      to a temp file,
    ///   2) a copy sitting next to the launcher (the releases/ folder layout,
    ///      and dev builds).
    /// Returns null only when neither exists.
    /// </summary>
    private static string? ResolveBootstrap()
    {
        var embedded = TryExtractEmbeddedBootstrap();
        if (embedded is not null) return embedded;

        var sideBySide = Path.Combine(AppContext.BaseDirectory, BootstrapExeName);
        return File.Exists(sideBySide) ? sideBySide : null;
    }

    /// <summary>
    /// Extracts the embedded bootstrap (logical name <c>SIDMSetup-Bootstrap.exe</c>)
    /// to a per-run temp folder and returns its path, or null when no resource
    /// is embedded (dev/IDE builds). Best-effort: any failure returns null so
    /// the caller falls back to the side-by-side copy.
    /// </summary>
    private static string? TryExtractEmbeddedBootstrap()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream(BootstrapExeName);
            if (stream is null) return null;

            var dir = Path.Combine(Path.GetTempPath(), "SIDM-Setup", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, BootstrapExeName);

            using (var file = File.Create(dest))
            {
                stream.CopyTo(file);
            }
            return dest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the folder the previous SIDM install lives in, or null if
    /// nothing's installed. Reads the standard Windows Uninstall registry —
    /// Velopack writes <c>InstallLocation</c> there at install time. We
    /// require the folder to still exist; a stale registry entry pointing at
    /// a deleted folder counts as "not installed" so the user falls back to
    /// the default path.
    /// </summary>
    private static string? TryFindExistingInstall()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(UninstallSubKey);
                if (key?.GetValue("InstallLocation") is string raw
                    && !string.IsNullOrWhiteSpace(raw))
                {
                    // Velopack pads InstallLocation with trailing NUL bytes
                    // (0x00) — not spaces. string.Trim() only strips Unicode
                    // whitespace, so NULs survive and Directory.Exists
                    // rejects the resulting path. Strip NULs first.
                    var loc = raw.Trim('\0').Trim();
                    if (loc.Length > 0 && Directory.Exists(loc))
                    {
                        return loc;
                    }
                }
            }
            catch
            {
                // Registry access can fail on locked-down machines; the
                // launcher should still work, just without auto-detect.
            }
        }
        return null;
    }
}

internal sealed class LauncherForm : Form
{
    private readonly TextBox _pathBox;

    public string? ChosenPath { get; private set; }

    public LauncherForm(string defaultPath, bool isUpdate = false)
    {
        Text = isUpdate ? "Update SIDM" : "Install SIDM";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        // Layout is stacked top-down. Heights are measured up front (Label
        // wrapping isn't known until layout, so positions hard-coded against
        // a single-line hint break the moment the text grows).
        const int margin = 16;
        const int contentWidth = 488;          // ClientSize.Width - 2*margin
        const int pathBoxWidth = 380;
        const int browseWidth = 92;
        const int actionButtonWidth = 80;
        const int rowGap = 12;

        var headingFont = new System.Drawing.Font(Font.FontFamily, 11, System.Drawing.FontStyle.Bold);
        var headingText = isUpdate
            ? "SIDM is already installed. Update it in place?"
            : "Where should SIDM be installed?";
        var hintText = isUpdate
            ? "Updating in place keeps your downloads, settings, and database."
            : "The default puts SIDM in your user profile so no admin prompt is needed.";

        var headingSize = TextRenderer.MeasureText(headingText, headingFont,
            new System.Drawing.Size(contentWidth, 0), TextFormatFlags.WordBreak);
        var hintSize = TextRenderer.MeasureText(hintText, Font,
            new System.Drawing.Size(contentWidth, 0), TextFormatFlags.WordBreak);

        var heading = new Label
        {
            Text = headingText,
            Font = headingFont,
            AutoSize = false,
            Size = new System.Drawing.Size(contentWidth, headingSize.Height),
            Left = margin,
            Top = margin,
        };

        var hintTop = heading.Bottom + 6;
        var hint = new Label
        {
            Text = hintText,
            AutoSize = false,
            Size = new System.Drawing.Size(contentWidth, hintSize.Height),
            Left = margin,
            Top = hintTop,
            ForeColor = System.Drawing.Color.DimGray,
        };

        var pathRowTop = hint.Bottom + rowGap;
        _pathBox = new TextBox
        {
            Left = margin,
            Top = pathRowTop,
            Width = pathBoxWidth,
            Text = defaultPath,
        };

        var browse = new Button
        {
            Text = "Browse...",
            Left = margin + pathBoxWidth + 8,
            Top = pathRowTop - 1,            // align baseline with TextBox
            Width = browseWidth,
        };
        browse.Click += OnBrowse;

        var actionRowTop = _pathBox.Bottom + rowGap + 8;
        var cancel = new Button
        {
            Text = "Cancel",
            Left = margin + contentWidth - actionButtonWidth,
            Top = actionRowTop,
            Width = actionButtonWidth,
            DialogResult = DialogResult.Cancel,
        };

        var install = new Button
        {
            Text = isUpdate ? "Update" : "Install",
            Left = cancel.Left - actionButtonWidth - 8,
            Top = actionRowTop,
            Width = actionButtonWidth,
            DialogResult = DialogResult.OK,
        };
        install.Click += OnInstall;

        ClientSize = new System.Drawing.Size(
            margin + contentWidth + margin,
            actionRowTop + cancel.Height + margin);

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
