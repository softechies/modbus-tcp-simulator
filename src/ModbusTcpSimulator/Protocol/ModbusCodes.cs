namespace ModbusTcpSimulator.Protocol;

public enum FunctionCode : byte
{
    ReadCoils = 0x01,
    ReadDiscreteInputs = 0x02,
    ReadHoldingRegisters = 0x03,
    ReadInputRegisters = 0x04,
    WriteSingleCoil = 0x05,
    WriteSingleRegister = 0x06,
    WriteMultipleCoils = 0x0F,
    WriteMultipleRegisters = 0x10,
    MaskWriteRegister = 0x16,
    ReadWriteMultipleRegisters = 0x17
}

public enum ExceptionCode : byte
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    ServerDeviceFailure = 0x04
}

/// <summary>Quantity limits from the Modbus Application Protocol spec v1.1b3.</summary>
public static class Limits
{
    public const int MaxReadBits = 2000;
    public const int MaxReadRegisters = 125;
    public const int MaxWriteBits = 1968;
    public const int MaxWriteRegisters = 123;
    public const int MaxReadWriteWriteRegisters = 121;
    public const int MaxPduLength = 253;
}

internal sealed class ModbusException(ExceptionCode code, string message) : Exception(message)
{
    public ExceptionCode Code { get; } = code;
}
