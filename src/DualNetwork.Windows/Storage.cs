using System.Collections.Concurrent;
using System.Text.Json;
using DualNetwork.Core.Control;
using DualNetwork.Core.Planning;

namespace DualNetwork.Windows;

public static class AppPaths
{
    /// <summary>Machine-wide state written while elevated: journal and snapshots.</summary>
    public static string MachineData { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DualNetwork");

    /// <summary>Per-user data: settings and logs.</summary>
    public static string UserData { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualNetwork");

    public static string Journal => Path.Combine(MachineData, "state.json");
    public static string Snapshots => Path.Combine(MachineData, "snapshots");
    public static string Settings => Path.Combine(UserData, "settings.json");
    public static string Logs => Path.Combine(UserData, "logs");

    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Journal and snapshots as JSON under %ProgramData%\DualNetwork.</summary>
public sealed class JsonStateStore : IStateStore
{
    public Journal LoadJournal()
    {
        try
        {
            if (!File.Exists(AppPaths.Journal)) return new Journal();
            return JsonSerializer.Deserialize<Journal>(File.ReadAllText(AppPaths.Journal), JsonDefaults.Options) ?? new Journal();
        }
        catch (Exception)
        {
            // Keep the unreadable file for troubleshooting. Routes are non-persistent, so a reboot is always a safe fallback.
            try { File.Copy(AppPaths.Journal, AppPaths.Journal + ".corrupt", overwrite: true); } catch { /* ignore */ }
            return new Journal();
        }
    }

    public void SaveJournal(Journal journal)
    {
        // A non-elevated process may read but not create the journal; there is nothing to persist then anyway.
        if (!journal.HasChanges && !journal.Enabled && !File.Exists(AppPaths.Journal)) return;
        AppPaths.WriteAtomic(AppPaths.Journal, JsonSerializer.Serialize(journal, JsonDefaults.Options));
    }

    public string SaveSnapshot(ConfigurationSnapshot snapshot)
    {
        Directory.CreateDirectory(AppPaths.Snapshots);
        var path = Path.Combine(AppPaths.Snapshots, $"snapshot-{snapshot.CapturedAt:yyyyMMdd-HHmmss}.json");
        AppPaths.WriteAtomic(path, JsonSerializer.Serialize(snapshot, JsonDefaults.Options));
        PruneSnapshots(keep: 20);
        return path;
    }

    public ConfigurationSnapshot? LoadSnapshot(string path)
    {
        try { return JsonSerializer.Deserialize<ConfigurationSnapshot>(File.ReadAllText(path), JsonDefaults.Options); }
        catch { return null; }
    }

    private static void PruneSnapshots(int keep)
    {
        try
        {
            foreach (var f in new DirectoryInfo(AppPaths.Snapshots).GetFiles("snapshot-*.json").OrderByDescending(f => f.Name).Skip(keep))
                f.Delete();
        }
        catch { /* best effort */ }
    }
}

public sealed class AppSettings
{
    public PlannerSettings Planner { get; set; } = new();
    public bool ShowAdvanced { get; set; }
    public bool ShowAllAdapters { get; set; }
    /// <summary>Manage routing automatically whenever the app runs (the main on/off switch; remembered across restarts).</summary>
    public bool AutoManage { get; set; } = true;
    /// <summary>Start in the tray at sign-in, elevated, via a Task Scheduler logon task.</summary>
    public bool StartWithWindows { get; set; } = true;
    /// <summary>Show a notification when routing is changed or a problem is detected.</summary>
    public bool Notifications { get; set; } = true;
}

public static class SettingsStore
{
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.Settings))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings), JsonDefaults.Options) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings) =>
        AppPaths.WriteAtomic(AppPaths.Settings, JsonSerializer.Serialize(settings, JsonDefaults.Options));
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss} {Level,-7} {Message}";
}

/// <summary>Activity log: in-memory (for the UI) and a daily file under %LOCALAPPDATA%\DualNetwork\logs.</summary>
public sealed class ActivityLog : IActivityLog
{
    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly object _fileLock = new();

    private readonly string? _tag;

    public ActivityLog(string? tag = null) => _tag = tag;

    public event EventHandler<LogEntry>? Written;
    public string CurrentFile => Path.Combine(AppPaths.Logs, $"dualnetwork{(_tag is null ? "" : "-" + _tag)}-{DateTime.Now:yyyyMMdd}.log");

    public IReadOnlyCollection<LogEntry> Recent => _recent.ToArray();

    public void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        _recent.Enqueue(entry);
        while (_recent.Count > 5000 && _recent.TryDequeue(out _)) { }
        try
        {
            lock (_fileLock)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(CurrentFile, entry + Environment.NewLine);
            }
        }
        catch { /* logging must never break networking */ }
        Written?.Invoke(this, entry);
    }
}
