using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace QuickNote.Services;

/// <summary>
/// Decides which folder the database and images live in, and prepares a shared
/// folder so several Windows accounts on the same computer can use one set of
/// scripts at the same time.
///
/// The shared folder is under ProgramData. Windows gives the account that
/// creates a folder full control and everyone else read-only, so the folder is
/// explicitly opened up to the local Users group right after it is created -
/// before the database is written, so the files inherit the permission.
/// </summary>
internal static class DataLocation
{
    /// <summary>Only this account (the default).</summary>
    public static string CurrentUserDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickNote");

    /// <summary>Machine-wide folder every account can reach.</summary>
    public static string AllUsersDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "QuickNote");

    /// <summary>
    /// Folder this run should use.
    ///
    /// A null scope means the user never chose. A brand new account then picks
    /// up an existing machine-wide database, so enabling sharing once is enough
    /// for everyone on the computer. An account that already has its own data
    /// is left alone: silently pointing it somewhere else would look like the
    /// scripts had disappeared.
    /// </summary>
    public static string Resolve(
        DataScope? scope,
        string? customDirectory,
        bool sharedDatabaseExists,
        bool localDatabaseExists)
    {
        return scope switch
        {
            DataScope.Custom when !string.IsNullOrWhiteSpace(customDirectory) => customDirectory.Trim(),
            DataScope.AllUsers => AllUsersDirectory,
            DataScope.CurrentUser => CurrentUserDirectory,
            _ => sharedDatabaseExists && !localDatabaseExists ? AllUsersDirectory : CurrentUserDirectory,
        };
    }

    public static string Resolve(AppSettings settings) => Resolve(
        settings.DataScope,
        settings.CustomDataDirectory,
        File.Exists(Path.Combine(AllUsersDirectory, DatabaseService.DatabaseFileName)),
        File.Exists(Path.Combine(CurrentUserDirectory, DatabaseService.DatabaseFileName)));

    /// <summary>Same folder, tolerant of trailing separators and case.</summary>
    public static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            var leftFull = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rightFull = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the folder sits on a network share. SQLite's WAL mode relies on
    /// shared memory and is not safe there, so the caller switches journal mode.
    /// </summary>
    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return false;

            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            // Unreadable drive information: assume a local disk.
            return false;
        }
    }

    /// <summary>
    /// Creates the folder if needed, opens a shared folder up to every local
    /// account and proves it is writable. Reports a readable reason on failure
    /// (the usual one being that a shared folder was made read-only by policy).
    /// </summary>
    public static bool TryPrepare(string directory, out string error)
    {
        error = string.Empty;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        // Only the machine-wide folder is opened up: a per-user folder must
        // stay readable by that account alone.
        if (OperatingSystem.IsWindows() && SamePath(directory, AllUsersDirectory))
        {
            try
            {
                GrantUsersAccess(directory);
            }
            catch (Exception)
            {
                // Not the owner (or no right to change permissions): the folder
                // may still be perfectly writable, which the probe below proves.
            }
        }

        try
        {
            var probe = Path.Combine(directory, $".quicknote-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Lets every member of the local Users group modify the folder and
    /// everything created inside it. Only works for the folder's owner, which
    /// is the account that created it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void GrantUsersAccess(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var rule = new FileSystemAccessRule(
            users,
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

        security.AddAccessRule(rule);
        info.SetAccessControl(security);
    }
}
