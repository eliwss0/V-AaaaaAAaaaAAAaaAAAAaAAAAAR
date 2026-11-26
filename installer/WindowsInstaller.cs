using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AwesomeVr_setup
{
    internal static class WindowsInstaller
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Payload payload;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("awesome_vr.payload.zip"))
                {
                    if (stream == null) throw new InvalidDataException("The installer payload is missing.");
                    payload = new Payload(stream);
                }
                if (args.Length == 1 && args[0] == "--verify-package") return 0;
                if (args.Length == 3 && args[0] == "--worker")
                {
                    string result = Run(payload, args[1], args[2]);
                    MessageBox.Show(result, "V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!! setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                if (args.Length != 0) throw new ArgumentException("Start the installer without arguments.");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SetupWindow(payload));
                return 0;
            }
            catch (Exception error)
            {
                if (args.Length == 1 && args[0] == "--verify-package")
                { Console.Error.WriteLine(error.Message); return 1; }
                MessageBox.Show(error.Message, "V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!! setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        internal static string Run(Payload payload, string action, string folder)
        {
            System.Text.StringBuilder output = new System.Text.StringBuilder();
            Installer installer = new Installer(folder, payload, line => output.AppendLine(line));
            if (action == "install") installer.Install();
            else if (action == "uninstall") installer.Uninstall();
            else throw new ArgumentException("Unknown setup action.");
            return output.ToString().Trim();
        }

        internal static string Quote(string value)
        {
            System.Text.StringBuilder result = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { ++slashes; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c); slashes = 0;
            }
            result.Append('\\', slashes * 2); result.Append('"');
            return result.ToString();
        }
    }

    internal sealed class SetupWindow : Form
    {
        private readonly Payload payload;
        private readonly ComboBox folder;

        internal SetupWindow(Payload package)
        {
            payload = package;
            Text = "V-AaaaaAAaaaAAAaaAAAAaAAAAAR!!! setup";
            Width = 650; Height = 230; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            string[] games = FindGames();
            Label label = new Label { Left = 18, Top = 18, Width = 590, Height = 40,
                Text = games.Length == 1 ? "Game found automatically. Close the game before continuing." :
                    games.Length > 1 ? "Choose one of the detected installations. Close the game before continuing." :
                    "Select your Steam or standalone game folder containing Awesome.exe." };
            folder = new ComboBox { Left = 20, Top = 62, Width = 495, DropDownStyle = ComboBoxStyle.DropDown };
            folder.Items.AddRange(games);
            if (games.Length == 1) folder.SelectedIndex = 0;
            Button browse = new Button { Left = 522, Top = 60, Width = 95, Text = "Browse..." };
            browse.Click += delegate {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Select the Aaaaa! for the Awesome game folder";
                    if (Directory.Exists(folder.Text)) dialog.SelectedPath = folder.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
                }
            };
            Button install = new Button { Left = 20, Top = 112, Width = 190, Height = 34, Text = "Install" };
            Button uninstall = new Button { Left = 220, Top = 112, Width = 160, Height = 34, Text = "Uninstall" };
            Button close = new Button { Left = 522, Top = 112, Width = 95, Height = 34, Text = "Close" };
            install.Click += delegate { Execute("install"); };
            uninstall.Click += delegate { Execute("uninstall"); };
            close.Click += delegate { Close(); };
            Controls.AddRange(new Control[] { label, folder, browse, install, uninstall, close });
            AcceptButton = install; CancelButton = close;
        }

        private void Execute(string action)
        {
            try
            {
                string result = WindowsInstaller.Run(payload, action, folder.Text);
                MessageBox.Show(this, result, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (UnauthorizedAccessException)
            {
                try
                {
                    ProcessStartInfo info = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location);
                    info.UseShellExecute = true; info.Verb = "runas";
                    info.Arguments = "--worker " + action + " " + WindowsInstaller.Quote(Path.GetFullPath(folder.Text));
                    Process.Start(info);
                }
                catch (Win32Exception error) { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
            catch (Exception error)
            { MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private static string[] FindGames()
        {
            var roots = new System.Collections.Generic.List<string>();
            roots.Add(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (string uninstall in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                using (RegistryKey entries = hive.OpenSubKey(uninstall))
                {
                    if (entries == null) continue;
                    foreach (string name in entries.GetSubKeyNames())
                    using (RegistryKey entry = entries.OpenSubKey(name))
                    {
                        if (entry == null) continue;
                        string title = entry.GetValue("DisplayName") as string;
                        if (!String.IsNullOrEmpty(title) && title.IndexOf("for the Awesome", StringComparison.OrdinalIgnoreCase) >= 0)
                            roots.Add(entry.GetValue("InstallLocation") as string);
                    }
                }
            }
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (key != null) roots.Add(key.GetValue("SteamPath") as string);
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                if (key != null) roots.Add(key.GetValue("InstallPath") as string);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            return GameDiscovery.Find(roots);
        }
    }
}
