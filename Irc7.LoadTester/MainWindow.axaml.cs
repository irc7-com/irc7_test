using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Irc7.LoadTester.Core;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Irc7.LoadTester;

public partial class MainWindow : Window
{
    private readonly IrcConnectionManager _manager = new();
    private Avalonia.Threading.DispatcherTimer? _dispatcherTimer;
    private int _uiPollIntervalMs = 1500;

    public MainWindow()
    {
        InitializeComponent();

        StartButton.Click += StartButton_Click;
        StopButton.Click += StopButton_Click;

        // Wire menu + checkbox controls
        try
        {
            var sendBox = this.FindControl<CheckBox>("SendPrivmsgBox");
            var menuSend = this.FindControl<MenuItem>("MenuSendPrivmsg");
            var menuLog = this.FindControl<MenuItem>("MenuLogToFile");

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
        }
        catch { }

        // Start periodic update of status bar only
        _dispatcherTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(_uiPollIntervalMs), Avalonia.Threading.DispatcherPriority.Background, (s, ev) => UpdateStatusBar());
        _dispatcherTimer.Start();
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
        var sendPriv = (this.FindControl<CheckBox>("SendPrivmsgBox")?.IsChecked) ?? false;
        var intervalTag = (this.FindControl<ComboBox>("IntervalBox")?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var interval = int.TryParse(intervalTag, out var iv) ? iv : 1000;

        var periodic = sendPriv;
        await _manager.StartAsync(host, port, count, prefix, channel, periodic, sendPriv, interval);
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = true;
        await _manager.StopAsync();
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
