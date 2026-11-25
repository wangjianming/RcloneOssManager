using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RcloneOssManager
{
    /// <summary>打开本地目录的统一入口：优先使用本机已安装的现代文件管理器，否则交给系统默认程序。</summary>
    internal static class FileManagerLaunch
    {
        private static string cachedManager;
        private static bool resolved;

        /// <summary>自动探测的根目录，按优先级排列。</summary>
        /// <returns>可能包含文件管理器的安装根目录。</returns>
        private static List<string> Roots()
        {
            var roots = new List<string>();
            Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            Add(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            Add(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
            roots.Add(@"D:\Program Files");
            roots.Add(@"D:\Program Files (x86)");
            roots.Add(@"D:\tools");
            return roots;
        }

        /// <summary>按安装路径给出的 Pulse 候选程序，顺序即优先级。</summary>
        /// <returns>可能存在的 Pulse 完整路径。</returns>
        internal static string[] PulseCandidates()
        {
            var list = new List<string>();
            foreach (var root in Roots())
            {
                if (String.IsNullOrWhiteSpace(root)) continue;
                var candidate = Path.Combine(root, "Pulse", "pulse.exe");
                if (!list.Contains(candidate, StringComparer.OrdinalIgnoreCase)) list.Add(candidate);
            }
            return list.ToArray();
        }

        /// <summary>自动探测的文件管理器候选：Pulse 优先，其次 Directory Opus。</summary>
        /// <returns>可能存在的程序完整路径。</returns>
        internal static string[] FileManagerCandidates()
        {
            var list = new List<string>(PulseCandidates());
            foreach (var root in Roots())
            {
                if (String.IsNullOrWhiteSpace(root)) continue;
                var candidate = Path.Combine(root, "Directory Opus", "dopus.exe");
                if (!list.Contains(candidate, StringComparer.OrdinalIgnoreCase)) list.Add(candidate);
            }
            return list.ToArray();
        }

        private static void Add(List<string> roots, string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return;
            if (!roots.Contains(value, StringComparer.OrdinalIgnoreCase)) roots.Add(value);
        }

        /// <summary>定位可用的文件管理器：优先用户配置，再查常见安装路径，最后查卸载登记；空值表示用系统默认。</summary>
        /// <returns>可执行文件路径或空值。</returns>
        internal static string ResolveManager()
        {
            if (resolved) return cachedManager;
            resolved = true;
            var configured = RuntimePaths.Current.FileManagerExecutable;
            if (!String.IsNullOrWhiteSpace(configured) && File.Exists(configured)) { cachedManager = configured; return cachedManager; }
            foreach (var candidate in FileManagerCandidates())
            {
                if (File.Exists(candidate)) { cachedManager = candidate; return cachedManager; }
            }
            cachedManager = FromRegistry("Pulse", "pulse.exe");
            if (cachedManager == null) cachedManager = FromRegistry("Directory Opus", "dopus.exe");
            return cachedManager;
        }

        /// <summary>从卸载登记里找程序的安装目录，覆盖用户自定义安装盘符的情况。</summary>
        /// <param name="displayName">产品名关键字。</param><param name="executableName">安装目录下的主程序文件名。</param>
        /// <returns>可执行文件路径或空值。</returns>
        internal static string FromRegistry(string displayName, string executableName)
        {
            if (String.IsNullOrWhiteSpace(displayName)) return null;
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            var hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            foreach (var hive in hives)
            {
                foreach (var view in views)
                {
                    var found = Scan(OpenBaseKey(hive, view), displayName, executableName);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static RegistryKey OpenBaseKey(RegistryHive hive, RegistryView view)
        {
            try { return RegistryKey.OpenBaseKey(hive, view); }
            catch (ArgumentException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            catch (IOException) { return null; }
        }

        private static string Scan(RegistryKey rootKey, string displayName, string executableName)
        {
            if (rootKey == null) return null;
            RegistryKey uninstall = null;
            try
            {
                uninstall = rootKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) return null;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    RegistryKey item = null;
                    try
                    {
                        item = uninstall.OpenSubKey(name);
                        if (item == null) continue;
                        var title = item.GetValue("DisplayName") as string;
                        if (String.IsNullOrWhiteSpace(title) || title.IndexOf(displayName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var exe = ExecutableFrom(item, executableName);
                        if (exe != null) return exe;
                    }
                    catch (System.Security.SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                    finally { if (item != null) item.Dispose(); }
                }
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            finally
            {
                if (uninstall != null) uninstall.Dispose();
                rootKey.Dispose();
            }
            return null;
        }

        private static string ExecutableFrom(RegistryKey item, string executableName)
        {
            var location = item.GetValue("InstallLocation") as string;
            if (!String.IsNullOrWhiteSpace(location))
            {
                var candidate = Path.Combine(location.Trim().Trim('"'), executableName);
                if (File.Exists(candidate)) return candidate;
            }
            var icon = item.GetValue("DisplayIcon") as string;
            if (!String.IsNullOrWhiteSpace(icon))
            {
                var path = icon.Trim().Trim('"');
                var comma = path.IndexOf(',');
                if (comma > 0) path = path.Substring(0, comma).Trim().Trim('"');
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>打开目录并返回失败原因，不抛出异常。</summary>
        /// <param name="folder">本地目录或挂载路径。</param><param name="error">失败时的中文说明。</param>
        /// <returns>是否已发起打开。</returns>
        internal static bool TryOpen(string folder, out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(folder)) { error = "尚未选择下载位置。"; return false; }
            string full;
            try { full = Path.GetFullPath(folder); }
            catch (ArgumentException) { error = "路径格式无效：" + folder; return false; }
            catch (NotSupportedException) { error = "路径格式无效：" + folder; return false; }
            catch (PathTooLongException) { error = "路径过长：" + folder; return false; }

            var manager = ResolveManager();
            if (manager != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(manager, "\"" + full + "\"") { UseShellExecute = true });
                    return true;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
                catch (IOException) { }
                // 程序无法启动时退回系统默认，不把用户卡在报错里。
                resolved = false; cachedManager = null;
            }

            try
            {
                Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                error = "系统没有可用来打开该目录的程序：" + ex.Message;
                return false;
            }
            catch (InvalidOperationException ex) { error = "无法打开目录：" + ex.Message; return false; }
            catch (IOException ex) { error = "无法打开目录：" + ex.Message; return false; }
        }

        /// <summary>打开目录并在失败时给出明确提示，不静默失败。</summary>
        /// <param name="folder">本地目录或挂载路径。</param><param name="owner">提示框的所属窗口，可空。</param>
        internal static void Open(string folder, IWin32Window owner)
        {
            string error;
            if (TryOpen(folder, out error)) return;
            var caption = "OSS 文件管理";
            if (owner != null) MessageBox.Show(owner, error, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else MessageBox.Show(error, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
