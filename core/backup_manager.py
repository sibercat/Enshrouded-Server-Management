import os
import time
import logging
import threading
from datetime import datetime, timedelta
from pathlib import Path
from typing import Optional

from core.utilities import create_backup

class BackupManager:
    """Manages server backup operations including scheduling and cleanup."""
    
    _instance = None
    _logger = None
    _initialized = False

    def __new__(cls, server, pause_event=None):
        if cls._instance is None:
            cls._instance = super(BackupManager, cls).__new__(cls)
        return cls._instance

    def __init__(self, server, pause_event=None):
        """Initialize backup manager."""
        # Skip if already initialized
        if BackupManager._initialized:
            return
            
        # Setup logger first
        if BackupManager._logger is None:
            BackupManager._logger = self._setup_logger()
        self.logger = BackupManager._logger
            
        self.server = server
        self.backup_thread = None
        self.stop_flag = threading.Event()
        self.pause_event = pause_event if pause_event else threading.Event()
        self.pause_event.set()  # Start in non-paused state
        
        # Initialize backup timing
        self.next_backup = None
        
        # Mark as initialized but don't start automatically
        BackupManager._initialized = True
        self.update_from_config(auto_start=False)

    def _setup_logger(self):
        """Setup dedicated logger for backup manager"""
        logger = logging.getLogger('backup_manager')
        
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
                
                file_handler = logging.FileHandler(log_dir / "backup_manager.log")
                file_handler.setFormatter(formatter)
                logger.addHandler(file_handler)
            except Exception as e:
                print(f"Failed to setup file logging: {e}")
            
            logger.setLevel(logging.INFO)
            
        # Prevent propagation to root logger
        logger.propagate = False
        
        return logger

    def _calculate_next_backup(self) -> None:
        """Calculate the next backup time based on current configuration"""
        try:
            auto_backup_config = self.server.config.get('auto_backup', {})
            if auto_backup_config.get('enabled', False):
                interval_minutes = auto_backup_config.get('interval_minutes', 360)
                self.next_backup = datetime.now() + timedelta(minutes=interval_minutes)
                self.logger.info(f"Next backup scheduled for: {self.next_backup}")
            else:
                self.next_backup = None
                self.logger.info("Auto-backup is disabled")
        except Exception as e:
            self.logger.error(f"Error calculating next backup time: {e}")
            self.next_backup = None

    def update_from_config(self, auto_start=True):
        """Update backup manager state based on current configuration"""
        try:
            auto_backup_config = self.server.config.get('auto_backup', {})
            
            if auto_backup_config.get('enabled', False):
                self.logger.info("Auto-backup is enabled in config")
                
                if not self.next_backup:
                    interval = auto_backup_config.get('interval_minutes', 360)
                    self.next_backup = datetime.now() + timedelta(minutes=interval)
                    self.logger.info(f"Next backup scheduled for: {self.next_backup}")
                
                if auto_start and not (self.backup_thread and self.backup_thread.is_alive()):
                    self.start()
            else:
                self.logger.info("Auto-backup is disabled in config")
                if self.backup_thread and self.backup_thread.is_alive():
                    self.stop()
                    
        except Exception as e:
            self.logger.error(f"Error updating from config: {e}")

    def start(self) -> bool:
        """Start the backup manager thread"""
        if self.backup_thread and self.backup_thread.is_alive():
            self.logger.info("Backup manager already running")
            return False

        try:
            # Only start if server is running
            if not self.server.is_running():
                self.logger.info("Server not running, backup manager will not start")
                return False

            self.stop_flag.clear()
            self.backup_thread = threading.Thread(target=self._backup_loop, name="BackupThread")
            self.backup_thread.daemon = True
            self.backup_thread.start()
            self.logger.info("Backup manager started")
            return True
        except Exception as e:
            self.logger.error(f"Failed to start backup manager: {e}")
            return False

    def stop(self) -> bool:
        """Stop the backup manager thread"""
        try:
            if self.backup_thread and self.backup_thread.is_alive():
                self.logger.info("Stopping backup manager")
                self.stop_flag.set()
                self.backup_thread.join(timeout=30)
                
                if self.backup_thread.is_alive():
                    self.logger.warning("Backup thread did not stop gracefully")
                else:
                    self.logger.info("Backup manager stopped successfully")
                    
                self.backup_thread = None
            return True
        except Exception as e:
            self.logger.error(f"Error stopping backup manager: {e}")
            return False

    def _backup_loop(self) -> None:
        """Main backup loop"""
        self.logger.info("Backup loop started")
        last_cleanup_check = datetime.now()
        
        while not self.stop_flag.is_set():
            try:
                # Wait for any pause to be lifted
                self.pause_event.wait()
                
                # Check if server is still running
                if not self.server.is_running():
                    self.logger.info("Server not running, stopping backup manager")
                    self.stop()
                    break

                current_time = datetime.now()
                
                # Check if backup is due
                if self.next_backup and current_time >= self.next_backup:
                    self.logger.info("Starting scheduled backup")
                    
                    # Check and cleanup old backups daily
                    if current_time - last_cleanup_check > timedelta(days=1):
                        if self._should_cleanup():
                            self._cleanup_old_backups()
                        last_cleanup_check = current_time
                    
                    # Perform backup
                    if self.server.backup():
                        self.logger.info("Automated backup completed successfully")
                        # Recalculate next backup time
                        self._calculate_next_backup()
                    else:
                        self.logger.error("Automated backup failed")
                        # Retry in 5 minutes on failure
                        self.next_backup = current_time + timedelta(minutes=5)
                
                # Sleep for 1 minute
                for _ in range(6):  # Check stop flag every 10 seconds
                    if self.stop_flag.is_set():
                        break
                    time.sleep(10)
                    
            except Exception as e:
                self.logger.error(f"Error in backup loop: {e}")
                time.sleep(60)  # Wait before retrying after error

    def _should_cleanup(self) -> bool:
        """Check if cleanup should be performed"""
        try:
            backup_dir = Path(self.server.config["backup_dir"])
            if not backup_dir.exists():
                return False
            
            backup_files = list(backup_dir.glob("enshrouded_backup_*.zip"))
            if not backup_files:
                return False
                
            auto_backup_config = self.server.config.get('auto_backup', {})
            keep_days = auto_backup_config.get('keep_days', 7)
            
            oldest_allowed = datetime.now() - timedelta(days=keep_days)
            
            for backup_file in backup_files:
                try:
                    date_str = backup_file.stem.split('_')[2:4]
                    file_date = datetime.strptime('_'.join(date_str), '%Y%m%d_%H%M%S')
                    if file_date < oldest_allowed:
                        return True
                except (ValueError, IndexError):
                    continue
                    
            return False
        except Exception as e:
            self.logger.error(f"Error checking cleanup status: {e}")
            return False

    def _cleanup_old_backups(self) -> None:
        """Clean up old backup files"""
        try:
            auto_backup_config = self.server.config.get('auto_backup', {})
            keep_days = auto_backup_config.get('keep_days', 7)
            backup_dir = Path(self.server.config["backup_dir"])
            
            if not backup_dir.exists():
                return
                
            cutoff_date = datetime.now() - timedelta(days=keep_days)
            deleted_count = 0
            failed_count = 0
            
            for backup_file in backup_dir.glob("enshrouded_backup_*.zip"):
                try:
                    date_str = backup_file.stem.split('_')[2:4]
                    file_date = datetime.strptime('_'.join(date_str), '%Y%m%d_%H%M%S')
                    
                    if file_date < cutoff_date:
                        backup_file.unlink()
                        deleted_count += 1
                        self.logger.info(f"Deleted old backup: {backup_file.name}")
                except (ValueError, IndexError) as e:
                    self.logger.warning(f"Could not parse date from backup filename {backup_file.name}: {e}")
                    failed_count += 1
                except Exception as e:
                    self.logger.error(f"Error deleting backup {backup_file.name}: {e}")
                    failed_count += 1
            
            if deleted_count > 0 or failed_count > 0:
                self.logger.info(f"Cleanup completed: {deleted_count} files deleted, {failed_count} failed")
                    
        except Exception as e:
            self.logger.error(f"Error during backup cleanup: {e}")

    def get_next_backup_time(self) -> Optional[datetime]:
        """Get the next scheduled backup time"""
        return self.next_backup

    def force_backup(self) -> bool:
        """Force an immediate backup"""
        try:
            if self.server.backup():
                self.logger.info("Forced backup completed successfully")
                # Recalculate next scheduled backup time
                self._calculate_next_backup()
                return True
            else:
                self.logger.error("Forced backup failed")
                return False
        except Exception as e:
            self.logger.error(f"Error during forced backup: {e}")
            return False

    def shutdown(self) -> None:
        """Perform cleanup on shutdown"""
        try:
            auto_backup_config = self.server.config.get('auto_backup', {})
            if auto_backup_config.get('backup_on_shutdown', True):
                self.logger.info("Performing shutdown backup")
                self.server.backup()
            self.stop()
        except Exception as e:
            self.logger.error(f"Error during shutdown: {e}")