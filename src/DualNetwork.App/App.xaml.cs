using System.Windows;
using DualNetwork.Windows;

namespace DualNetwork.App;

/// <summary>
/// Startup sequence:
///  1. If another instance runs, bring it to the front and exit.
///  2. Make sure we run elevated (one UAC prompt when started by hand; none when started by the sign-in task).
///  3. Start in the tray, turn Dual Network Mode on automatically, and keep monitoring.
/// Arguments: --background (start hidden in the tray, used by the sign-in task), --demo (simulated network).
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private DualNetworkHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // keep running in the tray when the window is closed

        var demo = e.Args.Contains("--demo");
        var background = e.Args.Contains(StartupTask.BackgroundArgument);
        var relaunched = e.Args.Contains("--relaunched");

        var name = demo ? "DualNetwork.App.Demo" : "DualNetwork.App";
        _instance = SingleInstance.Acquire(name, relaunched ? 10000 : 0);
        if (!_instance.IsFirst)
        {
            _instance.SignalFirstInstance();
            Shutdown();
            return;
        }

        if (!demo && !Privileges.IsElevated)
        {
            // Changing routes needs Administrator: relaunch elevated. Release the instance lock first so the
            // elevated copy can take it.
            _instance.Dispose();
            var args = string.Join(" ", e.Args.Where(a => a != "--relaunched").Append("--relaunched"));
            if (Privileges.RestartElevated(args)) { Shutdown(); return; }
            // UAC declined: continue in view-only mode.
            _instance = SingleInstance.Acquire(name);
        }

        DispatcherUnhandledException += (_, args) =>
        {
            _host?.Log.Write(Core.Control.LogLevel.Error, $"Unexpected error: {args.Exception}");
            MessageBox.Show($"Unexpected error: {args.Exception.Message}\n\nNetwork changes made by Dual Network are temporary and are removed at restart.",
                "Dual Network", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        _host = new DualNetworkHost(demo);
        var vm = new MainViewModel(_host);
        var window = new MainWindow(vm);
        MainWindow = window;

        _tray = new TrayIcon(open: window.ShowFromTray, toggle: () => vm.PrimaryCommand.Execute(null), exit: () => _ = ExitAsync(vm));
        vm.Tray = _tray;
        _instance.Listen(() => Dispatcher.InvokeAsync(window.ShowFromTray));

        if (!background) window.Show();
        await vm.InitializeAsync();
    }

    private async Task ExitAsync(MainViewModel vm)
    {
        await vm.PrepareToExitAsync();
        _tray?.Dispose();
        _tray = null;
        _host?.Dispose();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is signing out / shutting down: routes disappear with the reboot anyway, and leftover DNS rules
        // are cleaned up (or re-adopted) on the next start. Nothing to block on here.
        _tray?.Dispose();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
