using System.Net;
using DualNetwork.Core.Control;

namespace DualNetwork.Windows;

/// <summary>Composition root: wires the Windows implementations (or the demo simulation) into the platform-independent controller.</summary>
public sealed class DualNetworkHost : IDisposable
{
    private readonly WindowsNetworkStateProvider? _windowsProvider;

    public DualNetworkHost(bool demo = false)
    {
        IsDemo = demo;
        Settings = demo ? new AppSettings() : SettingsStore.Load();
        Log = new ActivityLog(demo ? "demo" : null);
        Probe = new InternetProbe();
        Monitor = new NetworkMonitor();
        Diagnostics = new DiagnosticsRunner(Probe);

        if (demo)
        {
            Simulation = new SimulatedNetwork();
            Controller = new DualNetworkController(Simulation, Simulation, Simulation, Simulation, Log, () => Settings.Planner, Simulation);
            BestInterface = Simulation.BestInterface;
            PersistentRoutes = new SimulatedPersistentRoutes(Simulation);
        }
        else
        {
            var nrpt = new NrptManager();
            _windowsProvider = new WindowsNetworkStateProvider(nrpt, Probe);
            Controller = new DualNetworkController(_windowsProvider, new WindowsRouteWriter(), nrpt, new JsonStateStore(), Log, () => Settings.Planner, new WindowsRouteVerifier());
            BestInterface = WindowsNetworkStateProvider.BestInterface;
            PersistentRoutes = new PersistentRouteManager();
        }
    }

    public bool IsDemo { get; }
    /// <summary>Only set in demo mode.</summary>
    public SimulatedNetwork? Simulation { get; }
    public AppSettings Settings { get; private set; }
    public ActivityLog Log { get; }
    public InternetProbe Probe { get; }
    public WindowsNetworkStateProvider Provider => _windowsProvider ?? throw new InvalidOperationException("Not available in demo mode");
    public DualNetworkController Controller { get; }
    public NetworkMonitor Monitor { get; }
    public DiagnosticsRunner Diagnostics { get; }
    /// <summary>Routes saved permanently in Windows (route -p): visibility and control.</summary>
    public IPersistentRouteStore PersistentRoutes { get; }
    /// <summary>Which interface Windows (or the simulation) uses for a destination.</summary>
    public Func<IPAddress, int?> BestInterface { get; }
    public bool IsElevated => IsDemo || Privileges.IsElevated;

    public void InvalidateCaches() => _windowsProvider?.InvalidateCaches();

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        if (!IsDemo) SettingsStore.Save(settings);
    }

    public void Dispose() => Monitor.Dispose();
}
