using System.IO;
using System.Windows;
using QuickNote.Services;

namespace QuickNote.Views;

/// <summary>
/// Chooses where the scripts are stored. Turning on the machine-wide option is
/// what lets several Windows accounts use one set of scripts at the same time.
/// The dialog only reports the choice; copying the data and restarting is the
/// caller's job.
/// </summary>
public partial class SettingsDialog : Window
{
    private bool _initializing = true;

    // The same text the panel shows, assembled once, for the copy button.
    private string _runtimeReport = "";

    public DataScope SelectedScope { get; private set; } = DataScope.CurrentUser;

    public string? SelectedCustomDirectory { get; private set; }

    public SettingsDialog(AppSettings settings, string currentDirectory)
    {
        InitializeComponent();

        UserPathText.Text = DataLocation.CurrentUserDirectory;
        CustomPathBox.Text = settings.CustomDataDirectory ?? string.Empty;

        // Preselect whatever is in use right now.
        if (DataLocation.SamePath(currentDirectory, DataLocation.AllUsersDirectory))
        {
            ScopeAll.IsChecked = true;
        }
        else if (DataLocation.SamePath(currentDirectory, DataLocation.CurrentUserDirectory))
        {
            ScopeUser.IsChecked = true;
        }
        else
        {
            ScopeCustom.IsChecked = true;
            CustomPathBox.Text = currentDirectory;
        }

        _initializing = false;
        UpdateHint();
        ShowRuntimeInfo();
    }

    /// <summary>
    /// Shows where the data and the logs live together with the tail of both
    /// files, so a launch problem can be read here instead of hunting through
    /// AppData. Diagnostics never get to be the reason the dialog fails, so
    /// everything in here is optional.
    /// </summary>
    private void ShowRuntimeInfo()
    {
        try
        {
            var data = DatabaseService.AppDataDir;
            var logs = AppSettings.Directory;

            var paths =
                $"数据目录   {data}\n" +
                $"图片目录   {System.IO.Path.Combine(data, "images")}\n" +
                $"日志目录   {logs}";

            var run = ReadTail(System.IO.Path.Combine(logs, "last-run.txt"), 2000).Trim();
            if (run.Length == 0)
                run = "（还没有记录，下次启动后这里会有版本号、进程号、exe 路径和数据目录）";

            var error = ReadTail(System.IO.Path.Combine(logs, "error.log"), 1600).Trim();
            if (error.Length == 0)
            {
                ErrorHeading.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
                error = "（无错误记录）";
            }

            PathsText.Text = paths;
            RunInfoText.Text = run;
            ErrorText.Text = error;

            _runtimeReport =
                $"数据目录: {data}\n" +
                $"图片目录: {System.IO.Path.Combine(data, "images")}\n" +
                $"日志目录: {logs}\n\n" +
                $"--- last-run.txt ---\n{run}\n\n" +
                $"--- error.log ---\n{error}";
        }
        catch (Exception ex)
        {
            PathsText.Text = "读取运行信息失败：" + ex.Message;
        }
    }

    /// <summary>
    /// Copies the whole report, so sending it somewhere takes one paste rather
    /// than three selections.
    /// </summary>
    private void CopyRuntimeInfo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_runtimeReport))
                Clipboard.SetText(_runtimeReport);
            MessageBox.Show(this, "运行信息已复制到剪贴板，直接粘贴发给我即可。",
                "复制完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制失败：{ex.Message}", "复制运行信息",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Reads the end of a file with FileShare.ReadWrite so an instance that is
    /// writing a crash report at this moment is not blocked.
    /// </summary>
    private static string ReadTail(string path, int characters)
    {
        try
        {
            if (!File.Exists(path))
                return string.Empty;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var wantedBytes = (long)characters * 4;   // UTF-8 worst case per character
            if (stream.Length > wantedBytes)
                stream.Position = stream.Length - wantedBytes;

            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            var text = reader.ReadToEnd();
            return text.Length > characters ? text[^characters..] : text;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", AppSettings.Directory)
                {
                    UseShellExecute = true,
                });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开文件夹失败：{ex.Message}\n\n路径：{AppSettings.Directory}",
                "运行信息", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Scope_Checked(object sender, RoutedEventArgs e) => UpdateHint();

    private void CustomPathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateHint();

    private void UpdateHint()
    {
        if (_initializing || HintText == null)
            return;

        var custom = CustomPathBox?.Text?.Trim() ?? string.Empty;

        var target = ScopeAll.IsChecked == true
            ? DataLocation.AllUsersDirectory
            : ScopeCustom.IsChecked == true
                ? custom
                : DataLocation.CurrentUserDirectory;

        if (ScopeCustom.IsChecked == true && custom.Length > 0 && DataLocation.IsNetworkPath(custom))
        {
            HintText.Text =
                $"将使用：{target}\n" +
                "注意：网络共享盘上 SQLite 只保证单写入，两个账号同时编辑仍有损坏风险；同一台电脑上的账号建议选「本机所有 Windows 用户共享」。";
            return;
        }

        HintText.Text =
            $"将使用：{target}\n" +
            "保存后会把现有话术和图片复制到新位置（原位置保留），然后自动重新启动。";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择存放话术的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = CustomPathBox.Text.Trim(),
        };

        var owner = new WindowHandle(Application.Current.MainWindow);
        if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK)
            return;

        CustomPathBox.Text = dialog.SelectedPath;
        ScopeCustom.IsChecked = true;
        UpdateHint();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ScopeAll.IsChecked == true)
        {
            SelectedScope = DataScope.AllUsers;
            SelectedCustomDirectory = null;
        }
        else if (ScopeCustom.IsChecked == true)
        {
            var path = CustomPathBox.Text.Trim();
            if (path.Length == 0)
            {
                MessageBox.Show(this, "请选择或输入一个文件夹。", "设置",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SelectedScope = DataScope.Custom;
            SelectedCustomDirectory = path;
        }
        else
        {
            SelectedScope = DataScope.CurrentUser;
            SelectedCustomDirectory = null;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>Adapter so the WinForms folder picker stays modal to this window.</summary>
    private sealed class WindowHandle : System.Windows.Forms.IWin32Window
    {
        public WindowHandle(Window window) =>
            Handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;

        public IntPtr Handle { get; }
    }
}
