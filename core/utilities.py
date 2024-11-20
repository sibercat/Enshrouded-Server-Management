# core/utilities.py

VERSION = "0.0.40"
BUILD_DATE = "2024-11-13"

import os
import sys
import time
import shutil
import logging
import platform
import subprocess
import urllib.request
import zipfile
from pathlib import Path
from typing import Optional, Tuple

class Colors:
    """ANSI color codes for terminal output."""
    HEADER = '\033[95m'
    BLUE = '\033[94m'
    GREEN = '\033[92m'
    YELLOW = '\033[93m'
    RED = '\033[91m'
    ENDC = '\033[0m'
    BOLD = '\033[1m'
    UNDERLINE = '\033[4m'

def get_version_info() -> dict:
    """Get version information for the application."""
    return {
        "version": VERSION,
        "build_date": BUILD_DATE,
        "python_version": platform.python_version(),
        "system": platform.system(),
        "architecture": platform.architecture()[0]
    }

def check_version_compatibility() -> bool:
    """Check if the current Python version is compatible."""
    required_version = (3, 8)  # Minimum required Python version
    current_version = sys.version_info[:2]
    return current_version >= required_version

def setup_logging(log_dir: Path = None) -> None:
    """Setup logging configuration.
    
    Args:
        log_dir: Optional directory for log files. Defaults to ./logs/
    """
    try:
        if log_dir is None:
            log_dir = Path("logs")
            
        log_dir.mkdir(parents=True, exist_ok=True)
        
        # Get the root logger
        root_logger = logging.getLogger()
        
        # Clear any existing handlers
        root_logger.handlers.clear()
        
        # Create formatter
        formatter = logging.Formatter('%(asctime)s - %(name)s - %(levelname)s - %(message)s')
        
        # File handler
        file_handler = logging.FileHandler(log_dir / "server_manager.log")
        file_handler.setFormatter(formatter)
        root_logger.addHandler(file_handler)
        
        # Console handler
        console_handler = logging.StreamHandler()
        console_handler.setFormatter(formatter)
        root_logger.addHandler(console_handler)
        
        root_logger.setLevel(logging.INFO)
        
        logging.info("Logging setup completed")
    except Exception as e:
        print(f"Error setting up logging: {e}")
        logging.basicConfig(level=logging.INFO)

def check_admin_privileges() -> bool:
    """Check if the application is running with administrator privileges."""
    try:
        if platform.system() == "Windows":
            import ctypes
            return ctypes.windll.shell32.IsUserAnAdmin()
        else:
            return os.geteuid() == 0
    except Exception as e:
        logging.error(f"Error checking admin privileges: {e}")
        return False

def install_steamcmd(install_dir: Optional[Path] = None) -> bool:
    """Install SteamCMD if not present.
    
    Args:
        install_dir: Installation directory for SteamCMD. Defaults to ~/steamcmd

    Returns:
        bool: True if installation successful or already installed, False otherwise
    """
    try:
        if install_dir is None:
            install_dir = Path.home() / "steamcmd"
            
        if not install_dir.exists():
            logging.info("SteamCMD not found. Installing...")
            install_dir.mkdir(parents=True, exist_ok=True)
            
            if platform.system() == "Windows":
                logging.info("Downloading SteamCMD for Windows...")
                urllib.request.urlretrieve(
                    "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip",
                    install_dir / "steamcmd.zip"
                )
                
                with zipfile.ZipFile(install_dir / "steamcmd.zip", 'r') as zip_ref:
                    zip_ref.extractall(install_dir)
                (install_dir / "steamcmd.zip").unlink()
                
                # Run steamcmd once to update
                subprocess.run([str(install_dir / "steamcmd.exe"), "+quit"], check=True)
            else:
                os.chdir(str(install_dir))
                subprocess.run(["wget", "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz"], check=True)
                subprocess.run(["tar", "-xvzf", "steamcmd_linux.tar.gz"], check=True)
                subprocess.run(["rm", "steamcmd_linux.tar.gz"], check=True)
                subprocess.run(["chmod", "+x", "steamcmd.sh"], check=True)
                
                # Run steamcmd once to update
                subprocess.run(["./steamcmd.sh", "+quit"], check=True)
            
            logging.info("SteamCMD installed successfully!")
            return True
            
        return True  # Already installed
        
    except Exception as e:
        logging.error(f"Failed to install SteamCMD: {e}")
        return False

def check_steamcmd(steamcmd_dir: Path) -> Tuple[bool, str]:
    """Check if SteamCMD is properly installed and executable.
    
    Args:
        steamcmd_dir: Directory where SteamCMD should be installed

    Returns:
        Tuple[bool, str]: (is_valid, executable_path)
    """
    try:
        if platform.system() == "Windows":
            steamcmd_exe = steamcmd_dir / "steamcmd.exe"
            if not steamcmd_exe.exists():
                return False, ""
            return True, str(steamcmd_exe)
        else:
            steamcmd_sh = steamcmd_dir / "steamcmd.sh"
            if not steamcmd_sh.exists():
                return False, ""
            if not os.access(steamcmd_sh, os.X_OK):
                steamcmd_sh.chmod(0o755)
            return True, str(steamcmd_sh)
    except Exception as e:
        logging.error(f"Error checking SteamCMD: {e}")
        return False, ""

