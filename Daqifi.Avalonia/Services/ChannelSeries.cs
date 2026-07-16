namespace Daqifi.Avalonia.Services;

/// <summary>
/// A fixed-capacity rolling buffer of the latest samples for one channel,
/// safe to append from the Core receive thread and snapshot from the UI
/// render timer.
/// </summary>
public sealed class ChannelSeries
{
    private readonly double[] _buffer;
    private readonly object _gate = new();
    private int _count;
    private int _head;

    public ChannelSeries(string name, uint colorArgb, int capacity)
    {
        Name = name;
        ColorArgb = colorArgb;
        _buffer = new double[capacity];
    }

    public string Name { get; }
    /// <summary>0xAARRGGBB.</summary>
    public uint ColorArgb { get; }
    public double Latest { get; private set; }
    public bool HasData => _count > 0;

    public void Append(double value)
    {
        lock (_gate)
        {
            _buffer[_head] = value;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) { _count++; }
            Latest = value;
        }
    }

    /// <summary>Oldest→newest copy of the buffered samples.</summary>
    public double[] Snapshot()
    {
        lock (_gate)
        {
            var outp = new double[_count];
            var start = (_head - _count + _buffer.Length) % _buffer.Length;
            for (var i = 0; i < _count; i++)
            {
                outp[i] = _buffer[(start + i) % _buffer.Length];
            }
            return outp;
        }
    }
}
