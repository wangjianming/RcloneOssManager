using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RcloneOssManager
{

    internal sealed class CommandResult
    {
        internal int ExitCode;
        internal string StandardOutput;
        internal string StandardError;
        internal bool TimedOut;
        internal bool Success { get { return !TimedOut && ExitCode == 0; } }
        internal string Error { get { return String.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError; } }
    }

    internal sealed partial class RcloneClient
    {
        /// <summary>路径由用户设置或内嵌引擎解析，不依赖固定盘符。</summary>
        internal static string RclonePath { get { return RuntimePaths.Current.Executable(); } }
        internal static string ConfigPath { get { RuntimePaths.Current.EnsureConfiguration(); return RuntimePaths.Current.RcloneConfiguration; } }
        internal const string LatestVersion = "v1.75.0";
        internal const bool UpgradeStopsMounts = true;
        internal const bool UpgradeRestartsMounts = true;
        // 挂载生命周期串行执行，连接测试保持独立，避免自动恢复与用户卸载互相竞争。
        internal static readonly object MountSync = new object();
        // 目录浏览依赖服务器修改时间的连接；在设置文件 ServerModtimeRemotes 中按连接标识配置，默认空。
        internal static string[] ServerModtimeRemotes { get { return RuntimePaths.Current.ServerModtimeRemotes ?? new string[0]; } }
        private readonly string configPath;
        internal string ConfigurationPath { get { return configPath; } }

        /// <summary>后台恢复挂载，不创建 WinForms 窗口。</summary>
        internal static void RestoreRecordedMounts()
        {
            var client = new RcloneClient();
            lock (MountSync)
            {
                var active = MainForm.GetMounts();
                var occupied = new HashSet<string>(Environment.GetLogicalDrives().Select(x => x.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
                foreach (var entry in MountStore.Read())
                {
                    if (active.ContainsKey(entry.Key) || occupied.Contains(entry.Value.Drive)) continue;
                    try
                    {
                        client.Mount(entry.Key, entry.Value.Drive, entry.Value.VolumeName);
                        ManagerLog.Write("开机恢复挂载", entry.Key, entry.Value.Drive, "success");
                    }
                    catch (Exception ex) { ManagerLog.Write("开机恢复挂载失败", entry.Key, entry.Value.Drive, ex.Message); }
                }
            }
        }

        /// <summary>验证 rclone 程序及其本机配置均可用。</summary>
        /// <param name="path">配置路径；默认使用现有配置，隔离测试可指定临时配置。</param>
        internal RcloneClient(string path = null)
        {
            configPath = path ?? ConfigPath;
            if (!File.Exists(RclonePath)) throw new InvalidOperationException("未找到 rclone.exe：" + RclonePath);
            if (!File.Exists(configPath)) throw new InvalidOperationException("未找到 rclone 配置文件：" + configPath);
        }

        /// <summary>以隐藏子进程执行 rclone，并完整收集标准输出和错误。</summary>
        /// <param name="arguments">不含程序路径的参数。</param><param name="timeoutMilliseconds">总等待上限。</param>
        /// <param name="overrideConfig">独立测试配置路径，可空。</param><param name="cancellation">取消信号。</param>
        /// <returns>进程结果；取消时抛出 OperationCanceledException。</returns>
        internal CommandResult Run(IEnumerable<string> arguments, int timeoutMilliseconds, string overrideConfig = null, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            var escaped = new List<string> { "--config", overrideConfig ?? configPath };
            escaped.AddRange(arguments);
            var info = new ProcessStartInfo(RclonePath, JoinArguments(escaped))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var watch = Stopwatch.StartNew();
                while (!process.WaitForExit(100))
                {
                    if (!cancellation.IsCancellationRequested && watch.ElapsedMilliseconds < timeoutMilliseconds) continue;
                    try { process.Kill(); process.WaitForExit(2000); } catch { }
                    cancellation.ThrowIfCancellationRequested();
                    return new CommandResult { ExitCode = -1, TimedOut = true, StandardOutput = SafeResult(stdout), StandardError = SafeResult(stderr) };
                }
                Task.WaitAll(new Task[] { stdout, stderr }, 2000);
                return new CommandResult { ExitCode = process.ExitCode, StandardOutput = SafeResult(stdout), StandardError = SafeResult(stderr) };
            }
        }

        /// <summary>读取当前本机 rclone remote 名称，不访问远端。</summary>
        internal List<string> GetRemotes()
        {
            return File.ReadAllLines(configPath, Encoding.UTF8).Select(x => x.Trim())
                .Where(x => x.StartsWith("[") && x.EndsWith("]") && x.Length > 2)
                .Select(x => x.Substring(1, x.Length - 2)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>一次读取连接显示名称；没有 description 的旧连接仍显示原标识，不迁移配置。</summary>
        /// <returns>内部标识与友好名称映射。</returns>
        internal Dictionary<string, string> GetDisplayNames()
        {
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string current = null;
            foreach (var line in File.ReadAllLines(configPath, Encoding.UTF8))
            {
                var text = line.Trim();
                if (text.StartsWith("[") && text.EndsWith("]") && text.Length > 2) { current = text.Substring(1, text.Length - 2); labels[current] = current; continue; }
                var split = text.IndexOf('=');
                if (current != null && split > 0 && text.Substring(0, split).Trim().Equals("description", StringComparison.OrdinalIgnoreCase))
                {
                    var label = text.Substring(split + 1).Trim(); if (!String.IsNullOrWhiteSpace(label)) labels[current] = label;
                }
            }
            return labels;
        }

        /// <summary>只更新 description，不修改配置标识、认证、排序、盘符或挂载进程。</summary>
        /// <param name="name">稳定的内部连接标识。</param><param name="label">界面显示名称。</param><param name="token">取消信号。</param>
        internal void UpdateDisplayName(string name, string label, CancellationToken token = default(CancellationToken))
        {
            ValidateDisplayName(label);
            if (!FindRemote(name)) throw new InvalidOperationException("连接已被删除，请刷新后重试。");
            // 参数结束标记保护以 -- 开头的显示名称，名称不会被解释为 rclone 选项。
            EnsureSuccess(Run(new[] { "config", "update", "--", name, "description", label }, 15000, null, token), "保存显示名称失败");
            if (GetDisplayNames()[name] != label.Trim()) throw new InvalidOperationException("显示名称保存核验失败，请刷新后检查。");
        }

        /// <summary>新连接使用独立稳定标识，用户不需要遵守 rclone 配置节的格式要求。</summary>
        /// <returns>自动生成的内部标识。</returns>
        internal static string NewRemoteName() { return "oss_" + Guid.NewGuid().ToString("N"); }

        /// <summary>显示名称仅限制空值和控制字符，允许中文、数字开头、空格及常见符号。</summary>
        /// <param name="label">用户输入的名称。</param>
        internal static void ValidateDisplayName(string label)
        {
            if (String.IsNullOrWhiteSpace(label) || label.Any(Char.IsControl)) throw new ArgumentException("请输入连接名称，名称不能包含换行或控制字符。");
        }

        /// <summary>读取编辑回填所需的本机配置字段，凭据不得写入日志。</summary>
        internal Dictionary<string, string> GetMetadata(string name)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inSection = false;
            foreach (var line in File.ReadAllLines(configPath, Encoding.UTF8))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]")) { inSection = String.Equals(trimmed.Substring(1, trimmed.Length - 2), name, StringComparison.OrdinalIgnoreCase); continue; }
                if (!inSection) continue;
                var index = trimmed.IndexOf('=');
                if (index <= 0) continue;
                var key = trimmed.Substring(0, index).Trim();
                var value = trimmed.Substring(index + 1).Trim();
                map[key] = value;
            }
            if (map.Count == 0) throw new InvalidOperationException("读取本机连接信息失败：未找到连接 " + name + "。");
            return map;
        }

        /// <summary>以只读 bucket 列举测试指定连接。</summary>
        /// <param name="name">本机连接名。</param><param name="cancellation">取消信号。</param><returns>只读请求结果。</returns>
        internal CommandResult Test(string name, CancellationToken cancellation = default(CancellationToken))
        {
            return Run(new[] { "lsd", name + ":", "--contimeout", "5s", "--timeout", "10s", "--retries", "1", "--low-level-retries", "1" }, 15000, null, cancellation);
        }

        /// <summary>生成 rclone 官方 Windows amd64 下载地址。</summary>
        internal static string OfficialDownloadUrl(string version) { return "https://downloads.rclone.org/" + version + "/rclone-" + version + "-windows-amd64.zip"; }

        /// <summary>读取当前本机 rclone 版本号。</summary>
        internal string CurrentVersion()
        {
            var result = Run(new[] { "version" }, 5000);
            var match = System.Text.RegularExpressions.Regex.Match(result.StandardOutput, "(?im)^rclone (v[0-9]+\\.[0-9]+\\.[0-9]+)");
            return result.Success && match.Success ? match.Groups[1].Value : "未知";
        }

        /// <summary>通过 GitHub 官方 API 获取最新稳定版标签，启用 TLS 1.2 兼容旧 Windows。</summary>
        internal static string GetLatestVersion()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/rclone/rclone/releases/latest");
            request.UserAgent = "RcloneOssManager"; request.Timeout = 10000; request.ReadWriteTimeout = 10000;
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var match = System.Text.RegularExpressions.Regex.Match(reader.ReadToEnd(), "\"tag_name\"\\s*:\\s*\"(v[0-9]+\\.[0-9]+\\.[0-9]+)\"");
                if (!match.Success) throw new InvalidOperationException("无法读取官方最新版本号。");
                return match.Groups[1].Value;
            }
        }

        /// <summary>比较语义版本，判断远端版本是否高于当前版本。</summary>
        internal static bool IsNewerVersion(string latest, string current) { Version latestVersion, currentVersion; return Version.TryParse((latest ?? String.Empty).TrimStart('v'), out latestVersion) && Version.TryParse((current ?? String.Empty).TrimStart('v'), out currentVersion) && latestVersion > currentVersion; }

        /// <summary>从官方地址下载并替换 rclone.exe，保留 rclone.conf 不变。</summary>
        internal void Upgrade(string version, Action<int, long, long, double, string> progress, Action<string> report)
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "rclone-upgrade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            var zipPath = Path.Combine(tempRoot, "rclone.zip");
            var stoppedMounts = false;
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
                using (var web = new WebClient())
                {
                    web.Headers[HttpRequestHeader.UserAgent] = "RcloneOssManager";
                    var watch = Stopwatch.StartNew();
                    web.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs args)
                    {
                        var seconds = Math.Max(0.001, watch.Elapsed.TotalSeconds);
                        progress(args.ProgressPercentage, args.BytesReceived, args.TotalBytesToReceive, args.BytesReceived / seconds, "正在下载 rclone " + version);
                    };
                    web.DownloadFileTaskAsync(new Uri(OfficialDownloadUrl(version)), zipPath).GetAwaiter().GetResult();
                }
                progress(100, 0, 0, 0, "下载完成，正在解压…");
                ZipFile.ExtractToDirectory(zipPath, tempRoot);
                progress(100, 0, 0, 0, "正在校验下载文件…");
                var extracted = Directory.GetFiles(tempRoot, "rclone.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (String.IsNullOrWhiteSpace(extracted)) throw new InvalidOperationException("官方压缩包中未找到 rclone.exe。");
                if (!IsValidDownloadedVersionOutput(ReadVersionOutput(extracted))) throw new InvalidOperationException("下载的 rclone.exe 校验未通过，未修改当前程序。");
                progress(100, 0, 0, 0, "正在替换 rclone.exe…");
                StopMountProcesses();
                stoppedMounts = true;
                var staged = RclonePath + ".new";
                File.Copy(extracted, staged, true);
                var backup = RclonePath + "." + DateTime.Now.ToString("yyyyMMddHHmmss") + ".bak";
                File.Move(RclonePath, backup);
                try { File.Move(staged, RclonePath); } catch { File.Move(backup, RclonePath); throw; }
                // 内嵌引擎升级后固定新路径，避免下次启动因原资源哈希不同恢复到旧版本。
                RuntimePaths.Current.Save(RclonePath, configPath);
                progress(100, 0, 0, 0, "正在恢复本地挂载…");
                // 一旦开始恢复就不重复启动脚本；慢启动进程由后续状态检查识别。
                stoppedMounts = false;
                StartRecordedMounts();
                report("rclone 已升级到 " + version + "。配置文件保持不变，已自动恢复记录中的本地磁盘。");
            }
            finally
            {
                if (stoppedMounts) { try { StartRecordedMounts(); } catch { } }
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        /// <summary>格式化升级下载进度，供界面状态栏和测试复用。</summary>
        internal static string FormatDownloadProgress(int percent, long received, long total, double bytesPerSecond, string stage)
        {
            var size = total > 0 ? FormatBytes(received) + " / " + FormatBytes(total) : FormatBytes(received);
            var speed = bytesPerSecond > 0 ? "    " + FormatBytes(bytesPerSecond) + "/s" : String.Empty;
            return (stage ?? "") + "    " + percent + "%    " + size + speed;
        }

        private static string FormatBytes(double value)
        {
            if (value < 1024) return value.ToString("0") + " B";
            if (value < 1024 * 1024) return (value / 1024).ToString("0.0") + " KB";
            if (value < 1024 * 1024 * 1024) return (value / (1024 * 1024)).ToString("0.0") + " MB";
            return (value / (1024 * 1024 * 1024)).ToString("0.0") + " GB";
        }

        /// <summary>运行临时目录中的 rclone version，确认下载文件可执行且输出版本标识。</summary>
        private static string ReadVersionOutput(string executable)
        {
            var info = new ProcessStartInfo(executable, "version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            using (var process = Process.Start(info)) { var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit(10000); return process.ExitCode == 0 ? output : output + "\n" + error; }
        }

        /// <summary>判断 rclone version 输出是否包含有效的 rclone 版本标识。</summary>
        internal static bool IsValidDownloadedVersionOutput(string output) { return !String.IsNullOrWhiteSpace(output) && System.Text.RegularExpressions.Regex.IsMatch(output, "(?im)^rclone v[0-9]+\\.[0-9]+\\.[0-9]+"); }

        /// <summary>结束本工具记录的所有 rclone 挂载进程，为 Windows 替换 exe 解除文件锁。</summary>
        private static void StopMountProcesses()
        {
            var drives = new HashSet<string>(MountStore.Read().Values.Select(x => x.Drive), StringComparer.OrdinalIgnoreCase);
            foreach (var process in FindMountProcessesByDrives(drives)) { try { process.Kill(); } catch { } }
            Thread.Sleep(1500);
        }

        /// <summary>调用统一启动器，按记录自动恢复被升级暂时中断的挂载。</summary>
        private static void StartRecordedMounts()
        {
            var script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mount-rclone-remotes-hidden.vbs");
            if (!File.Exists(script)) { RestoreRecordedMounts(); return; }
            using (var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("WINDIR") + "\\System32\\wscript.exe", "//B //Nologo \"" + script + "\"") { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (!process.WaitForExit(10000)) { try { process.Kill(); } catch { } throw new InvalidOperationException("启动挂载恢复脚本超时。"); }
                if (process.ExitCode != 0) throw new InvalidOperationException("挂载恢复脚本执行失败。");
            }
            foreach (var record in MountStore.Read().Values)
                if (!WaitForDrive(record.Drive, true, 5000)) throw new InvalidOperationException("挂载恢复后盘符未出现：" + record.Drive);
        }

        /// <summary>查找占用记录盘符的 rclone 进程。</summary>
        private static List<Process> FindMountProcessesByDrives(IEnumerable<string> drives)
        {
            var result = new List<Process>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='rclone.exe'"))
                foreach (ManagementObject item in searcher.Get())
                {
                    var commandLine = Convert.ToString(item["CommandLine"]);
                    var tokens = System.Text.RegularExpressions.Regex.Matches(commandLine ?? String.Empty, "\"[^\"]*\"|\\S+").Cast<System.Text.RegularExpressions.Match>().Select(match => match.Value.Trim('\"')).ToList();
                    var mountIndex = tokens.FindIndex(value => value.Equals("mount", StringComparison.OrdinalIgnoreCase));
                    if (mountIndex < 0 || tokens.Count < mountIndex + 3) continue;
                    var drive = tokens[mountIndex + 2].TrimEnd('\\');
                    if (!drives.Contains(drive)) continue;
                    try { result.Add(Process.GetProcessById(Convert.ToInt32(item["ProcessId"]))); } catch { }
                }
            }
            catch { }
            return result;
        }

        /// <summary>新增或更新本机连接配置，不输出输入的凭据。</summary>
        /// <param name="definition">连接输入快照。</param><param name="exists">是否编辑已有连接。</param>
        /// <param name="overrideConfig">独立配置路径，可空。</param><param name="cancellation">取消信号。</param>
        internal void Save(RemoteDefinition definition, bool exists, string overrideConfig = null, CancellationToken cancellation = default(CancellationToken))
        {
            Validate(definition, exists);
            var targetConfig = overrideConfig ?? configPath;
            // 新增不能覆盖同名连接；编辑不能在外部删除后悄悄重建。
            var present = File.Exists(targetConfig) && File.ReadAllLines(targetConfig, Encoding.UTF8)
                .Any(x => String.Equals(x.Trim(), "[" + definition.Name + "]", StringComparison.OrdinalIgnoreCase));
            if (!exists && present) throw new InvalidOperationException("连接名称已存在，请使用编辑功能或更换名称。");
            if (exists && !present) throw new InvalidOperationException("连接已被删除，请刷新列表后重新操作。");
            var options = BuildOptions(definition);
            if (definition.DisplayName != null) { options.Add("description"); options.Add(definition.DisplayName); }
            if (!String.IsNullOrWhiteSpace(definition.Secret))
            {
                options.Add(IsS3(definition.Type) ? "secret_access_key" : "pass");
                // 按用户要求以明文写入 rclone.conf；rclone 同时兼容明文和 obscured: 值。
                options.Add(definition.Secret);
            }
            var args = new List<string> { "config", exists ? "update" : "create", "--", definition.Name };
            if (!exists) args.Add(TypeName(definition.Type)); else { options.Insert(0, "type"); options.Insert(1, TypeName(definition.Type)); }
            args.AddRange(options);
            var result = Run(args, 15000, overrideConfig, cancellation);
            EnsureSuccess(result, "保存本机连接失败");
        }

        /// <summary>更新已有连接并支持修改本机连接名称；失败时恢复原名称。</summary>
        /// <param name="originalName">编辑前的连接名称。</param><param name="definition">编辑后的连接。</param>
        /// <param name="cancellation">取消信号。</param>
        internal void UpdateExisting(string originalName, RemoteDefinition definition, CancellationToken cancellation = default(CancellationToken))
        {
            if (String.IsNullOrWhiteSpace(originalName)) throw new InvalidOperationException("缺少原连接名称，请刷新列表后重试。");
            Validate(definition, true);
            var renamed = !String.Equals(originalName, definition.Name, StringComparison.OrdinalIgnoreCase);
            if (!renamed) { Save(definition, true, null, cancellation); return; }
            if (FindRemote(originalName) == false) throw new InvalidOperationException("原连接不存在，请刷新列表后重试。");
            if (FindRemote(definition.Name)) throw new InvalidOperationException("新连接名称已存在，请更换名称。");
            if (MainForm.GetMounts().ContainsKey(originalName)) throw new InvalidOperationException("请先卸载原连接，再修改连接名称。");
            RenameRemoteSection(originalName, definition.Name);
            var mountRenamed = false;
            var orderRenamed = false;
            try
            {
                Save(definition, true, null, cancellation);
                MountStore.Rename(originalName, definition.Name);
                mountRenamed = true;
                ConnectionOrder.Rename(originalName, definition.Name);
                orderRenamed = true;
            }
            catch
            {
                if (orderRenamed) { try { ConnectionOrder.Rename(definition.Name, originalName); } catch { } }
                if (mountRenamed) { try { MountStore.Rename(definition.Name, originalName); } catch { } }
                try { RenameRemoteSection(definition.Name, originalName); } catch { }
                throw;
            }
        }

        /// <summary>在本机配置中判断连接名称是否存在。</summary>
        /// <param name="name">连接名称。</param><returns>是否存在。</returns>
        private bool FindRemote(string name)
        {
            return File.ReadAllLines(configPath, Encoding.UTF8).Any(x => String.Equals(x.Trim(), "[" + name + "]", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>仅替换配置节标题，保留原有选项和凭据格式。</summary>
        /// <param name="oldName">原名称。</param><param name="newName">新名称。</param>
        private void RenameRemoteSection(string oldName, string newName)
        {
            var lines = File.ReadAllLines(configPath, Encoding.UTF8).ToList();
            var header = "[" + oldName + "]";
            var index = lines.FindIndex(x => String.Equals(x.Trim(), header, StringComparison.OrdinalIgnoreCase));
            if (index < 0) throw new InvalidOperationException("原连接不存在，请刷新列表后重试。");
            lines[index] = "[" + newName + "]";
            var staged = configPath + ".rename-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllLines(staged, lines, new UTF8Encoding(false));
                if (File.Exists(configPath)) File.Replace(staged, configPath, null); else File.Move(staged, configPath);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }

        /// <summary>在独立临时配置中测试输入，绝不修改正式配置或传入的连接名称。</summary>
        /// <param name="definition">界面输入的连接快照。</param>
        /// <param name="exists">是否需要继承已保存的额外配置。</param>
        /// <param name="cancellation">取消测试的信号。</param>
        internal void TestDefinition(RemoteDefinition definition, bool exists, CancellationToken cancellation = default(CancellationToken), string originalName = null)
        {
            Validate(definition, exists);
            var testDirectory = Path.Combine(Path.GetTempPath(), "ai-hub-rclone-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDirectory);
            var testConfigPath = Path.Combine(testDirectory, "rclone.conf");
            try
            {
                if (exists)
                {
                    var saved = GetMetadata(String.IsNullOrWhiteSpace(originalName) ? definition.Name : originalName);
                    File.WriteAllLines(testConfigPath, new[] { "[" + definition.Name + "]" }.Concat(saved.Select(x => x.Key + " = " + x.Value)), new UTF8Encoding(false));
                }
                Save(definition, exists, testConfigPath, cancellation);
                EnsureSuccess(Run(new[] { "lsd", definition.Name + ":", "--contimeout", "5s", "--timeout", "10s", "--retries", "1", "--low-level-retries", "1" }, 15000, testConfigPath, cancellation), "连接测试失败");
            }
            finally
            {
                // 仅清理本次创建的唯一目录，包括 rclone 可能生成的配置备份。
                try { Directory.Delete(testDirectory, true); }
                catch { ManagerLog.Write("清理测试配置失败", definition.Name, String.Empty, "临时测试配置未能删除，请关闭占用程序后检查临时目录。"); }
            }
        }

        /// <summary>仅删除本机 rclone remote，不触及远端 bucket 或对象。</summary>
        internal void Delete(string name)
        {
            EnsureSuccess(Run(new[] { "config", "delete", name }, 10000), "删除本机连接失败");
            MountStore.Remove(name);
            ManagerLog.Write("删除连接", name, String.Empty, "success");
        }

        /// <summary>启动 rclone 挂载并确认目标盘符已实际出现。</summary>
        internal void Mount(string name, string drive) { Mount(name, drive, name); }

        /// <summary>启动带本地磁盘名称的 rclone 挂载并确认目标盘符已实际出现。</summary>
        internal void Mount(string name, string drive, string volumeName)
        {
            if (String.IsNullOrWhiteSpace(drive) || drive.Length != 2 || drive[1] != ':') throw new InvalidOperationException("请选择有效的本地盘符。");
            if (IsDrivePresent(drive)) throw new InvalidOperationException("盘符 " + drive + " 已被占用。");
            volumeName = MountStore.NormalizeVolumeName(name, volumeName);
            var args = MountArguments(configPath, name, drive, volumeName);
            var info = new ProcessStartInfo(RclonePath, JoinArguments(args))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            Process process;
            try { process = Process.Start(info); }
            catch (Exception ex)
            {
                ManagerLog.Write("启动挂载失败", name, drive, ex.Message);
                throw new InvalidOperationException("无法启动本机挂载进程。", ex);
            }
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (process.WaitForExit(1500))
            {
                Task.WaitAll(new Task[] { stdout, stderr });
                var result = new CommandResult { ExitCode = process.ExitCode, StandardOutput = stdout.Result, StandardError = stderr.Result };
                ManagerLog.Write("启动挂载失败", name, drive, result.Error);
                EnsureSuccess(result, "挂载失败");
            }
            if (!WaitForDrive(drive, true, 5000))
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                ManagerLog.Write("启动挂载失败", name, drive, "mountpoint not ready");
                throw new InvalidOperationException("挂载失败：5 秒内未出现本地盘符，请检查日志。\r\n原有挂载未被修改。");
            }
            MountStore.Set(name, drive, volumeName);
            ManagerLog.Write("启动挂载", name, drive, "success");
        }

        /// <summary>拼装挂载参数：不带 <c>--network-mode</c>，盘符按本地盘显示卷标，不再拖上网络盘的 (\\server) 后缀。</summary>
        /// <param name="configPath">rclone 配置文件。</param><param name="name">连接标识。</param><param name="drive">盘符。</param><param name="volumeName">本地磁盘名称。</param><param name="serverModtimeRemotes">需要服务器修改时间的连接；为空时读取设置。</param><returns>参数列表。</returns>
        internal static List<string> MountArguments(string configPath, string name, string drive, string volumeName, IEnumerable<string> serverModtimeRemotes = null)
        {
            var args = new List<string> { "--config", configPath, "mount", name + ":", drive, "--vfs-cache-mode", "full", "--volname", volumeName, "--log-file", OperationLogPath(name), "--log-level", "INFO" };
            // 这些连接使用服务器修改时间，避免浏览目录时逐个请求对象元数据；其他连接保持原行为。
            var remotes = serverModtimeRemotes ?? ServerModtimeRemotes;
            if (remotes != null && remotes.Any(x => String.Equals(x, name, StringComparison.OrdinalIgnoreCase))) args.Add("--use-server-modtime");
            return args;
        }

        /// <summary>将已挂载连接迁移至新盘符或新名称，失败时优先恢复原挂载。</summary>
        internal void ChangeMount(string name, string currentDrive, string targetDrive, string targetVolumeName)
        {
            var hasCurrentMount = !String.IsNullOrWhiteSpace(currentDrive) && IsDrivePresent(currentDrive);
            var oldVolumeName = hasCurrentMount ? GetDriveVolumeName(currentDrive, name) : name;
            targetVolumeName = MountStore.NormalizeVolumeName(name, targetVolumeName);
            if (hasCurrentMount && currentDrive.Equals(targetDrive, StringComparison.OrdinalIgnoreCase) && oldVolumeName.Equals(targetVolumeName, StringComparison.Ordinal)) return;
            if (hasCurrentMount)
            {
                Unmount(currentDrive);
                if (!WaitForDrive(currentDrive, false, 5000)) throw new InvalidOperationException("无法释放原盘符 " + currentDrive + "，未修改挂载。");
            }
            try { Mount(name, targetDrive, targetVolumeName); }
            catch (Exception ex)
            {
                if (hasCurrentMount)
                {
                    try { Mount(name, currentDrive, oldVolumeName); ManagerLog.Write("恢复原挂载", name, currentDrive, "success"); }
                    catch (Exception restoreError) { ManagerLog.Write("恢复原挂载失败", name, currentDrive, restoreError.Message); throw new InvalidOperationException("新挂载失败，且恢复原盘符失败，请查看日志。", ex); }
                }
                throw;
            }
        }

        /// <summary>卸载指定的本地盘符。</summary>
        internal void Unmount(string drive)
        {
            var processes = FindMountProcesses(drive);
            if (processes.Count == 0)
            {
                ManagerLog.Write("卸载失败", String.Empty, drive, "mount process not found");
                throw new InvalidOperationException("卸载本地磁盘失败：未找到对应的 rclone 挂载进程。请刷新列表后重试。");
            }
            foreach (var process in processes) { try { process.Kill(); } catch (Exception ex) { ManagerLog.Write("卸载失败", String.Empty, drive, ex.Message); } }
            if (!WaitForDrive(drive, false, 5000))
            {
                ManagerLog.Write("卸载失败", String.Empty, drive, "mountpoint still exists");
                throw new InvalidOperationException("卸载本地磁盘失败：卸载命令已返回，但盘符仍在使用。请关闭正在访问该盘的文件窗口后重试。");
            }
            MountStore.RemoveByDrive(drive);
            ManagerLog.Write("卸载", String.Empty, drive, "success");
        }

        /// <summary>按命令行中的挂载盘符查找 rclone 进程；兼容不提供 unmount 子命令的旧版本。</summary>
        private static List<Process> FindMountProcesses(string drive)
        {
            var result = new List<Process>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='rclone.exe'"))
                foreach (ManagementObject item in searcher.Get())
                {
                    var commandLine = Convert.ToString(item["CommandLine"]);
                    var tokens = System.Text.RegularExpressions.Regex.Matches(commandLine ?? String.Empty, "\"[^\"]*\"|\\S+").Cast<System.Text.RegularExpressions.Match>().Select(match => match.Value.Trim('\"')).ToList();
                    var mountIndex = tokens.FindIndex(value => value.Equals("mount", StringComparison.OrdinalIgnoreCase));
                    if (mountIndex < 0 || tokens.Count < mountIndex + 3) continue;
                    var remoteToken = tokens[mountIndex + 1];
                    var parsedDrive = tokens[mountIndex + 2].TrimEnd('\\');
                    if (!remoteToken.EndsWith(":", StringComparison.Ordinal) || !parsedDrive.Equals(drive, StringComparison.OrdinalIgnoreCase)) continue;
                    try { result.Add(Process.GetProcessById(Convert.ToInt32(item["ProcessId"]))); } catch { }
                }
            }
            catch { }
            return result;
        }

        /// <summary>等待指定盘符出现或释放，避免将启动进程误认为实际挂载成功。</summary>
        private static bool WaitForDrive(string drive, bool shouldExist, int timeoutMilliseconds)
        {
            for (var elapsed = 0; elapsed <= timeoutMilliseconds; elapsed += 200)
            {
                if (IsDrivePresent(drive) == shouldExist) return true;
                Thread.Sleep(200);
            }
            return false;
        }

        /// <summary>仅查询本地盘符登记，不访问可能失联的网络磁盘。</summary>
        /// <param name="drive">盘符。</param><returns>盘符是否已登记。</returns>
        private static bool IsDrivePresent(string drive) { return Environment.GetLogicalDrives().Any(x => String.Equals(x.TrimEnd('\\'), drive, StringComparison.OrdinalIgnoreCase)); }

        /// <summary>读取挂载记录中的磁盘名称，避免等待远端磁盘响应。</summary>
        /// <param name="drive">盘符。</param><param name="fallback">记录缺失时的名称。</param><returns>本机记录名称。</returns>
        private static string GetDriveVolumeName(string drive, string fallback)
        {
            try { var record = MountStore.Read().Values.FirstOrDefault(x => x.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase)); return record == null ? fallback : record.VolumeName; }
            catch { return fallback; }
        }

        /// <summary>返回单个连接的 rclone 文件操作日志路径，连接名不进入目录层级。</summary>
        internal static string OperationLogPath(string name)
        {
            var safeName = System.Text.RegularExpressions.Regex.Replace(name ?? "remote", "[^A-Za-z0-9_-]", "_");
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rclone-" + safeName + ".log");
        }

        internal static void EnsureSuccess(CommandResult result, string prefix)
        {
            if (!result.Success) throw new InvalidOperationException(prefix + "：" + ErrorText.ToChinese(result.TimedOut ? "timeout" : result.Error));
        }

        private static string SafeResult(Task<string> task) { return task.Status == TaskStatus.RanToCompletion ? task.Result : String.Empty; }
        private static string JoinArguments(IEnumerable<string> args) { return String.Join(" ", args.Select(Quote)); }
        private static string Quote(string value)
        {
            var text = value ?? String.Empty;
            var builder = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in text)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') builder.Append('\\', slashes * 2 + 1);
                else builder.Append('\\', slashes);
                builder.Append(character);
                slashes = 0;
            }
            builder.Append('\\', slashes * 2);
            return builder.Append('"').ToString();
        }
        private static bool IsS3(string type) { return type == "MinIO / S3 兼容" || type == "其他 S3 兼容" || type == "阿里云 OSS"; }
        private static string TypeName(string type) { return IsS3(type) ? "s3" : type == "WebDAV" ? "webdav" : type == "SFTP" ? "sftp" : "ftp"; }
        internal static List<string> BuildOptions(RemoteDefinition d)
        {
            if (IsS3(d.Type))
            {
                var provider = d.Type == "MinIO / S3 兼容" ? "Minio" : d.Type == "阿里云 OSS" ? "Alibaba" : "Other";
                var options = new List<string> { "provider", provider, "endpoint", d.Endpoint, "access_key_id", d.Account, "env_auth", "false" };
                if (d.Type == "MinIO / S3 兼容") { options.Add("v2_auth"); options.Add("true"); }
                options.Add("region"); options.Add(d.Region ?? String.Empty);
                return options;
            }
            if (d.Type == "WebDAV") return new List<string> { "url", d.Endpoint, "user", d.Account, "vendor", "other" };
            return new List<string> { "host", d.Endpoint, "user", d.Account };
        }
        private static void Validate(RemoteDefinition d, bool exists)
        {
            if (String.IsNullOrWhiteSpace(d.Name) || !System.Text.RegularExpressions.Regex.IsMatch(d.Name, "^[A-Za-z][A-Za-z0-9_-]*$")) throw new InvalidOperationException("内部连接标识不合法，请重新打开编辑窗口。");
            if (d.DisplayName != null) ValidateDisplayName(d.DisplayName);
            if (String.IsNullOrWhiteSpace(d.Endpoint) || String.IsNullOrWhiteSpace(d.Account)) throw new InvalidOperationException("请填写 Endpoint / 主机和账号 / Access Key。");
            if (!exists && String.IsNullOrWhiteSpace(d.Secret)) throw new InvalidOperationException("新增连接必须填写密码 / Secret Key。");
            if (IsS3(d.Type) || d.Type == "WebDAV")
            {
                Uri address;
                if (!Uri.TryCreate(d.Endpoint, UriKind.Absolute, out address) || (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps) || String.IsNullOrWhiteSpace(address.Host) || !String.IsNullOrEmpty(address.UserInfo))
                    throw new InvalidOperationException("Endpoint 需填写完整的 http:// 或 https:// 地址，例如 http://192.168.1.10:9000；账号密码请填在独立输入框。");
            }
            else if (d.Endpoint.Contains("://") || d.Endpoint.Contains("/") || d.Endpoint.Contains("\\") || Uri.CheckHostName(d.Endpoint) == UriHostNameType.Unknown)
                throw new InvalidOperationException("主机只填写 IP 或域名，例如 192.168.1.10；不要包含协议、端口或路径。");
        }
    }
}
