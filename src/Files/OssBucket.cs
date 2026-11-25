using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

namespace RcloneOssManager
{
    /// <summary>桶权限快照，用于显示和保存前的并发变更检查，不包含连接凭据。</summary>
    internal sealed class OssBucketState
    {
        /// <summary>private、public-read或custom；复杂授权不通过简化界面覆盖。</summary>
        internal string Mode;
        /// <summary>策略及ACL的规范化快照，仅在内存比较。</summary>
        internal string Snapshot;
        /// <summary>只读的明确原因，不含策略内容或认证字段。</summary>
        internal string Reason = "";
        /// <summary>后端返回的策略原文，供策略编辑器展示；无策略时为空。</summary>
        internal string Policy = "";
        /// <summary>后端返回的桶ACL原文，或 ACL_UNSUPPORTED: 标记，供权限明细展示。</summary>
        internal string Acl = "";
    }

    /// <summary>只读展示桶权限，仅在与界面所读快照一致时切换私有与公共只读；生命周期改动需单独确认。</summary>
    internal sealed partial class OssBucketClient
    {
        /// <summary>现有连接字段，仅内存使用，任何请求和异常均不输出凭据。</summary>
        private readonly Dictionary<string, string> metadata;
        /// <summary>已验证的桶名称，禁止对象路径。</summary>
        private readonly string bucket;
        /// <summary>阿里云OSS使用独立的ACL接口与签名。</summary>
        private readonly bool alibaba;
        /// <summary>权限接口的非敏感错误码。</summary>
        private sealed class BucketError : InvalidOperationException
        {
            /// <summary>经过白名单过滤的后端错误码。</summary>
            internal readonly string Code;
            /// <summary>构造脱敏错误，不包含请求URL、签名或认证字段。</summary>
            /// <param name="code">后端错误码。</param><param name="status">HTTP状态码。</param>
            internal BucketError(string code, int status) : base("桶权限请求失败：HTTP " + status + "（" + code + "）。请检查管理权限、区域和公共访问阻止设置。") { Code = code; }
        }

