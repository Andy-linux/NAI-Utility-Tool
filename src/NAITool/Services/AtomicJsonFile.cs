using System.Text;
using System.Text.Json;

namespace NAITool.Services;

/// <summary>Same-directory, flushed atomic JSON publication with a last-valid backup.</summary>
internal static class AtomicJsonFile
{
    internal static string Read(string path, out bool recovered, Action<string>? validate = null,
        Func<string, string>? prepareRecovery = null)
    {
        recovered = false;
        try { return ReadValid(path, validate); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            string backup = ReadValid(path + ".bak", validate);
            backup = prepareRecovery?.Invoke(backup) ?? backup;
            Validate(backup);
            validate?.Invoke(backup);
            // Do not rotate a damaged primary over the known-good backup.
            try
            {
                if (prepareRecovery != null) Publish(path + ".bak", backup);
                Publish(path, backup);
            }
            catch (Exception repairError) when (repairError is IOException or UnauthorizedAccessException)
            {
                // A valid backup is still usable when the primary is read-only/locked.
                // Keep the recovery flag so the UI can tell the user to resolve storage access.
            }
            recovered = true;
            return backup;
        }
    }

    internal static void Write(string path, string json, Action<string>? validate = null,
        Func<string, string>? prepareBackup = null)
    {
        Validate(json);
        validate?.Invoke(json);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
        {
            string? previous = null;
            try { previous = ReadValid(path, validate); }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            if (previous != null)
            {
                string backup = prepareBackup?.Invoke(previous) ?? previous;
                Validate(backup);
                validate?.Invoke(backup);
                Publish(path + ".bak", backup);
            }
        }
        Publish(path, json);
        // First save (or an invalid pre-existing backup) also gets a recovery copy.
        bool validBackup = false;
        try
        {
            string existingBackup = ReadValid(path + ".bak", validate);
            if (prepareBackup != null)
            {
                string prepared = prepareBackup(existingBackup);
                Validate(prepared);
                validate?.Invoke(prepared);
                if (prepared != existingBackup) Publish(path + ".bak", prepared);
            }
            validBackup = true;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        if (!validBackup) Publish(path + ".bak", json);
    }

    private static string ReadValid(string path, Action<string>? validate = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string json = reader.ReadToEnd();
        Validate(json);
        validate?.Invoke(json);
        return json;
    }

    private static void Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Configuration must be a JSON object.");
    }

    private static void Publish(string path, string json)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
