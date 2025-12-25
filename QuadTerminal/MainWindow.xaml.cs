using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace QuadTerminal;

public partial class MainWindow : Window
{
    private const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private IntPtr _terminalHwnd = IntPtr.Zero;
    private DispatcherTimer? _windowMonitor;
    private Forms.NotifyIcon? _trayIcon;
    private bool _reallyClosing = false;
    private bool _closeFromTaskbar = false;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "QuadTerminal",
            Visible = true
        };

        // Try to load the app icon, fallback to system icon
        try
        {
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "claude-color.ico");
            if (System.IO.File.Exists(iconPath))
                _trayIcon.Icon = new Icon(iconPath);
            else
                _trayIcon.Icon = SystemIcons.Application;
        }
        catch
        {
            _trayIcon.Icon = SystemIcons.Application;
        }

        // Double-click to show window
        _trayIcon.DoubleClick += (s, e) => ShowWindow();

        // Context menu
        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add("Show", null, (s, e) => ShowWindow());
        contextMenu.Items.Add("Reset Terminal", null, (s, e) => ResetTerminal());
        contextMenu.Items.Add("-"); // Separator
        contextMenu.Items.Add("Exit", null, (s, e) => ExitApplication());
        _trayIcon.ContextMenuStrip = contextMenu;
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _reallyClosing = true;
        Close();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Hook into window messages to detect close source
        var hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        hwndSource?.AddHook(WndProc);

        await LaunchAndEmbedTerminal();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_CLOSE = 0xF060;

        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_CLOSE)
        {
            // lParam = 0 means close from taskbar/keyboard (Alt+F4)
            // lParam != 0 means close from X button (contains screen coordinates)
            _closeFromTaskbar = (lParam == IntPtr.Zero);
        }

        return IntPtr.Zero;
    }

    private async Task LaunchAndEmbedTerminal()
    {
        LoadingText.Text = "Starting terminal...";
        LoadingText.Visibility = Visibility.Visible;

        // Build the WT command for 4 CMD panes in 2x2 grid
        string Pane(string name) => $"--title {name} cmd /k \"echo === {name} === && cd /d K: && wsl\"";
        string wtArgs = $"--focus {Pane("P1")} ; " +
                        $"split-pane -V {Pane("P2")} ; " +
                        "mf left ; " +
                        $"split-pane -H {Pane("P3")} ; " +
                        "mf right ; " +
                        $"split-pane -H {Pane("P4")}";

        // Snapshot existing Windows Terminal windows BEFORE launching
        var existingWindows = GetExistingTerminalWindows();

        // Launch wt.exe (fire and forget - it's a shim that exits immediately)
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = wtArgs,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to start Windows Terminal.\n\nIs it installed?\n\nError: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            _reallyClosing = true;
            Close();
            return;
        }

        // Give WT time to process all split-pane commands
        await Task.Delay(800);

        // Wait for a NEW Windows Terminal window to appear
        _terminalHwnd = await WaitForNewTerminalWindow(existingWindows, timeoutMs: 10000);

        if (_terminalHwnd == IntPtr.Zero)
        {
            System.Windows.MessageBox.Show("Windows Terminal window not found.\n\nThe process started but no window appeared.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            _reallyClosing = true;
            Close();
            return;
        }

        // Small delay for window to stabilize before embedding
        await Task.Delay(300);

        // Embed the terminal window
        EmbedTerminalWindow();

        // Start monitoring if the terminal window still exists
        StartWindowMonitor();

        // Hide loading text
        LoadingText.Visibility = Visibility.Collapsed;
    }

    private async void ResetTerminal()
    {
        // Close current terminal and launch a new one
        CloseEmbeddedTerminal();
        await Task.Delay(300);
        await LaunchAndEmbedTerminal();
    }

    private void StartWindowMonitor()
    {
        _windowMonitor = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _windowMonitor.Tick += (s, e) =>
        {
            if (_terminalHwnd != IntPtr.Zero && !NativeMethods.IsWindow(_terminalHwnd))
            {
                _windowMonitor.Stop();
                _terminalHwnd = IntPtr.Zero;
                // Terminal closed - really exit the app
                _reallyClosing = true;
                Close();
            }
        };
        _windowMonitor.Start();
    }

    private HashSet<IntPtr> GetExistingTerminalWindows()
    {
        var windows = new HashSet<IntPtr>();
        NativeMethods.EnumWindows((hWnd, lParam) =>
        {
            var className = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, className, 256);
            if (className.ToString() == TerminalWindowClass)
                windows.Add(hWnd);
            return true; // Continue enumeration
        }, IntPtr.Zero);
        return windows;
    }

    private async Task<IntPtr> WaitForNewTerminalWindow(HashSet<IntPtr> existingWindows, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            IntPtr newWindow = IntPtr.Zero;
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                var className = new StringBuilder(256);
                NativeMethods.GetClassName(hWnd, className, 256);
                if (className.ToString() == TerminalWindowClass && !existingWindows.Contains(hWnd))
                {
                    newWindow = hWnd;
                    return false; // Stop enumeration - found it
                }
                return true; // Continue enumeration
            }, IntPtr.Zero);

            if (newWindow != IntPtr.Zero)
                return newWindow;

            await Task.Delay(100);
        }

        return IntPtr.Zero;
    }

    private void EmbedTerminalWindow()
    {
        IntPtr parentHwnd = new WindowInteropHelper(this).Handle;

        // Remove title bar and borders from WT window
        long style = (long)NativeMethods.GetWindowLong(_terminalHwnd, NativeMethods.GWL_STYLE);
        style = style & ~NativeMethods.WS_CAPTION & ~NativeMethods.WS_THICKFRAME;
        style = style | NativeMethods.WS_CHILD;
        NativeMethods.SetWindowLong(_terminalHwnd, NativeMethods.GWL_STYLE, new IntPtr(style));

        // Hide WT from taskbar: remove APPWINDOW, add TOOLWINDOW
        long exStyle = (long)NativeMethods.GetWindowLong(_terminalHwnd, NativeMethods.GWL_EXSTYLE);
        exStyle = exStyle & ~NativeMethods.WS_EX_APPWINDOW;
        exStyle = exStyle | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLong(_terminalHwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        // Reparent WT window into our window
        NativeMethods.SetParent(_terminalHwnd, parentHwnd);

        // Resize to fill our client area
        ResizeTerminal();

        // Make sure it's visible
        NativeMethods.ShowWindow(_terminalHwnd, NativeMethods.SW_SHOW);
    }

    private void ResizeTerminal()
    {
        if (_terminalHwnd != IntPtr.Zero && TerminalHost.IsLoaded)
        {
            // Get the position of TerminalHost relative to the window
            var hostPosition = TerminalHost.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0));

            var x = (int)hostPosition.X;
            var y = (int)hostPosition.Y;
            var width = (int)TerminalHost.ActualWidth;
            var height = (int)TerminalHost.ActualHeight;

            NativeMethods.MoveWindow(_terminalHwnd, x, y, width, height, true);
        }
    }

    private void CloseEmbeddedTerminal()
    {
        if (_terminalHwnd != IntPtr.Zero)
        {
            // Unparent before destroying
            NativeMethods.SetParent(_terminalHwnd, IntPtr.Zero);
            NativeMethods.DestroyWindow(_terminalHwnd);
            _terminalHwnd = IntPtr.Zero;
        }
    }

    private void Label_GotFocus(object sender, RoutedEventArgs e)
    {
        // Select all text when the label is focused for easy editing
        if (sender is System.Windows.Controls.TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeTerminal();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        // Close if: _reallyClosing (tray Exit, terminal closed) OR taskbar "Close window"
        if (_reallyClosing || _closeFromTaskbar)
        {
            // Actually closing - clean up
            _windowMonitor?.Stop();

            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            return;
        }

        // X button clicked - minimize to tray instead
        e.Cancel = true;
        Hide();
    }
}
