using System.Collections;

namespace NAITool;

/// <summary>A cheap snapshot over immutable, newest-first daily path arrays. No image I/O or flattened copy.</summary>
public sealed class HistoryFileIndex : IReadOnlyList<string>
{
    public sealed record Day(string Date, string[] Files, int Start);
    public static HistoryFileIndex Empty { get; } = new([]);
    public IReadOnlyList<Day> Days { get; }
    private readonly Dictionary<string, int> _indicesByPath = new(StringComparer.OrdinalIgnoreCase);
    public int Count { get; }

    public HistoryFileIndex(IEnumerable<KeyValuePair<string, string[]>> days)
    {
        var result = new List<Day>();
        int count = 0;
        foreach (var pair in days)
        {
            if (pair.Value.Length == 0) continue;
            var day = new Day(pair.Key, pair.Value, count);
            result.Add(day);
            for (int i = 0; i < day.Files.Length; i++) _indicesByPath[day.Files[i]] = count + i;
            count = checked(count + day.Files.Length);
        }
        Days = result;
        Count = count;
    }

    public string this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            int lo = 0, hi = Days.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (Days[mid].Start <= index) lo = mid;
                else hi = mid - 1;
            }
            var day = Days[lo];
            return day.Files[index - day.Start];
        }
    }

    public int IndexOf(string path)
    {
        return _indicesByPath.TryGetValue(path, out int index) ? index : -1;
    }

    public HistoryFileIndex Filter(IReadOnlySet<string> paths) => paths.Count == 0 ? Empty : new(
        Days.Select(day => new KeyValuePair<string, string[]>(day.Date, day.Files.Where(paths.Contains).ToArray())));

    public IEnumerator<string> GetEnumerator() => Days.SelectMany(day => day.Files).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
