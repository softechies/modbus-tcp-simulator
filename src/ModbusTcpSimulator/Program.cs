using System.Net;
using System.Net.Sockets;
using ModbusTcpSimulator.Cli;
using ModbusTcpSimulator.Data;
using ModbusTcpSimulator.Protocol;
using ModbusTcpSimulator.Server;
using ModbusTcpSimulator.Simulation;

const string Usage = """
    Modbus TCP Simulator

    Usage: modbus-sim [options]

      -c, --config <file>   JSON file with named points and signals (see samples/)
      -p, --port <port>     TCP port, default 502 (or the value in the config file)
      -b, --bind <address>  IP to listen on, default 0.0.0.0
      -v, --verbose         log every request and response as hex
      -h, --help            show this help

    Example: modbus-sim --config samples/tank-farm.json --port 5020
    """;

SimulatorConfig config;
try
{
    config = ParseArgs(args, out var showHelp);
    if (showHelp)
    {
        Console.WriteLine(Usage);
        return 0;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage);
    return 2;
}

var consoleLock = new object();
void Log(string message)
{
    lock (consoleLock) Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
}

var store = new DataStore();
SimulationEngine engine;
try
{
    engine = new SimulationEngine(store, config.Points);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine("Config error: " + ex.Message);
    return 2;
}

var handler = new ModbusRequestHandler(store);
await using var server = new ModbusTcpServer(IPAddress.Parse(config.Bind), config.Port, handler, Log) { Verbose = config.Verbose };

try
{
    server.Start();
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"Could not listen on {config.Bind}:{config.Port}: {ex.Message}");
    if (config.Port < 1024) Console.Error.WriteLine("Ports below 1024 may need admin/root rights. Try --port 5020.");
    return 1;
}

engine.Start(TimeSpan.FromMilliseconds(Math.Max(20, config.TickMilliseconds)));
await using var engineScope = engine;

Log($"Modbus TCP simulator listening on {config.Bind}:{server.Port} with {config.Points.Count} point(s)");

var commands = new CommandProcessor(store, engine, server);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

if (Console.IsInputRedirected)
{
    // Running in Docker or as a background process: no interactive console.
    Log("Press Ctrl+C to stop");
    try { await Task.Delay(Timeout.Infinite, shutdown.Token); } catch (OperationCanceledException) { }
}
else
{
    Console.WriteLine("Type 'help' for commands.");
    _ = Task.Run(() =>
    {
        while (!shutdown.IsCancellationRequested)
        {
            var line = Console.ReadLine();
            if (line is null) break; // stdin closed
            var output = commands.Execute(line);
            if (output.Length > 0) lock (consoleLock) Console.WriteLine(output);
            if (commands.QuitRequested) break;
        }
        shutdown.Cancel();
    });
    try { await Task.Delay(Timeout.Infinite, shutdown.Token); } catch (OperationCanceledException) { }
}

Log("Stopped");
return 0;

static SimulatorConfig ParseArgs(string[] args, out bool showHelp)
{
    showHelp = false;
    string? configPath = null;
    int? port = null;
    string? bind = null;
    var verbose = false;

    for (var i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");

        switch (args[i])
        {
            case "-c" or "--config": configPath = Next(); break;
            case "-p" or "--port": port = int.Parse(Next()); break;
            case "-b" or "--bind": bind = Next(); break;
            case "-v" or "--verbose": verbose = true; break;
            case "-h" or "--help" or "/?": showHelp = true; break;
            default: throw new ArgumentException($"Unknown option {args[i]}");
        }
    }

    var config = configPath is null ? new SimulatorConfig() : SimulatorConfig.Load(configPath);
    if (port is not null) config.Port = port.Value;
    if (bind is not null) config.Bind = bind;
    if (verbose) config.Verbose = true;

    if (config.Port is < 1 or > 65535) throw new ArgumentException("Port must be 1..65535");
    return config;
}
