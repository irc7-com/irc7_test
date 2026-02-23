using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Irc7.LoadTester.Core;
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace Irc7.LoadTester;

public partial class MainWindow : Window
{
    private readonly IrcConnectionManager _manager = new();
    private DispatcherTimer? _dispatcherTimer;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _screenLogQueue = new();
    private DispatcherTimer? _screenLogFlushTimer;
    private const int _screenLogFlushMs = 200;
    private const int _screenLogMaxChars = 20000; // keep recent text small to avoid UI cost
    private int _uiPollIntervalMs = 1500;

    public MainWindow()
    {
        InitializeComponent();

        // Buttons
        StartButton.Click += StartButton_Click;
        StopButton.Click += StopButton_Click;

        // Wire menu + checkbox controls
            try
            {
                var sendBox = this.FindControl<CheckBox>("SendPrivmsgBox");
                var menuSend = this.FindControl<MenuItem>("MenuSendPrivmsg");
                var menuLog = this.FindControl<MenuItem>("MenuLogToFile");
                var menuVerbose = this.FindControl<MenuItem>("MenuVerboseLog");
                var menuScreen = this.FindControl<MenuItem>("MenuScreenLog");
                var uiVerbose = this.FindControl<CheckBox>("VerboseLogBox");
                var uiScreen = this.FindControl<CheckBox>("ScreenLogBoxUI");

                // Keep menu and checkbox in sync
                sendBox.IsCheckedChanged += (_, __) =>
                {
                    var enabled = sendBox.IsChecked ?? false;
                    try { menuSend.Header = enabled ? "Send PRIVMSG ✓" : "Send PRIVMSG"; } catch { }
                    _manager.SetSendPrivmsgEnabled(enabled);
                };

                menuSend.Click += (_, __) =>
                {
                    try
                    {
                        var header = menuSend.Header?.ToString() ?? "Send PRIVMSG";
                        var enabled = !header.EndsWith(" ✓");
                        menuSend.Header = enabled ? "Send PRIVMSG ✓" : "Send PRIVMSG";
                        sendBox.IsChecked = enabled;
                        _manager.SetSendPrivmsgEnabled(enabled);
                    }
                    catch { }
                };

                menuLog.Click += (_, __) =>
                {
                    try
                    {
                        var header = menuLog.Header?.ToString() ?? "Log to file";
                        var enabled = !header.EndsWith(" ✓");
                        menuLog.Header = enabled ? "Log to file ✓" : "Log to file";
                        _manager.SetFileLoggingEnabled(enabled);
                    }
                    catch { }
                };

                // Menu -> UI sync for verbose
                menuVerbose.Click += (_, __) =>
                {
                    try
                    {
                        var header = menuVerbose.Header?.ToString() ?? "Verbose log";
                        var enabled = !header.EndsWith(" ✓");
                        menuVerbose.Header = enabled ? "Verbose log ✓" : "Verbose log";
                        if (uiVerbose != null) uiVerbose.IsChecked = enabled;
                        _manager.SetVerboseLoggingEnabled(enabled);
                    }
                    catch { }
                };

                // Menu -> UI sync for screen log
                menuScreen.Click += (_, __) =>
                {
                    try
                    {
                        var header = menuScreen.Header?.ToString() ?? "Screen log";
                        var enabled = !header.EndsWith(" ✓");
                        menuScreen.Header = enabled ? "Screen log ✓" : "Screen log";
                        if (uiScreen != null) uiScreen.IsChecked = enabled;
                        var box = this.FindControl<TextBox>("ScreenLogBox");
                        if (box != null) box.IsVisible = enabled;
                        _manager.SetScreenLoggingEnabled(enabled);
                        if (enabled) try { _screenLogQueue.Enqueue("[UI] Screen log enabled"); } catch { }
                    }
                    catch { }
                };

                // UI checkboxes -> manager + menu sync
                if (uiVerbose != null)
                {
                    uiVerbose.IsCheckedChanged += (_, __) =>
                    {
                        var enabled = uiVerbose.IsChecked ?? false;
                        try { menuVerbose.Header = enabled ? "Verbose log ✓" : "Verbose log"; } catch { }
                        _manager.SetVerboseLoggingEnabled(enabled);
                    };
                }
                if (uiScreen != null)
                {
                    uiScreen.IsCheckedChanged += (_, __) =>
                    {
                        var enabled = uiScreen.IsChecked ?? false;
                        try { menuScreen.Header = enabled ? "Screen log ✓" : "Screen log"; } catch { }
                        var box = this.FindControl<TextBox>("ScreenLogBox");
                        if (box != null) box.IsVisible = enabled;
                        _manager.SetScreenLoggingEnabled(enabled);
                        if (enabled) try { _screenLogQueue.Enqueue("[UI] Screen log enabled"); } catch { }
                    };
                }
            }
            catch { }

        // Update PRIVMSG byte estimate when channel or length change
        try
        {
            var channelBox = this.FindControl<TextBox>("ChannelBox");
            var privLenBox = this.FindControl<TextBox>("PrivmsgLengthBox");

            try
            {
                channelBox.TextChanged += (_, __) => UpdatePrivmsgBytes();
                privLenBox.TextChanged += (_, __) => UpdatePrivmsgBytes();
            }
            catch
            {
                channelBox.LostFocus += (_, __) => UpdatePrivmsgBytes();
                privLenBox.LostFocus += (_, __) => UpdatePrivmsgBytes();
            }

            UpdatePrivmsgBytes();
        }
        catch { }

        // Start periodic update of status bar
        _dispatcherTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(_uiPollIntervalMs), DispatcherPriority.Background, (s, ev) => UpdateStatusBar());
        _dispatcherTimer.Start();

