using System.Collections.Concurrent;
using System.Globalization;

namespace NAITool.Services;

public static class HistoryCatalogScanner
{
    public static Task<Dictionary<string, string[]>> ScanAsync(string directory, CancellationToken cancellationToken) => Task.Run(() =>
    {
        var collected = new ConcurrentBag<(string Path, DateTime Created, string Day)>();
        if (!Directory.Exists(directory)) return new Dictionary<string, string[]>(StringComparer.Ordinal);
        var dates = Directory.EnumerateDirectories(directory).Where(path =>
            DateTime.TryParseExact(Path.GetFileName(path), new[] { "yyyy-MM-dd", "yyyy-MM" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        Parallel.ForEach(dates, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
        }, path =>
        {
            try
            {
                // FileInfo from enumeration already contains timestamps; avoid a stat for every sort comparison.
                foreach (var file in new DirectoryInfo(path).EnumerateFiles().Where(file =>
                    file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase) || file.Extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    collected.Add((file.FullName, file.CreationTimeUtc, file.CreationTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
                }

            }
            catch (IOException) { /* A date directory can disappear during a rescan. */ }
            catch (UnauthorizedAccessException) { /* Other dates remain browsable. */ }
        });
        cancellationToken.ThrowIfCancellationRequested();
        return collected.GroupBy(file => file.Day).ToDictionary(group => group.Key,
            group => group.OrderByDescending(file => file.Created).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.Path).ToArray(), StringComparer.Ordinal);
    }, cancellationToken);
}
