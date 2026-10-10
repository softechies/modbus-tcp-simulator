namespace ModbusTcpSimulator.Data;

public enum Table
{
    Coils,
    DiscreteInputs,
    HoldingRegisters,
    InputRegisters
}

public static class TableExtensions
{
    public static bool IsBitTable(this Table table) => table is Table.Coils or Table.DiscreteInputs;

    public static string ShortName(this Table table) => table switch
    {
        Table.Coils => "coil",
        Table.DiscreteInputs => "di",
        Table.HoldingRegisters => "hr",
        Table.InputRegisters => "ir",
        _ => table.ToString()
    };

    public static bool TryParse(string text, out Table table)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "c": case "co": case "coil": case "coils": case "0x":
                table = Table.Coils; return true;
            case "di": case "discrete": case "discreteinput": case "discreteinputs": case "1x":
                table = Table.DiscreteInputs; return true;
            case "hr": case "holding": case "holdingregister": case "holdingregisters": case "4x":
                table = Table.HoldingRegisters; return true;
            case "ir": case "input": case "inputregister": case "inputregisters": case "3x":
                table = Table.InputRegisters; return true;
            default:
                table = default; return false;
        }
    }
}

/// <summary>
/// The four Modbus data tables, each with the full 0..65535 address range.
/// All access goes through one lock so multi register values (float32 etc.)
/// are never read half updated.
/// </summary>
public sealed class DataStore
{
    public const int Size = 65536;

    private readonly bool[] _coils = new bool[Size];
    private readonly bool[] _discreteInputs = new bool[Size];
    private readonly ushort[] _holdingRegisters = new ushort[Size];
    private readonly ushort[] _inputRegisters = new ushort[Size];
    private readonly object _lock = new();

    /// <summary>Raised after a Modbus client writes. Args: table, start address, count.</summary>
    public event Action<Table, int, int>? ClientWrite;

    public static bool InRange(int address, int count) =>
        address >= 0 && count >= 0 && address + count <= Size;

    public bool[] ReadBits(Table table, int address, int count)
    {
        var source = Bits(table);
        lock (_lock)
        {
            return source.AsSpan(address, count).ToArray();
        }
    }

    public ushort[] ReadRegisters(Table table, int address, int count)
    {
        var source = Registers(table);
        lock (_lock)
        {
            return source.AsSpan(address, count).ToArray();
        }
    }

    public void WriteBits(Table table, int address, ReadOnlySpan<bool> values, bool fromClient = false)
    {
        var target = Bits(table);
        lock (_lock)
        {
            values.CopyTo(target.AsSpan(address));
        }
        if (fromClient) ClientWrite?.Invoke(table, address, values.Length);
    }

    public void WriteRegisters(Table table, int address, ReadOnlySpan<ushort> values, bool fromClient = false)
    {
        var target = Registers(table);
        lock (_lock)
        {
            values.CopyTo(target.AsSpan(address));
        }
        if (fromClient) ClientWrite?.Invoke(table, address, values.Length);
    }

    /// <summary>Function 22: result = (current AND andMask) OR (orMask AND NOT andMask).</summary>
    public ushort MaskWriteHoldingRegister(int address, ushort andMask, ushort orMask)
    {
        ushort result;
        lock (_lock)
        {
            var current = _holdingRegisters[address];
            result = (ushort)((current & andMask) | (orMask & ~andMask));
            _holdingRegisters[address] = result;
        }
        ClientWrite?.Invoke(Table.HoldingRegisters, address, 1);
        return result;
    }

    /// <summary>Function 23: write first, then read, as one atomic step.</summary>
    public ushort[] WriteThenReadHoldingRegisters(int writeAddress, ReadOnlySpan<ushort> values, int readAddress, int readCount)
    {
        ushort[] result;
        lock (_lock)
        {
            values.CopyTo(_holdingRegisters.AsSpan(writeAddress));
            result = _holdingRegisters.AsSpan(readAddress, readCount).ToArray();
        }
        ClientWrite?.Invoke(Table.HoldingRegisters, writeAddress, values.Length);
        return result;
    }

    private bool[] Bits(Table table) => table switch
    {
        Table.Coils => _coils,
        Table.DiscreteInputs => _discreteInputs,
        _ => throw new ArgumentException($"{table} is not a bit table", nameof(table))
    };

    private ushort[] Registers(Table table) => table switch
    {
        Table.HoldingRegisters => _holdingRegisters,
        Table.InputRegisters => _inputRegisters,
        _ => throw new ArgumentException($"{table} is not a register table", nameof(table))
    };
}
