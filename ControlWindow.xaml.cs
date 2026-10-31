using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace EQMobOverlay;

public partial class ControlWindow : Window
{
    private readonly MainWindow _overlay;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _allowClose;
    private bool _syncingSettings;

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
        _syncingSettings = true;
        OverlayEnabledCheck.IsChecked = enabled;
        _syncingSettings = false;
    }

    public void SyncLockCheckbox(bool locked)
    {
        _syncingSettings = true;
        LockOverlayCheck.IsChecked = locked;
        _syncingSettings = false;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshProcessList();

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessList.SelectedItem is not EqProcessCandidate selected)
        {
            ProcessHelp.Text = "Select the EverQuest/Quarm window from the list first.";
            return;
        }

        _overlay.SelectEqProcess(selected);
        ProcessHelp.Text = $"Selected {selected.WindowTitle} — {selected.ProcessName} (PID {selected.ProcessId}).";
        RefreshStatus();
    }

    private void AutoButton_Click(object sender, RoutedEventArgs e)
    {
        _overlay.UseAutoDetect();
        ProcessHelp.Text = "Auto detect enabled.";
        RefreshStatus();
    }

    private void UsePidButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PidBox.Text?.Trim(), out var pid) || pid <= 0)
        {
            ProcessHelp.Text = "Enter a valid numeric PID from Task Manager > Details.";
            return;
        }

        var attached = _overlay.SelectEqProcessByPid(pid);
        ProcessHelp.Text = attached
            ? $"Attached directly to PID {pid}."
            : $"Could not attach to PID {pid}. Try running the overlay as Administrator if access is denied.";
        RefreshProcessList();
        RefreshStatus();
    }

    private void OverlayEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;
        var enabled = OverlayEnabledCheck.IsChecked == true;
        _overlay.SetOverlayEnabled(enabled);
        if (System.Windows.Application.Current is App app) app.SetTrayOverlayChecked(enabled);
    }

    private void KeepVisibleCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;
        _overlay.SetKeepVisibleWhenInactive(KeepVisibleCheck.IsChecked == true);
    }

    private void LockOverlayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;
        var locked = LockOverlayCheck.IsChecked == true;
        _overlay.SetLocked(locked);
        if (System.Windows.Application.Current is App app) app.SetTrayLockChecked(locked);
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
            select = processes.FirstOrDefault(p => p.ProcessId == previous.ProcessId && p.WindowHandle == previous.WindowHandle);
        if (select is null && processes.Count == 1) select = processes[0];
        ProcessList.SelectedItem = select;

        if (processes.Count == 0)
            ProcessHelp.Text = "No top-level windows found. You can still enter the EQ client PID manually above.";
    }

    private void RefreshStatus()
    {
        var status = _overlay.GetStatus();
        ConnectionStatus.Text = status.ConnectionText;
        TargetStatus.Text = status.TargetText;
        DatabaseStatus.Text = status.DatabaseText;

        _syncingSettings = true;
        OverlayEnabledCheck.IsChecked = status.OverlayEnabled;
        KeepVisibleCheck.IsChecked = status.KeepVisibleWhenInactive;
        LockOverlayCheck.IsChecked = status.Locked;
        _syncingSettings = false;

        if (System.Windows.Application.Current is App app)
        {
            app.SetTrayOverlayChecked(status.OverlayEnabled);
            app.SetTrayLockChecked(status.Locked);
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
