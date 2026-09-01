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
    /// <summary>Largest number of samples this series can hold — the size a
    /// <see cref="CopyTo"/> destination needs to be to take all of them.</summary>
    public int Capacity => _buffer.Length;

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

    /// <summary>
    /// Copies the buffered samples oldest→newest into <paramref name="destination"/> and returns
    /// how many were written. If the destination is shorter than <see cref="Capacity"/> only the
    /// newest samples that fit are copied.
    /// </summary>
    /// <remarks>
    /// The caller owns the destination and is meant to reuse it. This used to return a fresh
    /// <c>double[]</c>, and with the plot calling it once per channel per redraw that made the
    /// live plot the app's largest allocator (#122). The two block copies also replace a
    /// modulo-per-element loop: the ring wraps at most once, so where it wraps is arithmetic
    /// done once rather than a division per sample.
    /// </remarks>
    public int CopyTo(double[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            var count = Math.Min(_count, destination.Length);
            var start = (_head - count + _buffer.Length) % _buffer.Length;
            var beforeWrap = Math.Min(count, _buffer.Length - start);
            Array.Copy(_buffer, start, destination, 0, beforeWrap);
            Array.Copy(_buffer, 0, destination, beforeWrap, count - beforeWrap);
            return count;
        }
    }
}
