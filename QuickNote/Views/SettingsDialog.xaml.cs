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
