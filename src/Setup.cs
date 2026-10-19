using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PortJanitor Setup")]
[assembly: AssemblyProduct("PortJanitor")]
[assembly: AssemblyDescription("Installs PortJanitor for the current user.")]
[assembly: AssemblyCopyright("Copyright (c) PortJanitor contributors. MIT License.")]

namespace PortJanitor
{
    static class Setup
    {
        const string AppName = "PortJanitor";
        const string Title = "PortJanitor Setup";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PortJanitor";

        static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        static readonly string AppExe = Path.Combine(InstallDir, AppName + ".exe");
        static readonly string UninstallExe = Path.Combine(InstallDir, "Uninstall.exe");
        static readonly string[] Shortcuts =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk")
        };

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            bool quiet = HasArg(args, "/s", "/silent");
            bool uninstall = HasArg(args, "/uninstall") || SamePath(Path.GetDirectoryName(Application.ExecutablePath), InstallDir);
            try
            {
                return uninstall ? Uninstall(quiet) : Install(quiet);
            }
            catch (Exception ex)
            {
                if (!quiet) MessageBox.Show(ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static int Install(bool quiet)
        {
            string version = Application.ProductVersion;
            string installed = null;
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
                if (key != null) installed = key.GetValue("DisplayVersion") as string;

            string prompt = (installed == null ? "Install PortJanitor " + version + "?" : "PortJanitor " + installed + " is installed. Replace it with " + version + "?")
                + "\n\nIt installs for your account only, so no admin rights are needed:\n" + InstallDir
                + "\n\nShortcuts are added to your desktop and Start menu.";
            if (!quiet && MessageBox.Show(prompt, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
            if (!WaitUntilClosed(quiet)) return 1;

            Directory.CreateDirectory(InstallDir);
            using (var app = Assembly.GetExecutingAssembly().GetManifestResourceStream(AppName + ".exe"))
            using (var file = File.Create(AppExe))
                app.CopyTo(file);
            File.Copy(Application.ExecutablePath, UninstallExe, true);
            foreach (string link in Shortcuts) CreateShortcut(link, AppExe);

            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "Mohammed Saqib");
                key.SetValue("DisplayIcon", AppExe);
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", Quote(UninstallExe) + " /uninstall");
                key.SetValue("QuietUninstallString", Quote(UninstallExe) + " /uninstall /S");
                key.SetValue("URLInfoAbout", "https://github.com/md-saqib/port-janitor");
                key.SetValue("EstimatedSize", (int)((new FileInfo(AppExe).Length + new FileInfo(UninstallExe).Length) / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            if (!quiet) Process.Start(new ProcessStartInfo(AppExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
            return 0;
        }

        static int Uninstall(bool quiet)
        {
            if (!quiet && MessageBox.Show("Uninstall PortJanitor?\n\nThis removes " + InstallDir + " and the desktop and Start menu shortcuts.", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
            if (!WaitUntilClosed(quiet)) return 1;

            foreach (string link in Shortcuts) File.Delete(link);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (File.Exists(AppExe)) File.Delete(AppExe);
            if (!quiet) MessageBox.Show("PortJanitor was uninstalled.", Title, MessageBoxButtons.OK, MessageBoxIcon.Information);

            // Uninstall.exe can't delete itself, so cmd removes the folder after this process exits
            string dir = Quote(InstallDir);
            Process.Start(new ProcessStartInfo("cmd.exe", "/c for /l %i in (1,1,10) do @(ping -n 2 127.0.0.1 >nul & rd /s /q " + dir + " 2>nul & if not exist " + dir + " exit)")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetTempPath()
            });
            return 0;
        }

        static bool WaitUntilClosed(bool quiet)
        {
            while (IsLocked(AppExe))
            {
                if (quiet || MessageBox.Show("PortJanitor is running. Close it, then click Retry.", Title, MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry) return false;
            }
            return true;
        }

        static bool IsLocked(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false;
            }
            catch (IOException) { return true; }
        }

        static void CreateShortcut(string link, string target)
        {
            var shortcut = (IShellLinkW)new ShellLink();
            try
            {
                shortcut.SetPath(target);
                shortcut.SetWorkingDirectory(Path.GetDirectoryName(target));
                shortcut.SetDescription("Find the dev servers you forgot to stop, and kill them");
                shortcut.SetIconLocation(target, 0);
                ((IPersistFile)shortcut).Save(link, true);
            }
            finally { Marshal.ReleaseComObject(shortcut); }
        }

        static bool HasArg(string[] args, params string[] names)
        {
            return args.Any(a => names.Contains(a.ToLowerInvariant()));
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        static string Quote(string path)
        {
            return "\"" + path + "\"";
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, int flags);
            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relPath, int reserved);
            void Resolve(IntPtr hwnd, int flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }
    }
}
