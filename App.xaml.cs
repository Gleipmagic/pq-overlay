using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace EQMobOverlay;

public partial class App : System.Windows.Application
{
    private MainWindow? _overlay;
    private ControlWindow? _control;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _overlayMenuItem;
    private Forms.ToolStripMenuItem? _lockMenuItem;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _overlay = new MainWindow();
        _control = new ControlWindow(_overlay);
        MainWindow = _control;

        CreateTrayIcon();

        _overlay.Show();
        _overlay.Hide();
        _control.Show();
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "EQ Mob Overlay",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open controls", null, (_, _) => Dispatcher.Invoke(ShowControlWindow));
        menu.Items.Add("Auto detect EQ", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _overlay?.UseAutoDetect();
            _control?.RefreshNow();
        }));

        _overlayMenuItem = new Forms.ToolStripMenuItem("Show mob overlay")
        {
            Checked = true,
            CheckOnClick = true
        };
        _overlayMenuItem.CheckedChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_overlay is null || _overlayMenuItem is null) return;
            _overlay.SetOverlayEnabled(_overlayMenuItem.Checked);
            _control?.SyncOverlayCheckbox(_overlayMenuItem.Checked);
        });
        menu.Items.Add(_overlayMenuItem);

        _lockMenuItem = new Forms.ToolStripMenuItem("Lock overlay position / size")
        {
            Checked = true,
            CheckOnClick = true
        };
        _lockMenuItem.CheckedChanged += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_overlay is null || _lockMenuItem is null) return;
            _overlay.SetLocked(_lockMenuItem.Checked);
            _control?.SyncLockCheckbox(_lockMenuItem.Checked);
        });
        menu.Items.Add(_lockMenuItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowControlWindow);
    }

    public void ShowControlWindow()
    {
        if (_control is null) return;
        if (!_control.IsVisible) _control.Show();
        if (_control.WindowState == WindowState.Minimized) _control.WindowState = WindowState.Normal;
        _control.Activate();
        _control.RefreshNow();
    }

    public void SetTrayOverlayChecked(bool enabled)
    {
        if (_overlayMenuItem is not null && _overlayMenuItem.Checked != enabled)
            _overlayMenuItem.Checked = enabled;
    }

    public void SetTrayLockChecked(bool locked)
    {
        if (_lockMenuItem is not null && _lockMenuItem.Checked != locked)
            _lockMenuItem.Checked = locked;
    }

    public void UpdateTrayText(string text)
    {
        if (_trayIcon is null) return;
        _trayIcon.Text = string.IsNullOrWhiteSpace(text)
            ? "EQ Mob Overlay"
            : text[..Math.Min(text.Length, 63)];
    }

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;

        _trayIcon?.Dispose();
        _trayIcon = null;

        _overlay?.Close();
        _control?.CloseForExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
        base.OnExit(e);
    }
}
