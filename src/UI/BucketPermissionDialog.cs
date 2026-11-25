using System;
using System.Drawing;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>桶访问权限对话框：私有、公共只读与自定义三种都能落地，自定义可直接进入可视化策略编辑器。</summary>
    internal sealed class BucketPermissionDialog : Form
    {
        private readonly OssBucketState state;
        private readonly string bucket;
        private readonly bool policyEditable;
        private readonly RadioButton privateMode = new RadioButton { Text = "私有：仅认证账号可访问", Left = 20, Top = 60, Width = 420 };
        private readonly RadioButton publicMode = new RadioButton { Text = "公共只读：任何人可读取桶内对象", Left = 20, Top = 86, Width = 420 };
        private readonly RadioButton customMode = new RadioButton { Text = "自定义：可视化编辑桶策略", Left = 20, Top = 112, Width = 420 };
        private readonly CheckBox overwrite = new CheckBox { Left = 34, Top = 142, Width = 540, Height = 24, ForeColor = UiStyle.DangerText };
        private readonly FlatButton edit = new FlatButton { Text = "编辑策略…", Left = 444, Top = 108, Width = 116, Height = 28, BackColor = UiStyle.AccentSoft, ForeColor = UiStyle.AccentText };
        private readonly TextBox detail = new TextBox
        {
            Left = 20, Top = 200, Width = 540, Height = 178, Multiline = true, ReadOnly = true, TabStop = false,
            ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = UiStyle.Card, BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9.5F)
        };
        private readonly Label note = new Label { Left = 20, Top = 386, Width = 540, Height = 58, ForeColor = UiStyle.TextMuted };
        private readonly Button ok = new Button { Text = "保存", Left = 406, Top = 452, Width = 76 };
        private readonly FlatButton cancelDialog = new FlatButton { Text = "取消", DialogResult = DialogResult.Cancel, Left = 488, Top = 452, Width = 76, BackColor = UiStyle.AccentSoft, ForeColor = UiStyle.AccentText };
        private bool used;

        /// <summary>界面选定的目标模式：private、public-read 或 custom。</summary>
        internal string Target { get { return customMode.Checked ? "custom" : privateMode.Checked ? "private" : "public-read"; } }

        /// <summary>自定义模式要写入的策略正文：null 表示尚未编辑，空串表示删除全部桶策略。</summary>
        internal string Policy { get; private set; }

        /// <summary>按读取结果装配选项；三种模式都可用，覆盖既有自定义授权需要单独勾选确认。</summary>
        /// <param name="name">桶名称。</param><param name="snapshot">刚读取并展示给用户的权限快照。</param>
        internal BucketPermissionDialog(string name, OssBucketState snapshot) : this(name, snapshot, true) { }

        /// <summary>按读取结果装配选项。</summary>
        /// <param name="name">桶名称。</param><param name="snapshot">刚读取并展示给用户的权限快照。</param><param name="canEditPolicy">该后端是否有 Bucket Policy 可逐条编辑。</param>
        internal BucketPermissionDialog(string name, OssBucketState snapshot, bool canEditPolicy)
        {
            bucket = name;
            state = snapshot;
            policyEditable = canEditPolicy;
            Text = "桶访问权限"; Width = 582; Height = 540; StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            UiStyle.ApplyWindow(this);
            var info = new Label { Left = 18, Top = 12, Width = 546, Height = 42, Text = "桶：" + name + "\r\n当前：" + Describe(state) };
            detail.Text = OssBucketClient.DescribeAccess(state.Policy, state.Acl);
            var detailLabel = new Label { Left = 20, Top = 176, Width = 360, Text = "当前授权明细（只读，账号标识已掩码）", ForeColor = UiStyle.TextMuted };
            if (state.Mode == "private") privateMode.Checked = true;
            else if (state.Mode == "public-read") publicMode.Checked = true;
            else customMode.Checked = true;
            if (!policyEditable)
            {
                customMode.Enabled = false;
                edit.Enabled = false;
                note.Text = "该后端用桶 ACL 管理访问权限，没有 Bucket Policy 可逐条编辑；只能在私有与公共只读之间切换。对象 ACL、IAM 与公共访问阻止仍需到服务端控制台处理。";
            }
            var count = OssPolicyDoc.CountStatements(state.Policy);
            overwrite.Text = count > 0 ? "我确认放弃现有 " + count + " 条桶策略声明，写入后无法恢复" : "我确认放弃现有非标准的桶授权，写入后无法恢复";
            overwrite.CheckedChanged += delegate { UpdateActions(); };
            foreach (var radio in new[] { privateMode, publicMode, customMode }) radio.CheckedChanged += delegate { UpdateActions(); };
            edit.Click += delegate { OpenEditor(); };
            ok.Click += delegate { Accept(); };
            Controls.AddRange(new Control[] { info, privateMode, publicMode, customMode, edit, overwrite, detailLabel, detail, note, ok, cancelDialog });
            AcceptButton = ok; CancelButton = cancelDialog;
            UpdateActions();
        }

        /// <summary>打开可视化策略编辑器；只有编辑器校验通过才带回正文。</summary>
        private void OpenEditor()
        {
            if (!policyEditable) return;
            used = true;
            using (var editor = new BucketPolicyDialog(bucket, state))
            {
                if (editor.ShowDialog(this) != DialogResult.OK) return;
                Policy = editor.Desired;
            }
            note.Text = Policy == null ? "编辑未通过校验，没有带回正文，重新编辑或取消。"
                : Policy == "" ? "已在编辑器中确认：删除该桶的全部 Bucket Policy，对象只保留认证账号访问。"
                : "已在编辑器中确认新策略（" + OssPolicyDoc.CountStatements(Policy) + " 条声明），点“保存”后写入前还会重新读取比对。";
            UpdateActions();
        }

        private void Accept()
        {
            if (!ok.Enabled) return;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>按钮可用性把“覆盖自定义授权”和“未编辑就保存”两类误操作挡在界面里。</summary>
        private void UpdateActions()
        {
            var custom = customMode.Checked;
            edit.Enabled = custom && policyEditable;
            overwrite.Visible = state.Mode == "custom" && !custom;
            if (custom)
            {
                if (Policy == null && !used)
                    note.Text = policyEditable ? "选“编辑策略…”按整体起点改权限，界面右侧实时显示将要写入的正文；未编辑时不写入任何内容。" : note.Text;
                ok.Enabled = Policy != null;
                return;
            }
            // 改回简化模式后丢弃编辑器带回的正文，避免一次保存同时带着两种意图。
            Policy = null; used = false;
            ok.Enabled = state.Mode != "custom" || overwrite.Checked;
            if (Target == state.Mode) note.Text = "当前已是该模式，点“保存”不会写入任何改动。";
            else if (state.Mode == "custom" && !overwrite.Checked) note.Text = "当前是自定义授权：先勾选放弃既有授权的确认，否则不写入。";
            else if (!policyEditable) note.Text = "该后端用桶 ACL 管理访问权限，没有 Bucket Policy 可逐条编辑；只能在私有与公共只读之间切换。对象 ACL、IAM 与公共访问阻止仍需到服务端控制台处理。";
            else note.Text = "只维护桶 Policy（阿里云 OSS 为桶 ACL）；对象 ACL、IAM、公共访问阻止和显式拒绝仍会改变最终访问结果，保存后会读回核验。更细的改动请选“自定义”。";
        }

        /// <summary>将模式与限制原因转成一句界面文本，不宣称完整有效权限已核验。</summary>
        /// <param name="state">权限快照。</param><returns>模式和限制。</returns>
        internal static string Describe(OssBucketState state)
        {
            return (state.Mode == "private" ? "私有（桶策略/ACL）" : state.Mode == "public-read" ? "公共只读（桶策略/ACL）" : "自定义或复杂访问策略")
                + (state.Reason == "" ? "" : " · " + state.Reason);
        }
    }
}
