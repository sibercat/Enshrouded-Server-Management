namespace EnshroudedServerManager.Models;

// ── Game settings (mirrors enshrouded_server.json gameSettings block) ─────────
public class GameSettings
{
    // Player stats
    public double PlayerHealthFactor              { get; set; } = 1.0;
    public double PlayerManaFactor               { get; set; } = 1.0;
    public double PlayerStaminaFactor            { get; set; } = 1.0;
    public double PlayerBodyHeatFactor           { get; set; } = 1.0;
    public double PlayerDivingTimeFactor         { get; set; } = 1.0;

    // Survival
    public bool   EnableDurability               { get; set; } = true;
    public bool   EnableStarvingDebuff           { get; set; } = false;
    public double FoodBuffDurationFactor         { get; set; } = 1.0;
    public int    HungerToStarvingMinutes        { get; set; } = 10;   // stored as minutes; converted to ns on write
    public double ShroudTimeFactor               { get; set; } = 1.0;
    public string TombstoneMode                  { get; set; } = "AddBackpackMaterials";

    // World & environment
    public bool   EnableGliderTurbulences        { get; set; } = true;
    public string WeatherFrequency               { get; set; } = "Normal";
    public string FishingDifficulty              { get; set; } = "Normal";
    public double MiningDamageFactor             { get; set; } = 1.0;
    public double PlantGrowthSpeedFactor         { get; set; } = 1.0;

    // Economy / resources
    public double ResourceDropStackAmountFactor  { get; set; } = 1.0;
    public double FactoryProductionSpeedFactor   { get; set; } = 1.0;
    public double PerkUpgradeRecyclingFactor     { get; set; } = 0.5;
    public double PerkCostFactor                 { get; set; } = 1.0;

    // Experience
    public double ExperienceCombatFactor         { get; set; } = 1.0;
    public double ExperienceMiningFactor         { get; set; } = 1.0;
    public double ExperienceExplorationQuestsFactor { get; set; } = 1.0;

    // Enemies
    public string RandomSpawnerAmount            { get; set; } = "Normal";
    public string AggroPoolAmount                { get; set; } = "Normal";
    public double EnemyDamageFactor              { get; set; } = 1.0;
    public double EnemyHealthFactor              { get; set; } = 1.0;
    public double EnemyStaminaFactor             { get; set; } = 1.0;
    public double EnemyPerceptionRangeFactor     { get; set; } = 1.0;
    public double ThreatBonus                    { get; set; } = 1.0;
    public bool   PacifyAllEnemies               { get; set; } = false;

    // Bosses
    public double BossDamageFactor               { get; set; } = 1.0;
    public double BossHealthFactor               { get; set; } = 1.0;

    // Time (stored as minutes; converted to ns on write)
    public int    DayLengthMinutes               { get; set; } = 30;
    public int    NightLengthMinutes             { get; set; } = 12;

    // Misc
    public string TamingStartleRepercussion      { get; set; } = "LoseSomeProgress";
    public string CurseModifier                  { get; set; } = "Normal";
}

// ── User group ─────────────────────────────────────────────────────────────────
public class UserGroup
{
    public string Name                  { get; set; } = "";
    public string Password              { get; set; } = "";
    public bool   CanKickBan            { get; set; } = false;
    public bool   CanAccessInventories  { get; set; } = false;
    public bool   CanEditWorld          { get; set; } = true;
    public bool   CanEditBase           { get; set; } = false;
    public bool   CanExtendBase         { get; set; } = false;
    public int    ReservedSlots         { get; set; } = 0;
}

// ── Auto-backup config ─────────────────────────────────────────────────────────
public class AutoBackupConfig
{
    public bool Enabled            { get; set; } = false;
    public int  IntervalMinutes    { get; set; } = 360;
    public int  KeepDays           { get; set; } = 7;
    public bool BackupOnShutdown   { get; set; } = true;
}

// ── Main config ────────────────────────────────────────────────────────────────
public class ServerConfig
{
    private static string RootDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EnshroudedServer");

    // Paths
    public string ServerDir    { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EnshroudedServer", "server");
    public string SteamcmdDir  { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EnshroudedServer", "steamcmd");
    public string BackupDir    { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EnshroudedServer", "backups");
    public string AppId        { get; set; } = "2278520";
    public int    MaxBackups   { get; set; } = 5;

    // Server identity
    public string ServerName   { get; set; } = "My Enshrouded Server";
    public string ServerIp     { get; set; } = "0.0.0.0";
    public int    GamePort     { get; set; } = 15636;
    public int    QueryPort    { get; set; } = 15637;
    public int    MaxPlayers   { get; set; } = 16;

    // Server features
    public List<string> Tags        { get; set; } = new();
    public string VoiceChatMode     { get; set; } = "Proximity";
    public bool   EnableVoiceChat   { get; set; } = false;
    public bool   EnableTextChat    { get; set; } = false;

    // Game preset & settings
    public string       GameSettingsPreset { get; set; } = "Default";
    public GameSettings GameSettings       { get; set; } = new();

    // User groups (Admin, Friend, Guest, Visitor)
    public List<UserGroup> UserGroups { get; set; } = new()
    {
        new() { Name = "Admin",   Password = "Admin6cnHwt-D",   CanKickBan = true,  CanAccessInventories = true,  CanEditWorld = true,  CanEditBase = true,  CanExtendBase = true,  ReservedSlots = 0 },
        new() { Name = "Friend",  Password = "Friend8p[O-PxS",  CanKickBan = false, CanAccessInventories = true,  CanEditWorld = true,  CanEditBase = true,  CanExtendBase = false, ReservedSlots = 0 },
        new() { Name = "Guest",   Password = "Guest8AUvMEaA",   CanKickBan = false, CanAccessInventories = false, CanEditWorld = true,  CanEditBase = false, CanExtendBase = false, ReservedSlots = 0 },
        new() { Name = "Visitor", Password = "Visitor!XXXXXXX", CanKickBan = false, CanAccessInventories = false, CanEditWorld = false, CanEditBase = false, CanExtendBase = false, ReservedSlots = 0 }
    };

    // Server management
    public bool   AutoRestart           { get; set; } = true;
    public int    RestartInterval       { get; set; } = 24;
    public int    RestartWarningMinutes { get; set; } = 5;
    public bool   AutoRestartOnCrash    { get; set; } = false;
    public int    CrashRestartDelaySeconds { get; set; } = 10;
    public AutoBackupConfig AutoBackup  { get; set; } = new();

    // Launch args
    public bool   PvpEnabled     { get; set; } = false;
    public int    MaxFps         { get; set; } = 60;
    public int    TickRate       { get; set; } = 64;
    public string StartupParams  { get; set; } = "";

    // Discord
    public string DiscordStatusWebhookUrl { get; set; } = "";
    public string DiscordCrashWebhookUrl  { get; set; } = "";

    // App
    public bool   AllowCommands  { get; set; } = true;
    public string LogLevel       { get; set; } = "info";
}
