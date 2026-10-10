namespace ModbusTcpSimulator.Simulation;

public enum SignalType
{
    /// <summary>Stays at Min. Useful for a value you only change by hand.</summary>
    Constant,
    /// <summary>Climbs linearly from Min to Max over PeriodSeconds, then jumps back.</summary>
    Ramp,
    /// <summary>Smooth wave between Min and Max.</summary>
    Sine,
    /// <summary>Max for the first half of the period, Min for the second. Good for on/off states.</summary>
    Square,
    /// <summary>Random walk: moves up to Step per tick, stays within Min..Max. Looks like a real sensor.</summary>
    Random,
    /// <summary>Adds Step every PeriodSeconds, wraps from Max back to Min. Like a production counter.</summary>
    Counter
}

public sealed class SignalSettings
{
    public SignalType Type { get; set; } = SignalType.Constant;
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double PeriodSeconds { get; set; } = 10;
    public double Step { get; set; } = 1;
}

public static class SignalGenerator
{
    /// <param name="settings">Signal shape.</param>
    /// <param name="seconds">Seconds since the simulation started.</param>
    /// <param name="previous">Last value, used by the random walk.</param>
    /// <param name="random">Random source, injectable for tests.</param>
    public static double Evaluate(SignalSettings settings, double seconds, double previous, Random random)
    {
        var min = settings.Min;
        var max = settings.Max;
        var period = settings.PeriodSeconds > 0 ? settings.PeriodSeconds : 1;
        var phase = seconds % period / period; // 0..1

        return settings.Type switch
        {
            SignalType.Constant => min,
            SignalType.Ramp => min + (max - min) * phase,
            SignalType.Sine => (min + max) / 2 + (max - min) / 2 * Math.Sin(2 * Math.PI * phase),
            SignalType.Square => phase < 0.5 ? max : min,
            SignalType.Random => Math.Clamp(
                (double.IsNaN(previous) ? (min + max) / 2 : previous) + (random.NextDouble() * 2 - 1) * settings.Step,
                min, max),
            SignalType.Counter => Counter(settings, seconds, period),
            _ => min
        };
    }

    private static double Counter(SignalSettings s, double seconds, double period)
    {
        var step = s.Step > 0 ? s.Step : 1;
        var ticks = Math.Floor(seconds / period);
        var span = s.Max - s.Min + step;
        return span <= 0 ? s.Min : s.Min + ticks * step % span;
    }
}