        /// <summary>绑定已有连接和桶，拒绝不支持的协议或认证方式，不创建桶。</summary>
        /// <param name="settings">已有连接字段。</param><param name="name">桶名称。</param>
        internal OssBucketClient(Dictionary<string, string> settings, string name)
        {
            metadata = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase); bucket = name;
            if (Value("type") != "s3") throw new InvalidOperationException("只有S3/OSS存储桶支持桶权限设置。");
            if (!ValidBucketName(name)) throw new ArgumentException("请选择有效存储桶，不能传入对象或目录路径。");
            if (String.IsNullOrEmpty(Value("access_key_id")) || String.IsNullOrEmpty(Value("secret_access_key"))) throw new InvalidOperationException("桶权限功能需要现有连接中的显式认证字段；不支持环境凭据或角色认证。");
            if (Value("v2_auth").Equals("true", StringComparison.OrdinalIgnoreCase) && !Value("provider").Equals("Minio", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("此读取接口不支持仅S3 V2认证的后端。");
            alibaba = Value("provider").Equals("Alibaba", StringComparison.OrdinalIgnoreCase);
            Endpoint("acl");
        }

        /// <summary>读取字段，不将凭据写入配置、界面或日志。</summary>
        /// <param name="name">字段名称。</param><returns>字段或空值。</returns>
        private string Value(string name) { string value; return metadata.TryGetValue(name, out value) ? value : ""; }

        /// <summary>该后端是否有 Bucket Policy 可编辑；阿里云 OSS 等只有桶 ACL。</summary>
        internal bool SupportsPolicy { get { return !alibaba; } }

        /// <summary>桶名称规则与 S3／OSS 一致：3—63位小写字母、数字、点或短横线，首尾为字母或数字，不含连续点。</summary>
        /// <param name="name">待校验名称。</param><returns>是否可作为桶名。</returns>
        internal static bool ValidBucketName(string name)
        {
            return name != null && System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$") && !name.Contains("..");
        }

        /// <summary>读取策略和ACL；其他账号授权、公共写或复杂策略标为自定义并拒绝覆盖。</summary>
        /// <param name="token">本次取消信号。</param><returns>权限快照。</returns>
        internal OssBucketState Read(CancellationToken token)
        {
            var policy = "";
            try { policy = Request("GET", "policy", "", null, token); }
            catch (BucketError ex) { if (ex.Code != "NoSuchBucketPolicy" && ex.Code != "NoSuchPolicy") throw; }
            var acl = ""; var aclUnsupported = false;
            try { acl = Request("GET", "acl", "", null, token); }
            catch (BucketError ex)
            {
                // MinIO及部分S3兼容服务不实现桶ACL子资源；其权限模型仅由Bucket Policy控制。
                if (alibaba || !(ex.Code == "NotImplemented" || ex.Code == "UnsupportedOperation" || ex.Code == "AccessControlListNotSupported")) throw;
                aclUnsupported = true; acl = "ACL_UNSUPPORTED:" + ex.Code;
            }
            var mode = alibaba ? AlibabaAcl(acl) : PolicyMode(policy, bucket);
            // 策略与ACL是两个独立来源，分别说明原因，避免把公共写策略误报成“ACL有额外授权”。
            var reasons = new List<string>();
            if (mode == "custom") reasons.Add(alibaba ? "OSS桶ACL不是标准的私有或公共只读。" : "Bucket Policy 含公共写、条件、Deny、多条声明或无法简化的授权。");
            if (aclUnsupported && mode != "custom") reasons.Add("后端不支持读取桶ACL；以下仅按Bucket Policy显示，无法核验策略之外的授权。");
            if (alibaba && !String.IsNullOrWhiteSpace(policy)) { mode = "custom"; reasons.Add("OSS已有桶策略，修改ACL可能与策略冲突。"); }
            if (!alibaba && !aclUnsupported && !OwnerOnlyAcl(acl)) { mode = "custom"; reasons.Add("桶ACL含非所有者或匿名授权。"); }
            return new OssBucketState { Mode = mode, Reason = String.Join(" ", reasons), Policy = policy, Acl = acl, Snapshot = CanonicalJson(policy) + "\n" + acl };
        }


        /// <summary>把界面已展示的私有或公共只读模式切换为另一种；快照变化或复杂策略一律不写。</summary>
        /// <param name="target">目标模式：private 或 public-read。</param><param name="seen">界面读取并让用户确认过的快照。</param><param name="token">取消信号。</param><returns>写后重新读取的快照。</returns>
        internal OssBucketState ApplyMode(string target, OssBucketState seen, CancellationToken token) { return ApplyMode(target, seen, false, token); }

        /// <summary>
        /// 切换私有与公共只读；快照变化即中止。<paramref name="overwriteCustom"/> 只允许由界面上单独勾选的确认传入，
        /// 表示用户明知会放弃既有自定义授权；默认 false，避免任何调用路径顺手覆盖他人加的授权。
        /// </summary>
        /// <param name="target">目标模式：private 或 public-read。</param><param name="seen">界面读取并让用户确认过的快照。</param>
        /// <param name="overwriteCustom">是否已确认覆盖自定义授权。</param><param name="token">取消信号。</param><returns>写后重新读取的快照。</returns>
        internal OssBucketState ApplyMode(string target, OssBucketState seen, bool overwriteCustom, CancellationToken token)
        {
            if (target != "private" && target != "public-read") throw new ArgumentException("只能切换为私有或公共只读。");
            if (seen == null) throw new ArgumentNullException("seen");
            if (seen.Mode == "custom" && !overwriteCustom) throw new InvalidOperationException("当前权限无法简化为私有或公共只读，界面不覆盖既有授权，请到服务端控制台修改。");
            var current = Read(token);
            // 只替换用户刚确认的那份配置，避免把别人在此期间加的授权一并删掉。
            if (current.Snapshot != seen.Snapshot) throw new InvalidOperationException("桶权限在本次操作期间已被修改，未写入任何配置，请重新读取后再确认。");
            if (alibaba) Request("PUT", "acl", "", target, token);
            else if (target == "public-read") Request("PUT", "policy", PublicPolicy(bucket), null, token);
            else
            {
                try { Request("DELETE", "policy", "", null, token); }
                catch (BucketError ex) { if (ex.Code != "NoSuchBucketPolicy" && ex.Code != "NoSuchPolicy") throw; }
            }
            var after = Read(token);
            // 权限来自策略与ACL两个来源，覆盖自定义后另一个来源可能仍让模式停留在 custom，如实说明而非宣称失败。
            if (after.Mode != target) throw new InvalidOperationException("权限请求已提交，但读回结果仍为" + after.Mode + "："
                + (after.Reason == "" ? "未返回原因" : after.Reason.TrimEnd('。', ' ') + "。") + "请到服务端控制台核对。");
            return after;
        }


        /// <summary>读取后合并桶生命周期配置，为单个对象前缀添加到期删除规则，完整保留其他规则。</summary>
        /// <param name="objectKey">桶内完整对象键。</param><param name="days">对象最后修改后经过的天数。</param><param name="token">取消信号。</param>
        internal void SetObjectExpiry(string objectKey, int days, CancellationToken token)
        {
            if (String.IsNullOrWhiteSpace(objectKey) || objectKey.StartsWith("/") || objectKey.Contains("\\") || objectKey.Split('/').Any(x => x == ".." || x == ".")) throw new ArgumentException("对象路径无效。");
            if (days < 1 || days > 3650) throw new ArgumentOutOfRangeException("days", "有效期必须为1至3650天。");
            var xml = "";
            try { xml = Request("GET", "lifecycle", "", null, token); }
            catch (BucketError ex) { if (ex.Code != "NoSuchLifecycleConfiguration") throw; }
            var id = "RcloneOssManagerExpiry-" + Hash(Encoding.UTF8.GetBytes(objectKey)).Substring(0, 24);
            var lifecycle = BuildLifecycleXml(xml, objectKey, days);
            Request("PUT", "lifecycle", lifecycle, null, token);
            var verify = Request("GET", "lifecycle", "", null, token);
            var check = Xml(verify).SelectNodes("//*[local-name()='Rule']").Cast<XmlNode>().FirstOrDefault(x => { var node = x.SelectSingleNode("*[local-name()='ID']"); return node != null && node.InnerText == id; });
            var actualPrefix = check == null ? null : check.SelectSingleNode("*[local-name()='Filter']/*[local-name()='Prefix']");
            var actualDays = check == null ? null : check.SelectSingleNode("*[local-name()='Expiration']/*[local-name()='Days']");
            if (actualPrefix == null || actualPrefix.InnerText != objectKey || actualDays == null || actualDays.InnerText != days.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw new InvalidOperationException("生命周期规则已提交，但读回核验失败。请在存储桶控制台检查，未自动回滚。");
        }

        /// <summary>纯函数合并生命周期XML，保留所有不属于本工具的已有规则。</summary>
        /// <param name="xml">原始配置，空值表示尚无配置。</param><param name="objectKey">对象完整键。</param><param name="days">到期天数。</param><returns>新生命周期XML。</returns>
        internal static string BuildLifecycleXml(string xml, string objectKey, int days)
        {
            if (String.IsNullOrWhiteSpace(objectKey) || objectKey.StartsWith("/") || objectKey.Contains("\\") || objectKey.Split('/').Any(x => x == ".." || x == ".")) throw new ArgumentException("对象路径无效。");
            if (days < 1 || days > 3650) throw new ArgumentOutOfRangeException("days");
            XmlDocument doc;
            if (String.IsNullOrWhiteSpace(xml)) { doc = new XmlDocument { XmlResolver = null }; doc.LoadXml("<LifecycleConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\" />"); }
            else doc = Xml(xml);
            if (doc.DocumentElement == null || doc.DocumentElement.LocalName != "LifecycleConfiguration") throw new InvalidOperationException("桶生命周期配置格式异常，未修改。");
            var ns = doc.DocumentElement.NamespaceURI; var id = "RcloneOssManagerExpiry-" + Hash(Encoding.UTF8.GetBytes(objectKey)).Substring(0, 24);
            foreach (XmlNode rule in doc.SelectNodes("//*[local-name()='Rule']").Cast<XmlNode>().ToList())
            {
                var idNode = rule.SelectSingleNode("*[local-name()='ID']");
                if (idNode != null && idNode.InnerText == id)
                {
                    var existingPrefix = rule.SelectSingleNode("*[local-name()='Filter']/*[local-name()='Prefix']");
                    if (existingPrefix == null || existingPrefix.InnerText != objectKey) throw new InvalidOperationException("生命周期规则ID冲突，已保护现有规则不被覆盖。");
                    rule.ParentNode.RemoveChild(rule);
                }
            }
            if (doc.SelectNodes("//*[local-name()='Rule']").Count >= 1000) throw new InvalidOperationException("存储桶已达到生命周期规则上限1000条，未修改。");
            var ruleNode = doc.CreateElement("Rule", ns); var idNodeNew = doc.CreateElement("ID", ns); idNodeNew.InnerText = id;
            var filter = doc.CreateElement("Filter", ns); var prefix = doc.CreateElement("Prefix", ns); prefix.InnerText = objectKey; filter.AppendChild(prefix);
            var status = doc.CreateElement("Status", ns); status.InnerText = "Enabled";
            var expiration = doc.CreateElement("Expiration", ns); var expiryDays = doc.CreateElement("Days", ns); expiryDays.InnerText = days.ToString(System.Globalization.CultureInfo.InvariantCulture); expiration.AppendChild(expiryDays);
            ruleNode.AppendChild(idNodeNew); ruleNode.AppendChild(filter); ruleNode.AppendChild(status); ruleNode.AppendChild(expiration); doc.DocumentElement.AppendChild(ruleNode);
            return doc.DocumentElement.OuterXml;
        }

        /// <summary>只授权匿名读取对象，不授权列桶、上传、删除或修改权限。</summary>
        /// <param name="name">桶名称。</param><returns>完整的公共只读策略。</returns>
        internal static string PublicPolicy(string name)
        {
            return new JavaScriptSerializer().Serialize(new { Version = "2012-10-17", Statement = new[] { new { Sid = "RcloneOssManagerPublicRead", Effect = "Allow", Principal = "*", Action = "s3:GetObject", Resource = "arn:aws:s3:::" + name + "/*" } } });
        }

        /// <summary>识别唯一且无条件的匿名GetObject授权，忽略说明性Sid及单元素数组表示差异。</summary>
        /// <param name="policy">后端JSON策略。</param><param name="name">桶名称。</param><returns>简化模式或custom。</returns>
        internal static string PolicyMode(string policy, string name)
        {
            if (String.IsNullOrWhiteSpace(policy)) return "private";
            var root = new JavaScriptSerializer().DeserializeObject(policy) as Dictionary<string, object>;
            if (root == null || root.Keys.Any(x => x != "Version" && x != "Id" && x != "Statement") || !root.ContainsKey("Statement")) return "custom";
            var statements = root["Statement"] as object[];
            var statement = statements == null ? root["Statement"] as Dictionary<string, object> : statements.Length == 1 ? statements[0] as Dictionary<string, object> : null;
            if (statement == null || statement.Keys.Any(x => x != "Sid" && x != "Effect" && x != "Principal" && x != "Action" && x != "Resource") ||
                !statement.ContainsKey("Effect") || !statement.ContainsKey("Principal") || !statement.ContainsKey("Action") || !statement.ContainsKey("Resource")) return "custom";
            var principal = statement["Principal"];
            var map = principal as Dictionary<string, object>;
            if (map != null) { if (map.Count != 1 || !map.ContainsKey("AWS")) return "custom"; principal = map["AWS"]; }
            // 不删条件、Deny、多条声明或额外动作；只有语义完全相同的公共只读可由简化界面接管。
            return Equals(statement["Effect"], "Allow") && SingleValue(principal, "*") && SingleValue(statement["Action"], "s3:GetObject") && SingleValue(statement["Resource"], "arn:aws:s3:::" + name + "/*") ? "public-read" : "custom";
        }

        /// <summary>匹配策略字符串或仅含一个相同字符串的数组，不接受额外授权。</summary>
        /// <param name="value">策略字段。</param><param name="expected">唯一允许值。</param><returns>是否严格匹配。</returns>
        private static bool SingleValue(object value, string expected) { var array = value as object[]; return Equals(value, expected) || (array != null && array.Length == 1 && Equals(array[0], expected)); }

        /// <summary>规范化JSON键顺序；空策略保留为空，异常策略拒绝修改。</summary>
        /// <param name="text">JSON文本。</param><returns>可比较的文本。</returns>
        internal static string CanonicalJson(string text)
        {
            if (String.IsNullOrWhiteSpace(text)) return "";
            return Canonical(new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.DeserializeObject(text));
        }

        /// <summary>递归规范化JSON值，不改变授权内容。</summary>
        /// <param name="value">JSON值。</param><returns>有序JSON。</returns>
        private static string Canonical(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map != null) return "{" + String.Join(",", map.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new JavaScriptSerializer().Serialize(x.Key) + ":" + Canonical(x.Value))) + "}";
            var array = value as object[];
            return array != null ? "[" + String.Join(",", array.Select(Canonical)) + "]" : new JavaScriptSerializer().Serialize(value);
        }

