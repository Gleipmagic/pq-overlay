using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EQMobOverlay;

public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly EqMemoryReader _eq = new();
    private readonly NpcDatabase _db = new();
    private readonly OverlaySettings _settings = OverlaySettings.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private bool _dragging;
    private Point _dragStartMouse;
    private double _dragStartLeft;
    private double _dragStartTop;
    private string _lastLookupKey = string.Empty;
    private NpcInfo? _npc;
    private CancellationTokenSource? _lookupCts;
    private bool _databaseReady;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        _timer.Tick += Timer_Tick;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Left = 20;
        Top = 20;
        _timer.Start();

        var progress = new Progress<string>(s => TargetSubline.Text = s);
        try
        {
            await _db.EnsureReadyAsync(progress);
            _databaseReady = true;
        }
        catch (Exception ex)
        {
            TargetSubline.Text = "NPC database unavailable — live HP%/distance still work";
            Debug.WriteLine(ex);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (!_eq.EnsureAttached() || _eq.Process is null)
        {
            TargetName.Text = "Waiting for EverQuest…";
            TargetSubline.Text = _databaseReady ? "Start eqgame.exe" : _db.Status;
            ResetLiveValues();
            return;
        }

        if (!TryGetEqClientBounds(_eq.Process, out var bounds))
        {
            Visibility = Visibility.Hidden;
            return;
        }

        KeepInsideEq(bounds);

        var foreground = GetForegroundWindow();
        var overlayHwnd = new WindowInteropHelper(this).Handle;
        if (foreground != _eq.Process.MainWindowHandle && foreground != overlayHwnd)
        {
            Visibility = Visibility.Hidden;
            return;
        }
        Visibility = Visibility.Visible;

        var target = _eq.ReadTarget();
        if (target is null)
        {
            TargetName.Text = "No target";
            TargetSubline.Text = _databaseReady ? "Drag this panel anywhere inside EQ" : _db.Status;
            _npc = null;
            _lastLookupKey = string.Empty;
            ResetLiveValues();
            return;
        }

        TargetName.Text = target.DisplayName;
        TargetSubline.Text = $"Lv {target.Level}   •   Spawn {target.SpawnId}";
        DistanceText.Text = $"{target.Distance:0} u";

        var pct = target.HpPercent;
        HpText.Text = $"HP {pct:0}%";
        UpdateHealthBar(pct);

        var lookupKey = $"{target.ZoneId}:{EqMemoryReader.NormalizeNpcName(target.RawName)}";
        if (_databaseReady && lookupKey != _lastLookupKey)
        {
            _lastLookupKey = lookupKey;
            _npc = null;
            _lookupCts?.Cancel();
            _lookupCts?.Dispose();
            _lookupCts = new CancellationTokenSource();
            try
            {
                _npc = await _db.FindAsync(target.RawName, target.ZoneId, _lookupCts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        if (_npc is not null)
        {
            var estimated = (long)Math.Round(_npc.MaxHp * pct / 100.0);
            EstimatedHpText.Text = $"~ {estimated:N0} / {_npc.MaxHp:N0}";
            MRText.Text = _npc.MR.ToString();
            CRText.Text = _npc.CR.ToString();
            DRText.Text = _npc.DR.ToString();
            FRText.Text = _npc.FR.ToString();
            PRText.Text = _npc.PR.ToString();
        }
        else
        {
            EstimatedHpText.Text = "~ -- / --";
            MRText.Text = CRText.Text = DRText.Text = FRText.Text = PRText.Text = "--";
        }
    }

    private void UpdateHealthBar(double percent)
    {
        var available = Math.Max(0, HealthTrack.ActualWidth - 2);
        HealthFill.Width = available * Math.Clamp(percent, 0, 100) / 100.0;
    }

    private void ResetLiveValues()
    {
        DistanceText.Text = "-- u";
        HpText.Text = "HP --%";
        EstimatedHpText.Text = "~ -- / --";
        MRText.Text = CRText.Text = DRText.Text = FRText.Text = PRText.Text = "--";
        UpdateHealthBar(0);
    }

    private void PanelRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_eq.Process is null) return;
        _dragging = true;
        _dragStartMouse = PointToScreen(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        PanelRoot.CaptureMouse();
        e.Handled = true;
    }

    private void PanelRoot_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _eq.Process is null || e.LeftButton != MouseButtonState.Pressed) return;
        var now = PointToScreen(e.GetPosition(this));
        var scale = GetDpiScale();
        var nextLeft = _dragStartLeft + (now.X - _dragStartMouse.X) / scale;
        var nextTop = _dragStartTop + (now.Y - _dragStartMouse.Y) / scale;

        if (TryGetEqClientBounds(_eq.Process, out var bounds))
        {
            Left = Math.Clamp(nextLeft, bounds.Left, Math.Max(bounds.Left, bounds.Right - ActualWidth));
            Top = Math.Clamp(nextTop, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - ActualHeight));
        }
        e.Handled = true;
    }

    private void PanelRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        PanelRoot.ReleaseMouseCapture();

        if (_eq.Process is not null && TryGetEqClientBounds(_eq.Process, out var bounds))
        {
            _settings.OffsetX = Left - bounds.Left;
            _settings.OffsetY = Top - bounds.Top;
            _settings.Save();
        }
        e.Handled = true;
    }

    private void KeepInsideEq(Rect bounds)
    {
        if (!_dragging)
        {
            // Follow the EQ client when it moves. First placement uses saved client-relative coordinates.
            var desiredLeft = bounds.Left + _settings.OffsetX;
            var desiredTop = bounds.Top + _settings.OffsetY;
            Left = Math.Clamp(desiredLeft, bounds.Left, Math.Max(bounds.Left, bounds.Right - ActualWidth));
            Top = Math.Clamp(desiredTop, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - ActualHeight));
        }
    }

    private double GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    private bool TryGetEqClientBounds(Process process, out Rect bounds)
    {
        bounds = Rect.Empty;
        var hwnd = process.MainWindowHandle;
        if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return false;
        if (!GetClientRect(hwnd, out var rect)) return false;
        var topLeft = new POINT { X = rect.Left, Y = rect.Top };
        var bottomRight = new POINT { X = rect.Right, Y = rect.Bottom };
        if (!ClientToScreen(hwnd, ref topLeft) || !ClientToScreen(hwnd, ref bottomRight)) return false;

        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        bounds = new Rect(topLeft.X / scale, topLeft.Y / scale,
            (bottomRight.X - topLeft.X) / scale, (bottomRight.Y - topLeft.Y) / scale);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        _lookupCts?.Cancel();
        _lookupCts?.Dispose();
        _eq.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
