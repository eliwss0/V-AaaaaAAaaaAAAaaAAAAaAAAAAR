using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AwesomeVr_setup
{
    internal static class GameDiscovery
    {
        internal static string[] Find(IEnumerable<string> roots)
        {
            HashSet<string> games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
                if (!String.IsNullOrWhiteSpace(root)) libraries.Add(root);
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    string root = drive.RootDirectory.FullName;
                    foreach (string path in new[] { root, Path.Combine(root, "SteamLibrary"), Path.Combine(root, "Steam") })
                        if (Directory.Exists(Path.Combine(path, "steamapps"))) libraries.Add(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (string root in libraries.ToArray())
            {
                string config = Path.Combine(root, "steamapps/libraryfolders.vdf");
                try
                {
                    if (!File.Exists(config)) continue;
                    foreach (Match match in Regex.Matches(File.ReadAllText(config), "\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase))
                        libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\""));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (string root in roots)
                AddGame(games, root);
            foreach (string library in libraries)
            {
                List<string> folders = new List<string> { "Awesome" };
                try
                {
                    string manifest = Path.Combine(library, "steamapps/appmanifest_15560.acf");
                    if (File.Exists(manifest))
                    {
                        Match match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                        string name = match.Groups[1].Value;
                        if (match.Success && Installer.SafeRelative(name) && name.IndexOfAny(new[] { '/', '\\' }) < 0)
                            folders.Insert(0, name);
                    }
                    foreach (string folder in folders)
                    {
                        string game = Path.GetFullPath(Path.Combine(library, "steamapps/common", folder));
                        AddGame(games, game);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return games.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static void AddGame(HashSet<string> games, string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            try
            {
                string game = Path.GetFullPath(path);
                if (File.Exists(Path.Combine(game, "Awesome.exe")) &&
                    File.Exists(Path.Combine(game, "Awesome_Data/Managed/Assembly-CSharp.dll"))) games.Add(game);
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }
    }
}
