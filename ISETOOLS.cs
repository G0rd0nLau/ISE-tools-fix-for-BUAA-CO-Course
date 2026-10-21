// ISETOOLS - launcher / repair tool / rollback for Xilinx ISE 14.7 on Windows 10/11
// Single-file WinForms app, C# 5 only (buildable with the .NET Framework 4.x csc.exe, no SDK needed).
//
// Modes:
//   (no args)     launch ISE using the recorded install path (this is what the shortcuts point at)
//   --setup       install wizard: pick ISE dir + license, apply the repairs, create shortcuts
//   --rollback    restore everything the setup changed
//   --selftest    run built-in logic checks, exit 0/1 (used by build.ps1)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ISETools
{
    internal static class Program
    {
        internal const string AppName = "ISETOOLS";
        internal const string AppVersion = "1.0.0";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                string mode = args[0].Trim().ToLowerInvariant();
                if (mode == "--selftest")
                {
                    return SelfTest.Run();
                }
                if (mode == "--setup")
                {
                    if (!Admin.IsElevated()) { return Admin.RelaunchElevated("--setup"); }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new SetupForm(true));
                    return 0;
                }
                if (mode == "--rollback")
                {
                    if (!Admin.IsElevated()) { return Admin.RelaunchElevated("--rollback"); }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new SetupForm(false));
                    return 0;
                }
            }

            // default: launch ISE; fall back to the wizard when nothing is configured yet
            if (Journal.Exists())
            {
                return Launch.Run();
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(true));
            return 0;
        }
    }

    internal static class Paths
    {
        internal static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ISETOOLS"); }
        }
        internal static string InstalledExe { get { return Path.Combine(InstallDir, "ISETOOLS.exe"); } }
        internal static string JournalFile { get { return Path.Combine(InstallDir, "journal.txt"); } }
        internal static string LogFile { get { return Path.Combine(InstallDir, "isetools.log"); } }
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        internal static Action<string> Sink;

        internal static void Write(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            if (Sink != null) { try { Sink(line); } catch { } }
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Paths.InstallDir);
                    File.AppendAllText(Paths.LogFile, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* not writable without admin - UI sink still has it */ }
            }
        }
    }

    internal static class Admin
    {
        internal static bool IsElevated()
        {
            try
            {
                return new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        internal static int RelaunchElevated(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Assembly.GetEntryAssembly().Location;
                psi.Arguments = arguments;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("需要管理员权限才能修改系统设置。\r\n\r\n" + ex.Message,
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 2;
            }
        }
    }

    // ---------------------------------------------------------------- journal

    internal sealed class Journal
    {
        private readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal const string Absent = "{{ABSENT}}";

        internal string Get(string key, string fallback)
        {
            string v;
            if (_map.TryGetValue(key, out v)) { return v; }
            return fallback;
        }

        internal void Set(string key, string value) { _map[key] = value; }

        internal static bool Exists() { return File.Exists(Paths.JournalFile); }

        internal static Journal Load()
        {
            Journal j = new Journal();
            if (!File.Exists(Paths.JournalFile)) { return j; }
            string[] lines = File.ReadAllLines(Paths.JournalFile, Encoding.UTF8);
            foreach (string raw in lines)
            {
                string line = raw;
                int eq = line.IndexOf('=');
                if (eq <= 0) { continue; }
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1);
                if (k.Length > 0) { j._map[k] = v; }
            }
            return j;
        }

        internal void Save()
        {
            Directory.CreateDirectory(Paths.InstallDir);
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in _map)
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            }
            File.WriteAllText(Paths.JournalFile, sb.ToString(), Encoding.UTF8);
        }

        internal bool IsApplied { get { return Get("applied", "0") == "1"; } }
    }

    // ---------------------------------------------------------------- repair logic

    internal static class Repair
    {
        internal const string OrderKeyPath = @"SYSTEM\CurrentControlSet\Control\NetworkProvider\Order";
        internal const string HwOrderKeyPath = @"SYSTEM\CurrentControlSet\Control\NetworkProvider\HwOrder";
        internal const string ValueName = "ProviderOrder";
        internal const string BadProvider = "P9NP";
        internal const string EnvVcCheck = "XILINX_VC_CHECK_NOOP";
        internal const string EnvLicense = "XILINXD_LICENSE_FILE";

        /// Remove one provider token from a comma separated provider order ("A,B,C").
        internal static string RemoveProvider(string order, string token)
        {
            if (string.IsNullOrEmpty(order)) { return order; }
            string[] parts = order.Split(',');
            List<string> keep = new List<string>();
            foreach (string p in parts)
            {
                string t = p.Trim();
                if (t.Length == 0) { continue; }
                if (string.Equals(t, token, StringComparison.OrdinalIgnoreCase)) { continue; }
                keep.Add(t);
            }
            return string.Join(",", keep.ToArray());
        }

        internal static string ReadOrder(string keyPath)
        {
            RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (RegistryKey k = baseKey.OpenSubKey(keyPath, false))
            {
                if (k == null) { return null; }
                return k.GetValue(ValueName, null) as string;
            }
        }

        internal static void WriteOrder(string keyPath, string value)
        {
            RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (RegistryKey k = baseKey.OpenSubKey(keyPath, true))
            {
                if (k == null) { throw new InvalidOperationException("无法打开注册表键 HKLM\\" + keyPath); }
                k.SetValue(ValueName, value, RegistryValueKind.String);
            }
        }

        internal static string ReadMachineEnv(string name)
        {
            string v = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
            return v == null ? Journal.Absent : v;
        }

        internal static void WriteMachineEnv(string name, string value)
        {
            if (value == Journal.Absent || value == null)
            {
                Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Machine);
            }
            else
            {
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Machine);
            }
        }

        /// Normalise anything the user may pick into "...\ISE_DS". Pure string work (unit testable).
        internal static string NormalizeIseDs(string input)
        {
            if (string.IsNullOrEmpty(input)) { return null; }
            string p = input.Trim().Trim('"');
            if (p.Length == 0) { return null; }
            p = p.Replace('/', '\\');
            while (p.Length > 3 && p.EndsWith("\\", StringComparison.Ordinal)) { p = p.Substring(0, p.Length - 1); }

            // ...\ISE_DS\ISE  (or anything below it) -> walk up to ISE_DS
            string[] parts = p.Split('\\');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                if (string.Equals(parts[i], "ISE_DS", StringComparison.OrdinalIgnoreCase))
                {
                    StringBuilder sb = new StringBuilder();
                    for (int j = 0; j <= i; j++)
                    {
                        if (j > 0) { sb.Append('\\'); }
                        sb.Append(parts[j]);
                    }
                    string result = sb.ToString();
                    if (result.StartsWith("\\\\", StringComparison.Ordinal)) { result = result.Substring(0); }
                    return result;
                }
            }
            return null;
        }

        /// Disk level validation of an ISE_DS directory. Returns null when fine, else an error text.
        internal static string ValidateIseDs(string iseDs, out string iseExe)
        {
            iseExe = null;
            if (string.IsNullOrEmpty(iseDs)) { return "请先选择 ISE 安装位置。"; }
            if (!Directory.Exists(iseDs)) { return "目录不存在：" + iseDs; }

            string settings = Path.Combine(iseDs, "settings64.bat");
            if (!File.Exists(settings))
            {
                return "在 " + iseDs + " 下找不到 settings64.bat，看起来不是 ISE 的 ISE_DS 目录。\r\n" +
                       "正确目录形如：D:\\Program\\ISE147\\14.7\\ISE_DS";
            }

            string[] candidates = new string[]
            {
                Path.Combine(Path.Combine(Path.Combine(iseDs, "ISE"), "bin"), "nt64\\ise.exe"),
                Path.Combine(Path.Combine(Path.Combine(iseDs, "ISE"), "bin"), "nt\\ise.exe")
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c)) { iseExe = c; return null; }
            }
            return "找不到 ISE\\bin\\nt64\\ise.exe，安装可能不完整。";
        }

        /// Apply the repairs. Returns the journal that was written.
        internal static Journal Apply(string iseDs, string iseExe, string licensePath, Action<string> log)
        {
            Journal j = Journal.Load();
            if (!j.IsApplied)
            {
                // first run - remember the pristine values
                j.Set("prevOrder", Nvl(ReadOrder(OrderKeyPath)));
                j.Set("prevHwOrder", Nvl(ReadOrder(HwOrderKeyPath)));
                j.Set("prev." + EnvVcCheck, ReadMachineEnv(EnvVcCheck));
                j.Set("prev." + EnvLicense, ReadMachineEnv(EnvLicense));
            }

            // 1) network provider order: ISE blocks in WNetEnumResourceW while the P9 plan-9 provider is listed
            string order = ReadOrder(OrderKeyPath);
            string hwOrder = ReadOrder(HwOrderKeyPath);
            log("当前网络提供程序顺序 Order=" + Nvl(order) + " HwOrder=" + Nvl(hwOrder));
            string newOrder = RemoveProvider(order, BadProvider);
            string newHw = RemoveProvider(hwOrder, BadProvider);
            if (newOrder != order) { WriteOrder(OrderKeyPath, newOrder); log("已从 Order 移除 " + BadProvider + " -> " + newOrder); }
            else { log("Order 中已无 " + BadProvider); }
            if (newHw != hwOrder) { WriteOrder(HwOrderKeyPath, newHw); log("已从 HwOrder 移除 " + BadProvider + " -> " + newHw); }
            else { log("HwOrder 中已无 " + BadProvider); }

            // 2) suppress the false "VC++ 2008 runtime libraries are not installed" dialog
            WriteMachineEnv(EnvVcCheck, "1");
            log("已设置环境变量 " + EnvVcCheck + "=1（跳过 VC++2008 运行库自检误报）");

            // 3) point ISE at the license file
            if (!string.IsNullOrEmpty(licensePath))
            {
                WriteMachineEnv(EnvLicense, licensePath);
                log("已设置环境变量 " + EnvLicense + "=" + licensePath);
            }
            else
            {
                log("未提供许可证文件，跳过 " + EnvLicense);
            }

            // 4) record state
            j.Set("version", Program.AppVersion);
            j.Set("applied", "1");
            j.Set("iseDs", iseDs);
            j.Set("iseExe", iseExe);
            j.Set("license", licensePath == null ? "" : licensePath);
            j.Set("installDir", Paths.InstallDir);
            j.Save();

            // 5) install the launcher copy + shortcuts
            InstallShortcuts(j, log);
            WarnAboutOldBatchShortcuts(log);
            return j;
        }

        /// The stock ISE installer creates a .lnk that points at settings64.bat, which cannot be pinned
        /// to the taskbar. Tell the user about it instead of silently deleting their shortcut.
        internal static void WarnAboutOldBatchShortcuts(Action<string> log)
        {
            string[] dirs = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };
            foreach (string dir in dirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) { continue; }
                    foreach (string lnk in Directory.GetFiles(dir, "*.lnk"))
                    {
                        string target = Shortcut.ReadTarget(lnk);
                        if (target != null && target.EndsWith("settings64.bat", StringComparison.OrdinalIgnoreCase))
                        {
                            log("提示：发现旧的 .bat 型快捷方式 " + lnk + "（指向 settings64.bat，无法固定到任务栏），" +
                                "新的 " + Path.GetFileName(lnk).Replace(".lnk", "") + " 快捷方式已可用，这个旧的可以手动删掉。");
                        }
                    }
                }
                catch { }
            }
        }

        internal static void InstallSelf(Action<string> log)
        {
            Directory.CreateDirectory(Paths.InstallDir);
            string self = Assembly.GetEntryAssembly().Location;
            if (!string.Equals(self, Paths.InstalledExe, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(self, Paths.InstalledExe, true);
                log("已把启动器安装到 " + Paths.InstalledExe);
            }
            else
            {
                log("启动器已位于 " + Paths.InstalledExe);
            }
        }

        internal static void InstallShortcuts(Journal j, Action<string> log)
        {
            InstallSelf(log);
            string iseExe = j.Get("iseExe", "");
            string icon = (iseExe.Length > 0 ? iseExe + ",0" : Paths.InstalledExe + ",0");

            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Xilinx ISE 14.7.lnk");
            Shortcut.Create(desktop, Paths.InstalledExe, "", Path.GetDirectoryName(Paths.InstalledExe), icon,
                            "启动 Xilinx ISE 14.7（由 ISETOOLS 封装）");
            j.Set("lnk.desktop", desktop);
            log("已创建桌面快捷方式：" + desktop);

            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string folder = Path.Combine(programs, "ISETOOLS");
            Directory.CreateDirectory(folder);
            string lnkStart = Path.Combine(folder, "Xilinx ISE 14.7.lnk");
            Shortcut.Create(lnkStart, Paths.InstalledExe, "", Path.GetDirectoryName(Paths.InstalledExe), icon,
                            "启动 Xilinx ISE 14.7（由 ISETOOLS 封装）");
            j.Set("lnk.startMenu", lnkStart);
            log("已创建开始菜单项：" + lnkStart);

            string lnkTool = Path.Combine(folder, "ISETOOLS 修复工具.lnk");
            Shortcut.Create(lnkTool, Paths.InstalledExe, "--setup", Path.GetDirectoryName(Paths.InstalledExe),
                            Paths.InstalledExe + ",0", "ISETOOLS：重新应用或检查修复");
            j.Set("lnk.tool", lnkTool);

            string lnkRollback = Path.Combine(folder, "回滚（恢复修复前状态）.lnk");
            Shortcut.Create(lnkRollback, Paths.InstalledExe, "--rollback", Path.GetDirectoryName(Paths.InstalledExe),
                            Paths.InstalledExe + ",0", "ISETOOLS：撤销全部修复");
            j.Set("lnk.rollback", lnkRollback);
            j.Save();
        }

        internal static void Rollback(Action<string> log)
        {
            Journal j = Journal.Load();
            if (!j.IsApplied)
            {
                log("没有可回滚的记录（journal 不存在或已回滚过）。");
                return;
            }

            // 1) provider order
            string prevOrder = j.Get("prevOrder", "");
            string prevHw = j.Get("prevHwOrder", "");
            if (prevOrder.Length > 0 && prevOrder != Journal.Absent)
            {
                WriteOrder(OrderKeyPath, prevOrder);
                log("已恢复 Order=" + prevOrder);
            }
            if (prevHw.Length > 0 && prevHw != Journal.Absent)
            {
                WriteOrder(HwOrderKeyPath, prevHw);
                log("已恢复 HwOrder=" + prevHw);
            }

            // 2) environment variables
            WriteMachineEnv(EnvVcCheck, j.Get("prev." + EnvVcCheck, Journal.Absent));
            log("已恢复环境变量 " + EnvVcCheck + " -> " + j.Get("prev." + EnvVcCheck, Journal.Absent));
            WriteMachineEnv(EnvLicense, j.Get("prev." + EnvLicense, Journal.Absent));
            log("已恢复环境变量 " + EnvLicense + " -> " + j.Get("prev." + EnvLicense, Journal.Absent));

            // 3) restore any libPortability.dll.orig that is lying around in the ISE tree
            string iseDs = j.Get("iseDs", "");
            if (iseDs.Length > 0 && Directory.Exists(iseDs))
            {
                string[] libDirs = new string[]
                {
                    Path.Combine(iseDs, "ISE\\lib\\nt64"),
                    Path.Combine(iseDs, "ISE\\lib\\nt")
                };
                foreach (string dir in libDirs)
                {
                    string orig = Path.Combine(dir, "libPortability.dll.orig");
                    string live = Path.Combine(dir, "libPortability.dll");
                    if (File.Exists(orig) && File.Exists(live))
                    {
                        try
                        {
                            File.Copy(live, live + ".nosh", true);
                            File.Copy(orig, live, true);
                            log("已把 libPortability.dll 还原为修复前版本（" + dir + "）");
                        }
                        catch (Exception ex) { log("还原 libPortability.dll 失败：" + ex.Message); }
                    }
                }
            }

            // 4) drop shortcuts
            RemoveIfExists(j.Get("lnk.desktop", ""), log);
            RemoveIfExists(j.Get("lnk.startMenu", ""), log);
            RemoveIfExists(j.Get("lnk.tool", ""), log);
            RemoveIfExists(j.Get("lnk.rollback", ""), log);
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "ISETOOLS");
                if (Directory.Exists(folder) && Directory.GetFiles(folder).Length == 0) { Directory.Delete(folder); }
            }
            catch { }

            j.Set("applied", "0");
            j.Save();
            log("回滚完成。重新登录（或重启）后环境变量改动会彻底生效。");
        }

        private static void RemoveIfExists(string path, Action<string> log)
        {
            if (string.IsNullOrEmpty(path)) { return; }
            try
            {
                if (File.Exists(path)) { File.Delete(path); log("已删除快捷方式：" + path); }
            }
            catch (Exception ex) { log("删除快捷方式失败 " + path + " : " + ex.Message); }
        }

        private static string Nvl(string s) { return s == null ? Journal.Absent : s; }
    }

    // ---------------------------------------------------------------- shortcuts via WScript.Shell

    internal static class Shortcut
    {
        internal static void Create(string lnkPath, string target, string arguments, string workingDir,
                                    string iconLocation, string description)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) { throw new InvalidOperationException("WScript.Shell 不可用"); }
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                           new object[] { lnkPath });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { arguments ?? "" });
                if (!string.IsNullOrEmpty(workingDir))
                {
                    st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workingDir });
                }
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { iconLocation });
                if (!string.IsNullOrEmpty(description))
                {
                    st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
                }
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.ReleaseComObject(sc);
            }
            finally
            {
                if (shell != null) { Marshal.ReleaseComObject(shell); }
            }
        }

        /// Read the target of a .lnk (used for auto-detecting an existing ISE install).
        internal static string ReadTarget(string lnkPath)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) { return null; }
                object shell = Activator.CreateInstance(t);
                try
                {
                    object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                                               new object[] { lnkPath });
                    object target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null);
                    Marshal.ReleaseComObject(sc);
                    return target as string;
                }
                finally { Marshal.ReleaseComObject(shell); }
            }
            catch { return null; }
        }
    }

    // ---------------------------------------------------------------- launcher

    internal static class Launch
    {
        internal static int Run()
        {
            Journal j = Journal.Load();
            string iseDs = j.Get("iseDs", "");
            string iseExe = j.Get("iseExe", "");
            if (iseDs.Length == 0 || !Directory.Exists(iseDs))
            {
                MessageBox.Show("找不到已记录的 ISE 安装位置，请重新运行 ISETOOLS 修复向导。\r\n\r\n记录的路径：" +
                                (iseDs.Length == 0 ? "(空)" : iseDs),
                                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 3;
            }
            if (iseExe.Length == 0 || !File.Exists(iseExe))
            {
                string err;
                string found;
                err = Repair.ValidateIseDs(iseDs, out found);
                if (err != null)
                {
                    MessageBox.Show(err, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 4;
                }
                iseExe = found;
            }
            return StartIse(iseDs, iseExe);
        }

        /// settings64.bat with an argument does "start <arg>", which breaks on paths containing spaces,
        /// so we generate a tiny batch instead of guessing cmd quoting.
        internal static int StartIse(string iseDs, string iseExe)
        {
            try
            {
                string settings = Path.Combine(iseDs, "settings64.bat");
                string bat = Path.Combine(Path.GetTempPath(), "isetools-launch-" +
                                          Process.GetCurrentProcess().Id + ".bat");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("@echo off");
                sb.AppendLine("call \"" + settings + "\"");
                sb.AppendLine("if errorlevel 1 exit /b 1");
                sb.AppendLine("start \"\" \"" + iseExe + "\"");
                File.WriteAllText(bat, sb.ToString(), Encoding.Default);

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c \"" + bat + "\"";
                psi.WorkingDirectory = Path.Combine(iseDs, "ISE");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                try { p.WaitForExit(5000); } catch { }
                try { File.Delete(bat); } catch { }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动 ISE 失败：" + ex.Message, Program.AppName,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 5;
            }
        }
    }

    // ---------------------------------------------------------------- UI

    internal sealed class SetupForm : Form
    {
        private readonly bool _setupMode;
        private readonly TextBox _txtIse = new TextBox();
        private readonly TextBox _txtLic = new TextBox();
        private readonly TextBox _txtLog = new TextBox();
        private readonly Label _lblState = new Label();

        internal SetupForm(bool setupMode)
        {
            _setupMode = setupMode;
            Text = Program.AppName + " " + Program.AppVersion + " - Xilinx ISE 14.7 修复 / 启动器";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 470);
            Font = new Font("Microsoft YaHei UI", 9f);

            Label lb1 = new Label();
            lb1.Text = "ISE 安装位置（选择 ...\\14.7\\ISE_DS 或其下的 ISE 目录）：";
            lb1.SetBounds(12, 12, 600, 18);
            Controls.Add(lb1);

            _txtIse.SetBounds(12, 32, 520, 24);
            Controls.Add(_txtIse);

            Button b1 = new Button();
            b1.Text = "浏览…";
            b1.SetBounds(540, 31, 88, 26);
            b1.Click += OnBrowseIse;
            Controls.Add(b1);

            Label lb2 = new Label();
            lb2.Text = "许可证文件（Xilinx_ise.lic，可留空）：";
            lb2.SetBounds(12, 66, 600, 18);
            Controls.Add(lb2);

            _txtLic.SetBounds(12, 86, 520, 24);
            Controls.Add(_txtLic);

            Button b2 = new Button();
            b2.Text = "浏览…";
            b2.SetBounds(540, 85, 88, 26);
            b2.Click += OnBrowseLic;
            Controls.Add(b2);

            Button bApply = new Button();
            bApply.Text = "应用修复并安装";
            bApply.SetBounds(12, 122, 190, 34);
            bApply.Click += OnApply;
            Controls.Add(bApply);

            Button bRollback = new Button();
            bRollback.Text = "回滚到修复前";
            bRollback.SetBounds(212, 122, 150, 34);
            bRollback.Click += OnRollback;
            Controls.Add(bRollback);

            Button bLaunch = new Button();
            bLaunch.Text = "启动 ISE";
            bLaunch.SetBounds(372, 122, 120, 34);
            bLaunch.Click += OnLaunch;
            Controls.Add(bLaunch);

            Button bClose = new Button();
            bClose.Text = "关闭";
            bClose.SetBounds(540, 122, 88, 34);
            bClose.Click += delegate { Close(); };
            Controls.Add(bClose);

            _lblState.SetBounds(12, 164, 616, 20);
            _lblState.ForeColor = Color.DimGray;
            Controls.Add(_lblState);

            _txtLog.SetBounds(12, 188, 616, 268);
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.White;
            _txtLog.Font = new Font("Consolas", 9f);
            Controls.Add(_txtLog);

            Log.Sink = delegate(string line) { AppendLog(line); };

            Load += OnLoad;
        }

        private void OnLoad(object sender, EventArgs e)
        {
            Log.Write(Program.AppName + " " + Program.AppVersion + " 启动，管理员权限 = " + Admin.IsElevated());
            Journal j = Journal.Load();
            if (j.IsApplied)
            {
                _txtIse.Text = j.Get("iseDs", "");
                _txtLic.Text = j.Get("license", "");
                _lblState.Text = "状态：已应用修复（安装于 " + j.Get("installDir", Paths.InstallDir) + "）";
            }
            else
            {
                _lblState.Text = "状态：未安装 / 未应用修复";
                AutoDetect();
            }
        }

        private void AutoDetect()
        {
            // 1) an existing ISE shortcut on either desktop
            string[] desktops = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };
            foreach (string d in desktops)
            {
                try
                {
                    foreach (string lnk in Directory.GetFiles(d, "*.lnk"))
                    {
                        string target = Shortcut.ReadTarget(lnk);
                        if (target != null && target.EndsWith("settings64.bat", StringComparison.OrdinalIgnoreCase))
                        {
                            string iseDs = Repair.NormalizeIseDs(Path.GetDirectoryName(target));
                            if (iseDs != null) { _txtIse.Text = iseDs; Log.Write("自动检测到 ISE：" + iseDs + "（来自 " + lnk + "）"); }
                        }
                    }
                }
                catch { }
            }
            if (_txtIse.Text.Length == 0)
            {
                string[] guesses = new string[]
                {
                    @"D:\Program\ISE147\14.7\ISE_DS", @"D:\Program\ISE\14.7\ISE_DS",
                    @"C:\Xilinx\14.7\ISE_DS", @"D:\Xilinx\14.7\ISE_DS", @"E:\Xilinx\14.7\ISE_DS"
                };
                foreach (string g in guesses)
                {
                    string n = Repair.NormalizeIseDs(g);
                    if (n != null && File.Exists(Path.Combine(n, "settings64.bat"))) { _txtIse.Text = n; break; }
                }
            }

            // 2) a license file in the usual download place
            string[] licDirs = new string[] { @"E:\Download", @"D:\Download", @"C:\Xilinx", @"D:\Program\ISE147" };
            foreach (string dir in licDirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) { continue; }
                    string[] files = Directory.GetFiles(dir, "*.lic");
                    if (files.Length > 0) { _txtLic.Text = files[0]; Log.Write("自动检测到许可证：" + files[0]); break; }
                }
                catch { }
            }
        }

        private void OnBrowseIse(object sender, EventArgs e)
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "选择 ISE 安装目录（例如 D:\\Program\\ISE147\\14.7\\ISE_DS）";
            if (_txtIse.Text.Length > 0) { dlg.SelectedPath = _txtIse.Text; }
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                string n = Repair.NormalizeIseDs(dlg.SelectedPath);
                _txtIse.Text = n == null ? dlg.SelectedPath : n;
            }
        }

        private void OnBrowseLic(object sender, EventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Filter = "License files (*.lic)|*.lic|All files (*.*)|*.*";
            dlg.Title = "选择 Xilinx 许可证文件";
            if (dlg.ShowDialog(this) == DialogResult.OK) { _txtLic.Text = dlg.FileName; }
        }

        private void OnApply(object sender, EventArgs e)
        {
            if (!Admin.IsElevated())
            {
                Admin.RelaunchElevated("--setup");
                Close();
                return;
            }
            try
            {
                string iseDs = Repair.NormalizeIseDs(_txtIse.Text);
                string iseExe;
                string err = Repair.ValidateIseDs(iseDs, out iseExe);
                if (err != null)
                {
                    MessageBox.Show(this, err, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                List<string> running = new List<string>();
                foreach (Process p in Process.GetProcessesByName("ise")) { running.Add(p.Id.ToString()); }
                if (running.Count > 0)
                {
                    Log.Write("警告：检测到正在运行的 ise.exe (pid " + string.Join(",", running.ToArray()) +
                              ")，建议先关闭它们再验证。");
                }

                string lic = _txtLic.Text.Trim();
                if (lic.Length > 0)
                {
                    if (!File.Exists(lic)) { Log.Write("警告：许可证文件不存在：" + lic); }
                    else
                    {
                        string text = File.ReadAllText(lic);
                        if (text.IndexOf("xilinxd", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            Log.Write("警告：许可证内容里没有出现 xilinxd，可能不是 Xilinx ISE 的有效文件。");
                        }
                        else { Log.Write("许可证文件校验通过（含 xilinxd，uncounted/hostid=ANY 时无需 license 服务）。"); }
                    }
                }

                Repair.Apply(iseDs, iseExe, lic, Log.Write);
                _lblState.Text = "状态：已应用修复（安装于 " + Paths.InstallDir + "）";
                Log.Write("完成。桌面和开始菜单快捷方式都已指向 " + Paths.InstalledExe);
                Log.Write("提示：环境变量的改动对已经运行的程序无效；如遇异常，注销/重启一次即可。");
            }
            catch (Exception ex)
            {
                Log.Write("应用修复失败：" + ex);
                MessageBox.Show(this, "应用修复失败：\r\n" + ex.Message, Program.AppName,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnRollback(object sender, EventArgs e)
        {
            if (!Admin.IsElevated())
            {
                Admin.RelaunchElevated("--rollback");
                Close();
                return;
            }
            if (MessageBox.Show(this, "将把网络提供程序顺序、环境变量和备份过的 ISE 文件恢复到修复前的状态，并删除快捷方式。\r\n继续？",
                                Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }
            try
            {
                Repair.Rollback(Log.Write);
                _lblState.Text = "状态：已回滚";
            }
            catch (Exception ex)
            {
                Log.Write("回滚失败：" + ex);
                MessageBox.Show(this, "回滚失败：\r\n" + ex.Message, Program.AppName,
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnLaunch(object sender, EventArgs e)
        {
            string iseDs = Repair.NormalizeIseDs(_txtIse.Text);
            string iseExe;
            string err = Repair.ValidateIseDs(iseDs, out iseExe);
            if (err != null)
            {
                MessageBox.Show(this, err, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Log.Write("正在启动 ISE：" + iseExe);
            Launch.StartIse(iseDs, iseExe);
        }

        private void AppendLog(string line)
        {
            if (_txtLog.IsDisposed) { return; }
            _txtLog.AppendText(line + Environment.NewLine);
        }
    }

    // ---------------------------------------------------------------- self test

    internal static class SelfTest
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        internal static int Run()
        {
            if (!AttachConsole(0xFFFFFFFF)) { AllocConsole(); }
            try
            {
                Stream stdout = Console.OpenStandardOutput();
                Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
            }
            catch { }
            int failures = 0;

            failures += Check("RemoveProvider(P9NP middle)",
                Repair.RemoveProvider("RDPNP,P9NP,LanmanWorkstation,webclient", "P9NP") ==
                "RDPNP,LanmanWorkstation,webclient");

            failures += Check("RemoveProvider(case insensitive)",
                Repair.RemoveProvider("RDPNP,p9np,LanmanWorkstation", "P9NP") == "RDPNP,LanmanWorkstation");

            failures += Check("RemoveProvider(not present leaves order untouched)",
                Repair.RemoveProvider("A,B", "P9NP") == "A,B");

            failures += Check("RemoveProvider(single token)",
                Repair.RemoveProvider("P9NP", "P9NP") == "");

            failures += Check("RemoveProvider(null safe)",
                Repair.RemoveProvider(null, "P9NP") == null);

            failures += Check("NormalizeIseDs(...\\ISE_DS\\ISE)",
                Repair.NormalizeIseDs(@"D:\Program\ISE147\14.7\ISE_DS\ISE") == @"D:\Program\ISE147\14.7\ISE_DS");

            failures += Check("NormalizeIseDs(trailing slash)",
                Repair.NormalizeIseDs(@"D:\Program\ISE147\14.7\ISE_DS\") == @"D:\Program\ISE147\14.7\ISE_DS");

            failures += Check("NormalizeIseDs(forward slashes)",
                Repair.NormalizeIseDs("D:/Program/ISE147/14.7/ISE_DS/ISE") == @"D:\Program\ISE147\14.7\ISE_DS");

            failures += Check("NormalizeIseDs(quoted input)",
                Repair.NormalizeIseDs("\"D:\\Program\\ISE147\\14.7\\ISE_DS\"") == @"D:\Program\ISE147\14.7\ISE_DS");

            failures += Check("NormalizeIseDs(no ISE_DS -> null)",
                Repair.NormalizeIseDs(@"D:\Program\Foo") == null);

            string exe;
            failures += Check("ValidateIseDs(missing dir) reports an error",
                Repair.ValidateIseDs(@"D:\does\not\exist\ISE_DS", out exe) != null);

            // journal round trip in a temp dir
            string tmp = Path.Combine(Path.GetTempPath(), "isetools-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                Journal j = new Journal();
                j.Set("applied", "1");
                j.Set("iseDs", @"D:\x\ISE_DS");
                j.Set("prevOrder", "RDPNP,P9NP,LanmanWorkstation,webclient");
                string file = Path.Combine(tmp, "journal.txt");
                // round trip a journal written to a temp file (never touch the real one during a self test)
                File.WriteAllText(file, "applied=1\r\niseDs=D:\\x\\ISE_DS\r\nprevOrder=RDPNP,P9NP,LanmanWorkstation,webclient\r\n",
                                  Encoding.UTF8);
                Journal back = new Journal();
                foreach (string raw in File.ReadAllLines(file, Encoding.UTF8))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) { continue; }
                    back.Set(raw.Substring(0, eq).Trim(), raw.Substring(eq + 1));
                }
                failures += Check("journal round trip",
                    back.IsApplied &&
                    back.Get("iseDs", "") == @"D:\x\ISE_DS" &&
                    back.Get("prevOrder", "") == "RDPNP,P9NP,LanmanWorkstation,webclient");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "SELFTEST: ALL PASSED"
                : "SELFTEST: " + failures + " FAILURE(S)");
            return failures == 0 ? 0 : 1;
        }

        private static int Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
            return ok ? 0 : 1;
        }
    }
}
