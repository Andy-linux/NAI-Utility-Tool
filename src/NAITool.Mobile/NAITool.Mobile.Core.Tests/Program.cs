using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NAITool.Mobile.Core;
using NAITool.Services;

int count = 0;
static void Check(bool good, string message)
{
    if (!good) throw new Exception("FAILED: " + message);
}
void Passed(string message)
{
    Console.WriteLine("PASS: " + message);
    count++;
}
var p = new NAIParameters { Model = "nai-diffusion-5-full", Steps = 23, Effort = "high", Seed = "0" };
var sample = new MobileGenerationClient.Request(
    "https://gateway.example/api/novelai", "unused-test-key", "a girl with a hat",
    "bad quality", p, 832, 1216,
    [new MobileGenerationClient.Character("red eyes", "different pupils")]);

using (var json = JsonDocument.Parse(MobileGenerationClient.BuildRequestJson(sample)))
{
    var root = json.RootElement;
    var fields = root.GetProperty("parameters");
    Check(root.GetProperty("model").GetString() == "nai-diffusion-5-full", "V5 model");
    Check(fields.GetProperty("params_version").GetInt32() == 3, "parameter protocol");
    Check(fields.GetProperty("steps").GetInt32() == 23, "high steps");
    Check(fields.GetProperty("v4_prompt").GetProperty("caption").GetProperty("char_captions").GetArrayLength() == 1,
        "V5 character prompt");
    Check(fields.GetProperty("tag_hint_qt").GetInt32() == 1, "quality tag ID");
    Check(!root.ToString().Contains("unused-test-key"), "key not serialized");
}
Passed("desktop V5 request fields + character + quality");

var pm = new NAIParameters
{
    Model = "nai-diffusion-5-full", Steps = 40, Sampler = "k_dpmpp_2m", CfgRescale = 0.8, Scale = 4.5, Effort = "medium"
};
using (var json = JsonDocument.Parse(MobileGenerationClient.BuildRequestJson(sample with
{
    Parameters = pm, NegativePrompt = "user secret negative", Characters = []
})))
{
    var fields = json.RootElement.GetProperty("parameters");
    Check(json.RootElement.GetProperty("model").GetString() == "nai-diffusion-5-full-medium", "medium model");
    Check(fields.GetProperty("steps").GetInt32() == 14, "medium forces 14 steps");
    Check(fields.GetProperty("sampler").GetString() == "k_euler_ancestral", "medium forces sampler");
    Check(!fields.TryGetProperty("cfg_rescale", out _), "medium removes CFG rescale");
    Check(fields.GetProperty("scale").GetDouble() == 4.5, "medium keeps prompt guidance editable");
    Check(!fields.GetProperty("negative_prompt").ToString().Contains("user secret negative"), "medium disables custom UC");
    Check(pm.Steps == 40 && pm.Sampler == "k_dpmpp_2m" && pm.CfgRescale == 0.8,
        "original high draft remains untouched");
}
Passed("V5 Medium wire constraints and High draft preservation");

Check(MobileGenerationClient.ResolveUrl("") == "https://image.novelai.net/ai/generate-image", "official endpoint");
Check(MobileGenerationClient.ResolveUrl("https://gateway.example/api/novelai") ==
      "https://gateway.example/api/novelai/ai/generate-image", "custom root");
Check(MobileGenerationClient.ResolveUrl("https://gateway.example/api/novelai/ai/generate-image") ==
      "https://gateway.example/api/novelai/ai/generate-image", "legacy full URL");
Check(MobileGenerationClient.ResolveUrl("https://gateway.example/api/novelai", true).EndsWith("/ai/generate-image-stream"),
      "custom stream endpoint");
Passed("official/custom/legacy/stream exact origin boundaries");

byte[] image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+Yt3QAAAAASUVORK5CYII=");
byte[] Zip(byte[] bytes, bool duplicate = false)
{
    using var buffer = new MemoryStream();
    using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
    {
        var first = archive.CreateEntry("image.png");
        using (var stream = first.Open()) stream.Write(bytes);
        if (duplicate)
        {
            var second = archive.CreateEntry("image2.png");
            using var stream = second.Open();
            stream.Write(bytes);
        }
        var ignored = archive.CreateEntry("metadata.json");
        using var other = ignored.Open();
        other.Write(Encoding.UTF8.GetBytes("{\"a\":1}"));
    }
    return buffer.ToArray();
}

var mock = new FakeHandler((request, ct) =>
{
    Check(request.RequestUri!.Host == "gateway.example", "custom URL cannot fallback to official");
    Check(request.Headers.Authorization?.Scheme == "Bearer", "Bearer authorization");
    Check(request.Headers.Authorization?.Parameter == "unused-test-key", "token delivered only to requested gateway");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Zip(image))
    });
});
using (var http = new HttpClient(mock))
{
    var received = await new MobileGenerationClient(http).GenerateAsync(sample);
    Check(received.SequenceEqual(image), "single ZIP image");
    Check(mock.RequestCount == 1, "single charged request attempt");
}
Passed("mock gateway ZIP generation and no duplicate calls");

using (var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = new ByteArrayContent(Zip(image, duplicate: true))
}))))
{
    try
    {
        await new MobileGenerationClient(http).GenerateAsync(sample);
        throw new Exception("expected duplicate-image failure");
    }
    catch (InvalidDataException) { }
}
Passed("ambiguous ZIP with two images is rejected");

using (var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
{
    Content = new StringContent("401")
}))))
{
    try
    {
        await new MobileGenerationClient(http).GenerateAsync(sample);
        throw new Exception("expected HTTP 401");
    }
    catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized) { }
}
Passed("HTTP failures do not become fake generation success");

