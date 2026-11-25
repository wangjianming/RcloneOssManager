using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace RcloneOssManager
{
    /// <summary>快速桶统计结果；拒绝访问或接口不可用不能伪装成零占用。</summary>
    internal sealed class OssQuickResult
    {
        internal readonly Dictionary<string, OssUsage> Buckets = new Dictionary<string, OssUsage>(StringComparer.Ordinal);
        internal string Reason = "服务端快速统计不可用；可手动统计当前对象。";
    }

    /// <summary>MinIO专有的只读数据使用接口，读取后台扫描缓存而不是遍历对象。</summary>
    internal static class OssQuickUsage
    {
        /// <summary>调用MinIO管理接口，不升级权限、不配置指标Token、不跟随重定向。</summary>
        /// <param name="settings">现有连接字段，只在内存签名。</param><param name="token">取消信号。</param><returns>统计或可用性提示。</returns>
        internal static OssQuickResult Read(Dictionary<string, string> settings, CancellationToken token)
        {
            var result = new OssQuickResult(); string provider, endpoint, key, secret;
            settings.TryGetValue("provider", out provider); settings.TryGetValue("endpoint", out endpoint);
            if (!String.Equals(provider, "Minio", StringComparison.OrdinalIgnoreCase)) { result.Reason = "该后端没有通用S3快速桶大小接口；可手动统计当前对象。"; return result; }
            settings.TryGetValue("access_key_id", out key); settings.TryGetValue("secret_access_key", out secret);
            Uri address;
            if (String.IsNullOrEmpty(key) || String.IsNullOrEmpty(secret) || !Uri.TryCreate(endpoint, UriKind.Absolute, out address) || (address.Scheme != "http" && address.Scheme != "https") || address.UserInfo != "" || address.Query != "" || address.Fragment != "") { result.Reason = "MinIO快速统计需要有效端点和现有显式认证。"; return result; }
            var uri = new Uri(address.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/minio/admin/v3/datausageinfo");
            token.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(uri); request.Method = "GET"; request.AllowAutoRedirect = false; request.Timeout = 8000; request.ReadWriteTimeout = 8000;
            foreach (var header in OssBucketClient.SignedHeaders(settings, uri, "GET", new byte[0], DateTime.UtcNow)) if (header.Key != "host") request.Headers[header.Key] = header.Value;
            using (token.Register(request.Abort))
            {
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if ((int)response.StatusCode != 200) return result;
                        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        {
                            var text = new StringBuilder(); var buffer = new char[4096]; int read;
                            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); text.Append(buffer, 0, read); if (text.Length > 8 * 1024 * 1024) throw new InvalidOperationException("统计响应过大"); }
                            return Parse(text.ToString());
                        }
                    }
                }
                catch (WebException ex)
                {
                    token.ThrowIfCancellationRequested();
                    using (var response = ex.Response as HttpWebResponse)
                        result.Reason = response != null && (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized) ? "MinIO快速统计无读取权限（需admin:DataUsageInfo）；未自动扫描全桶。" : "MinIO快速统计接口不可用或网络超时；未自动扫描全桶。";
                }
                catch (ArgumentException) { result.Reason = "MinIO统计响应格式无效，未显示为零占用。"; }
                catch (InvalidOperationException) { result.Reason = "MinIO统计响应无有效缓存，未显示为零占用。"; }
            }
            return result;
        }

        /// <summary>解析服务端扫描缓存，保留更新时间、对象数及版本口径，缺字段不猜零。</summary>
        /// <param name="json">DataUsageInfo响应。</param><returns>有效桶统计。</returns>
        internal static OssQuickResult Parse(string json)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(json);
            object value; DateTimeOffset updated;
            if (root == null || !root.TryGetValue("lastUpdate", out value) || !DateTimeOffset.TryParse(Convert.ToString(value), out updated) || updated.Year <= 1 || !root.TryGetValue("bucketsUsageInfo", out value)) throw new InvalidOperationException("缓存未就绪");
            var buckets = value as Dictionary<string, object>; if (buckets == null) throw new InvalidOperationException("桶统计缺失");
            var result = new OssQuickResult { Reason = "MinIO后台扫描缓存，更新时间 " + updated.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "；并非实时扫描结果，口径可能含历史版本。" };
            foreach (var pair in buckets)
            {
                var data = pair.Value as Dictionary<string, object>; if (data == null) throw new InvalidOperationException("桶统计无效");
                result.Buckets[pair.Key] = new OssUsage { Bytes = Number(data, "size", true), Count = Number(data, "objectsCount", true), Versions = Number(data, "versionsCount", false), DeleteMarkers = Number(data, "deleteMarkersCount", false), CheckedAt = updated.LocalDateTime, Source = "MinIO服务端扫描缓存（可能含历史版本）" };
            }
            return result;
        }

        /// <summary>严格解析非负64位整数，拒绝溢出、浮点和负数。</summary>
        /// <param name="data">统计字段。</param><param name="name">字段名。</param><param name="required">是否必填。</param><returns>整数。</returns>
        private static long Number(Dictionary<string, object> data, string name, bool required)
        {
            object value; long number;
            if (!data.TryGetValue(name, out value)) { if (!required) return -1; throw new InvalidOperationException("统计字段缺失"); }
            if (!Int64.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out number)) throw new InvalidOperationException("统计数值无效");
            return number;
        }
    }
}
