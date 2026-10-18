using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace NAITool.Services;

/// <summary>Content-addressed reference payloads kept outside the small settings document.</summary>
internal static class ReferenceDraftStore
{
    private sealed record BlobKey(string Value);
    private static readonly ConditionalWeakTable<string, BlobKey> Keys = new();
    private static string DirectoryPath(string appRoot) => Path.Combine(appRoot, "user", "reference-drafts");

    internal static string Save(string appRoot, string base64)
    {
        var key = Keys.GetValue(base64, value =>
            new BlobKey(Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(value))))).Value;
        string directory = DirectoryPath(appRoot);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, key);
        if (!File.Exists(path))
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, Convert.FromBase64String(base64));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        return key;
    }

    internal static string? Load(string appRoot, string? key)
    {
        if (key == null || key.Length != 64 || !key.All(Uri.IsHexDigit)) return null;
        string path = Path.Combine(DirectoryPath(appRoot), key.ToLowerInvariant());
        if (!File.Exists(path)) return null;
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), key, StringComparison.OrdinalIgnoreCase))
            return null;
        string base64 = Convert.ToBase64String(bytes);
        Keys.Add(base64, new BlobKey(key.ToLowerInvariant()));
        return base64;
    }

    internal static void Clear(string appRoot)
    {
        string directory = DirectoryPath(appRoot);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
