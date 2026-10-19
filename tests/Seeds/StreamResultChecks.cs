using System.Reflection;
using System.Text;
using System.Text.Json;
using NAITool.Services;

internal static class StreamResultChecks
{
    public static async Task RunAsync()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var reader = typeof(NovelAIService).Assembly.GetType("NAITool.Services.NovelAIImageStream")!.GetMethod("ReadAsync", flags)!;
        var decodable = typeof(NovelAIService).GetMethod("IsDecodableImage", flags)!.CreateDelegate<Func<byte[], bool>>();
        byte[] first = TestImage.CreatePng();
        byte[] last = ImageMetadataService.ReplacePngComment(first, "{\"fixture\":\"last final\"}");
        int checks = 0;
        var failures = new List<string>();

        // Exercise the built receiver and image decoder. SSE is the gateway transport into the shared frame handler.
        async Task<byte[]> Read(string frames, List<byte[]> previews, CancellationToken ct = default)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(frames));
            var task = (Task<byte[]>)reader.Invoke(null, [stream, "text/event-stream", decodable, new Capture(previews), ct])!;
            return await task;
        }
        async Task Result(string name, string frames, byte[] expected, params byte[][] expectedPreviews)
        {
            checks++;
            var previews = new List<byte[]>();
            try
            {
                byte[] result = await Read(frames, previews);
                if (!result.AsSpan().SequenceEqual(expected) || previews.Count != expectedPreviews.Length ||
                    previews.Where((image, index) => !image.AsSpan().SequenceEqual(expectedPreviews[index])).Any())
                    failures.Add(name + ": unexpected final or preview sequence");
            }
            catch (Exception error) { failures.Add(name + ": " + error.Message); }
        }
        async Task Fails<T>(string name, string frames, CancellationToken ct = default) where T : Exception
        {
            checks++;
            try { await Read(frames, [], ct); failures.Add(name + ": unexpectedly succeeded"); }
            catch (T) { }
            catch (Exception error) { failures.Add(name + ": " + error.GetType().Name); }
        }
        string Frame(string type, byte[]? image = null, int? step = null)
        {
            var data = new Dictionary<string, object?> { ["event_type"] = type, ["samp_ix"] = 0 };
            if (image != null) data["image"] = Convert.ToBase64String(image);
            if (step.HasValue) data["step_ix"] = step.Value;
            return "data: " + JsonSerializer.Serialize(data) + "\n\n";
        }

        await Result("default preview warmup", Frame("intermediate", first, 2) + Frame("intermediate", first, 4) +
            Frame("intermediate", last, 5) + Frame("final", first), first, last);
        await Result("gateway without step metadata", Frame("intermediate", first) + Frame("final", last), last, first);
        await Result("preview after final", Frame("final", last) + Frame("intermediate", first, 10), last);
        await Result("latest final for sample zero", Frame("final", first) + Frame("final", last), last);
        await Fails<InvalidDataException>("preview cannot become final", Frame("intermediate", first, 5));
        await Fails<InvalidDataException>("explicit server error remains fatal", Frame("error") + Frame("final", last));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Fails<OperationCanceledException>("canceled reception", Frame("final", last), canceled.Token);

        if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
        Console.WriteLine($"Passed {checks} stream result regression checks.");
    }

    private sealed class Capture(List<byte[]> previews) : IProgress<byte[]>
    {
        public void Report(byte[] value) => previews.Add(value);
    }
}
