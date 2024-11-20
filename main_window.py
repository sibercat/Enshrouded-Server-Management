# main_window.py

import os
import sys
import time
import logging
import signal
from pathlib import Path
from PyQt6.QtWidgets import (
    QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, 
    QPushButton, QLabel, QTabWidget, QGroupBox,
    QLineEdit, QSpinBox, QDoubleSpinBox, QCheckBox,
    QTextEdit, QMessageBox, QProgressBar, QScrollArea,
    QApplication, QProgressDialog
)
from PyQt6.QtCore import Qt, QTimer, pyqtSignal, QThread
from PyQt6.QtGui import QFont, QIcon

from core.utilities import (
    Colors,
    get_process_info,
    check_steamcmd,
    install_steamcmd,
    VERSION,
    BUILD_DATE
)

try:
    import psutil
    PSUTIL_AVAILABLE = True
except ImportError:
    PSUTIL_AVAILABLE = False
    logging.warning("psutil not installed. Performance monitoring will be disabled.")
    
class ServerOperationWorker(QThread):
    finished = pyqtSignal(bool, str)
    
    def __init__(self, operation, server):
        super().__init__()
        self.operation = operation
        self.server = server
    
    def run(self):
        try:
            result = False
            if self.operation == "start":
                result = self.server.start()
            elif self.operation == "stop":
                result = self.server.stop()
            elif self.operation == "restart":
                result = self.server.restart()
            elif self.operation == "update":
                result = self.server.update()
                
            self.finished.emit(result, self.operation)
        except Exception as e:
            logging.error(f"Error in server operation: {e}")
            self.finished.emit(False, str(e))
            
