using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace NAITool.Services;

internal sealed partial class BusinessLog
{
    // Artifact-only traversal. No changes are made to the response consumed by generation.
    // Keep the original representation unless a decoded credential was actually replaced.
    private byte[] SanitizeArtifact(byte[] bytes, out bool changed)
    {
        changed = false;
        if (bytes.Length == 0) return bytes;
        byte[] clean = bytes;
        if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && bytes[2] == 3 && bytes[3] == 4)
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entries = new List<(ZipArchiveEntry Entry, byte[] Bytes)>();
            foreach (var entry in archive.Entries)
            {
                using var source = entry.Open(); using var data = new MemoryStream(); source.CopyTo(data);
                byte[] value = SanitizeArtifact(data.ToArray(), out bool modified);
                changed |= modified || RedactPlain(entry.FullName) != entry.FullName; entries.Add((entry, value));
            }
            if (!changed) return bytes;
            using var output = new MemoryStream();
            using (var sanitized = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var item in entries)
                {
                    string name = RedactPlain(item.Entry.FullName);
                    var entry = sanitized.CreateEntry(name); entry.LastWriteTime = item.Entry.LastWriteTime;
                    entry.ExternalAttributes = item.Entry.ExternalAttributes;
                    using var target = entry.Open(); target.Write(item.Bytes);
                }
            return output.ToArray();
        }
        if (bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            return SanitizePng(bytes, out changed);
        if ((bytes[0] & 0xf0) == 0x80 || bytes[0] is 0xde or 0xdf)
        {
            int offset = 0;
            clean = MessagePackValue(bytes, ref offset, false, out changed);
            if (offset != bytes.Length) throw new InvalidDataException("Trailing diagnostic MessagePack data.");
            return changed ? clean : bytes;
        }
        // Native image streams use a big-endian length followed by a MessagePack map.
        if (bytes.Length >= 5 && bytes[0] == 0 && ((bytes[4] & 0xf0) == 0x80 || bytes[4] is 0xde or 0xdf))
        {
            using var output = new MemoryStream(); int offset = 0;
            while (offset < bytes.Length)
            {
                if (bytes.Length - offset < 4) throw new InvalidDataException("Partial diagnostic MessagePack header.");
                int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4))); offset += 4;
                if (length > bytes.Length - offset) throw new InvalidDataException("Partial diagnostic MessagePack frame.");
                byte[] frame = SanitizeArtifact(bytes.AsSpan(offset, length).ToArray(), out bool modified);
                changed |= modified; WriteLength(output, frame.Length, 4); output.Write(frame); offset += length;
            }
            return changed ? output.ToArray() : bytes;
        }
        // Decode UTF-8 before matching: alternative JSON Unicode escapes must not hide keys/values.
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            string sanitized = Redact(text);
            if (text.Contains("data:", StringComparison.Ordinal))
                sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, "(?m)^(data:[ \\t]*)([^\\r\\n]*)",
                    match => match.Groups[1].Value + Redact(match.Groups[2].Value));
            changed = text != sanitized;
            if (changed) clean = Encoding.UTF8.GetBytes(sanitized);
        }
        catch (DecoderFallbackException) { }
        string raw = Encoding.Latin1.GetString(clean), replaced = raw;
        foreach (string secret in secrets)
            replaced = replaced.Replace(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(secret)), "[REDACTED]", StringComparison.Ordinal);
        changed |= raw != replaced;
        return raw == replaced ? clean : Encoding.Latin1.GetBytes(replaced);
    }
    private static void WriteLength(Stream output, int length, int width)
    {
        byte[] bytes = new byte[width];
        for (int i = width - 1; i >= 0; i--) { bytes[i] = (byte)length; length >>= 8; }
        output.Write(bytes);
    }
    private byte[] MessagePackValue(byte[] bytes, ref int offset, bool credential, out bool changed)
    {
        int start = offset; changed = credential;
        byte code = bytes[offset++];
        int count = -1; bool map = false;
        if ((code & 0xf0) == 0x80) { count = code & 15; map = true; }
        else if ((code & 0xf0) == 0x90) count = code & 15;
        else if (code is 0xde or 0xdf) { count = ReadLength(bytes, ref offset, code == 0xde ? 2 : 4); map = true; }
        else if (code is 0xdc or 0xdd) count = ReadLength(bytes, ref offset, code == 0xdc ? 2 : 4);
        if (count >= 0)
        {
            using var output = new MemoryStream(); output.Write(bytes.AsSpan(start, offset - start));
            for (int i = 0; i < count; i++)
            {
                bool secretField = false;
                if (map)
                {
                    int keyStart = offset;
                    byte[] key = MessagePackValue(bytes, ref offset, false, out bool keyChanged);
                    changed |= keyChanged; output.Write(key);
                    int keyOffset = keyStart;
                    string name = MessagePackString(bytes, ref keyOffset);
                    secretField = CredentialField.IsMatch(name);
                }
                byte[] value = MessagePackValue(bytes, ref offset, secretField, out bool modified);
                changed |= modified; output.Write(value);
            }
            return credential ? PackText("[REDACTED]") : changed ? output.ToArray() : bytes.AsSpan(start, offset - start).ToArray();
        }
        bool text = (code & 0xe0) == 0xa0 || code is 0xd9 or 0xda or 0xdb;
        bool binary = code is 0xc4 or 0xc5 or 0xc6;
        if (text || binary)
        {
            int length = (code & 0xe0) == 0xa0 ? code & 31 : ReadLength(bytes, ref offset, code is 0xd9 or 0xc4 ? 1 : code is 0xda or 0xc5 ? 2 : 4);
            byte[] value = bytes.AsSpan(offset, length).ToArray(); offset += length;
            if (credential) return PackText("[REDACTED]");
            if (text)
            {
                string original = new UTF8Encoding(false, true).GetString(value), clean = Redact(original);
                changed = clean != original; if (changed) return PackText(clean);
            }
            else
            {
                byte[] clean = SanitizeArtifact(value, out changed);
                // Known credential echoes can also occur in opaque image/vibe byte strings.
                string original = Encoding.Latin1.GetString(clean), sanitized = original;
                foreach (var secret in secrets) sanitized = sanitized.Replace(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(secret)), "[REDACTED]", StringComparison.Ordinal);
                changed |= original != sanitized;
                if (changed)
                { clean = Encoding.Latin1.GetBytes(sanitized); using var output = new MemoryStream(); output.WriteByte(0xc6); WriteLength(output, clean.Length, 4); output.Write(clean); return output.ToArray(); }
            }
        }
        else
        {
            int width = code switch { 0xcc or 0xd0 => 1, 0xcd or 0xd1 => 2, 0xce or 0xd2 or 0xca => 4, 0xcf or 0xd3 or 0xcb => 8,
                <= 0x7f or >= 0xe0 or 0xc0 or 0xc2 or 0xc3 => 0, _ => throw new InvalidDataException("Unsupported diagnostic MessagePack type.") };
            offset = checked(offset + width);
        }
        return credential ? PackText("[REDACTED]") : bytes.AsSpan(start, offset - start).ToArray();
    }
    private static int ReadLength(byte[] bytes, ref int offset, int width)
    {
        int result = 0;
        for (int i = 0; i < width; i++) result = checked(result * 256 + bytes[offset++]);
        return result;
    }
    private static byte[] PackText(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text); using var output = new MemoryStream();
        output.WriteByte(0xdb); WriteLength(output, bytes.Length, 4); output.Write(bytes); return output.ToArray();
    }
    private static string MessagePackString(byte[] bytes, ref int offset)
    {
        byte code = bytes[offset++]; int length;
        if ((code & 0xe0) == 0xa0) length = code & 31;
        else
        {
            int width = code switch { 0xd9 => 1, 0xda => 2, 0xdb => 4, _ => throw new InvalidDataException("Non-string diagnostic MessagePack key.") };
            length = 0; for (int i = 0; i < width; i++) length = checked(length * 256 + bytes[offset++]);
        }
        string text = new UTF8Encoding(false, true).GetString(bytes, offset, length); offset += length; return text;
    }
    private byte[] SanitizePng(byte[] bytes, out bool changed)
    {
        changed = false; using var output = new MemoryStream(); output.Write(bytes.AsSpan(0, 8)); int offset = 8;
        while (offset < bytes.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);
            byte[] data = bytes.AsSpan(offset + 8, length).ToArray(); bool modified = false;
            if (type == "tEXt")
            {
                int separator = Array.IndexOf(data, (byte)0);
                if (separator < 0) throw new InvalidDataException("Invalid PNG text metadata.");
                string original = Encoding.Latin1.GetString(data, separator + 1, data.Length - separator - 1);
                string clean = CredentialField.IsMatch(Encoding.Latin1.GetString(data, 0, separator)) ? "[REDACTED]" : Redact(original);
                modified = clean != original;
                if (modified) data = data[..(separator + 1)].Concat(Encoding.Latin1.GetBytes(clean)).ToArray();
            }
            else if (type is "zTXt" or "iTXt")
            {
                int separator = Array.IndexOf(data, (byte)0), textStart = separator + (type == "zTXt" ? 2 : 3);
                if (separator < 0) throw new InvalidDataException("Invalid PNG text metadata.");
                bool compressed = type == "zTXt" || data[separator + 1] == 1;
                if (type == "iTXt") for (int i = 0; i < 2; i++) { textStart = Array.IndexOf(data, (byte)0, textStart) + 1; if (textStart == 0) throw new InvalidDataException("Invalid PNG international text."); }
                byte[] text = data[textStart..];
                if (compressed) { using var z = new ZLibStream(new MemoryStream(text), CompressionMode.Decompress); using var decoded = new MemoryStream(); z.CopyTo(decoded); text = decoded.ToArray(); }
                string original = (type == "iTXt" ? Encoding.UTF8 : Encoding.Latin1).GetString(text);
                string clean = CredentialField.IsMatch(Encoding.Latin1.GetString(data, 0, separator)) ? "[REDACTED]" : Redact(original);
                modified = original != clean;
                if (modified)
                {
                    text = (type == "iTXt" ? Encoding.UTF8 : Encoding.Latin1).GetBytes(clean);
                    if (compressed) { using var encoded = new MemoryStream(); using (var z = new ZLibStream(encoded, CompressionLevel.Optimal, true)) z.Write(text); text = encoded.ToArray(); }
                    data = data[..textStart].Concat(text).ToArray();
                }
            }
            changed |= modified;
            if (!modified) output.Write(bytes.AsSpan(offset, length + 12));
            else
            {
                WriteLength(output, data.Length, 4); byte[] kind = Encoding.ASCII.GetBytes(type); output.Write(kind); output.Write(data);
                uint crc = 0xffffffff;
                foreach (byte b in kind.Concat(data)) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
                WriteLength(output, unchecked((int)~crc), 4);
            }
            offset += length + 12;
        }
        return changed ? output.ToArray() : bytes;
    }
}
