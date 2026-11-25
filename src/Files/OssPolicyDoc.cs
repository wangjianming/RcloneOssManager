using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace RcloneOssManager
{
    /// <summary>桶策略文档的解析、校验、缩进排版与模板生成；只处理桶级Policy，不解释条件与IAM。</summary>
    internal static class OssPolicyDoc
    {
        private static readonly string[] TopKeys = { "Version", "Id", "Statement" };
        private static readonly string[] StatementKeys = { "Sid", "Effect", "Principal", "NotPrincipal", "Action", "NotAction", "Resource", "NotResource", "Condition" };
        private static readonly string[] WriteActions = { "s3:PutObject", "s3:DeleteObject", "s3:PutObjectAcl", "s3:AbortMultipartUpload", "*" };

        /// <summary>解析并校验策略；错误必须全部消除才允许写入，警告只要求用户确认。</summary>
        /// <param name="json">用户编辑的策略文本。</param><param name="bucket">当前桶名称。</param>
        /// <param name="pretty">校验通过时的缩进文本。</param><param name="errors">阻断写入的问题。</param><param name="warnings">需要确认的风险。</param>
        /// <returns>是否可写入。</returns>
        internal static bool TryBuild(string json, string bucket, out string pretty, out List<string> errors, out List<string> warnings)
        {
            pretty = null;
            errors = new List<string>();
            warnings = new List<string>();
            if (String.IsNullOrWhiteSpace(json)) { errors.Add("策略内容为空；要取消全部桶策略请使用“清空桶策略”，不要提交空文本。"); return false; }
            object parsed;
            try { parsed = new JavaScriptSerializer().DeserializeObject(json); }
            catch (ArgumentException ex) { errors.Add("不是有效 JSON：" + ex.Message.Split('\n')[0]); return false; }
            var root = parsed as Dictionary<string, object>;
            if (root == null) { errors.Add("最外层必须是 JSON 对象。"); return false; }
            foreach (var key in root.Keys.Where(key => !TopKeys.Contains(key))) errors.Add("不支持的顶层字段：" + key + "。");
            if (!root.ContainsKey("Statement")) errors.Add("缺少 Statement 数组。");
            var version = Text(root.ContainsKey("Version") ? root["Version"] : null);
            if (version != "" && version != "2012-10-17" && version != "2008-10-17") errors.Add("Version 只接受 2012-10-17 或 2008-10-17，当前为 " + version + "。");
            var statements = Statements(root);
            if (statements == null) errors.Add("Statement 必须是对象或对象数组。");
            else
            {
                if (statements.Count == 0) warnings.Add("Statement 为空数组，效果等同于没有任何授权；建议直接清空桶策略。");
                foreach (var statement in statements) Check(statement, bucket, errors, warnings);
            }
            if (errors.Count > 0) return false;
            pretty = Indent(parsed);
            // 多条声明会命中同一条风险，重复提示只会淹没重点。
            warnings = warnings.Distinct().ToList();
            return true;
        }

        private static void Check(Dictionary<string, object> statement, string bucket, List<string> errors, List<string> warnings)
        {
            foreach (var key in statement.Keys.Where(key => !StatementKeys.Contains(key))) errors.Add("不支持的声明字段：" + key + "。");
            var effect = Text(statement.ContainsKey("Effect") ? statement["Effect"] : null);
            if (effect != "Allow" && effect != "Deny") { errors.Add("Effect 必须是 Allow 或 Deny，当前为 " + (effect == "" ? "缺失" : effect) + "。"); return; }
            if (statement.ContainsKey("Condition")) warnings.Add("存在 Condition 条件，本工具不解释条件语义，写入后实际访问结果由服务端判定。");
            if (statement.ContainsKey("NotAction") || statement.ContainsKey("NotPrincipal") || statement.ContainsKey("NotResource")) warnings.Add("存在 Not 系列字段，属于排除式授权，请自行确认范围。");
            // 空数组在 JSON 里合法但等于什么都没授权，界面删光勾选后必须拦住，不能当成有效策略。
            if (statement.ContainsKey("Action") && Collect(statement["Action"]).ToList().Count == 0) errors.Add("Action 不能是空数组，至少保留一项。");
            var resources = statement.ContainsKey("Resource") ? statement["Resource"] : statement.ContainsKey("NotResource") ? statement["NotResource"] : null;
            if (resources == null) { errors.Add("声明缺少 Resource。"); return; }
            var values = Flatten(resources).Select(Text).Where(x => x != "").ToList();
            if (values.Count == 0) { errors.Add("Resource 不能是空数组，至少保留一个桶或对象路径。"); return; }
            foreach (var arn in values)
            {
                if (arn == "*") { errors.Add("Resource 不能是裸 *，会波及其他桶。"); continue; }
                if (!arn.StartsWith("arn:aws:s3:::" + bucket, StringComparison.Ordinal) && !arn.StartsWith("arn:aws:s3:::" + bucket + "/", StringComparison.Ordinal))
                    errors.Add("Resource 指向其他位置，必须以 arn:aws:s3:::" + bucket + " 开头：" + arn);
            }
            if (effect != "Allow") return;
            var principal = statement.ContainsKey("Principal") ? statement["Principal"] : null;
            if (principal == null) { errors.Add("Allow 声明必须写明 Principal。"); return; }
            var names = Collect(principal).ToList();
            if (names.Any(x => x == "*" || x.EndsWith(":*", StringComparison.Ordinal))) warnings.Add("Principal 含 *，该声明对所有匿名账号生效。");
            var actions = Collect(statement.ContainsKey("Action") ? statement["Action"] : statement["NotAction"]).ToList();
            // 匿名可写可删是本工具默认拒绝的高风险组合，单独识别出来供界面要求逐字确认。
            if (names.Any(x => x == "*") && actions.Any(x => WriteActions.Contains(x))) warnings.Add("匿名账号可写入或删除对象，任何拿到桶地址的人都可上传与删除。");
        }

        internal static List<Dictionary<string, object>> Statements(Dictionary<string, object> root)
        {
            object value;
            if (!root.TryGetValue("Statement", out value)) return null;
            var single = value as Dictionary<string, object>;
            if (single != null) return new List<Dictionary<string, object>> { single };
            var array = value as object[];
            if (array == null) return null;
            var list = new List<Dictionary<string, object>>();
            foreach (var item in array)
            {
                var map = item as Dictionary<string, object>;
                if (map == null) return null;
                list.Add(map);
            }
            return list;
        }

        private static IEnumerable<object> Flatten(object value)
        {
            var array = value as object[];
            if (array != null) return array.SelectMany(Flatten);
            return new[] { value };
        }

        /// <summary>展开 Principal 与 Action 的所有可能写法：字符串、数组、以账号或服务为键的对象。</summary>
        /// <param name="value">策略字段值。</param><returns>叶子文本。</returns>
        private static IEnumerable<string> Collect(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map != null) return map.Values.SelectMany(Collect);
            var array = value as object[];
            if (array != null) return array.SelectMany(Collect);
            return Text(value).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('"'));
        }

        private static string Text(object value) { return value == null ? "" : value is string ? (string)value : new JavaScriptSerializer().Serialize(value); }

        /// <summary>把解析后的对象图排成固定缩进的文本，便于逐行核对与差异比较。</summary>
        /// <param name="node">解析结果节点。</param><returns>两空格缩进的 JSON。</returns>
        internal static string Indent(object node) { var text = new StringBuilder(); Write(node, text, 0); return text.ToString(); }

        private static void Write(object node, StringBuilder text, int depth)
        {
            var map = node as Dictionary<string, object>;
            if (map != null)
            {
                text.Append("{\n");
                var pairs = map.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
                for (var i = 0; i < pairs.Count; i++)
                {
                    Pad(text, depth + 1); text.Append(Serialize(pairs[i].Key)).Append(": ");
                    Write(pairs[i].Value, text, depth + 1);
                    if (i < pairs.Count - 1) text.Append(',');
                    text.Append('\n');
                }
                Pad(text, depth); text.Append('}');
                return;
            }
            var array = node as object[];
            if (array != null)
            {
                if (array.Length == 0) { text.Append("[]"); return; }
                text.Append("[\n");
                for (var i = 0; i < array.Length; i++)
                {
                    Pad(text, depth + 1);
                    // 声明数组里的对象已经展开排版，标量数组保持一行更省屏幕。
                    if (array[i] is Dictionary<string, object> || array[i] is object[]) Write(array[i], text, depth + 1);
                    else text.Append(Serialize(array[i]));
                    if (i < array.Length - 1) text.Append(',');
                    text.Append('\n');
                }
                Pad(text, depth); text.Append(']');
                return;
            }
            text.Append(Serialize(node));
        }

        private static void Pad(StringBuilder text, int depth) { text.Append(new string(' ', depth * 2)); }

        private static string Serialize(object value)
        {
            // 容器已由 Write 展开，这里只负责标量；否则标量数组会被拆成多行，横向还留大片空白。
            return new JavaScriptSerializer().Serialize(value);
        }

        /// <summary>把后端返回的策略排成缩进文本；无法解析时原样返回，供编辑器展示。</summary>
        /// <param name="json">策略原文。</param><returns>缩进文本或原文。</returns>
        internal static string Pretty(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return "";
            object parsed;
            try { parsed = new JavaScriptSerializer().DeserializeObject(json); }
            catch (ArgumentException) { return json; }
            return Indent(parsed);
        }

        /// <summary>统计声明条数，供权限明细显示规模；无法解析或没有声明返回0。</summary>
        /// <param name="json">策略原文。</param><returns>声明条数。</returns>
        internal static int CountStatements(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return 0;
            object parsed;
            try { parsed = new JavaScriptSerializer().DeserializeObject(json); } catch (ArgumentException) { return 0; }
            var root = parsed as Dictionary<string, object>;
            var statements = root == null ? null : Statements(root);
            return statements == null ? 0 : statements.Count;
        }

        /// <summary>逐条摘要策略，用于写入前的差异确认，不显示无关字段值。</summary>
        /// <param name="json">已校验的策略文本。</param><returns>每条声明一行的说明。</returns>
        internal static string Describe(string json)
        {
            if (String.IsNullOrWhiteSpace(json)) return "（没有桶策略）";
            object parsed;
            try { parsed = new JavaScriptSerializer().DeserializeObject(json); }
            catch (ArgumentException) { return "（无法解析）"; }
            var root = parsed as Dictionary<string, object>;
            var statements = root == null ? null : Statements(root);
            if (statements == null || statements.Count == 0) return "（没有声明）";
            return String.Join("\r\n", statements.Select(StatementSummary).ToArray());
        }

        /// <summary>单条声明的一行摘要，供权限明细、写入确认与编辑器声明列表共用。</summary>
        /// <param name="statement">已解析的声明。</param><returns>一行说明。</returns>
        internal static string StatementSummary(Dictionary<string, object> statement)
        {
            object value;
            var effect = statement.TryGetValue("Effect", out value) ? Text(value) : "";
            var principal = statement.TryGetValue("Principal", out value) ? PrincipalText(value) : "未写明";
            var action = statement.TryGetValue("Action", out value) ? Join(value, Short) : statement.TryGetValue("NotAction", out value) ? "非 " + Join(value, Short) : "未写明";
            var resource = statement.TryGetValue("Resource", out value) ? Join(value, Short) : statement.TryGetValue("NotResource", out value) ? "非 " + Join(value, Short) : "未写明";
            return (effect == "" ? "未写明效果" : effect) + " · " + principal + " · " + action + " · " + resource
                + (statement.ContainsKey("Condition") ? " · 含条件" : "");
        }

        private static string Join(object value, Func<string, string> map)
        {
            var items = Collect(value).Select(map).Where(x => x != "").ToList();
            return items.Count == 0 ? "（空）" : String.Join("、", items.ToArray());
        }

        /// <summary>把被授权者写成可读类别：匿名所有人／账号（掩码）／服务，不再直接输出 JSON 原文。</summary>
        /// <param name="value">Principal 字段值。</param><returns>可读说明。</returns>
        internal static string PrincipalText(object value)
        {
            if (value == null) return "未写明";
            var map = value as Dictionary<string, object>;
            if (map != null)
                return String.Join("＋", map.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
                {
                    var leaves = Leaves(x.Value);
                    // 已经说出"匿名所有人"或"账号 …"的写法不再叠加来源前缀，避免出现"账号：账号 1234…"这种重复。
                    if (leaves == "匿名所有人" || leaves.StartsWith("账号 ", StringComparison.Ordinal)) return leaves;
                    return KindName(x.Key) + "：" + leaves;
                }).ToArray());
            return Leaves(value);
        }

        private static string KindName(string key)
        {
            return key == "AWS" ? "账号" : key == "Service" ? "服务" : key;
        }

        private static string Leaves(object value)
        {
            var items = Collect(value).ToList();
            if (items.Count == 0) return "未写明";
            if (items.All(x => x == "*")) return "匿名所有人";
            // 明细要点名每个被授权者，一行放不下时最多列三个并给出总数，不能只说第一个就省略其余。
            var names = items.Take(3).Select(Name).ToArray();
            return items.Count > 3 ? String.Join("、", names) + " 等 " + items.Count + " 个" : String.Join("、", names);
        }

        /// <summary>单个被授权者标识；账号ID一律掩码，ARN 只保留主体部分。</summary>
        private static string Name(string value)
        {
            if (value == "*") return "匿名所有人";
            var match = System.Text.RegularExpressions.Regex.Match(value, "^arn:[a-z-]+:iam::([0-9]{4,}):(.+)$");
            if (match.Success) return "账号 " + OssBucketClient.Mask(match.Groups[1].Value) + " 的 " + match.Groups[2].Value;
            if (System.Text.RegularExpressions.Regex.IsMatch(value, "^[0-9]{4,}$")) return "账号 " + OssBucketClient.Mask(value);
            return value;
        }

        private static string Short(string value) { return value.Replace("arn:aws:s3:::", "").Replace("s3:", ""); }

        /// <summary>生成常用模板；匿名读写模板只作为起点，仍需用户逐字确认。</summary>
        /// <param name="kind">read、read-list、anon-rw 或 none。</param><param name="bucket">桶名称。</param><returns>缩进策略文本，none 返回空。</returns>
        internal static string Template(string kind, string bucket)
        {
            if (kind == "none") return "";
            var bucketArn = "arn:aws:s3:::" + bucket;
            if (kind == "read") return Format(new { Version = "2012-10-17", Statement = new object[] { new { Sid = "RcloneOssManagerPublicRead", Effect = "Allow", Principal = "*", Action = new[] { "s3:GetObject" }, Resource = new[] { bucketArn + "/*" } } } });
            var list = new { Sid = "RcloneOssManagerAnonymousList", Effect = "Allow", Principal = "*", Action = new[] { "s3:GetBucketLocation", "s3:ListBucket", "s3:ListBucketMultipartUploads" }, Resource = new[] { bucketArn } };
            var get = new { Sid = "RcloneOssManagerAnonymousGet", Effect = "Allow", Principal = "*", Action = new[] { "s3:GetObject" }, Resource = new[] { bucketArn + "/*" } };
            if (kind == "read-list") return Format(new { Version = "2012-10-17", Statement = new object[] { list, get } });
            var write = new { Sid = "RcloneOssManagerAnonymousWrite", Effect = "Allow", Principal = "*", Action = new[] { "s3:PutObject", "s3:DeleteObject", "s3:AbortMultipartUpload", "s3:ListMultipartUploadParts" }, Resource = new[] { bucketArn + "/*" } };
            return Format(new { Version = "2012-10-17", Statement = new object[] { list, get, write } });
        }

        /// <summary>匿名类型不是字典，先序列化再解析才能走统一的缩进排版。</summary>
        /// <param name="model">模板对象。</param><returns>缩进策略文本。</returns>
        private static string Format(object model) { return Pretty(new JavaScriptSerializer().Serialize(model)); }
    }
}
