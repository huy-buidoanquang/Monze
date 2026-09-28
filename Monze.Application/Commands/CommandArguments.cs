using System.Collections;

namespace Monze.Application.Commands;

/// <summary>
/// Allocation-free view over command arguments. The view can expose a module
/// prefix without copying the SDK argument list and can be sliced by index.
/// </summary>
public readonly struct CommandArguments : IReadOnlyList<string>
{
    private readonly IReadOnlyList<string>? _items;
    private readonly string? _prefix;
    private readonly int _start;
    private readonly int _count;

    public CommandArguments(IReadOnlyList<string> items)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _prefix = null;
        _start = 0;
        _count = items.Count;
    }

    public CommandArguments(string prefix, IReadOnlyList<string> items)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("A command prefix is required.", nameof(prefix));
        }

        _items = items ?? throw new ArgumentNullException(nameof(items));
        _prefix = prefix;
        _start = 0;
        _count = items.Count + 1;
    }

    private CommandArguments(
        IReadOnlyList<string> items,
        string? prefix,
        int start,
        int count)
    {
        _items = items;
        _prefix = prefix;
        _start = start;
        _count = count;
    }

    public int Count => _count;

    public int Length => _count;

    public string this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (_prefix is not null)
            {
                return index == 0 ? _prefix : _items![_start + index - 1];
            }

            return _items![_start + index];
        }
    }

    public CommandArguments Slice(int start)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, _count);
        if (start == 0)
        {
            return this;
        }

        if (_prefix is not null)
        {
            return new CommandArguments(_items!, null, _start + start - 1, _count - start);
        }

        return new CommandArguments(_items!, null, _start + start, _count - start);
    }

    public string Join(char separator, int start = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, _count);
        var itemCount = _count - start;
        if (itemCount == 0)
        {
            return string.Empty;
        }

        if (itemCount == 1)
        {
            return this[start];
        }

        var length = itemCount - 1;
        for (var i = start; i < _count; i++)
        {
            length += this[i].Length;
        }

        return string.Create(
            length,
            new JoinState(this, start, separator),
            static (destination, state) =>
            {
                var position = 0;
                for (var i = state.Start; i < state.Arguments.Count; i++)
                {
                    if (i != state.Start)
                    {
                        destination[position++] = state.Separator;
                    }

                    var value = state.Arguments[i];
                    value.AsSpan().CopyTo(destination[position..]);
                    position += value.Length;
                }
            });
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<string> IEnumerable<string>.GetEnumerator() => new Enumerator(this);

    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

    private readonly record struct JoinState(
        CommandArguments Arguments,
        int Start,
        char Separator);

    public struct Enumerator : IEnumerator<string>
    {
        private readonly CommandArguments _arguments;
        private int _index;

        internal Enumerator(CommandArguments arguments)
        {
            _arguments = arguments;
            _index = -1;
        }

        public string Current => _arguments[_index];

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            _index++;
            return _index < _arguments.Count;
        }

        public void Reset() => _index = -1;

        public void Dispose()
        {
        }
    }
}