        /// <summary>安全解析XML，禁止外部实体解析。</summary>
        /// <param name="text">后端XML。</param><returns>XML文档。</returns>
        private static XmlDocument Xml(string text) { var document = new XmlDocument { XmlResolver = null }; document.LoadXml(text); return document; }

        /// <summary>只接受桶所有者全权ACL，保留所有额外授权不动；便于独立用例交叉验证。</summary>
        /// <param name="acl">S3 ACL响应。</param><returns>是否仅所有者授权。</returns>
        internal static bool OwnerOnlyAcl(string acl)
        {
            var doc = Xml(acl);
            var grants = doc.SelectNodes("//*[local-name()='Grant']");
            if (grants.Count != 1) return false;
            var permission = grants[0].SelectSingleNode("*[local-name()='Permission']");
            if (permission == null || permission.InnerText.Trim() != "FULL_CONTROL") return false;
            var grantee = grants[0].SelectSingleNode("*[local-name()='Grantee']");
            if (grantee == null) return false;
            // 指向 AllUsers／AuthenticatedUsers 组的授权是额外的匿名权限，与所有者无关。
            if (grantee.SelectSingleNode("*[local-name()='URI']") != null) return false;
            var id = grantee.SelectSingleNode("*[local-name()='ID']");
            var granteeId = (id == null ? "" : id.InnerText).Trim();
            var owner = doc.SelectSingleNode("//*[local-name()='Owner']/*[local-name()='ID']");
            var ownerId = (owner == null ? "" : owner.InnerText).Trim();
            // MinIO等兼容后端不返回账号标识：Owner ID为空且Grantee只有类型，此时这条全权声明只能是所有者本身。
            if (granteeId == "") return ownerId == "";
            return granteeId == ownerId;
        }

