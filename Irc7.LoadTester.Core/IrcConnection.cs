using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Irc7.LoadTester.Core
{
    public class IrcConnection : IDisposable
    {
        private readonly int _id;
        private readonly string _nick;
        private readonly string _user;
        private readonly string _channel;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;
        private readonly ConcurrentQueue<string> _log = new();
        private const int MaxLogLines = 100;
        private bool _fileLoggingEnabled = false;
        private System.IO.StreamWriter? _fileWriter;
        private readonly object _fileLock = new();

        public event Action<int, string>? LineReceived;
        public event Action<int, string>? StatusChanged;
        public event Action<int, Exception>? Error;
        public event Action<int, long>? BytesReceived;
        public event Action<int, long>? BytesSent;
        public event Action<int, string>? SentLine;

        public IrcConnection(int id, string nick, string user, string channel)
        {
            _id = id;
            _nick = nick;
            _user = user;
            _channel = channel;
        }

        public void SetFileLoggingEnabled(bool enabled)
        {
            lock (_fileLock)
            {
                if (enabled == _fileLoggingEnabled) return;
                _fileLoggingEnabled = enabled;
                try
                {
                    if (enabled)
                    {
                        // Place logs in a `logs` folder beside the binary
                        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
                        try { System.IO.Directory.CreateDirectory(dir); } catch { }
                        var path = System.IO.Path.Combine(dir, $"conn_{_id}.log");
                        _fileWriter = new System.IO.StreamWriter(System.IO.File.Open(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read), Encoding.ASCII) { AutoFlush = true };
                        _fileWriter.WriteLine($"--- Log started {DateTime.UtcNow:O} ---");
                    }
                    else
                    {
                        try { _fileWriter?.WriteLine($"--- Log stopped {DateTime.UtcNow:O} ---"); } catch { }
                        try { _fileWriter?.Dispose(); } catch { }
                        _fileWriter = null;
                    }
                }
                catch { }
            }
        }

        public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                StatusChanged?.Invoke(_id, "Connecting");
                _client = new TcpClient();
                await _client.ConnectAsync(host, port, _cts.Token).ConfigureAwait(false);
                _stream = _client.GetStream();

                StatusChanged?.Invoke(_id, "Connected");

                // Send NICK/USER
                await SendRawAsync($"NICK {_nick}", _cts.Token).ConfigureAwait(false);
                await SendRawAsync($"USER {_user} 0 * :{_user}", _cts.Token).ConfigureAwait(false);

                // Start receiver and keep a reference to the task so callers can observe its completion
                _receiveTask = Task.Run(ReceiveLoopAsync, _cts.Token);

                // Join channel
                await Task.Delay(250, _cts.Token).ConfigureAwait(false);
                await SendRawAsync($"JOIN {_channel}", _cts.Token).ConfigureAwait(false);
                StatusChanged?.Invoke(_id, "Joined");
            }
            catch (Exception ex)
            {
                Error?.Invoke(_id, ex);
                StatusChanged?.Invoke(_id, "Error");
                Dispose();
            }
        }

        private async Task ReceiveLoopAsync()
        {
            if (_stream == null) return;
            var buffer = new byte[4096];
            var sb = new StringBuilder();
            try
            {
                while (!_cts!.IsCancellationRequested)
                {
                    int read = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    try { BytesReceived?.Invoke(_id, read); } catch { }
                    var s = Encoding.ASCII.GetString(buffer, 0, read);
                    sb.Append(s);
                    string accum = sb.ToString();
                    int idx;
                    while ((idx = accum.IndexOf("\r\n", StringComparison.Ordinal)) >= 0)
                    {
                        var line = accum[..idx];
                        // Reply to server PINGs immediately to keep connection alive
                        try
                        {
                            if (line.StartsWith("PING ", StringComparison.Ordinal) || line == "PING" || line.StartsWith("PING:", StringComparison.Ordinal))
                            {
                                // extract payload (may include leading ':')
                                var payload = line.Length > 4 ? line.Substring(5) : string.Empty;
                                var pong = string.IsNullOrEmpty(payload) ? "PONG" : $"PONG {payload}";
                                try { await SendRawAsync(pong, _cts!.Token).ConfigureAwait(false); } catch { }
                            }
                        }
                        catch { }

                        LineReceived?.Invoke(_id, line);
                        _log.Enqueue(line);
                        while (_log.Count > MaxLogLines)
                        {
                            _log.TryDequeue(out _);
                        }
                        // write to per-connection log file if enabled
                        try
                        {
                            lock (_fileLock)
                            {
                                if (_fileLoggingEnabled && _fileWriter != null)
                                {
                                    try { _fileWriter.WriteLine($"[{DateTime.UtcNow:O}] RECV: {line}"); } catch { }
                                }
                            }
                        }
                        catch { }
                        accum = accum[(idx + 2)..];
                    }
                    sb.Clear();
                    sb.Append(accum);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Error?.Invoke(_id, ex);
            }
            finally
            {
                try { /* verbose logging via manager only */ } catch { }
                StatusChanged?.Invoke(_id, "Closed");
                Dispose();
            }
        }

        public async Task SendRawAsync(string command, CancellationToken cancellationToken = default)
        {
            if (_stream == null) throw new InvalidOperationException("Not connected");
            if (!command.EndsWith("\r\n")) command += "\r\n";
            var bytes = Encoding.ASCII.GetBytes(command);
            await _stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken).ConfigureAwait(false);
            try { BytesSent?.Invoke(_id, bytes.Length); } catch { }
            try { SentLine?.Invoke(_id, command.TrimEnd('\r','\n')); } catch { }
            // write sent data to per-connection log if enabled
            try
            {
                lock (_fileLock)
                {
                    if (_fileLoggingEnabled && _fileWriter != null)
                    {
                        try { _fileWriter.WriteLine($"[{DateTime.UtcNow:O}] SEND: {command.TrimEnd('\r','\n')}"); } catch { }
                    }
                }
            }
            catch { }
        }

        public string[] GetRecentLog(int max = 500)
        {
            return _log.ToArray();
        }

        public void Dispose()
        {
            try
            {
                try { /* verbose logging via manager only */ } catch { }
                _cts?.Cancel();
            }
            catch { }
            try { _stream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }

            // Wait briefly for receive loop to finish to avoid lingering blocking reads
            try
            {
                if (_receiveTask != null && !_receiveTask.IsCompleted)
                {
                    try { /* verbose logging via manager only */ } catch { }
                    _receiveTask.Wait(500);
                    try { /* verbose logging via manager only */ } catch { }
                }
            }
            catch { }

            // close file writer if open
            try
            {
                lock (_fileLock)
                {
                    try { _fileWriter?.Dispose(); } catch { }
                    _fileWriter = null;
                }
            }
            catch { }
        }
    }
}
