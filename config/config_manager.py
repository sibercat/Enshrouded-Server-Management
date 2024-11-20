# config/config_manager.py

import os
import json
import logging
from pathlib import Path
from typing import Dict

from core.utilities import Colors

class ConfigManager:
    def __init__(self, config_path: str = "server_config.json"):
        self.config_path = config_path
        self._config = self.load_config()

    @property
    def config(self) -> Dict:
        return self._config

    def load_config(self) -> Dict:
        """Load configuration from JSON file or create default if not exists."""
        default_config = {
            # Server Directory Settings
            "server_dir": str(Path.home() / "EnshroudedServer"),
            "steamcmd_dir": str(Path.home() / "steamcmd"),
            "backup_dir": str(Path.home() / "EnshroudedBackups"),
            "app_id": "2278520",
            
            # Backup Settings
            "max_backups": 5,
            
            # Server Management
            "auto_restart": True,
            "restart_interval": 24,
            "restart_warning_minutes": 5,
            
            # Game Server Settings
            "server_name": "My Enshrouded Server",
            "max_players": 16,
            "game_port": 15636,
            "query_port": 15637,
            
            # User Groups
            "admin_password": "Admin6cnHwt-D",
            "friend_password": "Friend8p[O-PxS",
            "guest_password": "Guest8AUvMEaA",
            
            # Game Rules
            "game_difficulty": "normal",
            "pvp_enabled": False,
            "player_damage_multiplier": 1.0,
            "enemy_damage_multiplier": 1.0,
            "enemy_health_multiplier": 1.0,
            "day_length_minutes": 30,
            "night_length_minutes": 12,
            
            # Performance Settings
            "max_fps": 60,
            "tick_rate": 64,
            
            # Advanced Settings
            "startup_params": "",
            "allow_commands": True,
            "log_level": "info"
        }

        try:
            if os.path.exists(self.config_path):
                with open(self.config_path, 'r') as f:
                    return {**default_config, **json.load(f)}
            else:
                with open(self.config_path, 'w') as f:
                    json.dump(default_config, f, indent=4)
                return default_config
        except Exception as e:
            logging.error(f"Error loading config: {e}. Using defaults.")
            return default_config

    def update_server_json(self, server_dir: str) -> bool:
        """Update the server's enshrouded_server.json configuration file."""
        try:
            server_json_path = Path(server_dir) / "enshrouded_server.json"
            
            server_json = {
                "name": self.config["server_name"],
                "saveDirectory": "./savegame",
                "logDirectory": "./logs",
                "ip": "0.0.0.0",
                "queryPort": self.config["query_port"],
                "slotCount": self.config["max_players"],
                "gameSettingsPreset": "Default",
                "gameSettings": {
                    "playerHealthFactor": 1,
                    "playerManaFactor": 1,
                    "playerStaminaFactor": 1,
                    "playerBodyHeatFactor": 1,
                    "enableDurability": True,
                    "enableStarvingDebuff": False,
                    "foodBuffDurationFactor": 1,
                    "fromHungerToStarving": 600000000000,
                    "shroudTimeFactor": 1,
                    "tombstoneMode": "AddBackpackMaterials",
                    "enableGliderTurbulences": True,
                    "weatherFrequency": "Normal",
                    "miningDamageFactor": 1,
                    "plantGrowthSpeedFactor": 1,
                    "resourceDropStackAmountFactor": 1,
                    "factoryProductionSpeedFactor": 1,
                    "perkUpgradeRecyclingFactor": 0.5,
                    "perkCostFactor": 1,
                    "experienceCombatFactor": 1,
                    "experienceMiningFactor": 1,
                    "experienceExplorationQuestsFactor": 1,
                    "randomSpawnerAmount": "Normal",
                    "aggroPoolAmount": "Normal",
                    "enemyDamageFactor": self.config["enemy_damage_multiplier"],
                    "enemyHealthFactor": self.config["enemy_health_multiplier"],
                    "enemyStaminaFactor": 1,
                    "enemyPerceptionRangeFactor": 1,
                    "bossDamageFactor": 1,
                    "bossHealthFactor": 1,
                    "threatBonus": 1,
                    "pacifyAllEnemies": False,
                    "tamingStartleRepercussion": "LoseSomeProgress",
                    "dayTimeDuration": self.config["day_length_minutes"] * 60000000000,
                    "nightTimeDuration": self.config["night_length_minutes"] * 60000000000
                },
                "userGroups": [
                    {
                        "name": "Admin",
                        "password": self.config.get("admin_password", "Admin6cnHwt-D"),
                        "canKickBan": True,
                        "canAccessInventories": True,
                        "canEditBase": True,
                        "canExtendBase": True,
                        "reservedSlots": 0
                    },
                    {
                        "name": "Friend",
                        "password": self.config.get("friend_password", "Friend8p[O-PxS"),
                        "canKickBan": False,
                        "canAccessInventories": True,
                        "canEditBase": True,
                        "canExtendBase": False,
                        "reservedSlots": 0
                    },
                    {
                        "name": "Guest",
                        "password": self.config.get("guest_password", "Guest8AUvMEaA"),
                        "canKickBan": False,
                        "canAccessInventories": False,
                        "canEditBase": False,
                        "canExtendBase": False,
                        "reservedSlots": 0
                    }
                ]
            }

            if server_json_path.exists():
                try:
                    with open(server_json_path, 'r') as f:
                        existing_config = json.load(f)
                        if "userGroups" in existing_config:
                            server_json["userGroups"] = existing_config["userGroups"]
                        existing_config.update(server_json)
                        server_json = existing_config
                except json.JSONDecodeError:
                    logging.warning("Could not parse existing server JSON, creating new one")

            with open(server_json_path, 'w') as f:
                json.dump(server_json, f, indent=4)
            
            logging.info(f"Updated server configuration at {server_json_path}")
            return True
        except Exception as e:
            logging.error(f"Failed to update server JSON: {e}")
            return False

    def save_config(self) -> bool:
        """Save the current configuration to file."""
        try:
            with open(self.config_path, 'w') as f:
                json.dump(self.config, f, indent=4)
            return True
        except Exception as e:
            logging.error(f"Error saving config: {e}")
            return False

    def update_config(self, key: str, value: any) -> bool:
        """Update a specific configuration value."""
        try:
            self._config[key] = value
            return self.save_config()
        except Exception as e:
            logging.error(f"Error updating config: {e}")
            return False