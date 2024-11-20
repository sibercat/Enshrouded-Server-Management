import time
import logging
import threading
from datetime import datetime, timedelta
from pathlib import Path
from typing import Optional

from core.utilities import Colors

class RestartManager:
    """Manages server restart operations."""
    
    _instance = None
    _logger = None
    _initialized = False

    def __new__(cls, server):
        if cls._instance is None:
            cls._instance = super(RestartManager, cls).__new__(cls)
        return cls._instance

    def __init__(self, server):
        """Initialize restart manager."""
        # Skip if already initialized
        if RestartManager._initialized:
            return
            
        # Setup logger first
        if RestartManager._logger is None:
            RestartManager._logger = self._setup_logger()
        self.logger = RestartManager._logger
            
        self.server = server
        self.restart_thread = None
        self.stop_flag = threading.Event()
        self.next_restart = None
        
        # Mark as initialized but don't start automatically
        RestartManager._initialized = True
        self.update_from_config(auto_start=False)

    def _setup_logger(self):
        """Setup dedicated logger for restart manager"""
        logger = logging.getLogger('restart_manager')
        
        # Only add handlers if they don't exist
        if not logger.handlers:
            formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
            
            # Console handler
            console_handler = logging.StreamHandler()
            console_handler.setFormatter(formatter)
            logger.addHandler(console_handler)
            
            # File handler
            try:
                log_dir = Path("logs")
                log_dir.mkdir(exist_ok=True)
                
                file_handler = logging.FileHandler(log_dir / "restart_manager.log")
                file_handler.setFormatter(formatter)
                logger.addHandler(file_handler)
            except Exception as e:
                print(f"Failed to setup file logging: {e}")
            
            logger.setLevel(logging.INFO)
        
        # Prevent propagation to root logger
        logger.propagate = False
        
        return logger

    def update_from_config(self, auto_start=True):
        """Update restart manager state based on current configuration"""
        try:
            if self.server.config.get('auto_restart', False):
                if not self.next_restart:
                    interval_hours = self.server.config.get('restart_interval', 24)
                    self.next_restart = datetime.now() + timedelta(hours=interval_hours)
                    self.logger.info(f"Next restart scheduled for: {self.next_restart}")
                
                if auto_start and not (self.restart_thread and self.restart_thread.is_alive()):
                    self.start()
            else:
                self.logger.info("Auto-restart is disabled in config")
                if self.restart_thread and self.restart_thread.is_alive():
                    self.stop()
        except Exception as e:
            self.logger.error(f"Error updating from config: {e}")

    def start(self) -> bool:
        """Start the restart manager"""
        if self.restart_thread and self.restart_thread.is_alive():
            self.logger.info("Restart manager already running")
            return False

        try:
            # Only start if server is running
            if not self.server.is_running():
                self.logger.info("Server not running, restart manager will not start")
                return False

            self.stop_flag.clear()
            self.restart_thread = threading.Thread(target=self._restart_loop, name="RestartThread")
            self.restart_thread.daemon = True
            self.restart_thread.start()
            self.logger.info("Restart manager started")
            return True
        except Exception as e:
            self.logger.error(f"Failed to start restart manager: {e}")
            return False

    def stop(self) -> bool:
        """Stop the restart manager"""
        try:
            if self.restart_thread and self.restart_thread.is_alive():
                self.logger.info("Stopping restart manager")
                self.stop_flag.set()
                self.restart_thread.join(timeout=30)  # Wait up to 30 seconds
                
                if self.restart_thread.is_alive():
                    self.logger.warning("Restart thread did not stop gracefully")
                else:
                    self.logger.info("Restart manager stopped successfully")
                    
                self.restart_thread = None
            return True
        except Exception as e:
            self.logger.error(f"Error stopping restart manager: {e}")
            return False

    def _restart_loop(self) -> None:
        """Main restart loop"""
        self.logger.info("Restart loop started")
        
        while not self.stop_flag.is_set():
            try:
                # Check if server is still running
                if not self.server.is_running():
                    self.logger.info("Server not running, stopping restart manager")
                    self.stop()
                    break

                current_time = datetime.now()
                warning_minutes = self.server.config.get('restart_warning_minutes', 5)
                
                # Check if it's time to warn about restart
                if self.next_restart and current_time >= (self.next_restart - timedelta(minutes=warning_minutes)):
                    self.logger.info(f"Server restart scheduled in {warning_minutes} minutes")
                    # TODO: Implement warning mechanism (e.g., in-game message)
                    time.sleep(warning_minutes * 60)  # Wait for warning period
                    
                    if not self.stop_flag.is_set():
                        if self.server.restart():
                            self.logger.info("Automated restart completed successfully")
                            # Calculate next restart time
                            interval_hours = self.server.config.get('restart_interval', 24)
                            self.next_restart = datetime.now() + timedelta(hours=interval_hours)
                            self.logger.info(f"Next restart scheduled for: {self.next_restart}")
                        else:
                            self.logger.error("Automated restart failed")
                            # Retry in 5 minutes if failed
                            self.next_restart = datetime.now() + timedelta(minutes=5)
                
                # Sleep in small intervals to respond to stop flag more quickly
                for _ in range(6):  # Check stop flag every 10 seconds
                    if self.stop_flag.is_set():
                        break
                    time.sleep(10)
                    
            except Exception as e:
                self.logger.error(f"Error in restart loop: {e}")
                time.sleep(60)  # Wait before retrying after error

    def _calculate_next_restart(self) -> None:
        """Calculate the next restart time based on configuration"""
        try:
            if self.server.config.get('auto_restart', False):
                interval_hours = self.server.config.get('restart_interval', 24)
                self.next_restart = datetime.now() + timedelta(hours=interval_hours)
                self.logger.info(f"Next restart scheduled for: {self.next_restart}")
            else:
                self.next_restart = None
                self.logger.info("Auto-restart is disabled")
        except Exception as e:
            self.logger.error(f"Error calculating next restart time: {e}")
            self.next_restart = None

    def get_next_restart_time(self) -> Optional[datetime]:
        """Get the next scheduled restart time"""
        return self.next_restart

    def force_restart(self) -> bool:
        """Force an immediate server restart"""
        try:
            if self.server.restart():
                self.logger.info("Forced restart completed successfully")
                # Update next scheduled restart
                interval_hours = self.server.config.get('restart_interval', 24)
                self.next_restart = datetime.now() + timedelta(hours=interval_hours)
                return True
            else:
                self.logger.error("Forced restart failed")
                return False
        except Exception as e:
            self.logger.error(f"Error during forced restart: {e}")
            return False

    def shutdown(self) -> None:
        """Clean shutdown of restart manager"""
        try:
            self.logger.info("Shutting down restart manager")
            self.stop()
        except Exception as e:
            self.logger.error(f"Error during shutdown: {e}")