using ModbusTcpSimulator.Data;
using ModbusTcpSimulator.Simulation;
using Xunit;

namespace ModbusTcpSimulator.Tests;

public class PointAndSignalTests
{
    [Fact]
    public void Float32_word_orders()
    {
        // 1.0f = 0x3F800000
        Assert.Equal(new ushort[] { 0x3F80, 0x0000 }, PointCodec.Encode(1.0, DataType.Float32, WordOrder.ABCD));
        Assert.Equal(new ushort[] { 0x0000, 0x3F80 }, PointCodec.Encode(1.0, DataType.Float32, WordOrder.CDAB));
        Assert.Equal(1.0, PointCodec.Decode([0x0000, 0x3F80], DataType.Float32, WordOrder.CDAB));
    }

    [Theory]
    [InlineData(-1.0, (int)DataType.Int16, 0xFFFF)]
    [InlineData(70000.0, (int)DataType.UInt16, 0xFFFF)] // clamped
    [InlineData(-5.0, (int)DataType.UInt16, 0)]         // clamped
    [InlineData(1234.4, (int)DataType.UInt16, 1234)]    // rounded
    public void Sixteen_bit_encoding(double value, int type, int expected)
    {
        Assert.Equal((ushort)expected, PointCodec.Encode(value, (DataType)type, WordOrder.ABCD)[0]);
    }

    [Fact]
    public void Int32_round_trip()
    {
        var raw = PointCodec.Encode(-123456, DataType.Int32, WordOrder.ABCD);
        Assert.Equal(-123456, PointCodec.Decode(raw, DataType.Int32, WordOrder.ABCD));
    }

    [Fact]
    public void Scale_stores_tenths()
    {
        var store = new DataStore();
        var point = new PointConfig { Name = "T", Table = Table.InputRegisters, Address = 3, DataType = DataType.Int16, Scale = 10 };

        PointCodec.Write(store, point, -12.3);

        Assert.Equal(unchecked((ushort)(short)-123), store.ReadRegisters(Table.InputRegisters, 3, 1)[0]);
        Assert.Equal(-12.3, PointCodec.Read(store, point), 6);
    }

    [Fact]
    public void Validation_rejects_bool_in_register_table()
    {
        var point = new PointConfig { Name = "X", Table = Table.HoldingRegisters, DataType = DataType.Bool };
        Assert.Throws<InvalidOperationException>(point.Validate);
    }

    [Fact]
    public void Validation_rejects_32bit_value_at_last_address()
    {
        var point = new PointConfig { Name = "X", Table = Table.HoldingRegisters, Address = 65535, DataType = DataType.Float32 };
        Assert.Throws<InvalidOperationException>(point.Validate);
    }

    [Fact]
    public void Ramp_sine_square_shapes()
    {
        var rng = new Random(1);
        var ramp = new SignalSettings { Type = SignalType.Ramp, Min = 0, Max = 100, PeriodSeconds = 10 };
        Assert.Equal(50, SignalGenerator.Evaluate(ramp, 5, 0, rng), 6);
        Assert.Equal(0, SignalGenerator.Evaluate(ramp, 10, 0, rng), 6);

        var sine = new SignalSettings { Type = SignalType.Sine, Min = 0, Max = 100, PeriodSeconds = 4 };
        Assert.Equal(50, SignalGenerator.Evaluate(sine, 0, 0, rng), 6);
        Assert.Equal(100, SignalGenerator.Evaluate(sine, 1, 0, rng), 6);
        Assert.Equal(0, SignalGenerator.Evaluate(sine, 3, 0, rng), 6);

        var square = new SignalSettings { Type = SignalType.Square, Min = 0, Max = 1, PeriodSeconds = 10 };
        Assert.Equal(1, SignalGenerator.Evaluate(square, 2, 0, rng));
        Assert.Equal(0, SignalGenerator.Evaluate(square, 7, 0, rng));
    }

    [Fact]
    public void Counter_wraps()
    {
        var counter = new SignalSettings { Type = SignalType.Counter, Min = 0, Max = 3, Step = 1, PeriodSeconds = 1 };
        var values = Enumerable.Range(0, 6).Select(t => SignalGenerator.Evaluate(counter, t, 0, new Random())).ToArray();
        Assert.Equal(new double[] { 0, 1, 2, 3, 0, 1 }, values);
    }

    [Fact]
    public void Random_walk_stays_in_bounds()
    {
        var walk = new SignalSettings { Type = SignalType.Random, Min = 10, Max = 20, Step = 5 };
        var rng = new Random(42);
        var value = double.NaN;
        for (var i = 0; i < 1000; i++)
        {
            value = SignalGenerator.Evaluate(walk, i, value, rng);
            Assert.InRange(value, 10, 20);
        }
    }

    [Fact]
    public void Engine_applies_initial_values_and_respects_pause()
    {
        var store = new DataStore();
        var points = new[]
        {
            new PointConfig { Name = "Level", Table = Table.InputRegisters, Address = 0, DataType = DataType.UInt16, Value = 5,
                Signal = new SignalSettings { Type = SignalType.Ramp, Min = 0, Max = 100, PeriodSeconds = 10 } }
        };
        var engine = new SimulationEngine(store, points);

        Assert.Equal(5, store.ReadRegisters(Table.InputRegisters, 0, 1)[0]);

        engine.Tick(5);
        Assert.Equal(50, store.ReadRegisters(Table.InputRegisters, 0, 1)[0]);

        engine.Pause("level");
        engine.Tick(8);
        Assert.Equal(50, store.ReadRegisters(Table.InputRegisters, 0, 1)[0]);
    }

    [Fact]
    public void Engine_rejects_duplicate_names()
    {
        var points = new[]
        {
            new PointConfig { Name = "A", Address = 0 },
            new PointConfig { Name = "a", Address = 1 }
        };
        Assert.Throws<InvalidOperationException>(() => new SimulationEngine(new DataStore(), points));
    }

    [Fact]
    public void Sample_config_loads_and_validates()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "samples", "tank-farm.json");
        var config = SimulatorConfig.Load(path);

        Assert.Equal(5020, config.Port);
        Assert.NotEmpty(config.Points);
        _ = new SimulationEngine(new DataStore(), config.Points); // validates every point
        Assert.Contains(config.Points, p => p.Table == Table.Coils && p.DataType == DataType.Bool);
    }
}
