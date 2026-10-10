using System.Diagnostics;
using ModbusTcpSimulator.Data;

namespace ModbusTcpSimulator.Simulation;

/// <summary>
/// Updates every point that has a signal, on a fixed tick.
/// Individual points can be paused so a value written by hand (or by a Modbus client) sticks.
/// </summary>
public sealed class SimulationEngine : IAsyncDisposable
{
    private readonly DataStore _store;
    private readonly IReadOnlyList<PointConfig> _points;
    private readonly Dictionary<string, double> _lastValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _paused = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public SimulationEngine(DataStore store, IEnumerable<PointConfig> points, Random? random = null)
    {
        _store = store;
        _points = points.ToList();
        _random = random ?? new Random();

        var duplicates = _points.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new InvalidOperationException($"Duplicate point names: {string.Join(", ", duplicates)}");

        foreach (var point in _points)
        {
            point.Validate();
            PointCodec.Write(_store, point, point.Value);
            _lastValues[point.Name] = point.Value;
        }
    }

    public bool Enabled { get; set; } = true;

    public IReadOnlyList<PointConfig> Points => _points;

    public PointConfig? Find(string name) =>
        _points.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool IsPaused(string name) { lock (_lock) return _paused.Contains(name); }

    public void Pause(string name) { lock (_lock) _paused.Add(name); }

    public void Resume(string name) { lock (_lock) _paused.Remove(name); }

    /// <summary>Applies one step of every active signal for the given elapsed time.</summary>
    public void Tick(double elapsedSeconds)
    {
        if (!Enabled) return;

        lock (_lock)
        {
            foreach (var point in _points)
            {
                if (point.Signal is null || _paused.Contains(point.Name)) continue;

                var previous = _lastValues.TryGetValue(point.Name, out var v) ? v : double.NaN;
                var value = SignalGenerator.Evaluate(point.Signal, elapsedSeconds, previous, _random);
                _lastValues[point.Name] = value;
                PointCodec.Write(_store, point, value);
            }
        }
    }

    public void Start(TimeSpan tick)
    {
        var stopwatch = Stopwatch.StartNew();
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(tick);
            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    Tick(stopwatch.Elapsed.TotalSeconds);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) await _loop;
        _cts.Dispose();
    }
}