        /// <summary>读取OSS的桶ACL，公共写仍只显示为自定义。</summary>
        /// <param name="acl">OSS XML响应。</param><returns>模式。</returns>
        private static string AlibabaAcl(string acl) { var grant = Xml(acl).SelectSingleNode("//*[local-name()='AccessControlList']/*[local-name()='Grant']"); return grant != null && (grant.InnerText == "private" || grant.InnerText == "public-read") ? grant.InnerText : "custom"; }

        /// <summary>逐条列出策略声明与桶ACL授权，供权限界面显示明细；账号标识只保留前四位。</summary>
        /// <param name="policy">Bucket Policy 原文。</param><param name="acl">桶ACL原文或 ACL_UNSUPPORTED: 标记。</param><returns>多行明细文本。</returns>
        internal static string DescribeAccess(string policy, string acl)
        {
            var text = new StringBuilder();
            var count = OssPolicyDoc.CountStatements(policy);
            text.Append("Bucket Policy：").Append(count == 0 ? "（没有桶策略）" : count + " 条声明").Append("\r\n");
            if (count > 0)
                foreach (var line in OssPolicyDoc.Describe(policy).Split(new[] { "\r\n" }, StringSplitOptions.None)) text.Append("  ").Append(line).Append("\r\n");
            text.Append("\r\n桶 ACL：");
            if (acl != null && acl.StartsWith("ACL_UNSUPPORTED:", StringComparison.Ordinal))
                return text.Append("后端不支持读取（").Append(acl.Substring("ACL_UNSUPPORTED:".Length)).Append("），以上仅按 Bucket Policy 显示，无法核验策略之外的授权。").ToString();
            if (String.IsNullOrWhiteSpace(acl)) return text.Append("后端未返回桶ACL。").ToString();
            XmlDocument doc;
            try { doc = Xml(acl ?? ""); } catch (Exception) { return text.Append("响应无法解析。").ToString(); }
            var owner = NodeText(doc, "//*[local-name()='Owner']/*[local-name()='ID']");
            // 阿里云OSS的桶权限是一条纯文本 Grant（private／public-read）；AWS与MinIO的 Grant 内含 Grantee 子节点，
            // 直接取 InnerText 会把 URI 和 Permission 拼成一串，所以只有无子元素的 Grant 才按OSS结构解释。
            var oss = doc.SelectSingleNode("//*[local-name()='AccessControlList']/*[local-name()='Grant']");
            if (oss != null && !HasElementChild(oss))
            {
                var value = oss.InnerText.Trim();
                if (value != "") return text.Append("OSS 桶 ACL = ").Append(value).Append(owner == null ? "" : "；所有者 " + Mask(owner)).ToString();
            }
            var lines = new List<string>();
            foreach (XmlNode grant in doc.SelectNodes("//*[local-name()='Grant']"))
            {
                if (HasElementChild(grant)) lines.Add("  " + GranteeText(grant.SelectSingleNode("*[local-name()='Grantee']")) + " · " + NullTo(NodeText(grant, "*[local-name()='Permission']"), "未写明权限"));
            }
            text.Append(lines.Count == 0 ? "未返回任何授权。" : (owner == null ? "所有者标识未返回" : "所有者 " + Mask(owner)) + "，" + lines.Count + " 条授权");
            foreach (var line in lines) text.Append("\r\n").Append(line);
            return text.ToString();
        }

