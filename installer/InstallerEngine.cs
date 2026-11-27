using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AwesomeVr_setup
{
    [DataContract]
    internal sealed class FileSpec
    {
        [DataMember] internal string Relative { get; set; }
        [DataMember] internal bool Mutable { get; set; }
    }

    [DataContract]
    internal sealed class PackageIndex
    {
        [DataMember] internal int Version { get; set; }
        [DataMember] internal string Product { get; set; }
        [DataMember] internal List<FileSpec> Files { get; set; }
        [DataMember] internal List<string> BaseGameFiles { get; set; }
    }

    internal sealed class Payload
    {
        internal readonly PackageIndex Index;
        internal readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        internal readonly byte[] SharedLoader;

        internal Payload(Stream resource)
        {
            using (ZipArchive zip = new ZipArchive(resource, ZipArchiveMode.Read))
            {
                ZipArchiveEntry index = zip.GetEntry("manifest.json");
                if (index == null || index.Length > 1048576) throw new InvalidDataException("The package catalog is missing or invalid.");
                using (Stream stream = index.Open())
                    Index = (PackageIndex)new DataContractJsonSerializer(typeof(PackageIndex)).ReadObject(stream);
                if (Index == null || Index.Version != 1 || Index.Product != "awesome_vr" ||
                    Index.Files == null || Index.BaseGameFiles == null || Index.BaseGameFiles.Count == 0 ||
                    Index.BaseGameFiles.Any(path => !Installer.SafeRelative(path))) throw new InvalidDataException("Invalid package catalog.");
                foreach (FileSpec spec in Index.Files)
                {
                    if (spec == null || !Installer.SafeRelative(spec.Relative) || !Installer.IsPayloadPath(spec.Relative) ||
                        Files.ContainsKey(spec.Relative)) throw new InvalidDataException("Invalid package file entry.");
                    ZipArchiveEntry entry = zip.GetEntry("payload/" + spec.Relative.Replace('\\', '/'));
                    if (entry == null || entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("A package file is missing or too large.");
                    using (Stream input = entry.Open())
                    using (MemoryStream output = new MemoryStream())
                    {
                        input.CopyTo(output);
                        byte[] bytes = output.ToArray();
                        Files.Add(spec.Relative, bytes);
                    }
                }
                if (!Files.ContainsKey("version.dll") || !Files.ContainsKey("BepInEx/plugins/awesome_vr/awesome_vr.dll"))
                    throw new InvalidDataException("The installer package is incomplete.");
                ZipArchiveEntry shared = zip.GetEntry("support/BepInEx-version.dll");
                if (shared == null || shared.Length > 1048576)
                    throw new InvalidDataException("The shared loader is missing.");
                using (Stream input = shared.Open())
                using (MemoryStream output = new MemoryStream())
                {
                    input.CopyTo(output);
                    SharedLoader = output.ToArray();
                }
            }
        }
    }

    internal sealed class Installer
    {
        private sealed class SetupLock : IDisposable
        {
            private readonly Mutex mutex;
            internal SetupLock(string name)
            {
                mutex = new Mutex(false, name);
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { mutex.Dispose(); throw new IOException("Another setup is already running for this game."); }
            }
            public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
        private readonly string root;
        private readonly Payload payload;
        private readonly Action<string> report;

        internal Installer(string directory, Payload package, Action<string> progress)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Select the game folder.");
            root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            payload = package;
            report = progress ?? delegate { };
        }

        internal static string Hash(byte[] bytes)
        { using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", ""); }
        internal static bool SafeRelative(string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0) return false;
            string[] parts = relative.Replace('\\', '/').Split('/');
            return parts.All(p => p.Length > 0 && p != "." && p != ".." && p.TrimEnd(' ', '.') == p &&
                p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        }

        internal static bool IsPayloadPath(string relative)
        {
            string p = relative.Replace('\\', '/');
            return p.Equals("version.dll", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase) ||
                p.Equals(".doorstop_version", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("Awesome_Data/Plugins/awesome_vr_native.dll", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("BepInEx/config/awesome_vr.cfg", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("BepInEx/config/BepInEx.cfg", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("BepInEx/plugins/awesome_vr/awesome_vr.dll", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("awesome_vr/input/", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("awesome_vr/runtime/", StringComparison.OrdinalIgnoreCase);
        }

        private string PathFor(string relative)
        {
            if (!SafeRelative(relative)) throw new InvalidDataException("Unsafe installation path.");
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An installation path leaves the game folder.");
            string current = root;
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Select the real game folder instead of a directory link.");
            foreach (string part in relative.Replace('\\', '/').Split('/'))
            {
                current = Path.Combine(current, part);
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Installation paths must not contain links: " + relative);
            }
            return full;
        }

        private void CheckTarget()
        {
            if (!Directory.Exists(root) || !File.Exists(PathFor("Awesome.exe")) ||
                !File.Exists(PathFor("Awesome_Data/Managed/Assembly-CSharp.dll")))
                throw new InvalidOperationException("Choose the folder containing Awesome.exe and Awesome_Data.");
            if (Process.GetProcessesByName("Awesome").Length != 0)
                throw new InvalidOperationException("Close the game before installing or uninstalling.");
        }

        private bool OtherMods(params string[] directories)
        {
            string ownPlugin = PathFor("BepInEx/plugins/awesome_vr/awesome_vr.dll");
            foreach (string relative in directories)
            {
                string directory = PathFor(relative);
                if (!Directory.Exists(directory)) continue;
                Stack<string> pending = new Stack<string>(); pending.Push(directory);
                while (pending.Count > 0)
                {
                    string dir = pending.Pop();
                    foreach (string entry in Directory.GetFileSystemEntries(dir))
                    {
                        FileAttributes attr = File.GetAttributes(entry);
                        if ((attr & FileAttributes.ReparsePoint) != 0) throw new IOException("A mod directory contains a link: " + entry);
                        if ((attr & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                        string extension = Path.GetExtension(entry);
                        if (extension.Equals(".asi", StringComparison.OrdinalIgnoreCase) ||
                            (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                             !entry.Equals(ownPlugin, StringComparison.OrdinalIgnoreCase))) return true;
                    }
                }
            }
            return false;
        }

        private void CheckUnexpectedFiles()
        {
            var expected = new HashSet<string>(payload.Index.BaseGameFiles, StringComparer.OrdinalIgnoreCase)
            {
                "backup/.proton-override",
                "BepInEx/LogOutput.log",
                "BepInEx/cache/chainloader_typeloader.dat",
                "BepInEx/cache/harmony_interop_cache.dat",
                "BepInEx/config/awesome_vr.cfg.tmp"
            };
            foreach (FileSpec file in payload.Index.Files)
            {
                expected.Add(file.Relative);
                expected.Add("backup/" + file.Relative);
                expected.Add("backup/.absent/" + file.Relative);
            }
            if (!File.Exists(PathFor("backup/.absent/version.dll")) && !File.Exists(PathFor("backup/version.dll")))
                expected.Remove("version.dll");

            var unexpected = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    string relative = entry.Substring(root.Length + 1).Replace('\\', '/');
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) unexpected.Add(relative);
                    else if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else if (!expected.Contains(relative) && !GeneratedLog.IsMatch(relative)) unexpected.Add(relative);
                }
            }
            if (unexpected.Count == 0) return;
            unexpected.Sort(StringComparer.OrdinalIgnoreCase);
            string details = string.Join(Environment.NewLine, unexpected.Take(12));
            if (unexpected.Count > 12) details += Environment.NewLine + "And " + (unexpected.Count - 12) + " more.";
            throw new InvalidOperationException("Setup found unexpected files in the game folder. Uninstall other mods before installing V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!!. Move any remaining unexpected files out of the game folder and retry." +
                Environment.NewLine + Environment.NewLine + details);
        }

        private static readonly Regex GeneratedLog = new Regex(
            @"^(awesome_vr/logs/awesome_vr_log_[0-9]{8}_[0-9]{6}_([0-9]{7}|[0-9]{3}_[0-9]+)\.log|BepInEx/LogOutput\.log\.[0-9]+|preloader_[0-9]{8}_[0-9]{6}_[0-9]{3}\.log)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal void Install()
        {
            CheckTarget();
            using (SetupLock setupLock = new SetupLock("Local\\awesome_vr-" + Hash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))))
            {
                CheckUnexpectedFiles();
                string backupRoot = PathFor("backup");
                Directory.CreateDirectory(backupRoot);
                foreach (FileSpec spec in payload.Index.Files.OrderBy(x => x.Relative.Equals("version.dll", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
                {
                    string destination = PathFor(spec.Relative);
                    if (spec.Mutable && File.Exists(destination)) continue;
                    string backup = PathFor("backup/" + spec.Relative);
                    string absent = PathFor("backup/.absent/" + spec.Relative);
                    if (!File.Exists(backup) && !File.Exists(absent))
                    {
                        if (File.Exists(destination))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(backup));
                            File.Copy(destination, backup);
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(absent));
                            File.WriteAllText(absent, "");
                        }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.WriteAllBytes(destination, payload.Files[spec.Relative]);
                }
                report("Installed V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!!");
            }
        }

        internal void Uninstall()
        {
            CheckTarget();
            using (SetupLock setupLock = new SetupLock("Local\\awesome_vr-" + Hash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))))
            {
                bool shared = OtherMods("BepInEx/plugins", "BepInEx/patchers") || File.Exists(PathFor("winhttp.dll"));
                foreach (FileSpec spec in payload.Index.Files)
                    RestoreFile(spec.Relative, spec.Mutable, shared);
                report("Restored to base game");
            }
        }
        private void RestoreFile(string relative, bool mutable, bool shared)
        {
            string path = PathFor(relative);
            string backup = PathFor("backup/" + relative);
            string absent = PathFor("backup/.absent/" + relative);
            if (File.Exists(backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.Copy(backup, path, true);
                File.Delete(backup);
            }
            else if (File.Exists(absent) && !mutable)
            {
                if (relative.Equals("version.dll", StringComparison.OrdinalIgnoreCase) && shared && File.Exists(path))
                    File.WriteAllBytes(path, payload.SharedLoader);
                else if (File.Exists(path)) File.Delete(path);
            }
            if (File.Exists(absent)) File.Delete(absent);
        }
    }
}