def create_backup(source_dir: Path, backup_dir: Path, backup_name: str) -> bool:
    """Create a backup of specified directory.
    
    Args:
        source_dir: Directory to backup
        backup_dir: Directory to store backup
        backup_name: Name of backup file (without extension)

    Returns:
        bool: True if backup successful, False otherwise
    """
    try:
        if not source_dir.exists():
            logging.error(f"Source directory does not exist: {source_dir}")
            return False
            
        backup_dir.mkdir(parents=True, exist_ok=True)
        backup_path = backup_dir / f"{backup_name}.zip"
        
        def zipdir(path, ziph):
            for root, _, files in os.walk(path):
                for file in files:
                    file_path = os.path.join(root, file)
                    arcname = os.path.relpath(file_path, path)
                    ziph.write(file_path, arcname)
        
        if platform.system() == "Windows":
            with zipfile.ZipFile(str(backup_path), 'w', zipfile.ZIP_DEFLATED) as zipf:
                zipdir(source_dir, zipf)
        else:
            shutil.make_archive(
                str(backup_dir / backup_name),
                'zip',
                source_dir
            )
        
        logging.info(f"Backup created successfully: {backup_path}")
        return True
    except Exception as e:
        logging.error(f"Backup failed: {e}")
        return False

def get_process_info(process_name: str) -> Tuple[Optional[float], Optional[float]]:
    """Get CPU and memory usage for a process with optimized caching.
    
    Args:
        process_name: Name of process to monitor

    Returns:
        Tuple[Optional[float], Optional[float]]: (cpu_percent, memory_percent)
        Returns (None, None) if process not found or monitoring unavailable
    """
    try:
        import psutil
        
        # Initialize cache attributes if they don't exist
        if not hasattr(get_process_info, '_process_cache'):
            get_process_info._process_cache = None
            get_process_info._last_check = 0
            get_process_info._check_interval = 2  # seconds
            get_process_info._cache_timeout = 30  # seconds
            get_process_info._last_proc_refresh = 0
            
        current_time = time.time()
        
        # Return cached values if within check interval
        if (get_process_info._process_cache and 
            current_time - get_process_info._last_check < get_process_info._check_interval):
            try:
                return (get_process_info._process_cache.cpu_percent(interval=None),
                       get_process_info._process_cache.memory_percent())
            except (psutil.NoSuchProcess, psutil.AccessDenied):
                get_process_info._process_cache = None
        
        # Refresh process cache periodically or if process not found
        if (not get_process_info._process_cache or 
            current_time - get_process_info._last_proc_refresh > get_process_info._cache_timeout):
            
            found_process = None
            for proc in psutil.process_iter(['name', 'cpu_percent', 'memory_percent']):
                try:
                    if proc.info['name'] == process_name:
                        found_process = proc
                        break
                except (psutil.NoSuchProcess, psutil.AccessDenied):
                    continue
            
            get_process_info._process_cache = found_process
            get_process_info._last_proc_refresh = current_time
            
            # Initialize CPU monitoring if process found
            if found_process:
                try:
                    found_process.cpu_percent(interval=None)  # First call to initialize CPU monitoring
                    time.sleep(0.1)  # Short sleep to allow for initial CPU measurement
                except (psutil.NoSuchProcess, psutil.AccessDenied):
                    get_process_info._process_cache = None
        
        # Get current metrics
        if get_process_info._process_cache:
            try:
                cpu_percent = get_process_info._process_cache.cpu_percent(interval=None)
                memory_percent = get_process_info._process_cache.memory_percent()
                get_process_info._last_check = current_time
                
                # Validate values
                if cpu_percent is not None and memory_percent is not None:
                    # Ensure values are within reasonable ranges
                    cpu_percent = min(100.0, max(0.0, cpu_percent))
                    memory_percent = min(100.0, max(0.0, memory_percent))
                    return cpu_percent, memory_percent
                    
            except (psutil.NoSuchProcess, psutil.AccessDenied, psutil.ZombieProcess) as e:
                logging.debug(f"Process monitoring error: {e}")
                get_process_info._process_cache = None
                
        return None, None
        
    except ImportError:
        logging.warning("psutil not installed. Performance monitoring disabled.")
        return None, None
    except Exception as e:
        logging.error(f"Unexpected error in process monitoring: {e}")
        return None, None

def kill_process(process_name: str) -> bool:
    """Forcefully terminate a process by name.
    
    Args:
        process_name: Name of process to kill

    Returns:
        bool: True if process was killed, False otherwise
    """
    try:
        if platform.system() == "Windows":
            subprocess.run(["taskkill", "/F", "/IM", process_name], check=False)
        else:
            subprocess.run(f"pkill -f {process_name}", shell=True, check=False)
        return True
    except Exception as e:
        logging.error(f"Error killing process: {e}")
        return False

def is_port_available(port: int) -> bool:
    """Check if a network port is available.
    
    Args:
        port: Port number to check

    Returns:
        bool: True if port is available, False otherwise
    """
    import socket
    try:
        # Check both IPv4 and IPv6
        for family in (socket.AF_INET, socket.AF_INET6):
            try:
                with socket.socket(family, socket.SOCK_STREAM) as s:
                    s.bind(('', port))
                    return True
            except socket.error:
                continue
        return False
    except Exception as e:
        logging.error(f"Error checking port availability: {e}")
        return False