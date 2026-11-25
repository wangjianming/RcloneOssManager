using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace RcloneOssManager
{
    /// <summary>桶策略的一条可视化声明草稿：界面只维护 Sid／Effect／Principal／Action／Resource，其余字段与未被改动的写法原样写回。</summary>
    internal sealed class PolicyDraft
    {
        internal string Sid = "";
        internal string Effect = "Allow";
        /// <summary>anonymous、aws、service、locked。</summary>
        internal string PrincipalKind = "anonymous";
        /// <summary>aws 与 service 的取值每行一个；locked 时改为展示原文供核对。</summary>
        internal string PrincipalValues = "";
        internal readonly List<string> Actions = new List<string>();
        /// <summary>完整 ARN 写法，与将要写入的正文一致，不做简写换算。</summary>
        internal readonly List<string> Resources = new List<string>();
        /// <summary>界面不维护的字段（Condition、Not 系列、未知字段），写入时逐字带回。</summary>
        internal readonly Dictionary<string, object> Preserved = new Dictionary<string, object>(StringComparer.Ordinal);
        /// <summary>超出可视化范围的写法说明，界面必须如实显示而不是静默省略。</summary>
        internal readonly List<string> Notes = new List<string>();
        /// <summary>整条无法拆解时保存的原始节点，非空即逐字写回。</summary>
        internal object Raw;
        /// <summary>解析时的原声明，用于逐字段判断是否被改动。</summary>
        internal Dictionary<string, object> Source;
        private string initialSid = "", initialEffect = "", initialPrincipal = "", initialActions = "", initialResources = "";

        /// <summary>记录当前界面形态作为“未改动”的基准。</summary>
        internal void Capture()
        {
            initialSid = Sid; initialEffect = Effect;
            initialPrincipal = PrincipalKind + "\n" + PrincipalValues;
            initialActions = String.Join("\n", Actions.ToArray());
            initialResources = String.Join("\n", Resources.ToArray());
        }

        internal bool SidChanged { get { return Sid != initialSid; } }
        internal bool EffectChanged { get { return Effect != initialEffect; } }
        internal bool PrincipalChanged { get { return PrincipalKind + "\n" + PrincipalValues != initialPrincipal; } }
        internal bool ActionsChanged { get { return String.Join("\n", Actions.ToArray()) != initialActions; } }
        internal bool ResourcesChanged { get { return String.Join("\n", Resources.ToArray()) != initialResources; } }

        /// <summary>整条声明都保持解析时的写法；新建的声明没有原文可比，一律视为需要写入。</summary>
        internal bool Unchanged { get { return Source != null && !SidChanged && !EffectChanged && !PrincipalChanged && !ActionsChanged && !ResourcesChanged; } }

        /// <summary>本条是否还能逐字段编辑；整条无法拆解时禁用界面控件。</summary>
        internal bool Editable { get { return Raw == null; } }

        /// <summary>把无法可视化的原因合成一句提示。</summary>
        internal string Note { get { return Notes.Count == 0 ? "" : String.Join("　", Notes.ToArray()); } }
    }

    /// <summary>整份 Bucket Policy 的可视化草稿。</summary>
    internal sealed class PolicyModel
    {
        /// <summary>顶层 Version；原文没有时保持为空，写回不凭空补字段。</summary>
        internal string Version = "";
        internal readonly List<PolicyDraft> Statements = new List<PolicyDraft>();
        /// <summary>顶层非 Version／Statement 字段（如 Id），写入时原样带回。</summary>
        internal readonly Dictionary<string, object> Preserved = new Dictionary<string, object>(StringComparer.Ordinal);
        internal readonly List<string> Notes = new List<string>();
        /// <summary>整份文档无法逐条拆解，界面只展示原文并禁止写入。</summary>
        internal bool Locked;

        /// <summary>顶层说明文字，空字符串表示全部字段都能表达。</summary>
        internal string Note { get { return Notes.Count == 0 ? "" : String.Join("　", Notes.ToArray()); } }
    }

    /// <summary>Bucket Policy 与可视化草稿之间的双向转换；不猜语义，表达不了的写法一律原样保留。</summary>
    internal static class OssPolicyModel
    {
        private static readonly string[] Managed = { "Sid", "Effect", "Principal", "Action", "Resource" };

        /// <summary>解析策略原文为草稿；结构超出界面能力时置 Locked 并给出原因，绝不丢内容。</summary>
        /// <param name="json">后端返回的策略原文。</param><param name="error">无法解析的原因，可为空。</param><returns>草稿模型。</returns>
        internal static PolicyModel Parse(string json, out string error)
        {
            error = null;
            var model = new PolicyModel();
            if (String.IsNullOrWhiteSpace(json)) return model;
            object parsed;
            try { parsed = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 }.DeserializeObject(json); }
            catch (ArgumentException ex) { error = "后端返回的策略不是有效 JSON：" + ex.Message.Split('\n')[0]; model.Locked = true; return model; }
            var root = parsed as Dictionary<string, object>;
            if (root == null) { error = "策略最外层不是 JSON 对象，无法逐条编辑。"; model.Locked = true; return model; }
            object version;
            if (root.TryGetValue("Version", out version))
            {
                var text = version as string;
                if (text != null) model.Version = text;
                else { model.Preserved["Version"] = version; model.Notes.Add("顶层 Version 写法无法识别，原样保留"); }
            }
            foreach (var pair in root)
            {
                if (pair.Key == "Version" || pair.Key == "Statement") continue;
                model.Preserved[pair.Key] = pair.Value;
                model.Notes.Add(pair.Key == "Id" ? "顶层 Id 原样保留" : "顶层字段 " + pair.Key + " 界面不编辑，写入校验会拒绝未知字段");
            }
            object statements;
            if (!root.TryGetValue("Statement", out statements)) return model;
            var single = statements as Dictionary<string, object>;
            if (single != null) { model.Statements.Add(DraftOf(single)); return model; }
            var array = statements as object[];
            if (array == null) { error = "顶层 Statement 既不是对象也不是数组，无法逐条编辑。"; model.Locked = true; return model; }
            foreach (var item in array) model.Statements.Add(ItemOf(item));
            return model;
        }

        /// <summary>生成将要写入的策略正文；未被改动的字段沿用原文写法。</summary>
        /// <param name="model">草稿模型。</param><param name="bucket">当前桶名称。</param><returns>缩进 JSON 文本。</returns>
        internal static string ToJson(PolicyModel model, string bucket)
        {
            var root = new Dictionary<string, object>(StringComparer.Ordinal);
            if (!String.IsNullOrWhiteSpace(model.Version)) root["Version"] = model.Version;
            foreach (var pair in model.Preserved) root[pair.Key] = pair.Value;
            root["Statement"] = model.Statements.Select(x => ItemOf(x, bucket)).ToArray();
            return OssPolicyDoc.Indent(root);
        }

        /// <summary>新建一条默认声明：匿名读取本桶全部对象，界面按用户后续修改覆盖。</summary>
        /// <param name="bucket">当前桶名称。</param><returns>已记录基准的草稿。</returns>
        internal static PolicyDraft NewDraft(string bucket)
        {
            var draft = new PolicyDraft();
            draft.Effect = "Allow";
            draft.Actions.Add("s3:GetObject");
            draft.Resources.Add("arn:aws:s3:::" + bucket + "/*");
            draft.Capture();
            return draft;
        }

        /// <summary>按将要写入的正文给出一条声明的摘要，与权限明细、写入确认共用同一套写法。</summary>
        /// <param name="draft">草稿。</param><param name="bucket">当前桶名称。</param><returns>一行说明。</returns>
        internal static string Summary(PolicyDraft draft, string bucket)
        {
            var item = ItemOf(draft, bucket);
            var map = item as Dictionary<string, object>;
            return map == null ? "（整条原样保留）" + new JavaScriptSerializer().Serialize(item) : OssPolicyDoc.StatementSummary(map);
        }

        /// <summary>整条无法拆解时的原文，界面必须显示。</summary>
        /// <param name="draft">草稿。</param><returns>JSON 文本。</returns>
        internal static string RawText(PolicyDraft draft) { return new JavaScriptSerializer().Serialize(draft.Raw); }

        private static PolicyDraft ItemOf(object item)
        {
            var map = item as Dictionary<string, object>;
            if (map != null) return DraftOf(map);
            var draft = new PolicyDraft { Raw = item };
            draft.Notes.Add("整条声明不是 JSON 对象，界面不修改并原样写回");
            draft.Capture();
            return draft;
        }

        private static PolicyDraft DraftOf(Dictionary<string, object> statement)
        {
            var draft = new PolicyDraft { Source = statement };
            foreach (var pair in statement)
                if (!Managed.Contains(pair.Key))
                {
                    draft.Preserved[pair.Key] = pair.Value;
                    draft.Notes.Add(pair.Key + " 原样保留（界面不修改）");
                }
            object value;
            if (statement.TryGetValue("Sid", out value))
            {
                var text = value as string;
                if (text != null) draft.Sid = text;
                else { draft.Preserved["Sid"] = value; draft.Notes.Add("Sid 不是文本，原样保留"); }
            }
            if (statement.TryGetValue("Effect", out value))
            {
                var text = value as string;
                if (text == "Allow" || text == "Deny") draft.Effect = text;
                else { draft.Preserved["Effect"] = value; draft.Notes.Add("Effect 写法无法识别，原样保留"); }
            }
            if (statement.TryGetValue("Action", out value)) Fill(value, "Action", draft.Actions, draft);
            if (statement.TryGetValue("Resource", out value)) Fill(value, "Resource", draft.Resources, draft);
            if (statement.TryGetValue("Principal", out value)) PrincipalOf(draft, value);
            Distinct(draft.Actions); Distinct(draft.Resources);
            draft.Capture();
            return draft;
        }

        /// <summary>把字符串或字符串数组读进列表；其他写法退回原样保留。</summary>
        private static bool Fill(object value, string key, List<string> target, PolicyDraft draft)
        {
            var leaves = Strings(value);
            if (leaves == null)
            {
                draft.Preserved[key] = value;
                draft.Notes.Add(key + " 写法超出界面范围，原样保留");
                return false;
            }
            target.AddRange(leaves);
            return true;
        }

        private static void PrincipalOf(PolicyDraft draft, object value)
        {
            var leaves = Strings(value);
            if (leaves != null && leaves.Count == 1 && leaves[0] == "*") return;
            var map = value as Dictionary<string, object>;
            if (map != null && map.Count == 1)
            {
                var key = map.Keys.First();
                var items = Strings(map[key]);
                if (items != null && (key == "AWS" || key == "Service"))
                {
                    if (items.Count == 1 && items[0] == "*") return;
                    draft.PrincipalKind = key == "AWS" ? "aws" : "service";
                    draft.PrincipalValues = String.Join("\r\n", items.ToArray());
                    return;
                }
            }
            draft.PrincipalKind = "locked";
            draft.Preserved["Principal"] = value;
            draft.PrincipalValues = new JavaScriptSerializer().Serialize(value);
            draft.Notes.Add("Principal 写法超出界面范围，原样保留");
        }

        private static object ItemOf(PolicyDraft draft, string bucket)
        {
            if (draft.Raw != null) return draft.Raw;
            var map = new Dictionary<string, object>(StringComparer.Ordinal);
            Put(map, draft, "Sid", BlankToNull(draft.Sid.Trim()), draft.SidChanged);
            Put(map, draft, "Effect", draft.Effect, draft.EffectChanged);
            Put(map, draft, "Principal", PrincipalBuilt(draft), draft.PrincipalChanged);
            Put(map, draft, "Action", Leaf(draft.Actions), draft.ActionsChanged);
            Put(map, draft, "Resource", Leaf(draft.Resources), draft.ResourcesChanged);
            foreach (var pair in draft.Preserved) map[pair.Key] = pair.Value;
            return map;
        }

        /// <summary>未改动的字段写回原文；原声明里没有该字段时不凭空补写。新建声明没有原文，一律按界面取值写入。</summary>
        private static void Put(Dictionary<string, object> target, PolicyDraft draft, string key, object built, bool changed)
        {
            if (!changed && draft.Source != null)
            {
                object original;
                if (draft.Source.TryGetValue(key, out original)) target[key] = original;
                return;
            }
            if (built != null) target[key] = built;
        }

        private static object PrincipalBuilt(PolicyDraft draft)
        {
            if (draft.PrincipalKind == "anonymous") return "*";
            if (draft.PrincipalKind == "locked") return null;
            var items = Split(draft.PrincipalValues);
            if (items.Count == 0) return null;
            var key = draft.PrincipalKind == "service" ? "Service" : "AWS";
            return new Dictionary<string, object>(StringComparer.Ordinal) { { key, Leaf(items) } };
        }

        private static object Leaf(List<string> items)
        {
            if (items.Count == 0) return new object[0];
            return items.Count == 1 ? (object)items[0] : items.ToArray();
        }

        /// <summary>把桶名或前缀补成完整 ARN；已是 arn: 或 * 时原样返回，不擅自改写用户输入。</summary>
        /// <param name="value">用户填写的资源写法。</param><param name="bucket">当前桶名称。</param><returns>完整资源值。</returns>
        internal static string ExpandResource(string value, string bucket)
        {
            var text = (value ?? "").Trim().Trim('"');
            if (text == "" || text.StartsWith("arn:", StringComparison.Ordinal) || text == "*") return text;
            if (text == bucket) return "arn:aws:s3:::" + bucket;
            if (text.StartsWith(bucket + "/", StringComparison.Ordinal)) return "arn:aws:s3:::" + text;
            return "arn:aws:s3:::" + bucket + "/" + text.TrimStart('/');
        }

        /// <summary>按行与逗号拆成去重后的非空取值。</summary>
        internal static List<string> Split(string text)
        {
            var items = new List<string>();
            if (text == null) return items;
            foreach (var raw in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
                foreach (var part in raw.Split(','))
                {
                    var value = part.Trim().Trim('"');
                    if (value != "" && !items.Contains(value)) items.Add(value);
                }
            return items;
        }

        /// <summary>读取字符串或字符串数组的叶子值；出现其他类型返回 null 表示无法表达。</summary>
        private static List<string> Strings(object value)
        {
            var text = value as string;
            if (text != null) return new List<string> { text };
            var array = value as object[];
            if (array == null) return null;
            var items = new List<string>();
            foreach (var item in array)
            {
                var leaf = item as string;
                if (leaf == null) return null;
                items.Add(leaf);
            }
            return items;
        }

        private static void Distinct(List<string> items)
        {
            var keep = items.Distinct(StringComparer.Ordinal).ToList();
            items.Clear();
            items.AddRange(keep);
        }

        private static string BlankToNull(string value) { return value == "" ? null : value; }
    }
}
