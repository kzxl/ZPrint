using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ZPrint.Tool
{
    /// <summary>
    /// Manages background execution, system services, and boot/login autostart across Windows, Linux, and macOS.
    /// Pure C# BCL with zero third-party dependencies.
    /// </summary>
    public static class StartupManager
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SwHide = 0;
        private const int SwShow = 5;

        private const string AppName = "ZPrint";
        private const string TaskName = "ZPrintServer";

        /// <summary>
        /// Hides the current console window on Windows to run silently in the background.
        /// </summary>
        public static void HideConsole()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    IntPtr hWnd = GetConsoleWindow();
                    if (hWnd != IntPtr.Zero)
                    {
                        ShowWindow(hWnd, SwHide);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Returns the default persistent log file path for background execution.
        /// </summary>
        public static string GetLogFilePath()
        {
            string baseDir;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZPrint");
            }
            else
            {
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".zprint");
            }

            if (!Directory.Exists(baseDir))
            {
                try { Directory.CreateDirectory(baseDir); } catch { }
            }

            return Path.Combine(baseDir, "zprint-server.log");
        }

        #region User Login Autostart (No Admin Required)

        /// <summary>
        /// Enables automatic startup when the user logs into their desktop session.
        /// </summary>
        public static (bool Success, string Message) EnableAutostart(string extraServerArgs = "")
        {
            string exePath = GetExecutablePath();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string command = $"\"{exePath}\" server --background {extraServerArgs}".Trim();
                string args = $"add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v \"{AppName}\" /t REG_SZ /d \"{command}\" /f";
                var (code, output, err) = RunProcess("reg.exe", args);
                if (code == 0)
                {
                    return (true, $"User autostart enabled successfully.\nCommand: {command}");
                }
                return (false, $"Failed to configure registry: {err} {output}");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    string autostartDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
                    Directory.CreateDirectory(autostartDir);
                    string desktopFilePath = Path.Combine(autostartDir, "zprint.desktop");

                    string content = $"[Desktop Entry]\nType=Application\nName=ZPrint Server\nExec=\"{exePath}\" server --background {extraServerArgs}\nHidden=false\nNoDisplay=false\nX-GNOME-Autostart-enabled=true\n";
                    File.WriteAllText(desktopFilePath, content);
                    return (true, $"Linux desktop autostart entry written to: {desktopFilePath}");
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to create autostart desktop entry: {ex.Message}");
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                try
                {
                    string launchAgentsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
                    Directory.CreateDirectory(launchAgentsDir);
                    string plistPath = Path.Combine(launchAgentsDir, "com.zerouniverse.zprint.plist");

                    string plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>com.zerouniverse.zprint</string>
    <key>ProgramArguments</key>
    <array>
        <string>{exePath}</string>
        <string>server</string>
        <string>--background</string>
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
</dict>
</plist>";
                    File.WriteAllText(plistPath, plist);
                    return (true, $"macOS LaunchAgent created at: {plistPath}");
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to create LaunchAgent: {ex.Message}");
                }
            }

            return (false, "Autostart is not supported on this operating system.");
        }

        /// <summary>
        /// Disables automatic startup on login.
        /// </summary>
        public static (bool Success, string Message) DisableAutostart()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string args = $"delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v \"{AppName}\" /f";
                var (code, output, err) = RunProcess("reg.exe", args);
                if (code == 0)
                {
                    return (true, "User autostart disabled successfully.");
                }
                return (false, $"Failed to remove registry key: {err} {output}");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string desktopFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "zprint.desktop");
                if (File.Exists(desktopFilePath))
                {
                    File.Delete(desktopFilePath);
                    return (true, "Linux desktop autostart entry removed.");
                }
                return (true, "Linux autostart was not configured.");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string plistPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.zerouniverse.zprint.plist");
                if (File.Exists(plistPath))
                {
                    File.Delete(plistPath);
                    return (true, "macOS LaunchAgent removed.");
                }
                return (true, "macOS autostart was not configured.");
            }

            return (false, "Not supported on this platform.");
        }

        /// <summary>
        /// Checks the current status of user login autostart.
        /// </summary>
        public static (bool Enabled, string Details) GetAutostartStatus()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var (code, output, _) = RunProcess("reg.exe", $"query \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v \"{AppName}\"");
                if (code == 0 && output.Contains(AppName))
                {
                    return (true, output.Trim());
                }
                return (false, "Disabled (Registry key not set)");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "zprint.desktop");
                if (File.Exists(path))
                {
                    return (true, $"Enabled (File: {path})");
                }
                return (false, "Disabled (desktop file not found)");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.zerouniverse.zprint.plist");
                if (File.Exists(path))
                {
                    return (true, $"Enabled (File: {path})");
                }
                return (false, "Disabled (plist not found)");
            }

            return (false, "Unsupported platform");
        }

        #endregion

        #region System Boot Service (Starts at System Boot, Run as SYSTEM)

        /// <summary>
        /// Installs ZPrint as a system background service running at system boot (even before user login).
        /// Uses Windows Task Scheduler on Windows or systemd on Linux.
        /// </summary>
        public static (bool Success, string Message) InstallSystemService(string extraServerArgs = "")
        {
            string exePath = GetExecutablePath();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string command = $"\\\"{exePath}\\\" server --background {extraServerArgs}".Trim();
                // Create scheduled task running under SYSTEM at system startup (/sc onstart) with HIGHEST privileges
                string args = $"/create /tn \"{TaskName}\" /tr \"{command}\" /sc onstart /ru SYSTEM /rl HIGHEST /f";
                var (code, output, err) = RunProcess("schtasks.exe", args);
                if (code == 0)
                {
                    return (true, $"System boot task '{TaskName}' created successfully.\nCommand: {command}\nStarts automatically when Windows boots up.");
                }
                return (false, $"Failed to create Windows system task (Run as Administrator required):\n{err} {output}");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    string unitContent = $@"[Unit]
Description=ZPrint Universal LAN Print Bridge
After=network.target

[Service]
Type=simple
ExecStart=""{exePath}"" server --background {extraServerArgs}
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
";
                    string unitPath = "/etc/systemd/system/zprint.service";
                    File.WriteAllText(unitPath, unitContent);
                    RunProcess("systemctl", "daemon-reload");
                    RunProcess("systemctl", "enable zprint");
                    return (true, $"Systemd service installed to {unitPath} and enabled.\nRun: sudo systemctl start zprint");
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to install systemd service (sudo required): {ex.Message}");
                }
            }

            return (false, "System service installation not supported on this platform.");
        }

        /// <summary>
        /// Uninstalls the system boot service.
        /// </summary>
        public static (bool Success, string Message) UninstallSystemService()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var (code, output, err) = RunProcess("schtasks.exe", $"/delete /tn \"{TaskName}\" /f");
                if (code == 0)
                {
                    return (true, $"System boot task '{TaskName}' deleted successfully.");
                }
                return (false, $"Failed to delete task: {err} {output}");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                RunProcess("systemctl", "stop zprint");
                RunProcess("systemctl", "disable zprint");
                string unitPath = "/etc/systemd/system/zprint.service";
                if (File.Exists(unitPath))
                {
                    try { File.Delete(unitPath); } catch { }
                    RunProcess("systemctl", "daemon-reload");
                }
                return (true, "Systemd service removed.");
            }

            return (false, "Not supported on this platform.");
        }

        /// <summary>
        /// Starts the system background service immediately.
        /// </summary>
        public static (bool Success, string Message) StartSystemService()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var (code, output, err) = RunProcess("schtasks.exe", $"/run /tn \"{TaskName}\"");
                if (code == 0)
                {
                    return (true, $"System service '{TaskName}' started.");
                }
                return (false, $"Failed to start service: {err} {output}");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var (code, output, err) = RunProcess("systemctl", "start zprint");
                return (code == 0, code == 0 ? "ZPrint systemd service started." : $"{err} {output}");
            }

            return (false, "Not supported on this platform.");
        }

        /// <summary>
        /// Stops any currently running ZPrint background processes.
        /// </summary>
        public static (bool Success, string Message) StopSystemService()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                RunProcess("schtasks.exe", $"/end /tn \"{TaskName}\"");
                var (code, output, _) = RunProcess("taskkill.exe", "/f /im zprint.exe");
                return (true, $"Stopped ZPrint processes. Output: {output.Trim()}");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var (code, output, err) = RunProcess("systemctl", "stop zprint");
                return (code == 0, code == 0 ? "ZPrint systemd service stopped." : $"{err} {output}");
            }

            return (false, "Not supported on this platform.");
        }

        /// <summary>
        /// Queries the real-time status of the system service.
        /// </summary>
        public static (bool Installed, string Details) GetSystemServiceStatus()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var (code, output, _) = RunProcess("schtasks.exe", $"/query /tn \"{TaskName}\" /fo LIST");
                if (code == 0)
                {
                    return (true, output.Trim());
                }
                return (false, "Not installed (System task not found)");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var (code, output, _) = RunProcess("systemctl", "status zprint");
                return (code == 0, output.Trim());
            }

            return (false, "Unsupported platform");
        }

        #endregion

        #region Process Runner Helper

        private static string GetExecutablePath()
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                return processPath;
            }

            return Process.GetCurrentProcess().MainModule?.FileName ?? "zprint.exe";
        }

        private static (int ExitCode, string Output, string Error) RunProcess(string fileName, string arguments)
        {
            try
            {
                using var p = new Process();
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(5000);

                return (p.ExitCode, output, err);
            }
            catch (Exception ex)
            {
                return (-1, string.Empty, ex.Message);
            }
        }

        #endregion
    }
}
