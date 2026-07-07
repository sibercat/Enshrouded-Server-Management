using System.Text.Json;
using System.Text.Json.Nodes;
using EnshroudedServerManager.Core;
using EnshroudedServerManager.Models;

namespace EnshroudedServerManager.Config;

public class ConfigManager
{
    // Our app config (server_config.json) uses snake_case
    private static readonly JsonSerializerOptions AppJsonOptions = new()
    {
        PropertyNamingPolicy    = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented           = true,
        ReadCommentHandling     = JsonCommentHandling.Skip,
        AllowTrailingCommas     = true
    };

    // The game's enshrouded_server.json uses camelCase
    private static readonly JsonSerializerOptions GameJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true
    };

    private readonly string _configPath;
    public ServerConfig Config { get; private set; }

    public ConfigManager(string configPath = "server_config.json")
    {
        _configPath = configPath;
        Config = Load();
    }

    private ServerConfig Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json   = File.ReadAllText(_configPath);
                var loaded = JsonSerializer.Deserialize<ServerConfig>(json, AppJsonOptions);
                if (loaded != null)
                {
                    AppLogger.Info($"Configuration loaded from {_configPath}");
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Error loading config: {ex.Message}. Using defaults.");
        }

        var defaults = new ServerConfig();
        Save(defaults);
        AppLogger.Info("Created default configuration.");
        return defaults;
    }

    public bool Save() => Save(Config);

    private bool Save(ServerConfig config)
    {
        try
        {
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config, AppJsonOptions));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Error saving config: {ex.Message}");
            return false;
        }
    }

    // ── Write enshrouded_server.json ─────────────────────────────────────────
    public bool UpdateServerJson(string serverDir)
    {
        try
        {
            var serverJsonPath = Path.Combine(serverDir, "enshrouded_server.json");
            var gs = Config.GameSettings;

            // The server exits immediately on startup if any user group has an
            // empty password — generate one for each blank group and persist it
            // so the user can read it back in Settings → User Groups.
            bool generated = false;
            foreach (var group in Config.UserGroups)
            {
                if (string.IsNullOrWhiteSpace(group.Password))
                {
                    group.Password = GeneratePassword(group.Name);
                    generated = true;
                    AppLogger.Warning($"User group '{group.Name}' had an empty password — generated '{group.Password}'. " +
                                      "(The server refuses to start when a group password is blank.)");
                }
            }
            if (generated)
                Save();

            // Build the gameSettings block — convert minute values to nanoseconds
            var gameSettingsObj = new
            {
                playerHealthFactor              = gs.PlayerHealthFactor,
                playerManaFactor               = gs.PlayerManaFactor,
                playerStaminaFactor            = gs.PlayerStaminaFactor,
                playerBodyHeatFactor           = gs.PlayerBodyHeatFactor,
                playerDivingTimeFactor         = gs.PlayerDivingTimeFactor,
                enableDurability               = gs.EnableDurability,
                enableStarvingDebuff           = gs.EnableStarvingDebuff,
                foodBuffDurationFactor         = gs.FoodBuffDurationFactor,
                fromHungerToStarving           = (long)(gs.HungerToStarvingMinutes * 60_000_000_000L),
                shroudTimeFactor               = gs.ShroudTimeFactor,
                tombstoneMode                  = gs.TombstoneMode,
                enableGliderTurbulences        = gs.EnableGliderTurbulences,
                weatherFrequency               = gs.WeatherFrequency,
                fishingDifficulty              = gs.FishingDifficulty,
                miningDamageFactor             = gs.MiningDamageFactor,
                plantGrowthSpeedFactor         = gs.PlantGrowthSpeedFactor,
                resourceDropStackAmountFactor  = gs.ResourceDropStackAmountFactor,
                factoryProductionSpeedFactor   = gs.FactoryProductionSpeedFactor,
                perkUpgradeRecyclingFactor     = gs.PerkUpgradeRecyclingFactor,
                perkCostFactor                 = gs.PerkCostFactor,
                experienceCombatFactor         = gs.ExperienceCombatFactor,
                experienceMiningFactor         = gs.ExperienceMiningFactor,
                experienceExplorationQuestsFactor = gs.ExperienceExplorationQuestsFactor,
                randomSpawnerAmount            = gs.RandomSpawnerAmount,
                aggroPoolAmount                = gs.AggroPoolAmount,
                enemyDamageFactor              = gs.EnemyDamageFactor,
                enemyHealthFactor              = gs.EnemyHealthFactor,
                enemyStaminaFactor             = gs.EnemyStaminaFactor,
                enemyPerceptionRangeFactor     = gs.EnemyPerceptionRangeFactor,
                bossDamageFactor               = gs.BossDamageFactor,
                bossHealthFactor               = gs.BossHealthFactor,
                threatBonus                    = gs.ThreatBonus,
                pacifyAllEnemies               = gs.PacifyAllEnemies,
                tamingStartleRepercussion      = gs.TamingStartleRepercussion,
                dayTimeDuration                = (long)(gs.DayLengthMinutes  * 60_000_000_000L),
                nightTimeDuration              = (long)(gs.NightLengthMinutes * 60_000_000_000L),
                curseModifier                  = gs.CurseModifier
            };

            // Serialize user groups using camelCase
            var userGroupsNode = JsonSerializer.SerializeToNode(Config.UserGroups, GameJsonOptions)!;

            // Start from the existing file so keys owned by the server
            // (bannedAccounts, anything added in future updates) survive our rewrite.
            var serverObj = new JsonObject();
            if (File.Exists(serverJsonPath))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(serverJsonPath)) is JsonObject existing)
                        serverObj = existing;
                }
                catch { }
            }

            serverObj["name"]               = Config.ServerName;
            serverObj["saveDirectory"]      = "./savegame";
            serverObj["logDirectory"]       = "./logs";
            serverObj["ip"]                 = Config.ServerIp;
            serverObj["queryPort"]          = Config.QueryPort;
            serverObj["slotCount"]          = Config.MaxPlayers;
            serverObj["tags"]               = JsonSerializer.SerializeToNode(Config.Tags);
            serverObj["voiceChatMode"]      = Config.VoiceChatMode;
            serverObj["enableVoiceChat"]    = Config.EnableVoiceChat;
            serverObj["enableTextChat"]     = Config.EnableTextChat;
            serverObj["gameSettingsPreset"] = Config.GameSettingsPreset;
            serverObj["gameSettings"]       = JsonSerializer.SerializeToNode(gameSettingsObj);
            serverObj["userGroups"]         = userGroupsNode;

            File.WriteAllText(serverJsonPath, serverObj.ToJsonString(GameJsonOptions));
            AppLogger.Info($"Updated {serverJsonPath}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to update server JSON: {ex.Message}");
            return false;
        }
    }

    // Same shape as the passwords the server itself generates: GroupName + random suffix
    private static string GeneratePassword(string groupName)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
        var suffix = new char[8];
        for (int i = 0; i < suffix.Length; i++)
            suffix[i] = chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)];
        return groupName + new string(suffix);
    }
}
