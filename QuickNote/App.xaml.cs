using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace QuickNote;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Multiple instances are allowed (per user and across Windows accounts).
        // Concurrent database access is handled with SQLite WAL + busy_timeout.
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Registered here, before WPF opens StartupUri: a failure while the main
        // window or the database is being created used to end the process with
        // no window and no message, which is indistinguishable from "it will not
        // open". Anything that goes wrong now says so and leaves a file behind.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        WriteRunInfo();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = ReportFailure("未处理的异常", e.Exception);

        MessageBox.Show(
            $"QuickNote 启动时出错：{Describe(e.Exception)}\n\n详细记录：{path}",
            "QuickNote 无法继续",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
        Shutdown(1);
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        ReportFailure("进程级未处理异常", e.ExceptionObject as Exception);
    }

    /// <summary>
    /// Appends one failure to the log and returns where it was written.
    /// </summary>
    private static string ReportFailure(string heading, Exception? error)
    {
        var path = FailureLogPath;

        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
              .Append("  ").Append(VersionText)
              .Append("  ").Append(heading)
              .AppendLine();
            sb.AppendLine(error?.ToString() ?? "(无异常对象)");
            sb.AppendLine();

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // One launch that fails in a loop should not grow the file forever.
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                File.Delete(path);

            File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // The log is the last resort; a drive that cannot be written to must
            // not replace the error the user is about to be shown.
        }

        return path;
    }

    /// <summary>
    /// Records which build ran and which data folder it resolved, overwriting
    /// each start. Answers "did the exe I clicked even launch" without a
    /// debugger, and separates that from "it launched and then failed".
    /// </summary>
    private static void WriteRunInfo()
    {
        var path = RunInfoPath;

        try
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
              .Append("  ").Append(VersionText)
              .Append("  pid=").Append(Environment.ProcessId)
              .AppendLine()
              .Append("exe: ")
              .Append(Environment.ProcessPath ?? "(未知)")
              .AppendLine();

            // Resolving the data folder is itself a candidate failure (settings
            // file, an unreadable share, a folder another account owns), so it
            // goes in last and on its own.
            try
            {
                sb.Append("data: ").AppendLine(Services.DatabaseService.AppDataDir);
            }
            catch (Exception dataError)
            {
                sb.Append("data: 解析失败 - ").AppendLine(Describe(dataError));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch
        {
            // Diagnostics must never be the reason the app fails to start.
        }
    }

    private static string Describe(Exception? error) =>
        error == null ? "未知错误" : $"{error.GetType().Name}: {error.Message}";

    private static string VersionText =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知版本";

    // Deliberately pinned to this account's folder rather than the configured
    // data location: the data location is one of the things that can fail.
    private static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickNote");

    private static string FailureLogPath => Path.Combine(LogDirectory, "error.log");

    private static string RunInfoPath => Path.Combine(LogDirectory, "last-run.txt");
}
