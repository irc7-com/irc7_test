# irc7 Load Tester

Quick load tester that opens many concurrent TCP IRC connections and exercises NICK/USER/JOIN plus optional periodic PRIVMSGs.

**Prerequisites**
- .NET 10 (preview) SDK installed
- Avalonia (installed via NuGet in the project)

**Build**
```bash
dotnet build irc7_test.sln
```

**Run**
```bash
dotnet run --project Irc7.LoadTester
```

**What this build does (current state)**
- Status-only UI: Start/Stop controls and a status bar (totals, messages, bytes, errors).
- Per-connection list and output pane were removed to improve responsiveness under heavy load.
- Per-connection file logging remains available: toggle `Log to file` in the Settings menu; logs are written to `logs/conn_<id>.log` next to the binary.

**Controls**
- `Host`, `Port`, `Connections`, `Nick Prefix`, `Channel`: configure connections.
- `Send PRIVMSG` (checkbox / menu): enable periodic PRIVMSGs from each connection.
- `Start` / `Stop`: run/stop the test.

**Notes & tuning**
- In-memory per-connection log size is capped (see `IrcConnection.MaxLogLines`).
- UI polling and throttling parameters live in `Irc7.LoadTester/MainWindow.axaml.cs` (`_maxStatusPerTick`, `_uiPollIntervalMs`) and can be adjusted to trade freshness vs CPU.
- `IrcConnectionManager` debounces frequent summary updates to reduce UI pressure.

**Troubleshooting**
- If `dotnet build` fails with an Avalonia PDB lock, a previous `dotnet run` may be holding the file. Find and kill that process:
```bash
lsof path/to/obj/.../Avalonia/original.pdb
ps -p <PID>
kill <PID>
```
- For large runs (hundreds/thousands of connections) monitor CPU and file descriptors — the OS may limit concurrent sockets.

If you want the per-connection UI back (or a lightweight inspector), I can add a tiny query box that dumps a connection's recent lines on demand.
# Irc7 Load Tester (Avalonia, .NET 10)

This workspace contains an initial skeleton for an Avalonia-based GUI load tester for IRC servers.

Quick start (requires .NET 10 SDK):

```bash
dotnet build src/Irc7.LoadTester/Irc7.LoadTester.csproj
dotnet run --project src/Irc7.LoadTester/Irc7.LoadTester.csproj
```

Current features:
- Core TCP-based `IrcConnection` and `IrcConnectionManager` that perform `NICK`/`USER` and `JOIN` and can send periodic `PRIVMSG`.
- Simple Avalonia UI skeleton to start/stop tests, list connections, view per-connection logs, and send raw commands.

Next steps: polish UI bindings, add configuration persistence, throttling controls, and additional safety checks.
