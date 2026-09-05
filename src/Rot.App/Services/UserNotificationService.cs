using System.Drawing;
using System.Windows.Threading;

namespace Rot.App.Services;

internal sealed class UserNotificationService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _hideTimer;
    private readonly Icon? _ownedIcon;
    private readonly System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ContextMenuStrip? _contextMenu;
    private System.Windows.Forms.ToolStripMenuItem? _hidePlayerItem;
    private Func<Task>? _openSettings;
    private Func<Task>? _hidePlayer;
    private Func<Task>? _quit;
    private Func<bool>? _isPlayerVisible;
    private bool _persistentTray;
    private bool _trayInitialized;
    private bool _disposed;

    public UserNotificationService()
        : this(Dispatcher.CurrentDispatcher, TimeSpan.FromSeconds(7))
    {
    }

    internal UserNotificationService(Dispatcher dispatcher, TimeSpan hideInterval)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _hideTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = hideInterval
        };
        _hideTimer.Tick += OnHideTimerTick;

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Web", "assets", "launcher.ico");
            if (File.Exists(iconPath))
            {
                _ownedIcon = new Icon(iconPath);
            }

            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = _ownedIcon ?? SystemIcons.Information,
                Text = "Rot",
                Visible = false
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN System notifications are unavailable: {exception.Message}");
        }
    }

    internal bool IsTrayVisibleForTests => !_disposed && _notifyIcon?.Visible == true;

    internal System.Windows.Forms.ContextMenuStrip? ContextMenuForTests => _contextMenu;

    internal static bool ShouldEnableHidePlayer(bool disposed, bool playerVisible) =>
        !disposed && playerVisible;

    public bool InitializeTray(
        Func<Task> openSettings,
        Func<Task> hidePlayer,
        Func<bool> isPlayerVisible,
        Func<Task> quit)
    {
        ArgumentNullException.ThrowIfNull(openSettings);
        ArgumentNullException.ThrowIfNull(hidePlayer);
        ArgumentNullException.ThrowIfNull(isPlayerVisible);
        ArgumentNullException.ThrowIfNull(quit);

        if (_disposed || _notifyIcon is null)
        {
            return false;
        }

        if (_trayInitialized)
        {
            return true;
        }

        try
        {
            _openSettings = openSettings;
            _hidePlayer = hidePlayer;
            _isPlayerVisible = isPlayerVisible;
            _quit = quit;

            _contextMenu = new System.Windows.Forms.ContextMenuStrip();
            _hidePlayerItem = new System.Windows.Forms.ToolStripMenuItem("Hide Player");
            var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Settings");
            var quitItem = new System.Windows.Forms.ToolStripMenuItem("Quit Rot");
            _contextMenu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("Rot")
            {
                Enabled = false
            });
            _contextMenu.Items.Add(settingsItem);
            _contextMenu.Items.Add(_hidePlayerItem);
            _contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            _contextMenu.Items.Add(quitItem);
            _contextMenu.Opening += OnContextMenuOpening;
            settingsItem.Click += (_, _) => DispatchAsync(_openSettings, "Settings");
            _hidePlayerItem.Click += (_, _) => DispatchAsync(_hidePlayer, "Hide Player");
            quitItem.Click += (_, _) => DispatchAsync(_quit, "Quit Rot");
            _notifyIcon.ContextMenuStrip = _contextMenu;
            _notifyIcon.DoubleClick += OnNotifyIconDoubleClick;
            _persistentTray = true;
            _trayInitialized = true;
            _notifyIcon.Visible = true;
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN System tray is unavailable: {exception.Message}");
            if (_notifyIcon is not null)
            {
                _notifyIcon.ContextMenuStrip = null;
                _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
            }
            _contextMenu?.Dispose();
            _contextMenu = null;
            _hidePlayerItem = null;
            _openSettings = null;
            _hidePlayer = null;
            _isPlayerVisible = null;
            _quit = null;
            return false;
        }
    }

    public bool ShowOneLine(string message)
    {
        if (_disposed || _notifyIcon is null || string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var oneLine = string.Join(' ', message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        try
        {
            _hideTimer.Stop();
            _notifyIcon.BalloonTipTitle = "Rot";
            _notifyIcon.BalloonTipText = oneLine;
            _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
            _notifyIcon.Visible = true;
            _notifyIcon.ShowBalloonTip(3_500);
            _hideTimer.Start();
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Could not show a system notification: {exception.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hideTimer.Stop();
        _hideTimer.Tick -= OnHideTimerTick;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip = null;
            _notifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
            _notifyIcon.Dispose();
        }

        _contextMenu?.Dispose();
        _contextMenu = null;
        _hidePlayerItem = null;
        _openSettings = null;
        _hidePlayer = null;
        _isPlayerVisible = null;
        _quit = null;
        _persistentTray = false;
        _trayInitialized = false;
        _ownedIcon?.Dispose();
    }

    private void OnHideTimerTick(object? sender, EventArgs args)
    {
        _hideTimer.Stop();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = _persistentTray;
        }
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (_hidePlayerItem is null || _isPlayerVisible is null)
        {
            return;
        }

        try
        {
            _hidePlayerItem.Enabled = ShouldEnableHidePlayer(_disposed, _isPlayerVisible());
        }
        catch (Exception exception)
        {
            _hidePlayerItem.Enabled = false;
            Console.Error.WriteLine($"[rot] WARN Could not update the system tray menu: {exception.Message}");
        }
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs args) =>
        DispatchAsync(_openSettings, "Settings");

    private void DispatchAsync(Func<Task>? action, string name)
    {
        if (_disposed || action is null)
        {
            return;
        }

        _ = DispatchAsyncCore(action, name);
    }

    private async Task DispatchAsyncCore(Func<Task> action, string name)
    {
        try
        {
            await _dispatcher.InvokeAsync(async () =>
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    await action();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[rot] WARN System tray action '{name}' failed: {exception.Message}");
                }
            }).Task.Unwrap();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[rot] WARN Could not dispatch system tray action '{name}': {exception.Message}");
        }
    }
}
