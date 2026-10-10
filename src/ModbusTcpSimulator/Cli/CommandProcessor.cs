using System.Globalization;
using System.Text;
using ModbusTcpSimulator.Data;
using ModbusTcpSimulator.Server;
using ModbusTcpSimulator.Simulation;

namespace ModbusTcpSimulator.Cli;

/// <summary>Interactive console commands. Returns text instead of printing so it can be tested.</summary>
public sealed class CommandProcessor(DataStore store, SimulationEngine engine, ModbusTcpServer? server = null)
{
    public const string HelpText = """
        Commands
          get <table> <address> [count]        read raw values           e.g. get hr 0 10
          set <table> <address> <values...>    write raw values          e.g. set hr 5 1234 -1 / set coil 3 on off
          points                               list named points with decoded values
          setp <point> <value>                 set a point (pauses its signal)    e.g. setp Temperature 21.5
          pause <point> | resume <point>       stop or restart a point's signal
          sim on | sim off                     pause or resume all signals
          verbose on | verbose off             log every request as hex
          stats                                connected clients and request count
          help                                 this text
          quit                                 stop the simulator

        Tables: coil, di (discrete input), hr (holding register), ir (input register)
        Addresses are zero based (hr 0 is register 40001 in 1 based tools).
        """;

    public bool QuitRequested { get; private set; }

    public string Execute(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "";

        try
        {
            return parts[0].ToLowerInvariant() switch
            {
                "help" or "?" => HelpText,
                "get" or "read" => Get(parts),
                "set" or "write" => Set(parts),
                "points" or "list" or "ls" => Points(),
                "setp" => SetPoint(parts),
                "pause" => PauseResume(parts, pause: true),
                "resume" => PauseResume(parts, pause: false),
                "sim" => Sim(parts),
                "verbose" => Verbose(parts),
                "stats" => Stats(),
                "quit" or "exit" or "q" => Quit(),
                _ => $"Unknown command '{parts[0]}'. Type help."
            };
        }
        catch (FormatException ex)
        {
            return "Error: " + ex.Message;
        }
    }

    private string Get(string[] parts)
    {
        if (parts.Length < 3) return "Usage: get <table> <address> [count]";
        var table = ParseTable(parts[1]);
        var address = ParseInt(parts[2]);
        var count = parts.Length > 3 ? ParseInt(parts[3]) : 1;
        if (count < 1 || count > 1000) return "Count must be 1..1000";
        if (!DataStore.InRange(address, count)) return "Address out of range";

        var sb = new StringBuilder();
        if (table.IsBitTable())
        {
            var bits = store.ReadBits(table, address, count);
            for (var i = 0; i < bits.Length; i++)
                sb.AppendLine($"{table.ShortName()}[{address + i}] = {(bits[i] ? "ON" : "off")}");
        }
        else
        {
            var regs = store.ReadRegisters(table, address, count);
            for (var i = 0; i < regs.Length; i++)
                sb.AppendLine($"{table.ShortName()}[{address + i}] = {regs[i],5}  (int16 {unchecked((short)regs[i]),6}, 0x{regs[i]:X4})");
        }
        return sb.ToString().TrimEnd();
    }

    private string Set(string[] parts)
    {
        if (parts.Length < 4) return "Usage: set <table> <address> <values...>";
        var table = ParseTable(parts[1]);
        var address = ParseInt(parts[2]);
        var values = parts[3..];
        if (!DataStore.InRange(address, values.Length)) return "Address out of range";

        if (table.IsBitTable())
        {
            store.WriteBits(table, address, values.Select(ParseBool).ToArray());
        }
        else
        {
            store.WriteRegisters(table, address, values.Select(ParseRegister).ToArray());
        }
        return $"Wrote {values.Length} value(s) to {table.ShortName()}[{address}]";
    }

