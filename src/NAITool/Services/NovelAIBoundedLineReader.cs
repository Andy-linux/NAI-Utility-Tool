using System.Text;

namespace NAITool.Services;

/// <summary>Bound line allocation before parsing SSE rather than after StreamReader allocates a whole hostile line.</summary>
internal sealed class NovelAIBoundedLineReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly char[] _buffer = new char[4096];
    private int _offset, _count;
    public NovelAIBoundedLineReader(Stream stream) => _reader = new StreamReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await _reader.ReadAsync(_buffer.AsMemory(), ct); _offset = 0;
                if (_count == 0) return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
            }
            char character = _buffer[_offset++];
            if (character == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= NovelAIImageResponse.MaximumImageBytes * 2)
                throw new InvalidDataException("Stream line exceeds the size limit.");
            line.Append(character);
        }
    }
    public void Dispose() => _reader.Dispose();
}
