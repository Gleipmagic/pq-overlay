using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EQMobOverlay;

public partial class MainWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const double MinOverlayWidth = 136.0;
    private const double MinOverlayHeight = 34.0;
    private const double MaxOverlayWidth = 650.0;
    private const double MaxOverlayHeight = 245.0;

    private readonly EqMemoryReader _eq = new();
    private readonly NpcDatabase _db = new();
    private readonly OverlaySettings _settings = OverlaySettings.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private bool _dragging;
    private bool _resizing;
    private System.Windows.Point _dragStartMouse;
    private double _dragStartLeft;
    private double _dragStartTop;
    private string _lastLookupKey = string.Empty;
    private NpcInfo? _npc;
    private CancellationTokenSource? _lookupCts;
    private bool _databaseReady;
    private bool _overlayEnabled = true;
    private bool _tickBusy;
    private bool _locked;
    private bool _keepVisibleWhenInactive;
    private double _normalOpacityPercent;
    private double _hoverOpacityPercent;
    private string _targetStatus = "No target read yet";

    public MainWindow()
    {
        InitializeComponent();
        _locked = _settings.Locked;
        _keepVisibleWhenInactive = _settings.KeepVisibleWhenInactive;
        _normalOpacityPercent = Math.Clamp(_settings.NormalOpacityPercent, 0, 100);
        _hoverOpacityPercent = Math.Clamp(_settings.HoverOpacityPercent, 0, 100);
        Width = Math.Clamp(_settings.OverlayWidth, MinOverlayWidth, MaxOverlayWidth);
        Height = Math.Clamp(_settings.OverlayHeight, MinOverlayHeight, MaxOverlayHeight);

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        _timer.Tick += Timer_Tick;
    }

    public IReadOnlyList<EqProcessCandidate> GetEqProcesses() => _eq.GetCandidates();

    public bool SelectEqProcess(EqProcessCandidate candidate)
    {
        var attached = _eq.SelectProcess(candidate);
        ClearLookup();
        return attached;
    }

    public bool SelectEqProcessByPid(int pid)
    {
        var attached = _eq.SelectProcessByPid(pid);
        ClearLookup();
        return attached;
    }

    public void UseAutoDetect()
    {
        _eq.UseAutoDetect();
        ClearLookup();
    }

    public void SetOverlayEnabled(bool enabled)
    {
        _overlayEnabled = enabled;
        if (!enabled) Hide();
    }

    public void SetKeepVisibleWhenInactive(bool enabled)
    {
        _keepVisibleWhenInactive = enabled;
        _settings.KeepVisibleWhenInactive = enabled;
        _settings.Save();
    }

    public void SetNormalOpacity(double percent)
    {
        _normalOpacityPercent = Math.Clamp(percent, 0, 100);
        _settings.NormalOpacityPercent = _normalOpacityPercent;
        _settings.Save();
        UpdateOpacityFromCursor();
    }

    public void SetHoverOpacity(double percent)
    {
        _hoverOpacityPercent = Math.Clamp(percent, 0, 100);
        _settings.HoverOpacityPercent = _hoverOpacityPercent;
        _settings.Save();
        UpdateOpacityFromCursor();
    }

    public void SetLocked(bool locked)
    {
        _locked = locked;
        _settings.Locked = locked;
        _settings.Save();
        ApplyInteractionStyle();
    }

    public OverlayStatus GetStatus()
    {
        int? pid = null;
        try { pid = _eq.Process?.Id; } catch { }

        return new OverlayStatus(
            _eq.IsAttached,
            pid,
            _eq.ManualSelection,
            _eq.LastAttachStatus,
            _targetStatus,
            _db.Status,
            _overlayEnabled,
            IsVisible,
            _keepVisibleWhenInactive,
            _locked,
            _normalOpacityPercent,
            _hoverOpacityPercent);
    }

    private void ClearLookup()
    {
        _lastLookupKey = string.Empty;
        _npc = null;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Left = 20;
        Top = 20;
        ApplyInteractionStyle();
        UpdateResponsiveLayout();
        UpdateOpacityFromCursor();
        _timer.Start();

        var progress = new Progress<string>(s =>
        {
            _targetStatus = s;
        });

        try
        {
            await _db.EnsureReadyAsync(progress);
            _databaseReady = true;
        }
        catch (Exception ex)
        {
            _targetStatus = "NPC database unavailable";
            Debug.WriteLine(ex);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e) => ApplyInteractionStyle();

    private void ApplyInteractionStyle()
    {
        if (!IsInitialized) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        style |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (_locked) style |= WS_EX_TRANSPARENT;
        else style &= ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);

        PanelRoot.IsHitTestVisible = !_locked;
        ResizeThumb.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        UnlockOutline.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_tickBusy) return;
        _tickBusy = true;
        try
        {
            if (!_eq.EnsureAttached() || _eq.Process is null)
            {
                _targetStatus = "Waiting for a readable EQ client";
                TargetName.Text = "Waiting for EverQuest…";
                TargetName.Foreground = System.Windows.Media.Brushes.White;
                _targetStatus = _databaseReady ? _eq.LastAttachStatus : _db.Status;
                ResetLiveValues();
                Hide();
                return;
            }

            if (!TryGetEqClientBounds(_eq.WindowHandle, out var bounds))
            {
                _targetStatus = "EQ window is minimized or has no usable client area";
                Hide();
                return;
            }

            KeepInsideEq(bounds);
            UpdateOpacityFromCursor();

            var foreground = GetForegroundWindow();
            var overlayHwnd = new WindowInteropHelper(this).Handle;
            var shouldShow = _overlayEnabled &&
                             (_keepVisibleWhenInactive || foreground == _eq.WindowHandle || foreground == overlayHwnd);
            if (shouldShow)
            {
                if (!IsVisible) Show();
            }
            else if (IsVisible)
            {
                Hide();
            }

            var target = _eq.ReadTarget();
            if (target is null)
            {
                _targetStatus = _eq.LastReadStatus;
                TargetName.Text = "No target";
                TargetName.Foreground = System.Windows.Media.Brushes.White;
                ClearLookup();
                ResetLiveValues();
                return;
            }

            var pct = target.HpPercent;
            TargetName.Text = target.DisplayName;
            TargetName.Foreground = GetConBrush(target.PlayerLevel, target.Level);
            DistanceText.Text = $"{target.Distance:0} u";
            HealthText.Text = $"--   {pct:0}%";
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
                HealthText.Text = $"{estimated:N0}   {pct:0}%";
                MRText.Text = _npc.MR.ToString();
                CRText.Text = _npc.CR.ToString();
                DRText.Text = _npc.DR.ToString();
                FRText.Text = _npc.FR.ToString();
                PRText.Text = _npc.PR.ToString();
                _targetStatus = $"{target.DisplayName} — {pct:0}% HP — {target.Distance:0} units — database matched";
            }
            else
            {
                MRText.Text = CRText.Text = DRText.Text = FRText.Text = PRText.Text = "--";
                _targetStatus = $"{target.DisplayName} — {pct:0}% HP — {target.Distance:0} units — no database match";
            }
        }
        finally
        {
            _tickBusy = false;
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
        HealthText.Text = "--   --%";
        MRText.Text = CRText.Text = DRText.Text = FRText.Text = PRText.Text = "--";
        UpdateHealthBar(0);
    }

    private void PanelRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked || _eq.Process is null) return;
        _dragging = true;
        _dragStartMouse = PointToScreen(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        PanelRoot.CaptureMouse();
        e.Handled = true;
    }

    private void PanelRoot_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_locked || !_dragging || _eq.Process is null || e.LeftButton != MouseButtonState.Pressed) return;
        var now = PointToScreen(e.GetPosition(this));
        var scale = GetDpiScale();
        var nextLeft = _dragStartLeft + (now.X - _dragStartMouse.X) / scale;
        var nextTop = _dragStartTop + (now.Y - _dragStartMouse.Y) / scale;

        if (TryGetEqClientBounds(_eq.WindowHandle, out var bounds))
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
        SavePositionAndSize();
        e.Handled = true;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_locked || _eq.Process is null) return;
        _resizing = true;

        var maxWidth = MaxOverlayWidth;
        var maxHeight = MaxOverlayHeight;
        if (TryGetEqClientBounds(_eq.WindowHandle, out var bounds))
        {
            maxWidth = Math.Min(maxWidth, Math.Max(MinOverlayWidth, bounds.Right - Left));
            maxHeight = Math.Min(maxHeight, Math.Max(MinOverlayHeight, bounds.Bottom - Top));
        }

        Width = Math.Clamp(Width + e.HorizontalChange, MinOverlayWidth, maxWidth);
        Height = Math.Clamp(Height + e.VerticalChange, MinOverlayHeight, maxHeight);
        UpdateResponsiveLayout();
        UpdateHealthBar(ParseDisplayedPercent());
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _resizing = false;
        SavePositionAndSize();
    }

    private void UpdateResponsiveLayout()
    {
        if (!IsInitialized) return;
        // At the smallest height the overlay intentionally becomes a resist-only strip.
        var resistOnly = ActualHeight > 0 && ActualHeight < 56;
        HeaderGrid.Visibility = resistOnly ? Visibility.Collapsed : Visibility.Visible;
        HealthTrack.Visibility = resistOnly ? Visibility.Collapsed : Visibility.Visible;
        HeaderSpacer.Height = new GridLength(resistOnly ? 0 : 3);
        HealthSpacer.Height = new GridLength(resistOnly ? 0 : 3);
        PanelRoot.Padding = new Thickness(resistOnly ? 3 : 4);
    }

    private void UpdateOpacityFromCursor()
    {
        if (!IsInitialized) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var hovered = false;
        if (GetCursorPos(out var cursor) && GetWindowRect(hwnd, out var rect))
            hovered = cursor.X >= rect.Left && cursor.X <= rect.Right &&
                      cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;

        var percent = hovered ? _hoverOpacityPercent : _normalOpacityPercent;
        Opacity = Math.Clamp(percent / 100.0, 0.0, 1.0);
    }

    private static System.Windows.Media.Brush GetConBrush(byte playerLevel, byte targetLevel)
    {
        var diff = (int)targetLevel - playerLevel;
        if (diff == 0) return System.Windows.Media.Brushes.White;
        if (diff >= 1 && diff <= 3) return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 230, 0));
        if (diff >= 4) return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 55, 55));

        // Old-EQ style con bands. Project Quarm exposes green/light-blue/blue/white/yellow/red;
        // values that would be grey on newer clients are intentionally shown as green here.
        (int lightBlueMin, int greenMin) = playerLevel switch
        {
            >= 58 => (-15, -20),
            57 => (-15, -19),
            >= 55 => (-14, -19),
            >= 53 => (-14, -18),
            52 => (-13, -18),
            >= 49 => (-13, -17),
            >= 47 => (-12, -16),
            46 => (-11, -16),
            45 => (-12, -15),
            >= 43 => (-11, -15),
            >= 41 => (-11, -14),
            40 => (-10, -14),
            >= 37 => (-10, -13),
            >= 34 => (-9, -12),
            33 => (-9, -11),
            >= 31 => (-8, -11),
            >= 29 => (-8, -10),
            28 => (-7, -10),
            >= 25 => (-7, -9),
            >= 22 => (-6, -8),
            21 => (-6, -7),
            >= 16 => (int.MinValue, -6),
            _ => (int.MinValue, -6)
        };

        if (diff >= -5) return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(75, 120, 255));
        if (lightBlueMin != int.MinValue && diff >= lightBlueMin)
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 220, 255));
        if (diff >= greenMin) return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 255, 45));
        return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 255, 45));
    }

    private double ParseDisplayedPercent()
    {
        var text = HealthText.Text ?? string.Empty;
        var pctIndex = text.LastIndexOf('%');
        if (pctIndex <= 0) return 0;
        var start = pctIndex - 1;
        while (start >= 0 && (char.IsDigit(text[start]) || text[start] == '.')) start--;
        return double.TryParse(text[(start + 1)..pctIndex], out var pct) ? pct : 0;
    }

    private void SavePositionAndSize()
    {
        if (_eq.Process is not null && TryGetEqClientBounds(_eq.WindowHandle, out var bounds))
        {
            _settings.OffsetX = Left - bounds.Left;
            _settings.OffsetY = Top - bounds.Top;
        }
        _settings.OverlayWidth = Width;
        _settings.OverlayHeight = Height;
        _settings.Save();
    }

    private void KeepInsideEq(Rect bounds)
    {
        if (_dragging || _resizing) return;

        var desiredLeft = bounds.Left + _settings.OffsetX;
        var desiredTop = bounds.Top + _settings.OffsetY;
        Left = Math.Clamp(desiredLeft, bounds.Left, Math.Max(bounds.Left, bounds.Right - ActualWidth));
        Top = Math.Clamp(desiredTop, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - ActualHeight));
    }

    private double GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    private bool TryGetEqClientBounds(IntPtr hwnd, out Rect bounds)
    {
        bounds = Rect.Empty;
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
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
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
