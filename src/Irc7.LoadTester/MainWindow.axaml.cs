using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Irc7.LoadTester.Core;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;
using Avalonia.Collections;

namespace Irc7.LoadTester;

public partial class MainWindow : Window
{
    private readonly IrcConnectionManager _manager = new();
    private readonly AvaloniaList<ConnectionItem> _connectionItems = new();
    private int? _selectedId;
    private readonly ConcurrentDictionary<int, bool> _pendingUpdates = new();
    private readonly ConcurrentDictionary<int, string> _pendingStatusUpdates = new();
    private volatile bool _statusDirty = false;
    private int _maxStatusPerTick = 10;
    private int _uiPollIntervalMs = 1500;
    private System.Threading.Timer? _refreshTimer;
    private Avalonia.Threading.DispatcherTimer? _dispatcherTimer;
    private int _refreshIntervalMs = 5000;
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private System.Collections.Generic.List<MenuItem>? _intervalMenuItems;
    private System.Collections.Generic.List<string>? _intervalMenuBaseHeaders;

    public MainWindow()
    {
        InitializeComponent();

        StartButton.Click += StartButton_Click;
        StopButton.Click += StopButton_Click;
        SendButton.Click += SendButton_Click;
        ConnectionsList.SelectionChanged += ConnectionsList_SelectionChanged;
        // Wire send-PRIVMSG checkbox and menu item to manager at runtime
        try
        {
            var sendBox = this.FindControl<CheckBox>("SendPrivmsgBox");
            var menuSend = this.FindControl<MenuItem>("MenuSendPrivmsg");
            var menuLog = this.FindControl<MenuItem>("MenuLogToFile");

            // keep checkbox and menu in sync using click toggles (MenuItem doesn't expose IsChecked reliably)
            sendBox.IsCheckedChanged += (_, __) =>
            {
                var enabled = sendBox.IsChecked ?? false;
                // update menu header to show checkmark when enabled
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

            // Log to file menu toggles per-connection file logging
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


        ConnectionsList.ItemsSource = _connectionItems;

        // Insert initial summary slot (label only)
        _connectionItems.Add("Summary");

        // Mark incoming lines as pending; actual UI refresh occurs on the poll timer to avoid per-message UI work
        _manager.LineReceived += (id, line) =>
        {
            _pendingUpdates[id] = true;
            _statusDirty = true;
        };

        // Don't update status bar directly from manager events; coalesce via polling
        _manager.StatusChanged += (id, s) =>
        {
            _pendingStatusUpdates[id] = s;
            _statusDirty = true;
        };
        _manager.Error += (id, ex) =>
        {
            _pendingStatusUpdates[-Math.Abs(id)] = "Error";
            _statusDirty = true;
            Dispatcher.UIThread.InvokeAsync(() => AppendLog($"[ERR {id}] {ex.Message}"), Avalonia.Threading.DispatcherPriority.Background);
        };

        // When summary changes, update list entry and mark summary (id 0) as pending so
        // the summary output pane refreshes on the same interval.
        _manager.SummaryChanged += () =>
        {
            _statusDirty = true;
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateSummary();
                // If summary is selected, update output pane immediately and clear pending flag
                if (ConnectionsList.SelectedIndex == 0)
                {
                    _pendingUpdates.TryRemove(0, out _);
                }
                else
                {
                    _pendingUpdates[0] = true;
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        };
        // Build interval menu references
        _intervalMenuItems = new System.Collections.Generic.List<MenuItem>
        {
            this.FindControl<MenuItem>("Interval1s"),
            this.FindControl<MenuItem>("Interval2s"),
            this.FindControl<MenuItem>("Interval5s"),
            this.FindControl<MenuItem>("Interval10s"),
            this.FindControl<MenuItem>("Interval30s")
        };
        _intervalMenuBaseHeaders = _intervalMenuItems.Select(i => i?.Header?.ToString() ?? string.Empty).ToList();

        // Start a stable DispatcherTimer; actual refresh of logs is gated by _refreshIntervalMs
        _dispatcherTimer = new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(_uiPollIntervalMs), Avalonia.Threading.DispatcherPriority.Background, (s, ev) => OnRefreshTimerTick());
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
        // nothing else here; timer/menu are initialized once in constructor
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

    private void AppendLog(string line)
    {
        LogBox.Text += line + "\n";
        try
        {
            var len = LogBox.Text.Length;
            LogBox.CaretIndex = len;
            LogBox.SelectionStart = len;
            LogBox.SelectionEnd = len;
        }
        catch { }
    }

    private void UpdateConnectionStatus(int id, string status)
    {
        var text = $"{id}: {status}";
        // find existing item
        for (int i = 0; i < _connectionItems.Count; i++)
        {
            var it = _connectionItems[i];
            if (it.Id == id)
            {
                it.Display = text;
                // trigger collection change
                _connectionItems[i] = it;
                return;
            }
        }
        _connectionItems.Add(new ConnectionItem { Id = id, Display = text });
    }

    private void UpdateConnectionStatusFormatted(int id, string formatted)
    {
        for (int i = 0; i < _connectionItems.Count; i++)
        {
            var it = _connectionItems[i];
            if (it.Id == id)
            {
                it.Display = formatted;
                _connectionItems[i] = it;
                return;
            }
        }
        _connectionItems.Add(new ConnectionItem { Id = id, Display = formatted });
    }

    private void UpdateSummary()
    {
        // Keep the list label simple; full summary is shown in the right pane when selected
        if (_connectionItems.Count > 0)
        {
            try
            {
                _connectionItems.RemoveAt(0);
                _connectionItems.Insert(0, "Summary");
            }
            catch
            {
                _connectionItems[0] = "Summary";
            }
        }
        else
        {
            _connectionItems.Add("Summary");
        }

        // Force the List control to refresh its visuals to ensure UI updates immediately
        try
        {
            ConnectionsList.InvalidateVisual();
        }
        catch { }

        // If the summary is currently selected, update the output pane with current summary text
        try
        {
            if (ConnectionsList.SelectedIndex == 0)
            {
                // Ensure the SelectedItem points to the updated summary label and update the output pane
                ConnectionsList.SelectedItem = _connectionItems[0];
                LogBox.Text = _manager.GetSummary();
            }
        }
        catch { }
    }

    // Lightweight view model for list items to avoid string-parsing on the UI thread
    private struct ConnectionItem
    {
        public int Id { get; set; }
        public string Display { get; set; }
        public override string ToString() => Display;
    }

    private void OnRefreshTimerTick()
    {
        // Runs on UI thread via DispatcherTimer
        try
        {
            // Apply a limited number of pending status updates
            var statuses = _manager.GetAllStatuses();
            if (statuses != null && statuses.Count > 0)
            {
                int applied = 0;
                foreach (var kv in statuses)
                {
                    if (applied >= _maxStatusPerTick) break;
                    try { UpdateConnectionStatus(kv.Key, kv.Value); } catch { }
                    applied++;
                }
            }

            // Update status bar only when flagged dirty
            try
            {
                if (_statusDirty)
                {
                    UpdateStatusBar();
                    _statusDirty = false;
                }
            }
            catch { }

            // Refresh selected item's log at the configured interval
            if (!(ConnectionsList.SelectedItem is ConnectionItem sel)) return;

            int id;
            if (sel.Display == "Summary") id = 0;
            else id = sel.Id;

            if (!_pendingUpdates.ContainsKey(id)) return;
            var now = DateTime.UtcNow;
            if ((now - _lastRefreshTime).TotalMilliseconds < _refreshIntervalMs) return;
            _pendingUpdates.TryRemove(id, out _);
            _lastRefreshTime = now;

            try
            {
                if (id == 0)
                {
                    LogBox.Text = _manager.GetSummary();
                }
                else
                {
                    var lines = _manager.GetLog(id);
                    LogBox.Text = string.Join('\n', lines);
                }

                var len = LogBox.Text.Length;
                LogBox.CaretIndex = len;
                LogBox.SelectionStart = len;
                LogBox.SelectionEnd = len;
            }
            catch { }
        }
        catch { }
    }

    private void IntervalMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi)
        {
            var tagStr = mi.Tag?.ToString();
            if (int.TryParse(tagStr, out var ms))
            {
                _refreshIntervalMs = ms;
                try
                {
                                // Only update the configured interval value; the stable DispatcherTimer will use it
                                _lastRefreshTime = DateTime.MinValue; // reset timer so change takes effect immediately
                                Dispatcher.UIThread.InvokeAsync(() => AppendLog($"[INFO] Refresh interval set to {ms} ms"));
                }
                catch (Exception ex)
                {
                    // Log to UI for debugging
                    Dispatcher.UIThread.InvokeAsync(() => AppendLog($"[INFO] Failed to change timer: {ex.Message}"));
                }

                // Force an immediate refresh so the UI reflects any pending lines
                Dispatcher.UIThread.InvokeAsync(() => OnRefreshTimerTick());

                // Update visible headers to indicate selection (append a checkmark)
                if (_intervalMenuItems != null && _intervalMenuBaseHeaders != null)
                {
                    for (int i = 0; i < _intervalMenuItems.Count; i++)
                    {
                        var item = _intervalMenuItems[i];
                        var baseHdr = _intervalMenuBaseHeaders[i];
                        try
                        {
                            item.Header = ReferenceEquals(item, mi) ? baseHdr + " ✓" : baseHdr;
                        }
                        catch { }
                    }
                }
            }
        }
    }

    private void ConnectionsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConnectionsList.SelectedItem is string s)
        {
            // If the summary item is selected (index 0), show the summary in the output pane
            if (s == "Summary")
            {
                _selectedId = null;
                LogBox.Text = _manager.GetSummary();
                return;
            }

            if (int.TryParse(s.Split(':')[0], out var id))
            {
                _selectedId = id;
                var lines = _manager.GetLog(id);
                LogBox.Text = string.Join('\n', lines);
            }
            else
            {
                _selectedId = null;
                LogBox.Text = string.Empty;
            }
        }
    }

    private async void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _connectionItems.Clear();
        LogBox.Text = string.Empty;

        var host = HostBox.Text ?? "localhost";
        var port = int.TryParse(PortBox.Text, out var p) ? p : 6667;
        var count = int.TryParse(CountBox.Text, out var c) ? c : 10;
        var prefix = NickPrefixBox.Text ?? "TestUser";
        var channel = ChannelBox.Text ?? "%#Test";
        var sendPriv = (this.FindControl<CheckBox>("SendPrivmsgBox")?.IsChecked) ?? false;
        var intervalTag = (this.FindControl<ComboBox>("IntervalBox")?.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var interval = int.TryParse(intervalTag, out var iv) ? iv : 1000;

        // periodic behavior is controlled by whether SendPrivmsg is enabled
        var periodic = sendPriv;
        await _manager.StartAsync(host, port, count, prefix, channel, periodic, sendPriv, interval);
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        StartButton.IsEnabled = true;
        await _manager.StopAsync();
        _connectionItems.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { Console.WriteLine("OnClosed: stopping timers"); } catch { }
        try { _refreshTimer?.Dispose(); } catch { }
        try { _dispatcherTimer?.Stop(); } catch { }
        try { Console.WriteLine("OnClosed: attempting graceful StopAsync then kill if needed"); } catch { }

        // Run StopAsync on a background thread and wait a short timeout; if it doesn't finish,
        // fall back to ForceStopNow and kill the process to ensure we don't hang.
        try
        {
            Task.Run(() =>
            {
                try
                {
                    try { Console.WriteLine("OnClosed: calling StopAsync"); } catch { }
                    var stopTask = _manager.StopAsync();
                    if (stopTask == null)
                    {
                        try { Console.WriteLine("OnClosed: StopAsync returned null"); } catch { }
                    }
                    else
                    {
                        // Wait up to 1500ms for graceful shutdown
                        if (!stopTask.Wait(1500))
                        {
                            try { Console.WriteLine("OnClosed: StopAsync timeout, forcing stop"); } catch { }
                            try { _manager.ForceStopNow(); } catch { }
                        }
                        else
                        {
                            try { Console.WriteLine("OnClosed: StopAsync completed"); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    try { Console.WriteLine($"OnClosed: StopAsync error: {ex.Message}"); } catch { }
                    try { _manager.ForceStopNow(); } catch { }
                }
                finally
                {
                    try { Console.WriteLine("OnClosed: killing process now"); } catch { }
                    try { Process.GetCurrentProcess().Kill(); } catch (Exception ex) { try { Console.WriteLine($"OnClosed: Kill failed: {ex.Message}"); } catch { } }
                }
            });
        }
        catch (Exception ex)
        {
            try { Console.WriteLine($"OnClosed: background stop task failed: {ex.Message}"); } catch { }
            try { _manager.ForceStopNow(); } catch { }
            try { Process.GetCurrentProcess().Kill(); } catch { }
        }
    }

    private async void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ConnectionsList.SelectedItem is string s && int.TryParse(s.Split(':')[0], out var id))
        {
            var raw = RawInput.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                await _manager.SendToAsync(id, raw);
            }
        }
    }
}