class ServerManagerUI(QMainWindow):
    def __init__(self, server_manager):
        """Initialize the server manager UI."""
        super().__init__()
        self.server = server_manager  # Set server immediately
        self._last_status = None
        self._last_perf_update = 0
        self._perf_update_interval = 5  # seconds
        self._last_status_check = 0
        self._status_check_interval = 2  # seconds
        self.current_operation = None
        self.initUI()
        
        # Update timer for server status - reduced frequency
        self.status_timer = QTimer()
        self.status_timer.timeout.connect(self.update_server_status)
        self.status_timer.start(5000)  # Update every 5 seconds (increased from 2 seconds)
        
        # Add restart timer with reduced frequency
        self.restart_timer = QTimer()
        self.restart_timer.timeout.connect(self.update_next_restart_time)
        self.restart_timer.start(30000)  # Update every 30 seconds (increased from 15 seconds)

    def closeEvent(self, event):
        """Handle application shutdown."""
        try:
            # Create progress dialog
            progress = QProgressDialog("Preparing to shut down server manager...", None, 0, 5, self)
            progress.setWindowModality(Qt.WindowModality.WindowModal)
            progress.setWindowTitle("Shutting Down")
            progress.setCancelButton(None)
            progress.setMinimumDuration(0)
            progress.setValue(0)

            # Stop timers first
            progress.setLabelText("Stopping update timers...")
            self.status_timer.stop()
            self.restart_timer.stop()
            progress.setValue(1)

            # Handle server shutdown if running
            if self.server.is_running():
                reply = QMessageBox.question(
                    self,
                    'Server Running',
                    'The server is still running. Would you like to stop it?',
                    QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                    QMessageBox.StandardButton.Yes
                )

                if reply == QMessageBox.StandardButton.Yes:
                    progress.setLabelText("Stopping server...")
                    
                    # Create a QTimer for timeout
                    timeout_timer = QTimer(self)
                    timeout_timer.setSingleShot(True)
                    timeout_timer.start(30000)  # 30 second timeout
                    
                    while self.server.is_running() and timeout_timer.isActive():
                        if not self.server.stop():
                            break
                        QApplication.processEvents()  # Keep UI responsive
                        time.sleep(0.5)
                    
                    if self.server.is_running():
                        QMessageBox.warning(
                            self,
                            "Warning",
                            "Server shutdown timed out or failed. Application will close anyway."
                        )
            progress.setValue(2)

            # Shutdown backup manager
            progress.setLabelText("Shutting down backup manager...")
            if hasattr(self.server, '_backup_manager') and self.server._backup_manager:
                try:
                    shutdown_timer = QTimer()
                    shutdown_timer.setSingleShot(True)
                    shutdown_timer.start(60000)  # 60 second timeout for backup
                    
                    while (self.server._backup_manager.backup_thread and 
                           self.server._backup_manager.backup_thread.is_alive() and 
                           shutdown_timer.isActive()):
                        QApplication.processEvents()
                        time.sleep(0.5)
                        
                    self.server._backup_manager.shutdown()
                except Exception as e:
                    logging.error(f"Error during backup manager shutdown: {e}")
            progress.setValue(3)

            # Shutdown restart manager
            progress.setLabelText("Shutting down restart manager...")
            if hasattr(self.server, '_restart_manager') and self.server._restart_manager:
                try:
                    self.server._restart_manager.shutdown()
                except Exception as e:
                    logging.error(f"Error during restart manager shutdown: {e}")
            progress.setValue(4)

            # Final cleanup
            progress.setLabelText("Finalizing shutdown...")
            progress.setValue(5)
            
            event.accept()

        except Exception as e:
            logging.error(f"Error during shutdown: {e}")
            QMessageBox.critical(
                self,
                "Error",
                f"An error occurred during shutdown: {str(e)}\nThe application will close anyway."
            )
            event.accept()

    def update_server_status(self):
        """Update the server status display and performance metrics with reduced frequency."""
        try:
            current_time = time.time()
            
            # Check if we need to update status
            if current_time - self._last_status_check < self._status_check_interval:
                return
                
            # Check server status
            is_running = self.server.is_running()
            
            # Update status indicator only if changed
            if not hasattr(self, '_last_status') or self._last_status != is_running:
                # Update status text and color
                self.status_indicator.setText("Running" if is_running else "Stopped")
                self.status_indicator.setStyleSheet(
                    "color: green; font-weight: bold;" if is_running else "color: red; font-weight: bold;"
                )
                
                # Log status change to console
                if hasattr(self, 'console_output'):
                    self.console_output.append(
                        f"Server status changed: {'Running' if is_running else 'Stopped'}"
                    )
                
                # Store the current status
                self._last_status = is_running

            # Update version label periodically
            if hasattr(self.server, 'server_version'):
                version = self.server.server_version
                self.version_label.setText(f"Server Version: {version}")

            # Update performance metrics less frequently if server is running
            if is_running and PSUTIL_AVAILABLE and current_time - self._last_perf_update >= self._perf_update_interval:
                try:
                    process_name = "enshrouded_server.exe" if os.name == 'nt' else "enshrouded_server"
                    
                    cpu_usage, memory_usage = get_process_info(process_name)
                    if cpu_usage is not None:
                        self.cpu_bar.setValue(int(cpu_usage))
                        self.cpu_bar.setFormat(f"CPU: {cpu_usage:.1f}%")
                    
                    if memory_usage is not None:
                        self.memory_bar.setValue(int(memory_usage))
                        self.memory_bar.setFormat(f"Memory: {memory_usage:.1f}%")
                        
                    self._last_perf_update = current_time
                except Exception as e:
                    logging.warning(f"Error updating performance metrics: {e}")
            else:
                # Reset performance bars when server is not running
                if not is_running:
                    self.cpu_bar.setValue(0)
                    self.memory_bar.setValue(0)
                    self.cpu_bar.setFormat("CPU: 0%")
                    self.memory_bar.setFormat("Memory: 0%")

            # Update next backup time if available
            if hasattr(self.server, 'auto_backup'):
                next_backup = self.server.auto_backup.get_next_backup_time()
                if next_backup and hasattr(self, 'next_backup_label'):
                    self.next_backup_label.setText(
                        f"Next Backup: {next_backup.strftime('%Y-%m-%d %H:%M:%S')}"
                    )
                    
            self._last_status_check = current_time

        except Exception as e:
            logging.error(f"Error updating server status: {e}")
            self.status_indicator.setText("Unknown")
            self.status_indicator.setStyleSheet("color: orange; font-weight: bold;")

    def update_next_restart_time(self):
        """Update the next restart time label"""
        try:
            if hasattr(self.server, 'auto_restart'):
                next_restart = self.server.auto_restart.get_next_restart_time()
                if next_restart:
                    self.next_restart_label.setText(
                        f"Next Restart: {next_restart.strftime('%Y-%m-%d %H:%M:%S')}"
                    )
                else:
                    self.next_restart_label.setText("Next Restart: Not scheduled")
        except Exception as e:
            logging.error(f"Error updating next restart time: {e}")
            self.next_restart_label.setText("Next Restart: Error getting time")
            
    def on_server_operation(self, operation):
        """Handle server operations with threading"""
        # Disable all control buttons during operation
        self.start_btn.setEnabled(False)
        self.stop_btn.setEnabled(False)
        self.restart_btn.setEnabled(False)
        self.update_btn.setEnabled(False)
        
        # Update console
        self.console_output.append(f"Executing server {operation} operation...")
        
        # Create and start worker thread
        self.current_operation = operation
        self.worker = ServerOperationWorker(operation, self.server)
        self.worker.finished.connect(self.on_server_operation_complete)
        self.worker.start()

    def on_server_operation_complete(self, success, operation):
        """Handle completion of server operation"""
        # Re-enable all control buttons
        self.start_btn.setEnabled(True)
        self.stop_btn.setEnabled(True)
        self.restart_btn.setEnabled(True)
        self.update_btn.setEnabled(True)
        
        # Update UI based on result
        if isinstance(operation, str) and not operation.startswith("Error:"):
            if success:
                self.console_output.append(f"Server {operation} completed successfully")
            else:
                self.console_output.append(f"Server {operation} failed")
                QMessageBox.warning(self, "Operation Failed", f"Failed to {operation} server")
        else:
            self.console_output.append(f"Operation failed: {operation}")
            QMessageBox.critical(self, "Error", f"Error during operation: {operation}")
        
        # Update server status
        self.update_server_status()
        
        # Clean up
        self.worker.deleteLater()
        self.current_operation = None
        
    def initUI(self):
        self.setWindowTitle(f'Enshrouded Server Manager By Sibercat v{VERSION}')
        self.setMinimumSize(800, 600)

        # Create central widget and main layout
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        layout = QVBoxLayout(central_widget)

        # Create server status section
        self.create_status_section(layout)

        # Create main tab widget
        tabs = QTabWidget()
        layout.addWidget(tabs)

        # Add different tabs
        tabs.addTab(self.create_control_tab(), "Server Control")
        tabs.addTab(self.create_settings_tab(), "Settings")
        tabs.addTab(self.create_backup_tab(), "Backup")
        tabs.addTab(self.create_logs_tab(), "Logs")

    def create_status_section(self, parent_layout):
        status_group = QGroupBox("Server Status")
        status_layout = QVBoxLayout()

        # Status indicators
        status_row = QHBoxLayout()
        self.status_label = QLabel("Status: ")
        self.status_indicator = QLabel("Stopped")
        self.status_indicator.setStyleSheet("color: red; font-weight: bold;")
        status_row.addWidget(self.status_label)
        status_row.addWidget(self.status_indicator)
        
        # Add version label
        self.version_label = QLabel("Version: Unknown")  # Add this line
        status_row.addWidget(self.version_label)         # Add this line
        
        status_row.addStretch()

        # Server info
        info_layout = QHBoxLayout()
        self.server_name_label = QLabel(f"Server Name: {self.server.config['server_name']}")
        self.player_count_label = QLabel("Players: 0/16")
        info_layout.addWidget(self.server_name_label)
        info_layout.addWidget(self.player_count_label)
        info_layout.addStretch()

        status_layout.addLayout(status_row)
        status_layout.addLayout(info_layout)
        status_group.setLayout(status_layout)
        parent_layout.addWidget(status_group)

    def create_control_tab(self):
        widget = QWidget()
        layout = QVBoxLayout(widget)

        # Control buttons
        button_layout = QHBoxLayout()
        
        # Create buttons
        self.start_btn = QPushButton("Start Server")
        self.stop_btn = QPushButton("Stop Server")
        self.restart_btn = QPushButton("Restart Server")
        self.update_btn = QPushButton("Update Server")

        # Connect button signals to slots
        self.start_btn.clicked.connect(lambda: self.on_server_operation("start"))
        self.stop_btn.clicked.connect(lambda: self.on_server_operation("stop"))
        self.restart_btn.clicked.connect(lambda: self.on_server_operation("restart"))
        self.update_btn.clicked.connect(lambda: self.on_server_operation("update"))

        # Add buttons to layout
        for btn in [self.start_btn, self.stop_btn, self.restart_btn, self.update_btn]:
            btn.setMinimumWidth(120)
            button_layout.addWidget(btn)

        layout.addLayout(button_layout)

        # Performance monitoring
        perf_group = QGroupBox("Performance")
        perf_layout = QVBoxLayout()
        
        self.cpu_bar = QProgressBar()
        self.memory_bar = QProgressBar()
        self.cpu_bar.setFormat("CPU: %p%")
        self.memory_bar.setFormat("Memory: %p%")
        
        perf_layout.addWidget(self.cpu_bar)
        perf_layout.addWidget(self.memory_bar)
        perf_group.setLayout(perf_layout)
        layout.addWidget(perf_group)

        # Add console output
        console_group = QGroupBox("Console Output")
        console_layout = QVBoxLayout()
        self.console_output = QTextEdit()
        self.console_output.setReadOnly(True)
        console_layout.addWidget(self.console_output)
        console_group.setLayout(console_layout)
        layout.addWidget(console_group)

        return widget
        
    def create_settings_tab(self):
        widget = QWidget()
        layout = QVBoxLayout(widget)

        # Server Settings
        server_group = QGroupBox("Server Settings")
        server_layout = QVBoxLayout()

        # Server name
        name_layout = QHBoxLayout()
        name_layout.addWidget(QLabel("Server Name:"))
        self.server_name_input = QLineEdit(self.server.config['server_name'])
        name_layout.addWidget(self.server_name_input)

        # Ports
        ports_layout = QHBoxLayout()
        ports_layout.addWidget(QLabel("Game Port:"))
        self.game_port_input = QSpinBox()
        self.game_port_input.setRange(1, 65535)
        self.game_port_input.setValue(self.server.config['game_port'])
        ports_layout.addWidget(self.game_port_input)
        
        ports_layout.addWidget(QLabel("Query Port:"))
        self.query_port_input = QSpinBox()
        self.query_port_input.setRange(1, 65535)
        self.query_port_input.setValue(self.server.config['query_port'])
        ports_layout.addWidget(self.query_port_input)

        # Game settings
        game_layout = QHBoxLayout()
        game_layout.addWidget(QLabel("Max Players:"))
        self.max_players_input = QSpinBox()
        self.max_players_input.setRange(1, 16)
        self.max_players_input.setValue(self.server.config['max_players'])
        game_layout.addWidget(self.max_players_input)

        self.pvp_enabled = QCheckBox("Enable PvP")
        self.pvp_enabled.setChecked(self.server.config['pvp_enabled'])
        game_layout.addWidget(self.pvp_enabled)

        # Add all layouts to server settings
        server_layout.addLayout(name_layout)
        server_layout.addLayout(ports_layout)
        server_layout.addLayout(game_layout)
        
        # Game difficulty
        difficulty_layout = QHBoxLayout()
        difficulty_layout.addWidget(QLabel("Game Difficulty:"))
        self.difficulty_input = QLineEdit(self.server.config['game_difficulty'])
        difficulty_layout.addWidget(self.difficulty_input)
        server_layout.addLayout(difficulty_layout)

        # Multipliers
        multipliers_layout = QHBoxLayout()
        
        # Enemy Damage Multiplier
        multipliers_layout.addWidget(QLabel("Enemy Damage:"))
        self.enemy_damage_input = QDoubleSpinBox()
        self.enemy_damage_input.setRange(0.1, 10.0)
        self.enemy_damage_input.setSingleStep(0.1)
        self.enemy_damage_input.setValue(self.server.config['enemy_damage_multiplier'])
        multipliers_layout.addWidget(self.enemy_damage_input)
        
        # Enemy Health Multiplier
        multipliers_layout.addWidget(QLabel("Enemy Health:"))
        self.enemy_health_input = QDoubleSpinBox()
        self.enemy_health_input.setRange(0.1, 10.0)
        self.enemy_health_input.setSingleStep(0.1)
        self.enemy_health_input.setValue(self.server.config['enemy_health_multiplier'])
        multipliers_layout.addWidget(self.enemy_health_input)
        
        server_layout.addLayout(multipliers_layout)

        # Time settings
        time_layout = QHBoxLayout()
        
        # Day Length
        time_layout.addWidget(QLabel("Day Length (minutes):"))
        self.day_length_input = QSpinBox()
        self.day_length_input.setRange(1, 120)
        self.day_length_input.setValue(self.server.config['day_length_minutes'])
        time_layout.addWidget(self.day_length_input)
        
        # Night Length
        time_layout.addWidget(QLabel("Night Length (minutes):"))
        self.night_length_input = QSpinBox()
        self.night_length_input.setRange(1, 120)
        self.night_length_input.setValue(self.server.config['night_length_minutes'])
        time_layout.addWidget(self.night_length_input)
        
        server_layout.addLayout(time_layout)

        server_group.setLayout(server_layout)
        layout.addWidget(server_group)

        # Password Settings
        password_group = QGroupBox("Password Settings")
        password_layout = QVBoxLayout()
        
        # Admin Password
        admin_layout = QHBoxLayout()
        admin_layout.addWidget(QLabel("Admin Password:"))
        self.admin_password_input = QLineEdit(self.server.config['admin_password'])
        admin_layout.addWidget(self.admin_password_input)
        password_layout.addLayout(admin_layout)
        
        # Friend Password
        friend_layout = QHBoxLayout()
        friend_layout.addWidget(QLabel("Friend Password:"))
        self.friend_password_input = QLineEdit(self.server.config['friend_password'])
        friend_layout.addWidget(self.friend_password_input)
        password_layout.addLayout(friend_layout)
        
        # Guest Password
        guest_layout = QHBoxLayout()
        guest_layout.addWidget(QLabel("Guest Password:"))
        self.guest_password_input = QLineEdit(self.server.config['guest_password'])
        guest_layout.addWidget(self.guest_password_input)
        password_layout.addLayout(guest_layout)
        
        password_group.setLayout(password_layout)
        layout.addWidget(password_group)

        # Auto-Restart Settings
        restart_group = QGroupBox("Auto-Restart Settings")
        restart_layout = QVBoxLayout()

        self.auto_restart_enabled = QCheckBox("Enable Auto-Restart")
        self.auto_restart_enabled.setChecked(self.server.config.get('auto_restart', False))
        restart_layout.addWidget(self.auto_restart_enabled)

        restart_interval_layout = QHBoxLayout()
        restart_interval_layout.addWidget(QLabel("Restart Interval (hours):"))
        self.restart_interval = QSpinBox()
        self.restart_interval.setRange(1, 168)
        self.restart_interval.setValue(self.server.config.get('restart_interval', 24))
        restart_interval_layout.addWidget(self.restart_interval)
        restart_layout.addLayout(restart_interval_layout)

        warning_layout = QHBoxLayout()
        warning_layout.addWidget(QLabel("Warning Time (minutes):"))
        self.restart_warning = QSpinBox()
        self.restart_warning.setRange(0, 30)
        self.restart_warning.setValue(self.server.config.get('restart_warning_minutes', 5))
        warning_layout.addWidget(self.restart_warning)
        restart_layout.addLayout(warning_layout)

        self.next_restart_label = QLabel("Next Restart: Not scheduled")
        restart_layout.addWidget(self.next_restart_label)

        restart_group.setLayout(restart_layout)
        layout.addWidget(restart_group)

        # Save button
        save_btn = QPushButton("Save Settings")
        save_btn.clicked.connect(self.save_settings)
        layout.addWidget(save_btn)

        # Add a scroll area
        scroll = QScrollArea()
        scroll.setWidget(widget)
        scroll.setWidgetResizable(True)
        
        container = QWidget()
        container_layout = QVBoxLayout(container)
        container_layout.addWidget(scroll)
        
        return container
        
    def create_backup_tab(self):
        widget = QWidget()
        layout = QVBoxLayout(widget)

        # Backup settings
        backup_group = QGroupBox("Backup Settings")
        backup_layout = QVBoxLayout()

        # Auto-backup checkbox
        self.auto_backup_enabled = QCheckBox("Enable Auto-Backup")
        auto_backup_config = self.server.config.get('auto_backup', {})
        self.auto_backup_enabled.setChecked(auto_backup_config.get('enabled', False))
        backup_layout.addWidget(self.auto_backup_enabled)

        # Backup interval
        interval_layout = QHBoxLayout()
        interval_layout.addWidget(QLabel("Backup Interval (minutes):"))
        self.backup_interval = QSpinBox()
        self.backup_interval.setRange(5, 1440)
        self.backup_interval.setValue(auto_backup_config.get('interval_minutes', 360))
        interval_layout.addWidget(self.backup_interval)
        backup_layout.addLayout(interval_layout)

        # Retention settings
        retention_layout = QHBoxLayout()
        retention_layout.addWidget(QLabel("Keep Backups (days):"))
        self.retention_days = QSpinBox()
        self.retention_days.setRange(1, 30)
        self.retention_days.setValue(auto_backup_config.get('keep_days', 7))
        retention_layout.addWidget(self.retention_days)
        backup_layout.addLayout(retention_layout)

        # Add backup on shutdown option
        self.backup_on_shutdown = QCheckBox("Backup on Server Shutdown")
        self.backup_on_shutdown.setChecked(auto_backup_config.get('backup_on_shutdown', True))
        backup_layout.addWidget(self.backup_on_shutdown)

        backup_group.setLayout(backup_layout)
        layout.addWidget(backup_group)

        # Manual backup button
        backup_btn = QPushButton("Create Backup Now")
        backup_btn.clicked.connect(self.create_backup)
        layout.addWidget(backup_btn)
        
        # Save button
        save_btn = QPushButton("Save Settings")
        save_btn.clicked.connect(self.save_settings)
        layout.addWidget(save_btn)

        # Next backup info
        self.next_backup_label = QLabel("Next Backup: Not scheduled")
        layout.addWidget(self.next_backup_label)

        # Backup list
        backup_list_group = QGroupBox("Existing Backups")
        backup_list_layout = QVBoxLayout()
        self.backup_list = QTextEdit()
        self.backup_list.setReadOnly(True)
        backup_list_layout.addWidget(self.backup_list)
        backup_list_group.setLayout(backup_list_layout)
        layout.addWidget(backup_list_group)

        # Refresh backup list button
        refresh_btn = QPushButton("Refresh Backup List")
        refresh_btn.clicked.connect(self.refresh_backup_list)
        layout.addWidget(refresh_btn)

        # Initial backup list refresh
        self.refresh_backup_list()

        return widget

    def create_logs_tab(self):
        widget = QWidget()
        layout = QVBoxLayout(widget)

        # Log viewer
        log_group = QGroupBox("Server Logs")
        log_layout = QVBoxLayout()
        self.log_viewer = QTextEdit()
        self.log_viewer.setReadOnly(True)
        log_layout.addWidget(self.log_viewer)
        log_group.setLayout(log_layout)
        layout.addWidget(log_group)

        # Refresh button
        refresh_btn = QPushButton("Refresh Logs")
        refresh_btn.clicked.connect(self.refresh_logs)
        layout.addWidget(refresh_btn)

        # Initial log load
        self.refresh_logs()

        return widget

    def create_backup(self):
        if self.server.backup():
            self.console_output.append("Backup created successfully")
            self.refresh_backup_list()
        else:
            QMessageBox.warning(self, "Error", "Failed to create backup")

    def refresh_logs(self):
        try:
            log_path = os.path.join(self.server.config['server_dir'], 'logs', 'enshrouded_server.log')
            if os.path.exists(log_path):
                with open(log_path, 'r', encoding='utf-8') as f:
                    self.log_viewer.setText(f.read())
            else:
                self.log_viewer.setText("No log file found")
        except Exception as e:
            self.log_viewer.setText(f"Error reading logs: {str(e)}")
            logging.error(f"Error refreshing logs: {e}")

    def refresh_backup_list(self):
        try:
            backup_dir = Path(self.server.config['backup_dir'])
            if backup_dir.exists():
                backups = list(backup_dir.glob("enshrouded_backup_*.zip"))
                backup_list = "\n".join([b.name for b in sorted(backups, reverse=True)])
                self.backup_list.setText(backup_list if backup_list else "No backups found")
            else:
                self.backup_list.setText("Backup directory not found")
        except Exception as e:
            self.backup_list.setText(f"Error listing backups: {str(e)}")
            logging.error(f"Error refreshing backup list: {e}")

    def save_settings(self):
        try:
            # Server settings
            self.server.config['server_name'] = self.server_name_input.text()
            self.server.config['game_port'] = self.game_port_input.value()
            self.server.config['query_port'] = self.query_port_input.value()
            self.server.config['max_players'] = self.max_players_input.value()
            self.server.config['pvp_enabled'] = self.pvp_enabled.isChecked()
            
            # Game settings
            self.server.config['game_difficulty'] = self.difficulty_input.text()
            self.server.config['enemy_damage_multiplier'] = self.enemy_damage_input.value()
            self.server.config['enemy_health_multiplier'] = self.enemy_health_input.value()
            self.server.config['day_length_minutes'] = self.day_length_input.value()
            self.server.config['night_length_minutes'] = self.night_length_input.value()
            
            # Password settings
            self.server.config['admin_password'] = self.admin_password_input.text()
            self.server.config['friend_password'] = self.friend_password_input.text()
            self.server.config['guest_password'] = self.guest_password_input.text()
            
            # Auto-restart settings
            self.server.config['auto_restart'] = self.auto_restart_enabled.isChecked()
            self.server.config['restart_interval'] = self.restart_interval.value()
            self.server.config['restart_warning_minutes'] = self.restart_warning.value()
            
            # Auto-backup settings
            auto_backup_config = {
                'enabled': self.auto_backup_enabled.isChecked(),
                'interval_minutes': self.backup_interval.value(),
                'keep_days': self.retention_days.value(),
                'backup_on_shutdown': self.backup_on_shutdown.isChecked()
            }
            self.server.config['auto_backup'] = auto_backup_config
            
            if self.server.config_manager.save_config():
                # Update server configuration file
                self.server.config_manager.update_server_json(self.server.config["server_dir"])
                
                # Update managers
                if hasattr(self.server, 'auto_backup'):
                    self.server.auto_backup.update_from_config()
                    
                if hasattr(self.server, 'auto_restart'):
                    self.server.auto_restart.update_from_config()
                
                self.console_output.append("Settings saved successfully")
                QMessageBox.information(self, "Success", "Settings saved successfully")
            else:
                QMessageBox.warning(self, "Error", "Failed to save settings")
        except Exception as e:
            logging.error(f"Error saving settings: {e}")
            QMessageBox.critical(self, "Error", f"Error saving settings: {str(e)}")

