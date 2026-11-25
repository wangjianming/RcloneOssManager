using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RcloneOssManager
{
    internal sealed partial class OssFileClient
    {
        /// <summary>展开所选目录到安全的相对层级，保留空目录；不下载连接根或整个桶。</summary>
        /// <param name="parent">当前远端目录。</param><param name="selection">所选文件或目录。</param><param name="token">取消信号。</param><returns>包含目录与文件的完整下载计划。</returns>
        internal List<OssEntry> ExpandDownloads(string parent, IEnumerable<OssEntry> selection, CancellationToken token)
        {
            var plan = new List<OssEntry>();
            foreach (var entry in selection)
            {
                token.ThrowIfCancellationRequested();
                var path = Join(parent, entry.Path); RequireObject(path);
                plan.Add(new OssEntry { Name = entry.Name, Path = entry.Path, Size = entry.Size, IsDir = entry.IsDir });
                if (!entry.IsDir) continue;
                var args = new List<string> { "lsjson", Target(path), "--recursive", "--no-mimetype", "--no-modtime", "--contimeout", "10s", "--timeout", "30s", "--retries", "1" };
                if (bucketBased) args.AddRange(new[] { "--s3-versions=false", "--s3-version-at=off" });
                foreach (var child in client.ReadEntries(args, token))
                    plan.Add(new OssEntry { Name = Join(entry.Name, child.Path), Path = Join(entry.Path, child.Path), Size = child.Size, IsDir = child.IsDir });
            }
            // 全批大小写与文件/目录前缀冲突要在写入前识别，不能下载一半才发现越界。
            var names = new Dictionary<string, OssEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in plan)
            {
                var local = AppDomain.CurrentDomain.BaseDirectory;
                foreach (var segment in entry.Name.Split('/')) local = OssFileForm.LocalFile(local, segment);
                if (names.ContainsKey(entry.Name)) throw new ArgumentException("所选项目存在重叠或Windows大小写冲突：" + entry.Name);
                names.Add(entry.Name, entry);
            }
            foreach (var entry in plan)
            {
                var at = entry.Name.IndexOf('/');
                while (at >= 0)
                {
                    OssEntry ancestor;
                    if (names.TryGetValue(entry.Name.Substring(0, at), out ancestor) && !ancestor.IsDir) throw new ArgumentException("文件与目录路径冲突：" + entry.Name);
                    at = entry.Name.IndexOf('/', at + 1);
                }
            }
            return plan.OrderByDescending(x => x.IsDir).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>显式读取单文件元数据与哈希；不把不存在文件返回的虚拟目录当作对象。</summary>
        /// <param name="path">完整对象路径。</param><param name="token">取消信号。</param><returns>可选择复制的属性文本。</returns>
        internal string GetProperties(string path, CancellationToken token)
        {
            RequireObject(path);
            var args = new List<string> { "lsjson", Target(path), "--stat", "--metadata", "--hash", "--contimeout", "10s", "--timeout", "30s", "--retries", "1" };
            if (bucketBased) args.Add("--use-server-modtime");
            var response = client.Run(args, 60000, null, token);
            if (!response.Success) throw new InvalidOperationException("无法读取对象属性，请检查读取权限和网络。");
            var entry = Serializer().Deserialize<OssEntry>(response.StandardOutput);
            if (entry == null || entry.IsDir) throw new InvalidOperationException("对象不存在或是目录，未返回文件属性。");
            var text = "路径：" + Target(path) + "\r\n大小：" + Bytes(entry.Size) + "（" + entry.Size + " 字节）\r\n修改时间：" + OssFileForm.FormatTime(entry.ModTime) + "\r\n存储类型：" + (entry.Tier ?? "未提供") + "\r\n内容类型：" + (entry.MimeType ?? "未提供") + "\r\n对象标识：" + (entry.ID ?? "未提供");
            if (entry.Hashes != null) foreach (var pair in entry.Hashes) text += "\r\n" + pair.Key + "：" + pair.Value;
            if (entry.Metadata != null) foreach (var pair in entry.Metadata)
            {
                // 自定义元数据也可能包含认证信息，不将敏感命名字段暴露到属性窗口。
                if (System.Text.RegularExpressions.Regex.IsMatch(pair.Key, "secret|token|password|credential|authorization|cookie|signature|api.?key", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                text += "\r\n" + pair.Key + "：" + pair.Value;
            }
            return text;
        }

        /// <summary>只对明确配置为MinIO的连接读取服务端缓存，普通S3不会尝试集群管理接口。</summary>
        /// <param name="token">取消信号。</param><returns>快速查询结果及可用性原因。</returns>
        internal OssQuickResult QuickUsage(CancellationToken token) { return OssQuickUsage.Read(client.GetMetadata(remote), token); }
    }

    internal sealed partial class RcloneClient
    {
        /// <summary>逐行读取rclone列表，避免同时保留巨大的完整JSON字符串；后台取消只结束本次列举。</summary>
        /// <param name="arguments">lsjson命令参数。</param><param name="token">取消信号。</param><returns>列表模型，界面另按500项分页。</returns>
        internal List<OssEntry> ReadEntries(IEnumerable<string> arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var args = new List<string> { "--config", configPath }; args.AddRange(arguments);
            var info = new ProcessStartInfo(RclonePath, JoinArguments(args)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            using (var process = Process.Start(info))
            {
                var errors = process.StandardError.ReadToEndAsync();
                var output = Task.Run(() =>
                {
                    var entries = new List<OssEntry>(); var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
                    string line;
                    while ((line = process.StandardOutput.ReadLine()) != null)
                    {
                        token.ThrowIfCancellationRequested();
                        line = line.Trim().TrimEnd(',');
                        if (line == "[" || line == "]" || line == "[]" || line == "") continue;
                        var entry = serializer.Deserialize<OssEntry>(line);
                        if (entry != null) entries.Add(entry);
                        if (entries.Count > 250000) throw new InvalidOperationException("单层列表超过25万项，请缩小到子目录后浏览。");
                    }
                    return entries;
                });
                var watch = Stopwatch.StartNew();
                try
                {
                    while (!process.WaitForExit(100))
                    {
                        if (output.IsFaulted) throw new InvalidOperationException("列举内容异常或超过安全限制，请缩小目录范围。");
                        token.ThrowIfCancellationRequested();
                        if (watch.Elapsed.TotalMinutes > 30) throw new InvalidOperationException("列举超过30分钟，请缩小目录范围。");
                    }
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0) throw new InvalidOperationException("加载目录失败，请检查列举权限、路径及网络。");
                    return output.GetAwaiter().GetResult();
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
                    try { Task.WaitAll(new Task[] { output, errors }, 3000); } catch (AggregateException) { }
                }
            }
        }
    }
}
