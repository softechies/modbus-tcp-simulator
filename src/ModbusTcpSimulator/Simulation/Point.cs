using System.Buffers.Binary;
using ModbusTcpSimulator.Data;

namespace ModbusTcpSimulator.Simulation;

public enum DataType
{
    Bool,
    UInt16,
    Int16,
    UInt32,
    Int32,
    Float32
}

/// <summary>
/// Order of the two 16 bit words in a 32 bit value.
/// ABCD = high word first (Modbus default, "big endian").
/// CDAB = low word first ("word swapped"), common on many PLCs.
/// </summary>
public enum WordOrder
{
    ABCD,
    CDAB
}

/// <summary>A named value at a fixed address, optionally driven by a signal.</summary>
public sealed class PointConfig
{
    public string Name { get; set; } = "";
    public Table Table { get; set; } = Table.HoldingRegisters;
    public int Address { get; set; }
    public DataType DataType { get; set; } = DataType.UInt16;
    public WordOrder WordOrder { get; set; } = WordOrder.ABCD;

    /// <summary>Initial value. For bool points any non zero value is true.</summary>
    public double Value { get; set; }

    /// <summary>Optional engineering scale: raw = value * Scale. E.g. 10 to store 23.5 as 235.</summary>
    public double Scale { get; set; } = 1;

    public SignalSettings? Signal { get; set; }

    public int RegisterCount => DataType switch
    {
        DataType.UInt32 or DataType.Int32 or DataType.Float32 => 2,
        _ => 1
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException("Every point needs a name");

        if (Table.IsBitTable() != (DataType == DataType.Bool))
            throw new InvalidOperationException($"Point '{Name}': {DataType} cannot live in {Table}. Use Bool for coils and discrete inputs, numbers for registers.");

        if (!DataStore.InRange(Address, RegisterCount))
            throw new InvalidOperationException($"Point '{Name}': address {Address} out of range");

        if (Scale == 0)
            throw new InvalidOperationException($"Point '{Name}': scale cannot be 0");
    }
}

/// <summary>Converts between engineering values and raw coils/registers.</summary>
public static class PointCodec
{
    public static void Write(DataStore store, PointConfig point, double value)
    {
        if (point.DataType == DataType.Bool)
        {
            store.WriteBits(point.Table, point.Address, [value != 0]);
            return;
        }

        store.WriteRegisters(point.Table, point.Address, Encode(value * point.Scale, point.DataType, point.WordOrder));
    }

    public static double Read(DataStore store, PointConfig point)
    {
        if (point.DataType == DataType.Bool)
        {
            return store.ReadBits(point.Table, point.Address, 1)[0] ? 1 : 0;
        }

        var raw = store.ReadRegisters(point.Table, point.Address, point.RegisterCount);
        return Decode(raw, point.DataType, point.WordOrder) / point.Scale;
    }

    public static ushort[] Encode(double value, DataType type, WordOrder order)
    {
        switch (type)
        {
            case DataType.UInt16:
                return [(ushort)Math.Clamp(Math.Round(value), ushort.MinValue, ushort.MaxValue)];
            case DataType.Int16:
                return [unchecked((ushort)(short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue))];
        }

        uint bits = type switch
        {
            DataType.UInt32 => (uint)Math.Clamp(Math.Round(value), uint.MinValue, uint.MaxValue),
            DataType.Int32 => unchecked((uint)(int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)),
            DataType.Float32 => BitConverter.SingleToUInt32Bits((float)value),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

        var high = (ushort)(bits >> 16);
        var low = (ushort)(bits & 0xFFFF);
        return order == WordOrder.ABCD ? [high, low] : [low, high];
    }

    public static double Decode(ReadOnlySpan<ushort> raw, DataType type, WordOrder order)
    {
        switch (type)
        {
            case DataType.UInt16:
                return raw[0];
            case DataType.Int16:
                return unchecked((short)raw[0]);
        }

        var (high, low) = order == WordOrder.ABCD ? (raw[0], raw[1]) : (raw[1], raw[0]);
        var bits = ((uint)high << 16) | low;

        return type switch
        {
            DataType.UInt32 => bits,
            DataType.Int32 => unchecked((int)bits),
            DataType.Float32 => BitConverter.UInt32BitsToSingle(bits),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }
}
