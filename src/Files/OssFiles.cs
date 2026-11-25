using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Web.Script.Serialization;

namespace RcloneOssManager
{
    /// <summary>rclone本次文件传输统计；速度是实际统计值，不表示线路总带宽。</summary>
    internal sealed class OssTransferStats
    {
        /// <summary>累计传输字节、总字节和每秒字节速率。</summary>
        internal double Bytes, Total, Speed;
        /// <summary>保持原有上传和Shell拖拽的中文进度文本兼容。</summary>
        internal string Text { get { return "已传输 " + OssFileClient.Bytes(Bytes) + (Total > 0 ? " / " + OssFileClient.Bytes(Total) + "（" + Math.Min(100, Bytes * 100 / Total).ToString("F1") + "%）" : "") + "，速度 " + OssFileClient.Bytes(Speed) + "/秒"; } }
        /// <summary>从JSON日志读取统计；普通日志或异常统计不驱动进度条。</summary>
        /// <param name="line">rclone日志行。</param><returns>有效统计或空值。</returns>
        internal static OssTransferStats Parse(string line)
        {
            try
            {
                var map = new JavaScriptSerializer { MaxJsonLength = 128000 }.Deserialize<Dictionary<string, object>>(line);
                object value; if (map == null || !map.TryGetValue("stats", out value)) return null;
                var stats = value as Dictionary<string, object>; if (stats == null) return null;
                return new OssTransferStats { Bytes = Number(stats, "bytes"), Total = Number(stats, "totalBytes"), Speed = Number(stats, "speed") };
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
        /// <summary>统计缺失、负数或非有限数值视为零，避免绘制和带宽计算异常。</summary>
        /// <param name="stats">JSON统计字段。</param><param name="key">字段名。</param><returns>非负有限数值。</returns>
        private static double Number(Dictionary<string, object> stats, string key)
        {
            object value; double number;
            return stats.TryGetValue(key, out value) && Double.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number) && number > 0 ? number : 0;
        }
    }

    /// <summary>远端单层列表项；路径是相对当前列举目录的对象名称。</summary>
    internal sealed class OssEntry
    {
        /// <summary>对象名称。</summary>
        public string Name { get; set; }
        /// <summary>相对列举路径。</summary>
        public string Path { get; set; }
        /// <summary>是否为目录或存储桶。</summary>
        public bool IsDir { get; set; }
        /// <summary>是否为存储桶。</summary>
        public bool IsBucket { get; set; }
        /// <summary>文件字节数；目录可能为负数。</summary>
        public long Size { get; set; }
        /// <summary>后端返回的修改时间。</summary>
        public string ModTime { get; set; }
        /// <summary>对象属性接口返回的存储类型、标识、内容类型、哈希与元数据；普通列举不额外读取。</summary>
        public string Tier { get; set; }
        public string ID { get; set; }
        public string MimeType { get; set; }
        public Dictionary<string, string> Hashes { get; set; }
        public Dictionary<string, string> Metadata { get; set; }
    }

    /// <summary>一次只读递归列举结果，不代表配额、历史版本或云账单的物理占用。</summary>
    internal sealed class OssUsage
    {
        /// <summary>列举到的文件数、已知字节总数及大小未知的文件数。</summary>
        public long Count { get; set; }
        public long Bytes { get; set; }
        public long Sizeless { get; set; }
        /// <summary>统计结束的本机时间；扫描不是事务一致的实时快照。</summary>
        public DateTime CheckedAt { get; set; }
        /// <summary>非空表示服务端扫描缓存；其统计口径不等同当前对象递归大小。</summary>
        internal string Source = "";
        internal long Versions, DeleteMarkers;
        /// <summary>大小未知时明确标注下界，不把未知对象计成准确的零字节。</summary>
        internal string SizeText { get { return (Sizeless > 0 ? "≥ " : "") + OssFileClient.Bytes(Bytes); } }
        /// <summary>供状态、提示及结果对话框复用的口径说明。</summary>
        internal string Summary { get { return SizeText + "（" + Bytes.ToString("N0") + " 字节），" + Count.ToString("N0") + " 个文件" + (Sizeless > 0 ? "；其中 " + Sizeless + " 个大小未知，统计不完整" : "") + "；统计于 " + CheckedAt.ToString("yyyy-MM-dd HH:mm:ss") + (Source == "" ? "；手动列举当前文件，不含历史版本" : "；" + Source + "，版本 " + (Versions < 0 ? "未提供" : Versions.ToString()) + "，删除标记 " + (DeleteMarkers < 0 ? "未提供" : DeleteMarkers.ToString()) + "；不是实时快照或配额"); } }
    }

