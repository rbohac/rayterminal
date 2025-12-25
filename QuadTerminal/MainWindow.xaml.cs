using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace QuadTerminal;

public partial class MainWindow : Window
{
    private const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private IntPtr _terminalHwnd = IntPtr.Zero;
    private DispatcherTimer? _windowMonitor;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LaunchAndEmbedTerminal();
    }

    private async Task LaunchAndEmbedTerminal()
    {
        // Build the WT command for 4 CMD panes in 2x2 grid
        // 1. Open first pane (top-left)
        // 2. Split horizontally -> top and bottom, focus on bottom
        // 3. Move focus up to top pane
        // 4. Split vertically -> top-left and top-right
        // 5. Move focus down to bottom pane
        // 6. Split vertically -> bottom-left and bottom-right
        string Pane(string name) => $"--title {name} cmd /k \"echo === {name} === && cd /d K: && wsl\"";
        string wtArgs = $"{Pane("P1")} ; " +
                        $"split-pane -H {Pane("P3")} ; " +
                        "mf up ; " +
                        $"split-pane -V {Pane("P2")} ; " +
                        "mf down ; " +
                        $"split-pane -V {Pane("P4")}";

        // 1. Snapshot existing Windows Terminal windows BEFORE launching
        var existingWindows = GetExistingTerminalWindows();

        // 2. Launch wt.exe (fire and forget - it's a shim that exits immediately)
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
            MessageBox.Show($"Failed to start Windows Terminal.\n\nIs it installed?\n\nError: {ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        // 3. Wait for a NEW Windows Terminal window to appear
        _terminalHwnd = await WaitForNewTerminalWindow(existingWindows, timeoutMs: 10000);

        if (_terminalHwnd == IntPtr.Zero)
        {
            MessageBox.Show("Windows Terminal window not found.\n\nThe process started but no window appeared.",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        // Give the window a moment to fully initialize
        await Task.Delay(300);

        // Embed the terminal window
        EmbedTerminalWindow();

        // Start monitoring if the terminal window still exists
        StartWindowMonitor();

        // Hide loading text
        LoadingText.Visibility = Visibility.Collapsed;
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

    private void Label_GotFocus(object sender, RoutedEventArgs e)
    {
        // Select all text when the label is focused for easy editing
        if (sender is TextBox textBox)
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
        // Stop the window monitor
        _windowMonitor?.Stop();
    }
}
