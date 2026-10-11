using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DualNetwork.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _hintShown;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/DualNetwork.ico"));
        vm.ShowPlan = (plan, title, readOnly) => new ConfirmWindow(plan, title, vm.ShowAdvanced, readOnly) { Owner = IsVisible ? this : null }.ShowDialog() == true;

        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // bring to front even when another app has focus
        Topmost = false;
    }

    /// <summary>Closing the window keeps Dual Network running in the notification area; Exit is in the tray menu.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        if (!_hintShown)
        {
            _hintShown = true;
            _vm.Tray?.Notify("Dual Network is still running", "It keeps managing your networks from the notification area. Right-click the icon to exit.", Core.Planning.Severity.Info);
        }
    }

    private async void PreferWiFi_Click(object sender, RoutedEventArgs e) => await _vm.ApplyPreferenceAsync(false);
    private async void PreferClient_Click(object sender, RoutedEventArgs e) => await _vm.ApplyPreferenceAsync(true);
}
