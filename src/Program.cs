using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace VolumeControl
{
    internal static class Program
    {
        public const string MutexName = @"Local\VolumeControl.Instance";
        public const string ShowEventName = @"Local\VolumeControl.Show";
        public const string ExitEventName = @"Local\VolumeControl.Exit";

        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);
            string Value(string name) => args
                .Where(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Substring(name.Length + 1)).FirstOrDefault();

            var store = new ConfigStore(Value("--config"));
            bool yes = Has("--yes");

            if (Has("--uninstall"))
            {
                bool? delete = Has("--delete-settings") ? true : Has("--keep-settings") ? false : (bool?)null;
                Installer.RunUninstall(store, yes, delete);
                return;
            }

            string setOutput = Value("--set-output");
            if (setOutput != null)
            {
                var engine = new AudioEngine(store, startPaused: true);
                engine.Start();
                engine.SetOutput(setOutput);
                engine.Flush();
                engine.Stop();
                return;
            }

            string listPath = Value("--list");
            if (listPath != null)
            {
                WriteSessionList(store, listPath);
                return;
            }

            // Running from anywhere but the install folder means "install me", unless --portable is given.
            if (Has("--install") || (!Installer.IsInstalledCopy && !Has("--portable")))
            {
                Installer.RunInstall(store, yes, launch: !Has("--no-launch"));
                return;
            }

            using (var mutex = new Mutex(true, MutexName, out bool first))
            {
                if (!first)
                {
                    if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
                        using (show) show.Set();
                    return;
                }

                Application.ThreadException += (s, e) => { /* The tray must outlive a failed UI action. */ };
                Installer.SyncStartup(store.Data.StartWithWindows);
                using (var app = new TrayApp(store, Value("--only-exe")))
                    Application.Run(app);
            }
        }

        private static void WriteSessionList(ConfigStore store, string path)
        {
            // Read-only: starts paused, so no session is changed.
            var engine = new AudioEngine(store, startPaused: true);
            engine.Start();
            engine.Flush();
            var sb = new StringBuilder();
            foreach (var s in engine.Sessions.OrderBy(x => x.Exe))
                sb.AppendLine(string.Join("\t", s.Exe, s.IsActive ? "active" : "inactive",
                    s.Muted ? "muted" : "unmuted", s.Percent + "%", s.CommandLine));
            foreach (var o in engine.Outputs)
                sb.AppendLine(string.Join("\t", "OUTPUT", o.IsDefault ? "default" : "-", o.Name, o.Id));
            File.WriteAllText(path, sb.ToString());
            engine.Stop();
        }
    }
}
