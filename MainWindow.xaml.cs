using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EnshroudedServerManager.Config;
using EnshroudedServerManager.Core;
using EnshroudedServerManager.Models;

namespace EnshroudedServerManager;

public partial class MainWindow : Window
{
    private readonly ServerManager _server;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _restartTimer;
    private bool _isOperationRunning;
    private bool _shutdownInProgress;
    private bool _lastRunningState;

    // Tag name → CheckBox mapping (populated in ctor)
    private readonly Dictionary<string, CheckBox> _tagCheckBoxes = new();

    public MainWindow()
    {
        InitializeComponent();

        var rootDir    = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EnshroudedServer");
        Directory.CreateDirectory(rootDir);
        var configPath = Path.Combine(rootDir, "server_config.json");

        var configManager = new ConfigManager(configPath);
        _server = new ServerManager(configManager);

        AppLogger.SetUiCallback(line => AppendConsole(line));

        BuildTagMap();
        SetupTooltips();
        EnsureWorkingFolders();
        PopulateSettings();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();

        _restartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _restartTimer.Tick += (_, _) => UpdateScheduleLabels();
        _restartTimer.Start();

        UpdateStatusBar();

        if (!ServerManager.IsRunningAsAdmin())
            AppendConsole("WARNING: Not running as administrator — some operations may fail.");

        AppendConsole($"Enshrouded Server Manager v{ServerManager.Version} ({ServerManager.BuildDate}) ready.");
        AppendConsole($"Config: {configPath}");
        RefreshBackupList();
    }

    // ── Tag CheckBox map ──────────────────────────────────────────────────────
    private void BuildTagMap()
    {
        _tagCheckBoxes["LookingForPlayers"] = TagLookingForPlayers;
        _tagCheckBoxes["BaseBuilding"]      = TagBaseBuilding;
        _tagCheckBoxes["Exploration"]       = TagExploration;
        _tagCheckBoxes["Roleplay"]          = TagRoleplay;
        _tagCheckBoxes["English"]     = TagEnglish;
        _tagCheckBoxes["German"]      = TagGerman;
        _tagCheckBoxes["French"]      = TagFrench;
        _tagCheckBoxes["Italian"]     = TagItalian;
        _tagCheckBoxes["Japanese"]    = TagJapanese;
        _tagCheckBoxes["Korean"]      = TagKorean;
        _tagCheckBoxes["Polish"]      = TagPolish;
        _tagCheckBoxes["Portuguese"]  = TagPortuguese;
        _tagCheckBoxes["Russian"]     = TagRussian;
        _tagCheckBoxes["Spanish"]     = TagSpanish;
        _tagCheckBoxes["Thai"]        = TagThai;
        _tagCheckBoxes["Turkish"]     = TagTurkish;
        _tagCheckBoxes["Ukrainian"]   = TagUkrainian;
        _tagCheckBoxes["Chinese"]     = TagChinese;
        _tagCheckBoxes["Taiwanese"]   = TagTaiwanese;
    }

