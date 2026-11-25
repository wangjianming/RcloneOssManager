using System;
using System.Collections.Generic;
using System.Text;

namespace RcloneOssManager
{
    /// <summary>把连接元数据导出为 CSV；只含名称、类型、端点等公开字段，不含账号与密钥。</summary>
    internal static class ConnectionExport
    {
        /// <summary>按给定连接顺序生成 CSV 文本。</summary>
        /// <param name="client">用于读取显示名与配置段。</param><param name="names">要导出的连接标识。</param><param name="mounts">连接名到当前盘符的映射，可空。</param><param name="includeSecrets">是否附带账号与密钥明文。</param>
        /// <returns>带表头的 CSV 文本。</returns>
        internal static string BuildCsv(RcloneClient client, IEnumerable<string> names, IDictionary<string, string> mounts, bool includeSecrets = false)
        {
            var labels = client.GetDisplayNames();
            var builder = new StringBuilder();
            builder.AppendLine(includeSecrets ? "名称,显示名,类型,端点,区域,当前盘符,账号,密钥" : "名称,显示名,类型,端点,区域,当前盘符");
            foreach (var name in names)
            {
                var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try { meta = client.GetMetadata(name); }
                catch (Exception) { /* 配置段读不到时仍导出标识与挂载状态。 */ }
                string label, drive;
                labels.TryGetValue(name, out label);
                if (mounts == null || !mounts.TryGetValue(name, out drive)) drive = null;
                var cells = new List<string>
                {
                    Cell(name),
                    Cell(label),
                    Cell(First(meta, "type")),
                    Cell(First(meta, "endpoint")),
                    Cell(First(meta, "region")),
                    Cell(drive)
                };
                if (includeSecrets)
                {
                    cells.Add(Cell(First(meta, "access_key_id", "user")));
                    cells.Add(Cell(RevealSecret(meta)));
                }
                builder.AppendLine(String.Join(",", cells));
            }
            return builder.ToString();
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

        private static string Cell(string value)
        {
            if (String.IsNullOrEmpty(value)) return "";
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
