using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace RcloneOssManager
{
    /// <summary>把连接信息导出为 JSON，或从 JSON 导入创建/更新连接；不含密钥时导入会保留原密钥。</summary>
    internal static class ConnectionExport
    {
        internal sealed class Entry
        {
            public string name { get; set; }
            public string displayName { get; set; }
            public string type { get; set; }
            public string endpoint { get; set; }
            public string region { get; set; }
            public string account { get; set; }
            public string secret { get; set; }
        }

        internal sealed class Document
        {
            public int version { get; set; }
            public string exportedAt { get; set; }
            public List<Entry> connections { get; set; }
        }

        /// <summary>生成连接信息 JSON。</summary>
        /// <param name="client">用于读取配置段。</param><param name="names">要导出的连接标识。</param><param name="includeSecrets">是否附带账号与密钥明文。</param>
        /// <returns>JSON 文本。</returns>
        internal static string BuildJson(RcloneClient client, IEnumerable<string> names, bool includeSecrets)
        {
            var labels = client.GetDisplayNames();
            var document = new Document { version = 1, exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), connections = new List<Entry>() };
            foreach (var name in names)
            {
                var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try { meta = client.GetMetadata(name); }
                catch (Exception) { /* 配置段读不到时仍导出标识。 */ }
                string label;
                labels.TryGetValue(name, out label);
                document.connections.Add(new Entry
                {
                    name = name,
                    displayName = label,
                    type = InterfaceType(meta),
                    endpoint = First(meta, "endpoint", "url", "host"),
                    region = First(meta, "region"),
                    account = includeSecrets ? First(meta, "access_key_id", "user") : null,
                    secret = includeSecrets ? RevealSecret(meta) : null
                });
            }
            return new JavaScriptSerializer().Serialize(document);
        }

        /// <summary>从 JSON 导入连接，已存在的按名称更新，不存在的新建；导入前备份 rclone.conf。</summary>
        /// <param name="client">用于读写连接。</param><param name="path">JSON 文件路径。</param>
        /// <returns>面向用户的结果说明。</returns>
        internal static string Import(RcloneClient client, string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new ArgumentException("找不到要导入的 JSON 文件。");
            var document = new JavaScriptSerializer().Deserialize<Document>(File.ReadAllText(path, Encoding.UTF8));
            if (document == null || document.connections == null || document.connections.Count == 0) throw new InvalidDataException("JSON 中没有连接信息。");
            var configPath = client.ConfigurationPath;
            if (File.Exists(configPath)) File.Copy(configPath, configPath + ".bak", true);
            var existing = new HashSet<string>(client.GetRemotes(), StringComparer.OrdinalIgnoreCase);
            int created = 0, updated = 0, skipped = 0;
            var errors = new List<string>();
            foreach (var entry in document.connections)
            {
                if (entry == null || String.IsNullOrWhiteSpace(entry.name) || String.IsNullOrWhiteSpace(entry.type) || String.IsNullOrWhiteSpace(entry.endpoint)) { skipped++; continue; }
                var definition = new RemoteDefinition { Name = entry.name.Trim(), DisplayName = entry.displayName, Type = entry.type.Trim(), Endpoint = entry.endpoint.Trim(), Region = entry.region, Account = entry.account, Secret = entry.secret };
                var exists = existing.Contains(definition.Name);
                try
                {
                    client.Save(definition, exists);
                    if (exists) updated++; else { created++; existing.Add(definition.Name); }
                }
                catch (Exception ex) { errors.Add(definition.Name + "：" + ex.Message); skipped++; }
            }
            var message = "导入完成：新增 " + created + " 个，更新 " + updated + " 个，跳过 " + skipped + " 个；原 rclone.conf 已备份为 .bak。";
            if (errors.Count > 0) message += "\r\n未导入：" + String.Join("；", errors.ToArray());
            return message;
        }

        /// <summary>把 rclone.conf 的原始 type/provider 还原成界面类型。</summary>
        private static string InterfaceType(IDictionary<string, string> meta)
        {
            var type = First(meta, "type") ?? String.Empty;
            if (String.Equals(type, "s3", StringComparison.OrdinalIgnoreCase))
            {
                var provider = (First(meta, "provider") ?? String.Empty).ToLowerInvariant();
                if (provider == "minio") return "MinIO / S3 兼容";
                if (provider == "alibaba") return "阿里云 OSS";
                return "其他 S3 兼容";
            }
            if (String.Equals(type, "webdav", StringComparison.OrdinalIgnoreCase)) return "WebDAV";
            if (String.Equals(type, "sftp", StringComparison.OrdinalIgnoreCase)) return "SFTP";
            return "FTP";
        }

        private static string First(IDictionary<string, string> map, params string[] keys)
        {
            foreach (var key in keys)
            {
                string value;
                if (map.TryGetValue(key, out value) && !String.IsNullOrEmpty(value)) return value;
            }
            return null;
        }

        private static string RevealSecret(IDictionary<string, string> map)
        {
            var value = First(map, "secret_access_key", "pass");
            if (String.IsNullOrEmpty(value)) return null;
            try { return ObscureCodec.Reveal(value); }
            catch (Exception) { return value; }
        }
    }
}
