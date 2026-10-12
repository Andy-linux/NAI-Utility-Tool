namespace NAITool.Services;

/// <summary>One credential boundary for every NovelAI operation. Only the exact legacy suffix is migrated.</summary>
internal sealed class ApiEndpointResolver
{
    private const string LegacySuffix = "/ai/generate-image";
    public bool IsCustom { get; }
    public string Identity { get; }
    private readonly string? _root;

    public ApiEndpointResolver(string? configuredRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            Identity = "official";
            return;
        }
        if (!Uri.TryCreate(configuredRoot.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("API root must be an absolute HTTP(S) URL without credentials, query or fragment.");
        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(LegacySuffix, StringComparison.Ordinal)) path = path[..^LegacySuffix.Length];
        _root = uri.GetLeftPart(UriPartial.Authority) + path;
        Identity = _root;
        IsCustom = true;
    }

    public string Resolve(string path)
    {
        if (!Paths.Contains(path)) throw new ArgumentException("Unsupported API operation.");
        if (IsCustom) return _root + path;
        string host = path.StartsWith("/oa/", StringComparison.Ordinal) ? "https://text.novelai.net" : "https://image.novelai.net";
        return host + path;
    }

    private static readonly HashSet<string> Paths = new(StringComparer.Ordinal)
    {
        "/ai/generate-image", "/ai/generate-image-stream", "/ai/encode-vibe", "/ai/upscale", "/ai/augment-image",
        "/user/information", "/user/data", "/user/subscription", "/oa/v1/chat/completions", "/ai/generate-image/suggest-tags",
    };
    public string GenerateUrl => Resolve("/ai/generate-image");
    public string GenerateStreamUrl => Resolve("/ai/generate-image-stream");
    public string EncodeVibeUrl => Resolve("/ai/encode-vibe");
    public string UpscaleUrl => Resolve("/ai/upscale");
    public string UserInfoUrl => Resolve("/user/information");
    public string UserDataUrl => Resolve("/user/data");
    public string TextChatCompletionUrl => Resolve("/oa/v1/chat/completions");
}
