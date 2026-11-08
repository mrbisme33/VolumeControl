using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VolumeControl
{
    /// <summary>Per-user install. The exe is its own installer and uninstaller; no admin rights needed.</summary>
    internal static class Installer
    {
        private const string AppName = "VolumeControl";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\VolumeControl";

        public static string InstallDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");
        private static string ShortcutPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

        public static bool IsInstalledCopy => string.Equals(
            Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        public static void SyncStartup(bool enabled)
        {
            if (!IsInstalledCopy) return; // A build-folder or portable run must not claim the login entry.
            WriteStartup(enabled);
        }

        private static void WriteStartup(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(AppName, "\"" + InstalledExe + "\"");
                else key.DeleteValue(AppName, false);
            }
        }

        public static void RunInstall(ConfigStore store, bool assumeYes, bool launch)
        {
            if (!assumeYes)
            {
                var answer = MessageBox.Show(
                    "Install VolumeControl for this Windows account?\n\n" +
                    "It runs in the system tray and starts with Windows. By default, every app that plays " +
                    "sound is muted until you allow it. You can change that default in Settings.",
                    "VolumeControl Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (answer != DialogResult.OK) return;
            }

            try
            {
                StopRunningCopy();
                Directory.CreateDirectory(InstallDir);
                CopyWithRetry(Application.ExecutablePath, InstalledExe);
                CreateShortcut();
                WriteUninstallEntry();
                WriteStartup(store.Data.StartWithWindows);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (!assumeYes)
                    MessageBox.Show("Setup failed: " + ex.Message, "VolumeControl Setup",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
                return;
            }

            if (launch) Process.Start(InstalledExe);
        }

        /// <param name="deleteSettings">Null asks the user (I4).</param>
        public static void RunUninstall(ConfigStore store, bool assumeYes, bool? deleteSettings)
        {
            if (!assumeYes)
            {
                var ok = MessageBox.Show("Remove VolumeControl and restore the apps it changed?",
                    "Uninstall VolumeControl", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (ok != DialogResult.OK) return;
            }

            StopRunningCopy();

            // I5: put back anything still known to Windows that this tool has ever managed.
            var engine = new AudioEngine(store, startPaused: true);
            engine.Start();
            engine.Flush();
            engine.RestoreApps(store.Data.ManagedApps.ToList());
            engine.Stop();

            bool delete;
            if (deleteSettings.HasValue) delete = deleteSettings.Value;
            else if (assumeYes) delete = false;
            else delete = MessageBox.Show("Also delete your saved VolumeControl settings?\n\nChoose No to keep them for a future install.",
                "Uninstall VolumeControl", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;

            if (delete)
            {
                try
                {
                    string dir = Path.GetDirectoryName(store.FilePath);
                    if (File.Exists(store.FilePath)) File.Delete(store.FilePath);
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir)
                            .Any(f => !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)))
                        Directory.Delete(dir, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }

            WriteStartup(false);
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            ScheduleFolderRemoval();
        }

        private static void StopRunningCopy()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(Program.ExitEventName, out var exit))
                    using (exit) exit.Set();
            }
            catch (Exception) { }

            for (int i = 0; i < 50; i++)
            {
                if (!Mutex.TryOpenExisting(Program.MutexName, out var mutex)) return;
                mutex.Dispose();
                Thread.Sleep(100);
            }
        }

        private static void CopyWithRetry(string from, string to)
        {
            if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase)) return;
            for (int attempt = 0; ; attempt++)
            {
                try { File.Copy(from, to, true); return; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(250); }
            }
        }

        private static void CreateShortcut()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath));
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.Description = "Set default volume per app";
            link.Save();
        }

        private static void WriteUninstallEntry()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", version.ToString(3));
                key.SetValue("Publisher", "mrbisme33");
                key.SetValue("DisplayIcon", InstalledExe);
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            }
        }

        private static void ScheduleFolderRemoval()
        {
            // The running exe cannot delete itself, so a detached shell does it after this process exits.
            if (!IsInstalledCopy) return;
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
        }
    }
}
