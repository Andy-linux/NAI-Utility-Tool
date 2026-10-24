using Windows.Storage;
using NAITool.Services;

internal static class StorageFileChecks
{
    public static async Task RunAsync()
    {
        int checks = 0;
        void Check(bool condition, string scenario)
        {
            checks++;
            if (!condition) throw new Exception(scenario);
        }

        byte[] original = ImageMetadataService.ReplacePngComment(TestImage.CreatePng(),
            """{"prompt":"stream import","seed":4294967295,"sampler":"k_euler","steps":28}""");
        var virtualFile = await StorageFile.CreateStreamedFileAsync("browser-original.png", async request =>
        {
            using var stream = request.AsStreamForWrite();
            await stream.WriteAsync(original);
            await stream.FlushAsync();
        }, null);
        Check(virtualFile.Path.Length == 0, "The browser-style fixture must have no filesystem path");
        byte[] actual = await StorageFileReader.ReadAllBytesAsync(virtualFile);
        Check(actual.SequenceEqual(original), "Virtual imports must preserve every original byte");
        var metadata = ImageMetadataService.ReadFromBytes(actual);
        Check(metadata?.Seed == "4294967295" && metadata.PositivePrompt == "stream import",
            "A pathless file must reach the normal Info parser");

        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        try
        {
            await File.WriteAllBytesAsync(path, original);
            var localFile = await StorageFile.GetFileFromPathAsync(path);
            Check((await StorageFileReader.ReadAllBytesAsync(localFile)).SequenceEqual(original),
                "Ordinary disk-file imports must keep working");
        }
        finally { File.Delete(path); }
        Console.WriteLine($"Passed {checks} storage file import checks.");
    }
}
