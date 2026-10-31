using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace EQMobOverlay;

public partial class ControlWindow : Window
{
    private readonly MainWindow _overlay;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _allowClose;
    private bool _syncingCheckbox;

    public ControlWindow(MainWindow overlay)
    {
        _overlay = overlay;
        InitializeComponent();
        Loaded += ControlWindow_Loaded;
        Closing += ControlWindow_Closing;
        _timer.Tick += (_, _) => RefreshStatus();
    }

    private void ControlWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshProcessList();
        RefreshStatus();
        _timer.Start();
    }

    public void RefreshNow()
    {
        RefreshProcessList();
        RefreshStatus();
    }

    public void SyncOverlayCheckbox(bool enabled)
    {
        _syncingCheckbox = true;
        OverlayEnabledCheck.IsChecked = enabled;
        _syncingCheckbox = false;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshProcessList();

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not EqProcessCandidate selected)
        {
            ProcessHelp.Text = "Select a running eqgame.exe from the list first.";
            return;
        }

        _overlay.SelectEqProcess(selected.ProcessId);
        ProcessHelp.Text = $"Manually selected PID {selected.ProcessId}.";
        RefreshStatus();
    }

    private void AutoButton_Click(object sender, RoutedEventArgs e)
    {
        _overlay.UseAutoDetect();
        ProcessHelp.Text = "Auto detect enabled. The first usable eqgame.exe will be selected.";
        RefreshStatus();
    }

    private void OverlayEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingCheckbox) return;
        var enabled = OverlayEnabledCheck.IsChecked == true;
        _overlay.SetOverlayEnabled(enabled);
        if (Application.Current is App app) app.SetTrayOverlayChecked(enabled);
    }

    private void RefreshProcessList()
    {
        var previous = ProcessList.SelectedItem as EqProcessCandidate;
        var processes = _overlay.GetEqProcesses();
        ProcessList.ItemsSource = processes;

        var status = _overlay.GetStatus();
        EqProcessCandidate? select = null;
        if (status.ProcessId.HasValue)
            select = processes.FirstOrDefault(p => p.ProcessId == status.ProcessId.Value);
        if (select is null && previous is not null)
            select = processes.FirstOrDefault(p => p.ProcessId == previous.ProcessId);
        if (select is null && processes.Count == 1) select = processes[0];
        ProcessList.SelectedItem = select;

        if (processes.Count == 0)
            ProcessHelp.Text = "No running eqgame.exe found. Start EverQuest, then click Refresh.";
    }

    private void RefreshStatus()
    {
        var status = _overlay.GetStatus();
        ConnectionStatus.Text = status.ConnectionText;
        TargetStatus.Text = status.TargetText;
        DatabaseStatus.Text = status.DatabaseText;
        SyncOverlayCheckbox(status.OverlayEnabled);

        if (Application.Current is App app)
        {
            app.UpdateTrayText(status.Attached && status.ProcessId.HasValue
                ? $"EQ Mob Overlay — EQ PID {status.ProcessId.Value}"
                : "EQ Mob Overlay — waiting for EQ");
        }
    }

    private void ControlWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _timer.Stop();
            return;
        }

        e.Cancel = true;
        Hide();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _timer.Stop();
        Close();
    }
}
