using System.Diagnostics;
using System.Text;

namespace DualNetwork.Windows;

/// <summary>
/// Runs a fixed, internally-built PowerShell script. Callers must only interpolate values that were
/// validated beforehand (IP addresses, DNS names, GUIDs); never user free text.
/// </summary>
internal static class PowerShellRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr)
    {
        public bool Ok => ExitCode == 0;
    }

    private static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static async Task<Result> RunAsync(string script, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var wrapped =
            "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; " +
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "try { " + script + " } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));

        var psi = new ProcessStartInfo(PowerShellPath, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("PowerShell command timed out");
        }
        return new Result(p.ExitCode, (await stdout.ConfigureAwait(false)).Trim(), (await stderr.ConfigureAwait(false)).Trim());
    }
}
