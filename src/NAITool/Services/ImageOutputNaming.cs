using System.Text;

namespace NAITool.Services;

internal static class ImageOutputNaming
{
    internal static string Extension(byte[] bytes) => bytes.Length >= 12 &&
        Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP" ? ".webp" : ".png";

    internal static string Stem(string prompt, string seed)
    {
        string name = string.Concat(prompt.EnumerateRunes().Take(80).Select(r => r.ToString())) + " s-" + seed;
        foreach (char invalid in "\r\n\"%*/:<>?\\|") name = name.Replace(invalid, '_');
        // Windows additionally rejects control characters; seed is not allowed to create a path.
        return string.Concat(name.Select(c => char.IsControl(c) ? '_' : c));
    }

    internal static async Task<string> SaveAsync(string directory, string stem, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        for (int suffix = 0; ; suffix++)
        {
            string path = Path.Combine(directory, stem + (suffix == 0 ? "" : $" ({suffix})") + Extension(bytes));
            FileStream stream;
            try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true); }
            catch (IOException) when (File.Exists(path)) { continue; }
            await using (stream) await stream.WriteAsync(bytes);
            return path;
        }
    }
}
