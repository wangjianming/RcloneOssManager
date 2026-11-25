using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>桶策略编辑器：默认三步（选整体起点、核对正文、写入），逐条可视化编辑收在“高级”开关里，写入前仍按原文校验。</summary>
    internal sealed class BucketPolicyDialog : Form
    {
        /// <summary>常见动作目录，按完整写法列出与正文一致；不在目录内的写法照常显示并可勾选。</summary>
        internal static readonly string[] ActionCatalog =
        {
            "s3:GetObject", "s3:PutObject", "s3:DeleteObject", "s3:GetObjectAcl", "s3:PutObjectAcl",
            "s3:ListBucket", "s3:GetBucketLocation", "s3:ListBucketMultipartUploads",
            "s3:AbortMultipartUpload", "s3:ListMultipartUploadParts", "s3:*"
        };

        internal static readonly string[] PrincipalKinds = { "匿名所有人（*）", "指定账号／角色（AWS）", "服务账户（Service）", "写法超出界面（原样保留）" };

        private readonly PolicyModel model = new PolicyModel();
        private readonly string bucket;
        private readonly string original;
        private readonly string currentText;
        private readonly Label info = new Label { Height = 34 };
        private readonly ListBox statements = new ListBox { IntegralHeight = false, Font = new Font("Microsoft YaHei UI", 9.5F) };
        private readonly Label statementsCaption = new Label { Height = 20, ForeColor = UiStyle.TextMuted };
        private readonly Label previewCaption = new Label { Height = 20, ForeColor = UiStyle.TextMuted };
        private readonly Label versionCaption = new Label { Height = 20, Text = "策略版本", ForeColor = UiStyle.TextMuted };
        private readonly ComboBox version = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox effect = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox principalKind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox principalValues = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, Font = new Font("Consolas", 9.5F) };
        private readonly CheckedListBox actions = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, SelectionMode = SelectionMode.None, Font = new Font("Consolas", 9.5F) };
        private readonly ComboBox newAction = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        private readonly CheckedListBox resources = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, SelectionMode = SelectionMode.None, Font = new Font("Consolas", 9.5F) };
        private readonly ComboBox newResource = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        private readonly TextBox sid = new TextBox();
        private readonly Label preserved = new Label { ForeColor = UiStyle.TextMuted };
        private readonly RichTextBox preview = new RichTextBox { ReadOnly = true, TabStop = false, DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical, BackColor = UiStyle.Card, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9.5F) };
        private readonly Label problems = new Label { ForeColor = UiStyle.DangerText };
        private readonly Label notes = new Label { ForeColor = UiStyle.TextMuted };
        private readonly CheckBox acknowledge = new CheckBox { Text = "我已确认该策略允许匿名写入或删除", ForeColor = UiStyle.DangerText };
        private readonly Button apply = new Button { Text = "写入策略", DialogResult = DialogResult.OK };
        private readonly FlatButton cancelDialog = new FlatButton { Text = "取消", DialogResult = DialogResult.Cancel, BackColor = UiStyle.AccentSoft, ForeColor = UiStyle.AccentText };
        private readonly FlatButton[] templates;
        private readonly FlatButton restore = new FlatButton { Text = "恢复为当前策略", Width = 122, Height = 28, BackColor = UiStyle.AccentSoft, ForeColor = UiStyle.AccentText };
        private readonly FlatButton addStatement = new FlatButton { Text = "新增声明", Width = 96, Height = 28, BackColor = UiStyle.AccentSoft, ForeColor = UiStyle.AccentText };
        private readonly FlatButton removeStatement = new FlatButton { Text = "删除选中", Width = 96, Height = 28, BackColor = UiStyle.AccentSoft, ForeColor = UiStyle.AccentText };
        private readonly FlatButton advancedToggle = new FlatButton { Text = "高级：逐条修改声明", Width = 190, Height = 30, BackColor = UiStyle.AccentSoft, ForeColor = UiStyle.AccentText };
        private readonly FlatButton addAction;
        private readonly FlatButton addResource;
        private Control[] advancedOnly = new Control[0];
        private Label[] advancedCaptions = new Label[0];
        private readonly Control[] editable = new Control[0];
        private bool loading = true;
        private bool riskAck;

        /// <summary>校验通过的策略文本；按清空处理时为空字符串，未通过为 null。</summary>
        internal string Desired { get; private set; }

        /// <summary>是否按删除整个桶策略处理。</summary>
        internal bool Clear { get; private set; }

        /// <summary>是否处于逐条编辑的高级模式；界面可见性在窗体显示前取到的是继承值，判定状态只能看这个标志。</summary>
        internal bool AdvancedMode { get { return advanced; } }

        private bool advanced;

        /// <summary>按刚读取的策略快照装配编辑器；阿里云 OSS 走桶 ACL，不使用此入口。</summary>
        /// <param name="name">桶名称。</param><param name="state">界面读取并展示给用户的权限快照。</param>
        internal BucketPolicyDialog(string name, OssBucketState state)
        {
            bucket = name;
            original = state.Policy ?? "";
            currentText = BucketPermissionDialog.Describe(state);
            string parseError;
            Replace(OssPolicyModel.Parse(original, out parseError));

            Text = "桶策略编辑";
            StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            UiStyle.ApplyWindow(this);
            Controls.Add(info);
            Controls.Add(new Label { Left = 18, Top = 54, Width = 76, Height = 20, Text = "整体起点：", ForeColor = UiStyle.TextMuted });
            templates = new[]
            {
                Template("公共只读", "read", 96), Template("只读＋列表", "read-list", 206),
                Template("匿名可写可删", "anon-rw", 316), Template("清空策略", "none", 426)
            };
            restore.Click += delegate { string error; Replace(OssPolicyModel.Parse(original, out error)); Populate(); };
            addStatement.Click += delegate { AddStatement(); };
            removeStatement.Click += delegate { RemoveStatement(); };
            advancedToggle.Click += delegate { SetAdvanced(!advanced); };
            Controls.AddRange(templates);
            Controls.AddRange(new Control[] { restore, addStatement, removeStatement, advancedToggle });
            // 本来就没有策略的桶谈不上“清空”，起点按钮只留真正可用的动作。
            templates[3].Enabled = original.Trim() != "";

            Controls.Add(statementsCaption);
            statements.SelectedIndexChanged += delegate { if (loading) return; ShowSelected(); Rebuild(); };
            Controls.Add(statements);
            version.Items.AddRange(new object[] { "（不写 Version）", "2012-10-17", "2008-10-17" });
            version.SelectedIndexChanged += delegate { if (loading) return; model.Version = version.SelectedIndex <= 0 ? "" : (string)version.SelectedItem; Rebuild(); };
            Controls.AddRange(new Control[] { versionCaption, version, preserved });

            var statementCaption = Caption(332, 86, 120, "本条声明");
            var effectCaption = Caption(332, 111, 60, "效果");
            effect.Items.AddRange(new object[] { "Allow", "Deny" });
            effect.SelectedIndexChanged += delegate { Commit(); Rebuild(); };
            var principalCaption = Caption(332, 145, 90, "被授权者");
            principalKind.Items.AddRange(PrincipalKinds);
            principalKind.SelectedIndexChanged += delegate { KindChanged(); };
            var valuesCaption = Caption(332, 174, 348, "取值（每行一个账号ID或 ARN，逗号也可）");
            principalValues.TextChanged += delegate { Commit(); Rebuild(); };
            var actionsCaption = Caption(332, 258, 348, "动作（勾选即写入）");
            actions.ItemCheck += delegate(object sender, ItemCheckEventArgs e) { CheckChanged(actions, e, true); };
            newAction.Items.AddRange(ActionCatalog);
            addAction = Tool("添加动作", delegate { AddValue(newAction, false); });
            var resourcesCaption = Caption(332, 456, 348, "资源（勾选即写入，可填桶名或前缀）");
            resources.ItemCheck += delegate(object sender, ItemCheckEventArgs e) { CheckChanged(resources, e, false); };
            newResource.Items.AddRange(new object[] { "arn:aws:s3:::" + bucket, "arn:aws:s3:::" + bucket + "/*" });
            addResource = Tool("添加资源", delegate { AddValue(newResource, true); });
            var sidCaption = Caption(332, 617, 96, "Sid（可空）");
            sid.TextChanged += delegate { Commit(); Rebuild(); };
            advancedCaptions = new[] { statementCaption, effectCaption, principalCaption, valuesCaption, actionsCaption, resourcesCaption, sidCaption };
            advancedOnly = new Control[]
            {
                effect, principalKind, principalValues, actions, newAction, addAction,
                resources, newResource, addResource, sid, versionCaption, version, preserved, addStatement, removeStatement
            };
            Controls.AddRange(advancedCaptions);
            Controls.AddRange(new Control[]
            {
                effect, principalKind, principalValues, actions, newAction, addAction,
                resources, newResource, addResource, sid
            });

            previewCaption.Text = "实时生成的正文（就是将要写入的内容，只读）";
            acknowledge.CheckedChanged += delegate { UpdateApply(); };
            Controls.AddRange(new Control[] { previewCaption, preview, problems, notes, acknowledge, apply, cancelDialog });

            editable = new Control[] { statements, version, effect, principalKind, principalValues, actions, newAction, resources, newResource, sid, apply };
            AcceptButton = apply; CancelButton = cancelDialog;
            SetAdvanced(false);
            loading = false;
            if (parseError != null) Block(parseError);
            else Populate();
        }

        /// <summary>切换简洁／高级两种排版：简洁模式只留起点、声明摘要、正文和写入，逐条编辑控件全部收起。</summary>
        /// <param name="value">true 为逐条编辑的高级模式。</param>
        private void SetAdvanced(bool value)
        {
            advanced = value;
            advancedToggle.Text = value ? "回到简洁视图" : "高级：逐条修改声明";
            foreach (var control in advancedOnly) control.Visible = value;
            foreach (var caption in advancedCaptions) caption.Visible = value;
            statements.Visible = true;
            statementsCaption.Visible = true;
            if (value)
            {
                info.Text = "桶：" + bucket + "　高级逐条编辑：改哪条就只重写哪条，界面表达不了的字段原样保留。写入前重新读取比对，写后逐字读回核验。";
                info.SetBounds(18, 12, 984, 34);
                statementsCaption.Text = "声明列表（点选后在中间编辑）";
                statementsCaption.SetBounds(18, 86, 300, 20);
                statements.SetBounds(18, 106, 300, 348);
                versionCaption.SetBounds(18, 460, 120, 20);
                version.SetBounds(18, 480, 300, 26);
                preserved.SetBounds(18, 512, 300, 116);
                statementBounds();
                previewCaption.SetBounds(692, 86, 310, 20);
                preview.SetBounds(692, 106, 310, 470);
                problems.SetBounds(692, 582, 310, 42);
                notes.SetBounds(692, 626, 310, 42);
                acknowledge.SetBounds(18, 640, 300, 24);
                restore.SetBounds(536, 50, 122, 28);
                addStatement.SetBounds(664, 50, 96, 28);
                removeStatement.SetBounds(766, 50, 96, 28);
                apply.SetBounds(430, 648, 120, 30);
                cancelDialog.SetBounds(560, 648, 96, 30);
                advancedToggle.SetBounds(868, 50, 110, 28);
                Width = 1020; Height = 724;
            }
            else
            {
                info.Text = "桶：" + bucket + "　当前：" + currentText
                    + "\r\n三步：选一个整体起点 → 核对右侧正文 → 点写入策略；未改动的声明原样保留。";
                info.SetBounds(18, 12, 724, 34);
                statementsCaption.Text = "当前声明（只读，逐条修改请点左下“高级”）";
                statementsCaption.SetBounds(18, 86, 340, 20);
                statements.SetBounds(18, 106, 340, 260);
                previewCaption.SetBounds(372, 86, 370, 20);
                preview.SetBounds(372, 106, 370, 260);
                problems.SetBounds(18, 372, 724, 42);
                notes.SetBounds(18, 416, 724, 22);
                acknowledge.SetBounds(18, 442, 340, 24);
                restore.SetBounds(546, 50, 122, 28);
                apply.SetBounds(500, 470, 120, 30);
                cancelDialog.SetBounds(630, 470, 96, 30);
                advancedToggle.SetBounds(18, 470, 190, 30);
                Width = 762; Height = 548;
                ApplySimplePanes();
            }
        }

        /// <summary>简洁模式下没有声明就不摆空列表，正文占满整行，界面只留下真正能点的动作。</summary>
        private void ApplySimplePanes()
        {
            if (advanced) return;
            var empty = model.Statements.Count == 0;
            statements.Visible = !empty;
            statementsCaption.Visible = !empty;
            previewCaption.SetBounds(empty ? 18 : 372, 86, 370, 20);
            preview.SetBounds(empty ? 18 : 372, 106, empty ? 726 : 370, 260);
        }

        /// <summary>高级模式中间栏的具体坐标。</summary>
        private void statementBounds()
        {
            advancedCaptions[0].SetBounds(332, 86, 120, 20);
            advancedCaptions[1].SetBounds(332, 111, 60, 20);
            effect.SetBounds(430, 107, 120, 26);
            advancedCaptions[2].SetBounds(332, 145, 90, 20);
            principalKind.SetBounds(430, 141, 250, 26);
            advancedCaptions[3].SetBounds(332, 174, 348, 20);
            principalValues.SetBounds(332, 194, 348, 58);
            advancedCaptions[4].SetBounds(332, 258, 348, 20);
            actions.SetBounds(332, 278, 348, 140);
            newAction.SetBounds(332, 424, 258, 26);
            addAction.SetBounds(594, 421, 86, 28);
            advancedCaptions[5].SetBounds(332, 456, 348, 20);
            resources.SetBounds(332, 476, 348, 100);
            newResource.SetBounds(332, 582, 258, 26);
            addResource.SetBounds(594, 579, 86, 28);
            advancedCaptions[6].SetBounds(332, 617, 96, 20);
            sid.SetBounds(430, 614, 250, 26);
        }

        private static Label Caption(int left, int top, int width, string text)
        {
            return new Label { Left = left, Top = top, Width = width, Height = 20, Text = text, ForeColor = UiStyle.TextMuted };
        }

        /// <summary>正文只在内容真的变了时重画，重画后回到原先滚到的那一行，勾一下动作不会跳回顶部。</summary>
        /// <param name="text">将要写入的 JSON 正文。</param>
        private void ShowBody(string text)
        {
            if (preview.Text == text) return;
            // 句柄还没建时只缓存文本，配色等窗口显示时统一补一次。
            if (!preview.IsHandleCreated) { preview.Text = text; return; }
            var topLine = preview.GetLineFromCharIndex(preview.GetCharIndexFromPosition(new Point(1, 1)));
            // 逐段配色会画出选择带，先把控件藏起来一次画完再露出，避免正文像扫描一样闪。
            var wasVisible = preview.Visible;
            preview.Visible = false;
            try
            {
                preview.Text = text;
                JsonColorizer.Paint(preview);
                var lastLine = preview.GetLineFromCharIndex(Math.Max(preview.TextLength - 1, 0));
                preview.Select(preview.GetFirstCharIndexFromLine(Math.Min(topLine, lastLine)), 0);
                preview.ScrollToCaret();
            }
            finally { preview.Visible = wasVisible; }
        }

        /// <summary>建句柄时框架会把缓存的正文重新写进窗口，把配色抹回默认色，所以显示前统一补涂一次。</summary>
        /// <param name="e">事件参数。</param>
        protected override void OnShown(EventArgs e)
        {
            JsonColorizer.Paint(preview);
            base.OnShown(e);
        }

        /// <summary>整份策略无法逐条拆解时如实停手：展示原文，禁用编辑与写入，绝不按空声明列表覆盖。</summary>
        /// <param name="reason">无法编辑的原因。</param>
        private void Block(string reason)
        {
            loading = true;
            ShowBody(OssPolicyDoc.Pretty(original));
            ClearFields();
            foreach (var control in editable) control.Enabled = false;
            problems.ForeColor = UiStyle.DangerText;
            problems.Text = "本策略无法逐条可视化编辑：" + reason;
            notes.Text = "需要改动请到服务端控制台；界面不写入任何内容。";
            Desired = null; riskAck = false; acknowledge.Visible = false; UpdateApply();
            ApplySimplePanes();
            loading = false;
        }

        private FlatButton Template(string text, string kind, int left)
        {
            var button = new FlatButton { Text = text, Left = left, Top = 50, Width = 104, Height = 28, BackColor = UiStyle.AccentSoft, ForeColor = UiStyle.AccentText };
            button.Click += delegate
            {
                string error;
                Replace(OssPolicyModel.Parse(kind == "none" ? "" : OssPolicyDoc.Template(kind, bucket), out error));
                Populate();
            };
            return button;
        }

        private static FlatButton Tool(string text, Action onClick)
        {
            var button = new FlatButton { Text = text, Width = 86, Height = 28, BackColor = UiStyle.AccentSoft, ForeColor = UiStyle.AccentText };
            button.Click += delegate { onClick(); };
            return button;
        }

        /// <summary>把解析结果搬进当前模型，保持外部持有的引用不变。</summary>
        /// <param name="next">解析出的模型。</param>
        private void Replace(PolicyModel next)
        {
            loading = true;
            model.Statements.Clear();
            model.Preserved.Clear();
            model.Notes.Clear();
            foreach (var statement in next.Statements) model.Statements.Add(statement);
            foreach (var pair in next.Preserved) model.Preserved[pair.Key] = pair.Value;
            foreach (var note in next.Notes) model.Notes.Add(note);
            model.Version = next.Version;
            model.Locked = next.Locked;
            loading = false;
        }

        /// <summary>按模型重建全部界面状态：模板、恢复、初始装配都走这一条路径。</summary>
        private void Populate()
        {
            loading = true;
            foreach (var control in editable) if (control != apply) control.Enabled = true;
            version.SelectedIndex = model.Version == "2008-10-17" ? 2 : model.Version == "2012-10-17" ? 1 : 0;
            RenderList();
            loading = false;
            if (model.Statements.Count == 0) { ClearFields(); ShowNotes(); }
            else { statements.SelectedIndex = 0; ShowSelected(); }
            Rebuild();
        }

        private void ClearFields()
        {
            effect.SelectedIndex = 0; principalKind.SelectedIndex = 0; principalValues.Text = "";
            actions.Items.Clear(); resources.Items.Clear(); sid.Text = "";
            foreach (var control in new Control[] { effect, principalKind, principalValues, actions, newAction, resources, newResource, sid }) control.Enabled = false;
        }

        private void AddStatement()
        {
            model.Statements.Add(OssPolicyModel.NewDraft(bucket));
            loading = true;
            RenderList();
            loading = false;
            foreach (var control in new Control[] { effect, principalKind, principalValues, actions, newAction, resources, newResource, sid }) control.Enabled = true;
            statements.SelectedIndex = model.Statements.Count - 1;
            ShowSelected();
            Rebuild();
        }

        private void RemoveStatement()
        {
            var index = statements.SelectedIndex;
            if (index < 0) return;
            model.Statements.RemoveAt(index);
            loading = true;
            RenderList();
            loading = false;
            if (model.Statements.Count == 0) { ClearFields(); ShowNotes(); Rebuild(); return; }
            statements.SelectedIndex = Math.Min(index, model.Statements.Count - 1);
            ShowSelected();
            Rebuild();
        }

        private PolicyDraft Selected
        {
            get
            {
                var index = statements.SelectedIndex;
                return index < 0 || index >= model.Statements.Count ? null : model.Statements[index];
            }
        }

        /// <summary>声明列表只换文本，选中项由调用方在刷新后恢复。</summary>
        private void RenderList()
        {
            var selected = statements.SelectedIndex;
            statements.BeginUpdate();
            try
            {
                statements.Items.Clear();
                for (var i = 0; i < model.Statements.Count; i++)
                    statements.Items.Add("#" + (i + 1) + "　" + OssPolicyModel.Summary(model.Statements[i], bucket));
                if (model.Statements.Count > 0 && selected >= 0 && selected < model.Statements.Count) statements.SelectedIndex = selected;
            }
            finally { statements.EndUpdate(); }
        }

        private void ShowSelected()
        {
            var draft = Selected;
            if (draft == null) { ClearFields(); return; }
            loading = true;
            foreach (var control in new Control[] { effect, principalKind, principalValues, actions, newAction, resources, newResource, sid }) control.Enabled = draft.Editable;
            effect.SelectedItem = draft.Effect;
            principalKind.SelectedIndex = KindIndex(draft.PrincipalKind);
            principalValues.Enabled = draft.Editable && IsListed(draft.PrincipalKind);
            principalValues.Text = draft.PrincipalKind == "anonymous" ? "" : draft.PrincipalValues;
            FillList(actions, draft.Actions, ActionCatalog);
            FillList(resources, draft.Resources, new string[0]);
            sid.Text = draft.Sid;
            var note = draft.Editable ? draft.Note : "整条声明原样保留：" + OssPolicyModel.RawText(draft);
            if (model.Note != "") note = note == "" ? model.Note : note + "　" + model.Note;
            preserved.Text = note;
            ShowNotes();
            loading = false;
        }

        private void ShowNotes()
        {
            if (notes.Text != "") return;
            notes.ForeColor = UiStyle.TextMuted;
            notes.Text = "写入前逐条校验：资源必须落在本桶，Allow 必须写明被授权者；空声明列表按删除全部桶策略处理。";
        }

        /// <summary>列表项取“已有取值在前、目录在后”的并集，保证任何写法都能在界面上看到并勾选。</summary>
        private static void FillList(CheckedListBox list, List<string> current, IEnumerable<string> catalog)
        {
            list.Items.Clear();
            var seen = new List<string>();
            foreach (var value in current.Concat(catalog))
                if (!seen.Contains(value))
                {
                    seen.Add(value);
                    list.Items.Add(value, current.Contains(value));
                }
        }

        internal static int KindIndex(string kind) { return kind == "aws" ? 1 : kind == "service" ? 2 : kind == "locked" ? 3 : 0; }

        internal static string KindName(int index) { return index == 1 ? "aws" : index == 2 ? "service" : index == 3 ? "locked" : "anonymous"; }

        private static bool IsListed(string kind) { return kind == "aws" || kind == "service"; }

        private void KindChanged()
        {
            var draft = Selected;
            if (draft == null || loading || !draft.Editable) return;
            var kind = KindName(principalKind.SelectedIndex);
            if (kind != draft.PrincipalKind)
            {
                principalValues.Text = kind == "anonymous" || kind == "locked" ? "" : principalValues.Text;
                principalValues.Enabled = IsListed(kind);
            }
            Commit();
            Rebuild();
        }

        /// <summary>ItemCheck 触发时控件状态还没更新，按传入的新勾选值计算列表，避免漏掉刚点中的那一项。</summary>
        private void CheckChanged(CheckedListBox list, ItemCheckEventArgs e, bool isAction)
        {
            var draft = Selected;
            if (draft == null || loading || e.CurrentValue == e.NewValue) return;
            var items = new List<string>();
            for (var i = 0; i < list.Items.Count; i++)
                if (i == e.Index ? e.NewValue == CheckState.Checked : list.GetItemChecked(i)) items.Add((string)list.Items[i]);
            var target = isAction ? draft.Actions : draft.Resources;
            target.Clear();
            target.AddRange(items);
            if (IsHandleCreated) BeginInvoke((Action)Rebuild);
            else Rebuild();
        }

        private void AddValue(ComboBox input, bool expand)
        {
            var draft = Selected;
            if (draft == null || !draft.Editable) return;
            var text = (input.Text ?? "").Trim();
            if (text == "") return;
            var value = expand ? OssPolicyModel.ExpandResource(text, bucket) : text;
            var target = expand ? draft.Resources : draft.Actions;
            if (!target.Contains(value)) target.Add(value);
            loading = true;
            FillList(expand ? resources : actions, target, expand ? new string[0] : ActionCatalog);
            RenderList();
            loading = false;
            input.Text = "";
            Rebuild();
        }

        /// <summary>把中间控件的当前内容写回选中的声明；未选中或整条不可编辑时什么都不做。</summary>
        private void Commit()
        {
            var draft = Selected;
            if (draft == null || loading || !draft.Editable) return;
            draft.Effect = effect.SelectedItem == null ? "Allow" : (string)effect.SelectedItem;
            draft.PrincipalKind = KindName(principalKind.SelectedIndex);
            draft.PrincipalValues = principalValues.Text;
            draft.Sid = sid.Text;
        }

        /// <summary>重算正文、校验结论与按钮状态。</summary>
        private void Rebuild()
        {
            if (loading) return;
            Commit();
            loading = true;
            RenderList();
            var selected = Selected;
            preserved.Text = selected == null ? model.Note : (selected.Note == "" ? model.Note : selected.Note + "　" + model.Note);
            loading = false;
            ApplySimplePanes();
            if (model.Statements.Count == 0)
            {
                Clear = true;
                ShowBody("");
                // 空声明列表谈不上匿名写删风险，勾选框要一起收起，否则没有策略的桶一进界面就多条无关确认。
                riskAck = false;
                acknowledge.Visible = false;
                if (original.Trim() == "")
                {
                    Desired = null;
                    problems.ForeColor = UiStyle.TextMuted;
                    problems.Text = advanced
                        ? "当前没有桶策略，声明列表也是空的：先点“新增声明”或选一个整体起点。"
                        : "当前没有桶策略：在上方“整体起点”选一个即可；要逐条添加声明请点“高级”。";
                }
                else
                {
                    Desired = "";
                    problems.ForeColor = UiStyle.DangerText;
                    problems.Text = "声明列表为空：将删除该桶的全部 Bucket Policy，对象只保留认证账号访问；桶 ACL、IAM 与公共访问阻止不受影响。";
                }
                notes.Text = "当前：" + OssPolicyDoc.Describe(original).Replace("\r\n", "　");
                UpdateApply();
                return;
            }
            Clear = false;
            var json = OssPolicyModel.ToJson(model, bucket);
            ShowBody(json);
            List<string> errors, warnings;
            string pretty;
            var ok = OssPolicyDoc.TryBuild(json, bucket, out pretty, out errors, out warnings);
            Desired = ok ? pretty : null;
            if (!ok)
            {
                problems.ForeColor = UiStyle.DangerText;
                problems.Text = "不能写入：" + String.Join("　", errors.ToArray());
                notes.Text = "当前：" + OssPolicyDoc.Describe(original).Replace("\r\n", "　");
                riskAck = false;
                acknowledge.Visible = false;
                UpdateApply();
                return;
            }
            var risky = warnings.Exists(delegate(string x) { return x.Contains("写入或删除"); });
            // 窗体显示前 Control.Visible 取到的是继承可见性，判定必须用自己的标志位。
            riskAck = risky;
            acknowledge.Visible = risky;
            problems.ForeColor = warnings.Count == 0 ? UiStyle.SuccessText : risky ? UiStyle.DangerText : UiStyle.InfoText;
            problems.Text = warnings.Count == 0 ? "校验通过：未检测到需要额外确认的授权。" : "需要确认：" + String.Join("　", warnings.ToArray());
            notes.Text = "写入后摘要：" + OssPolicyDoc.Describe(pretty).Replace("\r\n", "　");
            UpdateApply();
        }

        private void UpdateApply() { apply.Enabled = Desired != null && (!riskAck || acknowledge.Checked); }
    }
}