byte[] MsgPack(string eventType)
{
    using var outStream = new MemoryStream();
    using var map = new MemoryStream();
    map.WriteByte(0x82); // two keys: event_type, image
    map.WriteByte(0xaa); map.Write(Encoding.UTF8.GetBytes("event_type"));
    map.WriteByte((byte)(0xa0 + eventType.Length)); map.Write(Encoding.UTF8.GetBytes(eventType));
    map.WriteByte(0xa5); map.Write(Encoding.UTF8.GetBytes("image"));
    map.WriteByte(0xc4); map.WriteByte((byte)image.Length); map.Write(image);
    var raw = map.ToArray();
    outStream.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(raw.Length)));
    outStream.Write(raw);
    return outStream.ToArray();
}
using (var http = new HttpClient(new FakeHandler((_, _) =>
{
    var content = new ByteArrayContent(MsgPack("final"));
    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
})))
{
    var received = await new MobileGenerationClient(http).GenerateAsync(sample with { Stream = true });
    Check(received.SequenceEqual(image), "MessagePack final decoded");
}
Passed("native length-prefixed MessagePack final frame");

using (var http = new HttpClient(new FakeHandler((_, _) =>
{
    var content = new ByteArrayContent(MsgPack("intermediate"));
    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
})))
{
    try
    {
        await new MobileGenerationClient(http).GenerateAsync(sample with { Stream = true });
        throw new Exception("expected no-final failure");
    }
    catch (InvalidDataException) { }
}
Passed("intermediate-only stream cannot save a charged result");

var inpaint = sample with
{
    Parameters = new NAIParameters
    {
        Model = "nai-diffusion-5-full-inpainting", Steps = 23, Effort = "high",
        InpaintStrength = 0.72, InpaintNoise = 0.25,
    },
    InpaintImageBase64 = "UklGRmFrZWltYWdl",
    InpaintMaskBase64 = "UklGRmFrZW1hc2s=",
};
using (var json = JsonDocument.Parse(MobileGenerationClient.BuildRequestJson(inpaint)))
{
    var root = json.RootElement;
    var fields = root.GetProperty("parameters");
    Check(root.GetProperty("action").GetString() == "infill", "inpaint action");
    Check(root.GetProperty("model").GetString() == "nai-diffusion-5-full-inpainting", "inpaint model");
    Check(fields.GetProperty("image").GetString() == inpaint.InpaintImageBase64, "WebP source base64");
    Check(fields.GetProperty("mask").GetString() == inpaint.InpaintMaskBase64, "WebP mask base64");
    Check(fields.GetProperty("add_original_image").GetBoolean() == false, "desktop inpaint composite mode");
    Check(fields.GetProperty("strength").GetDouble() == 0.72, "inpaint strength");
    Check(fields.GetProperty("noise").GetDouble() == 0.25, "inpaint noise");
}
Passed("inpaint wire format matches the dev request action/strength/image/mask");

using (var json = JsonDocument.Parse(MobileGenerationClient.BuildRequestJson(inpaint with
{
    Parameters = new NAIParameters
    {
        Model = "nai-diffusion-5-full-inpainting", Effort = "medium",
        Steps = 36, Sampler = "k_dpmpp_2m", CfgRescale = 0.5,
    }
})))
{
    var root = json.RootElement;
    var fields = root.GetProperty("parameters");
    Check(root.GetProperty("model").GetString() == "nai-diffusion-5-full-medium-inpainting", "medium inpaint model");
    Check(fields.GetProperty("steps").GetInt32() == 14, "medium inpaint steps");
    Check(!fields.TryGetProperty("cfg_rescale", out _), "medium inpaint CFG Rescale unsupported");
}
Passed("Medium inpainting model and sampler/cfg restrictions");

try
{
    _ = MobileGenerationClient.BuildRequestJson(inpaint with { InpaintMaskBase64 = null });
    throw new Exception("expected missing mask rejection");
}
catch (ArgumentException) { }
Passed("inpainting rejects incomplete image/mask pairs");

Check(MobileGenerationClient.MaxCharactersForModel("nai-diffusion-5-full") == 22, "V5 22 roles");
Check(MobileGenerationClient.MaxCharactersForModel("nai-diffusion-4-5-full") == 6, "V4.5 6 roles");
Check(MobileGenerationClient.MaxCharactersForModel("nai-diffusion-3") == 0, "V3 no multi-role captions");
var manyRoles = Enumerable.Range(0, 22).Select(i => new MobileGenerationClient.Character(
    "role " + i, "", 0.25, 0.75, true)).ToArray();
using (var json = JsonDocument.Parse(MobileGenerationClient.BuildRequestJson(sample with { Characters = manyRoles })))
{
    var captions = json.RootElement.GetProperty("parameters")
        .GetProperty("v4_prompt").GetProperty("caption").GetProperty("char_captions");
    Check(captions.GetArrayLength() == 22, "22 role entries on V5");
    Check(captions[0].GetProperty("centers")[0].GetProperty("x").GetDouble() == 0.25,
          "custom role position X");
}
Passed("V5 22-role request with positions");

try
{
    _ = MobileGenerationClient.BuildRequestJson(sample with
    {
        Parameters = new NAIParameters { Model = "nai-diffusion-4-5-full" },
        Characters = manyRoles
    });
    throw new Exception("expected V4.5 role limit");
}
catch (ArgumentException) { }
Passed("role limit prevents unsupported V4.5 request");

Console.WriteLine($"ALL {count} MOBILE CORE TESTS PASSED");

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    public int RequestCount { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestCount++;
        return handler(request, ct);
    }
}
