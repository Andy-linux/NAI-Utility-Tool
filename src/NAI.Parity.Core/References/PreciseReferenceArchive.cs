using System.IO.Compression;
using System.Text.Json;

namespace NAITool.Services;

public static class PreciseReferenceArchive
{
    private sealed class Entry
    {
        public string filename { get; set; } = "";
        public string description { get; set; } = "character&style";
        public double fidelity { get; set; } = 1;
        public double strength { get; set; } = 1;
        public double information_extracted { get; set; } = 1;
        public bool enabled { get; set; } = true;
    }

    public static List<PreciseReferenceInfo> Read(Stream source)
    {
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        using var metadata = (zip.GetEntry("metadata.json") ?? throw new InvalidDataException("metadata.json not found")).Open();
        var entries = JsonSerializer.Deserialize<List<Entry>>(metadata) ?? [];
        var cards = new List<PreciseReferenceInfo>();
        foreach (var e in entries)
        {
            if (!double.IsFinite(e.strength) || !double.IsFinite(e.fidelity))
                throw new InvalidDataException("Non-finite reference value");
            using var image = (zip.GetEntry(e.filename) ?? throw new InvalidDataException("Reference image missing: " + e.filename)).Open();
            using var bytes = new MemoryStream();
            image.CopyTo(bytes);
            cards.Add(new PreciseReferenceInfo { FileName = e.filename, ImageBase64 = Convert.ToBase64String(bytes.ToArray()),
                ReferenceType = PreciseReferenceRules.Parse(e.description), Strength = e.strength, Fidelity = e.fidelity, Enabled = e.enabled });
        }
        return cards;
    }

    public static void Write(Stream destination, IEnumerable<PreciseReferenceInfo> cards)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var entries = new List<Entry>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata.json" };
        foreach (var card in cards)
        {
            string filename = Path.GetFileName(card.FileName);
            if (string.IsNullOrWhiteSpace(filename)) filename = "reference.png";
            string unique = filename;
            for (int n = 2; !used.Add(unique); n++)
                unique = Path.GetFileNameWithoutExtension(filename) + "-" + n + Path.GetExtension(filename);
            using (var image = zip.CreateEntry(unique).Open()) image.Write(Convert.FromBase64String(card.ImageBase64));
            entries.Add(new Entry { filename = unique, description = PreciseReferenceRules.Description(card.ReferenceType),
                strength = card.Strength, fidelity = card.Fidelity, enabled = card.Enabled });
        }
        using var metadata = zip.CreateEntry("metadata.json").Open();
        JsonSerializer.Serialize(metadata, entries, new JsonSerializerOptions { WriteIndented = true });
    }
}
