using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Irc7.LoadTester.Core
{
    public class IrcConnectionManager : IDisposable
    {
        // Coalesce frequent SummaryChanged notifications to avoid flooding the UI.
        private volatile bool _summaryPending = false;
        private System.Threading.Timer? _summaryTimer;
        private int _summaryIntervalMs = 1000;
        private readonly ConcurrentDictionary<int, IrcConnection> _connections = new();
        private readonly ConcurrentDictionary<int, string> _statuses = new();
        private long _totalMessagesReceived;
        private long _totalMessagesSent;
        private long _totalBytesReceived;
        private long _totalBytesSent;
        private long _totalErrors;
        private readonly ConcurrentBag<Task> _connectTasks = new();
        private readonly ConcurrentDictionary<int, System.Threading.Timer> _periodicTimers = new();
        private volatile bool _fileLoggingEnabled = false;
        private CancellationTokenSource? _cts;
        private volatile bool _sendPrivmsgEnabled = true;

        public event Action<int, string>? LineReceived;
        public event Action<int, string>? StatusChanged;
        public event Action<int, Exception>? Error;
        public event Action? SummaryChanged;

        public IReadOnlyCollection<int> ConnectionIds => _connections.Keys.ToList().AsReadOnly();

        public async Task StartAsync(string host, int port, int count, string nickPrefix, string channel, bool periodic, bool sendPrivmsg, int intervalMs = 1000, CancellationToken? token = null)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token ?? CancellationToken.None);
            _sendPrivmsgEnabled = sendPrivmsg;
            // file logging flag carried forward
            var fileLogging = _fileLoggingEnabled;
            // Reset cumulative counters and clear previous state so the summary shows aggregates
            try
            {
                System.Threading.Interlocked.Exchange(ref _totalMessagesReceived, 0);
                System.Threading.Interlocked.Exchange(ref _totalMessagesSent, 0);
                System.Threading.Interlocked.Exchange(ref _totalErrors, 0);
                _statuses.Clear();
                foreach (var kv in _periodicTimers)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
                _periodicTimers.Clear();
                _connections.Clear();
                while (_connectTasks.TryTake(out _)) { }
            }
            catch { }
            // mark a summary pending; the summary timer will coalesce and raise the event
            _summaryPending = true;
            var ct = _cts.Token;

            // start (or restart) the summary coalescing timer
            try
            {
                _summaryTimer?.Dispose();
                _summaryTimer = new System.Threading.Timer(state =>
                {
                    try
                    {
                        if (_summaryPending)
                        {
                            _summaryPending = false;
                            SummaryChanged?.Invoke();
                        }
                    }
                    catch { }
                }, null, _summaryIntervalMs, _summaryIntervalMs);
            }
            catch { }

            for (int i = 1; i <= count; i++)
            {
                var nick = $"{nickPrefix}{i:D3}";
                var user = nick;
                var conn = new IrcConnection(i, nick, user, channel);
                // configure per-connection file logging if enabled
                try { conn.SetFileLoggingEnabled(fileLogging); } catch { }
                conn.LineReceived += (id, line) =>
                {
                    System.Threading.Interlocked.Increment(ref _totalMessagesReceived);
                    LineReceived?.Invoke(id, line);
                    _summaryPending = true;
                };
                conn.BytesReceived += (id, bytes) =>
                {
                    System.Threading.Interlocked.Add(ref _totalBytesReceived, bytes);
                    _summaryPending = true;
                };
                conn.BytesSent += (id, bytes) =>
                {
                    System.Threading.Interlocked.Add(ref _totalBytesSent, bytes);
                    _summaryPending = true;
                };
                conn.StatusChanged += (id, s) =>
                {
                    _statuses[id] = s;
                    StatusChanged?.Invoke(id, s);
                    _summaryPending = true;
                };
                conn.Error += (id, ex) =>
                {
                    System.Threading.Interlocked.Increment(ref _totalErrors);
                    Error?.Invoke(id, ex);
                    _summaryPending = true;
                };
                _connections[i] = conn;
                var connectTask = Task.Run(() => conn.ConnectAsync(host, port, ct), ct);
                _connectTasks.Add(connectTask);

                if (periodic)
                {
                    // Use a Timer per-connection for periodic sends to avoid long-running task lifecycle issues.
                    var timer = new System.Threading.Timer(state =>
                    {
                        var id = (int)state!;
                        Task.Run(async () =>
                        {
                            try
                            {
                                if (_cts == null || _cts.IsCancellationRequested) return;
                                if (!_sendPrivmsgEnabled) return;
                                if (_connections.TryGetValue(id, out var thisConn))
                                {
                                    try
                                    {
                                        var randPayload = $"{Guid.NewGuid().ToString("N").Substring(0,8)}-{DateTime.UtcNow:HHmmssfff}";
                                        await thisConn.SendRawAsync($"PRIVMSG {channel} :hello from {nick} {randPayload}", _cts.Token).ConfigureAwait(false);
                                        System.Threading.Interlocked.Increment(ref _totalMessagesSent);
                                        SummaryChanged?.Invoke();
                                    }
                                    catch (OperationCanceledException) { }
                                    catch { /* swallow send errors to keep timer running */ }
                                }
                            }
                            catch { }
                        });
                    }, i, intervalMs, intervalMs);
                    _periodicTimers[i] = timer;
                }

                // Small ramp-up
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        public async Task StopAsync()
        {
            try { Console.WriteLine("StopAsync: begin"); } catch { }
            try
            {
                _cts?.Cancel();
                try { Console.WriteLine("StopAsync: cancellation requested"); } catch { }
            }
            catch (Exception ex) { try { Console.WriteLine($"StopAsync: cancel error: {ex.Message}"); } catch { } }

            // Give brief moment for loops to observe cancellation
            await Task.Delay(100).ConfigureAwait(false);

            // Dispose any periodic timers first, then connections
            try
            {
                foreach (var kv in _periodicTimers)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
            }
            catch { }

            // Aggressively dispose connections
            try
            {
                foreach (var kv in _connections)
                {
                    try
                    {
                        kv.Value.Dispose();
                        try { Console.WriteLine($"StopAsync: disposed conn {kv.Key}"); } catch { }
                    }
                    catch (Exception ex) { try { Console.WriteLine($"StopAsync: dispose error conn {kv.Key}: {ex.Message}"); } catch { } }
                }
            }
            catch (Exception ex) { try { Console.WriteLine($"StopAsync: dispose loop error: {ex.Message}"); } catch { } }

            _connections.Clear();
            _statuses.Clear();
            System.Threading.Interlocked.Exchange(ref _totalMessagesReceived, 0);
            System.Threading.Interlocked.Exchange(ref _totalMessagesSent, 0);
            System.Threading.Interlocked.Exchange(ref _totalErrors, 0);

            // Wait briefly for connect tasks to finish and log status
            try
            {
                var all = _connectTasks.ToArray();
                if (all.Length > 0)
                {
                    var waitTask = Task.WhenAll(all);
                    var completed = await Task.WhenAny(waitTask, Task.Delay(3000)).ConfigureAwait(false);
                    // ignore detailed logging in release
                }
            }
            catch (Exception ex) { try { Console.WriteLine($"StopAsync: wait error: {ex.Message}"); } catch { } }

            // Clear tracked task bags for next run
            try
            {
                while (_connectTasks.TryTake(out _)) { }
                foreach (var kv in _periodicTimers)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
                _periodicTimers.Clear();
            }
            catch { }

            try { Console.WriteLine("StopAsync: end"); } catch { }

            SummaryChanged?.Invoke();
        }

        public Task SendToAsync(int id, string raw)
        {
            if (_connections.TryGetValue(id, out var conn))
            {
                System.Threading.Interlocked.Increment(ref _totalMessagesSent);
                SummaryChanged?.Invoke();
                return conn.SendRawAsync(raw, CancellationToken.None);
            }
            return Task.CompletedTask;
        }

        public string[] GetLog(int id)
        {
            if (_connections.TryGetValue(id, out var conn)) return conn.GetRecentLog();
            return Array.Empty<string>();
        }

        // Snapshot of current per-connection statuses
        public System.Collections.Generic.Dictionary<int, string> GetAllStatuses()
        {
            try
            {
                return _statuses.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            catch
            {
                return new System.Collections.Generic.Dictionary<int, string>();
            }
        }

        public string GetSummary()
        {
            var total = _connections.Count;
            var connected = _statuses.Values.Count(v => v == "Connected" || v == "Joined");
            var joined = _statuses.Values.Count(v => v == "Joined");
            var errors = System.Threading.Interlocked.Read(ref _totalErrors);
            var recv = System.Threading.Interlocked.Read(ref _totalMessagesReceived);
            var sent = System.Threading.Interlocked.Read(ref _totalMessagesSent);
            var bytesRecv = System.Threading.Interlocked.Read(ref _totalBytesReceived);
            var bytesSent = System.Threading.Interlocked.Read(ref _totalBytesSent);
            return $"Summary — Total: {total} | Connected: {connected} | Joined: {joined} | Errors: {errors} | Msg recv: {recv} | Msg sent: {sent} | Bytes recv: {bytesRecv} | Bytes sent: {bytesSent}";
        }

        // Return numeric counters for UI consumption
        public (int Total, int Connected, int Joined, long Errors, long Recv, long Sent, long BytesRecv, long BytesSent) GetCounters()
        {
            var total = _connections.Count;
            var connected = _statuses.Values.Count(v => v == "Connected" || v == "Joined");
            var joined = _statuses.Values.Count(v => v == "Joined");
            var errors = System.Threading.Interlocked.Read(ref _totalErrors);
            var recv = System.Threading.Interlocked.Read(ref _totalMessagesReceived);
            var sent = System.Threading.Interlocked.Read(ref _totalMessagesSent);
            var bytesRecv = System.Threading.Interlocked.Read(ref _totalBytesReceived);
            var bytesSent = System.Threading.Interlocked.Read(ref _totalBytesSent);
            return (total, connected, joined, errors, recv, sent, bytesRecv, bytesSent);
        }

        public void Dispose()
        {
            // Do not block on shutdown from Dispose to avoid UI-thread deadlocks.
            try
            {
                _ = StopAsync();
            }
            catch { }
        }

        // Toggle whether periodic PRIVMSG sends are enabled. Timers check this flag before sending.
        public void SetSendPrivmsgEnabled(bool enabled)
        {
            _sendPrivmsgEnabled = enabled;
        }

        public bool GetSendPrivmsgEnabled() => _sendPrivmsgEnabled;

        // Toggle whether per-connection file logging is enabled. Applies to existing and future connections.
        public void SetFileLoggingEnabled(bool enabled)
        {
            _fileLoggingEnabled = enabled;
            try
            {
                foreach (var kv in _connections)
                {
                    try { kv.Value.SetFileLoggingEnabled(enabled); } catch { }
                }
            }
            catch { }
        }

        public bool GetFileLoggingEnabled() => _fileLoggingEnabled;

        // Force immediate synchronous stop/cleanup. Use when application is shutting down
        // and we need to avoid awaiting background tasks.
        public void ForceStopNow()
        {
            try { _cts?.Cancel(); } catch { }
            try
            {
                foreach (var kv in _periodicTimers)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
            }
            catch { }
            try
            {
                foreach (var kv in _connections)
                {
                    try { kv.Value.Dispose(); } catch { }
                }
            }
            catch { }
            try { while (_connectTasks.TryTake(out _)) { } } catch { }
        }
    }
}
