using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using NAITool.Models;

namespace NAITool.Services;

/// <summary>
/// 应用设置持久化服务。
/// 通用设置与 API 凭证分别存储在 user/config/ 下的独立文件中。
/// </summary>
public class SettingsService
{
    private static string AppRootDir => AppPathResolver.AppRootDir;

    private static string DefaultConfigDir => Path.Combine(AppRootDir, "user", "config");
    private readonly string ConfigDir;
    private readonly string SettingsFilePath;
    private readonly string ApiConfigFilePath;

    public SettingsService() : this(DefaultConfigDir) { }

    internal SettingsService(string configDir)
    {
        ConfigDir = Path.GetFullPath(configDir);
        SettingsFilePath = Path.Combine(ConfigDir, "settings.json");
        ApiConfigFilePath = Path.Combine(ConfigDir, "apiconfig.json");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AppSettings Settings { get; private set; } = new();
    public ApiConfig CachedApiConfig { get; private set; } = new();
    public IReadOnlyList<ApiAccount> Accounts => CachedApiConfig.Accounts;
    public bool HasApiTokens => Accounts.Any(a => !string.IsNullOrWhiteSpace(a.Token));
    private readonly object _saveLock = new();

    // Retain cache by credential identity.
    public void SetApiTokens(IEnumerable<string> tokens)
    {
        lock (_saveLock)
        {
            var existing = Accounts.Where(a => a.Token.Length > 0)
                .GroupBy(a => a.Token, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First());
            CachedApiConfig.Accounts = tokens.Select(token =>
            {
                string trimmed = token.Trim();
                if (!existing.TryGetValue(trimmed, out var account))
                {
                    account = new ApiAccount { Token = trimmed };
                    if (trimmed.Length > 0) existing.Add(trimmed, account);
                }
                return account;
            }).ToList();
        }
    }

    // UI row mapping can preserve undecryptable ciphertext in untouched rows.
    public void SetApiAccounts(IEnumerable<ApiAccount> accounts)
    {
        lock (_saveLock)
        {
            var canonical = new Dictionary<string, ApiAccount>(StringComparer.Ordinal);
            CachedApiConfig.Accounts = accounts.Select(account =>
            {
                if (account.Token.Length == 0) return account;
                if (canonical.TryGetValue(account.Token, out var first)) return first;
                canonical.Add(account.Token, account);
                return account;
            }).ToList();
        }
    }

    public bool ApiTokenDecryptFailed { get; private set; }
    public bool SettingsRecoveredFromBackup { get; private set; }
    public bool ApiConfigRecoveredFromBackup { get; private set; }

    public static bool SettingsFileExists => File.Exists(Path.Combine(DefaultConfigDir, "settings.json")) ||
        File.Exists(Path.Combine(DefaultConfigDir, "settings.json.bak"));

    private static byte[] GetMachineEntropy()
    {
        using var regKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var guid = regKey?.GetValue("MachineGuid") as string ?? Environment.MachineName;
        return SHA256.HashData(Encoding.UTF8.GetBytes(guid));
    }

    private static string EncryptToken(string plainToken)
    {
        var data = Encoding.UTF8.GetBytes(plainToken);
        var entropy = GetMachineEntropy();
        var encrypted = ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string? DecryptToken(string encryptedToken)
    {
        try
        {
            var data = Convert.FromBase64String(encryptedToken);
            var entropy = GetMachineEntropy();
            var decrypted = ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>从磁盘加载设置（通用 + API 凭证 + 缓存账户信息）</summary>
    public void Load()
    {
        lock (_saveLock) LoadCore();
    }

    private void LoadCore()
    {
        SettingsRecoveredFromBackup = false;
        ApiConfigRecoveredFromBackup = false;
        ApiTokenDecryptFailed = false;
        Settings = new();
        CachedApiConfig = new();
        try
        {
            if (File.Exists(SettingsFilePath) || File.Exists(SettingsFilePath + ".bak"))
            {
                var json = AtomicJsonFile.Read(SettingsFilePath, out bool recovered, ValidateSettingsJson);
                SettingsRecoveredFromBackup = recovered;
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new();
                Settings.Normalize();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Load failed: {ex.GetType().Name}");
            Settings = new();
        }

        try
        {
            if (File.Exists(ApiConfigFilePath) || File.Exists(ApiConfigFilePath + ".bak"))
            {
                string json;
                bool recovered;
                try
                {
                    json = AtomicJsonFile.Read(ApiConfigFilePath, out recovered, ValidateUsableApiConfigJson, ProtectLegacyBackup);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // Foreign-machine DPAPI data remains intact and editable when neither copy decrypts.
                    json = AtomicJsonFile.Read(ApiConfigFilePath, out recovered, ValidateApiConfigJson, ProtectLegacyBackup);
                }
                ApiConfigRecoveredFromBackup = recovered;
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty(nameof(ApiConfig.Accounts), out _))
                {
                    CachedApiConfig = JsonSerializer.Deserialize<ApiConfig>(json, JsonOptions) ?? new();
                    CachedApiConfig.Accounts ??= [];
                    CachedApiConfig.Accounts = CachedApiConfig.Accounts.Where(a => a != null).ToList();
                    foreach (var account in Accounts)
                    {
                        if (string.IsNullOrWhiteSpace(account.EncryptedApiToken)) continue;
                        account.Token = DecryptToken(account.EncryptedApiToken)?.Trim() ?? "";
                        ApiTokenDecryptFailed |= account.Token.Length == 0;
                    }
                    var canonical = new Dictionary<string, ApiAccount>(StringComparer.Ordinal);
                    for (int i = 0; i < CachedApiConfig.Accounts.Count; i++)
                    {
                        var account = CachedApiConfig.Accounts[i];
                        if (account.Token.Length == 0) continue;
                        if (canonical.TryGetValue(account.Token, out var first))
                        {
                            first.Info ??= account.Info;
                            CachedApiConfig.Accounts[i] = first;
                        }
                        else canonical.Add(account.Token, account);
                    }
                }
                else
                {
                    // One-time import of the released single-account configuration.
                    var legacy = JsonSerializer.Deserialize<LegacyApiConfig>(json, JsonOptions);
                    if (legacy != null)
                    {
                        string token = !string.IsNullOrWhiteSpace(legacy.EncryptedApiToken)
                            ? DecryptToken(legacy.EncryptedApiToken) ?? "" : legacy.ApiToken ?? "";
                        ApiTokenDecryptFailed = token.Length == 0 && !string.IsNullOrEmpty(legacy.EncryptedApiToken);
                        CachedApiConfig.Accounts = [new ApiAccount
                        {
                            Token = token.Trim(), EncryptedApiToken = legacy.EncryptedApiToken,
                            Info = new NovelAiAccountInfo
                            {
                                AnlasBalance = legacy.CachedAnlas,
                                V5UsagePercent = legacy.CachedV5UsagePercent,
                                V5UsageIsNegative = legacy.CachedV5UsageIsNegative,
                                V5UsageTimeUntilNextPercentSeconds = legacy.CachedV5UsageTimeUntilNextPercentSeconds,
                                TierName = legacy.SubscriptionTier ?? "", TierLevel = legacy.SubscriptionTierLevel,
                                IsOpus = legacy.SubscriptionTierLevel >= 3,
                                HasActiveSubscription = legacy.SubscriptionActive == true,
                                ExpiresAt = legacy.SubscriptionExpiresAt,
                            },
                        }];
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ApiConfig] Load failed: {ex.GetType().Name}");
        }
    }

    /// <summary>保存设置到磁盘（通用设置与 API 凭证分别写入）</summary>
    public bool Save()
    {
        lock (_saveLock)
        try
        {
            Directory.CreateDirectory(ConfigDir);
            Settings.Normalize();

            var settingsJson = JsonSerializer.Serialize(Settings, JsonOptions);

            foreach (var account in Accounts)
            {
                if (!string.IsNullOrWhiteSpace(account.Token))
                {
                    // Re-encrypt changed credentials.
                    if (account.EncryptedApiToken == null || DecryptToken(account.EncryptedApiToken) != account.Token)
                        account.EncryptedApiToken = EncryptToken(account.Token);
                }
                // Keep undecryptable ciphertext for recovery; explicit UI clearing creates a new empty account.
            }
            var apiJson = JsonSerializer.Serialize(CachedApiConfig, JsonOptions);
            ValidateApiConfigJson(apiJson);
            AtomicJsonFile.Write(SettingsFilePath, settingsJson, ValidateSettingsJson);
            AtomicJsonFile.Write(ApiConfigFilePath, apiJson,
                ValidateApiConfigJson, ProtectLegacyBackup);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Save failed: {ex.GetType().Name}");
            return false;
        }
    }

    private static string ProtectLegacyBackup(string json)
    {
        // Encrypt legacy credentials in the recovery copy.
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.TryGetProperty(nameof(ApiConfig.Accounts), out _)) return json;
        var legacy = JsonSerializer.Deserialize<LegacyApiConfig>(json, JsonOptions)!;
        if (string.IsNullOrWhiteSpace(legacy.EncryptedApiToken) && !string.IsNullOrWhiteSpace(legacy.ApiToken))
            legacy.EncryptedApiToken = EncryptToken(legacy.ApiToken);
        legacy.ApiToken = null;
        return JsonSerializer.Serialize(legacy, JsonOptions);
    }

    private static void ValidateSettingsJson(string json)
    {
        _ = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
            ?? throw new JsonException("Invalid settings.");
    }

    private static void ValidateUsableApiConfigJson(string json)
    {
        ValidateApiConfigJson(json);
        using var parsed = JsonDocument.Parse(json);
        IEnumerable<string?> encrypted;
        if (parsed.RootElement.TryGetProperty(nameof(ApiConfig.Accounts), out _))
            encrypted = (JsonSerializer.Deserialize<ApiConfig>(json, JsonOptions)!.Accounts ?? [])
                .Where(a => a != null).Select(a => a.EncryptedApiToken);
        else
            encrypted = [JsonSerializer.Deserialize<LegacyApiConfig>(json, JsonOptions)!.EncryptedApiToken];
        if (encrypted.Any(value => !string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(DecryptToken(value))))
            throw new JsonException("Credential cannot be decrypted.");
    }

    private static void ValidateApiConfigJson(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.TryGetProperty(nameof(ApiConfig.Accounts), out _))
            _ = JsonSerializer.Deserialize<ApiConfig>(json, JsonOptions)
                ?? throw new JsonException("Invalid API configuration.");
        else
            _ = JsonSerializer.Deserialize<LegacyApiConfig>(json, JsonOptions)
                ?? throw new JsonException("Invalid legacy API configuration.");
    }

    private sealed class LegacyApiConfig
    {
        public LegacyApiConfig() { }
        public string? ApiToken { get; set; }
        public string? EncryptedApiToken { get; set; }
        public int? CachedAnlas { get; set; }
        public int? CachedV5UsagePercent { get; set; }
        public bool? CachedV5UsageIsNegative { get; set; }
        public int? CachedV5UsageTimeUntilNextPercentSeconds { get; set; }
        public string? SubscriptionTier { get; set; }
        public int? SubscriptionTierLevel { get; set; }
        public bool? SubscriptionActive { get; set; }
        public string? SubscriptionExpiresAt { get; set; }
    }
}

public class AppSettings
{
    public const double DefaultHistorySidebarWidth = 260;
    public const double MinHistorySidebarWidth = 200;
    public const double MaxHistorySidebarWidth = 600;
    public const double DefaultGalleryThumbnailHeight = 140;
    public const double MinGalleryThumbnailHeight = 80;
    public const double MaxGalleryThumbnailHeight = 320;

    public double HistorySidebarWidth { get; set; } = DefaultHistorySidebarWidth;
    public double GalleryThumbnailHeight { get; set; } = DefaultGalleryThumbnailHeight;
    public ApiCallMode ApiCallMode { get; set; } = ApiCallMode.RoundRobin;
    public string ApiBaseUrl { get; set; } = "";
    // Waiting plus execution is the end-to-end budget where no queue-start signal exists.
    public int ApiWaitingTimeoutSeconds { get; set; } = 120;
    public int ApiGenerationTimeoutSeconds { get; set; } = 300;
    public int ApiUpscaleTimeoutSeconds { get; set; } = 300;
    public int ApiTextTimeoutSeconds { get; set; } = 120;
    public int ApiReadOnlyTimeoutSeconds { get; set; } = 15;
    public bool WeightHighlight { get; set; } = true;
    public bool AutoComplete { get; set; } = true;
    public bool RememberPromptAndParameters { get; set; } = true;
    public double PromptEditorHeight { get; set; }
    public double StylePromptEditorHeight { get; set; }
    public bool SuperDropEnabled { get; set; } = true;
    public bool ScrollHistoryToTopAfterGeneration { get; set; } = true;
    public bool NewImageDeleteProtection { get; set; } = true;
    public bool EnableGenerationWaitingAnimation { get; set; } = true;
    public bool WildcardsEnabled { get; set; } = true;
    public bool WildcardsRequireExplicitSyntax { get; set; } = true;
    public int RandomStyleTagCount { get; set; } = 3;
    public int RandomStyleMinCount { get; set; } = 80;
    public bool RandomStyleUseWeight { get; set; }
    public bool AutoGenRandomStylePrefix { get; set; }
    public bool AccountAssetProtectionMode { get; set; } = false;
    public bool AccountAssetProtectionBlockOversizedDimensions { get; set; } = true;
    public bool AccountAssetProtectionBlockOversizedSteps { get; set; } = true;
    public bool AccountAssetProtectionDisablePaidFeatures { get; set; } = true;
    public bool UseProxy { get; set; }
    public string ProxyPort { get; set; } = "10808";
    public bool UseWebp { get; set; }
    public string ImageDeleteBehavior { get; set; } = "RecycleBin";
    public bool PrivacyMode { get; set; }
    public bool StripSavedImageMetadata { get; set; }
    public bool AutoCopyVibeOriginalsToWorkspace { get; set; }
    public string ThemeMode { get; set; } = "System";
    public string AppearanceTransparency { get; set; } = "Standard";
    public string LanguageCode { get; set; } = "";
    public bool DevLogEnabled { get; set; }
    public bool StreamGeneration { get; set; }
    public int EnhanceMagnitude { get; set; } = 3;
    public bool EnhanceShowIndividualSettings { get; set; }
    public double EnhanceStrength { get; set; } = 0.5;
    public double EnhanceNoise { get; set; }
    public double EnhanceUpscaleAmount { get; set; } = 1.5;
    public bool EnhanceUseMaxUpscale { get; set; }
    public bool UseNovelAiUpscale { get; set; }
    public OnnxPerformanceSettings OnnxPerformance { get; set; } = null!;
    public PostEffectsPerformanceSettings PostEffectsPerformance { get; set; } = null!;
    public ReverseTaggerSettings ReverseTagger { get; set; } = new();
    public NAIParameters GenParameters { get; set; } = new() { Model = NAIParameters.DefaultGenerationModel };
    public NAIParameters InpaintParameters { get; set; } = new() { Model = NAIParameters.DefaultInpaintModel };
    public NAIParameters I2IDenoiseParameters { get; set; } = new() { Model = NAIParameters.DefaultI2IDenoiseModel, DenoiseStrength = 0.7, DenoiseNoise = 0 };
    public RememberedPromptState RememberedPrompts { get; set; } = new();
    public int RememberedCustomWidth { get; set; } = 832;
    public int RememberedCustomHeight { get; set; } = 1216;
    public AutomationSettings Automation { get; set; } = new();

    [JsonIgnore]
    public bool UsesCustomApiBaseUrl => !string.IsNullOrWhiteSpace(ApiBaseUrl);

    public void Normalize()
    {
        HistorySidebarWidth = double.IsFinite(HistorySidebarWidth)
            ? Math.Clamp(HistorySidebarWidth, MinHistorySidebarWidth, MaxHistorySidebarWidth)
            : DefaultHistorySidebarWidth;
        GalleryThumbnailHeight = double.IsFinite(GalleryThumbnailHeight)
            ? Math.Clamp(GalleryThumbnailHeight, MinGalleryThumbnailHeight, MaxGalleryThumbnailHeight)
            : DefaultGalleryThumbnailHeight;
        ApiBaseUrl = NormalizeApiBaseUrl(ApiBaseUrl);
        ApiWaitingTimeoutSeconds = Math.Clamp(ApiWaitingTimeoutSeconds, 1, 3600);
        ApiGenerationTimeoutSeconds = Math.Clamp(ApiGenerationTimeoutSeconds, 1, 3600);
        ApiUpscaleTimeoutSeconds = Math.Clamp(ApiUpscaleTimeoutSeconds, 1, 3600);
        ApiTextTimeoutSeconds = Math.Clamp(ApiTextTimeoutSeconds, 1, 3600);
        ApiReadOnlyTimeoutSeconds = Math.Clamp(ApiReadOnlyTimeoutSeconds, 1, 3600);
        if (!Enum.IsDefined(ApiCallMode)) ApiCallMode = ApiCallMode.RoundRobin;
        if (!string.IsNullOrWhiteSpace(LanguageCode))
            LanguageCode = LocalizationService.NormalizeLanguageCode(LanguageCode);
        AppearanceTransparency = AppearanceTransparency switch
        {
            "Standard" or "Lesser" or "Opaque" => AppearanceTransparency,
            _ => "Standard",
        };
        ImageDeleteBehavior = ImageDeleteBehavior switch
        {
            "RecycleBin" or "PermanentDelete" => ImageDeleteBehavior,
            _ => "RecycleBin",
        };
        EnhanceMagnitude = Math.Clamp(EnhanceMagnitude, 1, 5);
        EnhanceStrength = double.IsFinite(EnhanceStrength) ? Math.Clamp(EnhanceStrength, 0.01, 0.99) : 0.5;
        EnhanceNoise = double.IsFinite(EnhanceNoise) ? Math.Clamp(EnhanceNoise, 0, 0.99) : 0;
        EnhanceUpscaleAmount = double.IsFinite(EnhanceUpscaleAmount) ? EnhanceUpscaleAmount switch
        {
            >= 1.75 => 2.0,
            >= 1.25 => 1.5,
            _ => 1.0,
        } : 1.5;
        ReverseTagger ??= new();
        OnnxPerformance ??= new()
        {
            UnloadModelAfterInference = ReverseTagger.UnloadModelAfterInference,
        };
        OnnxPerformance.Normalize();
        PostEffectsPerformance ??= new();
        PostEffectsPerformance.Normalize();
        ReverseTagger.UnloadModelAfterInference = OnnxPerformance.UnloadModelAfterInference;
        GenParameters ??= new() { Model = NAIParameters.DefaultGenerationModel };
        InpaintParameters ??= new() { Model = NAIParameters.DefaultInpaintModel };
        I2IDenoiseParameters ??= new() { Model = NAIParameters.DefaultI2IDenoiseModel, DenoiseStrength = 0.7, DenoiseNoise = 0 };
        GenParameters.Model ??= NAIParameters.DefaultGenerationModel;
        InpaintParameters.Model ??= NAIParameters.DefaultInpaintModel;
        I2IDenoiseParameters.Model ??= NAIParameters.DefaultI2IDenoiseModel;
        RememberedPrompts ??= new();
        Automation ??= new();
        Automation.Normalize();
        AutoGenRandomStylePrefix = Automation.Randomization.RandomizeStyleTags;
    }

    public static string NormalizeApiBaseUrl(string? value)
    {
        string trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
            return "";

        trimmed = trimmed.TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        return trimmed;
    }

    public static bool IsValidApiBaseUrl(string? value)
    {
        string normalized = NormalizeApiBaseUrl(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }
}

public class OnnxPerformanceSettings
{
    public string DevicePreference { get; set; } = "Gpu";
    public bool UnloadModelAfterInference { get; set; }

    [JsonIgnore]
    public bool PreferCpu =>
        string.Equals(DevicePreference, "Cpu", StringComparison.OrdinalIgnoreCase);

    public void Normalize()
    {
        DevicePreference = string.Equals(DevicePreference, "Cpu", StringComparison.OrdinalIgnoreCase)
            ? "Cpu"
            : "Gpu";
    }
}

public class PostEffectsPerformanceSettings
{
    public string DevicePreference { get; set; } = "Gpu";

    [JsonIgnore]
    public bool PreferCpu =>
        string.Equals(DevicePreference, "Cpu", StringComparison.OrdinalIgnoreCase);

    public void Normalize()
    {
        DevicePreference = string.Equals(DevicePreference, "Cpu", StringComparison.OrdinalIgnoreCase)
            ? "Cpu"
            : "Gpu";
    }
}

public class RememberedPromptState
{
    public string GenPositivePrompt { get; set; } = "";
    public string GenNegativePrompt { get; set; } = "";
    public string GenStylePrompt { get; set; } = "";
    public string I2IPositivePrompt { get; set; } = "";
    public string I2INegativePrompt { get; set; } = "";
    public string I2IStylePrompt { get; set; } = "";
    public bool IsSplitPrompt { get; set; }
    public List<RememberedCharacterState> GenCharacters { get; set; } = new();
    public List<RememberedCharacterState> I2ICharacters { get; set; } = new();
}

public class RememberedCharacterState
{
    public double EditorHeight { get; set; }
    public string PositivePrompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.5;
    public bool IsPositiveTab { get; set; } = true;
    public bool IsCollapsed { get; set; }
    public bool IsDisabled { get; set; }
    public bool UseCustomPosition { get; set; }
}

public class ReverseTaggerSettings
{
    public string ModelPath { get; set; } = "";
    public bool AddCharacterTags { get; set; } = true;
    public bool AddCopyrightTags { get; set; }
    public bool AddRatingTags { get; set; }
    public bool ReplaceUnderscoresWithSpaces { get; set; } = true;
    public double GeneralThreshold { get; set; } = 0.7;
    public double CharacterThreshold { get; set; } = 0.9;
    public bool UnloadModelAfterInference { get; set; }
}
