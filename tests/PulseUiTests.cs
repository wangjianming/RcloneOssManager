using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RcloneOssManagerTester
{
    /// <summary>隔离验收：只覆盖本轮改动的窗口几何、单实例通道、配色与文件管理器解析。</summary>
    internal static class Program
    {
        private static int passed;
        private static string productPath;
        private static readonly List<string> failures = new List<string>();

        [STAThread]
        private static int Main(string[] argv)
        {
            var group = argv != null && argv.Length > 0 ? argv[0] : "core";
            var output = argv != null && argv.Length > 1 ? argv[1] : Path.Combine(Path.GetTempPath(), group + "-results.json");
            productPath = argv != null && argv.Length > 2 ? argv[2] : null;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (group == "core") RunCore();
                else if (group == "interaction") RunInteraction();
                else if (group == "bundle") RunBundle();
                else if (group == "shot") RunShot();
                else { Console.Error.WriteLine("未知测试组：" + group); return 2; }
            }
            catch (Exception ex)
            {
                failures.Add("测试组异常：" + ex.GetType().Name + " " + ex.Message);
            }
            var result = new Dictionary<string, object>();
            result["group"] = group;
            result["passed"] = passed;
            result["failed"] = failures.Count;
            result["failures"] = failures.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
            Console.WriteLine(group + ": passed=" + passed + " failed=" + failures.Count);
            foreach (var item in failures) Console.WriteLine("  FAIL " + item);
            return failures.Count == 0 ? 0 : 1;
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; return; }
            failures.Add(detail == null ? name : name + " — " + detail);
        }

        private static void Check(string name, bool ok) { Check(name, ok, null); }

        /// <summary>按子串定位正文里的着色类别，找不到返回 null，用来断言配色分块落在正确的区间上。</summary>
        /// <param name="text">JSON 正文。</param>
        private static Func<string, RcloneOssManager.JsonTokenKind?> BuildKindFinder(string text)
        {
            var tokens = RcloneOssManager.JsonColorizer.Scan(text);
            return delegate(string needle)
            {
                var at = text.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0) return null;
                foreach (var token in tokens) if (token.Start <= at && at < token.Start + token.Length) return token.Kind;
                return null;
            };
        }

        // ---------------- core ----------------

        private static void RunCore()
        {
            // 几何：屏幕可达时保持原样
            var areas = new[] { new Rectangle(0, 0, 1920, 1040), new Rectangle(1920, 0, 1920, 1040) };
            var keep = RcloneOssManager.WindowGeometry.Clamp(new Rectangle(100, 100, 1080, 680), areas, new Size(960, 600));
            Check("几何-可见位置保持", keep.Left == 100 && keep.Top == 100 && keep.Width == 1080 && keep.Height == 680, keep.ToString());

            // 几何：完全在屏幕外时回到主屏居中
            var rescued = RcloneOssManager.WindowGeometry.Clamp(new Rectangle(9000, 9000, 1080, 680), areas, new Size(960, 600));
            Check("几何-离屏回到可见区", RcloneOssManager.WindowGeometry.Reachable(rescued, areas), rescued.ToString());
            Check("几何-离屏居中宽度", rescued.Width == 1080 && rescued.Height == 680, rescued.ToString());

            // 几何：超大尺寸被裁到工作区
            var huge = RcloneOssManager.WindowGeometry.Clamp(new Rectangle(0, 0, 5000, 4000), areas, new Size(960, 600));
            Check("几何-超大尺寸裁剪", huge.Width <= 1920 && huge.Height <= 1040, huge.ToString());

            // 几何：小于最小尺寸时抬到最小尺寸
            var tiny = RcloneOssManager.WindowGeometry.Clamp(new Rectangle(50, 50, 100, 80), areas, new Size(960, 600));
            Check("几何-小于最小值抬升", tiny.Width >= 960 && tiny.Height >= 600, tiny.ToString());

            // 几何：只有标题栏露出来才算可达
            var barOnly = new Rectangle(100, 1035, 1080, 680);
            Check("几何-标题栏压边判不可达", !RcloneOssManager.WindowGeometry.Reachable(barOnly, areas), barOnly.ToString());
            var barVisible = new Rectangle(100, 1000, 1080, 680);
            Check("几何-标题栏可见判可达", RcloneOssManager.WindowGeometry.Reachable(barVisible, areas), barVisible.ToString());

            // 几何：空显示器列表不抛异常
            var none = RcloneOssManager.WindowGeometry.Clamp(new Rectangle(10, 10, 1080, 680), new Rectangle[0], new Size(960, 600));
            Check("几何-无显示器不抛异常", none.Width == 1080);

            // 几何：往返保存与读取
            var temp = Path.Combine(Path.GetTempPath(), "oss-geometry-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var value = new RcloneOssManager.WindowGeometry();
                value.Left = 120; value.Top = 240; value.Width = 1180; value.Height = 720; value.Maximized = true;
                RcloneOssManager.WindowGeometry.Save(temp, value);
                var loaded = RcloneOssManager.WindowGeometry.Load(temp);
                Check("几何-保存后读回", loaded != null && loaded.Left == 120 && loaded.Top == 240 && loaded.Width == 1180 && loaded.Height == 720 && loaded.Maximized, loaded == null ? "null" : "ok");
                File.WriteAllText(temp, "{ 这不是 JSON", new UTF8Encoding(false));
                Check("几何-损坏文件返回空", RcloneOssManager.WindowGeometry.Load(temp) == null);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
            Check("几何-缺失文件返回空", RcloneOssManager.WindowGeometry.Load(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".json")) == null);

            // 单实例：两种模式的互斥体名必须不同，否则后台恢复会吞掉真正的启动
            Check("单实例-交互与后台互斥体分离", RcloneOssManager.SingleInstanceChannel.MutexName(false) != RcloneOssManager.SingleInstanceChannel.MutexName(true));
            Check("单实例-交互互斥体名稳定", RcloneOssManager.SingleInstanceChannel.MutexName(false) == "RcloneOssManager");
            Check("单实例-唤醒事件名稳定", RcloneOssManager.SingleInstanceChannel.ShowEventName == "RcloneOssManager-Show");

            // 文件管理器：候选列表有序且包含常见安装位置
            var candidates = RcloneOssManager.FileManagerLaunch.PulseCandidates();
            Check("文件管理器-候选非空", candidates.Length > 0);
            var allPulse = true;
            foreach (var item in candidates) if (!item.EndsWith("pulse.exe", StringComparison.OrdinalIgnoreCase)) allPulse = false;
            Check("文件管理器-候选均指向pulse.exe", allPulse);
            var resolved = RcloneOssManager.FileManagerLaunch.ResolveManager();
            Check("文件管理器-本机可解析到程序", resolved != null && File.Exists(resolved), resolved == null ? "未找到" : resolved);

            // 打开空路径必须明确报错而不是抛异常
            string error;
            Check("文件管理器-空路径返回错误", !RcloneOssManager.FileManagerLaunch.TryOpen("", out error) && !String.IsNullOrWhiteSpace(error));

            // 配色与圆角
            Check("样式-亮度深色", RcloneOssManager.UiStyle.Luminance(Color.Black) < 0.1);
            Check("样式-亮度浅色", RcloneOssManager.UiStyle.Luminance(Color.White) > 0.9);
            var blended = RcloneOssManager.UiStyle.Blend(Color.Black, Color.White, 0.5);
            Check("样式-混合到中点", blended.R >= 127 && blended.R <= 128, blended.R.ToString());
            var clamped = RcloneOssManager.UiStyle.Blend(Color.Black, Color.White, 5.0);
            Check("样式-混合比例越界收敛", clamped.R == 255);
            var path = RcloneOssManager.UiStyle.RoundedPath(new Rectangle(0, 0, 40, 20), 6);
            Check("样式-圆角路径非空", path.PointCount > 4);
            path.Dispose();
            var square = RcloneOssManager.UiStyle.RoundedPath(new Rectangle(0, 0, 40, 20), 0);
            Check("样式-零半径退化为矩形", square.PointCount == 4, square.PointCount.ToString());
            square.Dispose();

            // 菜单文案已经和具体文件管理器解绑
            Check("文案-更多菜单使用中性文案", RcloneOssManager.OssFileForm.OpenInManagerText == "在文件管理器中打开", RcloneOssManager.OssFileForm.OpenInManagerText);

            // 桶ACL判定：MinIO 返回空 Owner ID 且 Grantee 只有类型，必须识别为仅所有者，否则所有 MinIO 桶都会被锁死
            Check("权限-MinIO空ID全权视为仅所有者", RcloneOssManager.OssBucketClient.OwnerOnlyAcl(
                "<AccessControlPolicy><Owner><ID></ID><DisplayName></DisplayName></Owner><AccessControlList><Grant><Grantee xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"CanonicalUser\"><Type>CanonicalUser</Type></Grantee><Permission>FULL_CONTROL</Permission></Grant></AccessControlList></AccessControlPolicy>"));
            Check("权限-AWS一致ID视为仅所有者", RcloneOssManager.OssBucketClient.OwnerOnlyAcl(
                "<AccessControlPolicy><Owner><ID>abc123</ID></Owner><AccessControlList><Grant><Grantee><ID>abc123</ID></Grantee><Permission>FULL_CONTROL</Permission></Grant></AccessControlList></AccessControlPolicy>"));
            Check("权限-他人ID拒绝", !RcloneOssManager.OssBucketClient.OwnerOnlyAcl(
                "<AccessControlPolicy><Owner><ID>abc123</ID></Owner><AccessControlList><Grant><Grantee><ID>other999</ID></Grantee><Permission>FULL_CONTROL</Permission></Grant></AccessControlList></AccessControlPolicy>"));
            Check("权限-匿名组授权拒绝", !RcloneOssManager.OssBucketClient.OwnerOnlyAcl(
                "<AccessControlPolicy><Owner><ID></ID></Owner><AccessControlList><Grant><Grantee><URI>http://acs.amazonaws.com/groups/global/AllUsers</URI></Grantee><Permission>READ</Permission></Grant></AccessControlList></AccessControlPolicy>"));
            Check("权限-多条声明拒绝", !RcloneOssManager.OssBucketClient.OwnerOnlyAcl(
                "<AccessControlPolicy><Owner><ID>abc123</ID></Owner><AccessControlList><Grant><Grantee><ID>abc123</ID></Grantee><Permission>FULL_CONTROL</Permission></Grant><Grant><Grantee><ID>abc123</ID></Grantee><Permission>FULL_CONTROL</Permission></Grant></AccessControlList></AccessControlPolicy>"));
            Check("权限-非全权拒绝", !RcloneOssManager.OssBucketClient.OwnerOnlyAcl(
                "<AccessControlPolicy><Owner><ID>abc123</ID></Owner><AccessControlList><Grant><Grantee><ID>abc123</ID></Grantee><Permission>READ</Permission></Grant></AccessControlList></AccessControlPolicy>"));
            Check("权限-有Owner却无Grantee身份拒绝", !RcloneOssManager.OssBucketClient.OwnerOnlyAcl(
                "<AccessControlPolicy><Owner><ID>abc123</ID></Owner><AccessControlList><Grant><Grantee><Type>CanonicalUser</Type></Grantee><Permission>FULL_CONTROL</Permission></Grant></AccessControlList></AccessControlPolicy>"));

            // 桶策略判定：无策略是私有，匿名可写属于复杂策略
            Check("权限-无策略视为私有", RcloneOssManager.OssBucketClient.PolicyMode("", "demo") == "private");
            Check("权限-匿名读写策略视为复杂", RcloneOssManager.OssBucketClient.PolicyMode(
                "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"*\"]},\"Action\":[\"s3:PutObject\",\"s3:DeleteObject\"],\"Resource\":[\"arn:aws:s3:::demo/*\"]}]}", "demo") == "custom");
            Check("权限-匿名只读策略视为公共", RcloneOssManager.OssBucketClient.PolicyMode(
                "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"*\"]},\"Action\":[\"s3:GetObject\"],\"Resource\":[\"arn:aws:s3:::demo/*\"]}]}", "demo") == "public-read");

            // 策略文档校验与模板
            List<string> problems, risks;
            string formatted;
            Check("策略-空文本拒绝", !RcloneOssManager.OssPolicyDoc.TryBuild("  ", "demo", out formatted, out problems, out risks) && problems.Count == 1, problems.Count.ToString());
            Check("策略-坏JSON拒绝", !RcloneOssManager.OssPolicyDoc.TryBuild("{\"Statement\":[", "demo", out formatted, out problems, out risks), formatted);
            Check("策略-裸星号资源拒绝", !RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"s3:GetObject\",\"Resource\":\"*\"}]}", "demo", out formatted, out problems, out risks));
            Check("策略-跨桶资源拒绝", !RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"s3:GetObject\",\"Resource\":\"arn:aws:s3:::other/*\"}]}", "demo", out formatted, out problems, out risks));
            Check("策略-未知顶层字段拒绝", !RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"s3:GetObject\",\"Resource\":\"arn:aws:s3:::demo/*\"}],\"Id\":1,\"Extra\":2}", "demo", out formatted, out problems, out risks));
            Check("策略-Effect非法拒绝", !RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"None\",\"Principal\":\"*\",\"Action\":\"s3:GetObject\",\"Resource\":\"arn:aws:s3:::demo/*\"}]}", "demo", out formatted, out problems, out risks));
            Check("策略-Allow缺Principal拒绝", !RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Action\":\"s3:GetObject\",\"Resource\":\"arn:aws:s3:::demo/*\"}]}", "demo", out formatted, out problems, out risks));
            Check("策略-合法只读声明通过", RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"*\"]},\"Action\":[\"s3:GetObject\"],\"Resource\":[\"arn:aws:s3:::demo/*\"]}]}", "demo", out formatted, out problems, out risks) && problems.Count == 0, formatted);
            Check("策略-匿名只读不判为写风险", !risks.Exists(delegate(string x) { return x.Contains("写入或删除"); }), String.Join("|", risks.ToArray()));
            Check("策略-匿名可写提示风险", RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"s3:PutObject\",\"Resource\":\"arn:aws:s3:::demo/*\"}]}", "demo", out formatted, out problems, out risks)
                && risks.Exists(delegate(string x) { return x.Contains("写入或删除"); }), String.Join("|", risks.ToArray()));
            Check("策略-条件字段提示不解释", RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"s3:GetObject\",\"Resource\":\"arn:aws:s3:::demo/*\",\"Condition\":{\"StringEquals\":{\"aws:Referer\":\"x\"}}}]}", "demo", out formatted, out problems, out risks)
                && risks.Exists(delegate(string x) { return x.Contains("Condition"); }), String.Join("|", risks.ToArray()));
            Check("策略-Deny允许缺Principal", RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"Deny\",\"Action\":\"s3:DeleteObject\",\"Resource\":\"arn:aws:s3:::demo/*\"}]}", "demo", out formatted, out problems, out risks), String.Join("|", problems.ToArray()));
            var templates = new[] { "read", "read-list", "anon-rw" };
            foreach (var kind in templates)
                Check("策略-模板可校验 " + kind, RcloneOssManager.OssPolicyDoc.TryBuild(RcloneOssManager.OssPolicyDoc.Template(kind, "demo"), "demo", out formatted, out problems, out risks)
                    && RcloneOssManager.OssPolicyDoc.TryBuild(formatted, "demo", out formatted, out problems, out risks), String.Join("|", problems.ToArray()));
            Check("策略-清空模板为空", RcloneOssManager.OssPolicyDoc.Template("none", "demo") == "");
            Check("策略-模板为缩进多行", RcloneOssManager.OssPolicyDoc.Template("read", "demo").Contains("\n")
                && RcloneOssManager.OssPolicyDoc.Template("read", "demo").Contains("\n   "), RcloneOssManager.OssPolicyDoc.Template("read", "demo"));
            var anon = RcloneOssManager.OssPolicyDoc.Template("anon-rw", "demo");
            Check("策略-排版后可逐字再解析", RcloneOssManager.OssBucketClient.CanonicalJson(RcloneOssManager.OssPolicyDoc.Pretty(anon)) == RcloneOssManager.OssBucketClient.CanonicalJson(anon));
            Check("策略-摘要按声明分行", RcloneOssManager.OssPolicyDoc.Describe(anon).Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length == 3, RcloneOssManager.OssPolicyDoc.Describe(anon));
            Check("策略-无策略摘要可读", RcloneOssManager.OssPolicyDoc.Describe("").Contains("没有桶策略"));
            Check("校验-空Action数组拒绝", !RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":[],\"Resource\":[\"arn:aws:s3:::demo/*\"]}]}", "demo", out formatted, out problems, out risks)
                && problems.Exists(delegate(string x) { return x.Contains("Action 不能是空数组"); }), String.Join("|", problems.ToArray()));
            Check("校验-空Resource数组拒绝", !RcloneOssManager.OssPolicyDoc.TryBuild(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":[\"s3:GetObject\"],\"Resource\":[]}]}", "demo", out formatted, out problems, out risks)
                && problems.Exists(delegate(string x) { return x.Contains("Resource 不能是空数组"); }), String.Join("|", problems.ToArray()));

            // 可视化模型：逐字回写、只改本条、表达不了的写法一律原样带回，且仍受写入校验保护。
            string modelError;
            var blank = RcloneOssManager.OssPolicyModel.Parse("", out modelError);
            Check("模型-无策略解析成空列表", modelError == null && !blank.Locked && blank.Statements.Count == 0, modelError);
            var brokenJson = RcloneOssManager.OssPolicyModel.Parse("{\"Statement\":[", out modelError);
            Check("模型-坏JSON锁定编辑", brokenJson.Locked && modelError != null && modelError.Contains("JSON"), modelError);
            var badShape = RcloneOssManager.OssPolicyModel.Parse("{\"Statement\":\"x\"}", out modelError);
            Check("模型-Statement写法异常锁定编辑", badShape.Locked && modelError.Contains("Statement"), modelError);
            var single = "{\"Statement\":{\"Effect\":\"Allow\",\"Principal\":\"*\",\"Action\":\"s3:GetObject\",\"Resource\":\"arn:aws:s3:::demo/*\"}}";
            var singleModel = RcloneOssManager.OssPolicyModel.Parse(single, out modelError);
            Check("模型-单对象声明照样可编辑", modelError == null && singleModel.Statements.Count == 1 && singleModel.Statements[0].PrincipalKind == "anonymous", modelError);
            var singleJson = RcloneOssManager.OssPolicyModel.ToJson(singleModel, "demo");
            Check("模型-未改动不补Version与Sid", !singleJson.Contains("Version") && !singleJson.Contains("Sid"), singleJson);
            var roundTrip = RcloneOssManager.OssPolicyModel.Parse(anon, out modelError);
            Check("模型-未改动逐字回写", modelError == null && RcloneOssManager.OssBucketClient.CanonicalJson(RcloneOssManager.OssPolicyModel.ToJson(roundTrip, "demo"))
                == RcloneOssManager.OssBucketClient.CanonicalJson(anon), RcloneOssManager.OssPolicyModel.ToJson(roundTrip, "demo"));
            Check("模型-匿名读写拆成三条可编辑", roundTrip.Statements.Count == 3 && roundTrip.Statements[0].PrincipalKind == "anonymous", roundTrip.Statements.Count.ToString());
            roundTrip.Statements[2].Actions.Remove("s3:DeleteObject");
            var editedJson = RcloneOssManager.OssPolicyModel.ToJson(roundTrip, "demo");
            Check("模型-改动只影响本条", !editedJson.Contains("s3:DeleteObject") && editedJson.Contains("s3:PutObject") && editedJson.Contains("s3:ListBucket")
                && RcloneOssManager.OssPolicyDoc.CountStatements(editedJson) == 3, editedJson);
            Check("模型-改动后仍通过校验", RcloneOssManager.OssPolicyDoc.TryBuild(editedJson, "demo", out formatted, out problems, out risks), String.Join("|", problems.ToArray()));
            var conditional = RcloneOssManager.OssPolicyModel.Parse(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"*\"]},\"Action\":\"s3:GetObject\",\"Resource\":\"arn:aws:s3:::demo/*\",\"Condition\":{\"StringEquals\":{\"aws:Referer\":\"x\"}}}]}", out modelError);
            var conditionalJson = RcloneOssManager.OssPolicyModel.ToJson(conditional, "demo");
            Check("模型-条件字段原样带回", conditionalJson.Contains("aws:Referer") && conditional.Statements[0].Note.Contains("Condition"), conditionalJson);
            Check("模型-条件字段仍在摘要里", RcloneOssManager.OssPolicyModel.Summary(conditional.Statements[0], "demo").Contains("含条件"), RcloneOssManager.OssPolicyModel.Summary(conditional.Statements[0], "demo"));
            var wrapped = RcloneOssManager.OssPolicyModel.Parse(
                "{\"Id\":\"keep\",\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"*\"],\"Service\":[\"logs.example.com\"]},\"Action\":[\"s3:GetObject\"],\"Resource\":[\"arn:aws:s3:::demo/*\"]}]}", out modelError);
            var wrappedJson = RcloneOssManager.OssPolicyModel.ToJson(wrapped, "demo");
            Check("模型-多键Principal整字段原样保留", wrapped.Statements[0].PrincipalKind == "locked" && wrappedJson.Contains("logs.example.com"), wrappedJson);
            Check("模型-顶层Id原样带回", wrappedJson.Contains("\"Id\"") && wrapped.Note.Contains("Id"), wrappedJson);
            var oddStatement = RcloneOssManager.OssPolicyModel.Parse("{\"Statement\":[\"x\"]}", out modelError);
            Check("模型-非对象声明整条保留", modelError == null && oddStatement.Statements.Count == 1 && !oddStatement.Statements[0].Editable
                && RcloneOssManager.OssPolicyModel.ToJson(oddStatement, "demo").Contains("\"x\""), RcloneOssManager.OssPolicyModel.ToJson(oddStatement, "demo"));
            Check("模型-原样保留仍受写入校验", !RcloneOssManager.OssPolicyDoc.TryBuild(RcloneOssManager.OssPolicyModel.ToJson(oddStatement, "demo"), "demo", out formatted, out problems, out risks));
            var freshModel = new RcloneOssManager.PolicyModel();
            freshModel.Statements.Add(RcloneOssManager.OssPolicyModel.NewDraft("demo"));
            var freshJson = RcloneOssManager.OssPolicyModel.ToJson(freshModel, "demo");
            Check("模型-新建声明字段齐全", freshJson.Contains("Effect") && freshJson.Contains("Principal") && freshJson.Contains("s3:GetObject")
                && RcloneOssManager.OssPolicyDoc.TryBuild(freshJson, "demo", out formatted, out problems, out risks), String.Join("|", problems.ToArray()) + freshJson);
            Check("模型-新建声明没有可保留的原文", !RcloneOssManager.OssPolicyModel.NewDraft("demo").Unchanged);
            Check("资源-桶名展开", RcloneOssManager.OssPolicyModel.ExpandResource("demo", "demo") == "arn:aws:s3:::demo");
            Check("资源-前缀展开", RcloneOssManager.OssPolicyModel.ExpandResource("logs/*", "demo") == "arn:aws:s3:::demo/logs/*");
            Check("资源-已有ARN与星号不改写", RcloneOssManager.OssPolicyModel.ExpandResource("arn:aws:s3:::other/x", "demo") == "arn:aws:s3:::other/x"
                && RcloneOssManager.OssPolicyModel.ExpandResource("*", "demo") == "*");
            Check("取值-按行与逗号拆分去重", String.Join(",", RcloneOssManager.OssPolicyModel.Split("123456789012, arn:aws:s3:::demo/*\n123456789012"))
                == "123456789012,arn:aws:s3:::demo/*", String.Join(",", RcloneOssManager.OssPolicyModel.Split("123456789012, arn:aws:s3:::demo/*\n123456789012")));

            // 权限明细：逐条列出策略声明与桶ACL授权，账号标识必须掩码，兼容后端不返回ID的写法。
            // 标签按 AWS/MinIO 真实返回结构用 AccessControlList；OSS 的 Grant 是纯文本，嵌套结构的 Grant 不能被当成 OSS 拼接显示。
            var minioAcl = "<AccessControlPolicy><Owner><ID></ID><DisplayName></DisplayName></Owner><AccessControlList>"
                + "<Grant><Grantee xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"CanonicalUser\"></Grantee>"
                + "<Permission>FULL_CONTROL</Permission></Grant></AccessControlList></AccessControlPolicy>";
            var minioDetail = RcloneOssManager.OssBucketClient.DescribeAccess("", minioAcl);
            Check("明细-MinIO空标识按所有者说明", minioDetail.Contains("后端未返回账号标识") && minioDetail.Contains("FULL_CONTROL"), minioDetail);
            Check("明细-无策略时明确写出", minioDetail.Contains("（没有桶策略）"), minioDetail);
            Check("明细-嵌套Grant不按OSS拼接", !minioDetail.Contains("OSS 桶 ACL"), minioDetail);
            var awsAcl = "<AccessControlPolicy><Owner><ID>abc123def4567890</ID></Owner><AccessControlList>"
                + "<Grant><Grantee xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:type=\"Group\"><URI>http://acs.amazonaws.com/groups/global/AllUsers</URI></Grantee><Permission>READ</Permission></Grant>"
                + "<Grant><Grantee><ID>abc123def4567890</ID></Grantee><Permission>FULL_CONTROL</Permission></Grant></AccessControlList></AccessControlPolicy>";
            var awsDetail = RcloneOssManager.OssBucketClient.DescribeAccess("", awsAcl);
            Check("明细-匿名组单独成行", awsDetail.Contains("匿名所有人（AllUsers） · READ"), awsDetail);
            Check("明细-两条授权计数", awsDetail.Contains("2 条授权"), awsDetail);
            Check("明细-账号ID只保留前四位", !awsDetail.Contains("abc123def4567890") && awsDetail.Contains("abc1…"), awsDetail);
            var authAcl = awsAcl.Replace("AllUsers", "AuthenticatedUsers");
            Check("明细-已认证组可识别", RcloneOssManager.OssBucketClient.DescribeAccess("", authAcl).Contains("已认证账号（AuthenticatedUsers）"));
            var ossAcl = "<AccessControlPolicy><Owner><ID>1234567890</ID></Owner><AccessControlList><Grant>public-read</Grant></AccessControlList></AccessControlPolicy>";
            Check("明细-OSS按单条Grant文本显示", RcloneOssManager.OssBucketClient.DescribeAccess("", ossAcl).Contains("OSS 桶 ACL = public-read"), RcloneOssManager.OssBucketClient.DescribeAccess("", ossAcl));
            Check("明细-不支持ACL时说明来源", RcloneOssManager.OssBucketClient.DescribeAccess("", "ACL_UNSUPPORTED:NotImplemented").Contains("后端不支持读取（NotImplemented）"));
            Check("明细-无法解析不抛异常", RcloneOssManager.OssBucketClient.DescribeAccess("", "<>不是XML").Contains("无法解析"));
            Check("明细-空ACL如实说明未返回", RcloneOssManager.OssBucketClient.DescribeAccess("", "").Contains("后端未返回桶ACL"), RcloneOssManager.OssBucketClient.DescribeAccess("", ""));
            Check("明细-无授权条目如实说明", RcloneOssManager.OssBucketClient.DescribeAccess("", "<AccessControlPolicy><Owner><ID>abc123def4567890</ID></Owner><AccessControlList></AccessControlList></AccessControlPolicy>").Contains("未返回任何授权"));
            Check("明细-策略按声明计数", RcloneOssManager.OssBucketClient.DescribeAccess(RcloneOssManager.OssPolicyDoc.Template("read-list", "demo"), minioAcl).Contains("2 条声明"));
            // 真实桶里常见的 {"AWS":["*"]} 写法要读成人话，动作逐条列全，账号与角色ARN一律掩码。
            var nestedPrincipal = RcloneOssManager.OssBucketClient.DescribeAccess(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"*\"]},\"Action\":[\"s3:GetObject\",\"s3:PutObject\",\"s3:DeleteObject\"],\"Resource\":[\"arn:aws:s3:::demo/*\"]}]}", "");
            Check("明细-嵌套匿名Principal不露JSON", !nestedPrincipal.Contains("AWS") && nestedPrincipal.Contains("匿名所有人"), nestedPrincipal);
            Check("明细-动作逐条列全", nestedPrincipal.Contains("GetObject") && nestedPrincipal.Contains("PutObject") && nestedPrincipal.Contains("DeleteObject"), nestedPrincipal);
            var accountPrincipal = RcloneOssManager.OssBucketClient.DescribeAccess(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"123456789012\",\"arn:aws:iam::987654321098:role/read\"]},\"Action\":\"s3:GetObject\",\"Resource\":\"arn:aws:s3:::demo/*\"}]}", "");
            Check("明细-账号ID掩码", !accountPrincipal.Contains("123456789012") && accountPrincipal.Contains("1234…（共12位）"), accountPrincipal);
            Check("明细-角色ARN只留主体并掩码", !accountPrincipal.Contains("987654321098") && accountPrincipal.Contains("账号 9876…（共12位） 的 role/read"), accountPrincipal);
            Check("明细-多取值逐一点出且不重复来源", accountPrincipal.Contains("、账号 9876…（共12位） 的 role/read") && !accountPrincipal.Contains("账号：账号"), accountPrincipal);
            var manyPrincipal = RcloneOssManager.OssBucketClient.DescribeAccess(
                "{\"Statement\":[{\"Effect\":\"Allow\",\"Principal\":{\"AWS\":[\"111111111111\",\"222222222222\",\"333333333333\",\"444444444444\",\"555555555555\"]},\"Action\":\"s3:GetObject\",\"Resource\":\"arn:aws:s3:::demo/*\"}]}", "");
            Check("明细-过多取值列前三并给总数", manyPrincipal.Contains("等 5 个") && manyPrincipal.Contains("1111…（共12位）") && !manyPrincipal.Contains("555555555555"), manyPrincipal);

            // 正文配色：键名、普通取值、需要警惕的通配与 Deny、数字与标点分色，引号不闭合的残段一律不上色。
            var colored = "{\r\n  \"Statement\": [\r\n    {\r\n      \"Effect\": \"Allow\",\r\n      \"Principal\": \"*\",\r\n      \"Action\": [\"s3:*\", \"s3:GetObject\"],\r\n      \"Resource\": \"arn:aws:s3:::demo/*\",\r\n      \"Version\": 2012\r\n    }\r\n  ]\r\n}";
            var kindOf = BuildKindFinder(colored);
            Check("正文-键名与括号分色", kindOf("\"Statement\"") == RcloneOssManager.JsonTokenKind.Key
                && kindOf("{") == RcloneOssManager.JsonTokenKind.Punct, kindOf("\"Statement\"") + "/" + kindOf("{"));
            Check("正文-普通取值与资源路径不标红", kindOf("\"Allow\"") == RcloneOssManager.JsonTokenKind.Text
                && kindOf("\"arn:aws:s3:::demo/*\"") == RcloneOssManager.JsonTokenKind.Text, kindOf("\"Allow\"") + "/" + kindOf("\"arn:aws:s3:::demo/*\""));
            Check("正文-匿名通配与任意动作标红", kindOf("\"*\"") == RcloneOssManager.JsonTokenKind.Risk
                && kindOf("\"s3:*\"") == RcloneOssManager.JsonTokenKind.Risk, kindOf("\"*\"") + "/" + kindOf("\"s3:*\""));
            Check("正文-数字单独类别", kindOf("2012") == RcloneOssManager.JsonTokenKind.Number, kindOf("2012").ToString());
            var denyJson = "{\"Effect\":\"Deny\",\"Action\":\"s3:GetObject\"}";
            Check("正文-Deny同样标红", BuildKindFinder(denyJson)("\"Deny\"") == RcloneOssManager.JsonTokenKind.Risk, denyJson);
            var brokenText = "{\"Sid\":\"没有收尾";
            var broken = RcloneOssManager.JsonColorizer.Scan(brokenText);
            Check("正文-引号不闭合后续不上色", broken.Count == 3 && broken[2].Start == 6, broken.Count + " 段，末段起 " + broken[broken.Count - 1].Start);
            var anonBody = RcloneOssManager.OssPolicyDoc.Template("anon-rw", "demo");
            var templateTokens = RcloneOssManager.JsonColorizer.Scan(anonBody);
            var inside = templateTokens.TrueForAll(delegate(RcloneOssManager.JsonToken t) { return t.Start >= 0 && t.Start + t.Length <= anonBody.Length; });
            Check("正文-模板全部区间落在正文内", inside && templateTokens.Exists(delegate(RcloneOssManager.JsonToken t) { return t.Kind == RcloneOssManager.JsonTokenKind.Risk; }), templateTokens.Count.ToString());
            Check("正文-配色表覆盖全部类别", RcloneOssManager.JsonColorizer.ColorFor(RcloneOssManager.JsonTokenKind.Key) != RcloneOssManager.JsonColorizer.ColorFor(RcloneOssManager.JsonTokenKind.Text)
                && RcloneOssManager.JsonColorizer.ColorFor(RcloneOssManager.JsonTokenKind.Risk) == RcloneOssManager.UiStyle.DangerText, RcloneOssManager.JsonColorizer.ColorFor(RcloneOssManager.JsonTokenKind.Key).ToString());

            // 盘符命名：本地盘模式下资源管理器只显示卷标，不再拖上网络盘的 (\\server) 后缀。
            var mountArgs = RcloneOssManager.RcloneClient.MountArguments("cfg.conf", "test-remote", "L:", "test-remote");
            Check("挂载-不再走网络盘模式且卷标照传", !mountArgs.Contains("--network-mode") && mountArgs.Contains("--volname")
                && mountArgs[mountArgs.IndexOf("--volname") + 1] == "test-remote", string.Join(" ", mountArgs.ToArray()));
            var modtimeRemotes = new[] { "server-modtime" };
            Check("挂载-仅白名单连接带服务器时间", RcloneOssManager.RcloneClient.MountArguments("cfg.conf", "server-modtime", "M:", "server-modtime", modtimeRemotes).Contains("--use-server-modtime")
                && !RcloneOssManager.RcloneClient.MountArguments("cfg.conf", "other-remote", "N:", "other-remote", modtimeRemotes).Contains("--use-server-modtime"),
                string.Join(" ", mountArgs.ToArray()));

            // 建桶名称规则与权限接口共用同一处判定。
            Check("建桶-合法短横线名", RcloneOssManager.OssBucketClient.ValidBucketName("perm-test"));
            Check("建桶-过短或过大写拒绝", !RcloneOssManager.OssBucketClient.ValidBucketName("ab") && !RcloneOssManager.OssBucketClient.ValidBucketName("Abc"));
            Check("建桶-连续点与越界长度拒绝", !RcloneOssManager.OssBucketClient.ValidBucketName("a..b") && !RcloneOssManager.OssBucketClient.ValidBucketName(new string('a', 64)) && RcloneOssManager.OssBucketClient.ValidBucketName(new string('a', 63)));
            Check("建桶-空值拒绝", !RcloneOssManager.OssBucketClient.ValidBucketName(null));

            // 菜单入口只读连接信息，读不到时必须安静隐藏而不是让浏览窗口打不开。
            var ghostFiles = new RcloneOssManager.OssFileClient(new RcloneOssManager.RcloneClient(), "probe-不存在的连接", true);
            Check("菜单-连接信息读不到时不显示策略编辑", !ghostFiles.PolicyEditable);

            // 列宽必须按真实字体与真实单元格文本来量：自绘单元格左右各留4像素，列宽只比内容宽8像素。
            var cellFont = new Font("Microsoft YaHei UI", 11F);
            var sizeText = RcloneOssManager.OssFileClient.Bytes(1024 * 1023.9 * 1023.9);
            var timeText = RcloneOssManager.OssFileForm.FormatTime("2026-11-19T20:19:00Z");
            Check("列宽-大小列取最宽尺寸文本", Fits(RcloneOssManager.OssFileForm.FileMinimumWidths[1], sizeText, cellFont), sizeText + " / " + RcloneOssManager.OssFileForm.FileMinimumWidths[1]);
            Check("列宽-时间列取完整时间文本", Fits(RcloneOssManager.OssFileForm.FileMinimumWidths[3], timeText, cellFont), timeText + " / " + RcloneOssManager.OssFileForm.FileMinimumWidths[3]);
            Check("列宽-类型列取存储桶文本", Fits(RcloneOssManager.OssFileForm.FileMinimumWidths[2], "存储桶", cellFont), "存储桶 / " + RcloneOssManager.OssFileForm.FileMinimumWidths[2]);
            Check("列宽-只有名称列参与扩展", RcloneOssManager.OssFileForm.FileColumnWeights.Sum() == RcloneOssManager.OssFileForm.FileColumnWeights[0], RcloneOssManager.OssFileForm.FileColumnWeights.Sum().ToString());
            Check("列宽-操作列留足四段图标", RcloneOssManager.OssFileForm.FileMinimumWidths[4] >= 4 * 30, RcloneOssManager.OssFileForm.FileMinimumWidths[4].ToString());
            Check("列宽-挂载列取未挂载文本", Fits(RcloneOssManager.MainForm.SideMinimumWidths[1], "未挂载", cellFont), "未挂载 / " + RcloneOssManager.MainForm.SideMinimumWidths[1]);
            Check("列宽-侧栏名称优先", RcloneOssManager.MainForm.SideColumnWeights[0] > RcloneOssManager.MainForm.SideColumnWeights[1]);
        }

        /// <summary>列最小宽度必须容得下内容加自绘内边距，又不能比内容宽出多余空白。</summary>
        /// <param name="minimum">列最小宽度。</param><param name="text">该列最宽单元格文本。</param><param name="font">列表字体。</param><returns>是否刚好容纳。</returns>
        private static bool Fits(int minimum, string text, Font font)
        {
            var content = Width(text, font) + 8;
            return minimum >= content && minimum <= content + 12;
        }

        /// <summary>按列表实际字体量文本宽度，用于校验列最小宽度不是拍脑袋定的。</summary>
        /// <param name="text">单元格文本。</param><param name="font">列表字体。</param><returns>像素宽度。</returns>
        private static int Width(string text, Font font)
        {
            return TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width;
        }

        /// <summary>列表各列实际宽度之和，用于判断是否会出现横向滚动。</summary>
        /// <param name="list">目标列表。</param><returns>像素总宽。</returns>
        private static int TotalWidth(ListView list)
        {
            var total = 0;
            foreach (ColumnHeader column in list.Columns) total += column.Width;
            return total;
        }

        // ---------------- interaction ----------------

        private static void RunInteraction()
        {
            // 1) 真实的 WinForms 消息循环里创建主窗口，确认按钮与列表就绪
            Exception failure = null;
            Form main = null;
            var orderPath = Path.Combine(Path.GetTempPath(), "oss-order-" + Guid.NewGuid().ToString("N") + ".txt");
            using (var ready = new ManualResetEvent(false))
            {
                var thread = new Thread(delegate()
                {
                    try
                    {
                        main = new RcloneOssManager.MainForm(false, null, false, orderPath);
                        var timer = new System.Windows.Forms.Timer();
                        timer.Interval = 1200;
                        timer.Tick += delegate { timer.Stop(); ready.Set(); };
                        main.Shown += delegate { timer.Start(); };
                        Application.Run(main);
                    }
                    catch (Exception ex) { failure = ex; ready.Set(); }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                Check("交互-主窗口在限定时间内就绪", ready.WaitOne(20000), "超时");
            }
            try
            {
                Check("交互-主窗口创建无异常", failure == null, failure == null ? null : failure.GetType().Name + " " + failure.Message);
                var form = main;
                Check("交互-主窗口可见", form != null && form.Visible, form == null ? "null" : form.Visible.ToString());
                if (form != null)
                {
                    var buttons = new List<Button>();
                    Collect(form, buttons);
                    Check("交互-扁平按钮已生效", buttons.Count >= 5 && buttons.TrueForAll(b => b is RcloneOssManager.FlatButton), buttons.Count.ToString());
                    var painted = true;
                    foreach (var button in buttons)
                    {
                        try { using (var bitmap = new Bitmap(Math.Max(1, button.Width), Math.Max(1, button.Height))) button.DrawToBitmap(bitmap, new Rectangle(0, 0, button.Width, button.Height)); }
                        catch (Exception) { painted = false; }
                    }
                    Check("交互-全部按钮可绘制", painted);
                    Check("交互-窗口记住位置文件路径", !String.IsNullOrWhiteSpace(RcloneOssManager.WindowGeometry.FilePath()));

                    var controls = new List<Control>();
                    CollectAll(form, controls);
                    var split = controls.Find(delegate(Control x) { return x is SplitContainer; }) as SplitContainer;
                    Check("交互-连接侧栏收窄", split != null && split.SplitterDistance == RcloneOssManager.MainForm.SideWidth,
                        split == null ? "无分栏" : split.SplitterDistance.ToString());
                    var bars = controls.FindAll(delegate(Control x) { return x is FlowLayoutPanel && (x.Name == "primaryActions" || x.Name == "secondaryActions"); });
                    var clipped = new List<string>();
                    foreach (FlowLayoutPanel bar in bars)
                        foreach (Control child in bar.Controls)
                            if (child.Right > bar.ClientSize.Width) clipped.Add(child.Text + " " + child.Right + ">" + bar.ClientSize.Width);
                    Check("交互-侧栏按钮不超出分组", bars.Count == 2 && clipped.Count == 0, String.Join(" | ", clipped.ToArray()));
                    var wide = controls.Find(delegate(Control x) { return x is Button && (x as Button).Text == "卸载本地盘"; }) as Button;
                    Check("交互-长文案按钮不被截断", wide != null && wide.Width >= TextRenderer.MeasureText(wide.Text, wide.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width + 12,
                        wide == null ? "缺失" : wide.Width + " / " + TextRenderer.MeasureText(wide.Text, wide.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding).Width);
                    var sideList = controls.Find(delegate(Control x) { return x is ListView; }) as ListView;
                    Check("交互-挂载列右对齐", sideList != null && sideList.Columns.Count > 1 && sideList.Columns[1].TextAlign == HorizontalAlignment.Right
                        && sideList.Columns[0].TextAlign == HorizontalAlignment.Left, sideList == null ? "无列表" : sideList.Columns.Count.ToString());
                    Check("交互-侧栏列表无横向滚动", sideList != null && TotalWidth(sideList) <= sideList.ClientSize.Width,
                        sideList == null ? "无列表" : TotalWidth(sideList) + " / " + sideList.ClientSize.Width);
                }

                // 2) 单实例唤醒：信号必须真的置位
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, RcloneOssManager.SingleInstanceChannel.ShowEventName))
                {
                    RcloneOssManager.SingleInstanceChannel.RequestShow();
                    Check("交互-唤醒信号已置位", signal.WaitOne(4000));

                    // 3) 已存在窗口时，置前调用必须返回成功而不是静默失败
                    Exception pumpFailure = null;
                    var activated = false;
                    using (var form2 = new Form())
                    using (var shown = new ManualResetEvent(false))
                    using (var done = new ManualResetEvent(false))
                    {
                        form2.Text = "单实例目标窗口";
                        form2.Shown += delegate { shown.Set(); };
                        var thread2 = new Thread(delegate()
                        {
                            try { Application.Run(form2); }
                            catch (Exception ex) { pumpFailure = ex; }
                        });
                        thread2.SetApartmentState(ApartmentState.STA);
                        thread2.IsBackground = true;
                        thread2.Start();
                        shown.WaitOne(8000);
                        Thread.Sleep(300);
                        form2.BeginInvoke((MethodInvoker)delegate { try { activated = RcloneOssManager.SingleInstanceChannel.ActivateExisting(form2); } finally { done.Set(); } });
                        done.WaitOne(5000);
                        Check("交互-置前已存在窗口", activated, pumpFailure == null ? "返回失败" : pumpFailure.Message);
                        form2.BeginInvoke((MethodInvoker)delegate { form2.Close(); });
                        Thread.Sleep(500);
                    }

                    // 4) 建桶入口：只在连接根目录可用，进入桶内后禁用
                    Exception menuFailure = null;
                    using (var bucketForm = new RcloneOssManager.OssFileForm(new RcloneOssManager.RcloneClient(), "probe-不存在的连接", true, null, false, false))
                    using (var flatForm = new RcloneOssManager.OssFileForm(new RcloneOssManager.RcloneClient(), "probe-不存在的连接", false, null, false, false))
                    {
                        try
                        {
                            var menu = FindMenu(bucketForm);
                            var item = FindMenuItem(menu, "新建桶…");
                            Check("交互-桶连接更多菜单含新建桶", item != null, menu == null ? "无菜单" : menu.Items.Count.ToString());
                            Check("交互-非桶连接不显示新建桶", FindMenuItem(FindMenu(flatForm), "新建桶…") == null);
                            Check("交互-非桶连接不显示桶权限入口", FindMenuItem(FindMenu(flatForm), "桶访问权限设置…") == null);
                            if (item != null)
                            {
                                RaiseOpening(menu, bucketForm);
                                Check("交互-根目录允许新建桶", item.Enabled, "currentPath=" + PathOf(bucketForm));
                                SetField(bucketForm, "currentPath", "some-bucket");
                                RaiseOpening(menu, bucketForm);
                                Check("交互-桶内禁止新建桶", !item.Enabled, "currentPath=" + PathOf(bucketForm));
                                SetField(bucketForm, "currentPath", "");
                            }
                        }
                        catch (Exception ex) { menuFailure = ex; }
                    }
                    Check("交互-建桶菜单检查无异常", menuFailure == null, menuFailure == null ? null : menuFailure.GetType().Name + " " + menuFailure.Message);
                }

                // 5) 权限与策略对话框：自定义不再整体置灰，覆盖既有授权要单独勾选确认，正文由界面实时生成
                Exception dialogFailure = null;
                var anonPolicy = RcloneOssManager.OssPolicyDoc.Template("anon-rw", "demo");
                var customState = new RcloneOssManager.OssBucketState { Mode = "custom", Reason = "Bucket Policy 含公共写。", Policy = anonPolicy, Acl = "", Snapshot = "s" };
                using (var permission = new RcloneOssManager.BucketPermissionDialog("demo", customState))
                {
                    try
                    {
                        var radios = permission.Controls.Cast<Control>().OfType<RadioButton>().ToList();
                        var save = (Button)permission.AcceptButton;
                        Check("交互-权限对话框含三个模式单选", radios.Count == 3 && save != null, radios.Count.ToString());
                        Check("交互-自定义时三个单选都可用", radios.All(x => x.Enabled));
                        Check("交互-默认选中自定义", radios[2].Checked && permission.Target == "custom", String.Join(",", radios.Select(x => x.Checked ? x.Text : "-").ToArray()));
                        Check("交互-未编辑自定义不允许保存", !save.Enabled);
                        var ack = permission.Controls.Cast<Control>().OfType<CheckBox>().FirstOrDefault();
                        Check("交互-覆盖确认写明声明条数", ack != null && ack.Text.Contains("3 条"), ack == null ? "无勾选框" : ack.Text);
                        radios[1].Checked = true;
                        // 窗体未显示时 Control.Visible 取到继承可见性，勾选框是否露出交由真实渲染用例核对，这里只盯住写入闸门。
                        Check("交互-自定义改公共只读同样要确认", !save.Enabled, save.Enabled.ToString());
                        radios[0].Checked = true;
                        Check("交互-自定义降到私有也要确认", !save.Enabled);
                        ack.Checked = true;
                        Check("交互-勾选覆盖确认可保存", save.Enabled);
                        radios[2].Checked = true;
                        Check("交互-回到自定义重新要求编辑", !save.Enabled && permission.Policy == null);
                    }
                    catch (Exception ex) { dialogFailure = ex; }
                }

                // 6) 可视化策略编辑器：未改动逐字回写，勾选变化即时反映到正文且只影响本条声明
                using (var editor = new RcloneOssManager.BucketPolicyDialog("demo", customState))
                {
                    try
                    {
                        var preview = editor.Controls.Cast<Control>().OfType<TextBoxBase>().FirstOrDefault(x => x.ReadOnly);
                        var list = editor.Controls.Cast<Control>().OfType<ListBox>().FirstOrDefault(x => x.GetType() == typeof(ListBox));
                        var checks = editor.Controls.Cast<Control>().OfType<CheckedListBox>().ToList();
                        Check("交互-编辑器含声明列表与只读正文", list != null && preview != null && checks.Count == 2, list == null ? "无列表" : checks.Count.ToString());
                        Check("交互-三条声明逐条可点选", list != null && list.Items.Count == 3, list == null ? "" : list.Items.Count.ToString());
                        Check("交互-未改动正文与原策略逐字一致", editor.Desired != null && RcloneOssManager.OssBucketClient.CanonicalJson(editor.Desired)
                            == RcloneOssManager.OssBucketClient.CanonicalJson(anonPolicy), editor.Desired);
                        Check("交互-正文框展示的就是将要写入的内容", preview != null && preview.Text.Contains("Statement")
                            && RcloneOssManager.OssBucketClient.CanonicalJson(preview.Text) == RcloneOssManager.OssBucketClient.CanonicalJson(anonPolicy), preview == null ? "" : preview.Text);
                        var simpleWidth = editor.Width;
                        var toggle = editor.Controls.Cast<Control>().OfType<Button>().FirstOrDefault(x => x.Text.StartsWith("高级"));
                        // 无句柄时 PerformClick 不触发，切换路径交给显示后的对话框渲染用例，这里只核对默认排版与开关本身。
                        Check("交互-默认简洁模式且逐条编辑藏在高级开关后", !editor.AdvancedMode && toggle != null && simpleWidth == 762 && editor.Height == 548,
                            (toggle == null ? "无开关" : toggle.Text) + " " + simpleWidth + "x" + editor.Height);
                        list.SelectedIndex = 2;
                        var actionIndex = -1;
                        for (var i = 0; i < checks[0].Items.Count; i++) if ((string)checks[0].Items[i] == "s3:DeleteObject") { actionIndex = i; break; }
                        checks[0].SetItemChecked(actionIndex, false);
                        Check("交互-取消勾选即时改写正文", editor.Desired != null && !editor.Desired.Contains("s3:DeleteObject")
                            && editor.Desired.Contains("s3:PutObject") && editor.Desired.Contains("s3:ListBucket")
                            && RcloneOssManager.OssPolicyDoc.CountStatements(editor.Desired) == 3, editor.Desired);
                        Check("交互-正文框与Desired同源", preview.Text.Contains("s3:PutObject") && !preview.Text.Contains("s3:DeleteObject"), preview.Text);
                        Check("交互-列表摘要跟随改动", list.Items[2].ToString().Contains("PutObject") && !list.Items[2].ToString().Contains("DeleteObject"), list.Items[2].ToString());
                        checks[0].SetItemChecked(actionIndex, true);
                        Check("交互-重新勾回恢复原文", RcloneOssManager.OssBucketClient.CanonicalJson(editor.Desired) == RcloneOssManager.OssBucketClient.CanonicalJson(anonPolicy), editor.Desired);
                        list.SelectedIndex = 0;
                        checks[1].SetItemChecked(0, false);
                        Check("交互-资源为空时拦下写入", editor.Desired == null && !((Button)editor.AcceptButton).Enabled, editor.Desired);
                    }
                    catch (Exception ex) { dialogFailure = dialogFailure == null ? ex : dialogFailure; }
                }
                Check("交互-权限与策略对话框检查无异常", dialogFailure == null, dialogFailure == null ? null : dialogFailure.GetType().Name + " " + dialogFailure.Message);
            }
            finally
            {
                try
                {
                    if (main != null && !main.IsDisposed) main.BeginInvoke((MethodInvoker)delegate { main.Close(); });
                    Thread.Sleep(700);
                }
                catch (InvalidOperationException) { }
                try { if (File.Exists(orderPath)) File.Delete(orderPath); } catch (IOException) { }
            }
        }

        /// <summary>取出文件窗口的“更多”菜单；菜单展开时才会重算各项可用性。</summary>
        /// <param name="form">文件浏览窗口。</param><returns>更多菜单，取不到时为空。</returns>
        private static ContextMenuStrip FindMenu(object form)
        {
            var field = typeof(RcloneOssManager.OssFileForm).GetField("moreMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(form) as ContextMenuStrip;
        }

        private static ToolStripItem FindMenuItem(ContextMenuStrip menu, string text)
        {
            if (menu == null) return null;
            foreach (ToolStripItem item in menu.Items) if (item.Text == text) return item;
            return null;
        }

        /// <summary>真实展开一次菜单以触发 Opening 门禁，再收起。</summary>
        /// <param name="menu">待展开的菜单。</param><param name="owner">宿主窗口。</param>
        private static void RaiseOpening(ContextMenuStrip menu, Control owner)
        {
            menu.Show(owner, new Point(8, 8));
            Application.DoEvents();
            menu.Close();
            Application.DoEvents();
        }

        private static string PathOf(object form)
        {
            return (string)typeof(RcloneOssManager.OssFileForm).GetField("currentPath", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        }

        private static void SetField(object target, string name, object value)
        {
            typeof(RcloneOssManager.OssFileForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }

        private static void Collect(Control root, List<Button> buttons)
        {
            var button = root as Button;
            if (button != null) buttons.Add(button);
            foreach (Control child in root.Controls) Collect(child, buttons);
        }

        /// <summary>收集全部子控件，用于检查分栏宽度、按钮分组边界与列对齐。</summary>
        /// <param name="root">起始容器。</param><param name="all">累加列表。</param>
        private static void CollectAll(Control root, List<Control> all)
        {
            all.Add(root);
            foreach (Control child in root.Controls) CollectAll(child, all);
        }

        // ---------------- shot ----------------

        private static void RunShot()
        {
            var target = productPath;
            Exception failure = null;
            using (var done = new ManualResetEvent(false))
            {
                var thread = new Thread(delegate()
                {
                    try
                    {
                        var form = new RcloneOssManager.MainForm(false, null, false, Path.Combine(Path.GetTempPath(), "oss-shot-order.txt"));
                        form.Shown += delegate
                        {
                            var timer = new System.Windows.Forms.Timer();
                            timer.Interval = 1500;
                            timer.Tick += delegate
                            {
                                timer.Stop();
                                try
                                {
                                    using (var bitmap = new Bitmap(form.Width, form.Height))
                                    {
                                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                                        bitmap.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                                    }
                                }
                                catch (Exception ex) { failure = ex; }
                                form.Close();
                            };
                            timer.Start();
                        };
                        Application.Run(form);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { done.Set(); }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                done.WaitOne(30000);
            }
            Check("截图-生成成功", failure == null && target != null && File.Exists(target) && new FileInfo(target).Length > 5000,
                failure != null ? failure.Message : "文件过小或缺失");
        }

        // ---------------- bundle ----------------

        private static void RunBundle()
        {
            var assembly = String.IsNullOrWhiteSpace(productPath) ? typeof(RcloneOssManager.RuntimePaths).Assembly : Assembly.LoadFrom(productPath);
            var names = assembly.GetManifestResourceNames();
            var hasRclone = false; var hasHash = false; var hasIcon = false;
            foreach (var name in names)
            {
                if (name == "rclone.exe.gz") hasRclone = true;
                if (name == "rclone.sha256") hasHash = true;
                if (name == "oss-manager.ico") hasIcon = true;
            }
            Check("打包-内嵌rclone引擎", hasRclone);
            Check("打包-内嵌校验记录", hasHash);
            Check("打包-内嵌图标", hasIcon);
            var types = assembly.GetTypes();
            var wanted = new[] { "RuntimePaths", "OssFileForm", "RuntimeSettingsForm", "ObjectInfoForm", "FlatButton", "FileManagerLaunch", "WindowGeometry", "SingleInstanceChannel", "NativeMethods" };
            foreach (var name in wanted)
            {
                var found = false;
                foreach (var type in types) if (type.Name == name) { found = true; break; }
                Check("打包-类型存在 " + name, found);
            }
            var versioned = assembly.GetName().Version;
            Check("打包-程序集版本可读", versioned != null);
        }
    }
}
