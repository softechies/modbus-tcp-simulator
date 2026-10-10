using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModbusTcpSimulator.Simulation;

public sealed class SimulatorConfig
{
    public string Bind { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 502;
    public int TickMilliseconds { get; set; } = 200;
    public bool Verbose { get; set; }
    public List<PointConfig> Points { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new TableJsonConverter(), new JsonStringEnumConverter() }
    };

    public static SimulatorConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SimulatorConfig>(json, JsonOptions)
               ?? throw new InvalidOperationException($"{path} is empty");
    }
}

/// <summary>Lets config files use short table names: "hr", "ir", "coil", "di".</summary>
public sealed class TableJsonConverter : JsonConverter<Data.Table>
{
    public override Data.Table Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? "";
        return Data.TableExtensions.TryParse(text, out var table) || Enum.TryParse(text, true, out table)
            ? table
            : throw new JsonException($"Unknown table '{text}'. Use coil, di, hr or ir.");
    }

    public override void Write(Utf8JsonWriter writer, Data.Table value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Data.TableExtensions.ShortName(value));
}
