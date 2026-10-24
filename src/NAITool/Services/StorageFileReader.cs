using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace NAITool.Services;

public static class StorageFileReader
{
    // Browser drags can provide a readable virtual file with an empty Path.
    // Read the original stream; decoding/re-encoding here would discard image Info.
    public static async Task<byte[]> ReadAllBytesAsync(StorageFile file)
    {
        using var input = await file.OpenStreamForReadAsync();
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }
}
