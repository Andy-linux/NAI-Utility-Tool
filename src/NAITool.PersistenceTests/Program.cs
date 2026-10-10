using System.Text.Json;
using NAITool.Services;

string root = Path.Combine(Path.GetTempPath(), "nai-persistence-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        passed++;
        Console.WriteLine("PASS " + name);
    }
    foreach (string file in new[] { "settings.json", "apiconfig.json" })
    {
        string path = Path.Combine(root, file);
        AtomicJsonFile.Write(path, "{\"revision\":1,\"synthetic\":true}");
        Check(File.Exists(path + ".bak"), file + ": initial recovery copy");
        AtomicJsonFile.Write(path, "{\"revision\":2,\"synthetic\":true}");
        Check(JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("revision").GetInt32() == 2,
            file + ": new primary");
        Check(JsonDocument.Parse(File.ReadAllText(path + ".bak")).RootElement.GetProperty("revision").GetInt32() == 1,
            file + ": previous valid backup");
        File.WriteAllText(path, "{truncated");
        string recovered = AtomicJsonFile.Read(path, out bool restored);
        Check(restored && recovered.Contains("\"revision\":1"), file + ": truncated recovery");
        Check(File.ReadAllText(path) == recovered, file + ": repaired primary");
        File.WriteAllText(path, "bad");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Check(AtomicJsonFile.Read(path, out restored) == recovered && restored,
                file + ": valid backup usable even when primary repair is locked");
        File.Delete(path);
        Check(AtomicJsonFile.Read(path, out restored) == recovered && restored, file + ": missing recovery");
        string before = File.ReadAllText(path);
        try { AtomicJsonFile.Write(path, "[]"); throw new Exception("invalid accepted"); }
        catch (JsonException) { }
        Check(File.ReadAllText(path) == before, file + ": invalid publication preserves file");
        File.WriteAllText(path, "{\"revision\":\"wrong-type\"}");
        _ = AtomicJsonFile.Read(path, out restored, json =>
        {
            using var parsed = JsonDocument.Parse(json);
            var revision = parsed.RootElement.GetProperty("revision");
            if (revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt32(out _))
                throw new JsonException("Bad type");
        });
        Check(restored, file + ": schema-invalid recovery");
        File.WriteAllText(path, "{}");
        File.WriteAllText(path + ".bak", "bad");
        File.WriteAllText(path, "bad");
        bool rejected = false;
        try { AtomicJsonFile.Read(path, out _); }
        catch (JsonException) { rejected = true; }
        Check(rejected, file + ": invalid backup is not restored");
        AtomicJsonFile.Write(path, "{\"revision\":3}");
        File.WriteAllText(path, "bad");
        Check(AtomicJsonFile.Read(path, out restored).Contains("\"revision\":3") && restored,
            file + ": new save repairs invalid backup");
    }
    string serviceRoot = Path.Combine(root, "service");
    var service = new SettingsService(serviceRoot);
    service.Settings.ApiBaseUrl = "https://synthetic.invalid/api/novelai";
    service.Settings.GenParameters.Steps = 19;
    service.Settings.GenParameters.Effort = "medium";
    service.Settings.GenParameters.Sampler = "k_dpmpp_2m";
    service.Settings.GenParameters.CfgRescale = 0.45;
    service.Settings.RememberedPrompts.GenNegativePrompt = "synthetic High negative draft";
    service.Settings.ApiGenerationTimeoutSeconds = 245;
    service.SetApiTokens(["synthetic-not-a-real-key"]);
    Check(service.Save(), "settings service initial encrypted save");
    string apiPath = Path.Combine(serviceRoot, "apiconfig.json");
    Check(!File.ReadAllText(apiPath).Contains("synthetic-not-a-real-key"), "credential plaintext not persisted");
    var loaded = new SettingsService(serviceRoot);
    loaded.Load();
    Check(loaded.Accounts.Single().Token == "synthetic-not-a-real-key", "DPAPI credential roundtrip");
    Check(loaded.Settings.GenParameters.Steps == 19, "saved custom steps preserved");
    Check(loaded.Settings.GenParameters.Effort == "medium" &&
        loaded.Settings.GenParameters.Sampler == "k_dpmpp_2m" &&
        loaded.Settings.GenParameters.CfgRescale == 0.45 &&
        loaded.Settings.RememberedPrompts.GenNegativePrompt == "synthetic High negative draft",
        "effort selection and High draft survive settings roundtrip");
    var effectiveMedium = ModelCapabilities.CreateRequestSnapshot(loaded.Settings.GenParameters);
    Check(effectiveMedium.Steps == 14 && effectiveMedium.Sampler == "k_euler_ancestral" &&
        effectiveMedium.Model == "nai-diffusion-5-full-medium" &&
        loaded.Settings.GenParameters.Steps == 19 && loaded.Settings.GenParameters.CfgRescale == 0.45,
        "effective Medium snapshot does not mutate restored High draft");
    Check(loaded.Settings.ApiGenerationTimeoutSeconds == 245, "timeout setting roundtrip");
    var limits = new AppSettings
    {
        ApiWaitingTimeoutSeconds = 0, ApiGenerationTimeoutSeconds = int.MaxValue,
        ApiReadOnlyTimeoutSeconds = -1,
    };
    limits.Normalize();
    Check(limits.ApiWaitingTimeoutSeconds == 1 && limits.ApiGenerationTimeoutSeconds == 3600 &&
        limits.ApiReadOnlyTimeoutSeconds == 1, "timeout settings normalized to bounded values");
    var nullableModel = JsonSerializer.Deserialize<AppSettings>("{\"GenParameters\":{\"Model\":null,\"Steps\":19},\"ReverseTagger\":null}")!;
    nullableModel.Normalize();
    Check(nullableModel.GenParameters.Model == NAIParameters.DefaultGenerationModel && nullableModel.GenParameters.Steps == 19,
        "null model migration preserves explicit saved steps");
    Check(!AppSettings.IsValidApiBaseUrl("https://key@example.invalid/api") &&
        !AppSettings.IsValidApiBaseUrl("https://example.invalid/api?key=synthetic") &&
        !AppSettings.IsValidApiBaseUrl("https://example.invalid/api#fragment"),
        "root rejects embedded credentials query and fragment");
    service.Settings.GenParameters.Steps = 20;
    Check(service.Save(), "settings service second save");
    File.WriteAllText(Path.Combine(serviceRoot, "settings.json"), "{\"GenParameters\":\"invalid\"}");
    File.WriteAllText(apiPath, "{\"Accounts\":\"invalid\"}");
    loaded = new SettingsService(serviceRoot);
    loaded.Load();
    Check(loaded.SettingsRecoveredFromBackup && loaded.Settings.GenParameters.Steps == 19,
        "typed settings backup recovery");
    Check(loaded.ApiConfigRecoveredFromBackup && loaded.Accounts.Single().Token == "synthetic-not-a-real-key",
        "typed encrypted credentials backup recovery");
    loaded.Accounts.Single().Token = "synthetic-replaced-not-a-real-key";
    Check(loaded.Save(), "mutable credential update saved");
    var changedCredential = new SettingsService(serviceRoot);
    changedCredential.Load();
    Check(changedCredential.Accounts.Single().Token == "synthetic-replaced-not-a-real-key",
        "mutable credential does not retain older encryption");
    string goodApi = File.ReadAllText(apiPath);
    File.WriteAllText(apiPath, "{\"Accounts\":[{\"EncryptedApiToken\":\"invalid-base64\"}]}");
    File.WriteAllText(apiPath + ".bak", goodApi);
    changedCredential.Load();
    Check(changedCredential.ApiConfigRecoveredFromBackup && !changedCredential.ApiTokenDecryptFailed &&
        changedCredential.Accounts.Single().Token == "synthetic-replaced-not-a-real-key",
        "DPAPI-invalid primary recovers decryptable backup");
    string legacyRoot = Path.Combine(root, "legacy");
    Directory.CreateDirectory(legacyRoot);
    File.WriteAllText(Path.Combine(legacyRoot, "apiconfig.json"),
        "{\"ApiToken\":\"synthetic-legacy-key\",\"CachedAnlas\":120,\"SubscriptionTierLevel\":3}");
    var legacy = new SettingsService(legacyRoot);
    legacy.Load();
    Check(legacy.Accounts.Single().Token == "synthetic-legacy-key" && legacy.Accounts.Single().Info?.AnlasBalance == 120,
        "legacy single-account migration retains credential and balance");
    Check(legacy.Save() && !File.ReadAllText(Path.Combine(legacyRoot, "apiconfig.json")).Contains("synthetic-legacy-key"),
        "legacy primary migrates to encrypted multi-account format");
    Check(!File.ReadAllText(Path.Combine(legacyRoot, "apiconfig.json.bak")).Contains("synthetic-legacy-key"),
        "legacy recovery backup also excludes plaintext credential");
    File.WriteAllText(Path.Combine(legacyRoot, "apiconfig.json"), "bad");
    legacy = new SettingsService(legacyRoot);
    legacy.Load();
    Check(legacy.ApiConfigRecoveredFromBackup && legacy.Accounts.Single().Token == "synthetic-legacy-key",
        "encrypted legacy backup remains recoverable");
    File.WriteAllText(Path.Combine(legacyRoot, "apiconfig.json"), "bad");
    File.WriteAllText(Path.Combine(legacyRoot, "apiconfig.json.bak"), "bad");
    legacy.Load();
    Check(legacy.Accounts.Count == 0, "failed reload does not retain stale credentials");
    string undecryptableRoot = Path.Combine(root, "undecryptable");
    Directory.CreateDirectory(undecryptableRoot);
    string undecryptablePath = Path.Combine(undecryptableRoot, "apiconfig.json");
    string undecryptableJson = "{\"Accounts\":[{\"EncryptedApiToken\":\"invalid-base64\"}]}";
    File.WriteAllText(undecryptablePath, undecryptableJson);
    File.WriteAllText(undecryptablePath + ".bak", undecryptableJson);
    var undecryptable = new SettingsService(undecryptableRoot);
    undecryptable.Load();
    Check(undecryptable.ApiTokenDecryptFailed && undecryptable.Accounts.Single().EncryptedApiToken == "invalid-base64",
        "undecryptable copies retain ciphertext with warning");
    undecryptable.Settings.ThemeMode = "Dark";
    Check(undecryptable.Save() && File.ReadAllText(undecryptablePath).Contains("invalid-base64") &&
        File.ReadAllText(undecryptablePath + ".bak").Contains("invalid-base64"),
        "ordinary settings save preserves undecryptable credential in both copies");
    undecryptable.Load();
    Check(undecryptable.Settings.ThemeMode == "Dark", "ordinary settings survive restart with undecryptable credential");
    File.Delete(undecryptablePath + ".bak");
    undecryptable.Load();
    Check(undecryptable.ApiTokenDecryptFailed && undecryptable.Accounts.Single().EncryptedApiToken == "invalid-base64",
        "single undecryptable primary remains available without backup");
    var untouchedCiphertext = undecryptable.Accounts.Single();
    undecryptable.SetApiAccounts([untouchedCiphertext, new ApiAccount { Token = "synthetic-added-row" }]);
    Check(ReferenceEquals(undecryptable.Accounts[0], untouchedCiphertext) &&
        undecryptable.Accounts[0].EncryptedApiToken == "invalid-base64",
        "account row update preserves untouched undecryptable credential object");
    string precedenceRoot = Path.Combine(root, "precedence");
    Directory.CreateDirectory(precedenceRoot);
    string precedencePath = Path.Combine(precedenceRoot, "apiconfig.json");
    string encryptedCurrent = changedCredential.Accounts.Single().EncryptedApiToken!;
    File.WriteAllText(precedencePath, JsonSerializer.Serialize(new
    {
        ApiToken = "synthetic-stale-plaintext", EncryptedApiToken = encryptedCurrent,
    }));
    var precedence = new SettingsService(precedenceRoot);
    precedence.Load();
    Check(precedence.Save(), "legacy dual-field save");
    File.WriteAllText(precedencePath, "bad");
    precedence.Load();
    Check(precedence.Accounts.Single().Token == "synthetic-replaced-not-a-real-key",
        "legacy encrypted credential precedence preserved in recovery copy");
    string recoveryRoot = Path.Combine(root, "legacy-backup");
    Directory.CreateDirectory(recoveryRoot);
    string recoveryPath = Path.Combine(recoveryRoot, "apiconfig.json");
    File.WriteAllText(recoveryPath + ".bak", "{\"ApiToken\":\"synthetic-backup-only\"}");
    var backupOnly = new SettingsService(recoveryRoot);
    backupOnly.Load();
    Check(backupOnly.ApiConfigRecoveredFromBackup && backupOnly.Accounts.Single().Token == "synthetic-backup-only" &&
        !File.ReadAllText(recoveryPath).Contains("synthetic-backup-only") &&
        !File.ReadAllText(recoveryPath + ".bak").Contains("synthetic-backup-only"),
        "legacy backup-only recovery sanitizes both copies");
    string directRoot = Path.Combine(root, "direct-save");
    Directory.CreateDirectory(directRoot);
    string directPath = Path.Combine(directRoot, "apiconfig.json");
    File.WriteAllText(directPath, "bad");
    File.WriteAllText(directPath + ".bak", "{\"ApiToken\":\"synthetic-old-backup\"}");
    var direct = new SettingsService(directRoot);
    direct.SetApiTokens(["synthetic-new-key"]);
    Check(direct.Save() && !File.ReadAllText(directPath + ".bak").Contains("synthetic-old-backup"),
        "direct save sanitizes existing legacy backup without loading");
    string concurrentPath = Path.Combine(root, "concurrent.json");
    AtomicJsonFile.Write(concurrentPath, "{\"revision\":0}");
    using var stop = new CancellationTokenSource();
    int readCount = 0;
    var readerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var reader = Task.Run(() =>
    {
        while (!stop.IsCancellationRequested)
        {
            // Share delete so Windows permits atomic replacement while reading.
            using var parsed = JsonDocument.Parse(AtomicJsonFile.Read(concurrentPath, out _));
            _ = parsed.RootElement.GetProperty("revision").GetInt32();
            Interlocked.Increment(ref readCount);
            readerStarted.TrySetResult();
        }
    });
    _ = Task.WhenAny(reader, readerStarted.Task).GetAwaiter().GetResult();
    if (reader.IsCompleted) reader.GetAwaiter().GetResult();
    try
    {
        for (int i = 1; i <= 50; i++) AtomicJsonFile.Write(concurrentPath, $"{{\"revision\":{i}}}");
    }
    finally { stop.Cancel(); reader.GetAwaiter().GetResult(); }
    Check(readCount > 0, "concurrent reads see complete JSON during publication");
    Check(!Directory.EnumerateFiles(root, "*.tmp").Any(), "temporary files cleaned");
    Console.WriteLine($"Persistence checks passed: {passed}");
}
finally { Directory.Delete(root, recursive: true); }
