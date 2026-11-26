using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace AwesomeVr
{
    [BepInPlugin("awesome_vr", "V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!!", AwesomeVRPlugin.ModVersion)]
    public sealed class AwesomeVRPlugin : BaseUnityPlugin
    {
        public const string ModVersion = "0.1";
        private ConfigEntry<bool> safeMode, loggingEnabled;
        private DateTime lastConfigWrite;
        private float nextPoll;
        private bool effectiveSafeMode;
        private bool failed;
        private string gameRoot;
        private string logPath;
        private bool logInitialized;
        private OpenVRRenderer vr;
        private bool requestedVR, vrAttempted;

        private void Awake()
        {
            try
            {
                gameRoot = Directory.GetParent(Application.dataPath).FullName;
                requestedVR = Environment.GetEnvironmentVariable("AWESOME_VR_ENABLE") == "1";
                Config.SaveOnConfigSet = false;
                loggingEnabled = Config.Bind<bool>("General", "EnableLogging", false);
                safeMode = Config.Bind<bool>("General", "SafeMode", true,
                    "Keep VR disabled. When false, regular launches offer a game mode choice.");
                VRModSettings.Initialize(Config);
                if (!File.Exists(Config.ConfigFilePath)) VRConfig.Save(Config);
                lastConfigWrite = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
                RefreshState();
                Write("startup", "version=" + ModVersion + " unity=" + Application.unityVersion +
                    " pointerBytes=" + IntPtr.Size + " clr=" + Environment.Version +
                    " safeMode=" + effectiveSafeMode + " requestedVR=" + requestedVR);
                Snapshot();
            }
            catch (Exception exception)
            {
                failed = true;
                Write("error", exception.ToString());
            }
        }

        private void InitializeLog()
        {
            logInitialized = true;
            try
            {
                string logDirectory = Path.Combine(gameRoot, "awesome_vr/logs");
                Directory.CreateDirectory(logDirectory);
                string name = Environment.GetEnvironmentVariable("AWESOME_VR_LOG_FILE");
                if (!string.IsNullOrEmpty(name) && name == Path.GetFileName(name) &&
                    name.StartsWith("awesome_vr_log_", StringComparison.Ordinal) &&
                    name.EndsWith(".log", StringComparison.Ordinal))
                {
                    logPath = Path.Combine(logDirectory, name);
                    return;
                }
                string[] logs = Directory.GetFiles(logDirectory, "awesome_vr_log_*.log");
                Array.Sort(logs, StringComparer.Ordinal);
                for (int i = logs.Length - 1; i >= 2; --i) File.Delete(logs[i]);
                logPath = Path.Combine(logDirectory, "awesome_vr_log_" +
                    DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff") + ".log");
            }
            catch (IOException error) { Logger.LogWarning(error.Message); }
            catch (UnauthorizedAccessException error) { Logger.LogWarning(error.Message); }
        }

        private void Update()
        {
            if (failed) return;
            try
            {
                if (Time.realtimeSinceStartup < nextPoll) return;
                nextPoll = Time.realtimeSinceStartup + 1;
                try
                {
                    DateTime changed = File.GetLastWriteTimeUtc(Config.ConfigFilePath);
                    if (changed != lastConfigWrite)
                    {
                        Config.Reload();
                        lastConfigWrite = changed;
                        Write("config-reload", "Configuration reloaded");
                    }
                }
                catch (IOException) { return; }
                catch (UnauthorizedAccessException) { return; }
                RefreshState();
            }
            catch (Exception exception)
            {
                failed = true;
                Write("error", exception.ToString());
            }
        }

        private void RefreshState()
        {
            bool requested = safeMode.Value;
            if (requested != effectiveSafeMode)
                Write("safe-mode", "enabled=" + requested);
            effectiveSafeMode = requested;
            if (effectiveSafeMode && vr != null) { vr.End(); Destroy(vr); vr = null; }
            if (requestedVR && !effectiveSafeMode && !vrAttempted)
            {
                vrAttempted = true;
                vr = gameObject.AddComponent<OpenVRRenderer>();
                try { vr.Begin(gameRoot, delegate(string message) { Write("openvr", message); }); }
                catch (Exception exception)
                {
                    Write("openvr-error", exception.ToString());
                    vr.End(); Destroy(vr); vr = null;
                }
            }
        }

        private void Snapshot()
        {
            Write("snapshot", "scene=" + Application.loadedLevelName +
                " graphics=" + SystemInfo.graphicsDeviceVersion +
                " gpu=" + SystemInfo.graphicsDeviceName + " screen=" + Screen.width + "x" + Screen.height +
                " legacyVr=" + Options.isVRSupportEnabled + " legacySteamVr=" + Options.useSteamVRInsteadOfOculus +
                " safeMode=" + effectiveSafeMode);
        }

        private void Write(string kind, string message)
        {
            if (loggingEnabled == null || !loggingEnabled.Value) return;
            if (!logInitialized) InitializeLog();
            if (logPath == null) return;
            try
            {
                File.AppendAllText(logPath, DateTime.UtcNow.ToString("o") + " [" + kind + "] " +
                    message.Replace('\r', ' ').Replace('\n', ' ') + Environment.NewLine);
            }
            catch (IOException exception) { Logger.LogWarning("Diagnostic file unavailable: " + exception.Message); }
            catch (UnauthorizedAccessException exception) { Logger.LogWarning("Diagnostic file unavailable: " + exception.Message); }
        }

        private void OnApplicationQuit()
        {
            if (!failed) Write("shutdown", "Host application is quitting");
        }
    }
}
