using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using DualNetwork.Core.Control;
using DualNetwork.Core.Model;
using DualNetwork.Core.Planning;
using DualNetwork.Windows;

namespace DualNetwork.App;

public enum ModeState { Off, On, Standby, Leftovers }

public sealed class MainViewModel : ObservableObject
{
    private readonly DualNetworkHost _host;
    private readonly Dispatcher _ui;
    private readonly List<LogEntry> _allLog = new();
    private int _networkEventsInFlight;

    public MainViewModel(DualNetworkHost host)
    {
        _host = host;
        _ui = Dispatcher.CurrentDispatcher;

        PrimaryCommand = new RelayCommand(OnPrimaryAsync, () => !IsBusy);
        ResumeCommand = new RelayCommand(ResumeAsync, () => !IsBusy);
        RestoreCommand = new RelayCommand(RestoreAsync, () => !IsBusy);
        RefreshCommand = new RelayCommand(() => RefreshAsync(manual: true), () => !IsBusy);
        RunTestCommand = new RelayCommand(RunTestAsync, () => !IsTesting);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        SaveSettingsCommand = new RelayCommand(SaveSettingsAsync, () => !IsBusy);
        ResetSettingsCommand = new RelayCommand(LoadSettingsIntoForm);
        OpenLogFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.Logs));
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(AppPaths.MachineData));
        CopyLogCommand = new RelayCommand(() => Clipboard.SetText(string.Join(Environment.NewLine, VisibleLog.Select(l => l.ToString()))));
        ClearLogCommand = new RelayCommand(() => { _allLog.Clear(); VisibleLog.Clear(); });
        ToggleCableCommand = new RelayCommand(ToggleCableAsync, () => IsDemo && !IsBusy);
        ViewChangesCommand = new RelayCommand(ViewChanges, () => _host.Controller.LastPlan is not null);

        foreach (var e in _host.Log.Recent) AddLog(e);
        _host.Log.Written += (_, e) => _ui.InvokeAsync(() => AddLog(e));
        _host.Monitor.Changed += (_, reason) => _ = OnNetworkChangedAsync(reason);
        LoadSettingsIntoForm();
    }

    // ------------------------------------------------------------------ bindable state

    public bool IsElevated => _host.IsElevated;
    public bool IsDemo => _host.IsDemo;
    public string ElevationText => IsDemo ? "DEMO — simulated network, Windows is not changed" : IsElevated ? "Administrator" : "Standard user — view only";

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }

    private bool _isTesting;
    public bool IsTesting
    {
        get => _isTesting;
        set { if (Set(ref _isTesting, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested(); }
    }

    private ModeState _mode;
    public ModeState Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            Raise(nameof(ModeTitle));
            Raise(nameof(ModeSeverity));
            Raise(nameof(PrimaryButtonText));
            Raise(nameof(IsLeftovers));
            Raise(nameof(IsActive));
        }
    }

    public string ModeTitle => Mode switch
    {
        ModeState.On => "ON",
        ModeState.Standby => "STANDBY",
        ModeState.Leftovers => "ACTIVE (previous session)",
        _ => "OFF",
    };

    public Severity ModeSeverity => Mode switch
    {
        ModeState.On => Severity.Good,
        ModeState.Standby or ModeState.Leftovers => Severity.Warning,
        _ => Severity.Info,
    };

    public string PrimaryButtonText => Mode is ModeState.On or ModeState.Standby or ModeState.Leftovers ? "Turn off" : "Turn on";
    public bool IsLeftovers => Mode == ModeState.Leftovers;
    public bool IsActive => Mode is ModeState.On or ModeState.Standby or ModeState.Leftovers;

    private string _modeDetail = "";
    public string ModeDetail { get => _modeDetail; set => Set(ref _modeDetail, value); }

    private Severity _modeDetailSeverity = Severity.Info;
    public Severity ModeDetailSeverity { get => _modeDetailSeverity; set => Set(ref _modeDetailSeverity, value); }

    private string _clientRoleLine = "Ethernet → Client Network";
    public string ClientRoleLine { get => _clientRoleLine; set => Set(ref _clientRoleLine, value); }

    private string _internetRoleLine = "Wi-Fi → Internet";
    public string InternetRoleLine { get => _internetRoleLine; set => Set(ref _internetRoleLine, value); }

    private bool _bothHaveInternet;
    public bool BothHaveInternet { get => _bothHaveInternet; set => Set(ref _bothHaveInternet, value); }

    private string _lastUpdated = "";
    public string LastUpdated { get => _lastUpdated; set => Set(ref _lastUpdated, value); }

    public ObservableCollection<AdapterViewModel> Adapters { get; } = new();
    public ObservableCollection<RoutingLine> RoutingLines { get; } = new();
    public ObservableCollection<DnsGroup> DnsGroups { get; } = new();
    public ObservableCollection<Notice> Notices { get; } = new();
    public ObservableCollection<string> Preserved { get; } = new();
    public ObservableCollection<DiagnosticResult> TestResults { get; } = new();
    public ObservableCollection<StatusSection> StatusSections { get; } = new();
    public ObservableCollection<RouteRow> RouteRows { get; } = new();
    public ObservableCollection<LogEntry> VisibleLog { get; } = new();
    public ObservableCollection<string> AdapterNames { get; } = new();

    private string _testSummary = "Run the test to check both networks.";
    public string TestSummary { get => _testSummary; set => Set(ref _testSummary, value); }

    private bool _showAllAdapters;
    public bool ShowAllAdapters
    {
        get => _showAllAdapters;
        set { if (Set(ref _showAllAdapters, value)) UpdateView(); }
    }

    private bool _showLogDetails;
    public bool ShowLogDetails
    {
        get => _showLogDetails;
        set
        {
            if (!Set(ref _showLogDetails, value)) return;
            VisibleLog.Clear();
            foreach (var e in _allLog.Where(LogVisible)) VisibleLog.Add(e);
        }
    }

    // ------------------------------------------------------------------ settings form

    private string _internetAdapter = Auto;
    public string InternetAdapter { get => _internetAdapter; set => Set(ref _internetAdapter, value); }
    private string _clientAdapter = Auto;
    public string ClientAdapter { get => _clientAdapter; set => Set(ref _clientAdapter, value); }
    private bool _preferClientInternet;
    public bool PreferClientInternet
    {
        get => _preferClientInternet;
        set { if (Set(ref _preferClientInternet, value)) Raise(nameof(PreferWiFiInternet)); }
    }
    public bool PreferWiFiInternet { get => !PreferClientInternet; set => PreferClientInternet = !value; }
    private bool _routeContainingBlock = true;
    public bool RouteContainingBlock { get => _routeContainingBlock; set => Set(ref _routeContainingBlock, value); }
    private bool _routeAllBlocks;
    public bool RouteAllBlocks { get => _routeAllBlocks; set => Set(ref _routeAllBlocks, value); }
    private bool _manageDns = true;
    public bool ManageDns { get => _manageDns; set => Set(ref _manageDns, value); }
    private string _extraPrefixes = "";
    public string ExtraPrefixes { get => _extraPrefixes; set => Set(ref _extraPrefixes, value); }
    private string _internalDomains = "";
    public string InternalDomains { get => _internalDomains; set => Set(ref _internalDomains, value); }
    private bool _showAdvanced;
    public bool ShowAdvanced { get => _showAdvanced; set => Set(ref _showAdvanced, value); }
    private bool _startWithWindows = true;
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    private bool _notifications = true;
    public bool Notifications { get => _notifications; set => Set(ref _notifications, value); }
    private string _settingsStatus = "";
    public string SettingsStatus { get => _settingsStatus; set => Set(ref _settingsStatus, value); }

    public const string Auto = "Automatic";
    public string JournalPath => AppPaths.Journal;
    public string SnapshotPath => AppPaths.Snapshots;
    public string LogPath => AppPaths.Logs;

    // ------------------------------------------------------------------ commands

    public RelayCommand PrimaryCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RunTestCommand { get; }
    public RelayCommand RestartAsAdminCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand CopyLogCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand ToggleCableCommand { get; }
    public RelayCommand ViewChangesCommand { get; }

    /// <summary>Shows a plan in a dialog. Set by the window. The bool is "read-only" (only a Close button).</summary>
    public Func<RoutingPlan, string, bool, bool>? ShowPlan { get; set; }

    /// <summary>Notification-area icon; set by the application.</summary>
    public TrayIcon? Tray { get; set; }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Everything happens automatically from here: leftovers are adopted or cleaned up, the mode is
    /// turned on (unless the user turned it off), sign-in start is kept registered, and monitoring starts.</summary>
    public async Task InitializeAsync()
    {
        var c = _host.Controller;
        _host.Log.Write(LogLevel.Info, $"Dual Network started ({(IsDemo ? "demo, simulated network" : IsElevated ? "Administrator" : "standard user, view only")})");
        IsBusy = true;
        try
        {
            if (!IsElevated)
                _host.Log.Write(LogLevel.Warning, "Administrator rights were not granted — showing status only. Restart Dual Network and accept the prompt to let it manage routing.");
            else if (c.HasLeftovers)
            {
                _host.Log.Write(LogLevel.Info, $"Found changes from a previous session ({c.Journal.Routes.Count} route(s), {c.Journal.NrptRules.Count} DNS rule(s))");
                if (_host.Settings.AutoManage) await c.ResumeAsync();
                else await c.DisableAsync();
            }

            if (IsElevated && _host.Settings.AutoManage && !c.IsEnabled)
                Report(await c.ActivateAsync("App started"));
            else if (!c.IsEnabled)
                await c.PreviewAsync();
            else
                Report(await c.ReconcileAsync("App started"));
        }
        catch (Exception ex)
        {
            _host.Log.Write(LogLevel.Error, $"Startup failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            UpdateView();
        }

        ApplyStartWithWindows();
        _initialized = true;
        NotifyIfChanged();
        _host.Monitor.Start();
    }

    private bool _initialized;

    private async Task OnNetworkChangedAsync(string reason)
    {
        // Collapse bursts: if a refresh is already queued, this event will be covered by it.
        if (Interlocked.Increment(ref _networkEventsInFlight) > 2) { Interlocked.Decrement(ref _networkEventsInFlight); return; }
        try
        {
            if (_host.Controller.IsEnabled) Report(await _host.Controller.ReconcileAsync(reason));
            else await _host.Controller.PreviewAsync();
            await _ui.InvokeAsync(() => { UpdateView(); NotifyIfChanged(); });
        }
        catch (Exception ex)
        {
            _host.Log.Write(LogLevel.Error, $"Re-evaluation after '{reason}' failed: {ex.Message}");
        }
        finally { Interlocked.Decrement(ref _networkEventsInFlight); }
    }

    private async Task RefreshAsync(bool manual)
    {
        IsBusy = true;
        try
        {
            if (manual) _host.InvalidateCaches();
            if (_host.Controller.IsEnabled) Report(await _host.Controller.ReconcileAsync(manual ? "Manual refresh" : "Settings changed"));
            else await _host.Controller.PreviewAsync();
        }
        catch (Exception ex)
        {
            _host.Log.Write(LogLevel.Error, $"Could not read the network configuration: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            UpdateView();
            NotifyIfChanged();
        }
    }

    /// <summary>A failed self-check is the one thing worth interrupting the user for immediately.</summary>
    private void Report(OperationResult result)
    {
        if (!result.Success && result.ChangesMade && _host.Settings.Notifications)
            _ui.InvokeAsync(() => Tray?.Notify("Dual Network: check needed", result.Message, Severity.Problem));
    }

    // ------------------------------------------------------------------ on / off

    private async Task OnPrimaryAsync()
    {
        if (Mode is ModeState.On or ModeState.Standby or ModeState.Leftovers) await TurnOffAsync();
        else await TurnOnAsync();
    }

    private async Task TurnOnAsync()
    {
        if (!IsElevated) { RestartAsAdmin(); return; }
        IsBusy = true;
        try
        {
            SetAutoManage(true);
            _host.InvalidateCaches();
            Report(await _host.Controller.ActivateAsync("Turned on"));
        }
        catch (Exception ex)
        {
            _host.Log.Write(LogLevel.Error, ex.Message);
        }
        finally
        {
            IsBusy = false;
            UpdateView();
            NotifyIfChanged();
        }
    }

    /// <summary>Turns automatic management off (remembered) and restores the original configuration.</summary>
    private async Task TurnOffAsync()
    {
        SetAutoManage(false);
        await RestoreChangesAsync();
    }

    private async Task RestoreChangesAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _host.Controller.DisableAsync();
            if (!result.Success)
                MessageBox.Show(result.Message + "\n\n• " + string.Join("\n• ", result.DetailLines), "Dual Network", MessageBoxButton.OK, MessageBoxImage.Warning);
            await _host.Controller.PreviewAsync();
        }
        catch (Exception ex)
        {
            _host.Log.Write(LogLevel.Error, ex.Message);
        }
        finally
        {
            IsBusy = false;
            UpdateView();
            NotifyIfChanged();
        }
    }

    private void SetAutoManage(bool on)
    {
        if (_host.Settings.AutoManage == on) return;
        _host.Settings.AutoManage = on;
        _host.SaveSettings(_host.Settings);
    }

    private async Task ResumeAsync()
    {
        if (!IsElevated) { RestartAsAdmin(); return; }
        IsBusy = true;
        try { await _host.Controller.ResumeAsync(); }
        catch (Exception ex) { _host.Log.Write(LogLevel.Error, ex.Message); }
        finally { IsBusy = false; UpdateView(); }
    }

    private async Task RestoreAsync()
    {
        if (!IsElevated) { RestartAsAdmin(); return; }
        await RestoreChangesAsync();
    }

    /// <summary>Exit from the tray: monitoring stops, so remove our changes. The on/off choice is remembered and
    /// the mode comes back on by itself at the next start.</summary>
    public async Task PrepareToExitAsync()
    {
        _host.Monitor.Stop();
        if (_host.Controller.Journal.HasChanges || _host.Controller.IsEnabled)
            await RestoreChangesAsync();
        _host.Log.Write(LogLevel.Info, "Dual Network exited");
    }

    private void RestartAsAdmin()
    {
        if (Privileges.RestartElevated("--relaunched"))
        {
            Tray?.Dispose();
            Application.Current.Shutdown();
        }
        else
            _host.Log.Write(LogLevel.Info, "Administrator prompt was declined");
    }

    private void ApplyStartWithWindows()
    {
        if (IsDemo || !Privileges.IsElevated) return;
        try
        {
            var exe = Environment.ProcessPath!;
            var registered = StartupTask.RegisteredPath();
            if (_host.Settings.StartWithWindows)
            {
                if (!string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase))
                {
                    StartupTask.Register(exe);
                    _host.Log.Write(LogLevel.Info, "Dual Network will start automatically (in the notification area) when you sign in to Windows");
                }
            }
            else if (registered is not null)
            {
                StartupTask.Unregister();
                _host.Log.Write(LogLevel.Info, "Dual Network will no longer start when you sign in");
            }
        }
        catch (Exception ex)
        {
            _host.Log.Write(LogLevel.Warning, $"Could not update the sign-in start: {ex.Message}");
        }
    }

    /// <summary>Opens the plan/changes dialog for what is currently in effect.</summary>
    private void ViewChanges()
    {
        var plan = _host.Controller.LastPlan;
        if (plan is null) return;
        var title = _host.Controller.Journal.HasChanges ? "Changes Dual Network made" : "What Dual Network would do";
        ShowPlan?.Invoke(plan, title, true);
    }

    // ------------------------------------------------------------------ notifications

    private string? _lastNotified;

    /// <summary>Tray tooltip/dot always; a notification only when the situation actually changed.</summary>
    private void NotifyIfChanged()
    {
        var c = _host.Controller;
        Tray?.Update(ModeTitle, ModeDetail, ModeSeverity, IsActive);
        if (!_initialized) return;

        var plan = c.LastPlan;
        var key = $"{Mode}|{c.Journal.Routes.Count}|{c.Journal.NrptRules.Count}|{plan?.RefusalReasons.FirstOrDefault()}|{plan?.EffectiveInternetAdapter?.Index}";
        if (key == _lastNotified) return;
        var first = _lastNotified is null;
        _lastNotified = key;
        if (!_host.Settings.Notifications || plan is null) return;

        switch (Mode)
        {
            case ModeState.On when c.Journal.HasChanges:
                Tray?.Notify("Dual Network is on",
                    $"Internet → {plan.EffectiveInternetAdapter?.Name}\nClient network {string.Join(", ", plan.ClientNetworks)} → {plan.ClientAdapter?.Name}",
                    Severity.Good);
                break;
            case ModeState.On when first:
                Tray?.Notify("Dual Network is on", "Routing is already correct. Dual Network is watching and will step in if needed.", Severity.Good);
                break;
            case ModeState.Standby when !first || plan.ClientAdapter is not null:
                Tray?.Notify("Dual Network is standing by", plan.RefusalReasons.FirstOrDefault() ?? "", Severity.Warning);
                break;
            case ModeState.Off when !first:
                Tray?.Notify("Dual Network is off", "The original network configuration is restored.", Severity.Info);
                break;
        }
    }

    // ------------------------------------------------------------------ diagnostics

    private async Task RunTestAsync()
    {
        IsTesting = true;
        TestSummary = "Testing…";
        TestResults.Clear();
        try
        {
            _host.InvalidateCaches();
            var plan = _host.Controller.IsEnabled ? (await _host.Controller.ReconcileAsync("Network test")).Plan! : await _host.Controller.PreviewAsync();
            UpdateView();
            var results = _host.IsDemo ? DemoDiagnostics(plan) : await _host.Diagnostics.RunAsync(_host.Controller.LastState!, plan, _host.Settings.Planner);
            foreach (var r in results) TestResults.Add(r);
            var problems = results.Count(r => r.Status == Severity.Problem);
            var warnings = results.Count(r => r.Status == Severity.Warning);
            TestSummary = problems + warnings == 0
                ? $"All checks passed · {DateTime.Now:HH:mm:ss}"
                : $"{problems} problem(s), {warnings} warning(s) · {DateTime.Now:HH:mm:ss}";
            _host.Log.Write(problems > 0 ? LogLevel.Warning : LogLevel.Success, $"Network test: {TestSummary}");
            foreach (var r in results) _host.Log.Write(LogLevel.Detail, $"  [{r.Status}] {r.Name}: {r.Detail}");
        }
        catch (Exception ex)
        {
            TestSummary = "Test failed: " + ex.Message;
            _host.Log.Write(LogLevel.Error, TestSummary);
        }
        finally { IsTesting = false; }
    }

    // ------------------------------------------------------------------ settings

    private void LoadSettingsIntoForm()
    {
        var s = _host.Settings;
        InternetAdapter = s.Planner.InternetAdapterName ?? Auto;
        ClientAdapter = s.Planner.ClientAdapterName ?? Auto;
        PreferClientInternet = s.Planner.InternetPreference == InternetPreference.ClientNetwork;
        RouteContainingBlock = s.Planner.RouteContainingPrivateBlock;
        RouteAllBlocks = s.Planner.RouteAllPrivateBlocks;
        ManageDns = s.Planner.ManageDns;
        ExtraPrefixes = string.Join(Environment.NewLine, s.Planner.ExtraClientPrefixes);
        InternalDomains = string.Join(Environment.NewLine, s.Planner.ExtraInternalDomains);
        ShowAdvanced = s.ShowAdvanced;
        ShowAllAdapters = s.ShowAllAdapters;
        StartWithWindows = s.StartWithWindows;
        Notifications = s.Notifications;
        SettingsStatus = "";
    }

    private AppSettings SettingsFromForm()
    {
        static List<string> Lines(string text) =>
            text.Split(new[] { '\r', '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        return new AppSettings
        {
            ShowAdvanced = ShowAdvanced,
            ShowAllAdapters = ShowAllAdapters,
            StartWithWindows = StartWithWindows,
            Notifications = Notifications,
            AutoManage = _host.Settings.AutoManage,
            Planner = new PlannerSettings
            {
                InternetAdapterName = InternetAdapter == Auto ? null : InternetAdapter,
                ClientAdapterName = ClientAdapter == Auto ? null : ClientAdapter,
                InternetPreference = PreferClientInternet ? InternetPreference.ClientNetwork : InternetPreference.WiFi,
                RouteContainingPrivateBlock = RouteContainingBlock,
                RouteAllPrivateBlocks = RouteAllBlocks,
                ManageDns = ManageDns,
                ExtraClientPrefixes = Lines(ExtraPrefixes),
                ExtraInternalDomains = Lines(InternalDomains),
                RouteMetric = _host.Settings.Planner.RouteMetric,
            },
        };
    }

    private async Task SaveSettingsAsync()
    {
        _host.SaveSettings(SettingsFromForm());
        _host.Log.Write(LogLevel.Info, "Settings saved");
        ApplyStartWithWindows();
        await RefreshAsync(manual: false); // when on, the new settings are applied right away
        SettingsStatus = _host.Controller.IsEnabled ? $"Saved and applied · {DateTime.Now:HH:mm:ss}" : $"Saved · {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>Internet preference chooser on the overview (only shown when both networks have Internet).</summary>
    public async Task ApplyPreferenceAsync(bool preferClient)
    {
        if (preferClient == (_host.Settings.Planner.InternetPreference == InternetPreference.ClientNetwork)) return;
        PreferClientInternet = preferClient;
        await SaveSettingsAsync();
    }

    // ------------------------------------------------------------------ view building

    private void UpdateView()
    {
        var c = _host.Controller;
        var state = c.LastState;
        var plan = c.LastPlan;

        Mode = c.HasLeftovers ? ModeState.Leftovers
            : c.IsEnabled ? (plan?.Status == PlanStatus.Refused ? ModeState.Standby : ModeState.On)
            : ModeState.Off;
        LastUpdated = state is null ? "" : $"Updated {state.CapturedAt:HH:mm:ss}";
        if (state is null || plan is null) return;

        ClientRoleLine = $"{plan.ClientAdapter?.Name ?? "Ethernet"} → Client Network";
        InternetRoleLine = $"{(plan.EffectiveInternetAdapter ?? plan.InternetAdapter)?.Name ?? "Wi-Fi"} → Internet";
        BothHaveInternet = plan.BothHaveInternet;

        (ModeDetail, ModeDetailSeverity) = Mode switch
        {
            ModeState.Leftovers => ("Changes from an earlier session are still in place. Restart Dual Network as Administrator to adopt or clean them up.", Severity.Warning),
            ModeState.Standby => ("Automatic · standing by: " + plan.RefusalReasons.FirstOrDefault(), Severity.Warning),
            ModeState.On => (c.Journal.HasChanges
                ? $"Automatic · {c.Journal.Routes.Count} temporary route(s){(c.Journal.NrptRules.Count > 0 ? $", {c.Journal.NrptRules.Count} DNS rule(s)" : "")} in effect · watching for changes"
                : "Automatic · routing is already correct · watching for changes", Severity.Good),
            _ when !IsElevated => ("View only: Administrator rights were not granted, so Dual Network cannot manage routing. Turn on to grant them.", Severity.Warning),
            _ => plan.Status switch
            {
                PlanStatus.Refused => ("Off. (Would currently stand by: " + plan.RefusalReasons.FirstOrDefault() + ")", Severity.Info),
                PlanStatus.NothingToDo => ("Off. Your original configuration is in effect; routing happens to be correct right now.", Severity.Info),
                _ => ($"Off. Turning on would add {plan.Routes.Count} temporary route(s){(plan.NrptRules.Count > 0 ? $" and {plan.NrptRules.Count} DNS rule(s)" : "")}.", Severity.Info),
            },
        };

        // Adapters
        // Sync in place: clearing the list would make the bound ComboBoxes reset the user's selection.
        var names = new List<string> { Auto };
        names.AddRange(state.Adapters.Where(a => a.Kind is not (AdapterKind.Loopback or AdapterKind.Vpn)).Select(a => a.Name));
        names.AddRange(new[] { InternetAdapter, ClientAdapter }.Where(n => !string.IsNullOrEmpty(n)));
        names = names.Distinct().ToList();
        foreach (var stale in AdapterNames.Except(names).ToList()) AdapterNames.Remove(stale);
        foreach (var n in names.Where(n => !AdapterNames.Contains(n))) AdapterNames.Add(n);

        Adapters.Clear();
        var shown = state.Adapters
            .Where(a => a.Kind != AdapterKind.Loopback)
            .Where(a => ShowAllAdapters || a.IsUp)
            .OrderBy(a => a.Index == plan.InternetAdapter?.Index ? 0 : a.Index == plan.ClientAdapter?.Index ? 1 : a.Kind == AdapterKind.Vpn ? 2 : 3)
            .ThenBy(a => !a.IsUp);
        foreach (var a in shown) Adapters.Add(AdapterViewModel.From(a, plan));

        // Routing status: ask Windows where traffic goes right now and compare with the intended adapter.
        RoutingLines.Clear();
        var intendedInternet = plan.EffectiveInternetAdapter;
        RoutingLines.Add(Actual("Internet", IPAddress.Parse("8.8.8.8"), intendedInternet, state));
        if (plan.ClientAdapter is { HasUsableIPv4: true } client)
        {
            foreach (var net in plan.ClientNetworks) RoutingLines.Add(Actual(net.ToString(), net.FirstHost, client, state));
            var ranges = (c.IsEnabled ? c.Journal.Routes : plan.Routes).Where(r => r.Purpose == RoutePurpose.ClientRange);
            foreach (var r in ranges) RoutingLines.Add(Actual(r.Destination.ToString(), r.Destination.FirstHost, c.IsEnabled ? client : null, state,
                c.IsEnabled ? null : "after enabling: " + client.Name));
        }

        // DNS
        DnsGroups.Clear();
        foreach (var a in new[] { plan.InternetAdapter, plan.ClientAdapter }.Where(a => a is not null).DistinctBy(a => a!.Index))
            DnsGroups.Add(new DnsGroup($"{a!.Name} DNS", a.DnsServers.Count == 0 ? "None" : string.Join(Environment.NewLine, a.DnsServers),
                a.DnsSuffix is null ? null : $"Suffix: {a.DnsSuffix}"));
        var activeRules = c.Journal.NrptRules;
        if (activeRules.Count > 0)
            DnsGroups.Add(new DnsGroup("Split DNS (active)", string.Join(Environment.NewLine, activeRules.Select(r => $"*{r.Namespace} → {string.Join(", ", r.NameServers)}")),
                "Everything else uses Windows' normal DNS"));
        else if (plan.NrptRules.Count > 0)
            DnsGroups.Add(new DnsGroup("Split DNS (after enabling)", string.Join(Environment.NewLine, plan.NrptRules.Select(r => $"*{r.Namespace} → {string.Join(", ", r.NameServers)}")),
                "Everything else uses Windows' normal DNS"));

        // Notices
        Notices.Clear();
        if (!IsElevated) Notices.Add(new Notice(Severity.Warning, "Administrator rights were not granted, so Dual Network can only show status. Click Restart as Administrator (or Turn on) and accept the Windows prompt."));
        if (Mode != ModeState.Standby) foreach (var r in plan.RefusalReasons) Notices.Add(new Notice(Severity.Problem, r));
        foreach (var w in plan.Warnings) Notices.Add(new Notice(w.Severity, w.Text));

        Preserved.Clear();
        foreach (var p in plan.Preserved) Preserved.Add(p);

        BuildStatusSections(state, plan);

        RouteRows.Clear();
        var owned = c.Journal.Routes.Select(r => r.Key).ToHashSet();
        foreach (var r in state.Routes.Where(r => r.InterfaceIndex != 1).OrderBy(r => r.Destination.Length == 0 ? 0 : 1).ThenBy(r => r.Destination))
            RouteRows.Add(new RouteRow(r.Destination.ToString(), r.IsOnLink ? "on-link" : r.NextHop.ToString(),
                state.Adapter(r.InterfaceIndex)?.Name ?? $"#{r.InterfaceIndex}", r.RouteMetric, state.InterfaceMetric(r.InterfaceIndex), r.Protocol, owned.Contains(r.Key)));
    }

    private RoutingLine Actual(string label, IPAddress probe, AdapterInfo? intended, NetworkState state, string? hint = null)
    {
        var idx = _host.BestInterface(probe);
        var actual = idx is null ? null : state.Adapter(idx.Value);
        var name = actual?.Name ?? (idx is null ? "No route" : $"Interface {idx}");
        var sev = intended is null ? Severity.Info : actual?.Index == intended.Index ? Severity.Good : Severity.Problem;
        return new RoutingLine(label, name, sev, hint ?? (sev == Severity.Problem ? $"expected {intended!.Name}" : null));
    }

    private void BuildStatusSections(NetworkState state, RoutingPlan plan)
    {
        StatusSections.Clear();
        foreach (var (a, role) in new[] { (plan.InternetAdapter, "Internet"), (plan.ClientAdapter, "Client") })
        {
            if (a is null) continue;
            var items = new List<StatusItem>
            {
                a.IsUp ? new(Severity.Good, "Connected") : new(Severity.Problem, "Disconnected"),
            };
            if (role == "Client" && a.HasUsableIPv4) items.Add(new(Severity.Good, $"Client network {string.Join(", ", a.Subnets)}"));
            items.Add(a.Internet switch
            {
                InternetStatus.Available => new(Severity.Good, "Internet available"),
                InternetStatus.NotAvailable => new(role == "Internet" ? Severity.Problem : Severity.Info, "Internet not available"),
                _ => new(Severity.Info, "Internet: unknown"),
            });
            StatusSections.Add(new StatusSection(a.Name, items));
        }

        var routing = RoutingLines.Select(r => new StatusItem(r.Severity, $"{r.Label} → {r.Target}{(r.Hint is null ? "" : $"  ({r.Hint})")}")).ToList();
        StatusSections.Add(new StatusSection("Routing", routing));

        var dns = new List<StatusItem>();
        var rules = _host.Controller.Journal.NrptRules;
        if (rules.Count > 0) dns.Add(new(Severity.Good, $"Client DNS for {string.Join(", ", rules.Select(r => r.Namespace.TrimStart('.')))}"));
        else if (plan.ClientAdapter?.DnsServers.Count > 0) dns.Add(new(Severity.Info, "Client DNS: queried by Windows alongside Wi-Fi DNS (no split-DNS rule)"));
        if (plan.InternetAdapter is not null) dns.Add(new(Severity.Good, $"{plan.InternetAdapter.Name} DNS for public names"));
        StatusSections.Add(new StatusSection("DNS", dns));

        foreach (var vpn in state.Adapters.Where(a => a.IsUp && a.Kind == AdapterKind.Vpn))
            StatusSections.Add(new StatusSection($"VPN · {vpn.Name}", new[] { new StatusItem(Severity.Info, $"{vpn.Product} active — not modified") }));
    }

    // ------------------------------------------------------------------ demo mode

    private bool _cablePlugged = true;
    public string CableButtonText => _cablePlugged ? "Unplug client cable (demo)" : "Plug client cable back in (demo)";

    private async Task ToggleCableAsync()
    {
        _host.Simulation!.ToggleEthernet();
        _cablePlugged = !_cablePlugged;
        Raise(nameof(CableButtonText));
        _host.Log.Write(LogLevel.Info, _cablePlugged ? "(demo) Client Ethernet cable plugged in" : "(demo) Client Ethernet cable unplugged");
        await OnNetworkChangedAsync("Network interface changed");
    }

    /// <summary>The real test sends packets; in demo mode report what the simulation would show.</summary>
    private IReadOnlyList<DiagnosticResult> DemoDiagnostics(RoutingPlan plan)
    {
        var list = new List<DiagnosticResult>();
        var client = plan.ClientAdapter;
        var wifi = plan.InternetAdapter;
        list.Add(new("Ethernet connectivity", client?.IsUp == true ? Severity.Good : Severity.Problem,
            client?.IsUp == true ? $"{client.Name} connected · {string.Join(", ", client.UsableAddresses)}" : "Client cable unplugged"));
        list.Add(new("Wi-Fi connectivity", Severity.Good, $"{wifi?.Name} connected · {string.Join(", ", wifi?.UsableAddresses ?? Array.Empty<AdapterAddress>())}"));
        if (client?.IsUp == true)
        {
            list.Add(new("Internal network reachability", Severity.Good, $"Reached DNS 10.25.30.10, gateway {client.Gateway} (simulated)"));
            list.Add(new($"Internet through {client.Name}", Severity.Info, "Not available (expected for an isolated client network)"));
        }
        var internetIdx = _host.BestInterface(IPAddress.Parse("8.8.8.8"));
        var viaWifi = internetIdx == wifi?.Index;
        list.Add(new("Internet connectivity", viaWifi ? Severity.Good : Severity.Problem,
            viaWifi ? "Microsoft connectivity test passed (simulated)" : "Failed: traffic goes to the client network, which has no Internet (simulated)"));
        list.Add(new("DNS resolution (public)", Severity.Good, "www.microsoft.com resolved via Wi-Fi DNS (simulated)"));
        if (client?.IsUp == true)
        {
            var split = _host.Controller.Journal.NrptRules.Count > 0;
            list.Add(new("DNS resolution (internal: client.local)", split ? Severity.Good : Severity.Warning,
                split ? "client.local → 10.25.30.10 via split-DNS rule (simulated)" : "Answered only if Windows happens to ask the client DNS first (no split-DNS rule)"));
        }
        list.Add(new("Current default route (Internet)", viaWifi ? Severity.Good : Severity.Problem,
            $"8.8.8.8 → {_host.Controller.LastState?.Adapter(internetIdx ?? -1)?.Name ?? "no route"}"));
        return list;
    }

    // ------------------------------------------------------------------ log

    private bool LogVisible(LogEntry e) => ShowLogDetails || e.Level != LogLevel.Detail;

    private void AddLog(LogEntry e)
    {
        _allLog.Add(e);
        if (_allLog.Count > 4000) _allLog.RemoveRange(0, 500);
        if (!LogVisible(e)) return;
        VisibleLog.Add(e);
        while (VisibleLog.Count > 3000) VisibleLog.RemoveAt(0);
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
