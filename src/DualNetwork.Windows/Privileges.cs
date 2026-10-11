using System.Diagnostics;
using System.Security.Principal;

namespace DualNetwork.Windows;

public static class Privileges
{
    public static bool IsElevated { get; } = Check();

    private static bool Check()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Relaunches the current executable elevated (UAC prompt). Returns false if the user declined.</summary>
    public static bool RestartElevated(string? arguments = null)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path");
        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments ?? "") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }
}
