using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace NAITool.Services;

internal static class NovelAIStreamFrames
{
    public static async IAsyncEnumerable<Dictionary<string, object?>> ReadMessagePackAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        byte[] header = new byte[4];
        while (true)
        {
            int first = await stream.ReadAsync(header.AsMemory(0, 1), ct);
            if (first == 0) yield break;
            await stream.ReadExactlyAsync(header.AsMemory(1, 3), ct);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length == 0 || length > NovelAIImageResponse.MaximumImageBytes)
                throw new InvalidDataException("Invalid stream frame length.");
            byte[] frame = new byte[(int)length];
            await stream.ReadExactlyAsync(frame, ct);
            yield return NovelAIMessagePack.ReadMap(frame);
        }
    }

    public static async IAsyncEnumerable<(string Event, string Data)> ReadSseAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new NovelAIBoundedLineReader(stream);
        string eventName = "";
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return (eventName, data.ToString().TrimEnd('\n'));
                eventName = ""; data.Clear(); continue;
            }
            if (line.StartsWith(':')) continue;
            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "event") eventName = value;
            if (field == "data") data.Append(value).Append('\n');
            if (data.Length > NovelAIImageResponse.MaximumImageBytes * 2) throw new InvalidDataException("SSE event exceeds size limit.");
        }
        // SSE events are terminated by a blank line. Incomplete events at EOF are not promoted to final.
        if (data.Length > 0) throw new InvalidDataException("Truncated SSE event.");
    }
}