    /// <summary>复用现有连接执行远端文件操作，不使用挂载盘符。</summary>
    internal sealed partial class OssFileClient
    {
        /// <summary>共享的现有连接客户端。</summary>
        private readonly RcloneClient client;
        /// <summary>原配置中的连接名称。</summary>
        private readonly string remote;
        /// <summary>是否启用存储桶根路径保护。</summary>
        private readonly bool bucketBased;

        /// <summary>绑定一个已存在的连接，不建立新的认证配置。</summary>
        /// <param name="connection">现有 rclone 客户端。</param>
        /// <param name="name">连接名称。</param>
        /// <param name="usesBuckets">是否需要保护存储桶根路径。</param>
        internal OssFileClient(RcloneClient connection, string name, bool usesBuckets)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            if (String.IsNullOrWhiteSpace(name) || name.Contains(":") || name.Contains("/") || name.Contains("\\") || name.Any(Char.IsControl))
                throw new ArgumentException("连接名称无效。");
            client = connection; remote = name; bucketBased = usesBuckets;
        }

        /// <summary>列出当前层级和更新时间；S3 使用服务端时间，避免逐对象读取自定义时间元数据。</summary>
        /// <param name="path">连接内相对路径，空值表示根目录。</param>
        /// <param name="token">取消信号。</param><returns>目录优先的列表。</returns>
        internal List<OssEntry> List(string path, CancellationToken token)
        {
            var args = new List<string> { "lsjson", Target(path), "--no-mimetype", "--default-time", "0001-01-01T00:00:00Z", "--contimeout", "10s", "--timeout", "30s", "--retries", "1" };
            if (bucketBased) args.Add("--use-server-modtime");
            var entries = client.ReadEntries(args, token);
            foreach (var entry in entries) { entry.IsBucket = bucketBased && String.IsNullOrEmpty(path) && entry.IsDir; if (bucketBased && entry.IsDir) entry.ModTime = null; }
            return entries.OrderByDescending(x => x.IsDir).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>只读递归统计一个桶或目录；不下载对象，不默认扫描所有桶，最多等待30分钟。</summary>
        /// <param name="path">桶或目录的连接内路径。</param><param name="token">取消信号。</param><returns>文件逻辑大小及统计完成时间。</returns>
        internal OssUsage GetUsage(string path, CancellationToken token)
        {
            ValidatePath(path);
            if (bucketBased && String.IsNullOrEmpty(path)) throw new ArgumentException("请选择一个存储桶或进入桶内目录后统计，不扫描全部桶。");
            var args = new List<string> { "size", Target(path), "--json", "--contimeout", "10s", "--timeout", "30s", "--retries", "1", "--low-level-retries", "1" };
            if (bucketBased) args.AddRange(new[] { "--s3-versions=false", "--s3-version-at=off", "--use-server-modtime" });
            var result = client.Run(args, 30 * 60 * 1000, null, token);
            // 错误输出不直接回显，避免端点或请求URL携带认证查询信息。
            if (!result.Success) throw new InvalidOperationException(result.TimedOut ? "占用统计超过30分钟，未生成完整结果。" : "占用统计失败，请检查路径、列举权限及网络；未生成完整结果。");
            token.ThrowIfCancellationRequested();
            return ParseUsage(result.StandardOutput);
        }

        /// <summary>严格校验计数、字节和未知大小字段，缺失或异常结果不能当作零占用。</summary>
        /// <param name="json">rclone size的JSON输出。</param><returns>完整统计数据。</returns>
        internal static OssUsage ParseUsage(string json)
        {
            try
            {
                var map = Serializer().Deserialize<Dictionary<string, object>>(json);
                var values = new long[3]; var keys = new[] { "count", "bytes", "sizeless" };
                for (var i = 0; i < keys.Length; i++)
                {
                    object value;
                    if (map == null || !map.TryGetValue(keys[i], out value) || value == null || !Int64.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out values[i])) throw new ArgumentException();
                }
                if (values[2] > values[0]) throw new ArgumentException();
                return new OssUsage { Count = values[0], Bytes = values[1], Sizeless = values[2], CheckedAt = DateTime.Now };
            }
            catch (ArgumentException) { throw new InvalidOperationException("占用统计结果格式无效，未显示为零占用。"); }
            catch (InvalidOperationException) { throw new InvalidOperationException("占用统计结果格式无效，未显示为零占用。"); }
        }

