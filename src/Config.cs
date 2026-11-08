using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace VolumeControl
{
    internal enum RuleMode { UseDefault, Mute, Full, Custom }

    [DataContract]
    internal sealed class AppRule
    {
        [DataMember(Name = "exe", Order = 1)] public string Exe = "";
        /// <summary>Optional text the process command line must contain (P6).</summary>
        [DataMember(Name = "commandLineContains", Order = 2)] public string CommandLineContains = "";
        [DataMember(Name = "mode", Order = 3)] public string ModeText = nameof(RuleMode.UseDefault);
        [DataMember(Name = "percent", Order = 4)] public int Percent = 100;

        public RuleMode Mode
        {
            get => Enum.TryParse(ModeText, true, out RuleMode m) ? m : RuleMode.UseDefault;
            set => ModeText = value.ToString();
        }

        public string Key => MakeKey(Exe, CommandLineContains);
        public static string MakeKey(string exe, string match) => (exe + "|" + (match ?? "")).ToLowerInvariant();
    }

    [DataContract]
    internal sealed class Settings
    {
        [DataMember(Name = "startWithWindows", Order = 1)] public bool StartWithWindows = true;
        [DataMember(Name = "notifyOnAutoMute", Order = 2)] public bool NotifyOnAutoMute = true;
        [DataMember(Name = "defaultMode", Order = 3)] public string DefaultModeText = nameof(RuleMode.Mute);
        [DataMember(Name = "defaultPercent", Order = 4)] public int DefaultPercent = 50;
        [DataMember(Name = "rules", Order = 5)] public List<AppRule> Rules = new List<AppRule>();
        /// <summary>Every exe the tool has managed, so uninstall knows what to restore (I5).</summary>
        [DataMember(Name = "managedApps", Order = 6)] public List<string> ManagedApps = new List<string>();

        public RuleMode DefaultMode
        {
            get => Enum.TryParse(DefaultModeText, true, out RuleMode m) && m != RuleMode.UseDefault ? m : RuleMode.Mute;
            set => DefaultModeText = value.ToString();
        }
    }

    /// <summary>Thread-safe settings store backed by one JSON file. All access takes <see cref="Sync"/>.</summary>
    internal sealed class ConfigStore
    {
        public const string SystemSoundsExe = "[System sounds]";

        public readonly object Sync = new object();
        public Settings Data { get; private set; }
        public string FilePath { get; }

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VolumeControl", "settings.json");

        public ConfigStore(string path)
        {
            FilePath = path ?? DefaultPath;
            Data = Load();
        }

        private Settings Load()
        {
            if (!File.Exists(FilePath)) return new Settings();
            try
            {
                using (var fs = File.OpenRead(FilePath))
                {
                    var loaded = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(fs);
                    if (loaded == null) throw new InvalidDataException("empty");
                    if (loaded.Rules == null) loaded.Rules = new List<AppRule>();
                    if (loaded.ManagedApps == null) loaded.ManagedApps = new List<string>();
                    loaded.Rules.RemoveAll(r => r == null || string.IsNullOrWhiteSpace(r.Exe));
                    foreach (var r in loaded.Rules)
                    {
                        if (r.CommandLineContains == null) r.CommandLineContains = "";
                        r.Percent = Clamp(r.Percent);
                    }
                    loaded.DefaultPercent = Clamp(loaded.DefaultPercent);
                    return loaded;
                }
            }
            catch (Exception)
            {
                // T1: keep the bad file for inspection and start clean.
                try { File.Copy(FilePath, FilePath + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak", true); }
                catch (Exception) { }
                return new Settings();
            }
        }

        public static int Clamp(int percent) => Math.Max(0, Math.Min(100, percent));

        public void Save()
        {
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    string temp = FilePath + ".tmp";
                    using (var fs = File.Create(temp))
                    using (var writer = JsonReaderWriterFactory.CreateJsonWriter(fs, Encoding.UTF8, false, true, "  "))
                    {
                        new DataContractJsonSerializer(typeof(Settings)).WriteObject(writer, Data);
                        writer.Flush();
                    }
                    if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                    else File.Move(temp, FilePath);
                }
                catch (IOException) { /* next change will retry */ }
                catch (UnauthorizedAccessException) { }
                catch (XmlException) { }
            }
        }

        /// <summary>A rule whose command-line text matches wins over the plain rule for that exe.</summary>
        public AppRule FindRule(string exe, string commandLine)
        {
            lock (Sync)
            {
                AppRule plain = null;
                foreach (var r in Data.Rules)
                {
                    if (!string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(r.CommandLineContains)) plain = r;
                    else if (!string.IsNullOrEmpty(commandLine) &&
                             commandLine.IndexOf(r.CommandLineContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        return r;
                }
                return plain;
            }
        }

        public AppRule GetByKey(string key)
        {
            lock (Sync) return Data.Rules.FirstOrDefault(r => r.Key == key);
        }

        public AppRule Upsert(string exe, string match, RuleMode mode, int percent)
        {
            lock (Sync)
            {
                string key = AppRule.MakeKey(exe, match);
                var rule = Data.Rules.FirstOrDefault(r => r.Key == key);
                if (rule == null)
                {
                    rule = new AppRule { Exe = exe, CommandLineContains = match ?? "" };
                    Data.Rules.Add(rule);
                }
                rule.Mode = mode;
                rule.Percent = Clamp(percent);
                Save();
                return rule;
            }
        }

        public void Remove(string key)
        {
            lock (Sync)
            {
                if (Data.Rules.RemoveAll(r => r.Key == key) > 0) Save();
            }
        }

        public void MarkManaged(string exe)
        {
            lock (Sync)
            {
                if (Data.ManagedApps.Any(e => string.Equals(e, exe, StringComparison.OrdinalIgnoreCase))) return;
                Data.ManagedApps.Add(exe);
                Save();
            }
        }
    }
}
