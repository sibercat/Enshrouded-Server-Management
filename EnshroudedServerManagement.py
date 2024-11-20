#v0.0.40 - Added Server version in UI
import os
import sys
import time
import shutil
import logging
import platform
import subprocess
import zipfile
import threading
from datetime import datetime, timedelta
from pathlib import Path
from typing import Dict, Optional

# Import core functionalities
from core.utilities import (
    VERSION,
    BUILD_DATE,
    setup_logging,
    check_admin_privileges,
    install_steamcmd,
    check_steamcmd,
    Colors
)

from core.backup_manager import BackupManager
from core.restart_manager import RestartManager
from core.server_launcher import ServerLauncher
from config.config_manager import ConfigManager
from main_window import launch_ui

class EnshroudedServer:
    def __init__(self, config_path: str = "server_config.json"):
        """Initialize the Enshrouded server manager."""
        try:
            # Store config_path and initialize basic attributes first
            self.config_path = config_path
            self.is_windows = platform.system() == "Windows"
            self.process = None
            self._backup_manager = None
            self._restart_manager = None
            self._server_version = "Unknown"
            self._managers_running = False
            self._shutdown_event = threading.Event()
            self._shutdown_timeout = 30  # seconds
            
            # Process monitoring cache
            self._process_id = None
            self._last_check = 0
            self._check_interval = 2  # seconds
            self._last_version_check = 0
            self._version_check_interval = 300  # 5 minutes

            # Setup logging before config loading
            setup_logging()

            # Load configuration
            self.config_manager = ConfigManager(config_path)
            self.config = self.config_manager.config

            # Initialize server launcher
            self.server_launcher = ServerLauncher(self.config_manager, self.is_windows)

            # Try to get server version if logs exist
            self._update_server_version()

        except Exception as e:
            print(f"{Colors.RED}Error initializing server manager: {e}{Colors.ENDC}")
            logging.error(f"Initialization error: {e}")
            raise

    def _update_server_version(self) -> None:
        """Update the server version from log file if available."""
        try:
            # Check if we need to update version based on interval
            current_time = time.time()
            if current_time - self._last_version_check < self._version_check_interval:
                return

            log_path = Path(self.config["server_dir"]) / "logs" / "enshrouded_server.log"
            if not log_path.exists():
                logging.info("Server logs not found. Version will be updated after first run.")
                return

            with open(log_path, 'r', encoding='utf-8') as f:
                for line in f:
                    if "Game Version (SVN):" in line:
                        version = line.split("Game Version (SVN):")[1].strip()
                        if version != self._server_version:
                            self._server_version = version
                            logging.info(f"Server version detected: {version}")
                        break

            self._last_version_check = current_time
            
        except Exception as e:
            logging.error(f"Error reading server version: {e}")

    @property
    def server_version(self) -> str:
        """Get the current server version."""
        return self._server_version

    def _start_managers(self) -> None:
        """Start backup and restart managers if enabled and server is running."""
        if not self._managers_running and self.is_running():
            logging.info("Starting server managers...")
            if (self.config.get('auto_backup', {}).get('enabled', False) and 
                self._backup_manager and hasattr(self._backup_manager, 'initialized')):
                logging.info("Starting backup manager...")
                self._backup_manager.start()
                
            if (self.config.get('auto_restart', False) and 
                self._restart_manager and hasattr(self._restart_manager, 'initialized')):
                logging.info("Starting restart manager...")
                self._restart_manager.start()
                
            self._managers_running = True

    def _stop_managers(self) -> None:
        """Stop backup and restart managers."""
        if self._managers_running:
            logging.info("Stopping server managers...")
            if self._backup_manager:
                self._backup_manager.stop()
            if self._restart_manager:
                self._restart_manager.stop()
            self._managers_running = False

    @property
    def auto_backup(self):
        """Lazy initialization of backup manager."""
        if self._backup_manager is None:
            self._backup_manager = BackupManager(self)
            self._backup_manager.initialized = True
        return self._backup_manager

    @property
    def auto_restart(self):
        """Lazy initialization of restart manager."""
        if self._restart_manager is None:
            self._restart_manager = RestartManager(self)
            self._restart_manager.initialized = True
        return self._restart_manager

    def is_running(self) -> bool:
        """Check if the server process is running with caching."""
        current_time = time.time()
        if current_time - self._last_check < self._check_interval:
            return self._process_id is not None

        try:
            if self.is_windows:
                cmd = 'tasklist /FI "IMAGENAME eq enshrouded_server.exe" /FO CSV /NH'
                output = subprocess.getoutput(cmd)
                if "enshrouded_server.exe" in output:
                    self._process_id = output.split(',')[1].strip('"')
                else:
                    self._process_id = None
            else:
                cmd = 'pgrep -f enshrouded_server'
                output = subprocess.getoutput(cmd).strip()
                self._process_id = output if output else None

            self._last_check = current_time
            return self._process_id is not None

        except Exception as e:
            logging.error(f"Error checking server status: {e}")
            return False

    def start(self) -> bool:
        """Start the Enshrouded server."""
        try:
            if self.is_running():
                logging.warning("Server is already running!")
                return False

            success, process = self.server_launcher.launch(self.is_running)
            if success:
                self.process = process
                self._process_id = process.pid if process else None
                
                # Initialize managers if they haven't been created yet
                if self.config.get('auto_backup', {}).get('enabled', False):
                    self.auto_backup  # This initializes but won't auto-start now
                if self.config.get('auto_restart', False):
                    self.auto_restart  # This initializes but won't auto-start now
                    
                # Explicitly start managers after successful server start
                self._start_managers()
                
                # Force version update after start
                self._last_version_check = 0
                self._update_server_version()
                return True
            return False
        except Exception as e:
            logging.error(f"Error starting server: {e}")
            return False

    def stop(self) -> bool:
        """Stop the Enshrouded server with improved process handling."""
        if not self.is_running():
            logging.warning("Server is not running!")
            return False

        logging.info("Stopping Enshrouded server...")
        success = False

        try:
            # Stop managers before stopping server
            self._stop_managers()
            
            # Set shutdown event
            self._shutdown_event.set()
            
            # First attempt graceful shutdown
            if self.process:
                try:
                    self.process.terminate()
                    # Wait for up to 10 seconds for graceful shutdown
                    try:
                        self.process.wait(timeout=10)
                        success = True
                    except subprocess.TimeoutExpired:
                        logging.warning("Graceful shutdown timed out, forcing termination...")
                except Exception as e:
                    logging.error(f"Error during graceful shutdown: {e}")

            # If graceful shutdown failed, force kill the process
            if not success:
                if self.is_windows:
                    # Windows-specific process termination
                    try:
                        subprocess.run(
                            ["taskkill", "/F", "/IM", "enshrouded_server.exe"],
                            timeout=5,
                            check=False
                        )
                        
                        # Additional cleanup for Windows
                        try:
                            subprocess.run([
                                "powershell",
                                "-command",
                                "$processes = Get-Process | Where-Object {$_.MainWindowTitle -like '*enshrouded_server*'}; if ($processes) { $processes | Stop-Process -Force }"
                            ], timeout=5, check=False)
                        except Exception:
                            pass
                    except Exception as e:
                        logging.error(f"Error during Windows process termination: {e}")
                else:
                    # Linux-specific process termination
                    try:
                        subprocess.run("pkill -9 -f enshrouded_server", shell=True, timeout=5)
                        
                        # Additional cleanup for Linux terminals
                        terminals = ["gnome-terminal", "konsole", "xterm", "xfce4-terminal"]
                        for term in terminals:
                            try:
                                subprocess.run(
                                    f"pkill -f '{term}.*Enshrouded Server'",
                                    shell=True,
                                    timeout=2,
                                    check=False
                                )
                            except Exception:
                                continue
                    except Exception as e:
                        logging.error(f"Error during Linux process termination: {e}")

            # Wait for process to fully terminate
            start_time = time.time()
            while self.is_running() and (time.time() - start_time) < self._shutdown_timeout:
                time.sleep(0.5)

            if self.is_running():
                logging.error("Failed to stop server after timeout!")
                return False

            self.process = None
            self._process_id = None
            logging.info("Server stopped successfully!")
            return True

        except Exception as e:
            logging.error(f"Error stopping server: {e}")
            return False

    def restart(self) -> bool:
        """Restart the Enshrouded server."""
        logging.info("Restarting Enshrouded server...")
        self.stop()  # This will stop managers
        time.sleep(5)  # Allow time for full shutdown
        return self.start()  # This will restart managers if successful

    def update(self) -> bool:
        """Update the Enshrouded server via SteamCMD."""
        logging.info("Updating Enshrouded server...")
        
        # Stop server if running
        if self.is_running():
            if not self.stop():
                logging.error("Failed to stop server for update")
                return False

        try:
            # Install/verify SteamCMD
            steamcmd_dir = Path(self.config["steamcmd_dir"])
            if not install_steamcmd(steamcmd_dir):
                logging.error("Failed to install/verify SteamCMD")
                return False

            # Get proper SteamCMD executable path
            valid, steamcmd_path = check_steamcmd(steamcmd_dir)
            if not valid:
                logging.error("SteamCMD not found or not executable")
                return False

            cmd = [
                steamcmd_path,
                "+force_install_dir", self.config["server_dir"],
                "+login", "anonymous",
                "+app_update", self.config["app_id"],
                "validate",
                "+quit"
            ]
            
            os.chdir(str(steamcmd_dir))
            result = subprocess.run(cmd, capture_output=True, text=True)
            
            if result.returncode == 0:
                logging.info("Update completed successfully!")
                # Force version update after successful update
                self._last_version_check = 0
                self._update_server_version()
                return True
            else:
                logging.error(f"Update failed: {result.stderr}")
                return False

        except Exception as e:
            logging.error(f"Error during update: {e}")
            return False

    def backup(self) -> bool:
        """Create a backup of the server files."""
        backup_dir = Path(self.config["backup_dir"])
        backup_dir.mkdir(parents=True, exist_ok=True)
        
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        backup_name = f"enshrouded_backup_{timestamp}"
        
        try:
            if self.is_running():
                logging.warning("Server is running during backup, data might be inconsistent")
            
            save_dir = Path(self.config["server_dir"]) / "savegame"
            if not save_dir.exists():
                logging.error(f"No save directory found at {save_dir}")
                return False
                
            backup_path = backup_dir / f"{backup_name}.zip"
            
            def zipdir(path, ziph):
                """Helper function to zip a directory."""
                for root, dirs, files in os.walk(path):
                    for file in files:
                        file_path = os.path.join(root, file)
                        arcname = os.path.relpath(file_path, path)
                        ziph.write(file_path, arcname)
            
            if self.is_windows:
                # Use Python's built-in zipfile for Windows
                with zipfile.ZipFile(str(backup_path), 'w', zipfile.ZIP_DEFLATED) as zipf:
                    zipdir(save_dir, zipf)
            else:
                # Use shutil for other platforms
                shutil.make_archive(
                    str(backup_dir / backup_name),
                    'zip',
                    save_dir
                )
            
            logging.info(f"Backup created successfully: {backup_path}")
            return True
        except Exception as e:
            logging.error(f"Backup failed: {e}")
            return False

    def shutdown(self) -> bool:
        """Perform a clean shutdown of the server and all managers."""
        logging.info("Initiating server shutdown sequence...")
        shutdown_success = True

        try:
            # Stop managers first
            self._stop_managers()

            # Stop the server process
            if self.is_running():
                if not self.stop():
                    logging.error("Failed to stop server process")
                    shutdown_success = False

            # Final cleanup
            self._shutdown_event.set()
            self._process_id = None
            
            return shutdown_success

        except Exception as e:
            logging.error(f"Error during shutdown sequence: {e}")
            return False


if __name__ == "__main__":
    try:
        print(f"Initializing Enshrouded Server Manager v{VERSION} ({BUILD_DATE})")
        print("Checking system requirements...")
        
        # Check if running with proper permissions
        if not check_admin_privileges():
            print("Warning: Running without administrator privileges.")
            print("Some features may not work properly.")
            
        # Initialize server manager
        server = EnshroudedServer()
        
        # Launch the Qt UI
        launch_ui(server)
        
    except KeyboardInterrupt:
        print("\nCaught keyboard interrupt. Exiting...")
        try:
            if 'server' in locals() and hasattr(server, 'shutdown'):
                print("Performing clean shutdown...")
                server.shutdown()
        except Exception as shutdown_error:
            print(f"Error during shutdown: {shutdown_error}")
        finally:
            sys.exit(0)
    except Exception as e:
        print(f"Fatal error: {e}")
        logging.critical(f"Fatal error: {e}")
        try:
            if 'server' in locals() and hasattr(server, 'shutdown'):
                print("Attempting emergency shutdown...")
                server.shutdown()
        except Exception as shutdown_error:
            print(f"Error during emergency shutdown: {shutdown_error}")
        finally:
            sys.exit(1)