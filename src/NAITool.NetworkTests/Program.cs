using NAITool.Services;
using System.IO.Compression;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { passed++; return; } throw new Exception(name); }
foreach (string root in new[] { "https://gateway.test/api/novelai", "https://gateway.test/api/novelai/ai/generate-image", "https://gateway.test/api/novelai/ai/generate-image/" })
{
    var resolver = new ApiEndpointResolver(root);
    foreach (string path in new[] { "/ai/generate-image", "/ai/generate-image-stream", "/ai/encode-vibe", "/ai/upscale", "/ai/augment-image", "/user/information", "/user/data", "/oa/v1/chat/completions" })
        Check(resolver.Resolve(path) == "https://gateway.test/api/novelai" + path, "custom path " + path);
}
Check(new ApiEndpointResolver("https://gateway.test/ai/generate-image-extra").GenerateUrl.EndsWith("/ai/generate-image-extra/ai/generate-image"), "exact suffix only");
Check(new ApiEndpointResolver("").TextChatCompletionUrl == "https://text.novelai.net/oa/v1/chat/completions", "official text");
foreach (string invalid in new[] { "gateway.test", "https://key:secret@gateway.test", "https://gateway.test/?key=secret", "https://gateway.test/#x", "file:///tmp/foo" }) Reject(() => new ApiEndpointResolver(invalid), "invalid root");
Check(NovelAIRequestPolicy.Budget(120, 300).TotalSeconds == 420, "waiting generation budget");
Check(NovelAIRequestPolicy.Budget(120, 30, true).TotalSeconds == 30, "readonly independent");
Check(!NovelAIRequestPolicy.Exception(new Exception("Bearer synthetic-secret")).Contains("synthetic-secret"), "safe exception");
var paidV5 = new ApiRequestCost(true, 30, OpusFreeEligible: true);
var account = new ApiAccount { Token = "synthetic", Info = new NovelAiAccountInfo { IsOpus = true, HasActiveSubscription = true, V5UsagePercent = 0, V5UsageIsNegative = true, AnlasBalance = 100 } };
Check(ApiAccountRouter.GetCandidates(new[] { account }, paidV5).Length == 1, "exhausted V5 allows paid Anlas");
Check(paidV5.AnlasFor(account.Info) == 30, "exhausted V5 paid");
Check(!ApiRequestCost.Image("nai-diffusion-4-5-full", 1024, 1024, 23, characterReference: true).OpusFreeEligible, "precise reference excludes Opus base-free");
account.Info = new NovelAiAccountInfo { IsOpus = true, HasActiveSubscription = true, V5UsagePercent = 0, V5UsageIsNegative = false, AnlasBalance = 0 };
Check(paidV5.AnlasFor(account.Info) == 0, "percent zero not exhaustion");
account.Info = new NovelAiAccountInfo { IsOpus = true, HasActiveSubscription = true };
Check(paidV5.AnlasFor(account.Info) == 30, "unknown V5 not free");
Check(ApiQuotaTotals.From(new[] { account }).Anlas == null, "unknown not zero");
byte[] Zip(params (string Name, byte[] Bytes)[] entries)
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var item in entries) { using var stream = archive.CreateEntry(item.Name).Open(); stream.Write(item.Bytes); }
    return output.ToArray();
}
// Synthetic decoder predicate isolates archive selection; real SKBitmap decode is exercised in Windows application tests.
byte[] image = [1, 2, 3];
bool Decode(byte[] bytes) => bytes.SequenceEqual(image);
Check((await NovelAIImageResponse.ReadSingleAsync(Zip(("meta.json", [9]), ("bad.png", [4]), ("result.png", image)), Decode, default)).SequenceEqual(image), "ZIP valid result after metadata and invalid image");
try { await NovelAIImageResponse.ReadSingleAsync(Zip(("a.png", image), ("b.png", image)), Decode, default); throw new Exception("multiple images accepted"); } catch (InvalidDataException) { passed++; }
try { await NovelAIImageResponse.ReadSingleAsync(Zip(("bad.png", [4])), Decode, default); throw new Exception("invalid image accepted"); } catch (InvalidDataException) { passed++; }
try { await NovelAIImageResponse.ReadSingleAsync(Zip(Enumerable.Range(0, 129).Select(i => ($"bad{i}.png", new byte[] { 4 })).ToArray()), Decode, default); throw new Exception("entry limit ignored"); } catch (InvalidDataException) { passed++; }
try { await NovelAIImageResponse.ReadBoundedAsync(new MemoryStream(new byte[5]), default, 4); throw new Exception("byte limit ignored"); } catch (InvalidDataException) { passed++; }
byte[] packed = [0x83, 0xaa, 0x65, 0x76, 0x65, 0x6e, 0x74, 0x5f, 0x74, 0x79, 0x70, 0x65, 0xa5, 0x66, 0x69, 0x6e, 0x61, 0x6c, 0xa7, 0x73, 0x61, 0x6d, 0x70, 0x5f, 0x69, 0x78, 0, 0xa5, 0x69, 0x6d, 0x61, 0x67, 0x65, 0xc4, 3, 1, 2, 3];
Check((string?)NovelAIMessagePack.ReadMap(packed)["event_type"] == "final", "MessagePack string");
Check(((byte[])NovelAIMessagePack.ReadMap(packed)["image"]!).SequenceEqual(image), "MessagePack binary");
foreach (byte[] malformed in new[] { new byte[] { 0x81, 0x01, 0x01 }, packed.Concat(new byte[] { 0 }).ToArray(), Enumerable.Repeat((byte)0x91, 34).Concat(new byte[] { 0xc0 }).ToArray() })
{
    try { NovelAIMessagePack.ReadMap(malformed); throw new Exception("malformed MessagePack accepted"); } catch (InvalidDataException) { passed++; }
}
byte[] framed = new byte[packed.Length + 4];
System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(framed, packed.Length);
packed.CopyTo(framed, 4);
int frames = 0;
await foreach (var frame in NovelAIStreamFrames.ReadMessagePackAsync(new MemoryStream(framed), default)) frames++;
Check(frames == 1, "four-byte big endian framing");
try { await foreach (var frame in NovelAIStreamFrames.ReadMessagePackAsync(new MemoryStream(framed[..^1]), default)) { } throw new Exception("truncation accepted"); } catch (EndOfStreamException) { passed++; }
var events = new List<(string Event, string Data)>();
await foreach (var item in NovelAIStreamFrames.ReadSseAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(": comment\nevent: final\ndata: {\ndata: }\n\n")), default)) events.Add(item);
Check(events.Count == 1 && events[0].Event == "final" && events[0].Data == "{\n}", "multiline SSE dispatch");
MemoryStream Sse(string data) => new(System.Text.Encoding.UTF8.GetBytes(data));
string previewEvent = "data: {\"event_type\":\"intermediate\",\"image\":\"AQID\"}\n\n";
string finalEvent = "data: {\"event_type\":\"final\",\"image\":\"AQID\"}\n\n";
Check((await NovelAIImageStream.ReadAsync(Sse(previewEvent + finalEvent), "text/event-stream", Decode, null, default)).SequenceEqual(image), "SSE final accepted");
foreach (string data in new[] { previewEvent, previewEvent + "data: {\"event_type\":\"error\",\"message\":\"synthetic-secret\"}\n\n", finalEvent[..^1], finalEvent + finalEvent })
{
    try { await NovelAIImageStream.ReadAsync(Sse(data), "text/event-stream", Decode, null, default); throw new Exception("invalid stream accepted"); }
    catch (InvalidDataException) { passed++; }
}
Check((await NovelAIImageStream.ReadAsync(new MemoryStream(framed), "application/octet-stream", Decode, null, default)).SequenceEqual(image), "native framed final accepted");
try { await NovelAIImageStream.ReadAsync(Sse("data: {\"event_type\":\"final\",\"samp_ix\":1,\"image\":\"AQID\"}\n\n"), "text/event-stream", Decode, null, default); throw new Exception("unexpected sample accepted"); } catch (InvalidDataException) { passed++; }
Check((await NovelAIImageResponse.ReadSingleAsync(Zip(("thumbnail.png", image), ("image0.png", image)), Decode, default, name => name.StartsWith("image") && name.EndsWith(".png"))).SequenceEqual(image), "verified upscale ZIP result-name policy");
Check((await NovelAIImageStream.ReadAsync(Sse(finalEvent.Replace("event_type", "type")), "text/event-stream", Decode, null, default)).SequenceEqual(image), "legacy SSE type-only final");
try { await NovelAIImageStream.ReadAsync(Sse("data: {\"event_type\":\"final\",\"type\":\"intermediate\",\"image\":\"AQID\"}\n\n"), "text/event-stream", Decode, null, default); throw new Exception("conflicting discriminator accepted"); } catch (InvalidDataException) { passed++; }
foreach (string? mime in new string?[] { null, "application/x-native-stream", "application/binary" })
    Check((await NovelAIImageStream.ReadAsync(new MemoryStream(framed), mime, Decode, null, default)).SequenceEqual(image), "selected msgpack fallback " + mime);
