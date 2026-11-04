using System.Windows;

namespace QuickNote;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Multiple instances are allowed (per user and across Windows accounts).
        // Concurrent database access is handled with SQLite WAL + busy_timeout.
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }
}