        private static bool HasElementChild(XmlNode node)
        {
            foreach (XmlNode child in node.ChildNodes)
                if (child.NodeType == XmlNodeType.Element) return true;
            return false;
        }

        private static string NodeText(XmlNode node, string path)
        {
            if (node == null) return null;
            var hit = node.SelectSingleNode(path);
            if (hit == null) return null;
            var value = hit.InnerText.Trim();
            return value == "" ? null : value;
        }

        private static string NullTo(string value, string fallback) { return value == null ? fallback : value; }

        /// <summary>把被授权者写成可分辨的类别，兼容组URI、账号ID与后端不返回标识三种写法。</summary>
        /// <param name="grantee">Grantee 节点，可为空。</param><returns>一行说明。</returns>
        private static string GranteeText(XmlNode grantee)
        {
            if (grantee == null) return "被授权者未写明";
            var uri = NodeText(grantee, "*[local-name()='URI']");
            if (uri != null)
            {
                if (uri.IndexOf("AllUsers", StringComparison.OrdinalIgnoreCase) >= 0) return "匿名所有人（AllUsers）";
                if (uri.IndexOf("Authenticated", StringComparison.OrdinalIgnoreCase) >= 0) return "已认证账号（AuthenticatedUsers）";
                return "未识别的授权组";
            }
            var id = NodeText(grantee, "*[local-name()='ID']");
            if (id != null) return "账号 " + Mask(id);
            if (NodeText(grantee, "*[local-name()='EmailAddress']") != null) return "邮箱账号（不显示地址）";
            // MinIO等后端把类型写在 xsi:type 属性上且账号ID为空，这条全权声明只能是所有者自身。
            return NullTo(GranteeType(grantee), "被授权者") + "（后端未返回账号标识）";
        }