def launch_ui(server_manager):
    """Launch the server manager UI."""
    try:
        app = QApplication(sys.argv)
        
        # Create main window with server_manager
        window = ServerManagerUI(server_manager)
        
        # Handle system signals for clean shutdown
        def signal_handler(signum, frame):
            """Handle system shutdown signals"""
            try:
                logging.info(f"Received signal {signum}")
                
                # Create progress dialog
                progress = QProgressDialog("Shutting down server manager...", None, 0, 3, window)
                progress.setWindowModality(Qt.WindowModality.WindowModal)
                progress.setWindowTitle("Shutting Down")
                progress.setCancelButton(None)
                progress.setMinimumDuration(0)
                progress.show()
                
                # Shutdown backup manager
                if hasattr(server_manager, 'auto_backup'):
                    progress.setLabelText("Shutting down backup manager...\nThis might take a moment if backup on shutdown is enabled.")
                    server_manager.auto_backup.shutdown()
                progress.setValue(1)
                
                # Shutdown restart manager
                if hasattr(server_manager, 'auto_restart'):
                    progress.setLabelText("Shutting down restart manager...")
                    server_manager.auto_restart.shutdown()
                progress.setValue(2)
                
                progress.setLabelText("Finalizing shutdown...")
                progress.setValue(3)
                
                QApplication.quit()
                
            except Exception as e:
                logging.error(f"Error during signal handler shutdown: {e}")
                QApplication.quit()
        
        # Register signal handlers for clean shutdown
        signal.signal(signal.SIGINT, signal_handler)
        signal.signal(signal.SIGTERM, signal_handler)
        
        window.show()
        sys.exit(app.exec())
        
    except Exception as e:
        logging.error(f"Error launching UI: {e}")
        raise

# Update the main script section as well
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
        
        # Verify server initialization
        if not server or not hasattr(server, 'config'):
            raise RuntimeError("Failed to initialize server manager properly")
            
        # Launch the Qt UI
        launch_ui(server)
        
    except KeyboardInterrupt:
        print("\nCaught keyboard interrupt. Exiting...")
        sys.exit(0)
    except Exception as e:
        print(f"Fatal error: {e}")
        logging.critical(f"Fatal error: {e}")
        sys.exit(1)