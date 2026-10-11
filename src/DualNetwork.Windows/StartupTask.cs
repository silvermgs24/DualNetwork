using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace DualNetwork.Windows;

/// <summary>
/// "Start with Windows": a Task Scheduler logon task that starts Dual Network with the highest privileges,
/// so it runs elevated in the tray without a UAC prompt at every sign-in.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "Dual Network";
    public const string BackgroundArgument = "--background";

    public static bool IsRegistered() => Run($"/Query /TN \"{TaskName}\"").ExitCode == 0;

    /// <summary>Creates or updates the task for the current user and the current executable.</summary>
    public static void Register(string exePath) => Register(exePath, TaskName, highest: true);

    internal static void Register(string exePath, string taskName, bool highest)
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Dual Network: keeps the client Ethernet network and Wi-Fi Internet working at the same time.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>{(highest ? "HighestAvailable" : "LeastPrivilege")}</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowHardTerminate>true</AllowHardTerminate>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{SecurityElement.Escape(exePath)}"</Command>
                  <Arguments>{BackgroundArgument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"dualnetwork-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            var r = Run($"/Create /TN \"{taskName}\" /XML \"{file}\" /F");
            if (r.ExitCode != 0) throw new InvalidOperationException($"Could not register the startup task: {r.Output.Trim()}");
        }
        finally { File.Delete(file); }
    }

    public static void Unregister()
    {
        if (!IsRegistered()) return;
        var r = Run($"/Delete /TN \"{TaskName}\" /F");
        if (r.ExitCode != 0) throw new InvalidOperationException($"Could not remove the startup task: {r.Output.Trim()}");
    }

    /// <summary>The executable path the registered task starts, or null.</summary>
    public static string? RegisteredPath()
    {
        var r = Run($"/Query /TN \"{TaskName}\" /XML");
        if (r.ExitCode != 0) return null;
        var start = r.Output.IndexOf("<Command>", StringComparison.Ordinal);
        var end = r.Output.IndexOf("</Command>", StringComparison.Ordinal);
        if (start < 0 || end < start) return null;
        return SecurityElementUnescape(r.Output[(start + 9)..end]).Trim('"');
    }

    private static string SecurityElementUnescape(string s) =>
        s.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

    private static (int ExitCode, string Output) Run(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, output);
    }
}

public static class StartupTaskSelfTest
{
    /// <summary>Registers the same task definition at standard privilege under a test name, reads it back, deletes it.</summary>
    public static string Run(string exePath)
    {
        const string name = "Dual Network (self-test)";
        StartupTask.Register(exePath, name, highest: false);
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Query /TN \"{name}\" /V /FO LIST")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var info = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var del = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), $"/Delete /TN \"{name}\" /F") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        del.WaitForExit();
        return info;
    }
}
