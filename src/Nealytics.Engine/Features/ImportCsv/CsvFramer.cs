namespace Nealytics.Engine.Features.ImportCsv;

using System.Buffers;
using System.Collections.Generic;

internal sealed class CsvFramer(int limit)
{
    private enum State
    {
        Start,
        Text,
        Quoted,
        Closed,
        CarriageReturn,
        Complete
    }

    internal const int MaxColumns = 256;
    private readonly List<int> _commas = [];
    private State _state;
    private int _preamble;
    private int _bytes;

    public IReadOnlyList<int> Commas => _commas;
    public bool Complete => _state == State.Complete;
    public string? Error { get; private set; }
    public bool ValidEnd => _state is State.Start or State.Text or State.Closed;

    public long Scan(ReadOnlySequence<byte> buffer)
    {
        SequenceReader<byte> reader = new(buffer);
        while (reader.TryRead(out byte value))
        {
            if (++_bytes > limit)
            {
                Error = "record_too_large";
            }
            else
            {
                Advance(value);
            }
            if (Complete || Error is not null)
            {
                break;
            }
        }
        return reader.Consumed;
    }

    public void Reset()
    {
        _state = State.Start;
        _bytes = 0;
        _commas.Clear();
    }

    private void Advance(byte value)
    {
        if (_preamble >= 0)
        {
            byte expected = _preamble switch { 0 => 0xEF, 1 => 0xBB, _ => 0xBF };
            if (value == expected)
            {
                _preamble++;
                if (_preamble == 3)
                {
                    _preamble = -1;
                }
                return;
            }
            if (_preamble != 0)
            {
                _state = State.Text;
            }
            _preamble = -1;
        }
        if (_state == State.Quoted)
        {
            if (value == '"')
            {
                _state = State.Closed;
            }
        }
        else if (_state == State.CarriageReturn)
        {
            if (value == '\n')
            {
                _state = State.Complete;
            }
            else
            {
                Error = "invalid_csv";
            }
        }
        else if (value == ',')
        {
            if (_commas.Count == MaxColumns - 1)
            {
                Error = "column_limit";
            }
            else
            {
                _commas.Add(_bytes - 1);
                _state = State.Start;
            }
        }
        else if (value == '\r')
        {
            _state = State.CarriageReturn;
        }
        else if (value == '\n')
        {
            _state = State.Complete;
        }
        else if (value == '"' && _state is State.Start or State.Closed)
        {
            _state = State.Quoted;
        }
        else if (value == '"' || _state == State.Closed)
        {
            Error = "invalid_csv";
        }
        else
        {
            _state = State.Text;
        }
    }
}
