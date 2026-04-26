using System.Text.Json;
using Microsoft.Extensions.Logging;
using static AdvancedWeaponSystem.Config;

namespace AdvancedWeaponSystem;

public static class ConfigLoader
{
    private const string ConfigFileName = "config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true
    };

    public static Config Load(string moduleDirectory, ILogger logger)
    {
        string configDirectory = GetConfigDirectory(moduleDirectory);
        Directory.CreateDirectory(configDirectory);

        string configPath = Path.Combine(configDirectory, ConfigFileName);
        Config config;
        if (File.Exists(configPath))
        {
            string json = File.ReadAllText(configPath);
            config = Deserialize(json);
        }
        else
        {
            config = CreateDefaultConfig();
        }

        Sanitize(config);
        File.WriteAllText(configPath, Serialize(config));
        logger.LogInformation("Loaded JSON config from {ConfigPath}. weaponRules={WeaponRules}", configPath, config.WeaponDatas.Count);
        return config;
    }

    private static Config Deserialize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        if (document.RootElement.ValueKind == JsonValueKind.Object &&
            (document.RootElement.TryGetProperty("Config", out _) || document.RootElement.TryGetProperty("Weapons", out _)))
        {
            ConfigFileLayout? layout = JsonSerializer.Deserialize<ConfigFileLayout>(json, JsonOptions);
            return layout?.ToConfig() ?? CreateDefaultConfig();
        }

        Config? flatConfig = JsonSerializer.Deserialize<Config>(json, JsonOptions);
        return flatConfig ?? CreateDefaultConfig();
    }

    private static string Serialize(Config config)
    {
        return JsonSerializer.Serialize(ConfigFileLayout.FromConfig(config), JsonOptions);
    }

    private static void Sanitize(Config config)
    {
        config.GameDataUpdateUrl = config.GameDataUpdateUrl?.Trim() ?? string.Empty;
        config.WeaponDatas ??= [];

        Dictionary<string, WeaponData> normalizedWeaponData = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string configuredWeaponName, WeaponData configuredData) in config.WeaponDatas)
        {
            WeaponData data = configuredData ?? new WeaponData();
            string weaponName = string.IsNullOrWhiteSpace(configuredWeaponName) ? data.Weapon : configuredWeaponName;
            if (string.IsNullOrWhiteSpace(weaponName))
                continue;

            string normalizedWeaponName = weaponName.Trim().ToLowerInvariant();
            data.Weapon = normalizedWeaponName;
            data.AdminFlagsToIgnoreBlockUsing = data.AdminFlagsToIgnoreBlockUsing?
                .Where(static flag => !string.IsNullOrWhiteSpace(flag))
                .Select(static flag => flag.Trim())
                .ToList() ?? [];
            data.WeaponQuota = data.WeaponQuota?
                .Where(static kvp => kvp.Key >= 0 && kvp.Value >= 0)
                .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value) ?? [];
            data.MapSpecificQuota = SanitizeMapSpecificQuota(data.MapSpecificQuota);

            normalizedWeaponData[normalizedWeaponName] = data;
        }

        config.WeaponDatas = normalizedWeaponData;
    }

    private static Dictionary<string, Dictionary<int, int>> SanitizeMapSpecificQuota(Dictionary<string, Dictionary<int, int>>? mapSpecificQuota)
    {
        Dictionary<string, Dictionary<int, int>> normalized = new(StringComparer.OrdinalIgnoreCase);
        if (mapSpecificQuota is null)
            return normalized;

        foreach ((string mapName, Dictionary<int, int> quota) in mapSpecificQuota)
        {
            if (string.IsNullOrWhiteSpace(mapName) || quota is null)
                continue;

            Dictionary<int, int> cleanQuota = quota
                .Where(static kvp => kvp.Key >= 0 && kvp.Value >= 0)
                .ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);

            if (cleanQuota.Count == 0)
                continue;

            normalized[mapName.Trim().ToLowerInvariant()] = cleanQuota;
        }

        return normalized;
    }

    private static Config CreateDefaultConfig()
    {
        return new Config
        {
            WeaponDatas = new Dictionary<string, WeaponData>
            {
                ["weapon_glock"] = new()
                {
                    Clip = 30,
                    UnlimitedClip = true,
                    Damage = "1000",
                    ReloadAfterShoot = true
                },
                ["weapon_deagle"] = new()
                {
                    Clip = 1,
                    Magazines = 1,
                    UnlimitedMagazines = true,
                    OnlyHeadshot = true,
                    Damage = "*2"
                },
                ["weapon_awp"] = new()
                {
                    WeaponQuota = new Dictionary<int, int>
                    {
                        [4] = 1,
                        [8] = 2,
                        [16] = 3,
                        [32] = 4
                    },
                    AdminFlagsToIgnoreBlockUsing = ["@css/root"]
                },
                ["weapon_ssg08"] = new()
                {
                    BlockUsing = true,
                    AdminFlagsToIgnoreBlockUsing = ["@css/ban", "@css/unban"],
                    Damage = "/2"
                },
                ["weapon_aug"] = new()
                {
                    BlockUsing = true
                },
                ["weapon_ak47"] = new()
                {
                    WeaponQuota = new Dictionary<int, int>
                    {
                        [0] = 1,
                        [5] = 2,
                        [10] = 3,
                        [16] = 4,
                        [32] = 5
                    }
                }
            }
        };
    }

    public static string GetConfigDirectory(string moduleDirectory)
    {
        string moduleFullPath = Path.GetFullPath(moduleDirectory);
        DirectoryInfo? current = new(moduleFullPath);

        while (current is not null)
        {
            DirectoryInfo? parent = current.Parent;
            DirectoryInfo? grandParent = parent?.Parent;
            if (parent is not null &&
                grandParent is not null &&
                string.Equals(parent.Name, "plugins", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(grandParent.Name, "counterstrikesharp", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(grandParent.FullName, "configs", "plugins", current.Name);
            }

            current = parent;
        }

        return Path.Combine(moduleFullPath, "config");
    }

    private sealed class ConfigFileLayout
    {
        public ConfigSettings Config { get; set; } = new();
        public Dictionary<string, WeaponData> Weapons { get; set; } = [];

        public Config ToConfig()
        {
            return new Config
            {
                AutoUpdateSignatures = Config.AutoUpdateSignatures,
                GameDataUpdateUrl = Config.GameDataUpdateUrl,
                EnableCanAcquireHook = Config.EnableCanAcquireHook,
                WeaponDatas = Weapons
            };
        }

        public static ConfigFileLayout FromConfig(Config config)
        {
            return new ConfigFileLayout
            {
                Config = new ConfigSettings
                {
                    AutoUpdateSignatures = config.AutoUpdateSignatures,
                    GameDataUpdateUrl = config.GameDataUpdateUrl,
                    EnableCanAcquireHook = config.EnableCanAcquireHook
                },
                Weapons = config.WeaponDatas
            };
        }
    }

    private sealed class ConfigSettings
    {
        public bool AutoUpdateSignatures { get; set; } = true;
        public string GameDataUpdateUrl { get; set; } = "https://raw.githubusercontent.com/Micka2302/cs2-advanced-weapon-system/main/gamedata.json";
        public bool EnableCanAcquireHook { get; set; } = true;
    }
}
