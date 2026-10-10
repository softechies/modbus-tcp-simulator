# Modbus TCP Simulator

A lightweight Modbus TCP server for testing HMIs, SCADA systems, data loggers and
your own Modbus client code without real hardware. Define named points in a JSON file,
give them moving signals (sine, ramp, random walk, counters), and poke values by hand
from an interactive console.

Written in plain .NET 8 with no third party packages. Runs on Windows, Linux, macOS and Docker.

[![build](https://github.com/softechies/modbus-tcp-simulator/actions/workflows/build.yml/badge.svg)](https://github.com/softechies/modbus-tcp-simulator/actions/workflows/build.yml)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

## Features

* **Full protocol coverage for testing:** function codes 01, 02, 03, 04, 05, 06, 15, 16, 22 (mask write) and 23 (read/write multiple)
* **Correct exception responses:** illegal function, illegal data address, illegal data value, with spec quantity limits
* **All four tables, full address range:** coils, discrete inputs, holding registers, input registers (0..65535)
* **Typed points:** `bool`, `uint16`, `int16`, `uint32`, `int32`, `float32`, with `ABCD` or `CDAB` word order and an engineering scale factor
* **Live signals:** constant, ramp, sine, square, random walk, counter
* **Interactive console:** read and write any address, set named points, pause signals, watch traffic as hex
* **Multiple clients:** each connection handled independently, split and pipelined frames handled correctly
* **Headless mode:** runs fine in Docker or as a background process

## Quick start

```bash
git clone https://github.com/softechies/modbus-tcp-simulator.git
cd modbus-tcp-simulator
dotnet run --project src/ModbusTcpSimulator -- --config samples/tank-farm.json --port 5020
```

Then point any Modbus client at `127.0.0.1:5020`. For example with
[mbpoll](https://github.com/epsilonrt/mbpoll) (note it uses 1 based addresses):

```bash
mbpoll -a 1 -r 1 -c 2 -t 3:float -1 127.0.0.1 -p 5020   # Tank1 Level as float32
```

Or with Python and pymodbus:

```python
from pymodbus.client import ModbusTcpClient
client = ModbusTcpClient("127.0.0.1", port=5020)
print(client.read_holding_registers(2, count=1).registers)   # Pump Speed
client.write_coil(1, True)                                  # start Pump2
```

### Docker

```bash
docker build -t modbus-sim .
docker run --rm -p 502:502 modbus-sim
```

## Console commands

```
> points
Name                   Table  Addr Type              Value  Signal
Tank1 Level %          ir        0 Float32          57.257  Sine
Tank2 Level %          ir        2 Float32          10.481  Ramp
Tank1 Temp C           ir        4 Int16              21.7  Random
Pump Speed rpm         hr        2 UInt16             1450
Pump1 Run              coil      0 Bool                 ON
...

> get hr 0 4
hr[0] = 17046  (int16  17046, 0x4296)
hr[1] =     0  (int16      0, 0x0000)
hr[2] =  1450  (int16   1450, 0x05AA)
hr[3] =     1  (int16      1, 0x0001)

> set hr 3 2
> set coil 1 on
> setp Tank1 Level % 95        # pauses that point's signal so the value sticks
> resume Tank1 Level %
> verbose on                   # log every request/response as hex
> stats
```

Type `help` for the full list.

## Config file

```jsonc
{
  "port": 5020,
  "tickMilliseconds": 200,
  "points": [
    {
      "name": "Tank1 Level %",
      "table": "ir",              // coil | di | hr | ir
      "address": 0,               // zero based
      "dataType": "float32",      // bool | uint16 | int16 | uint32 | int32 | float32
      "wordOrder": "ABCD",        // ABCD (high word first) or CDAB
      "scale": 1,                 // raw = value * scale, e.g. 10 for tenths
      "value": 50,                // initial value
      "signal": { "type": "sine", "min": 20, "max": 85, "periodSeconds": 120 }
    }
  ]
}
```

| Signal | Behaviour | Uses |
|---|---|---|
| `constant` | Holds `min` | Placeholder you change by hand |
| `ramp` | Linear `min` to `max` over the period, then restarts | Filling tanks, test sweeps |
| `sine` | Smooth wave between `min` and `max` | Temperatures, levels |
| `square` | `max` for half the period, `min` for the rest | Run/stop states, valve cycles |
| `random` | Random walk of up to `step` per tick, clamped to the range | Realistic noisy sensors |
| `counter` | Adds `step` every period, wraps after `max` | Production counts, totalisers |

See [`samples/tank-farm.json`](samples/tank-farm.json) for a complete example.

## Addressing

Addresses in this tool are **zero based protocol addresses**, which is what goes on the wire.
Many HMI and SCADA tools use 1 based "register numbers" instead:

| Table | Here | Typical tool notation |
|---|---|---|
| Coil 0 | `coil 0` | 00001 |
| Discrete input 0 | `di 0` | 10001 |
| Input register 0 | `ir 0` | 30001 |
| Holding register 0 | `hr 0` | 40001 |

## Command line

```
modbus-sim [options]
  -c, --config <file>   JSON config with points and signals
  -p, --port <port>     TCP port (default 502)
  -b, --bind <address>  IP to listen on (default 0.0.0.0)
  -v, --verbose         log every request and response as hex
```

The simulator answers every unit id and echoes it back, like most Modbus TCP devices.

## Project layout

```
src/ModbusTcpSimulator/
  Protocol/    request handler (PDU in, PDU out), function and exception codes
  Server/      TCP listener and MBAP framing
  Data/        the four data tables
  Simulation/  typed points, signals, config loading
  Cli/         interactive console commands
tests/         protocol, codec, signal and TCP integration tests
samples/       example configs
```

## Tests

```bash
dotnet test
```

## License

MIT

---

Built and maintained by [Softechies Solutions](https://softechies.net). We build industrial and
Windows software in .NET: Modbus and OPC integrations, device drivers, Windows services and desktop tools.