foreach (string mime in new[] { "application/json", "text/html" })
{
    try { await NovelAIImageStream.ReadAsync(new MemoryStream(framed), mime, Decode, null, default); throw new Exception("explicit JSON/HTML accepted"); } catch (InvalidDataException) { passed++; }
}
Check(NovelAIRequestPolicy.ExceptionKey(new EndOfStreamException()) == "api.safe.invalid_response", "truncated stream classification");
Check(NovelAIRequestPolicy.Exception(new EndOfStreamException()).Contains("billing outcome may be unknown"), "truncated stream unknown billing");
var knownLow = new ApiAccount { Token = "low", Info = new NovelAiAccountInfo { AnlasBalance = 40 } };
var knownHigh = new ApiAccount { Token = "high", Info = new NovelAiAccountInfo { AnlasBalance = 90 } };
Check(new ApiAccountRouter().Select(new[] { knownLow, knownHigh }, ApiCallMode.MostQuota, new ApiRequestCost(false, 30), customEndpoint: true) == knownHigh, "custom most known quota");
Check(new ApiAccountRouter().Select(new[] { knownHigh, knownLow }, ApiCallMode.LeastQuota, new ApiRequestCost(false, 30), customEndpoint: true) == knownLow, "custom least known quota");
var freeAccount = new ApiAccount { Token = "free", Info = new NovelAiAccountInfo { IsOpus = true, HasActiveSubscription = true } };
Check(new ApiAccountRouter().Select(new[] { knownHigh, freeAccount }, ApiCallMode.RoundRobin, new ApiRequestCost(false, 30, OpusFreeEligible: true), customEndpoint: true) == freeAccount, "custom known free priority");
byte[] unsignedPacked = packed[..26].Concat(new byte[] { 0xcf, 0, 0, 0, 0, 0, 0, 0, 0 }).Concat(packed[27..]).ToArray();
byte[] unsignedFramed = new byte[unsignedPacked.Length + 4];
System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(unsignedFramed, unsignedPacked.Length); unsignedPacked.CopyTo(unsignedFramed, 4);
Check((await NovelAIImageStream.ReadAsync(new MemoryStream(unsignedFramed), null, Decode, null, default)).SequenceEqual(image), "native uint64 zero sample index");
foreach (byte[] invalidSample in new[] { new byte[] { 0xcb, 0, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 0xa1, 0x30 }, new byte[] { 1 } })
{
    byte[] invalidPacked = packed[..26].Concat(invalidSample).Concat(packed[27..]).ToArray();
    byte[] invalidFramed = new byte[invalidPacked.Length + 4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(invalidFramed, invalidPacked.Length); invalidPacked.CopyTo(invalidFramed, 4);
    try { await NovelAIImageStream.ReadAsync(new MemoryStream(invalidFramed), null, Decode, null, default); throw new Exception("invalid native sample accepted"); } catch (InvalidDataException) { passed++; }
}
Console.WriteLine($"PASS {passed} network assertions (synthetic local data only)");

namespace NAITool.Services
{
    // Calculator-independent router fixtures. No real protocol/price is asserted by these stubs.
    internal static class NovelAiAnlasCalculator
    {
        internal static bool IsV5Model(string model) => model.Contains("-5");
        internal static int PaidBaseCost(string model, int width, int height, int steps, bool sm, double strength, bool smDyn = false) => 30;
    }
}
