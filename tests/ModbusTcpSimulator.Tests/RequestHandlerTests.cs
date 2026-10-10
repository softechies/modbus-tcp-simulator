using ModbusTcpSimulator.Data;
using ModbusTcpSimulator.Protocol;
using Xunit;

namespace ModbusTcpSimulator.Tests;

public class RequestHandlerTests
{
    private readonly DataStore _store = new();
    private readonly ModbusRequestHandler _handler;

    public RequestHandlerTests() => _handler = new ModbusRequestHandler(_store);

    private byte[] Send(string hex) => _handler.Handle(Convert.FromHexString(hex));

    [Fact]
    public void Read_holding_registers()
    {
        _store.WriteRegisters(Table.HoldingRegisters, 10, [0x1234, 0xABCD]);
        Assert.Equal("03041234ABCD", Convert.ToHexString(Send("03000A0002")));
    }

    [Fact]
    public void Read_input_registers()
    {
        _store.WriteRegisters(Table.InputRegisters, 0, [7]);
        Assert.Equal("04020007", Convert.ToHexString(Send("0400000001")));
    }

    [Fact]
    public void Read_coils_packs_lsb_first()
    {
        // Coils 0..9: on, off, on, on, off, off, off, off, on, off
        _store.WriteBits(Table.Coils, 0, [true, false, true, true, false, false, false, false, true, false]);
        // byte 1 = 0b00001101 = 0x0D, byte 2 = 0b00000001
        Assert.Equal("01020D01", Convert.ToHexString(Send("010000000A")));
    }

    [Fact]
    public void Read_discrete_inputs()
    {
        _store.WriteBits(Table.DiscreteInputs, 5, [true]);
        Assert.Equal("020101", Convert.ToHexString(Send("0200050001")));
    }

    [Fact]
    public void Write_single_coil_echoes_and_sets()
    {
        Assert.Equal("050003FF00", Convert.ToHexString(Send("050003FF00")));
        Assert.True(_store.ReadBits(Table.Coils, 3, 1)[0]);

        Send("0500030000");
        Assert.False(_store.ReadBits(Table.Coils, 3, 1)[0]);
    }

    [Fact]
    public void Write_single_coil_rejects_bad_value()
    {
        Assert.Equal("8503", Convert.ToHexString(Send("0500031234")));
    }

    [Fact]
    public void Write_single_register()
    {
        Assert.Equal("060001BEEF", Convert.ToHexString(Send("060001BEEF")));
        Assert.Equal(0xBEEF, _store.ReadRegisters(Table.HoldingRegisters, 1, 1)[0]);
    }

    [Fact]
    public void Write_multiple_coils()
    {
        // 10 coils from address 20: CD 01 -> 1,0,1,1,0,0,1,1, 1,0
        Assert.Equal("0F0014000A", Convert.ToHexString(Send("0F0014000A02CD01")));
        Assert.Equal([true, false, true, true, false, false, true, true, true, false], _store.ReadBits(Table.Coils, 20, 10));
    }

    [Fact]
    public void Write_multiple_registers()
    {
        Assert.Equal("1000000002", Convert.ToHexString(Send("1000000002040001FFFF")));
        Assert.Equal(new ushort[] { 1, 0xFFFF }, _store.ReadRegisters(Table.HoldingRegisters, 0, 2));
    }

    [Fact]
    public void Write_multiple_registers_rejects_wrong_byte_count()
    {
        Assert.Equal("9003", Convert.ToHexString(Send("1000000002030001FF")));
    }

    [Fact]
    public void Mask_write_register()
    {
        // Spec example: current 0x12, AND 0xF2, OR 0x25 -> 0x17
        _store.WriteRegisters(Table.HoldingRegisters, 4, [0x0012]);
        Assert.Equal("16000400F20025", Convert.ToHexString(Send("16000400F20025")));
        Assert.Equal(0x17, _store.ReadRegisters(Table.HoldingRegisters, 4, 1)[0]);
    }

    [Fact]
    public void Read_write_multiple_writes_before_reading()
    {
        Assert.Equal("170400AA00BB", Convert.ToHexString(Send("1700000002000000020400AA00BB")));
    }

    [Fact]
    public void Unknown_function_returns_illegal_function()
    {
        Assert.Equal("AB01", Convert.ToHexString(Send("2B0E0100")));
    }

    [Fact]
    public void Quantity_zero_returns_illegal_data_value()
    {
        Assert.Equal("8303", Convert.ToHexString(Send("0300000000")));
    }

    [Fact]
    public void Quantity_over_limit_returns_illegal_data_value()
    {
        Assert.Equal("8303", Convert.ToHexString(Send("030000007E"))); // 126 > 125
        Assert.Equal("8103", Convert.ToHexString(Send("01000007D1"))); // 2001 > 2000
    }

    [Fact]
    public void Address_past_end_returns_illegal_data_address()
    {
        Assert.Equal("8302", Convert.ToHexString(Send("03FFFF0002")));
    }

    [Fact]
    public void Truncated_request_returns_illegal_data_value()
    {
        Assert.Equal("8303", Convert.ToHexString(Send("0300")));
    }
}