        /// <summary>读取被授权者类型：AWS/MinIO 用 xsi:type 属性，部分SDK写成 Type 子元素，两种都要认。</summary>
        /// <param name="grantee">Grantee 节点。</param><returns>类型名，取不到时为空。</returns>
        private static string GranteeType(XmlNode grantee)
        {
            if (grantee == null) return null;
            if (grantee.Attributes != null)
                foreach (XmlAttribute attr in grantee.Attributes)
                    if (attr.LocalName == "type" && !String.IsNullOrWhiteSpace(attr.Value)) return attr.Value.Trim();
            return NodeText(grantee, "*[local-name()='Type']");
        }

        /// <summary>账号标识只保留前四位与长度，避免界面与日志完整外泄账号ID。</summary>
        /// <param name="value">账号ID。</param><returns>掩码文本。</returns>
        internal static string Mask(string value) { return value.Length <= 4 ? value : value.Substring(0, 4) + "…（共" + value.Length + "位）"; }

        /// <summary>构造桶权限端点；禁止URL内凭据、查询和重定向，路径保留代理前缀。</summary>
        /// <param name="resource">acl或policy子资源。</param><returns>HTTP地址。</returns>
        private Uri Endpoint(string resource)
        {
            var endpoint = Value("endpoint"); var region = Value("region"); if (region == "") region = "us-east-1";
            if (endpoint == "" && Value("provider").Equals("AWS", StringComparison.OrdinalIgnoreCase)) endpoint = "https://s3." + region + ".amazonaws.com";
            Uri address;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out address) || (address.Scheme != "http" && address.Scheme != "https") || address.UserInfo != "" || address.Query != "" || address.Fragment != "") throw new InvalidOperationException("桶权限接口要求不带用户名、查询或片段的HTTP(S)端点。");
            var builder = new UriBuilder(address);
            if (alibaba || Value("force_path_style").Equals("false", StringComparison.OrdinalIgnoreCase)) { if (!builder.Host.StartsWith(bucket + ".", StringComparison.OrdinalIgnoreCase)) builder.Host = bucket + "." + builder.Host; builder.Path = builder.Path.TrimEnd('/') + "/"; }
            else builder.Path = builder.Path.TrimEnd('/') + "/" + bucket;
            builder.Query = resource + "="; return builder.Uri;
        }

        /// <summary>发送已签名请求，仅捕获错误码；默认TLS验证不禁用，也不打印请求头。</summary>
        /// <param name="method">GET/PUT/DELETE。</param><param name="resource">子资源。</param><param name="body">策略正文。</param>
        /// <param name="acl">OSS ACL请求头，可空。</param><param name="token">取消信号。</param><returns>响应正文。</returns>
        private string Request(string method, string resource, string body, string acl, CancellationToken token)
        {
            if (resource == "acl" && method != "GET" && !alibaba) throw new InvalidOperationException("S3/MinIO 的桶权限由 Bucket Policy 管理，不写入桶 ACL。");
            if (method != "GET" && method != "PUT" && method != "DELETE") throw new ArgumentException("桶权限接口只使用 GET、PUT 和 DELETE。");
            token.ThrowIfCancellationRequested();
            var uri = Endpoint(resource); var bytes = Encoding.UTF8.GetBytes(body);
            var request = (HttpWebRequest)WebRequest.Create(uri); request.Method = method; request.AllowAutoRedirect = false; request.Timeout = 15000; request.ReadWriteTimeout = 15000;
            var contentType = resource == "lifecycle" ? "application/xml" : "application/json";
            if (body != "") request.ContentType = contentType;
            if (alibaba)
            {
                var date = DateTime.UtcNow.ToString("r", System.Globalization.CultureInfo.InvariantCulture); request.Date = DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
                var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
                if (acl != null) headers["x-oss-acl"] = acl;
                if (Value("session_token") != "") headers["x-oss-security-token"] = Value("session_token");
                foreach (var pair in headers) request.Headers[pair.Key] = pair.Value;
                var canonical = method + "\n\n" + (body == "" ? "" : contentType) + "\n" + date + "\n" + String.Join("", headers.Select(x => x.Key + ":" + x.Value + "\n")) + "/" + bucket + "/?" + resource;
                using (var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(Value("secret_access_key")))) request.Headers["Authorization"] = "OSS " + Value("access_key_id") + ":" + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
            }
            else
            {
                var headers = SignedHeaders(metadata, uri, method, bytes, DateTime.UtcNow);
                foreach (var pair in headers) if (pair.Key != "host") request.Headers[pair.Key] = pair.Value;
            }
            using (token.Register(request.Abort))
            {
                try
                {
                    if (method == "PUT") { request.ContentLength = bytes.Length; using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length); }
                    using (var response = (HttpWebResponse)request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        if ((int)response.StatusCode >= 300) throw new InvalidOperationException("桶权限接口返回重定向，未跟随；请检查端点和区域。");
                        return reader.ReadToEnd();
                    }
                }
                catch (WebException ex)
                {
                    token.ThrowIfCancellationRequested();
                    var response = ex.Response as HttpWebResponse;
                    if (response == null) throw new InvalidOperationException("无法连接桶权限接口，请检查网络、TLS证书和端点。");
                    using (response) using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        var error = "UnknownError";
                        try { var doc = Xml(reader.ReadToEnd()); var node = doc.SelectSingleNode("//*[local-name()='Code']"); if (node != null && System.Text.RegularExpressions.Regex.IsMatch(node.InnerText, "^[A-Za-z0-9]{1,80}$")) error = node.InnerText; } catch (XmlException) { }
                        throw new BucketError(error, (int)response.StatusCode);
                    }
                }
            }
        }

        /// <summary>写入用户编辑的桶策略原文；快照变化即中止，写后逐字比对读回结果。</summary>
        /// <param name="policy">已通过校验的策略文本；空内容表示删除桶策略。</param><param name="seen">界面读取并展示过的快照。</param><param name="token">取消信号。</param><returns>写后重新读取的快照。</returns>
        internal OssBucketState ApplyPolicy(string policy, OssBucketState seen, CancellationToken token)
        {
            if (alibaba) throw new InvalidOperationException("阿里云 OSS 的访问权限由桶 ACL 管理，本入口只用于支持 Bucket Policy 的 S3／MinIO 后端。");
            if (seen == null) throw new ArgumentNullException("seen");
            var desired = String.IsNullOrWhiteSpace(policy) ? "" : policy;
            if (desired != "")
            {
                // 写入门槛不依赖界面校验：正文非法或指向其他桶一律在此拦下。
                string valid;
                List<string> errors, warnings;
                if (!OssPolicyDoc.TryBuild(desired, bucket, out valid, out errors, out warnings)) throw new InvalidOperationException("策略未通过校验：" + errors[0]);
                desired = valid;
            }
            var current = Read(token);
            if (current.Snapshot != seen.Snapshot) throw new InvalidOperationException("桶权限在本次操作期间已被修改，未写入任何配置，请重新读取后再确认。");
            if (desired == "")
            {
                try { Request("DELETE", "policy", "", null, token); }
                catch (BucketError ex) { if (ex.Code != "NoSuchBucketPolicy" && ex.Code != "NoSuchPolicy") throw; }
            }
            else Request("PUT", "policy", desired, null, token);
            var after = Read(token);
            var expected = CanonicalJson(desired);
            var actual = CanonicalJson(after.Policy);
            // 服务端可能改写或拒绝部分声明，只有读回与提交逐字一致才说成功。
            if (actual != expected) throw new InvalidOperationException("策略请求已提交，但读回内容与提交不一致（原 " + Fingerprint(CanonicalJson(current.Policy)) + "，提交 " + Fingerprint(expected) + "，读回 " + Fingerprint(actual) + "），请到服务端控制台核对。");
            return after;
        }

        /// <summary>策略指纹，用于报错时定位差异而不回显策略正文。</summary>
        /// <param name="text">规范化策略文本。</param><returns>短指纹。</returns>
        private static string Fingerprint(string text)
        {
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Substring(0, 12);
        }

        /// <summary>按AWS Signature V4规范生成签名，便于独立签名器交叉验证。</summary>
        /// <param name="settings">认证和区域字段。</param><param name="uri">实际请求地址。</param><param name="method">HTTP方法。</param>
        /// <param name="body">精确正文。</param><param name="utc">签名时间。</param><returns>签名请求头，不可输出到日志。</returns>
        internal static Dictionary<string, string> SignedHeaders(Dictionary<string, string> settings, Uri uri, string method, byte[] body, DateTime utc)
        {
            string key, secret, region, session; settings.TryGetValue("access_key_id", out key); settings.TryGetValue("secret_access_key", out secret); settings.TryGetValue("region", out region); settings.TryGetValue("session_token", out session);
            if (String.IsNullOrEmpty(region)) region = "us-east-1";
            var time = utc.ToUniversalTime().ToString("yyyyMMddTHHmmssZ"); var day = time.Substring(0, 8);
            var headers = new SortedDictionary<string, string>(StringComparer.Ordinal) { { "host", uri.Authority }, { "x-amz-content-sha256", Hash(body) }, { "x-amz-date", time } };
            if (!String.IsNullOrEmpty(session)) headers["x-amz-security-token"] = session;
            var names = String.Join(";", headers.Keys);
            var canonical = method + "\n" + uri.AbsolutePath + "\n" + uri.Query.TrimStart('?') + "\n" + String.Join("", headers.Select(x => x.Key + ":" + x.Value + "\n")) + "\n" + names + "\n" + Hash(body);
            var scope = day + "/" + region + "/s3/aws4_request";
            var signing = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secret), day), region), "s3"), "aws4_request");
            var signature = Hex(Hmac(signing, "AWS4-HMAC-SHA256\n" + time + "\n" + scope + "\n" + Hash(Encoding.UTF8.GetBytes(canonical))));
            var result = headers.ToDictionary(x => x.Key, x => x.Value); result["Authorization"] = "AWS4-HMAC-SHA256 Credential=" + key + "/" + scope + ", SignedHeaders=" + names + ", Signature=" + signature; return result;
        }

        /// <summary>SHA-256摘要。</summary>
        /// <param name="bytes">正文。</param><returns>十六进制摘要。</returns>
        private static string Hash(byte[] bytes) { using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(bytes)); }
        /// <summary>HMAC-SHA256签名步骤。</summary>
        /// <param name="key">步骤密钥。</param><param name="text">签名文本。</param><returns>签名。</returns>
        private static byte[] Hmac(byte[] key, string text) { using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(Encoding.UTF8.GetBytes(text)); }
        /// <summary>字节转小写十六进制。</summary>
        /// <param name="bytes">字节。</param><returns>十六进制文本。</returns>
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
    }
}