    private string Points()
    {
        if (engine.Points.Count == 0) return "No points configured. Pass a config file with --config.";

        var sb = new StringBuilder();
        sb.AppendLine($"{"Name",-22} {"Table",-5} {"Addr",5} {"Type",-8} {"Value",14}  Signal");
        foreach (var p in engine.Points)
        {
            var value = PointCodec.Read(store, p);
            var shown = p.DataType == DataType.Bool ? (value != 0 ? "ON" : "off") : value.ToString("0.###", CultureInfo.InvariantCulture);
            var signal = p.Signal is null ? "" : p.Signal.Type + (engine.IsPaused(p.Name) ? " (paused)" : "");
            sb.AppendLine($"{Truncate(p.Name, 22),-22} {p.Table.ShortName(),-5} {p.Address,5} {p.DataType,-8} {shown,14}  {signal}");
        }
        if (!engine.Enabled) sb.AppendLine("All signals paused (sim off)");
        return sb.ToString().TrimEnd();
    }

    private string SetPoint(string[] parts)
    {
        if (parts.Length < 3) return "Usage: setp <point> <value>";
        var name = string.Join(' ', parts[1..^1]); // allow names with spaces
        var point = engine.Find(name);
        if (point is null) return $"No point named '{name}'";

        var value = point.DataType == DataType.Bool
            ? (ParseBool(parts[^1]) ? 1 : 0)
            : double.Parse(parts[^1], CultureInfo.InvariantCulture);

        if (point.Signal is not null) engine.Pause(point.Name);
        PointCodec.Write(store, point, value);
        return point.Signal is null
            ? $"{point.Name} = {value}"
            : $"{point.Name} = {value} (signal paused, 'resume {point.Name}' to restart it)";
    }

    private string PauseResume(string[] parts, bool pause)
    {
        if (parts.Length < 2) return $"Usage: {(pause ? "pause" : "resume")} <point>";
        var name = string.Join(' ', parts[1..]);
        var point = engine.Find(name);
        if (point is null) return $"No point named '{name}'";
        if (pause) engine.Pause(point.Name); else engine.Resume(point.Name);
        return $"{point.Name} {(pause ? "paused" : "resumed")}";
    }

    private string Sim(string[] parts)
    {
        if (parts.Length < 2) return $"Simulation is {(engine.Enabled ? "on" : "off")}";
        engine.Enabled = ParseBool(parts[1]);
        return $"Simulation {(engine.Enabled ? "on" : "off")}";
    }

    private string Verbose(string[] parts)
    {
        if (server is null) return "Server not running";
        if (parts.Length > 1) server.Verbose = ParseBool(parts[1]);
        return $"Verbose logging {(server.Verbose ? "on" : "off")}";
    }

    private string Stats()
    {
        if (server is null) return "Server not running";
        var sb = new StringBuilder();
        sb.AppendLine($"Listening on port {server.Port}");
        sb.AppendLine($"Requests served: {server.RequestCount}");
        sb.AppendLine($"Clients: {server.ClientCount}");
        foreach (var endpoint in server.ClientEndpoints) sb.AppendLine("  " + endpoint);
        return sb.ToString().TrimEnd();
    }

    private string Quit()
    {
        QuitRequested = true;
        return "Stopping...";
    }

    // Parsing

    private static Table ParseTable(string text) =>
        TableExtensions.TryParse(text, out var table) ? table : throw new FormatException($"Unknown table '{text}'. Use coil, di, hr or ir.");

    private static int ParseInt(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new FormatException($"'{text}' is not a number");

    internal static bool ParseBool(string text) => text.ToLowerInvariant() switch
    {
        "1" or "on" or "true" or "yes" => true,
        "0" or "off" or "false" or "no" => false,
        _ => throw new FormatException($"'{text}' is not on/off")
    };

    /// <summary>Accepts 0..65535, negative int16 values, and hex like 0x1F.</summary>
    internal static ushort ParseRegister(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value is >= short.MinValue and <= ushort.MaxValue)
            return unchecked((ushort)value);

        throw new FormatException($"'{text}' is not a valid register value (-32768..65535 or 0x0000..0xFFFF)");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
