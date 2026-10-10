using System.Net;
using System.Net.Sockets;
using ModbusTcpSimulator.Cli;
using ModbusTcpSimulator.Data;
using ModbusTcpSimulator.Protocol;
using ModbusTcpSimulator.Server;
using ModbusTcpSimulator.Simulation;
using Xunit;

namespace ModbusTcpSimulator.Tests;

public class ServerTests
{
    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream)
    {
        var header = new byte[7];
        await stream.ReadExactlyAsync(header);
        var length = (header[4] << 8) | header[5];
        var body = new byte[length - 1];
        await stream.ReadExactlyAsync(body);
        return [.. header, .. body];
    }

    [Fact]
    public async Task Answers_read_request_over_tcp()
    {
        var store = new DataStore();
        store.WriteRegisters(Table.HoldingRegisters, 0, [42, 43]);
        await using var server = new ModbusTcpServer(IPAddress.Loopback, 0, new ModbusRequestHandler(store));
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();

        // tx 0x0001, protocol 0, length 6, unit 0x11, read hr 0 count 2
        await stream.WriteAsync(Convert.FromHexString("000100000006110300000002"));
        var response = await ReadFrameAsync(stream);

        Assert.Equal("000100000007110304002A002B", Convert.ToHexString(response));
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Handles_request_split_across_packets_and_pipelined_requests()
    {
        var store = new DataStore();
        await using var server = new ModbusTcpServer(IPAddress.Loopback, 0, new ModbusRequestHandler(store));
        server.Start();

        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();

        // First request in two pieces
        await stream.WriteAsync(Convert.FromHexString("0007000000"));
        await stream.FlushAsync();
        await Task.Delay(50);
        await stream.WriteAsync(Convert.FromHexString("06010600050064")); // write hr 5 = 100
        // Two more requests in a single write
        await stream.WriteAsync(Convert.FromHexString("000800000006010300050001" + "000900000006010300050001"));

        Assert.Equal("000700000006010600050064", Convert.ToHexString(await ReadFrameAsync(stream)));
        Assert.Equal("0008000000050103020064", Convert.ToHexString(await ReadFrameAsync(stream)));
        Assert.Equal("0009000000050103020064", Convert.ToHexString(await ReadFrameAsync(stream)));
    }

    [Fact]
    public async Task Closes_connection_on_invalid_protocol_id()
    {
        await using var server = new ModbusTcpServer(IPAddress.Loopback, 0, new ModbusRequestHandler(new DataStore()));
        server.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Convert.FromHexString("000100010006110300000002")); // protocol id 1

        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, read); // server closed the socket
    }

    [Fact]
    public void Console_commands_get_and_set()
    {
        var store = new DataStore();
        var engine = new SimulationEngine(store, [new PointConfig { Name = "Speed", Table = Table.HoldingRegisters, Address = 10, DataType = DataType.Float32, Value = 1.5 }]);
        var cli = new CommandProcessor(store, engine);

        Assert.Contains("Wrote 2", cli.Execute("set hr 0 100 -1"));
        Assert.Equal(new ushort[] { 100, 0xFFFF }, store.ReadRegisters(Table.HoldingRegisters, 0, 2));

        cli.Execute("set coil 7 on");
        Assert.True(store.ReadBits(Table.Coils, 7, 1)[0]);

        Assert.Contains("hr[1] = 65535", cli.Execute("get hr 1"));
        Assert.Contains("1.5", cli.Execute("points"));

        cli.Execute("setp Speed 2.25");
        Assert.Equal(2.25, PointCodec.Read(store, engine.Points[0]), 6);

        Assert.StartsWith("Error", cli.Execute("set hr 0 70000"));
        Assert.StartsWith("Unknown command", cli.Execute("bogus"));

        cli.Execute("quit");
        Assert.True(cli.QuitRequested);
    }

    [Theory]
    [InlineData("0x00FF", 255)]
    [InlineData("-32768", 32768)]
    [InlineData("65535", 65535)]
    public void Register_value_parsing(string text, int expected)
    {
        Assert.Equal((ushort)expected, CommandProcessor.ParseRegister(text));
    }
}
