using System.Buffers.Binary;
using System.Text;

namespace NAITool.Services;

/// <summary>Bounded MessagePack value reader for native image stream maps (no package/runtime dependency).</summary>
internal sealed class NovelAIMessagePack
{
    private readonly byte[] _bytes;
    private int _offset;
    private NovelAIMessagePack(byte[] bytes) => _bytes = bytes;
    public static Dictionary<string, object?> ReadMap(byte[] bytes)
    {
        var reader = new NovelAIMessagePack(bytes);
        var value = reader.Read(0) as Dictionary<string, object?> ?? throw new InvalidDataException("Expected MessagePack map.");
        if (reader._offset != bytes.Length) throw new InvalidDataException("Trailing MessagePack data.");
        return value;
    }
    private byte[] Take(int count)
    {
        if (count < 0 || count > _bytes.Length - _offset) throw new InvalidDataException("Truncated MessagePack value.");
        var value = _bytes.AsSpan(_offset, count).ToArray(); _offset += count; return value;
    }
    private uint Number(int count) => count switch
    {
        1 => Take(1)[0], 2 => BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
        4 => BinaryPrimitives.ReadUInt32BigEndian(Take(4)), _ => throw new InvalidDataException(),
    };
    private int Length(int count) => checked((int)Number(count));
    private object? Read(int depth)
    {
        if (depth > 32) throw new InvalidDataException("MessagePack nesting limit exceeded.");
        byte code = Take(1)[0];
        if (code <= 0x7f) return (long)code;
        if (code >= 0xe0) return (long)(sbyte)code;
        if ((code & 0xe0) == 0xa0) return Text(code & 31);
        if ((code & 0xf0) == 0x80) return Map(code & 15, depth);
        if ((code & 0xf0) == 0x90) return Array(code & 15, depth);
        return code switch
        {
            0xc0 => null, 0xc2 => false, 0xc3 => true,
            0xc4 => Take(Length(1)), 0xc5 => Take(Length(2)), 0xc6 => Take(Length(4)),
            0xcc => (long)Number(1), 0xcd => (long)Number(2), 0xce => (long)Number(4),
            0xcf => BinaryPrimitives.ReadUInt64BigEndian(Take(8)),
            0xd0 => (long)(sbyte)Take(1)[0], 0xd1 => (long)BinaryPrimitives.ReadInt16BigEndian(Take(2)),
            0xd2 => (long)BinaryPrimitives.ReadInt32BigEndian(Take(4)), 0xd3 => BinaryPrimitives.ReadInt64BigEndian(Take(8)),
            0xca => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(Take(4))),
            0xcb => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(Take(8))),
            0xd9 => Text(Length(1)), 0xda => Text(Length(2)), 0xdb => Text(Length(4)),
            0xdc => Array(Length(2), depth), 0xdd => Array(Length(4), depth),
            0xde => Map(Length(2), depth), 0xdf => Map(Length(4), depth),
            _ => throw new InvalidDataException("Unsupported MessagePack value."),
        };
    }
    private string Text(int length) => new UTF8Encoding(false, true).GetString(Take(length));
    private Dictionary<string, object?> Map(int length, int depth)
    {
        if (length > _bytes.Length) throw new InvalidDataException("Invalid MessagePack map size.");
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < length; i++)
        {
            string key = Read(depth + 1) as string ?? throw new InvalidDataException("Non-string MessagePack map key.");
            if (!map.TryAdd(key, Read(depth + 1))) throw new InvalidDataException("Duplicate MessagePack map key.");
        }
        return map;
    }
    private List<object?> Array(int length, int depth)
    {
        if (length > _bytes.Length) throw new InvalidDataException("Invalid MessagePack array size.");
        var array = new List<object?>();
        for (int i = 0; i < length; i++) array.Add(Read(depth + 1));
        return array;
    }
}
