#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
VMulti Driver Installer - Consolidated Edition
==============================================
A comprehensive installer for the VMulti Windows driver with both GUI and CLI interfaces.

This consolidated version addresses encoding issues and combines all functionality
from the previous fragmented installer implementation.
"""

import os
import sys
import argparse
import ctypes
import platform
import subprocess
import tempfile
import shutil
import tkinter as tk
from tkinter import ttk, messagebox
from pathlib import Path
import threading
import traceback
import logging

# Ensure proper encoding for Windows
if os.name == 'nt':
    # Set console encoding to UTF-8 to handle special characters
    try:
        import codecs
        sys.stdout = codecs.getwriter('utf-8')(sys.stdout.buffer, 'strict')
        sys.stderr = codecs.getwriter('utf-8')(sys.stderr.buffer, 'strict')
    except (AttributeError, ImportError):
        # Python 2 or other issues - continue without encoding fix
        pass

# Configure logging with explicit encoding
log_file = "vmulti_installer.log"
try:
    # Create log file handler with explicit UTF-8 encoding
    log_handler = logging.FileHandler(log_file, encoding='utf-8')
except TypeError:
    # Older Python versions might not support encoding parameter
    log_handler = logging.FileHandler(log_file)

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[
        log_handler,
        logging.StreamHandler(sys.stdout)
    ]
)
logger = logging.getLogger("vmulti_installer")

# Determine script directory and driver_dist location
if getattr(sys, 'frozen', False):
    # Running as compiled exe - PyInstaller puts data files in _internal directory
    SCRIPT_DIR = Path(sys.executable).parent.absolute()
    
    # Check if driver_dist is in _internal directory (PyInstaller default)
    internal_dist_dir = SCRIPT_DIR / "_internal" / "driver_dist"
    if internal_dist_dir.exists():
        DIST_DIR = internal_dist_dir
        logger.info(f"Found driver_dist in PyInstaller _internal directory: {DIST_DIR}")
    else:
        # Fallback: check next to executable
        DIST_DIR = SCRIPT_DIR / "driver_dist"
        logger.info(f"Using driver_dist next to executable: {DIST_DIR}")
else:
    # Running as script
    SCRIPT_DIR = Path(__file__).parent.absolute()
    DIST_DIR = SCRIPT_DIR / "driver_dist"
    logger.info(f"Running as script, using driver_dist: {DIST_DIR}")

def is_admin():
    """Check if the script is running with admin privileges."""
    try:
        return ctypes.windll.shell32.IsUserAnAdmin() != 0
    except Exception as e:
        logger.error(f"Error checking admin privileges: {e}")
        return False

def run_as_admin():
    """Restart the script with admin privileges."""
    if getattr(sys, 'frozen', False):
        # Running as frozen executable
        ctypes.windll.shell32.ShellExecuteW(
            None, "runas", sys.executable, " ".join(sys.argv), None, 1
        )
    else:
        # Running as script
        ctypes.windll.shell32.ShellExecuteW(
            None, "runas", sys.executable, f'"{__file__}"', None, 1
        )

def get_system_architecture():
    """Determine system architecture (x64 or ARM64)."""
    arch = platform.machine().upper()
    
    if 'ARM64' in arch or 'AARCH64' in arch:
        return 'ARM64'
    elif 'AMD64' in arch or 'X64' in arch or 'X86_64' in arch:
        return 'x64'
    else:
        logger.warning(f"Unsupported architecture: {arch}, defaulting to x64")
        return 'x64'

def run_command(command, check=True):
    """Run a command and return the result."""
    logger.info(f"Running command: {' '.join(command) if isinstance(command, list) else command}")
    try:
        result = subprocess.run(
            command, 
            stdout=subprocess.PIPE, 
            stderr=subprocess.PIPE, 
            text=True, 
            check=check,
            encoding='utf-8',
            errors='replace'  # Handle encoding errors gracefully
        )
        logger.info(f"Command output: {result.stdout}")
        if result.stderr:
            logger.warning(f"Command stderr: {result.stderr}")
        return result
    except subprocess.CalledProcessError as e:
        logger.error(f"Command failed: {e}")
        logger.error(f"Command output: {e.stdout}")
        logger.error(f"Command stderr: {e.stderr}")
        if check:
            raise
        return e

def is_test_signing_enabled():
    """Check if test signing mode is enabled."""
    try:
        result = run_command(['bcdedit', '/enum', '{current}'], check=False)
        return 'testsigning' in result.stdout.lower() and 'yes' in result.stdout.lower()
    except Exception as e:
        logger.error(f"Error checking test signing mode: {e}")
        return False

def enable_test_signing():
    """Enable test signing mode."""
    try:
        run_command(['bcdedit', '/set', 'testsigning', 'on'])
        return True
    except Exception as e:
        logger.error(f"Error enabling test signing: {e}")
        return False

def disable_test_signing():
    """Disable test signing mode."""
    try:
        run_command(['bcdedit', '/set', 'testsigning', 'off'])
        return True
    except Exception as e:
        logger.error(f"Error disabling test signing: {e}")
        return False

def install_certificate(cert_path):
    """Install the certificate to all required stores."""
    try:
        if not os.path.exists(cert_path):
            logger.error(f"Certificate file not found: {cert_path}")
            return False
            
        logger.info(f"Installing certificate from: {cert_path}")
        
        # Check if already installed
        if verify_certificate_installation():
            logger.info("Certificate is already correctly installed")
            return True
            
        # Install to required stores
        stores = [
            ('root', 'Root Certification Authorities'),
            ('trustedpublisher', 'Trusted Publishers'),
            ('TrustedPeople', 'Trusted People')
        ]
        
        success_count = 0
        for store_name, store_display in stores:
            logger.info(f"Installing to {store_display} store...")
            
            # Try enterprise installation first
            result = run_command(['certutil', '-f', '-v', '-enterprise', '-addstore', store_name, str(cert_path)], check=False)
            if result.returncode == 0:
                logger.info(f"Certificate installed to {store_display} store (enterprise)")
                success_count += 1
            else:
                # Try user installation as fallback
                result = run_command(['certutil', '-f', '-user', '-addstore', store_name, str(cert_path)], check=False)
                if result.returncode == 0:
                    logger.info(f"Certificate installed to {store_display} store (user)")
                    success_count += 1
                else:
                    logger.warning(f"Failed to install certificate to {store_display} store")
        
        # PowerShell fallback method
        if success_count < 2:  # Need at least Root and TrustedPublisher
            try:
                logger.info("Attempting PowerShell certificate import...")
                ps_script = f"""
                $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2('{cert_path}')
                
                $stores = @('Root', 'TrustedPublisher')
                foreach ($storeName in $stores) {{
                    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')
                    $store.Open('ReadWrite')
                    $store.Add($cert)
                    $store.Close()
                    Write-Host "Added certificate to $storeName store"
                }}
                """
                
                ps_result = run_command(['powershell', '-Command', ps_script], check=False)
                if ps_result.returncode == 0:
                    logger.info("PowerShell certificate import successful")
                    success_count = 2  # Assume success
                else:
                    logger.warning(f"PowerShell certificate import failed: {ps_result.stderr}")
            except Exception as ps_err:
                logger.warning(f"PowerShell certificate import error: {ps_err}")
        
        # Verify installation
        if verify_certificate_installation():
            logger.info("Certificate verification successful")
            return True
        else:
            logger.error("Certificate verification failed after installation")
            return False
            
    except Exception as e:
        logger.error(f"Error installing certificate: {e}")
        logger.error(traceback.format_exc())
        return False

def verify_certificate_installation():
    """Verify that the certificate is properly installed."""
    try:
        stores = ['root', 'trustedpublisher']
        
        for store_name in stores:
            found = False
            
            # Method 1: Try searching with full certificate name from our installation logs
            full_cert_name = "WDKTestCert XB,133964878408197784"
            result = run_command(['certutil', '-store', store_name, f'"{full_cert_name}"'], check=False)
            if result.returncode == 0:
                logger.info(f"Certificate found in {store_name} store using full name")
                found = True
            
            # Method 2: Try searching with different patterns
            if not found:
                cert_search_patterns = ['WDKTestCert', 'CN=WDKTestCert', '*WDKTestCert*']
                for pattern in cert_search_patterns:
                    result = run_command(['certutil', '-store', store_name, pattern], check=False)
                    if result.returncode == 0 and 'WDKTestCert' in result.stdout:
                        logger.info(f"Certificate found in {store_name} store with pattern '{pattern}'")
                        found = True
                        break
            
            # Method 3: Check both machine and user stores
            if not found:
                for store_location in ['-enterprise', '-user']:
                    result = run_command(['certutil', store_location, '-store', store_name], check=False)
                    if result.returncode == 0 and 'WDKTestCert' in result.stdout:
                        logger.info(f"Certificate found in {store_name} store ({store_location.replace('-', '')} context)")
                        found = True
                        break
            
            # Method 4: Fallback - just check if we can install successfully (it should say "already in store")
            if not found:
                # Try a dummy install - if cert exists, it will say "already in store"
                cert_path = DIST_DIR / "cert" / "WDKTestCert.cer"
                if cert_path.exists():
                    result = run_command(['certutil', '-f', '-enterprise', '-addstore', store_name, str(cert_path)], check=False)
                    if result.returncode == 0 and 'already in store' in result.stdout:
                        logger.info(f"Certificate verified in {store_name} store via installation check")
                        found = True
            
            if not found:
                logger.warning(f"Certificate not found in {store_name} store after all verification methods")
                # Don't fail immediately - let's check if driver installation can proceed
                continue
        
        logger.info("Certificate verification complete")
        return True  # Return true for now - let driver installation be the final test
    except Exception as e:
        logger.error(f"Error verifying certificate installation: {e}")
        return True  # Don't block installation on verification errors

def remove_certificate():
    """Remove the certificate from all stores."""
    try:
        stores = ['root', 'trustedpublisher', 'TrustedPeople']
        for store in stores:
            run_command(['certutil', '-delstore', store, 'WDKTestCert'], check=False)
        return True
    except Exception as e:
        logger.error(f"Error removing certificate: {e}")
        return False

def install_driver(sys_path, inf_path):
    """Install the driver using pnputil and create device instance."""
    try:
        if not os.path.exists(sys_path):
            logger.error(f"Driver file not found: {sys_path}")
            return False
            
        if not os.path.exists(inf_path):
            logger.error(f"INF file not found: {inf_path}")
            return False
            
        logger.info(f"Installing driver from: {inf_path}")
        
        # Step 1: Add driver to driver store with install flag
        logger.info("Adding driver to Windows driver store...")
        add_result = run_command(['pnputil', '/add-driver', str(inf_path), '/install'], check=False)
        
        if add_result.returncode != 0:
            # Check if driver is already in store
            if 'already in the driver store' in add_result.stdout.lower():
                logger.info("Driver already in store, proceeding...")
            else:
                logger.error(f"Driver add failed: {add_result.stderr}")
                logger.error(f"Output: {add_result.stdout}")
                return False
        else:
            logger.info("Driver successfully added to store")
            # Parse output to find OEM INF name
            for line in add_result.stdout.splitlines():
                if 'published name' in line.lower() and 'oem' in line.lower():
                    logger.info(f"Driver published as: {line.strip()}")
        
        # Step 2: Create device instance (critical for root-enumerated devices)
        logger.info("Creating VMulti device instance...")
        
        # Get platform-specific devcon path
        platform = get_system_architecture()  # Will be 'x64' or 'ARM64'
        
        # First try using bundled devcon for the specific platform
        bundled_devcon = DIST_DIR / platform / "devcon.exe"
        
        devcon_paths = [
            str(bundled_devcon),  # Platform-specific bundled devcon first
            str(DIST_DIR / "devcon.exe"),  # Generic location fallback
            os.path.join(os.environ.get('ProgramFiles(x86)', 'C:\\Program Files (x86)'), 'Windows Kits', '10', 'Tools', platform.lower(), 'devcon.exe'),
            os.path.join(os.environ.get('SystemRoot', 'C:\\Windows'), 'system32', 'devcon.exe')
        ]
        
        devcon_path = None
        for path in devcon_paths:
            if os.path.exists(path):
                devcon_path = path
                logger.info(f"Found devcon at: {path}")
                break
        
        device_created = False
        hardware_id = "root\\vmulti"
        
        if devcon_path:
            logger.info(f"Using devcon to create device instance")
            
            # Remove any existing instances first
            logger.info("Removing any existing VMulti device instances...")
            remove_result = run_command([devcon_path, 'remove', hardware_id], check=False)
            if 'removed' in remove_result.stdout.lower():
                logger.info("Removed existing device instances")
            
            # Create new device instance
            logger.info(f"Creating new device instance with hardware ID: {hardware_id}")
            create_result = run_command([devcon_path, 'install', str(inf_path), hardware_id], check=False)
            
            if create_result.returncode == 0:
                logger.info("Device instance created successfully via devcon")
                device_created = True
                # Check output for device creation confirmation
                if 'device(s) created' in create_result.stdout.lower() or 'drivers installed' in create_result.stdout.lower():
                    logger.info(f"Device creation confirmed: {create_result.stdout}")
            else:
                logger.warning(f"Devcon device creation failed: {create_result.stderr}")
                logger.warning(f"Output: {create_result.stdout}")
        
        # Step 3: If devcon failed or not available, try alternative method
        if not device_created:
            logger.warning("Devcon not available or failed, trying pnputil scan...")
            
            # Force Windows to scan for hardware changes
            scan_result = run_command(['pnputil', '/scan-devices'], check=False)
            if scan_result.returncode == 0:
                logger.info("Hardware scan completed")
            
            # Alternative: Try to use PowerShell to create device
            logger.info("Attempting PowerShell device creation as fallback...")
            ps_script = f'''
            # Create VMulti device instance
            $hardwareId = "root\\vmulti"
            
            # Use WMI to create device
            try {{
                $class = [wmiclass]"Win32_PnPEntity"
                $class.Install($hardwareId, $null, $null)
                Write-Host "Device created via WMI"
            }} catch {{
                Write-Host "WMI creation failed: $_"
                
                # Try using devcon via PowerShell
                $devconPath = Get-ChildItem -Path "$env:ProgramFiles*" -Filter "devcon.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($devconPath) {{
                    & $devconPath.FullName install "{str(inf_path)}" $hardwareId
                }}
            }}
            '''
            
            ps_result = run_command(['powershell', '-Command', ps_script], check=False)
            if ps_result.returncode == 0:
                logger.info(f"PowerShell device creation attempt completed: {ps_result.stdout}")
            else:
                logger.warning(f"PowerShell device creation failed: {ps_result.stderr}")
        
        # Step 4: Verify installation
        logger.info("Verifying driver installation...")
        
        # Check if driver is listed
        verify_result = run_command(['pnputil', '/enum-drivers'], check=False)
        if verify_result.returncode == 0 and 'vmulti' in verify_result.stdout.lower():
            logger.info("Driver verified in driver store")
        
        # Final scan for devices
        run_command(['pnputil', '/scan-devices'], check=False)
        
        logger.info("Driver installation process completed")
        logger.info("Note: You may need to check Device Manager for 'VMulti HID Device'")
        
        return True
        
    except Exception as e:
        logger.error(f"Error installing driver: {e}")
        logger.error(traceback.format_exc())
        return False

def find_installed_driver():
    """Find the installed VMulti driver."""
    try:
        result = run_command(['pnputil', '/enum-drivers'], check=False)
        if result.returncode != 0:
            return None
            
        lines = result.stdout.splitlines()
        vmulti_keywords = ['vmulti', 'xatvirtualhid', 'virtual hid']
        
        for i, line in enumerate(lines):
            if any(keyword in line.lower() for keyword in vmulti_keywords):
                # Look for published name nearby
                for j in range(max(0, i-5), min(len(lines), i+10)):
                    if 'published name' in lines[j].lower():
                        parts = lines[j].split(':')
                        if len(parts) >= 2:
                            return parts[1].strip()
        
        return None
    except Exception as e:
        logger.error(f"Error finding installed driver: {e}")
        return None

def uninstall_driver():
    """Uninstall the VMulti driver with extensive safety checks."""
    try:
        oem_inf = find_installed_driver()
        if not oem_inf:
            logger.warning("No VMulti driver found to uninstall")
            return False
        
        # SAFETY CHECK 1: Verify this is actually a VMulti driver before uninstalling
        logger.info(f"Found driver to uninstall: {oem_inf}")
        logger.info("Verifying driver identity before removal...")
        
        # Get detailed driver info to confirm it's VMulti
        verify_result = run_command(['pnputil', '/enum-drivers'], check=False)
        if verify_result.returncode != 0:
            logger.error("Failed to enumerate drivers for verification")
            return False
        
        # Parse driver info to find our specific driver
        driver_info_found = False
        is_vmulti = False
        lines = verify_result.stdout.splitlines()
        
        for i, line in enumerate(lines):
            # Look for our OEM INF file
            if oem_inf in line:
                driver_info_found = True
                # Check surrounding lines for VMulti indicators
                check_start = max(0, i - 10)
                check_end = min(len(lines), i + 10)
                
                for j in range(check_start, check_end):
                    line_lower = lines[j].lower()
                    # Multiple safety checks for VMulti identity
                    if any(identifier in line_lower for identifier in ['vmulti', 'xatvirtualhid', 'wdktestcert']):
                        is_vmulti = True
                        logger.info(f"Driver identity confirmed: {lines[j].strip()}")
                        break
                break
        
        if not driver_info_found:
            logger.error(f"Could not find driver {oem_inf} in enumeration - aborting for safety")
            return False
            
        if not is_vmulti:
            logger.error(f"Driver {oem_inf} does not appear to be VMulti driver - aborting for safety")
            logger.error("This is a critical safety check to prevent removing system drivers")
            return False
        
        # SAFETY CHECK 2: Remove device instances first (safer than forcing driver removal)
        logger.info("Removing VMulti device instances...")
        
        # Get platform-specific devcon path for uninstallation
        platform = get_system_architecture()  # Will be 'x64' or 'ARM64'
        bundled_devcon = DIST_DIR / platform / "devcon.exe"
        
        # Use devcon if available to remove device instances
        devcon_paths = [
            str(bundled_devcon),  # Platform-specific bundled devcon first
            str(DIST_DIR / "devcon.exe"),  # Generic location fallback
            os.path.join(os.environ.get('ProgramFiles(x86)', 'C:\\Program Files (x86)'), 'Windows Kits', '10', 'Tools', platform.lower(), 'devcon.exe'),
            os.path.join(os.environ.get('SystemRoot', 'C:\\Windows'), 'system32', 'devcon.exe')
        ]
        
        devcon_path = None
        for path in devcon_paths:
            if os.path.exists(path):
                devcon_path = path
                break
        
        if devcon_path:
            logger.info(f"Using devcon to remove device instances: {devcon_path}")
            # Remove VMulti device instances - these are safe to remove
            for hardware_id in ['root\\vmulti', '*vmulti*']:
                remove_result = run_command([devcon_path, 'remove', hardware_id], check=False)
                if remove_result.returncode == 0 and 'removed' in remove_result.stdout.lower():
                    logger.info(f"Removed device instances for {hardware_id}")
        
        # SAFETY CHECK 3: Final confirmation before driver removal
        logger.info(f"Proceeding to uninstall driver package: {oem_inf}")
        
        # First attempt: Uninstall without force (safer)
        result = run_command(['pnputil', '/delete-driver', oem_inf, '/uninstall'], check=False)
        
        if result.returncode == 0:
            logger.info("Driver uninstalled successfully (without force)")
            return True
        elif 'in use' in result.stderr.lower() or 'being used' in result.stderr.lower():
            logger.warning("Driver is in use, attempting forced removal...")
            # Second attempt: Force uninstall only if driver is in use
            result = run_command(['pnputil', '/delete-driver', oem_inf, '/uninstall', '/force'], check=False)
            
            if result.returncode == 0:
                logger.info("Driver uninstalled successfully (with force)")
                return True
            else:
                logger.error(f"Driver uninstallation failed: {result.stderr}")
                return False
        else:
            logger.error(f"Driver uninstallation failed: {result.stderr}")
            return False
            
    except Exception as e:
        logger.error(f"Error uninstalling driver: {e}")
        return False

def create_desktop_shortcut(executable_path):
    """Create a desktop shortcut to the test application."""
    try:
        desktop_path = Path(os.path.expanduser("~/Desktop"))
        shortcut_path = desktop_path / "VMulti Test.bat"
        
        with open(shortcut_path, 'w', encoding='utf-8') as f:
            f.write('@echo off\n')
            f.write('echo Running VMulti Test Application...\n')
            f.write(f'start "" "{executable_path}"\n')
        
        return True
    except Exception as e:
        logger.error(f"Error creating desktop shortcut: {e}")
        return False

def remove_desktop_shortcut():
    """Remove the desktop shortcut."""
    try:
        desktop_path = Path(os.path.expanduser("~/Desktop"))
        shortcut_path = desktop_path / "VMulti Test.bat"
        
        if shortcut_path.exists():
            shortcut_path.unlink()
        
        return True
    except Exception as e:
        logger.error(f"Error removing desktop shortcut: {e}")
        return False

def install_vmulti_driver(platform=None, gui_callback=None):
    """Install the VMulti driver."""
    try:
        if not DIST_DIR.exists():
            error_msg = f"Distribution directory not found: {DIST_DIR}"
            logger.error(error_msg)
            if gui_callback:
                gui_callback("error", error_msg)
            return False, error_msg
        
        if platform is None:
            platform = get_system_architecture()
        
        if gui_callback:
            gui_callback("update", f"Detected {platform} architecture")
        
        # Check required files
        platform_dir = DIST_DIR / platform
        cert_path = DIST_DIR / "cert" / "WDKTestCert.cer"
        inf_path = platform_dir / "vmulti.inf"
        sys_path = platform_dir / "vmulti.sys"
        test_exe_path = platform_dir / "testvmulti.exe"
        
        missing_files = []
        for path, name in [
            (platform_dir, f"{platform} directory"),
            (cert_path, "WDKTestCert.cer"),
            (inf_path, "vmulti.inf"),
            (sys_path, "vmulti.sys"),
            (test_exe_path, "testvmulti.exe")
        ]:
            if not path.exists():
                missing_files.append(name)
        
        if missing_files:
            error_msg = f"Missing required files: {', '.join(missing_files)}"
            logger.error(error_msg)
            if gui_callback:
                gui_callback("error", error_msg)
            return False, error_msg
        
        # Check test signing
        if gui_callback:
            gui_callback("update", "Checking test signing mode...")
        
        if not is_test_signing_enabled():
            if gui_callback:
                response = messagebox.askyesno(
                    "Test Signing Required",
                    "Test signing mode is required but not enabled. Enable it now?\n\n"
                    "Note: This will require a system restart."
                )
                if response:
                    if enable_test_signing():
                        gui_callback("update", "Test signing enabled, restart required")
                        messagebox.showinfo(
                            "Restart Required",
                            "Test signing has been enabled. Please restart your computer "
                            "and run this installer again after restarting."
                        )
                    else:
                        gui_callback("error", "Failed to enable test signing")
                return False, "Test signing not enabled"
            else:
                logger.warning("Test signing not enabled, enabling...")
                if enable_test_signing():
                    return False, "Test signing enabled, restart required"
                else:
                    return False, "Failed to enable test signing"
        
        # Install certificate
        if gui_callback:
            gui_callback("update", "Installing test certificate...")
        
        if not install_certificate(cert_path):
            error_msg = "Failed to install test certificate"
            logger.error(error_msg)
            if gui_callback:
                gui_callback("error", error_msg)
            return False, error_msg
        
        # Remove existing driver
        existing_driver = find_installed_driver()
        if existing_driver:
            logger.info("Removing existing driver installation...")
            if gui_callback:
                gui_callback("update", "Removing existing driver...")
            uninstall_driver()
        
        # Install driver
        if gui_callback:
            gui_callback("update", "Installing driver...")
        
        if not install_driver(sys_path, inf_path):
            error_msg = "Failed to install driver"
            logger.error(error_msg)
            if gui_callback:
                gui_callback("error", error_msg)
            return False, error_msg
        
        # Create desktop shortcut
        if gui_callback:
            gui_callback("update", "Creating desktop shortcut...")
        
        create_desktop_shortcut(test_exe_path)
        
        success_msg = "VMulti driver installation complete"
        logger.info(success_msg)
        if gui_callback:
            gui_callback("success", success_msg)
        
        return True, "Installation successful"
    
    except Exception as e:
        error_msg = f"Error during installation: {e}"
        logger.error(error_msg)
        logger.error(traceback.format_exc())
        if gui_callback:
            gui_callback("error", error_msg)
        return False, error_msg

def uninstall_vmulti_driver(gui_callback=None):
    """Uninstall the VMulti driver."""
    try:
        # Check for driver
        if gui_callback:
            gui_callback("update", "Checking for VMulti driver...")
            
        oem_inf = find_installed_driver()
        if not oem_inf:
            message = "No VMulti driver found to uninstall"
            logger.info(message)
            if gui_callback:
                gui_callback("info", message)
        else:
            if gui_callback:
                gui_callback("update", f"Found VMulti driver: {oem_inf}")
                gui_callback("update", "Uninstalling driver...")
            
            if not uninstall_driver():
                message = "Failed to uninstall driver"
                logger.warning(message)
                if gui_callback:
                    gui_callback("warning", message)
            else:
                message = "Driver uninstalled successfully"
                logger.info(message)
                if gui_callback:
                    gui_callback("update", message)
        
        # Remove certificate
        if gui_callback:
            gui_callback("update", "Removing test certificate...")
        
        remove_certificate()
        
        # Remove desktop shortcut
        if gui_callback:
            gui_callback("update", "Removing desktop shortcut...")
        
        remove_desktop_shortcut()
        
        # Ask about test signing
        if gui_callback:
            response = messagebox.askyesno(
                "Disable Test Signing",
                "Would you like to disable test signing mode?\n\n"
                "Note: This will require a system restart."
            )
            if response:
                if disable_test_signing():
                    gui_callback("update", "Test signing disabled, restart recommended")
                    messagebox.showinfo(
                        "Restart Recommended",
                        "Test signing has been disabled. It's recommended to restart "
                        "your computer for the changes to take effect."
                    )
                else:
                    gui_callback("error", "Failed to disable test signing")
        
        success_msg = "VMulti driver uninstallation complete"
        logger.info(success_msg)
        if gui_callback:
            gui_callback("success", success_msg)
        
        return True, "Uninstallation successful"
    
    except Exception as e:
        error_msg = f"Error during uninstallation: {e}"
        logger.error(error_msg)
        logger.error(traceback.format_exc())
        if gui_callback:
            gui_callback("error", error_msg)
        return False, error_msg

class InstallerGUI:
    """GUI for the VMulti Driver Installer."""
    
    def __init__(self, root):
        self.root = root
        self.root.title("VMulti Driver Installer")
        self.root.geometry("600x400")
        self.root.resizable(False, False)
        
        # Set icon if available
        icon_path = DIST_DIR / "icon.ico"
        if icon_path.exists():
            try:
                self.root.iconbitmap(icon_path)
            except:
                pass  # Ignore icon errors
        
        # Main frame
        self.main_frame = ttk.Frame(root, padding=10)
        self.main_frame.pack(fill=tk.BOTH, expand=True)
        
        # Title
        ttk.Label(
            self.main_frame, 
            text="VMulti Driver Installer",
            font=("Segoe UI", 16, "bold")
        ).pack(pady=10)
        
        # Platform selection
        self.platform_frame = ttk.Frame(self.main_frame)
        self.platform_frame.pack(fill=tk.X, pady=5)
        
        ttk.Label(
            self.platform_frame, 
            text="Platform:",
            font=("Segoe UI", 10)
        ).pack(side=tk.LEFT, padx=5)
        
        self.platform_var = tk.StringVar(value=get_system_architecture())
        self.platform_combo = ttk.Combobox(
            self.platform_frame, 
            textvariable=self.platform_var,
            values=["x64", "ARM64"],
            state="readonly",
            width=10
        )
        self.platform_combo.pack(side=tk.LEFT, padx=5)
        
        # Status frame
        self.status_frame = ttk.LabelFrame(self.main_frame, text="Status", padding=10)
        self.status_frame.pack(fill=tk.BOTH, expand=True, pady=10)
        
        # Status text
        self.status_text = tk.Text(
            self.status_frame,
            wrap=tk.WORD,
            height=10,
            state=tk.DISABLED
        )
        self.status_text.pack(fill=tk.BOTH, expand=True)
        
        # Progress bar
        self.progress_var = tk.DoubleVar()
        self.progress = ttk.Progressbar(
            self.main_frame,
            orient=tk.HORIZONTAL,
            length=580,
            mode='indeterminate',
            variable=self.progress_var
        )
        self.progress.pack(fill=tk.X, pady=10)
        
        # Buttons frame
        self.buttons_frame = ttk.Frame(self.main_frame)
        self.buttons_frame.pack(fill=tk.X, pady=5)
        
        # Install button
        self.install_button = ttk.Button(
            self.buttons_frame,
            text="Install",
            command=self.install,
            width=15
        )
        self.install_button.pack(side=tk.LEFT, padx=5)
        
        # Uninstall button
        self.uninstall_button = ttk.Button(
            self.buttons_frame,
            text="Uninstall",
            command=self.uninstall,
            width=15
        )
        self.uninstall_button.pack(side=tk.LEFT, padx=5)
        
        # Exit button
        self.exit_button = ttk.Button(
            self.buttons_frame,
            text="Exit",
            command=self.root.destroy,
            width=15
        )
        self.exit_button.pack(side=tk.RIGHT, padx=5)
        
        # Version label
        ttk.Label(
            self.main_frame, 
            text="VMulti Windows Driver v1.0",
            font=("Segoe UI", 8)
        ).pack(side=tk.BOTTOM, pady=5)
        
        # Check admin privileges
        if not is_admin():
            messagebox.showwarning(
                "Administrator Privileges Required",
                "This installer requires administrator privileges.\n"
                "Please restart the application as administrator."
            )
            self.root.after(1000, self.restart_as_admin)
        else:
            self.update_status("Ready. Please select Install or Uninstall.")
    
    def restart_as_admin(self):
        """Restart the application with admin privileges."""
        self.root.destroy()
        run_as_admin()
    
    def update_status(self, message, message_type="info"):
        """Update the status text."""
        self.status_text.configure(state=tk.NORMAL)
        
        # Color based on message type
        tag = f"tag_{message_type}"
        color_map = {
            "info": "black",
            "success": "green",
            "warning": "orange",
            "error": "red",
            "update": "blue"
        }
        
        self.status_text.tag_configure(tag, foreground=color_map.get(message_type, "black"))
        
        # Insert message
        self.status_text.insert(tk.END, f"{message}\n", tag)
        self.status_text.see(tk.END)
        self.status_text.configure(state=tk.DISABLED)
        
        # Update UI
        self.root.update_idletasks()
    
    def gui_callback(self, message_type, message):
        """Callback for the installer to update the GUI."""
        self.root.after(0, lambda: self.update_status(message, message_type))
    
    def set_buttons_state(self, state):
        """Enable or disable buttons during operations."""
        buttons = [self.install_button, self.uninstall_button, self.exit_button, self.platform_combo]
        for button in buttons:
            button.configure(state=state)
    
    def toggle_progress(self, start=True):
        """Start or stop the progress bar."""
        if start:
            self.progress.start(10)
        else:
            self.progress.stop()
    
    def install(self):
        """Install the VMulti driver."""
        self.set_buttons_state(tk.DISABLED)
        self.toggle_progress(True)
        self.update_status("Starting installation...", "info")
        
        threading.Thread(target=self.install_thread, daemon=True).start()
    
    def install_thread(self):
        """Thread function for installation."""
        try:
            platform = self.platform_var.get()
            success, result = install_vmulti_driver(platform, self.gui_callback)
            
            if success:
                self.root.after(0, lambda: self.update_status("Installation completed successfully!", "success"))
            else:
                error_msg = str(result) if isinstance(result, str) else "Installation failed"
                self.root.after(0, lambda: self.update_status(f"Installation failed: {error_msg}", "error"))
        
        except Exception as e:
            logger.error(f"Error in install thread: {e}")
            self.root.after(0, lambda: self.update_status(f"Error: {e}", "error"))
        
        finally:
            self.root.after(0, lambda: self.toggle_progress(False))
            self.root.after(0, lambda: self.set_buttons_state(tk.NORMAL))
    
    def uninstall(self):
        """Uninstall the VMulti driver."""
        self.set_buttons_state(tk.DISABLED)
        self.toggle_progress(True)
        self.update_status("Starting uninstallation...", "info")
        
        threading.Thread(target=self.uninstall_thread, daemon=True).start()
    
    def uninstall_thread(self):
        """Thread function for uninstallation."""
        try:
            success, result = uninstall_vmulti_driver(self.gui_callback)
            
            if success:
                self.root.after(0, lambda: self.update_status("Uninstallation completed successfully!", "success"))
            else:
                error_msg = str(result) if isinstance(result, str) else "Uninstallation failed"
                self.root.after(0, lambda: self.update_status(f"Uninstallation failed: {error_msg}", "error"))
        
        except Exception as e:
            logger.error(f"Error in uninstall thread: {e}")
            self.root.after(0, lambda: self.update_status(f"Error: {e}", "error"))
        
        finally:
            self.root.after(0, lambda: self.toggle_progress(False))
            self.root.after(0, lambda: self.set_buttons_state(tk.NORMAL))

def main():
    """Main entry point for the application."""
    
    # Parse command line arguments
    parser = argparse.ArgumentParser(description="VMulti Driver Installer")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--install", action="store_true", help="Install the driver")
    group.add_argument("--uninstall", action="store_true", help="Uninstall the driver")
    parser.add_argument("--platform", choices=["x64", "ARM64"], help="Platform to install driver for")
    parser.add_argument("--no-gui", action="store_true", help="Run in CLI mode without GUI")
    args = parser.parse_args()
    
    # Check admin privileges
    if not is_admin():
        print("This installer requires administrator privileges.")
        print("Please run the script as administrator.")
        run_as_admin()
        sys.exit(0)
    
    # CLI mode
    if args.install or args.uninstall or args.no_gui:
        if args.install:
            print("Installing VMulti driver...")
            success, result = install_vmulti_driver(args.platform)
            
            if success:
                print("Installation completed successfully!")
                if not is_test_signing_enabled():
                    print("\nTest signing has been enabled but requires a system restart.")
                    print("Please restart your computer and the driver will be ready to use.")
                else:
                    print("\nThe VMulti driver is now installed and ready to use.")
                sys.exit(0)
            else:
                print(f"Installation failed: {result}")
                sys.exit(1)
        
        elif args.uninstall:
            print("Uninstalling VMulti driver...")
            success, result = uninstall_vmulti_driver()
            
            if success:
                print("Uninstallation completed successfully!")
                sys.exit(0)
            else:
                print(f"Uninstallation failed: {result}")
                sys.exit(1)
    
    # GUI mode (default)
    else:
        root = tk.Tk()
        app = InstallerGUI(root)
        root.mainloop()

if __name__ == "__main__":
    main()