        /// <summary>创建受限 JSON 解析器，较大目录仍由后台线程读取。</summary>
        /// <returns>解析器。</returns>
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }; }

        /// <summary>拼接对象路径，不改变大小写或名称中的空格。</summary>
        /// <param name="parent">父路径。</param><param name="child">相对子路径。</param><returns>连接内相对路径。</returns>
        internal static string Join(string parent, string child)
        {
            ValidatePath(parent); ValidatePath(child);
            return String.IsNullOrEmpty(parent) ? child : parent + "/" + child;
        }

        /// <summary>拒绝越界路径及 Windows 分隔符，不对对象键执行本地路径规范化。</summary>
        /// <param name="path">连接内相对路径。</param>
        internal static void ValidatePath(string path)
        {
            if (path == null || path.StartsWith("/", StringComparison.Ordinal) || path.Contains("\\") || path.Any(Char.IsControl) ||
                path.Split('/').Any(x => x == "." || x == "..") || path.Contains("//") || path.EndsWith("/", StringComparison.Ordinal))
                throw new ArgumentException("路径必须是连接内相对路径，不支持反斜杠、空层级或 . / .. 路径。");
        }

        /// <summary>构造 rclone 目标，命令参数仍由现有封装统一转义。</summary>
        /// <param name="path">连接内相对路径。</param><returns>连接名和对象路径。</returns>
        internal string Target(string path) { ValidatePath(path); return remote + ":" + path; }

        /// <summary>按连接的HTTP入口生成对象绝对链接，不包含凭据或签名参数。</summary>
        /// <param name="path">连接内相对路径。</param><returns>经过路径转义的HTTP链接。</returns>
        internal string Url(string path) { return BuildUrl(client.GetMetadata(remote), path); }

        /// <summary>绑定现有桶权限接口；认证只在内存使用，不修改rclone连接配置。</summary>
        /// <param name="bucket">唯一桶名称。</param><returns>桶权限客户端。</returns>
        internal OssBucketClient Bucket(string bucket) { return new OssBucketClient(client.GetMetadata(remote), bucket); }

        /// <summary>该连接是否有 Bucket Policy 可编辑；阿里云 OSS 用桶 ACL，没有策略文档。</summary>
        internal bool PolicyEditable
        {
            get
            {
                // 只决定菜单项是否出现，读不到连接信息时按不可编辑处理；真正写入仍会重新读取并如实报错。
                try
                {
                    string provider;
                    var metadata = client.GetMetadata(remote);
                    return metadata != null && (!metadata.TryGetValue("provider", out provider) || !provider.Equals("Alibaba", StringComparison.OrdinalIgnoreCase));
                }
                catch (InvalidOperationException) { return false; }
            }
        }

        /// <summary>构造S3或WebDAV链接；无HTTP入口时拒绝虚构协议或认证信息。</summary>
        /// <param name="metadata">连接字段，仅使用协议和地址等非秘密字段。</param>
        /// <param name="path">对象相对路径。</param><returns>不带认证参数的绝对链接。</returns>
        internal static string BuildUrl(Dictionary<string, string> metadata, string path)
        {
            ValidatePath(path);
            string type, endpoint;
            metadata.TryGetValue("type", out type);
            if (type != "s3" && type != "webdav") throw new InvalidOperationException("该连接没有HTTP入口，不能生成http链接；可通过右键复制rclone路径。");
            metadata.TryGetValue(type == "webdav" ? "url" : "endpoint", out endpoint);
            Uri address;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out address) || (address.Scheme != "http" && address.Scheme != "https")) throw new InvalidOperationException("连接未设置有效HTTP地址，无法生成绝对链接。");
            // 仅拼接对象地址，不复制端点内的用户名、密码、查询令牌或URL片段。
            var builder = new UriBuilder(address) { UserName = "", Password = "", Query = "", Fragment = "" };
            string provider, style; metadata.TryGetValue("provider", out provider); metadata.TryGetValue("force_path_style", out style);
            // IP或localhost端点无法拥有子域名，即使连接按虚拟主机式配置也必须按路径式拼接，否则会生成"桶名.192.168.x.x"这类错误地址。
            System.Net.IPAddress endpointHost;
            var hostIsAddress = System.Net.IPAddress.TryParse(address.Host, out endpointHost) || String.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase);
            if (type == "s3" && !String.IsNullOrEmpty(path) && !hostIsAddress && (String.Equals(style, "false", StringComparison.OrdinalIgnoreCase) || String.Equals(provider, "Alibaba", StringComparison.OrdinalIgnoreCase)))
            {
                var slash = path.IndexOf('/'); var bucket = slash < 0 ? path : path.Substring(0, slash);
                if (!System.Text.RegularExpressions.Regex.IsMatch(bucket, "^[a-z0-9][a-z0-9.-]*$")) throw new ArgumentException("桶名称不能安全转换为域名。");
                if (!builder.Host.StartsWith(bucket + ".", StringComparison.OrdinalIgnoreCase)) builder.Host = bucket + "." + builder.Host;
                path = slash < 0 ? "" : path.Substring(slash + 1);
            }
            return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + String.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        }

        /// <summary>为存在的文件生成带签名的临时共享链接；仅支持link的后端（如S3），默认7天有效。</summary>
        /// <param name="path">文件相对路径。</param><param name="token">取消信号。</param>
        /// <returns>可分享的HTTP链接；签名属于临时凭据，只在用户明确复制时离开本机。</returns>
        internal string ShareLink(string path, CancellationToken token)
        {
            RequireObject(path);
            var source = Find(path, token);
            if (source == null || source.IsDir) throw new ArgumentException("只能为存在的文件生成共享链接。");
            var result = client.Run(new[] { "link", Target(path), "--expire", "168h" }, 60000, null, token);
            if (result.TimedOut) throw new InvalidOperationException("生成共享链接超时。");
            if (result.ExitCode != 0) throw new InvalidOperationException("生成共享链接失败：" + ErrorText.ToChinese(result.StandardError));
            var url = (result.StandardOutput ?? "").Trim();
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("后端没有返回HTTP共享链接，当前连接不支持共享。");
            return url;
        }

        /// <summary>保护根目录与存储桶；文件操作必须位于桶内。</summary>
        /// <param name="path">对象路径。</param>
        internal void RequireObject(string path)
        {
            ValidatePath(path);
            if (String.IsNullOrEmpty(path) || (bucketBased && path.IndexOf('/') < 1))
                throw new ArgumentException("请进入存储桶后操作文件，不能修改连接根目录或存储桶。");
        }

        /// <summary>创建真实目录；S3创建目录标记对象，禁止借此创建桶或覆盖现有对象。</summary>
        /// <param name="path">目录相对路径，S3必须在桶内。</param><param name="token">取消信号。</param><param name="progress">进度回调。</param>
        internal void CreateDirectory(string path, CancellationToken token, Action<string> progress)
        {
            RequireObject(path);
            if (Find(path, token) != null) throw new InvalidOperationException("该名称已存在，请更换目录名称。");
            var args = new List<string> { "mkdir", Target(path) };
            if (bucketBased) args.Add("--s3-directory-markers");
            client.RunFileCommand(args, token, progress);
            var created = Find(path, token);
            if (created == null || !created.IsDir) throw new InvalidOperationException("创建目录后未能重新列举到目录，请刷新检查后端是否支持空目录标记。");
        }

        /// <summary>在连接根目录创建存储桶；沿用 rclone 现有认证与后端适配，创建后重新列举根确认。</summary>
        /// <param name="name">桶名称。</param><param name="token">取消信号。</param><param name="progress">进度回调。</param>
        internal void CreateBucket(string name, CancellationToken token, Action<string> progress)
        {
            if (!bucketBased) throw new InvalidOperationException("只有 S3／OSS 连接支持创建存储桶。");
            if (!OssBucketClient.ValidBucketName(name))
                throw new ArgumentException("桶名称需为3—63位小写字母、数字、点或短横线，首尾必须是字母或数字，且不能含连续点。");
            if (Find(name, token) != null) throw new InvalidOperationException("该名称已存在，未创建。请更换桶名称。");
            client.RunFileCommand(new List<string> { "mkdir", Target(name) }, token, progress);
            var created = Find(name, token);
            if (created == null || !created.IsBucket) throw new InvalidOperationException("创建命令已结束，但未在桶列表中看到新桶，请刷新检查账号是否具备建桶权限。");
        }

        /// <summary>上传一个本地文件，完成后重新读取目标对象的大小。</summary>
        /// <param name="local">本地源文件。</param><param name="path">远端目标。</param>
        /// <param name="overwrite">是否明确允许覆盖。</param><param name="token">取消信号。</param><param name="progress">进度回调。</param>
        internal void UploadFile(string local, string path, bool overwrite, CancellationToken token, Action<string> progress)
        {
            RequireObject(path);
            if (!File.Exists(local)) throw new ArgumentException("上传源文件不存在。");
            if (!overwrite && Find(path, token) != null) throw new InvalidOperationException("目标名称已存在，未覆盖。请勾选允许覆盖后重试。");
            var size = new FileInfo(local).Length;
            Transfer("copyto", local, Target(path), overwrite, token, progress);
            var target = Find(path, token);
            if (target == null || target.IsDir || target.Size != size) throw new InvalidOperationException("上传命令已结束，但目标文件大小验证失败，请刷新检查。");
        }

        /// <summary>下载到同目录独立暂存文件，大小核验后再替换目标；失败或取消保留原文件。</summary>
        /// <param name="path">远端源文件。</param><param name="local">本地目标文件。</param>
        /// <param name="overwrite">是否允许覆盖。</param><param name="token">取消信号。</param><param name="progress">进度回调。</param>
        /// <param name="statistics">可选结构化统计回调，不从文字反解析数值。</param>
        /// <param name="maxBytes">预览最大字节数；普通下载默认不限制。</param>
        internal void DownloadFile(string path, string local, bool overwrite, CancellationToken token, Action<string> progress, Action<OssTransferStats> statistics = null, long maxBytes = Int64.MaxValue)
        {
            RequireObject(path);
            var source = Find(path, token);
            if (source == null || source.IsDir) throw new ArgumentException("请下载一个存在的远端文件。");
            if (source.Size < 0 || source.Size > maxBytes) throw new ArgumentException("文件超过预览限制，请使用普通下载。");
            if (statistics != null) statistics(new OssTransferStats { Total = Math.Max(0, source.Size) });
            local = System.IO.Path.GetFullPath(local);
            if (Directory.Exists(local) || (!overwrite && File.Exists(local))) throw new InvalidOperationException("本地目标已存在，未覆盖。");
            var staging = local + ".rcloneossmanager-" + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                // 独立暂存文件允许原位写入，避免杀掉子进程后遗留rclone随机.partial文件。
                var arguments = new List<string>(DownloadArguments(Target(path), staging, true));
                if (maxBytes < Int64.MaxValue) arguments.AddRange(new[] { "--max-transfer", maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "b", "--cutoff-mode", "HARD" });
                client.RunFileCommand(arguments, token, progress, statistics);
                if (!File.Exists(staging) || new FileInfo(staging).Length != source.Size) throw new InvalidOperationException("下载后的文件大小验证失败。");
                token.ThrowIfCancellationRequested();
                if (File.Exists(local))
                {
                    if (!overwrite) throw new InvalidOperationException("下载期间目标文件被其他程序创建，未覆盖。");
                    File.Replace(staging, local, null);
                }
                else File.Move(staging, local);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        /// <summary>生成固定并发度的下载参数；rclone仅对达到阈值的大对象并行分块。</summary>
        /// <param name="source">rclone源路径。</param><param name="target">暂存目标。</param><param name="immutable">禁止覆盖暂存目标。</param><returns>隔离的命令参数。</returns>
        internal static string[] DownloadArguments(string source, string target, bool immutable)
        {
            var args = new List<string> { "copyto", source, target, "--multi-thread-streams", "4", "--multi-thread-cutoff", "64Mi", "--inplace" };
            if (immutable) args.Add("--immutable");
            return args.ToArray();
        }

        /// <summary>从明确输入的HTTP(S)对象链接下载到同目录暂存文件，成功后再替换，始终禁止文件协议和URL凭据。</summary>
        /// <param name="address">HTTP或HTTPS下载地址。</param><param name="local">本地目标文件。</param><param name="overwrite">是否允许覆盖。</param><param name="token">取消信号。</param><param name="progress">rclone文本进度。</param><param name="statistics">结构化传输统计。</param>
        internal void DownloadUrl(string address, string local, bool overwrite, CancellationToken token, Action<string> progress, Action<OssTransferStats> statistics = null)
        {
            Uri uri;
            if (!Uri.TryCreate(address, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !String.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("仅允许不含用户名密码的HTTP/HTTPS地址。");
            local = System.IO.Path.GetFullPath(local);
            if (Directory.Exists(local) || (!overwrite && File.Exists(local))) throw new InvalidOperationException("本地目标已存在，未覆盖。");
            var staging = local + ".rcloneossmanager-" + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                try { client.RunFileCommand(new[] { "copyurl", address, staging, "--immutable", "--inplace" }, token, progress, statistics); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { throw new InvalidOperationException("HTTP下载失败；为保护地址中的查询参数，详细错误已省略。"); }
                if (!File.Exists(staging)) throw new InvalidOperationException("HTTP下载结束后未生成暂存文件。");
                token.ThrowIfCancellationRequested();
                if (File.Exists(local)) { if (!overwrite) throw new InvalidOperationException("下载期间目标文件被其他程序创建，未覆盖。"); File.Replace(staging, local, null); }
                else File.Move(staging, local);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        /// <summary>上传文件夹内容，随后只读逐文件核对目标大小，不执行同步删除。</summary>
        /// <param name="local">本地目录。</param><param name="path">远端目录。</param>
        /// <param name="overwrite">是否允许覆盖。</param><param name="token">取消信号。</param><param name="progress">进度回调。</param>
        internal void UploadDirectory(string local, string path, bool overwrite, CancellationToken token, Action<string> progress)
        {
            RequireObject(path);
            if (!Directory.Exists(local)) throw new ArgumentException("上传目录不存在。");
            Transfer("copy", local, Target(path), overwrite, token, progress);
            if (progress != null) progress("上传已结束，正在逐文件核对目标大小…");
            client.RunFileCommand(new[] { "check", local, Target(path), "--one-way", "--size-only" }, token, progress);
        }

        /// <summary>先复制、核验新对象，再删除旧对象；禁止覆盖已有名称及目录改名。</summary>
        /// <param name="path">原对象路径。</param><param name="newPath">同目录的新对象路径。</param>
        /// <param name="token">取消信号。</param><param name="progress">进度回调。</param>
        internal void RenameFile(string path, string newPath, CancellationToken token, Action<string> progress)
        {
            RequireObject(path); RequireObject(newPath);
            if (String.Equals(path, newPath, StringComparison.Ordinal)) throw new ArgumentException("新旧名称相同。");
            var parent = path.LastIndexOf('/'); var newParent = newPath.LastIndexOf('/');
            if ((parent < 0 ? "" : path.Substring(0, parent)) != (newParent < 0 ? "" : newPath.Substring(0, newParent)))
                throw new ArgumentException("第一版只支持同目录文件重命名。");
            var source = Find(path, token);
            if (source == null || source.IsDir) throw new ArgumentException("第一版只支持单文件重命名。");
            if (Find(newPath, token) != null) throw new InvalidOperationException("新名称已存在，未覆盖。");
            Transfer("copyto", Target(path), Target(newPath), false, token, progress);
            var target = Find(newPath, token);
            if (target == null || target.IsDir || target.Size != source.Size) throw new InvalidOperationException("新对象验证失败，原对象未删除。");
            token.ThrowIfCancellationRequested();
            client.RunFileCommand(new[] { "deletefile", Target(path) }, token, progress);
            if (Find(path, token) != null) throw new InvalidOperationException("新对象已生成，但旧对象仍存在，请刷新检查。");
        }

        /// <summary>仅删除存在的单文件，完成后重新列举确认；禁止目录和存储桶删除。</summary>
        /// <param name="path">选中的对象路径。</param><param name="token">取消信号。</param><param name="progress">进度回调。</param>
        internal void DeleteFile(string path, CancellationToken token, Action<string> progress)
        {
            RequireObject(path);
            var entry = Find(path, token);
            if (entry == null || entry.IsDir) throw new ArgumentException("只允许删除存在的单文件，不支持删除目录或存储桶。");
            client.RunFileCommand(new[] { "deletefile", Target(path) }, token, progress);
            if (Find(path, token) != null) throw new InvalidOperationException("删除命令已结束，但目标仍存在，请刷新检查。");
        }

        /// <summary>只删除明确选中的子目录，递归范围以路径分隔符界定；根目录和桶始终拒绝。</summary>
        /// <param name="path">已确认的目录路径。</param><param name="token">取消信号。</param><param name="progress">进度回调。</param>
        internal void DeleteDirectory(string path, CancellationToken token, Action<string> progress)
        {
            RequireObject(path);
            var entry = Find(path, token);
            if (entry == null || !entry.IsDir || entry.IsBucket) throw new ArgumentException("只能删除明确存在的子目录，不能删除存储桶。");
            client.RunFileCommand(new[] { "purge", Target(path).TrimEnd('/') + "/" }, token, progress);
            if (Find(path, token) != null) throw new InvalidOperationException("目录删除后仍有对象或被并发写入，请刷新检查。");
        }

        /// <summary>在父目录中精确查找对象，避免 S3 stat 将缺失文件伪装为空目录。</summary>
        /// <param name="path">对象路径。</param><param name="token">取消信号。</param><returns>对象或空值。</returns>
        internal OssEntry Find(string path, CancellationToken token)
        {
            ValidatePath(path);
            var index = path.LastIndexOf('/');
            var parent = index < 0 ? "" : path.Substring(0, index);
            var name = index < 0 ? path : path.Substring(index + 1);
            return List(parent, token).FirstOrDefault(x => String.Equals(x.Name, name, StringComparison.Ordinal));
        }

        /// <summary>执行拷贝并实施覆盖策略；不删除目标多余文件。</summary>
        /// <param name="command">copy 或 copyto。</param><param name="source">源路径。</param><param name="destination">目标路径。</param>
        /// <param name="overwrite">是否允许覆盖。</param><param name="token">取消信号。</param><param name="progress">进度回调。</param>
        private void Transfer(string command, string source, string destination, bool overwrite, CancellationToken token, Action<string> progress)
        {
            var args = new List<string> { command, source, destination, overwrite ? "--ignore-times" : "--immutable" };
            if (command == "copy") { args.Add("--create-empty-src-dirs"); if (bucketBased) args.Add("--s3-directory-markers"); }
            client.RunFileCommand(args, token, progress);
        }

        /// <summary>将真实传输统计转换成中文，未获得统计时返回空值。</summary>
        /// <param name="line">rclone JSON 日志行。</param><returns>进度文本。</returns>
        internal static string ProgressText(string line)
        {
            var stats = OssTransferStats.Parse(line); return stats == null ? "" : stats.Text;
        }

        /// <summary>格式化文件字节数，不宣称未经测量的速度。</summary>
        /// <param name="value">字节数。</param><returns>可读文本。</returns>
        internal static string Bytes(double value)
        {
            var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" }; var unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "F0" : "F1") + " " + units[unit];
        }
    }

    internal sealed partial class RcloneClient
    {
        /// <summary>持续读取传输统计；取消只终止本次子进程，不影响挂载。</summary>
        /// <param name="arguments">操作参数。</param><param name="token">取消信号。</param><param name="progress">中文进度回调。</param>
        /// <param name="statistics">可选结构化统计，仅本次子进程的实际速率。</param>
        internal void RunFileCommand(IEnumerable<string> arguments, CancellationToken token, Action<string> progress, Action<OssTransferStats> statistics = null)
        {
            token.ThrowIfCancellationRequested();
            var args = new List<string> { "--config", configPath }; args.AddRange(arguments);
            args.AddRange(new[] { "--use-json-log", "--stats", "1s", "--stats-log-level", "NOTICE", "--log-level", "NOTICE", "--contimeout", "10s", "--timeout", "60s", "--retries", "1" });
            var info = new ProcessStartInfo(RclonePath, JoinArguments(args)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                // 只保留错误末尾，防止大批量任务将完整日志堆积在内存中。
                var errors = new StringBuilder();
                var reader = Task.Run(async () =>
                {
                    string line;
                    while ((line = await process.StandardError.ReadLineAsync()) != null)
                    {
                        var stats = OssTransferStats.Parse(line);
                        if (stats != null && statistics != null) statistics(stats);
                        var text = stats == null ? "" : stats.Text;
                        if (!String.IsNullOrEmpty(text) && progress != null) progress(text);
                        if (line.IndexOf("\"error\"", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            errors.AppendLine(line);
                            if (errors.Length > 12000) errors.Remove(0, errors.Length - 12000);
                        }
                    }
                });
                while (!process.WaitForExit(100))
                {
                    if (!token.IsCancellationRequested) continue;
                    try { process.Kill(); process.WaitForExit(); } catch (InvalidOperationException) { }
                    Task.WaitAll(output, reader);
                    token.ThrowIfCancellationRequested();
                }
                Task.WaitAll(output, reader);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("文件操作失败：" + (errors.Length == 0 ? "退出码 " + process.ExitCode : ErrorText.ToChinese(errors.ToString())));
            }
        }
    }
}
