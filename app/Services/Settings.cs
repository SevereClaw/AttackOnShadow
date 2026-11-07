using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace AttackOnShadow.Services
{
    public static class Paths
    {
        public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AttackOnShadow");
        public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AttackOnShadow");
        public static string SettingsFile => Path.Combine(Data, "settings.json");
        public static string LogFile => Path.Combine(Data, "log.txt");
        public static string UsbLogFile => Path.Combine(Data, "usb.txt");
        public static string Exe => System.Reflection.Assembly.GetExecutingAssembly().Location;
    }

    public static class Log
    {
        static readonly object sync = new object();
        public static bool UsbEnabled = true;

        static void Write(string file, string line)
        {
            try
            {
                lock (sync)
                {
                    Directory.CreateDirectory(Paths.Data);
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > 512 * 1024) File.Delete(file + ".old");
                    if (fi.Exists && fi.Length > 512 * 1024) File.Move(file, file + ".old");
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
        public static void Info(string s) => Write(Paths.LogFile, s);
        public static void Usb(string s) { if (UsbEnabled) Write(Paths.UsbLogFile, s); }
    }

    [DataContract]
    public sealed class GameRule
    {
        [DataMember(Order = 1)] public string Exe;
        [DataMember(Order = 2)] public int Layer;
    }

    [DataContract]
    public sealed class AppSettings
    {
        [DataMember] public bool AutoEnabled = true;
        [DataMember] public List<GameRule> Rules = new List<GameRule>();
        [DataMember] public int FallbackLayer = -1;       // -1: stay on whatever layer
        [DataMember] public bool CloseToTray = true;
        [DataMember] public bool Autostart = false;
        [DataMember] public bool CheckUpdates = true;
        [DataMember] public bool AutoInstallUpdates = true;
        [DataMember] public DateTime LastUpdateCheck;
        [DataMember] public bool UsbLog = true;
        [DataMember] public bool ImportedFromMod;

        [OnDeserializing] void Defaults(StreamingContext c)
        {
            AutoEnabled = true; Rules = new List<GameRule>(); FallbackLayer = -1; CloseToTray = true;
            CheckUpdates = true; AutoInstallUpdates = true; UsbLog = true;
        }

        public static AppSettings Load()
        {
            AppSettings s = null;
            try
            {
                if (File.Exists(Paths.SettingsFile))
                    using (var f = File.OpenRead(Paths.SettingsFile))
                        s = (AppSettings)new DataContractJsonSerializer(typeof(AppSettings)).ReadObject(f);
            }
            catch (Exception e) { Log.Info("settings unreadable: " + e.Message); }
            s = s ?? new AppSettings();
            if (s.Rules == null) s.Rules = new List<GameRule>();
            if (!s.ImportedFromMod) { s.ImportFromMod(); s.ImportedFromMod = true; s.Save(); }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.Data);
                string tmp = Paths.SettingsFile + ".tmp";
                using (var f = File.Create(tmp))
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(f, Encoding.UTF8, true, true, "  "))
                    new DataContractJsonSerializer(typeof(AppSettings)).WriteObject(w, this);
                if (File.Exists(Paths.SettingsFile)) File.Delete(Paths.SettingsFile);
                File.Move(tmp, Paths.SettingsFile);
            }
            catch (Exception e) { Log.Info("settings not saved: " + e.Message); }
        }

        /// <summary>Takes the game rules made in the old AttackOnShadow mod (driver panel).</summary>
        void ImportFromMod()
        {
            try
            {
                string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Attack Shark Driver", "aos-settings.json");
                if (!File.Exists(old)) return;
                string json = File.ReadAllText(old);
                foreach (Match m in Regex.Matches(json, "\"exe\"\\s*:\\s*\"([^\"]+)\"[^{}]*?\"profile\"\\s*:\\s*(\\d+)"))
                {
                    string exe = m.Groups[1].Value;
                    int layer = Math.Min(1, int.Parse(m.Groups[2].Value) / 4);
                    if (!Rules.Exists(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase)))
                        Rules.Add(new GameRule { Exe = exe, Layer = layer });
                }
                var fb = Regex.Match(json, "\"fallback\"\\s*:\\s*\\{[^{}]*?\"profile\"\\s*:\\s*(\\d+)");
                if (fb.Success) FallbackLayer = Math.Min(1, int.Parse(fb.Groups[1].Value) / 4);
                Log.Info($"imported {Rules.Count} rules from the mod");
            }
            catch (Exception e) { Log.Info("mod import failed: " + e.Message); }
        }
    }
}
