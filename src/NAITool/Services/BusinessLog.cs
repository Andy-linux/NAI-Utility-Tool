using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NAITool.Services;

// Local opt-in business diagnostics. Response bytes are observed by the existing reader,
// held in memory, then redacted before any persistence (including partial streams).
internal sealed partial class BusinessLog : IDisposable
{
    private static readonly AsyncLocal<BusinessLog?> Ambient = new();
    internal static BusinessLog? Current => Ambient.Value;
    private readonly BusinessLog? parent;
    private readonly string root;
    private readonly string[] secrets;
    private readonly bool enabled;
    internal readonly bool Debug;
    internal readonly string GenerationId;
    private readonly string snapshot;
    private readonly string callSnapshot;
    private RequestCapture? request;
    private readonly bool unassociatedUiReport;
    internal BusinessLog(bool enabled, bool debug, string root, IEnumerable<string> secrets, object input, bool unassociatedUiReport = false)
    {
        parent = Current;
        this.unassociatedUiReport = unassociatedUiReport && parent == null;
        this.enabled = parent?.enabled ?? enabled;
        Debug = parent?.Debug ?? debug;
        this.root = root;
        this.secrets = secrets.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray();
        GenerationId = parent?.GenerationId ?? (this.unassociatedUiReport ? "ui-" + DateTime.UtcNow.ToString("yyyy-MM-dd") : Guid.NewGuid().ToString("N"));
        callSnapshot = this.enabled ? Serialize(input) : "{}";
        snapshot = parent?.snapshot ?? callSnapshot;
        Ambient.Value = this;
        Stage("preparation_start", input);
    }
    private static string Serialize(object? value)
    {
        try { return JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true }); }
        catch { return "\"snapshot serialization failed\""; }
    }
    private static readonly Regex CredentialField = new("^(authorization|cookie|set-cookie|api[_-]?key|access[_-]?token|refresh[_-]?token|token)$", RegexOptions.IgnoreCase);
    private string RedactJsonStrings(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            bool changed = false;
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output)) Write(document.RootElement, writer);
            return changed ? Encoding.UTF8.GetString(output.ToArray()) : text;
            void Write(JsonElement element, Utf8JsonWriter writer)
            {
            if (element.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var field in element.EnumerateObject())
                {
                    writer.WritePropertyName(field.Name);
                    if (CredentialField.IsMatch(field.Name)) { changed = true; writer.WriteStringValue("[REDACTED]"); }
                    else Write(field.Value, writer);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            { writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(item, writer); writer.WriteEndArray(); }
            else if (element.ValueKind == JsonValueKind.String)
            {
                string value = element.GetString() ?? "";
                string trimmed = value.TrimStart();
                // Raw Info is often JSON nested in a JSON string. Sanitize that structure too.
                if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) value = RedactJsonStrings(value);
                string clean = RedactPlain(value);
                if (clean != element.GetString()) changed = true;
                writer.WriteStringValue(clean);
            }
            else element.WriteTo(writer);
            }
        }
        catch (JsonException) { return text; }
    }
    internal string Redact(string text) => RedactPlain(RedactJsonStrings(text));
    private string RedactPlain(string text)
    {
        foreach (string secret in secrets)
        {
            text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            string escaped = JsonSerializer.Serialize(secret)[1..^1];
            text = text.Replace(escaped, "[REDACTED]", StringComparison.Ordinal);
        }
        text = Regex.Replace(text, "(?i)(\\\"(?:authorization|cookie|set-cookie|api[_-]?key|access[_-]?token|refresh[_-]?token|token)\\\"\\s*:\\s*)\\\"(?:\\\\.|[^\\\"])*\\\"", "$1\"[REDACTED]\"");
        text = Regex.Replace(text, "(?im)(^[ \\t]*(?:authorization|cookie|set-cookie|api[_-]?key|access[_-]?token|refresh[_-]?token)\\s*[:=]\\s*)[^\\r\\n]+", "$1[REDACTED]");
        text = Regex.Replace(text, "(?i)(Bearer\\s+)[A-Za-z0-9._~+/-]+=*", "$1[REDACTED]");
        text = Regex.Replace(text, "(?i)([?&](?:api[_-]?key|token|access_token)=)[^&\\s\\\"]+", "$1[REDACTED]");
        return text;
    }
    internal void Stage(string stage, object? data = null)
    {
        if (!enabled || !Debug) return;
        try
        {
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, GenerationId + ".events.jsonl"),
                Redact(Serialize(new { generation_id = unassociatedUiReport ? null : GenerationId, request_id = request?.Id, time = DateTimeOffset.UtcNow, stage, data })) + "\n");
        }
        catch { /* Logging must never change the business result. */ }
    }
    internal HttpContent Content(object payload)
    {
        // The exact representation is serialized once and is shared by sending and capture.
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        request = new RequestCapture(this, bytes);
        if (parent != null) parent.request = request;
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        Stage("resolved_payload", payload);
        return content;
    }
    internal void Dispatch(HttpRequestMessage message)
    {
        request?.Dispatch(message);
        Stage("dispatch", new { url = message.RequestUri?.ToString(), method = message.Method.Method });
    }
    internal void Headers(HttpResponseMessage response)
    {
        try { request?.Headers(response); } catch { }
        Stage("headers_received", new { http_status = (int)response.StatusCode, type = response.Content.Headers.ContentType?.ToString() });
        try { if (enabled && request != null) response.Content = new ObservedContent(response.Content, request); }
        catch { Stage("capture_setup_failed"); }
    }
    public void Dispose()
    {
        request?.Finish(false, "operation_end");
        Stage("operation_end");
        Ambient.Value = parent;
    }
    private static readonly object SimpleLock = new();
    private string SimpleSettings(byte[] outgoing)
    {
        // Only known textual/scalar generation settings. Never retain arbitrary image/reference fields.
        var fields = new Dictionary<string, object?>();
        using var doc = JsonDocument.Parse(outgoing);
        void Select(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return;
            foreach (var field in element.EnumerateObject())
            {
                if (field.Name == "parameters") { Select(field.Value); continue; }
                if (!new[] { "input", "prompt", "negative_prompt", "model", "action", "seed", "width", "height", "steps", "scale", "sampler", "noise_schedule", "cfg_rescale", "qualityToggle", "ucPreset", "sm", "sm_dyn" }.Contains(field.Name)) continue;
                if (field.Value.ValueKind == JsonValueKind.String)
                {
                    string value = Redact(field.Value.GetString() ?? "");
                    if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || (value.Length > 256 && !value.Any(char.IsWhiteSpace) && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')))
                        value = "[payload omitted]";
                    fields[field.Name] = value.Length > 512 ? value[..512] + "…" : value;
                }
                else if (field.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)
                    fields[field.Name] = field.Value.Clone();
            }
        }
        Select(doc.RootElement);
        return Redact(Serialize(fields));
    }
    private void SaveSimple(object record)
    {
        lock (SimpleLock)
        {
            string directory = Path.Combine(root, "simple");
            Directory.CreateDirectory(directory);
            var today = DateTime.UtcNow.Date;
            string path = Path.Combine(directory, today.ToString("yyyy-MM-dd") + ".jsonl");
            File.AppendAllText(path, Redact(Serialize(record)) + "\n", Encoding.UTF8);
            // Own only exact daily filenames inside Simple; never sweep Debug or output.
            var files = new DirectoryInfo(directory).EnumerateFiles("*.jsonl").Where(f =>
                DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                .OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            foreach (var file in files.ToArray())
                if (DateTime.ParseExact(Path.GetFileNameWithoutExtension(file.Name), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) < today.AddDays(-6))
                { file.Delete(); files.Remove(file); }
            long total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= 8 * 1024 * 1024) break;
                total -= file.Length;
                file.Delete();
            }
        }
    }
    private sealed class RequestCapture
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        private readonly BusinessLog owner;
        private readonly byte[] outgoing;
        private readonly MemoryStream body = new();
        private long observedLength;
        private int? status;
        private string? type;
        private bool finished, captureFailed;
        private DateTimeOffset prepared = DateTimeOffset.UtcNow;
        private DateTimeOffset? dispatched;
        private string? url, method, requestContentType;
        internal void Dispatch(HttpRequestMessage message)
        {
            dispatched = DateTimeOffset.UtcNow;
            url = owner.Redact(message.RequestUri?.ToString() ?? "");
            method = message.Method.Method;
            requestContentType = message.Content?.Headers.ContentType?.ToString();
        }
        private DateTimeOffset? headersReceived;
        internal RequestCapture(BusinessLog owner, byte[] outgoing) { this.owner = owner; this.outgoing = outgoing; }
        internal void Headers(HttpResponseMessage response) { headersReceived = DateTimeOffset.UtcNow; status = (int)response.StatusCode; type = response.Content.Headers.ContentType?.ToString(); }
        internal void Observe(ReadOnlySpan<byte> bytes)
        {
            try { observedLength += bytes.Length; if (owner.Debug) body.Write(bytes); } catch { captureFailed = true; }
        }
        // Binary bodies use reversible Latin1 for exact replacement of credential byte sequences.
        // Secret-free bytes stay exact; changed bodies are explicitly labelled sanitized artifacts.
        private byte[] Sanitize(byte[] bytes, out bool changed)
        {
            bytes = owner.SanitizeArtifact(bytes, out bool structuredChanged);
            string original = Encoding.Latin1.GetString(bytes), clean = original;
            foreach (string secret in owner.secrets)
                foreach (string variant in new[] { secret, JsonSerializer.Serialize(secret)[1..^1] })
                    clean = clean.Replace(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(variant)), "[REDACTED]", StringComparison.Ordinal);
            // Do not reserialize raw entities: exact business representation remains intact.
            clean = owner.RedactPlain(clean);
            changed = structuredChanged || clean != original;
            return Encoding.Latin1.GetBytes(clean);
        }
        internal void Finish(bool eof, string termination)
        {
            if (finished) return;
            finished = true;
            bool complete = eof && !captureFailed;
            if (!owner.enabled || (!owner.Debug && !(status is >= 200 and <= 299 && complete))) return;
            try
            {
                if (!owner.Debug)
                {
                    owner.SaveSimple(new { time = DateTimeOffset.UtcNow, generation_id = owner.GenerationId, request_id = Id,
                        url = url is { Length: > 512 } ? url[..512] : url, method, http_status = status,
                        body_complete = complete, termination, response_type = type, observed_length = observedLength,
                        request_length = outgoing.Length, settings = JsonDocument.Parse(owner.SimpleSettings(outgoing)).RootElement });
                    return;
                }
                byte[] requestBytes = Sanitize(outgoing, out bool requestRedacted);
                byte[]? responseBytes = null;
                bool responseRedacted = false;
                string? redactionError = null;
                try { responseBytes = Sanitize(body.ToArray(), out responseRedacted); }
                catch (Exception ex) { redactionError = owner.Redact(ex.ToString()); }
                string dir = Path.Combine(owner.root, owner.GenerationId, Id);
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "request.bin"), requestBytes);
                if (responseBytes != null) File.WriteAllBytes(Path.Combine(dir, "response.bin"), responseBytes);
                File.WriteAllText(Path.Combine(dir, "transaction.json"), owner.Redact(Serialize(new
                {
                    generation_id = owner.GenerationId, request_id = Id, preparation_start = prepared,
                    url, method, request_content_type = requestContentType,
                    dispatch_time = dispatched, headers_received = headersReceived, body_termination_time = DateTimeOffset.UtcNow,
                    input_snapshot = JsonDocument.Parse(owner.snapshot).RootElement,
                    effective_call_inputs = JsonDocument.Parse(owner.callSnapshot).RootElement,
                    http_status = status, body_complete = complete, termination, capture_failed = captureFailed,
                    response_type = type, observed_length = body.Length, stored_length = responseBytes?.Length ?? 0,
                    response_artifact_saved = responseBytes != null, redaction_failed = redactionError != null, redaction_error = redactionError,
                    request_length = outgoing.Length,
                    representation = "HTTP entity bytes delivered to application reader, not TLS/on-wire bytes",
                    request_artifact = requestRedacted ? "sanitized byte-derived" : "unmodified entity bytes",
                    response_artifact = responseBytes == null ? "not saved: redaction failed" : responseRedacted ? "sanitized byte-derived" : "unmodified entity bytes",
                    redactions = new { request = requestRedacted, response = responseRedacted, rules = "configured credentials/decoded JSON strings; credential fields in nested JSON, MessagePack maps/frames and ZIP/image metadata; Authorization/Cookie/API key/exact Token fields; Bearer values; credential URL query values" }
                })));
            }
            catch { owner.Stage("capture_storage_failure", new { body_complete = false }); }
        }
    }
    private sealed class ObservedContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly RequestCapture capture;
        internal ObservedContent(HttpContent inner, RequestCapture capture)
        {
            this.inner = inner; this.capture = capture;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length) { length = inner.Headers.ContentLength ?? 0; return inner.Headers.ContentLength.HasValue; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { using var source = await CreateContentReadStreamAsync(); await source.CopyToAsync(stream); }
        protected override async Task<Stream> CreateContentReadStreamAsync() => new ObservedStream(await inner.ReadAsStreamAsync(), capture);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) => new ObservedStream(await inner.ReadAsStreamAsync(ct), capture);
        protected override void Dispose(bool disposing) { if (disposing) { capture.Finish(false, "reader_terminated_before_eof"); inner.Dispose(); } base.Dispose(disposing); }
    }
    private sealed class ObservedStream(Stream inner, RequestCapture capture) : Stream
    {
        private void ReadObserved(ReadOnlySpan<byte> bytes) { if (bytes.Length == 0) capture.Finish(true, "eof"); else capture.Observe(bytes); }
        public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); if (count > 0) ReadObserved(buffer.AsSpan(offset, n)); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try { int n = await inner.ReadAsync(buffer, ct); if (buffer.Length > 0) ReadObserved(buffer.Span[..n]); return n; }
            catch (Exception ex) { capture.Finish(false, ex.GetType().Name); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) { capture.Finish(false, "reader_terminated_before_eof"); inner.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