    // ── Window closing ────────────────────────────────────────────────────────
    private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownInProgress) return;
        _statusTimer.Stop();
        _restartTimer.Stop();

        if (_server.IsRunning())
        {
            var result = MessageBox.Show(
                "The server is still running. Stop it before closing?",
                "Server Running", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                _statusTimer.Start();
                _restartTimer.Start();
                return;
            }

            if (result == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                _shutdownInProgress = true;
                IsEnabled = false;
                AppendConsole("Shutting down...");
                await _server.ShutdownAsync();
                AppLogger.Shutdown();
                Application.Current.Shutdown();
                return;
            }
        }

        // Server is not running (or user chose No) — still run shutdown backup if enabled
        await _server.BackupManager.ShutdownAsync();
        AppLogger.Shutdown();
    }

    // ── Buttons ───────────────────────────────────────────────────────────────
    private async void StartBtn_Click(object sender, RoutedEventArgs e)   => await RunOperationAsync("start",   () => _server.StartAsync());
    private async void StopBtn_Click(object sender, RoutedEventArgs e)    => await RunOperationAsync("stop",    () => _server.StopAsync());
    private async void RestartBtn_Click(object sender, RoutedEventArgs e) => await RunOperationAsync("restart", () => _server.RestartAsync());
    private async void UpdateBtn_Click(object sender, RoutedEventArgs e)  => await RunOperationAsync("update",  () => _server.UpdateAsync());

    private async void CreateBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        AppendConsole("Creating backup...");
        bool ok = await _server.BackupAsync();
        AppendConsole(ok ? "Backup created successfully." : "Backup failed — check logs.");
        if (ok) RefreshBackupList();
    }

    private void SaveSettingsBtn_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private async void FirewallBtn_Click(object sender, RoutedEventArgs e)
    {
        var gamePort  = _server.Config.GamePort;
        var queryPort = _server.Config.QueryPort;

        FirewallBtn.IsEnabled = false;
        AppendConsole($"Setting up firewall rules for ports {gamePort} and {queryPort} (UDP inbound)...");

        try
        {
            var rules = new[]
            {
                ($"Enshrouded Game Port {gamePort}",  gamePort),
                ($"Enshrouded Query Port {queryPort}", queryPort)
            };

            bool allOk = true;
            foreach (var (name, port) in rules)
            {
                // Remove existing rule first (ignore failure) then add fresh
                await RunNetshAsync($"advfirewall firewall delete rule name=\"{name}\"");

                int code = await RunNetshAsync(
                    $"advfirewall firewall add rule name=\"{name}\" " +
                    $"dir=in action=allow protocol=UDP localport={port}");

                if (code == 0)
                    AppendConsole($"  ✓ Rule added: {name} (UDP {port} inbound)");
                else
                {
                    AppendConsole($"  ✗ Failed to add rule: {name} (exit code {code})");
                    allOk = false;
                }
            }

            if (allOk)
            {
                AppendConsole("Firewall setup complete.");
                MessageBox.Show(
                    $"Firewall rules added successfully.\n\n" +
                    $"  UDP {gamePort}  — Game traffic\n" +
                    $"  UDP {queryPort}  — Steam server browser",
                    "Firewall Setup", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("One or more firewall rules failed. Check the console for details.",
                    "Firewall Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Firewall setup error: {ex.Message}");
            MessageBox.Show($"Error setting up firewall:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            FirewallBtn.IsEnabled = true;
        }
    }

    private static async Task<int> RunNetshAsync(string args)
    {
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "netsh",
            Arguments       = args,
            UseShellExecute = false,
            CreateNoWindow  = true
        };
        proc.Start();
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    private void RefreshBackupBtn_Click(object sender, RoutedEventArgs e) => RefreshBackupList();
    private void RefreshLogsBtn_Click(object sender, RoutedEventArgs e)  => RefreshLogs();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        const string apiUrl      = "https://api.github.com/repos/sibercat/Enshrouded-Server-Management/releases/latest";
        const string releasesUrl = "https://github.com/sibercat/Enshrouded-Server-Management/releases";

        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("EnshroudedServerManager");
            var json = await http.GetStringAsync(apiUrl);

            // Parse tag_name from JSON (e.g. "v0.2.0" or "0.2.0")
            var doc     = System.Text.Json.JsonDocument.Parse(json);
            var tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var remote  = tagName.TrimStart('v');
            var current = ServerManager.Version;

            if (Version.TryParse(remote, out var remoteVer) &&
                Version.TryParse(current, out var currentVer) &&
                remoteVer > currentVer)
            {
                var result = MessageBox.Show(
                    $"A new version is available: v{remote}\nYou have: v{current}\n\nOpen the releases page?",
                    "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(releasesUrl) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show($"You are up to date! (v{current})", "Check for Updates",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not check for updates:\n{ex.Message}", "Check for Updates",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void GamePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateGameplaySettingsEnabled();

    private void ResetGameSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset all gameplay settings to their default values?",
            "Reset to Defaults", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _server.Config.GameSettings   = new GameSettings();
        _server.Config.GameSettingsPreset = "Default";
        PopulateGameplayTab();
    }

    private void UpdateGameplaySettingsEnabled()
    {
        bool isCustom = (GamePresetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Custom";

        foreach (var ctrl in new System.Windows.FrameworkElement[]
        {
            // Player Stats
            PlayerHealthInput, PlayerManaInput, PlayerStaminaInput,
            PlayerBodyHeatInput, PlayerDivingInput,
            // Survival
            EnableDurabilityCheck, EnableStarvingCheck, FoodBuffDurationInput,
            HungerToStarvingInput, ShroudTimeInput, TombstoneModeCombo,
            // World & Environment
            WeatherFreqCombo, FishingDifficultyCombo, EnableGliderCheck,
            MiningDamageInput, PlantGrowthInput, DayLengthInput, NightLengthInput,
            // Resources & Economy
            ResourceDropInput, FactorySpeedInput, PerkRecyclingInput, PerkCostInput,
            // Experience
            CombatXpInput, MiningXpInput, ExploreXpInput,
            // Enemies
            EnemyAmountCombo, AggroPoolCombo, EnemyDamageInput, EnemyHealthInput,
            EnemyStaminaInput, EnemyPerceptionInput, ThreatBonusInput,
            PacifyEnemiesCheck, TamingRepercussionCombo,
            // Bosses
            BossDamageInput, BossHealthInput,
            // Misc
            CurseModifierCombo
        })
        {
            ctrl.IsEnabled = isCustom;
        }
    }

    // ── Operation runner ──────────────────────────────────────────────────────
    private async Task RunOperationAsync(string name, Func<Task<bool>> op)
    {
        if (_isOperationRunning) return;
        _isOperationRunning = true;
        SetButtons(false);
        AppendConsole($"Executing: {name}...");

        try
        {
            bool ok = await op();
            AppendConsole(ok ? $"'{name}' completed." : $"'{name}' failed — check logs.");
            if (!ok)
                MessageBox.Show($"Failed to {name} server.", "Operation Failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Error in '{name}': {ex.Message}");
            MessageBox.Show($"Error during {name}:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isOperationRunning = false;
            SetButtons(true);
            UpdateStatusBar();
        }
    }

    // ── Status bar ────────────────────────────────────────────────────────────
    private void UpdateStatusBar()
    {
        bool running = _server.IsRunning();

        if (running != _lastRunningState)
        {
            StatusIndicator.Text       = running ? "Running" : "Stopped";
            StatusIndicator.Foreground = running ? Brushes.Green : Brushes.Red;
            AppendConsole($"Server status: {(running ? "Running" : "Stopped")}");

            // If server went from running to stopped without us stopping it — crash
            if (!running && _lastRunningState)
                _ = _server.NotifyCrashIfUnexpectedAsync();

            _lastRunningState = running;
        }

        VersionLabel.Text    = $"  |  Server Version: {_server.ServerVersion}";
        ServerNameLabel.Text = $"Server: {_server.Config.ServerName}";

        if (running)
        {
            var (cpu, mem) = _server.GetMetrics();
            CpuBar.Value     = cpu;
            CpuLabel.Text    = $"CPU: {cpu:F1}%";
            MemoryBar.Value  = mem;
            MemoryLabel.Text = $"Memory: {mem:F0} MB";
        }
        else
        {
            CpuBar.Value = MemoryBar.Value = 0;
            CpuLabel.Text    = "CPU: 0%";
            MemoryLabel.Text = "Memory: 0 MB";
        }
    }

    private void UpdateScheduleLabels()
    {
        var nr = _server.RestartManager.NextRestart;
        NextRestartLabel.Text = nr.HasValue
            ? $"Next Restart: {nr.Value:yyyy-MM-dd HH:mm:ss}"
            : "Next Restart: Not scheduled";

        var nb = _server.BackupManager.NextBackup;
        NextBackupLabel.Text = nb.HasValue
            ? $"Next Backup: {nb.Value:yyyy-MM-dd HH:mm:ss}"
            : "Next Backup: Not scheduled";
    }

    // ── Populate settings ─────────────────────────────────────────────────────
    private void PopulateSettings()
    {
        var c  = _server.Config;
        var gs = c.GameSettings;

        // ── Server tab ──
        ServerNameInput.Text    = c.ServerName;
        ServerIpInput.Text      = c.ServerIp;
        GamePortInput.Text      = c.GamePort.ToString();
        QueryPortInput.Text     = c.QueryPort.ToString();
        MaxPlayersInput.Text    = c.MaxPlayers.ToString();
        StartupParamsInput.Text = c.StartupParams;

        PvpEnabledCheck.IsChecked     = c.PvpEnabled;
        MaxFpsInput.Text              = c.MaxFps.ToString();
        TickRateInput.Text            = c.TickRate.ToString();

        EnableVoiceChatCheck.IsChecked = c.EnableVoiceChat;
        EnableTextChatCheck.IsChecked  = c.EnableTextChat;
        SetCombo(VoiceChatModeCombo, c.VoiceChatMode);

        // Tags
        foreach (var kvp in _tagCheckBoxes)
            kvp.Value.IsChecked = c.Tags.Contains(kvp.Key);

        AutoRestartCheck.IsChecked        = c.AutoRestart;
        RestartIntervalInput.Text         = c.RestartInterval.ToString();
        RestartWarningInput.Text          = c.RestartWarningMinutes.ToString();
        AutoRestartOnCrashCheck.IsChecked = c.AutoRestartOnCrash;
        CrashRestartDelayInput.Text       = c.CrashRestartDelaySeconds.ToString();
        DiscordStatusWebhookInput.Text = c.DiscordStatusWebhookUrl;
        DiscordCrashWebhookInput.Text  = c.DiscordCrashWebhookUrl;

        // ── Gameplay tab ──
        PopulateGameplayTab();

        // ── User Groups tab ──
        PopulateUserGroup(c, "Admin",   AdminPassInput,   AdminReservedInput,   AdminCanKickBan,   AdminCanAccessInventories,   AdminCanEditWorld,   AdminCanEditBase,   AdminCanExtendBase);
        PopulateUserGroup(c, "Friend",  FriendPassInput,  FriendReservedInput,  FriendCanKickBan,  FriendCanAccessInventories,  FriendCanEditWorld,  FriendCanEditBase,  FriendCanExtendBase);
        PopulateUserGroup(c, "Guest",   GuestPassInput,   GuestReservedInput,   GuestCanKickBan,   GuestCanAccessInventories,   GuestCanEditWorld,   GuestCanEditBase,   GuestCanExtendBase);
        PopulateUserGroup(c, "Visitor", VisitorPassInput, VisitorReservedInput, VisitorCanKickBan, VisitorCanAccessInventories, VisitorCanEditWorld, VisitorCanEditBase, VisitorCanExtendBase);

        // ── Backup tab ──
        AutoBackupCheck.IsChecked       = c.AutoBackup.Enabled;
        BackupIntervalInput.Text        = c.AutoBackup.IntervalMinutes.ToString();
        RetentionDaysInput.Text         = c.AutoBackup.KeepDays.ToString();
        BackupOnShutdownCheck.IsChecked = c.AutoBackup.BackupOnShutdown;
    }

    private void PopulateGameplayTab()
    {
        var c  = _server.Config;
        var gs = c.GameSettings;

        SetCombo(GamePresetCombo, c.GameSettingsPreset);
        UpdateGameplaySettingsEnabled();

        PlayerHealthInput.Text    = gs.PlayerHealthFactor.ToString("F2");
        PlayerManaInput.Text      = gs.PlayerManaFactor.ToString("F2");
        PlayerStaminaInput.Text   = gs.PlayerStaminaFactor.ToString("F2");
        PlayerBodyHeatInput.Text  = gs.PlayerBodyHeatFactor.ToString("F2");
        PlayerDivingInput.Text    = gs.PlayerDivingTimeFactor.ToString("F2");

        EnableDurabilityCheck.IsChecked = gs.EnableDurability;
        EnableStarvingCheck.IsChecked   = gs.EnableStarvingDebuff;
        FoodBuffDurationInput.Text      = gs.FoodBuffDurationFactor.ToString("F2");
        HungerToStarvingInput.Text      = gs.HungerToStarvingMinutes.ToString();
        ShroudTimeInput.Text            = gs.ShroudTimeFactor.ToString("F2");
        SetCombo(TombstoneModeCombo, gs.TombstoneMode);

        SetCombo(WeatherFreqCombo,       gs.WeatherFrequency);
        SetCombo(FishingDifficultyCombo, gs.FishingDifficulty);
        EnableGliderCheck.IsChecked     = gs.EnableGliderTurbulences;
        MiningDamageInput.Text          = gs.MiningDamageFactor.ToString("F2");
        PlantGrowthInput.Text           = gs.PlantGrowthSpeedFactor.ToString("F2");
        DayLengthInput.Text             = gs.DayLengthMinutes.ToString();
        NightLengthInput.Text           = gs.NightLengthMinutes.ToString();

        ResourceDropInput.Text  = gs.ResourceDropStackAmountFactor.ToString("F2");
        FactorySpeedInput.Text  = gs.FactoryProductionSpeedFactor.ToString("F2");
        PerkRecyclingInput.Text = gs.PerkUpgradeRecyclingFactor.ToString("F2");
        PerkCostInput.Text      = gs.PerkCostFactor.ToString("F2");

        CombatXpInput.Text  = gs.ExperienceCombatFactor.ToString("F2");
        MiningXpInput.Text  = gs.ExperienceMiningFactor.ToString("F2");
        ExploreXpInput.Text = gs.ExperienceExplorationQuestsFactor.ToString("F2");

        SetCombo(EnemyAmountCombo, gs.RandomSpawnerAmount);
        SetCombo(AggroPoolCombo,   gs.AggroPoolAmount);
        EnemyDamageInput.Text     = gs.EnemyDamageFactor.ToString("F2");
        EnemyHealthInput.Text     = gs.EnemyHealthFactor.ToString("F2");
        EnemyStaminaInput.Text    = gs.EnemyStaminaFactor.ToString("F2");
        EnemyPerceptionInput.Text = gs.EnemyPerceptionRangeFactor.ToString("F2");
        ThreatBonusInput.Text     = gs.ThreatBonus.ToString("F2");
        PacifyEnemiesCheck.IsChecked = gs.PacifyAllEnemies;
        SetCombo(TamingRepercussionCombo, gs.TamingStartleRepercussion);

        BossDamageInput.Text = gs.BossDamageFactor.ToString("F2");
        BossHealthInput.Text = gs.BossHealthFactor.ToString("F2");

        SetCombo(CurseModifierCombo, gs.CurseModifier);
    }

    private static void PopulateUserGroup(ServerConfig c, string name,
        TextBox passBox, TextBox reservedBox,
        CheckBox kickBan, CheckBox inventory, CheckBox editWorld, CheckBox editBase, CheckBox extendBase)
    {
        var g = c.UserGroups.FirstOrDefault(u => u.Name == name);
        if (g == null) return;
        passBox.Text              = g.Password;
        reservedBox.Text          = g.ReservedSlots.ToString();
        kickBan.IsChecked         = g.CanKickBan;
        inventory.IsChecked       = g.CanAccessInventories;
        editWorld.IsChecked       = g.CanEditWorld;
        editBase.IsChecked        = g.CanEditBase;
        extendBase.IsChecked      = g.CanExtendBase;
    }

    // ── Save settings ─────────────────────────────────────────────────────────
    private void SaveSettings()
    {
        try
        {
            var c  = _server.Config;
            var gs = c.GameSettings;

            // ── Server tab ──
            c.ServerName   = ServerNameInput.Text.Trim();
            c.ServerIp     = ServerIpInput.Text.Trim();
            c.GamePort     = ParseInt(GamePortInput.Text,   c.GamePort);
            c.QueryPort    = ParseInt(QueryPortInput.Text,  c.QueryPort);
            c.MaxPlayers   = ParseInt(MaxPlayersInput.Text, c.MaxPlayers);
            c.StartupParams = StartupParamsInput.Text.Trim();

            c.PvpEnabled     = PvpEnabledCheck.IsChecked == true;
            c.MaxFps         = ParseInt(MaxFpsInput.Text,   c.MaxFps);
            c.TickRate       = ParseInt(TickRateInput.Text,  c.TickRate);

            c.EnableVoiceChat = EnableVoiceChatCheck.IsChecked == true;
            c.EnableTextChat  = EnableTextChatCheck.IsChecked  == true;
            c.VoiceChatMode   = (VoiceChatModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? c.VoiceChatMode;

            c.Tags = _tagCheckBoxes
                .Where(kvp => kvp.Value.IsChecked == true)
                .Select(kvp => kvp.Key)
                .ToList();

            c.AutoRestart              = AutoRestartCheck.IsChecked == true;
            c.RestartInterval          = ParseInt(RestartIntervalInput.Text,    c.RestartInterval);
            c.RestartWarningMinutes    = ParseInt(RestartWarningInput.Text,     c.RestartWarningMinutes);
            c.AutoRestartOnCrash       = AutoRestartOnCrashCheck.IsChecked == true;
            c.CrashRestartDelaySeconds = ParseInt(CrashRestartDelayInput.Text,  c.CrashRestartDelaySeconds);
            c.DiscordStatusWebhookUrl = DiscordStatusWebhookInput.Text.Trim();
            c.DiscordCrashWebhookUrl  = DiscordCrashWebhookInput.Text.Trim();

            // ── Gameplay tab ──
            c.GameSettingsPreset = (GamePresetCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? c.GameSettingsPreset;

            gs.PlayerHealthFactor      = ParseDouble(PlayerHealthInput.Text,   gs.PlayerHealthFactor);
            gs.PlayerManaFactor        = ParseDouble(PlayerManaInput.Text,     gs.PlayerManaFactor);
            gs.PlayerStaminaFactor     = ParseDouble(PlayerStaminaInput.Text,  gs.PlayerStaminaFactor);
            gs.PlayerBodyHeatFactor    = ParseDouble(PlayerBodyHeatInput.Text, gs.PlayerBodyHeatFactor);
            gs.PlayerDivingTimeFactor  = ParseDouble(PlayerDivingInput.Text,   gs.PlayerDivingTimeFactor);

            gs.EnableDurability        = EnableDurabilityCheck.IsChecked == true;
            gs.EnableStarvingDebuff    = EnableStarvingCheck.IsChecked   == true;
            gs.FoodBuffDurationFactor  = ParseDouble(FoodBuffDurationInput.Text, gs.FoodBuffDurationFactor);
            gs.HungerToStarvingMinutes = ParseInt(HungerToStarvingInput.Text,    gs.HungerToStarvingMinutes);
            gs.ShroudTimeFactor        = ParseDouble(ShroudTimeInput.Text,       gs.ShroudTimeFactor);
            gs.TombstoneMode           = ComboValue(TombstoneModeCombo,          gs.TombstoneMode);

            gs.WeatherFrequency        = ComboValue(WeatherFreqCombo,       gs.WeatherFrequency);
            gs.FishingDifficulty       = ComboValue(FishingDifficultyCombo, gs.FishingDifficulty);
            gs.EnableGliderTurbulences = EnableGliderCheck.IsChecked == true;
            gs.MiningDamageFactor      = ParseDouble(MiningDamageInput.Text, gs.MiningDamageFactor);
            gs.PlantGrowthSpeedFactor  = ParseDouble(PlantGrowthInput.Text,  gs.PlantGrowthSpeedFactor);
            gs.DayLengthMinutes        = ParseInt(DayLengthInput.Text,   gs.DayLengthMinutes);
            gs.NightLengthMinutes      = ParseInt(NightLengthInput.Text,  gs.NightLengthMinutes);

            gs.ResourceDropStackAmountFactor = ParseDouble(ResourceDropInput.Text,  gs.ResourceDropStackAmountFactor);
            gs.FactoryProductionSpeedFactor  = ParseDouble(FactorySpeedInput.Text,  gs.FactoryProductionSpeedFactor);
            gs.PerkUpgradeRecyclingFactor    = ParseDouble(PerkRecyclingInput.Text,  gs.PerkUpgradeRecyclingFactor);
            gs.PerkCostFactor                = ParseDouble(PerkCostInput.Text,       gs.PerkCostFactor);

            gs.ExperienceCombatFactor             = ParseDouble(CombatXpInput.Text,  gs.ExperienceCombatFactor);
            gs.ExperienceMiningFactor             = ParseDouble(MiningXpInput.Text,  gs.ExperienceMiningFactor);
            gs.ExperienceExplorationQuestsFactor  = ParseDouble(ExploreXpInput.Text, gs.ExperienceExplorationQuestsFactor);

            gs.RandomSpawnerAmount      = ComboValue(EnemyAmountCombo, gs.RandomSpawnerAmount);
            gs.AggroPoolAmount          = ComboValue(AggroPoolCombo,   gs.AggroPoolAmount);
            gs.EnemyDamageFactor        = ParseDouble(EnemyDamageInput.Text,     gs.EnemyDamageFactor);
            gs.EnemyHealthFactor        = ParseDouble(EnemyHealthInput.Text,     gs.EnemyHealthFactor);
            gs.EnemyStaminaFactor       = ParseDouble(EnemyStaminaInput.Text,    gs.EnemyStaminaFactor);
            gs.EnemyPerceptionRangeFactor = ParseDouble(EnemyPerceptionInput.Text, gs.EnemyPerceptionRangeFactor);
            gs.ThreatBonus              = ParseDouble(ThreatBonusInput.Text,     gs.ThreatBonus);
            gs.PacifyAllEnemies         = PacifyEnemiesCheck.IsChecked == true;
            gs.TamingStartleRepercussion = ComboValue(TamingRepercussionCombo,  gs.TamingStartleRepercussion);

            gs.BossDamageFactor = ParseDouble(BossDamageInput.Text, gs.BossDamageFactor);
            gs.BossHealthFactor = ParseDouble(BossHealthInput.Text, gs.BossHealthFactor);

            gs.CurseModifier = ComboValue(CurseModifierCombo, gs.CurseModifier);

            // ── User Groups tab ──
            SaveUserGroup(c, "Admin",   AdminPassInput,   AdminReservedInput,   AdminCanKickBan,   AdminCanAccessInventories,   AdminCanEditWorld,   AdminCanEditBase,   AdminCanExtendBase);
            SaveUserGroup(c, "Friend",  FriendPassInput,  FriendReservedInput,  FriendCanKickBan,  FriendCanAccessInventories,  FriendCanEditWorld,  FriendCanEditBase,  FriendCanExtendBase);
            SaveUserGroup(c, "Guest",   GuestPassInput,   GuestReservedInput,   GuestCanKickBan,   GuestCanAccessInventories,   GuestCanEditWorld,   GuestCanEditBase,   GuestCanExtendBase);
            SaveUserGroup(c, "Visitor", VisitorPassInput, VisitorReservedInput, VisitorCanKickBan, VisitorCanAccessInventories, VisitorCanEditWorld, VisitorCanEditBase, VisitorCanExtendBase);

            // ── Backup tab ──
            c.AutoBackup.Enabled          = AutoBackupCheck.IsChecked == true;
            c.AutoBackup.IntervalMinutes  = ParseInt(BackupIntervalInput.Text, c.AutoBackup.IntervalMinutes);
            c.AutoBackup.KeepDays         = ParseInt(RetentionDaysInput.Text,  c.AutoBackup.KeepDays);
            c.AutoBackup.BackupOnShutdown = BackupOnShutdownCheck.IsChecked == true;

            if (_server.SaveConfig())
            {
                AppendConsole("Settings saved.");
                ServerNameLabel.Text = $"Server: {c.ServerName}";
                MessageBox.Show("Settings saved successfully.", "Saved",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("Failed to save settings.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Error saving settings: {ex.Message}");
            MessageBox.Show($"Error saving settings:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void SaveUserGroup(ServerConfig c, string name,
        TextBox passBox, TextBox reservedBox,
        CheckBox kickBan, CheckBox inventory, CheckBox editWorld, CheckBox editBase, CheckBox extendBase)
    {
        var g = c.UserGroups.FirstOrDefault(u => u.Name == name);
        if (g == null)
        {
            g = new UserGroup { Name = name };
            c.UserGroups.Add(g);
        }
        g.Password             = passBox.Text;
        g.ReservedSlots        = ParseInt(reservedBox.Text, g.ReservedSlots);
        g.CanKickBan           = kickBan.IsChecked    == true;
        g.CanAccessInventories = inventory.IsChecked  == true;
        g.CanEditWorld         = editWorld.IsChecked  == true;
        g.CanEditBase          = editBase.IsChecked   == true;
        g.CanExtendBase        = extendBase.IsChecked == true;
    }

    // ── Backup list ───────────────────────────────────────────────────────────
    private void RefreshBackupList()
    {
        try
        {
            var dir = _server.Config.BackupDir;
            if (!Directory.Exists(dir)) { BackupList.Text = "Backup directory not found."; return; }

            var files = Directory.GetFiles(dir, "enshrouded_backup_*.zip")
                                 .OrderByDescending(f => f)
                                 .Select(Path.GetFileName)
                                 .ToList();

            BackupList.Text = files.Count > 0 ? string.Join(Environment.NewLine, files) : "No backups found.";
        }
        catch (Exception ex) { BackupList.Text = $"Error: {ex.Message}"; }
    }

    // ── Log viewer ────────────────────────────────────────────────────────────
    private void RefreshLogs()
    {
        try
        {
            var logPath = Path.Combine(_server.Config.ServerDir, "logs", "enshrouded_server.log");
            if (!File.Exists(logPath)) { LogViewer.Text = "No server log file found."; return; }

            using var fs     = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            LogViewer.Text   = reader.ReadToEnd();
            LogViewer.ScrollToEnd();
        }
        catch (Exception ex) { LogViewer.Text = $"Error reading logs: {ex.Message}"; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void EnsureWorkingFolders()
    {
        foreach (var dir in new[] {
            _server.Config.ServerDir,
            _server.Config.SteamcmdDir,
            _server.Config.BackupDir,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EnshroudedServer", "logs") })
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AppLogger.Info($"Created: {dir}");
            }
        }
    }

    private void AppendConsole(string line)
    {
        Dispatcher.InvokeAsync(() =>
        {
            ConsoleOutput.AppendText(line + Environment.NewLine);
            ConsoleOutput.ScrollToEnd();
        });
    }

    private void SetButtons(bool enabled)
    {
        StartBtn.IsEnabled = StopBtn.IsEnabled = RestartBtn.IsEnabled = UpdateBtn.IsEnabled = enabled;
    }

    private static void SetCombo(ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
            if (item.Content?.ToString() == value) { combo.SelectedItem = item; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string ComboValue(ComboBox combo, string fallback)
        => (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text, out int v) ? v : fallback;

    private static double ParseDouble(string text, double fallback)
        => double.TryParse(text,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out double v) ? v : fallback;

    // ── Tooltips ──────────────────────────────────────────────────────────────
    private void SetupTooltips()
    {
        // ── Server Control tab ──
        StartBtn.ToolTip   = "Install (if needed) and start the Enshrouded dedicated server.";
        StopBtn.ToolTip    = "Gracefully stop the running server process.";
        RestartBtn.ToolTip = "Stop then immediately start the server again.";
        UpdateBtn.ToolTip  = "Download/update the server files via SteamCMD (app ID 2278520).";

        // ── Server Settings — Identity ──
        ServerNameInput.ToolTip    = "Display name shown in the server browser. This also writes the \"name\" field in enshrouded_server.json.";
        ServerIpInput.ToolTip      = "IP address the server binds to. Leave blank or use 0.0.0.0 to bind all interfaces.";
        GamePortInput.ToolTip      = "UDP port for game traffic (default: 15636). Must be open in your firewall/router.";
        QueryPortInput.ToolTip     = "UDP port for Steam server queries (default: 15637). Required for server-browser discovery.";
        MaxPlayersInput.ToolTip    = "Maximum simultaneous players allowed (1–16).";
        StartupParamsInput.ToolTip = "Extra command-line arguments appended when launching enshrouded_server.exe.";

        // ── Server Settings — Launch Options ──
        PvpEnabledCheck.ToolTip    = "Enable PvP mode. Adds the -pvp flag to the server launch command.";
        MaxFpsInput.ToolTip        = "Server-side frame-rate cap. Lower values reduce CPU usage (e.g. 30). Affects physics tick smoothness.";
        TickRateInput.ToolTip      = "Network update rate in Hz. Higher = more responsive but more bandwidth (default: 60).";

        // ── Server Settings — Voice/Chat ──
        VoiceChatModeCombo.ToolTip     = "Voice chat scope: Proximity (nearby players only) or Global (everyone on the server).";
        EnableVoiceChatCheck.ToolTip   = "Enable in-game proximity/global voice chat.";
        EnableTextChatCheck.ToolTip    = "Enable the in-game text chat box.";

        // ── Server Settings — Tags ──
        TagLookingForPlayers.ToolTip = "Tag: marks server as actively recruiting players.";
        TagBaseBuilding.ToolTip      = "Tag: base-building focused server.";
        TagExploration.ToolTip       = "Tag: exploration focused server.";
        TagRoleplay.ToolTip          = "Tag: roleplay server.";
        TagEnglish.ToolTip     = "Language tag: English-speaking server.";
        TagGerman.ToolTip      = "Language tag: German-speaking server.";
        TagFrench.ToolTip      = "Language tag: French-speaking server.";
        TagItalian.ToolTip     = "Language tag: Italian-speaking server.";
        TagJapanese.ToolTip    = "Language tag: Japanese-speaking server.";
        TagKorean.ToolTip      = "Language tag: Korean-speaking server.";
        TagPolish.ToolTip      = "Language tag: Polish-speaking server.";
        TagPortuguese.ToolTip  = "Language tag: Portuguese-speaking server.";
        TagRussian.ToolTip     = "Language tag: Russian-speaking server.";
        TagSpanish.ToolTip     = "Language tag: Spanish-speaking server.";
        TagThai.ToolTip        = "Language tag: Thai-speaking server.";
        TagTurkish.ToolTip     = "Language tag: Turkish-speaking server.";
        TagUkrainian.ToolTip   = "Language tag: Ukrainian-speaking server.";
        TagChinese.ToolTip     = "Language tag: Simplified Chinese-speaking server.";
        TagTaiwanese.ToolTip   = "Language tag: Traditional Chinese (Taiwanese)-speaking server.";

        // ── Server Settings — Auto Restart ──
        AutoRestartCheck.ToolTip    = "Automatically restart the server on a recurring schedule.";
        RestartIntervalInput.ToolTip = "How often (in minutes) to restart the server when auto-restart is enabled.";
        RestartWarningInput.ToolTip  = "Minutes before restart at which a warning is broadcast/logged.";

        // ── Gameplay Settings — Preset ──
        GamePresetCombo.ToolTip = "Overall difficulty preset: Default, Relaxed, Hard, Survival, or Custom. " +
                                  "Custom lets you fine-tune every factor below.";

        // ── Gameplay — Player Stats ──
        PlayerHealthInput.ToolTip   = "Multiplier for player max health. 1.0 = normal. (e.g. 2.0 = double health)";
        PlayerManaInput.ToolTip     = "Multiplier for player max mana. 1.0 = normal.";
        PlayerStaminaInput.ToolTip  = "Multiplier for player max stamina. 1.0 = normal.";
        PlayerBodyHeatInput.ToolTip = "Multiplier for how fast the player generates/loses body heat. " +
                                      "Higher = warmer longer. 1.0 = normal.";
        PlayerDivingInput.ToolTip   = "Multiplier for how long the player can dive underwater. 1.0 = normal.";

        // ── Gameplay — Survival ──
        EnableDurabilityCheck.ToolTip  = "When enabled, gear loses durability with use and can break.";
        EnableStarvingCheck.ToolTip    = "When enabled, going without food applies a starvation debuff.";
        FoodBuffDurationInput.ToolTip  = "Multiplier for how long food buffs last. 1.0 = normal. 2.0 = double duration.";
        HungerToStarvingInput.ToolTip  = "Time in minutes from fully fed to starving. Default ≈ 16 min (960 s). " +
                                         "Stored as nanoseconds in enshrouded_server.json.";
        ShroudTimeInput.ToolTip        = "Multiplier for how long the player can spend inside the Shroud. " +
                                         "1.0 = normal. Higher = more time allowed.";
        TombstoneModeCombo.ToolTip     = "What happens to your items on death:\n" +
                                         "  NoTombstone – items drop where you die\n" +
                                         "  AddBackpackMaterials – materials return to backpack\n" +
                                         "  Tombstone – items stay in a grave to reclaim\n" +
                                         "  Everything – all items kept on respawn";

        // ── Gameplay — World & Environment ──
        WeatherFreqCombo.ToolTip       = "How often weather events occur: Disabled, Rare, Normal, or Often.";
        FishingDifficultyCombo.ToolTip = "Fishing mini-game difficulty: Easy, Normal, Hard.";
        EnableGliderCheck.ToolTip      = "When enabled, gliding is affected by wind turbulences. Disable for smoother flight.";
        MiningDamageInput.ToolTip      = "Multiplier for damage dealt when mining ore/stone. 1.0 = normal. " +
                                         "Higher = mine faster.";
        PlantGrowthInput.ToolTip       = "Multiplier for crop/plant growth speed. 1.0 = normal. 2.0 = twice as fast.";
        DayLengthInput.ToolTip         = "Length of daytime in minutes. Default = 30 min. " +
                                         "Stored as nanoseconds in enshrouded_server.json.";
        NightLengthInput.ToolTip       = "Length of nighttime in minutes. Default = 10 min. " +
                                         "Stored as nanoseconds in enshrouded_server.json.";

        // ── Gameplay — Resources & Economy ──
        ResourceDropInput.ToolTip  = "Multiplier for stack sizes of dropped resources. 1.0 = normal. " +
                                     "Higher = more resources per node.";
        FactorySpeedInput.ToolTip  = "Multiplier for factory/crafting station production speed. 1.0 = normal.";
        PerkRecyclingInput.ToolTip = "Multiplier for how much material is returned when recycling upgraded perks. " +
                                     "1.0 = normal (full recycle).";
        PerkCostInput.ToolTip      = "Multiplier for perk upgrade costs. 0.5 = half price. 2.0 = double price.";

        // ── Gameplay — Experience ──
        CombatXpInput.ToolTip  = "Multiplier for XP gained from killing enemies. 1.0 = normal.";
        MiningXpInput.ToolTip  = "Multiplier for XP gained from mining. 1.0 = normal.";
        ExploreXpInput.ToolTip = "Multiplier for XP gained from exploration and quest completion. 1.0 = normal.";

        // ── Gameplay — Enemies ──
        EnemyAmountCombo.ToolTip      = "Overall enemy spawn density: Few, Normal, Many, or Extreme.";
        AggroPoolCombo.ToolTip        = "Size of the aggro pool (how many enemies can be simultaneously hostile): " +
                                        "Low, Normal, High, or Extreme.";
        EnemyDamageInput.ToolTip      = "Multiplier for damage enemies deal to players. 1.0 = normal. " +
                                        "0.5 = half damage.";
        EnemyHealthInput.ToolTip      = "Multiplier for enemy max health. 1.0 = normal. 2.0 = double HP.";
        EnemyStaminaInput.ToolTip     = "Multiplier for enemy stamina (used for enemy actions/attacks). 1.0 = normal.";
        EnemyPerceptionInput.ToolTip  = "Multiplier for enemy detection/vision range. 1.0 = normal. " +
                                        "Lower = shorter sight radius.";
        ThreatBonusInput.ToolTip      = "Bonus multiplier added to the overall threat level, increasing enemy aggression.";
        PacifyEnemiesCheck.ToolTip    = "When enabled, all enemies are pacified and will not attack players.";
        TamingRepercussionCombo.ToolTip = "What happens when a taming attempt startles an animal:\n" +
                                          "  None – no penalty\n" +
                                          "  Hurt – animal takes damage\n" +
                                          "  Kill – animal dies";

        // ── Gameplay — Bosses ──
        BossDamageInput.ToolTip = "Multiplier for damage bosses deal to players. 1.0 = normal.";
        BossHealthInput.ToolTip = "Multiplier for boss max health. 1.0 = normal. 2.0 = double HP.";

        // ── Gameplay — Miscellaneous ──
        CurseModifierCombo.ToolTip = "Adjusts the potency/frequency of curses: None, Low, Normal, or High.";

        // ── User Groups ──
        const string passHint     = "Password required to join this group. Leave blank for no password.";
        const string reservedHint = "Number of player slots reserved exclusively for this group.";
        const string kickBanHint  = "Allow members of this group to kick and ban other players.";
        const string inventoryHint = "Allow members of this group to access other players' inventories.";
        const string editWorldHint = "Allow members of this group to modify the world (terrain, objects).";
        const string editBaseHint  = "Allow members of this group to build/edit bases.";
        const string extendBaseHint = "Allow members of this group to extend base building boundaries.";

        AdminPassInput.ToolTip   = passHint;      AdminReservedInput.ToolTip   = reservedHint;
        AdminCanKickBan.ToolTip  = kickBanHint;   AdminCanAccessInventories.ToolTip = inventoryHint;
        AdminCanEditWorld.ToolTip = editWorldHint; AdminCanEditBase.ToolTip    = editBaseHint;
        AdminCanExtendBase.ToolTip = extendBaseHint;

        FriendPassInput.ToolTip  = passHint;      FriendReservedInput.ToolTip  = reservedHint;
        FriendCanKickBan.ToolTip = kickBanHint;   FriendCanAccessInventories.ToolTip = inventoryHint;
        FriendCanEditWorld.ToolTip = editWorldHint; FriendCanEditBase.ToolTip  = editBaseHint;
        FriendCanExtendBase.ToolTip = extendBaseHint;

        GuestPassInput.ToolTip   = passHint;      GuestReservedInput.ToolTip   = reservedHint;
        GuestCanKickBan.ToolTip  = kickBanHint;   GuestCanAccessInventories.ToolTip = inventoryHint;
        GuestCanEditWorld.ToolTip = editWorldHint; GuestCanEditBase.ToolTip    = editBaseHint;
        GuestCanExtendBase.ToolTip = extendBaseHint;

        VisitorPassInput.ToolTip  = passHint;     VisitorReservedInput.ToolTip  = reservedHint;
        VisitorCanKickBan.ToolTip = kickBanHint;  VisitorCanAccessInventories.ToolTip = inventoryHint;
        VisitorCanEditWorld.ToolTip = editWorldHint; VisitorCanEditBase.ToolTip = editBaseHint;
        VisitorCanExtendBase.ToolTip = extendBaseHint;

        // ── Backup tab ──
        AutoBackupCheck.ToolTip       = "Automatically create server backups on a recurring schedule.";
        BackupIntervalInput.ToolTip   = "How often (in minutes) to create an automatic backup.";
        RetentionDaysInput.ToolTip    = "Number of days to keep old backup files. Older backups are deleted automatically.";
        BackupOnShutdownCheck.ToolTip = "Create a backup whenever the server is stopped or the manager is closed.";
    }
}
