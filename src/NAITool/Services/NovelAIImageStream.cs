using System.Text.Json;

namespace NAITool.Services;

internal static class NovelAIImageStream
{
    public static async Task<byte[]> ReadAsync(Stream stream, string? mediaType, Func<byte[], bool> decodable,
        IProgress<byte[]>? progress, CancellationToken ct)
    {
        byte[]? final = null;
        void Process(Dictionary<string, object?> frame, string eventName = "")
        {
            string type = frame.TryGetValue("event_type", out var kind) && kind is string text ? text : eventName;
            if (type == "error" || frame.ContainsKey("error"))
                throw new InvalidDataException("Image stream returned an error; billing outcome may be unknown.");
            if (type != "intermediate" && type != "final") return;
            if (frame.TryGetValue("samp_ix", out var sample) && sample is not (0L or 0UL))
                throw new InvalidDataException("Unexpected image sample index for a single-result request.");
            if (type == "intermediate")
            {
                // The website hides step_ix < 5 by default. Gateways without step metadata retain their previews.
                // Once a final is received, preview-only traffic cannot replace or invalidate that result.
                if (final != null || (frame.TryGetValue("step_ix", out var step) && step is < 5L or < 5UL)) return;
            }
            if (!frame.TryGetValue("image", out var value)) throw new InvalidDataException("Image stream frame has no image.");
            byte[] bytes = value switch
            {
                byte[] binary => binary,
                string base64 => Convert.FromBase64String(base64),
                _ => throw new InvalidDataException("Invalid stream image field."),
            };
            if (bytes.Length > NovelAIImageResponse.MaximumImageBytes || !decodable(bytes))
                throw new InvalidDataException("Stream image is not decodable.");
            if (type == "final")
            {
                // A single sample may repeat its final frame; keep the latest validated result.
                final = bytes;
            }
            else
            {
                progress?.Report(bytes);
            }
        }
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var item in NovelAIStreamFrames.ReadSseAsync(stream, ct))
            {
                if (item.Data == "[DONE]") continue;
                using var document = JsonDocument.Parse(item.Data);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid SSE image frame.");
                var map = new Dictionary<string, object?>();
                foreach (var property in document.RootElement.EnumerateObject())
                    map.Add(property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() :
                        property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out long number) ? number : property.Value.Clone());
                // Gateway SSE compatibility uses a named event or a JSON type discriminator; native maps use event_type only.
                if (map.TryGetValue("type", out var sseType))
                {
                    if (map.TryGetValue("event_type", out var explicitType) && !Equals(explicitType, sseType))
                        throw new InvalidDataException("Conflicting SSE event discriminators.");
                    map["event_type"] = sseType;
                }
                if (item.Event.Length > 0 && map.TryGetValue("event_type", out var namedType) && !Equals(namedType, item.Event))
                    throw new InvalidDataException("Conflicting named SSE event discriminator.");
                Process(map, item.Event);
            }
        }
        else
        {
            // Official requestStream selects msgpack on the request and parses framing without MIME negotiation.
            // Reject explicit non-stream error representations; missing/generic/unknown MIME still uses the selected protocol.
            if (mediaType != null && (mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Non-image representation returned by image stream.");
            await foreach (var frame in NovelAIStreamFrames.ReadMessagePackAsync(stream, ct)) Process(frame);
        }
        return final ?? throw new InvalidDataException("Image stream ended without a final image; billing outcome may be unknown.");
    }
}
