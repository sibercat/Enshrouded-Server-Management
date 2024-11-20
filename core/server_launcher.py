# core/server_launcher.py

import os
import time
import shutil
import logging
import subprocess
from pathlib import Path
from typing import Optional, Tuple

from core.utilities import (
    check_steamcmd, 
    kill_process, 
    is_port_available, 
    Colors
)

class ServerLauncher:
    def __init__(self, config_manager, is_windows: bool):
        self.config_manager = config_manager
        self.config = config_manager.config
        self.is_windows = is_windows
        self.process = None

    def _find_linux_terminal(self) -> str:
        """Find available terminal emulator on Linux systems."""
        terminal_cmd = "x-terminal-emulator"
        terminals = [
            "gnome-terminal", "konsole", "xterm", 
            "x-terminal-emulator", "xfce4-terminal"
        ]
        
        for term in terminals:
            if shutil.which(term):
                return term
        return terminal_cmd

    def _launch_windows_process(self, server_exe: str, startup_params: str) -> Optional[subprocess.Popen]:
        """Launch server process on Windows."""
        return subprocess.Popen(
            [server_exe] + startup_params.split(),
            creationflags=subprocess.CREATE_NEW_CONSOLE
        )

    def _launch_linux_process(self, server_exe: str, startup_params: str) -> Optional[subprocess.Popen]:
        """Launch server process on Linux."""
        terminal_cmd = self._find_linux_terminal()
        
        if terminal_cmd == "gnome-terminal":
            return subprocess.Popen([
                terminal_cmd,
                "--title=Enshrouded Server",
                "--",
                "bash", "-c",
                f"cd '{self.config['server_dir']}' && {server_exe} {startup_params}"
            ])
        else:
            return subprocess.Popen([
                terminal_cmd,
                "-T", "Enshrouded Server",
                "-e",
                f"bash -c 'cd \"{self.config['server_dir']}\" && {server_exe} {startup_params}'"
            ])

    def _print_server_info(self):
        """Print server information after successful launch."""
        print(f"{Colors.GREEN}Server started successfully!{Colors.ENDC}")
        print(f"Server Name: {self.config['server_name']}")
        print(f"Max Players: {self.config['max_players']}")
        print(f"Game Port: {self.config['game_port']}")
        print(f"\nServer console window has been opened")
        print("\nUser Group Passwords:")
        print(f"Admin: {self.config['admin_password']}")
        print(f"Friend: {self.config['friend_password']}")
        print(f"Guest: {self.config['guest_password']}")

    def launch(self, is_running_check) -> Tuple[bool, Optional[subprocess.Popen]]:
        """
        Launch the Enshrouded server.
        
        Args:
            is_running_check: Function to check if server is running
            
        Returns:
            Tuple[bool, Optional[subprocess.Popen]]: Success status and process object
        """
        if is_running_check():
            print(f"{Colors.YELLOW}Server is already running!{Colors.ENDC}")
            return False, None

        print(f"{Colors.BLUE}Starting Enshrouded server...{Colors.ENDC}")
        server_exe = "enshrouded_server.exe" if self.is_windows else "./enshrouded_server"
        
        try:
            os.chdir(self.config["server_dir"])
            
            if not self.config_manager.update_server_json(self.config["server_dir"]):
                print(f"{Colors.YELLOW}Warning: Failed to update server configuration file{Colors.ENDC}")
            
            startup_params = self._build_startup_parameters()
            
            # Launch process based on platform
            self.process = (
                self._launch_windows_process(server_exe, startup_params)
                if self.is_windows
                else self._launch_linux_process(server_exe, startup_params)
            )
            
            time.sleep(5)  # Wait for server to start
            
            if is_running_check():
                self._print_server_info()
                return True, self.process
            else:
                print(f"{Colors.RED}Failed to start server!{Colors.ENDC}")
                return False, None

        except Exception as e:
            print(f"{Colors.RED}Error starting server: {e}{Colors.ENDC}")
            logging.error(f"Server launch error: {e}")
            return False, None

    def _build_startup_parameters(self) -> str:
        """Build startup parameters string from config settings."""
        params = [
            f"-servername \"{self.config['server_name']}\"",
            f"-gameport {self.config['game_port']}",
            f"-queryport {self.config['query_port']}",
            f"-maxplayers {self.config['max_players']}",
            f"-maxfps {self.config['max_fps']}",
            f"-tickrate {self.config['tick_rate']}"
        ]
            
        if self.config['pvp_enabled']:
            params.append("-pvp")
            
        params.append(f"-difficulty {self.config['game_difficulty']}")
        
        if self.config['startup_params']:
            params.append(self.config['startup_params'])
            
        return " ".join(params)