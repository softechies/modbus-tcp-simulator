using System.Buffers.Binary;
using ModbusTcpSimulator.Data;

namespace ModbusTcpSimulator.Protocol;

/// <summary>
/// Turns a request PDU (function code + data) into a response PDU.
/// Transport independent: the TCP server adds and strips the MBAP header.
/// Validation order follows the spec: function code, then quantity (0x03), then address (0x02).
/// </summary>
public sealed class ModbusRequestHandler(DataStore store)
{
    public byte[] Handle(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length == 0)
        {
            throw new ArgumentException("Empty PDU", nameof(pdu));
        }

        var function = pdu[0];
        try
        {
            return (FunctionCode)function switch
            {
                FunctionCode.ReadCoils => ReadBits(Table.Coils, pdu),
                FunctionCode.ReadDiscreteInputs => ReadBits(Table.DiscreteInputs, pdu),
                FunctionCode.ReadHoldingRegisters => ReadRegisters(Table.HoldingRegisters, pdu),
                FunctionCode.ReadInputRegisters => ReadRegisters(Table.InputRegisters, pdu),
                FunctionCode.WriteSingleCoil => WriteSingleCoil(pdu),
                FunctionCode.WriteSingleRegister => WriteSingleRegister(pdu),
                FunctionCode.WriteMultipleCoils => WriteMultipleCoils(pdu),
                FunctionCode.WriteMultipleRegisters => WriteMultipleRegisters(pdu),
                FunctionCode.MaskWriteRegister => MaskWrite(pdu),
                FunctionCode.ReadWriteMultipleRegisters => ReadWriteMultiple(pdu),
                _ => throw new ModbusException(ExceptionCode.IllegalFunction, $"Function 0x{function:X2} not supported")
            };
        }
        catch (ModbusException ex)
        {
            return ExceptionResponse(function, ex.Code);
        }
        catch (Exception)
        {
            return ExceptionResponse(function, ExceptionCode.ServerDeviceFailure);
        }
    }

    public static byte[] ExceptionResponse(byte function, ExceptionCode code) =>
        [(byte)(function | 0x80), (byte)code];

    private byte[] ReadBits(Table table, ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 5);
        var address = U16(pdu, 1);
        var count = U16(pdu, 3);
        RequireQuantity(count, 1, Limits.MaxReadBits);
        RequireAddress(address, count);

        var bits = store.ReadBits(table, address, count);
        var byteCount = (count + 7) / 8;
        var response = new byte[2 + byteCount];
        response[0] = pdu[0];
        response[1] = (byte)byteCount;
        PackBits(bits, response.AsSpan(2));
        return response;
    }

    private byte[] ReadRegisters(Table table, ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 5);
        var address = U16(pdu, 1);
        var count = U16(pdu, 3);
        RequireQuantity(count, 1, Limits.MaxReadRegisters);
        RequireAddress(address, count);

        var registers = store.ReadRegisters(table, address, count);
        var response = new byte[2 + count * 2];
        response[0] = pdu[0];
        response[1] = (byte)(count * 2);
        WriteRegistersTo(registers, response.AsSpan(2));
        return response;
    }

    private byte[] WriteSingleCoil(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 5);
        var address = U16(pdu, 1);
        var value = U16(pdu, 3);
        if (value is not (0xFF00 or 0x0000))
        {
            throw new ModbusException(ExceptionCode.IllegalDataValue, "Coil value must be 0xFF00 or 0x0000");
        }
        RequireAddress(address, 1);

        store.WriteBits(Table.Coils, address, [value == 0xFF00], fromClient: true);
        return pdu[..5].ToArray(); // echo
    }

    private byte[] WriteSingleRegister(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 5);
        var address = U16(pdu, 1);
        var value = (ushort)U16(pdu, 3);
        RequireAddress(address, 1);

        store.WriteRegisters(Table.HoldingRegisters, address, [value], fromClient: true);
        return pdu[..5].ToArray(); // echo
    }

    private byte[] WriteMultipleCoils(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 6);
        var address = U16(pdu, 1);
        var count = U16(pdu, 3);
        var byteCount = pdu[5];
        RequireQuantity(count, 1, Limits.MaxWriteBits);
        if (byteCount != (count + 7) / 8 || pdu.Length < 6 + byteCount)
        {
            throw new ModbusException(ExceptionCode.IllegalDataValue, "Byte count does not match quantity");
        }
        RequireAddress(address, count);

        var bits = UnpackBits(pdu.Slice(6, byteCount), count);
        store.WriteBits(Table.Coils, address, bits, fromClient: true);
        return pdu[..5].ToArray(); // function, address, quantity
    }

    private byte[] WriteMultipleRegisters(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 6);
        var address = U16(pdu, 1);
        var count = U16(pdu, 3);
        var byteCount = pdu[5];
        RequireQuantity(count, 1, Limits.MaxWriteRegisters);
        if (byteCount != count * 2 || pdu.Length < 6 + byteCount)
        {
            throw new ModbusException(ExceptionCode.IllegalDataValue, "Byte count does not match quantity");
        }
        RequireAddress(address, count);

        var values = ReadRegistersFrom(pdu.Slice(6, byteCount));
        store.WriteRegisters(Table.HoldingRegisters, address, values, fromClient: true);
        return pdu[..5].ToArray();
    }

    private byte[] MaskWrite(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 7);
        var address = U16(pdu, 1);
        RequireAddress(address, 1);
        store.MaskWriteHoldingRegister(address, (ushort)U16(pdu, 3), (ushort)U16(pdu, 5));
        return pdu[..7].ToArray(); // echo
    }

    private byte[] ReadWriteMultiple(ReadOnlySpan<byte> pdu)
    {
        RequireLength(pdu, 10);
        var readAddress = U16(pdu, 1);
        var readCount = U16(pdu, 3);
        var writeAddress = U16(pdu, 5);
        var writeCount = U16(pdu, 7);
        var byteCount = pdu[9];

        RequireQuantity(readCount, 1, Limits.MaxReadRegisters);
        RequireQuantity(writeCount, 1, Limits.MaxReadWriteWriteRegisters);
        if (byteCount != writeCount * 2 || pdu.Length < 10 + byteCount)
        {
            throw new ModbusException(ExceptionCode.IllegalDataValue, "Byte count does not match write quantity");
        }
        RequireAddress(readAddress, readCount);
        RequireAddress(writeAddress, writeCount);

        var values = ReadRegistersFrom(pdu.Slice(10, byteCount));
        var read = store.WriteThenReadHoldingRegisters(writeAddress, values, readAddress, readCount);

        var response = new byte[2 + readCount * 2];
        response[0] = pdu[0];
        response[1] = (byte)(readCount * 2);
        WriteRegistersTo(read, response.AsSpan(2));
        return response;
    }

    // Helpers

    private static int U16(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt16BigEndian(span[offset..]);

    private static void RequireLength(ReadOnlySpan<byte> pdu, int length)
    {
        if (pdu.Length < length)
        {
            throw new ModbusException(ExceptionCode.IllegalDataValue, "Request too short");
        }
    }

    private static void RequireQuantity(int count, int min, int max)
    {
        if (count < min || count > max)
        {
            throw new ModbusException(ExceptionCode.IllegalDataValue, $"Quantity {count} outside {min}..{max}");
        }
    }

    private static void RequireAddress(int address, int count)
    {
        if (!DataStore.InRange(address, count))
        {
            throw new ModbusException(ExceptionCode.IllegalDataAddress, $"Address {address} + {count} out of range");
        }
    }

    internal static void PackBits(ReadOnlySpan<bool> bits, Span<byte> target)
    {
        target.Clear();
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                target[i / 8] |= (byte)(1 << (i % 8)); // first coil is the LSB of the first byte
            }
        }
    }

    internal static bool[] UnpackBits(ReadOnlySpan<byte> bytes, int count)
    {
        var bits = new bool[count];
        for (var i = 0; i < count; i++)
        {
            bits[i] = (bytes[i / 8] & (1 << (i % 8))) != 0;
        }
        return bits;
    }

    private static void WriteRegistersTo(ReadOnlySpan<ushort> registers, Span<byte> target)
    {
        for (var i = 0; i < registers.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(target[(i * 2)..], registers[i]);
        }
    }

    private static ushort[] ReadRegistersFrom(ReadOnlySpan<byte> bytes)
    {
        var values = new ushort[bytes.Length / 2];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes[(i * 2)..]);
        }
        return values;
    }
}
