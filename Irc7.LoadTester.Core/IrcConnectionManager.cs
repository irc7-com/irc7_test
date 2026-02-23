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
        private volatile bool _verboseLoggingEnabled = false;
        private volatile bool _screenLoggingEnabled = false;

        public event Action<int, string>? LineReceived;
        public event Action<int, string>? StatusChanged;
        public event Action<int, Exception>? Error;
        public event Action? SummaryChanged;
        public event Action<string>? LogLine;

        public IReadOnlyCollection<int> ConnectionIds => _connections.Keys.ToList().AsReadOnly();

        public async Task StartAsync(string host, int port, int count, string nickPrefix, string channel, bool periodic, bool sendPrivmsg, int intervalMs = 1000, bool cycleSockets = false, int cycleGraceMs = 5000, CancellationToken? token = null)
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
                var id = i;
                var nick = $"{nickPrefix}{id:D3}";
                var user = nick;

                if (cycleSockets)
                {
                    var cycleTask = Task.Run(async () =>
                    {
                        try
                        {
                            while (!_cts.IsCancellationRequested)
                            {
                                var instanceNick = $"{nickPrefix}{id:D3}-{Guid.NewGuid().ToString("N").Substring(0,6)}";
                                var instanceUser = instanceNick;
                                var conn = new IrcConnection(id, instanceNick, instanceUser, channel);
                                try { conn.SetFileLoggingEnabled(fileLogging); } catch { }
                                conn.LineReceived += (id, line) =>
                                {
                                    System.Threading.Interlocked.Increment(ref _totalMessagesReceived);
                                    LineReceived?.Invoke(id, line);
                                    try { if (_screenLoggingEnabled) LogLine?.Invoke($"[{id}] RECV: {line}"); } catch { }
                                    _summaryPending = true;
                                };
                                conn.BytesReceived += (cid, bytes) =>
                                {
                                    System.Threading.Interlocked.Add(ref _totalBytesReceived, bytes);
                                    _summaryPending = true;
                                };
                                conn.BytesSent += (cid, bytes) =>
                                {
                                    System.Threading.Interlocked.Add(ref _totalBytesSent, bytes);
                                    _summaryPending = true;
                                };
                                conn.StatusChanged += (cid, s) =>
                                {
                                    _statuses[cid] = s;
                                    StatusChanged?.Invoke(cid, s);
                                    _summaryPending = true;
                                };
                                conn.Error += (cid, ex) =>
                                {
                                    System.Threading.Interlocked.Increment(ref _totalErrors);
                                    Error?.Invoke(cid, ex);
                                    _summaryPending = true;
                                };
                                conn.SentLine += (id, line) =>
                                {
                                    try { if (_screenLoggingEnabled) LogLine?.Invoke($"[{id}] SEND: {line}"); } catch { }
                                    _summaryPending = true;
                                };

                                _connections[id] = conn;
                                var connectTaskLocal = Task.Run(() => conn.ConnectAsync(host, port, ct), ct);
                                _connectTasks.Add(connectTaskLocal);

                                try
                                {
                                    await connectTaskLocal.ConfigureAwait(false);
                                                            try { if (_verboseLoggingEnabled) LogVerbose($"Cycle connected {id} as {instanceNick}"); } catch { }
                                }
                                catch (Exception ex)
                                {
                                    try { if (_verboseLoggingEnabled) LogVerbose($"Cycle connect error for {id}: {ex.Message}"); } catch { }
                                }

                                if (_cts == null || _cts.IsCancellationRequested) break;

                                var sentOk = false;
                                // Track last sent time via SentLine to ensure we wait until all send activity
                                // has quiesced before disconnecting. The configured grace period is applied
                                // after the send sequence has finished.
                                var lastSent = DateTime.MinValue;
                                Action<int, string> sentHandler = (cid, line) =>
                                {
                                    try { lastSent = DateTime.UtcNow; } catch { }
                                };
                                try
                                {
                                    conn.SentLine += sentHandler;
                                    try { if (_verboseLoggingEnabled) LogVerbose($"Cycle {id} sending PRIVMSG from {instanceNick}"); } catch { }
                                    await conn.SendRawAsync($"PRIVMSG {channel} :cycle from {instanceNick}", _cts.Token).ConfigureAwait(false);
                                    System.Threading.Interlocked.Increment(ref _totalMessagesSent);
                                    _summaryPending = true;
                                    SummaryChanged?.Invoke();
                                    // mark that we sent at least once
                                    lastSent = DateTime.UtcNow;
                                    sentOk = true;
                                    try { if (_verboseLoggingEnabled) LogVerbose($"Cycle {id} PRIVMSG sent from {instanceNick}"); } catch { }
                                }
                                catch (Exception ex)
                                {
                                    sentOk = false;
                                    try { if (_verboseLoggingEnabled) LogVerbose($"Cycle {id} send error: {ex.Message}"); } catch { }
                                }

                                // If we did send, wait for a short stabilization window (no more SentLine events)
                                if (sentOk)
                                {
                                    try
                                    {
                                        var stabilizeMs = 250;
                                        var watchdogMs = Math.Max(2000, stabilizeMs * 10);
                                        var sw = System.Diagnostics.Stopwatch.StartNew();
                                        while (!_cts.IsCancellationRequested)
                                        {
                                            if (lastSent != DateTime.MinValue)
                                            {
                                                var since = (DateTime.UtcNow - lastSent).TotalMilliseconds;
                                                if (since >= stabilizeMs) break;
                                            }
                                            if (sw.ElapsedMilliseconds > watchdogMs) break;
                                            try { await Task.Delay(50, _cts.Token).ConfigureAwait(false); } catch { break; }
                                        }
                                    }
                                    catch { }
                                }

                                // apply additional user-configured grace period after sends finished
                                var delay = Math.Max(0, cycleGraceMs);
                                if (sentOk && delay > 0)
                                {
                                    try { await Task.Delay(delay, _cts.Token).ConfigureAwait(false); } catch { }
                                }
                                try { if (conn != null) conn.SentLine -= sentHandler; } catch { }

                                try { conn.Dispose(); } catch { }
                                try { if (_verboseLoggingEnabled) LogVerbose($"Cycle {id} disposed {instanceNick}"); } catch { }
                                try { _connections.TryRemove(id, out _); } catch { }
                                try
                                {
                                    if (_periodicTimers.TryRemove(id, out var t))
                                    {
                                        try { t.Dispose(); } catch { }
                                    }
                                }
                                catch { }

                                try { _statuses.TryRemove(id, out _); } catch { }
                                try { StatusChanged?.Invoke(id, "Closed"); } catch { }

                                _summaryPending = true;
                                SummaryChanged?.Invoke();

                                // brief pause before reconnecting
                                try { await Task.Delay(50, _cts.Token).ConfigureAwait(false); } catch { }
                            }
                        }
                        catch { }
                    }, ct);
                    _connectTasks.Add(cycleTask);
                }
                else
                {
                    var conn = new IrcConnection(id, nick, user, channel);
                    // configure per-connection file logging if enabled
                    try { conn.SetFileLoggingEnabled(fileLogging); } catch { }
                    conn.LineReceived += (cid, line) =>
                    {
                        System.Threading.Interlocked.Increment(ref _totalMessagesReceived);
                        LineReceived?.Invoke(cid, line);
                        try { if (_screenLoggingEnabled) LogLine?.Invoke($"[{cid}] RECV: {line}"); } catch { }
                        _summaryPending = true;
                    };
                    conn.BytesReceived += (cid, bytes) =>
                    {
                        System.Threading.Interlocked.Add(ref _totalBytesReceived, bytes);
                        _summaryPending = true;
                    };
                    conn.BytesSent += (cid, bytes) =>
                    {
                        System.Threading.Interlocked.Add(ref _totalBytesSent, bytes);
                        _summaryPending = true;
                    };
                    conn.StatusChanged += (cid, s) =>
                    {
                        _statuses[cid] = s;
                        StatusChanged?.Invoke(cid, s);
                        _summaryPending = true;
                    };
                    conn.Error += (cid, ex) =>
                    {
                        System.Threading.Interlocked.Increment(ref _totalErrors);
                        Error?.Invoke(cid, ex);
                        _summaryPending = true;
                    };
                    // forward per-connection sent lines to screen log when enabled
                    conn.SentLine += (cid, line) =>
                    {
                        try { if (_screenLoggingEnabled) LogLine?.Invoke($"[{cid}] SEND: {line}"); } catch { }
                        _summaryPending = true;
                    };
                    _connections[id] = conn;
                    var connectTask = Task.Run(() => conn.ConnectAsync(host, port, ct), ct);
                    _connectTasks.Add(connectTask);

                    if (periodic)
                    {
                        // Use a Timer per-connection for periodic sends to avoid long-running task lifecycle issues.
                        var timer = new System.Threading.Timer(state =>
                        {
                            var iid = (int)state!;
                            Task.Run(async () =>
                            {
                                try
                                {
                                    if (_cts == null || _cts.IsCancellationRequested) return;
                                    if (!_sendPrivmsgEnabled) return;
                                    if (_connections.TryGetValue(iid, out var thisConn))
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
                        }, id, intervalMs, intervalMs);
                        _periodicTimers[id] = timer;
                    }
                }

                // Small ramp-up
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        public async Task StopAsync()
        {
            try { if (_verboseLoggingEnabled) Console.WriteLine("StopAsync: begin"); } catch { }
            try
            {
                _cts?.Cancel();
                try { if (_verboseLoggingEnabled) Console.WriteLine("StopAsync: cancellation requested"); } catch { }
            }
            catch (Exception ex) { try { if (_verboseLoggingEnabled) Console.WriteLine($"StopAsync: cancel error: {ex.Message}"); } catch { } }

            // Give brief moment for loops to observe cancellation
            await Task.Delay(100).ConfigureAwait(false);

            // Dispose any periodic timers first
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
                        try { if (_verboseLoggingEnabled) Console.WriteLine($"StopAsync: disposed conn {kv.Key}"); } catch { }
                    }
                    catch (Exception ex) { try { if (_verboseLoggingEnabled) Console.WriteLine($"StopAsync: dispose error conn {kv.Key}: {ex.Message}"); } catch { } }
                }
            }
            catch (Exception ex) { try { if (_verboseLoggingEnabled) Console.WriteLine($"StopAsync: dispose loop error: {ex.Message}"); } catch { } }

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
                    if (_verboseLoggingEnabled) Console.WriteLine("StopAsync: wait completed");
                }
            }
            catch (Exception ex) { try { if (_verboseLoggingEnabled) Console.WriteLine($"StopAsync: wait error: {ex.Message}"); } catch { } }

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

            try { if (_verboseLoggingEnabled) Console.WriteLine("StopAsync: end"); } catch { }

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

        // Toggle verbose console logging for debugging
        public void SetVerboseLoggingEnabled(bool enabled)
        {
            _verboseLoggingEnabled = enabled;
            try { LogLine?.Invoke($"[manager] Verbose logging {(enabled ? "enabled" : "disabled")}"); } catch { }
        }

        public void SetScreenLoggingEnabled(bool enabled)
        {
            _screenLoggingEnabled = enabled;
            try { LogLine?.Invoke($"[manager] Screen logging {(enabled ? "enabled" : "disabled")}"); } catch { }
        }

        private void LogVerbose(string message)
        {
            try { Console.WriteLine(message); } catch { }
            try { if (_screenLoggingEnabled) LogLine?.Invoke(message); } catch { }
        }
    }
}