        // Screen log flush timer (batch updates to UI)
        _screenLogFlushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(_screenLogFlushMs), DispatcherPriority.Background, (s, ev) => FlushScreenLog());
        _screenLogFlushTimer.Start();

        // Subscribe to manager log lines for on-screen logging (enqueue only)
        try
        {
            _manager.LogLine += (line) =>
            {
                try { _screenLogQueue.Enqueue(line); } catch { }
            };
        }
        catch { }
    }

    private void UpdateStatusBar()
    {
        try
        {
            var (total, connected, joined, errors, recv, sent, bytesRecv, bytesSent) = _manager.GetCounters();
            StatusTotal.Text = total.ToString();
            StatusConnected.Text = connected.ToString();
            StatusJoined.Text = joined.ToString();
            StatusRecv.Text = recv.ToString();
            StatusSent.Text = sent.ToString();
            StatusBytesRecv.Text = FormatBytes(bytesRecv);
            StatusBytesSent.Text = FormatBytes(bytesSent);
            StatusErrors.Text = errors.ToString();

            UpdatePrivmsgBytes();
        }
        catch { }
    }

    private void UpdatePrivmsgBytes()
    {
        try
        {
            var channel = this.FindControl<TextBox>("ChannelBox")?.Text ?? "%#Test";
            var privLenText = this.FindControl<TextBox>("PrivmsgLengthBox")?.Text ?? "0";
            var privLen = int.TryParse(privLenText, out var pl) ? Math.Max(0, pl) : 0;

            var message = new string('A', privLen);
            var command = $"PRIVMSG {channel} :{message}\r\n";
            var byteCount = Encoding.ASCII.GetByteCount(command);

            StatusPrivmsgBytes.Text = FormatBytes(byteCount);
        }
        catch
        {
            try { StatusPrivmsgBytes.Text = "0 B"; } catch { }
        }
    }

    private void FlushScreenLog()
    {
        try
        {
            if (_screenLogQueue.IsEmpty) return;
            var box = this.FindControl<TextBox>("ScreenLogBox");
            if (box == null || !box.IsVisible) return;

            var sb = new StringBuilder();
            var count = 0;
            while (_screenLogQueue.TryDequeue(out var line) && count < 500)
            {
                sb.AppendLine(line);
                count++;
            }

            if (sb.Length == 0) return;

            // Append on UI thread
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    box.Text += sb.ToString();
                    if (box.Text.Length > _screenLogMaxChars)
                    {
                        box.Text = box.Text[^_screenLogMaxChars..];
                    }
                    // scroll to end
                    try { box.CaretIndex = box.Text?.Length ?? 0; } catch { }
                }
                catch { }
            });
        }
        catch { }
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024.0;
        const double MB = KB * 1024.0;
        const double GB = MB * 1024.0;
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < MB) return $"{bytes / KB:0.##} KB";
        if (bytes < GB) return $"{bytes / MB:0.##} MB";
        return $"{bytes / GB:0.##} GB";
    }

    private async void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        var host = HostBox.Text ?? "localhost";
        var port = int.TryParse(PortBox.Text, out var p) ? p : 6667;
        var count = int.TryParse(CountBox.Text, out var c) ? c : 10;
        var prefix = NickPrefixBox.Text ?? "TestUser";
        var channel = ChannelBox.Text ?? "%#Test";
        var cycle = (this.FindControl<CheckBox>("CycleSocketsBox")?.IsChecked) ?? false;
        var cycleGrace = int.TryParse(this.FindControl<TextBox>("CycleGraceBox")?.Text, out var cg) ? Math.Max(0, cg) : 0;
        var sendPriv = (this.FindControl<CheckBox>("SendPrivmsgBox")?.IsChecked) ?? false;
        var intervalTag = (this.FindControl<ComboBox>("IntervalBox")?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var interval = int.TryParse(intervalTag, out var iv) ? iv : 1000;

        try
        {
            await _manager.StartAsync(host, port, count, prefix, channel, sendPriv, sendPriv, interval, cycle, cycleGrace);
        }
        catch
        {
            StopButton.IsEnabled = false;
            StartButton.IsEnabled = true;
        }
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = true;
        try
        {
            await _manager.StopAsync();
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { _dispatcherTimer?.Stop(); } catch { }
        try
        {
            var stopTask = _manager.StopAsync();
            if (stopTask != null)
            {
                if (!stopTask.Wait(1500))
                {
                    try { _manager.ForceStopNow(); } catch { }
                }
            }
        }
        catch { try { _manager.ForceStopNow(); } catch { } }
        try { Process.GetCurrentProcess().Kill(); } catch { }
    }
}
