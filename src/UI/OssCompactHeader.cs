using System;
using System.Drawing;
using System.Windows.Forms;

namespace RcloneOssManager
{
    internal sealed partial class OssFileForm
    {
        /// <summary>“更多”菜单里打开本地位置的文案；不再绑定某个具体文件管理器。</summary>
        internal const string OpenInManagerText = "在文件管理器中打开";

        /// <summary>低频操作与传输设置集中到更多菜单，不再永久占用两排空间。</summary>
        private readonly ContextMenuStrip moreMenu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 9.5F) };

        /// <summary>建桶入口只在连接根目录（桶列表）可用，用于在菜单展开时重新判定。</summary>
        private ToolStripMenuItem createBucket;

        /// <summary>常用文件操作和筛选同排，取消统计仍是直接可见的独立入口。</summary>
        /// <returns>固定一行且按窗口宽度伸展的顶部布局。</returns>
        private Control InitCompactHeader()
        {
            foreach (Control button in toolbar.Controls)
            {
                button.Size = new Size(UiStyle.ButtonWidth, UiStyle.ButtonHeight);
                button.Margin = new Padding(0, 0, button.Text == "新建目录" ? 14 : 6, 0);
            }
            moreMenu.Renderer = new UiStyle.StripeMenuRenderer();
            AddMoreAction("上传文件夹", UploadFolder);
            AddMoreAction("下载地址…", DownloadAddress);
            moreMenu.Items.Add(new ToolStripSeparator());
            AddMoreAction(OpenInManagerText, OpenMounted);
            if (bucketBased)
            {
                createBucket = new ToolStripMenuItem("新建桶…");
                createBucket.Click += delegate { if (!Busy) CreateBucket(); };
                moreMenu.Items.Add(createBucket);
                AddMoreAction("桶访问权限设置…", BucketPermissions);
                if (files.PolicyEditable) AddMoreAction("桶策略编辑…", BucketPolicyEdit);
                AddMoreAction("设置到期删除…", SetObjectExpiry);
            }
            moreMenu.Items.Add(new ToolStripSeparator());
            var settings = new ToolStripMenuItem("传输设置");
            var options = new TableLayoutPanel { Name = "transferOptions", Size = new Size(230, 66), ColumnCount = 2, RowCount = 2, Padding = new Padding(4), Margin = new Padding(0), Font = Font, BackColor = Color.FromArgb(245, 247, 251) };
            options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
            options.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); options.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            overwrite.Margin = new Padding(4, 3, 0, 0); options.Controls.Add(overwrite, 0, 0); options.SetColumnSpan(overwrite, 2);
            options.Controls.Add(new Label { Text = "并行文件（1–4）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(4, 0, 0, 0) }, 0, 1);
            parallelDownloads.Margin = new Padding(4, 0, 4, 0); options.Controls.Add(parallelDownloads, 1, 1);
            settings.DropDownItems.Add(new ToolStripControlHost(options) { Margin = new Padding(0), Padding = new Padding(0) }); moreMenu.Items.Add(settings);
            // 菜单本身不属于文件右键菜单，必须单独保留Busy门禁，禁止绕过正在执行的任务。
            moreMenu.Opening += delegate
            {
                foreach (ToolStripItem item in moreMenu.Items) item.Enabled = !Busy;
                if (createBucket != null) createBucket.Enabled = !Busy && currentPath == "";
            };
            var more = MainForm.ButtonOf("更多 ▾", UiStyle.AccentSoft, UiStyle.AccentText);
            more.Name = "moreActions"; more.Size = new Size(UiStyle.ButtonWidth, UiStyle.ButtonHeight); more.Margin = new Padding(0, 0, 6, 0);
            more.Click += delegate { moreMenu.Show(more, new Point(0, more.Height)); };
            toolbar.Controls.Add(more); toolbar.Controls.Add(cancelUsage);
            var header = new Panel { Name = "compactHeader", Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(6, 0, 6, 0) };
            header.Controls.Add(toolbar);
            FormClosed += delegate { moreMenu.Dispose(); };
            return header;
        }

        /// <summary>更多菜单复用已有操作入口，点击前再次检查任务状态以避免菜单打开期间状态变化。</summary>
        /// <param name="text">菜单显示名称。</param><param name="action">已有操作。</param>
        private void AddMoreAction(string text, Action action)
        {
            moreMenu.Items.Add(text, null, delegate { if (!Busy) action(); });
        }
    }
}
