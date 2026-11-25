using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>独立的轻量文件窗口，与连接管理共享配置，不依赖挂载盘符。</summary>
    internal sealed partial class OssFileForm : Form
    {
        // 当前连接及其文件客户端；对象路径始终区分大小写。
        private readonly OssFileClient files;
        private readonly string remote;

        /// <summary>文件列表各列96DPI最小宽度：大小与修改时间按最宽内容取固定值，剩余宽度全部给名称列。</summary>
        internal static readonly int[] FileMinimumWidths = { 110, 100, 64, 168, 140 };
        /// <summary>文件列表弹性权重，只有名称列参与扩展。</summary>
        internal static readonly int[] FileColumnWeights = { 10, 0, 0, 0, 0 };
        /// <summary>嵌入主窗口右侧面板时为真；此时不做独立窗口的屏幕定位。</summary>
        private readonly bool embedded;

        private readonly bool bucketBased;
        private readonly TextBox pathBox = new HintTextBox();
        private readonly TextBox filter = new HintTextBox { Hint = "关键字查询", AccessibleName = "关键字查询", Width = 330 };
        private readonly BufferedListView list = new BufferedListView();
        private readonly FlowLayoutPanel toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
        private readonly CheckBox overwrite = new CheckBox { Text = "同名时替换", AutoSize = true, Checked = true, Margin = new Padding(8, 9, 4, 0) };
        private readonly TextBox history = new TextBox { Dock = DockStyle.Bottom, Height = 100, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private readonly Label status = new Label { Dock = DockStyle.Fill, Height = 26, AutoEllipsis = true, Padding = new Padding(4, 3, 4, 0), Margin = new Padding(0) };
        /// <summary>状态栏当前展示的文件绝对路径；非空时点击状态栏即可复制。</summary>
        private string statusFileUrl;
        private readonly Button cancel;
        /// <summary>统计时的取消入口不依赖下载任务区，空下载区保持隐藏。</summary>
        private readonly Button cancelUsage;
        /// <summary>本窗口最近的手动统计；刷新或文件写操作后清除，不冒充实时占用。</summary>
        private readonly Dictionary<string, OssUsage> usage = new Dictionary<string, OssUsage>(StringComparer.Ordinal);
        /// <summary>失败时自动展开任务详情，避免错误被底部省略号隐藏。</summary>
        private readonly CheckBox showHistory = new CheckBox { Text = "错误详情", AutoSize = true, Margin = new Padding(10, 9, 0, 0) };
        private readonly List<Button> actions = new List<Button>();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private List<OssEntry> entries = new List<OssEntry>();
        private CancellationTokenSource active;
        private string currentPath = "";
        private bool closeAfterCancel;
        private string localFolder;
        /// <summary>逐文件任务列表，下载时保持可选择和单项取消。</summary>
        private readonly BufferedListView downloads = new BufferedListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, Font = new Font("Microsoft YaHei UI", 11F) };
        /// <summary>本批独立取消令牌的队列，仅在执行期间保留。</summary>
        private OssDownloadQueue downloadQueue;
        /// <summary>下载列表区域和布局，队列结束后保留结果供检查；以 Dock=Bottom 挂在文件列表下方，收起时高度归零。</summary>
        private readonly Panel downloadPanel = new Panel { Dock = DockStyle.Bottom, Height = 0, Visible = true, Margin = new Padding(0), BackColor = UiStyle.Card };
        /// <summary>任务区操作按钮及详情布局，不受远端Busy禁用限制。</summary>
        private readonly FlowLayoutPanel taskTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
        private readonly TableLayoutPanel taskLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
        private readonly Button downloadFolder;
        /// <summary>仅对本批尚未结束的选中项启用取消。</summary>
        private readonly Button cancelOne;
        /// <summary>当前下载队列可同时传输的文件数，避免未经限制地占满连接。</summary>
        private readonly NumericUpDown parallelDownloads = new NumericUpDown { Name = "parallelDownloads", Minimum = 1, Maximum = 4, Value = 3, Width = 54, TextAlign = HorizontalAlignment.Center, Margin = new Padding(4, 5, 8, 0) };
        /// <summary>文件区宿主：浏览器占满剩余空间，任务区以 Dock=Bottom 叠在下方，收起时列表自然占满不留空白。</summary>
        private readonly Panel filesHost = new Panel { Name = "filesHost", Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiStyle.Card };
        /// <summary>选中任务的实际时间、完整路径和原因，支持选择复制。</summary>
        private readonly TextBox taskDetails = new TextBox { Name = "taskDetails", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = UiStyle.Hover, Margin = new Padding(4) };

        /// <summary>是否存在正在列举、传输或验证的任务。</summary>
        internal bool Busy { get { return active != null; } }

        /// <summary>本窗口管理的连接内部标识，主窗口切换连接时用于对账。</summary>
        internal string ConnectionName { get { return remote; } }

        /// <summary>同步友好名称，只更新标题，不改变当前连接、目录或正在进行的任务。</summary>
        /// <param name="label">连接显示名称。</param>
        internal void SetDisplayName(string label) { Text = "OSS 文件管理 — " + label; }

        /// <summary>构建文件操作窗口，不访问远端；显示后才加载根目录。嵌入主窗口时跳过独立窗口定位。</summary>
        /// <param name="client">现有连接客户端。</param><param name="name">连接名称。</param>
        /// <param name="usesBuckets">是否使用存储桶。</param><param name="mountedDrive">可选的已挂载盘符。</param>
        /// <param name="autoLoad">显示后是否自动加载，隔离测试可关闭。</param>
        /// <param name="embedded">是否嵌入主窗口右侧，作为面板而非独立窗口。</param>
        internal OssFileForm(RcloneClient client, string name, bool usesBuckets, string mountedDrive = null, bool autoLoad = true, bool embedded = false)
        {
            files = new OssFileClient(client, name, usesBuckets); remote = name; bucketBased = usesBuckets;
            // 参数与字段同名，显式保存嵌入标记供OnShown等成员判断。
            this.embedded = embedded;
            SetDisplayName(name); StartPosition = FormStartPosition.CenterParent; Icon = RuntimePaths.LoadIcon();
            Size = new Size(1080, 760); MinimumSize = new Size(900, 600); Font = new Font("Microsoft YaHei UI", 9.5F);
            KeyPreview = true; AutoScaleMode = AutoScaleMode.Dpi;
            UiStyle.ApplyWindow(this);
            var address = InitAddressBar();
            AddAction("上传文件", UploadFiles);
            AddAction("新建目录", CreateDirectory); AddAction("重命名", RenameSelected); AddAction("多选删除", DeleteSelected);
            AddAction("本地预览", PreviewSelected);
            if (embedded) { TopLevel = false; FormBorderStyle = FormBorderStyle.None; MinimumSize = new Size(400, 300); }

            cancelUsage = MainForm.ButtonOf("取消统计", UiStyle.AccentSoft, UiStyle.AccentText);
            cancelUsage.Size = new Size(UiStyle.ButtonWidth, UiStyle.ButtonHeight); cancelUsage.Margin = new Padding(0, 0, 6, 0); cancelUsage.Visible = false;
            cancelUsage.Click += delegate { Cancel(); };
            downloadFolder = MakeButton("打开下载目录", () => OpenLocalFolder(SelectedDownloadFolder())); downloadFolder.Name = "downloadFolder"; downloadFolder.Enabled = false;
            cancel = MakeButton("取消任务", Cancel); cancel.Enabled = false;
            var header = InitCompactHeader();

            list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.HideSelection = false; list.MultiSelect = true;
            list.GridLines = false; list.Font = new Font("Microsoft YaHei UI", 11F); list.ShowItemToolTips = false;
            list.Columns.Add("名称", 430); list.Columns.Add("大小", 110); list.Columns.Add("类型", 80); list.Columns.Add("修改时间", 175); list.Columns.Add("操作", 168);
            list.Columns[1].TextAlign = HorizontalAlignment.Right; list.BorderStyle = BorderStyle.None; list.BackColor = UiStyle.Card; list.Margin = new Padding(0);
            // 大小、类型、修改时间只按最宽内容取固定宽（1023.9 GiB、存储桶、2026-11-19 20:19:00，自绘单元格左右各留4像素），操作列固定四段图标宽；
            // 窗口变宽时剩余空间全部给名称列，避免时间和大小两侧留大片空白（WinForms ListView 不支持原生冻结列）。
            UiStyle.BindColumns(list, FileMinimumWidths, FileColumnWeights);
            list.ContextMenuStrip = menu;
            menu.Renderer = new UiStyle.StripeMenuRenderer(); menu.Font = new Font("Microsoft YaHei UI", 10F); menu.ShowImageMargin = false;
            menu.Items.Add("进入目录", null, delegate { EnterSelected(); });
            menu.Items.Add("新建目录（Ctrl+Shift+N）", null, delegate { CreateDirectory(); });
            menu.Items.Add("统计桶 / 目录占用…", null, delegate { CountUsage(); });
            if (bucketBased) menu.Items.Add("桶访问权限设置…", null, delegate { BucketPermissions(); });
            menu.Items.Add("下载所选文件 / 目录", null, delegate { DownloadFiles(); });
            menu.Items.Add("本地预览（空格）", null, delegate { PreviewSelected(); });
            menu.Items.Add("复制共享链接（7天有效）", null, delegate { ShareSelected(); });
            menu.Items.Add("对象属性（Alt+Enter）", null, delegate { ShowProperties(); });
            menu.Items.Add("重命名文件（F2）", null, delegate { RenameSelected(); });
            var deleteMenu = menu.Items.Add("删除所选文件 / 目录（Del）…", null, delegate { DeleteSelected(); }); deleteMenu.ForeColor = UiStyle.DangerText;
            menu.Items.Add("复制绝对链接", null, delegate { CopyPath(); });
            menu.Items.Add("复制rclone路径", null, delegate { CopyPath(false); });
            list.ItemActivate += delegate { if ((DateTime.UtcNow - lastRowAction).TotalMilliseconds > 500) EnterSelected(); };
            AllowDrop = list.AllowDrop = true;
            DragEventHandler dragEnter = delegate(object sender, DragEventArgs e) { e.Effect = !Busy && e.Data.GetDataPresent(DataFormats.FileDrop) ? e.AllowedEffect & DragDropEffects.Copy : DragDropEffects.None; };
            DragEventHandler dragDrop = async delegate(object sender, DragEventArgs e) { if (!Busy && e.Data.GetDataPresent(DataFormats.FileDrop)) await UploadDropped((string[])e.Data.GetData(DataFormats.FileDrop)); };
            DragEnter += dragEnter; DragOver += dragEnter; DragDrop += dragDrop;
            list.DragEnter += dragEnter; list.DragOver += dragEnter; list.DragDrop += dragDrop;
            list.ItemDrag += delegate(object sender, ItemDragEventArgs e) { if (e.Button == MouseButtons.Left && !Busy) DragDownload(); };
            list.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                var item = list.GetItemAt(e.X, e.Y);
                if (item != null && !item.Selected) { list.SelectedItems.Clear(); item.Selected = true; }
            };
            filter.TextChanged += delegate { pageIndex = 0; RenderEntries(); };
            pathBox.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; NavigateAddress(); } };

            downloads.Columns.Add("文件", 185); downloads.Columns.Add("状态", 82); downloads.Columns.Add("进度", 116); downloads.Columns.Add("速度", 96); downloads.Columns.Add("带宽", 92); downloads.Columns.Add("开始时间", 132); downloads.Columns.Add("完成时间", 132); downloads.Columns.Add("本地位置", 260); downloads.Columns.Add("结果 / 原因", 220);
            downloads.BorderStyle = BorderStyle.None; downloads.Margin = new Padding(0); downloads.ShowItemToolTips = true;
            UiStyle.SetRowHeight(downloads, 30);
            UiStyle.BindColumns(downloads, new[] { 120, 76, 90, 80, 80, 170, 170, 150, 120 }, new[] { 4, 1, 1, 1, 1, 1, 1, 4, 3 });
            downloads.OwnerDraw = true;
            downloads.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e) { UiStyle.DrawHeader(e); };
            downloads.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { if (downloads.View != View.Details) e.DrawDefault = true; };
            downloads.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e)
            {
                var item = e.Item.Tag as OssDownloadItem;
                if (e.ColumnIndex != 2 || item == null) { UiStyle.DrawCell(e, downloads.HoveredIndex); return; }
                DrawDownloadProgress(e.Graphics, e.Bounds, item, downloads.Font, e.Item.Selected ? UiStyle.Selection : e.Item.BackColor);
            };
            taskTools.Controls.Add(new Label { Text = "任务记录", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(6, 8, 16, 0) });
            taskTools.Controls.Add(downloadFolder);
            cancelOne = MakeButton("取消选中下载", CancelSelectedDownloads); cancelOne.Enabled = false; taskTools.Controls.Add(cancelOne);
            taskTools.Controls.Add(cancel); taskTools.Controls.Add(showHistory);
            InitTaskActions();
            taskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); taskLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); taskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); taskLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
            taskLayout.Controls.Add(taskTools, 0, 0); taskLayout.Controls.Add(downloads, 0, 1); taskLayout.Controls.Add(taskDetails, 0, 2); taskLayout.Controls.Add(history, 0, 3); downloadPanel.Controls.Add(taskLayout);
            downloads.SelectedIndexChanged += delegate { UpdateTaskDetails(); UpdateTaskPanelVisibility(); };
            UpdateTaskDetails();
            var browser = new TableLayoutPanel { Name = "fileBrowser", Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = UiStyle.Card, Padding = new Padding(6), Margin = new Padding(0) };
            browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); browser.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); browser.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            browser.Controls.Add(address, 0, 0); browser.Controls.Add(list, 0, 2);
            InitBrowser(browser);
            downloadPanel.Padding = new Padding(0, 6, 0, 0);
            // 先加浏览器后加任务区：WinForms 从集合末尾开始应用停靠，任务区先占底部，浏览器填充剩余空间。
            filesHost.Controls.Add(browser);
            filesHost.Controls.Add(downloadPanel);
            var layout = new TableLayoutPanel { Name = "fileLayout", Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.Controls.Add(header, 0, 0); layout.Controls.Add(filesHost, 0, 1); layout.Controls.Add(status, 0, 2); Controls.Add(layout);
            history.Dock = DockStyle.Fill; history.BorderStyle = BorderStyle.FixedSingle; history.BackColor = UiStyle.Card; history.Visible = false;
            showHistory.CheckedChanged += delegate { history.Visible = showHistory.Checked; UpdateTaskLayout(); };
            Shown += delegate { UpdateTaskLayout(); };
            status.Text = "无需挂载 · F5刷新 · F2改名 · Del删除 · Ctrl+Shift+N创建目录";
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!Busy) return;
                e.Cancel = true;
                if (MessageBox.Show(this, "任务正在执行。取消任务并关闭窗口？\r\n已完成的文件不会回滚，重命名中断可能保留新旧两个对象。", "关闭文件管理", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                { closeAfterCancel = true; Cancel(); }
            };
            FormClosed += delegate { menu.Dispose(); };
            if (autoLoad) Shown += delegate { Navigate(""); };
        }

        /// <summary>任务区以固定高度挂在文件列表下方；无任务时高度归零，文件列表靠 Dock 停靠自动占满，不残留空白。</summary>
        private void UpdateTaskLayout()
        {
            var scale = DeviceDpi / 96F;
            var taskFixed = (int)(210 * scale);
            var show = downloads.Items.Count > 0;
            taskTools.Visible = show; taskDetails.Visible = show;
            downloadPanel.Height = show ? taskFixed : 0;
            if (show) taskLayout.RowStyles[3].Height = showHistory.Checked ? Math.Min(70 * scale, Math.Max(24 * scale, taskFixed / 6)) : 0;
        }

        /// <summary>任务区可见性并入 UpdateTaskLayout 统一处理；保留方法供旧调用点使用。</summary>
        private void UpdateTaskPanelVisibility()
        {
            UpdateTaskLayout();
        }

        /// <summary>直接显示选中任务详情，并禁止对历史或已结束任务执行取消。</summary>
        private void UpdateTaskDetails()
        {
            if (IsDisposed || Disposing) return;
            var selected = downloads.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag as OssDownloadItem).Where(x => x != null).ToList();
            cancelOne.Enabled = downloadQueue != null && selected.Any(x => downloadQueue.Items.Contains(x) && !x.Finished && x.State != "正在取消");
            var item = selected.FirstOrDefault();
            var text = item == null ? "选中任务可查看下载时间、完整路径和原因（可选择复制）" :
                "开始：" + (item.StartedAt.HasValue ? item.StartedAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") + "（未开始）") +
                "　结束：" + (item.EndedAt.HasValue ? item.EndedAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : "—") +
                "\r\n路径：" + item.LocalPath + "\r\n" + item.State + " · " + item.Detail;
            if (taskDetails.Text == text) return;
            // 更新运行中的选中项时保留复制选区，避免进度刷新打断用户选择。
            var start = taskDetails.SelectionStart; var length = taskDetails.SelectionLength;
            taskDetails.Text = text; taskDetails.Select(Math.Min(start, text.Length), Math.Min(length, text.Length - Math.Min(start, text.Length)));
        }

        /// <summary>优先打开选中历史下载所在目录，否则使用最近下载位置。</summary>
        /// <returns>本地目录，不受远端任务忙碌限制。</returns>
        private string SelectedDownloadFolder()
        {
            var item = downloads.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag as OssDownloadItem).FirstOrDefault();
            return item != null && item.LocalPath != "" ? Path.GetDirectoryName(item.LocalPath) : localFolder;
        }

        /// <summary>绘制单项进度条及百分比，终态颜色区分成功、取消和失败。</summary>
        /// <param name="graphics">目标画布。</param><param name="bounds">进度单元格。</param><param name="item">真实任务状态。</param>
        /// <param name="font">列表字体。</param><param name="background">当前行或选中背景。</param>
        internal static void DrawDownloadProgress(Graphics graphics, Rectangle bounds, OssDownloadItem item, Font font, Color background)
        {
            using (var brush = new SolidBrush(background)) graphics.FillRectangle(brush, bounds);
            var bar = Rectangle.Inflate(bounds, -6, -4); if (bar.Width <= 0 || bar.Height <= 0) return;
            using (var brush = new SolidBrush(UiStyle.ProgressTrack)) graphics.FillRectangle(brush, bar);
            var color = item.State == "已完成" ? UiStyle.ProgressDone : item.State == "失败" ? UiStyle.ProgressFail : item.State == "已取消" || item.State == "正在取消" ? Color.Silver : UiStyle.ProgressRunning;
            var fill = bar; fill.Width = (int)(bar.Width * (item.Percent ?? 0) / 100);
            if (fill.Width > 0) using (var brush = new SolidBrush(color)) graphics.FillRectangle(brush, fill);
            TextRenderer.DrawText(graphics, item.PercentText, font, bar, UiStyle.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var pen = new Pen(UiStyle.BorderSoft)) graphics.DrawLine(pen, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom - 1);
        }

        /// <summary>独立窗口按父窗口或屏幕工作区居中；嵌入面板时位置由父容器接管。两种模式显示后都需按最终尺寸重算布局。</summary>
        /// <param name="e">首次显示事件。</param>
        protected override void OnShown(EventArgs e)
        {
            UpdateTaskLayout();
            if (embedded) { base.OnShown(e); return; }
            var area = Screen.FromControl(Owner ?? this).WorkingArea;
            var bounds = Owner != null && Owner.Visible && Owner.WindowState != FormWindowState.Minimized ? Owner.Bounds : area;
            Location = new Point(Math.Max(area.Left, Math.Min(area.Right - Width, bounds.Left + (bounds.Width - Width) / 2)), Math.Max(area.Top, Math.Min(area.Bottom - Height, bounds.Top + (bounds.Height - Height) / 2)));
            base.OnShown(e);
        }

        /// <summary>创建统一按钮。</summary>
        /// <param name="text">按钮文案。</param><param name="action">操作。</param><returns>按钮。</returns>
        private Button MakeButton(string text, Action action)
        {
            var button = MainForm.ButtonOf(text, UiStyle.AccentSoft, UiStyle.AccentText);
            button.Size = new Size(UiStyle.ButtonWidth, UiStyle.ButtonHeight); button.Margin = new Padding(0, 0, 6, 0);
            if (text == "上传文件" || text == "下载文件") { button.BackColor = UiStyle.Accent; button.ForeColor = UiStyle.OnAccent; }
            if (text == "删除文件" || text == "多选删除") { button.BackColor = UiStyle.DangerSoft; button.ForeColor = UiStyle.DangerText; }
            button.Click += delegate { if (text == "取消任务" || text == "取消选中下载" || text == "打开下载目录" || !Busy) action(); };
            return button;
        }

        /// <summary>添加任务期间禁用的工具栏按钮。</summary>
        /// <param name="text">文案。</param><param name="action">操作。</param><param name="group">目标分组，未指定时放入远端操作栏。</param>
        private void AddAction(string text, Action action, FlowLayoutPanel group = null) { var button = MakeButton(text, action); actions.Add(button); (group ?? toolbar).Controls.Add(button); if (text == "删除文件") { button.ForeColor = UiStyle.DangerText; button.Font = new Font(button.Font, FontStyle.Bold); } }

        /// <summary>统一处理快捷键；文本框中的Delete和Backspace仍用于编辑文本。</summary>
        /// <param name="msg">窗口消息。</param><param name="keyData">按键和修饰键。</param><returns>是否已处理。</returns>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { if (!Busy) Navigate(currentPath); return true; }
            if (keyData == Keys.Escape && Busy) { Cancel(); return true; }
            if (Busy) return base.ProcessCmdKey(ref msg, keyData);
            if (keyData == (Keys.Alt | Keys.Left)) { MoveHistory(-1); return true; }
            if (keyData == (Keys.Alt | Keys.Right)) { MoveHistory(1); return true; }
            if (keyData == (Keys.Alt | Keys.Enter)) { ShowProperties(); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.N)) { CreateDirectory(); return true; }
            if (list.ContainsFocus)
            {
                if (keyData == (Keys.Control | Keys.A)) { SelectCurrentPage(true); return true; }
                if (keyData == Keys.Space) { PreviewSelected(); return true; }
                if (keyData == Keys.F2) { RenameSelected(); return true; }
                if (keyData == Keys.Delete) { DeleteSelected(); return true; }
                if (keyData == Keys.Back || keyData == (Keys.Alt | Keys.Up)) { GoParent(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>异步进入指定路径；加载失败保留旧列表及旧路径。</summary>
        /// <param name="path">连接内相对路径。</param>
        internal async void Navigate(string path)
        {
            if (Busy) return;
            try { OssFileClient.ValidatePath(path); }
            catch (ArgumentException ex) { ShowError(ex.Message); return; }
            await Execute("加载目录", async (token, progress) =>
            {
                var loaded = await Task.Run(() => files.List(path, token));
                var changed = currentPath != path;
                currentPath = path; pathBox.Text = DisplayPath(path); entries = loaded; usage.Clear();
                // 切层后旧队列路径失效，一并清空；在途统计按完整路径回填，不会混层。
                usageQueue.Clear(); pendingUsage.Clear();
                permissionPath = ""; permissionText = "";
                if (changed) { list.SelectedItems.Clear(); filter.Text = ""; }
                pageIndex = 0; RecordPath(path); RenderNavigation();
                RenderEntries();
                await LoadQuickUsage(token);
            }, false);
            requestedHistory = -1;
            if (pendingNotice != null) { status.Text = pendingNotice; pendingNotice = null; }
            RefreshBucketInfo();
        }

        /// <summary>显示本层名称筛选结果，不进行后台递归搜索。</summary>
        private void RenderEntries()
        {
            UpdateActionHover(null, -1, "");
            list.BeginUpdate();
            try
            {
                var selected = new HashSet<string>(list.SelectedItems.Cast<ListViewItem>().Where(x => x.Tag is OssEntry).Select(x => ((OssEntry)x.Tag).Path), StringComparer.Ordinal);
                list.Items.Clear();
                // 顶级目录没有上一级，不显示".."行；进入子目录后首行固定为".."。
                if (!String.IsNullOrEmpty(currentPath)) list.Items.Add(new ListViewItem(new[] { "..", "", "上一级", "", "" }) { ForeColor = UiStyle.AccentText });
                var page = PageEntries().ToList();
                foreach (var item in page)
                {
                    var key = String.IsNullOrEmpty(currentPath) ? item.Path : currentPath + "/" + item.Path;
                    OssUsage total; usage.TryGetValue(key, out total);
                    var row = new ListViewItem(new[] { item.Name, item.IsDir ? (total == null ? "—" : total.SizeText) : OssFileClient.Bytes(item.Size), item.IsBucket ? "存储桶" : item.IsDir ? "目录" : "文件", FormatEntryTime(item), "" });
                    if (total != null) row.ToolTipText = total.Summary;
                    row.Tag = item; row.BackColor = UiStyle.StripeColor(list.Items.Count); list.Items.Add(row); row.Selected = selected.Contains(item.Path);
                    // 目录大小慢速回填：缺统计的目录进后台队列，由计时器约3秒一个逐个获取。
                    if (item.IsDir && total == null && pendingUsage.Add(key)) usageQueue.Enqueue(key);
                }
                // 空目录和筛选无结果都补一行说明，避免整片空白看起来像加载失败。
                if (page.Count == 0) list.Items.Add(new ListViewItem(new[] { entries.Count == 0 ? "此位置没有对象" : "没有名称包含“" + filter.Text + "”的条目", "", "", "", "" }) { ForeColor = UiStyle.TextMuted, Tag = EmptyRow });
            }
            finally { list.EndUpdate(); }
            if (usageQueue.Count > 0 && !usageFillTimer.Enabled)
            {
                usageFillTimer.Start();
                if (!usageFilling) UiStyle.SetStatus(status, "正在后台低频统计目录大小（约3秒一个），完成后自动填充。", "info");
            }
        }

        /// <summary>将后端更新时间转换为本机时区；缺失、无效或零值时间不显示虚构日期。</summary>
        /// <param name="value">后端的带时区时间。</param><returns>精确到秒的本地时间或占位符。</returns>
        internal static string FormatTime(string value)
        {
            DateTimeOffset time;
            if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time) || time.Year <= 1) return "—";
            return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>进入唯一选中的目录，文件双击不自动下载或执行。</summary>
        private void EnterSelected()
        {
            if (Busy || list.SelectedItems.Count != 1) return;
            var entry = list.SelectedItems[0].Tag as OssEntry;
            if (entry == null) { if (list.SelectedItems[0].Tag == EmptyRow) return; GoParent(); return; }
            if (entry.IsDir) { try { Navigate(OssFileClient.Join(currentPath, entry.Path)); } catch (ArgumentException ex) { ShowError(ex.Message); } }
            else status.Text = "请点击最后一列“下载”或右键下载；不会自动执行远端文件。";
        }

        /// <summary>返回上一级目录。</summary>
        /// <summary>返回上一级目录；已在根目录时不重复加载。</summary>
        private void GoParent()
        {
            if (currentPath == "") { status.Text = "已位于根目录。"; return; }
            var index = currentPath.LastIndexOf('/'); Navigate(index < 0 ? "" : currentPath.Substring(0, index));
        }

        /// <summary>确定唯一统计范围；不把筛选、多选或上一级导航误当成整个桶。</summary>
        /// <returns>所选桶/目录或当前目录；无法确定时抛出参数异常。</returns>
        private string UsagePath()
        {
            if (list.SelectedItems.Count > 1) throw new ArgumentException("请只选择一个桶或目录统计，避免重复扫描。");
            var path = currentPath;
            if (list.SelectedItems.Count == 1)
            {
                var entry = list.SelectedItems[0].Tag as OssEntry;
                if (entry == null || !entry.IsDir) throw new ArgumentException("请选择一个桶或目录；文件大小已经显示在列表中。");
                path = OssFileClient.Join(currentPath, entry.Path);
            }
            if (bucketBased && path == "") throw new ArgumentException("请先选中一个存储桶，或进入桶内目录再统计。");
            return path;
        }

        /// <summary>确认后在后台只读统计并回填大小列；取消不生成部分结果，也不展开空下载区。</summary>
        private async void CountUsage()
        {
            if (Busy) return;
            string path;
            try { path = UsagePath(); } catch (ArgumentException ex) { ShowError(ex.Message); return; }
            var scope = files.Target(path);
            if (bucketBased && path.IndexOf('/') < 0)
            {
                var completed = false;
                await Execute("快速查询桶大小", async (token, progress) => { await LoadQuickUsage(token); token.ThrowIfCancellationRequested(); completed = true; }, false);
                if (!completed || closeAfterCancel || IsDisposed || Disposing) return;
                OssUsage cached;
                if (usage.TryGetValue(path, out cached) && cached.Source != "")
                { UsageDialog("桶大小（服务端缓存）", "范围：" + scope + "\r\n\r\n" + cached.Summary + "\r\n\r\n直接读取服务端统计，没有列举对象。统计时间由MinIO后台扫描决定，不等于实时值，可能包含历史版本。", false); return; }
            }
            var note = "范围：" + scope + "\r\n\r\n递归列举全部子目录，统计当前可见文件的逻辑大小，不下载内容。\r\n大桶可能耗时较长，并产生服务商列举请求费用，最多等待30分钟。\r\n名称筛选不限制统计范围；不含历史版本、删除标记、未完成分片及配额/账单物理占用。\r\n扫描期间有外部修改时不是一致性快照。可点“取消统计”或按Esc中止。";
            if (!UsageDialog("确认占用统计", note, true)) return;
            usage.Remove(path); RenderEntries(); cancelUsage.Visible = true;
            OssUsage total = null; var watch = Stopwatch.StartNew();
            using (var timer = new System.Windows.Forms.Timer { Interval = 1000 })
            {
                timer.Tick += delegate { if (Busy && active != null && !active.IsCancellationRequested) status.Text = "正在递归统计 " + scope + " · 已耗时 " + watch.Elapsed.TotalSeconds.ToString("F0") + " 秒（可取消）"; };
                timer.Start();
                try
                {
                    await Execute("统计占用", async (token, progress) =>
                    {
                        total = await Task.Run(() => files.GetUsage(path, token));
                        token.ThrowIfCancellationRequested();
                        usage[path] = total; RenderEntries();
                    }, false, () => total.Summary);
                }
                finally { timer.Stop(); cancelUsage.Visible = false; }
            }
            if (total != null && usage.ContainsKey(path) && !IsDisposed && !Disposing)
                UsageDialog("占用统计结果", "范围：" + scope + "\r\n\r\n" + total.Summary + "\r\n耗时：" + watch.Elapsed.TotalSeconds.ToString("F1") + " 秒\r\n\r\n仅统计当前列举到的文件逻辑大小；不含历史版本、删除标记、未完成分片、目录标记及配额/账单物理占用。\r\n不是事务快照；刷新或文件修改会清除本窗口旧结果。", false);
        }

        /// <summary>显示可复制的统计范围、风险或结果，不使用不可选择的消息文本。</summary>
        /// <param name="caption">对话框标题。</param><param name="text">口径及结果。</param><param name="confirm">是否需要开始统计确认。</param><returns>用户是否同意开始。</returns>
        private bool UsageDialog(string caption, string text, bool confirm)
        {
            using (var dialog = new Form { Text = caption, ClientSize = new Size(670, 320), MinimumSize = new Size(570, 290), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false, MaximizeBox = false })
            {
                var details = new TextBox { Text = text, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = UiStyle.Card, Bounds = new Rectangle(16, 16, 638, 235), Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
                var ok = new Button { Text = confirm ? "开始统计" : "关闭", DialogResult = DialogResult.OK, Location = new Point(442, 273), Size = new Size(100, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
                var back = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(552, 273), Size = new Size(100, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Visible = confirm };
                dialog.Controls.AddRange(new Control[] { details, ok, back });
                dialog.AcceptButton = ok; dialog.CancelButton = confirm ? back : ok;
                return dialog.ShowDialog(this) == DialogResult.OK;
            }
        }

        /// <summary>获取当前选中的文件，拒绝目录混选。</summary>
        /// <returns>选中文件或空列表。</returns>
        private List<OssEntry> SelectedFiles()
        {
            if (list.SelectedItems.Cast<ListViewItem>().Any(x => !(x.Tag is OssEntry))) { ShowError("请在列表中选择文件或目录；“..”和空目录提示行不能操作。"); return new List<OssEntry>(); }
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(x => (OssEntry)x.Tag).ToList();
            if (selected.Count == 0 || selected.Any(x => x.IsDir)) { ShowError("此操作只支持文件；目录下载请使用操作列或右键下载。"); return new List<OssEntry>(); }
            // OSS 可包含本工具不支持的对象键，先拦截，不能从异步界面事件抛出路径异常。
            try { foreach (var item in selected) OssFileClient.ValidatePath(item.Path); }
            catch (ArgumentException ex) { ShowError(ex.Message); return new List<OssEntry>(); }
            return selected;
        }

        /// <summary>确认覆盖策略；关闭对话框时不执行操作。</summary>
        /// <returns>是否继续。</returns>
        private bool ConfirmOverwrite()
        {
            return !overwrite.Checked || MessageBox.Show(this, "已允许覆盖同名文件。确认继续？\r\n覆盖不能通过本机回收站撤销。", "确认覆盖", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        /// <summary>选择多个本地文件上传，也接受文件管理器中复制后选定的文件路径。</summary>
        private async void UploadFiles()
        {
            if (bucketBased && String.IsNullOrEmpty(currentPath)) { ShowError("请先进入一个存储桶再上传，或在路径栏输入桶名称。"); return; }
            using (var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || !ConfirmOverwrite()) return;
                var names = dialog.FileNames; var path = currentPath; var replace = overwrite.Checked;
                await Execute("上传文件", (token, progress) => Task.Run(() =>
                {
                    foreach (var local in names) { token.ThrowIfCancellationRequested(); progress(Path.GetFileName(local)); files.UploadFile(local, OssFileClient.Join(path, Path.GetFileName(local)), replace, token, progress); }
                }), true);
            }
        }

        /// <summary>上传本地目录到当前路径下同名子目录，不使用 sync。</summary>
        private async void UploadFolder()
        {
            if (bucketBased && String.IsNullOrEmpty(currentPath)) { ShowError("请先进入一个存储桶再上传。"); return; }
            using (var dialog = new FolderBrowserDialog { Description = "选择要上传的文件夹，将保留文件夹名称" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || !ConfirmOverwrite()) return;
                var local = dialog.SelectedPath; var name = new DirectoryInfo(local).Name;
                if (new DirectoryInfo(local).Parent == null) { ShowError("请选择普通文件夹，不支持上传整个磁盘根目录。"); return; }
                var target = OssFileClient.Join(currentPath, name); var replace = overwrite.Checked;
                await Execute("上传文件夹", (token, progress) => Task.Run(() => files.UploadDirectory(local, target, replace, token, progress)), true);
            }
        }

        /// <summary>拖入本地文件及目录，先检查整批路径；只复制，不移动本地源。</summary>
        /// <param name="paths">Shell提供的本地路径。</param><returns>本次上传任务。</returns>
        private async Task UploadDropped(string[] paths)
        {
            if (Busy || paths == null || paths.Length == 0) return;
            if (bucketBased && String.IsNullOrEmpty(currentPath)) { ShowError("请先进入存储桶，再拖入文件或文件夹。"); return; }
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in paths)
                {
                    var folder = Directory.Exists(path);
                    if (!folder && !File.Exists(path)) throw new ArgumentException("拖入的本地路径不存在。");
                    if (folder && new DirectoryInfo(path).Parent == null) throw new ArgumentException("不支持上传整个磁盘根目录。");
                    var name = folder ? new DirectoryInfo(path).Name : Path.GetFileName(path);
                    OssFileClient.Join(currentPath, name);
                    if (!seen.Add(name)) throw new ArgumentException("拖入项目存在同名目标，请分批上传：" + name);
                }
            }
            catch (ArgumentException ex) { ShowError(ex.Message); return; }
            if (!ConfirmOverwrite()) return;
            var parent = currentPath; var replace = overwrite.Checked;
            await Execute("拖拽上传", (token, progress) => Task.Run(() =>
            {
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested();
                    if (Directory.Exists(path)) files.UploadDirectory(path, OssFileClient.Join(parent, new DirectoryInfo(path).Name), replace, token, progress);
                    else files.UploadFile(path, OssFileClient.Join(parent, Path.GetFileName(path)), replace, token, progress);
                }
            }), true);
        }

        /// <summary>向Shell拖出虚拟文件；目标请求内容时才下载，绝不移动远端对象。</summary>
        private async void DragDownload()
        {
            var selected = SelectedFiles(); if (selected.Count == 0) return;
            var parent = currentPath;
            await Execute("拖出下载", (token, progress) =>
            {
                using (var data = new OssDragData(files, parent, selected, token, progress))
                {
                    var effect = OssDropSource.Start(data, token);
                    if (!String.IsNullOrEmpty(data.LastError)) throw new InvalidOperationException(data.LastError);
                    if (effect == 0) throw new OperationCanceledException();
                }
                return Task.FromResult(true);
            }, false);
        }

        /// <summary>解析地址栏中的HTTP(S)对象URL或remote:bucket/object路径，拒绝不安全及不完整路径。</summary>
        /// <param name="address">用户显式输入的下载地址。</param><returns>源地址与文件名，校验失败返回null。</returns>
        internal static string[] ParseDownloadAddress(string address)
        {
            if (String.IsNullOrWhiteSpace(address) || address.Any(Char.IsControl)) return null;
            var text = address.Trim(); Uri uri;
            if (Uri.TryCreate(text, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                if (!String.IsNullOrEmpty(uri.UserInfo) || String.IsNullOrEmpty(uri.Host)) return null;
                var leaf = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
                try { leaf = Uri.UnescapeDataString(leaf ?? ""); } catch { return null; }
                if (String.IsNullOrWhiteSpace(leaf) || leaf == "." || leaf == ".." || leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
                return new[] { text, leaf };
            }
            if (text.IndexOf("://", StringComparison.Ordinal) >= 0) return null;
            var colon = text.IndexOf(':');
            if (colon <= 0 || colon == text.Length - 1) return null;
            var alias = text.Substring(0, colon); var objectPath = text.Substring(colon + 1).TrimStart('/');
            if (alias.Any(c => !(Char.IsLetterOrDigit(c) || c == '_' || c == '-')) || String.IsNullOrWhiteSpace(objectPath) || objectPath.IndexOf('\\') >= 0 || objectPath.Split('/').Any(s => s == ".." || s == ".")) return null;
            var filename = objectPath.TrimEnd('/').Split('/').LastOrDefault();
            if (String.IsNullOrWhiteSpace(filename) || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            return new[] { alias + ":" + objectPath, filename, alias };
        }

        /// <summary>将地址栏输入作为下载地址；只接受当前连接的remote，按用户选择的本地目录写入。</summary>
        private async void DownloadAddress()
        {
            if (Busy) return;
            var parsed = ParseDownloadAddress(pathBox.Text);
            if (parsed == null) { ShowError("请输入完整的HTTP/HTTPS对象链接或remote:存储桶/对象路径。"); return; }
            var isUrl = parsed[0].StartsWith("http://", StringComparison.OrdinalIgnoreCase) || parsed[0].StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!isUrl && !String.Equals(parsed[2], remote, StringComparison.Ordinal)) { ShowError("地址中的remote必须与当前连接名称一致：" + remote); return; }
            using (var dialog = new FolderBrowserDialog { Description = "选择对象下载保存目录" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || !ConfirmOverwrite()) return;
                var local = LocalFile(dialog.SelectedPath, parsed[1]);
                if (Directory.Exists(local) || (!overwrite.Checked && File.Exists(local))) { ShowError("本地目标已存在或不是文件；未开始下载。"); return; }
                var entry = new OssEntry { Name = parsed[1], Path = isUrl ? parsed[0] : parsed[0].Substring(remote.Length + 1), Size = -1 };
                await DownloadSelection(new List<OssEntry> { entry }, "", dialog.SelectedPath, overwrite.Checked);
            }
        }

        /// <summary>下载所选文件到本地目录，先检查全部文件名与覆盖冲突。</summary>
        private async void DownloadFiles()
        {
            if (Busy) return;
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag as OssEntry).ToList();
            if (selected.Count == 0 || selected.Any(x => x == null || x.IsBucket)) { ShowError("请选择文件或桶内目录；不能下载连接根或整个桶。"); return; }
            using (var dialog = new FolderBrowserDialog { Description = "选择下载保存目录（目录保留完整层级）" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || !ConfirmOverwrite()) return;
                var folder = dialog.SelectedPath; var path = currentPath; var replace = overwrite.Checked;
                try
                {
                    List<OssEntry> plan = null;
                    await Execute("准备下载目录", async (token, progress) => { plan = await Task.Run(() => files.ExpandDownloads(path, selected, token)); }, false);
                    if (plan == null || closeAfterCancel || IsDisposed) return;
                    selected = plan;
                }
                catch (ArgumentException ex) { ShowError(ex.Message); return; }
                try { await DownloadSelection(selected, path, folder, replace); }
                catch (ArgumentException ex) { ShowError(ex.Message); }
            }
        }

        /// <summary>开始逐文件队列，立即启用下载目录；取消一项后继续其他文件。</summary>
        /// <param name="selected">已验证的文件。</param><param name="parent">远端目录。</param>
        /// <param name="folder">本地目标目录。</param><param name="replace">覆盖选项。</param><returns>下载任务。</returns>
        internal async Task DownloadSelection(List<OssEntry> selected, string parent, string folder, bool replace)
        {
            if (Busy) return;
            // 内部入口同样检查整批目标，防止未来调用者绕过对话框的冲突保护。
            var targets = selected.Select(x => LocalRelativeFile(folder, x.Name)).ToList();
            if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count || selected.Where((x, i) => x.IsDir ? File.Exists(targets[i]) : Directory.Exists(targets[i]) || (!replace && File.Exists(targets[i]))).Any()) throw new ArgumentException("下载目标存在同名文件或目录冲突。");
            localFolder = folder; downloadFolder.Enabled = Directory.Exists(folder);
            // 保留本窗口前几批记录；仅移除最早的历史行，当前批次不受影响。
            while (downloads.Items.Count > 500) downloads.Items.RemoveAt(0);
            foreach (ListViewItem row in downloads.Items) row.BackColor = UiStyle.StripeColor(row.Index);
            UpdateTaskLayout();
            using (var queue = new OssDownloadQueue(selected, folder, (int)parallelDownloads.Value))
            {
                downloadQueue = queue;
                foreach (var item in queue.Items) { downloads.Items.Add(new ListViewItem(new[] { item.Entry.Name, item.State, item.PercentText, item.SpeedText, item.BandwidthText, item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") + "（排队）", "—", item.LocalPath, "" }) { Tag = item, BackColor = UiStyle.StripeColor(downloads.Items.Count) }); }
                UpdateTaskPanelVisibility(); UpdateTaskDetails();
                try
                {
                    await Execute("下载队列", (token, progress) => queue.RunAsync(files, parent, folder, replace, token, UpdateDownloadRow), false, queue.Summary);
                    if (!IsDisposed && queue.Items.Any(x => x.State == "失败")) { status.ForeColor = UiStyle.DangerText; showHistory.Checked = true; }
                }
                finally { downloadQueue = null; UpdateTaskDetails(); }
            }
        }

        /// <summary>刷新对应单项行，不重建列表或丢失当前选择。</summary>
        /// <param name="item">变化的下载项。</param>
        private void UpdateDownloadRow(OssDownloadItem item)
        {
            if (IsDisposed || Disposing) return;
            var row = downloads.Items.Cast<ListViewItem>().First(x => x.Tag == item);
            row.SubItems[1].Text = item.State;
            row.SubItems[2].Text = item.PercentText; row.SubItems[3].Text = item.SpeedText; row.SubItems[4].Text = item.BandwidthText;
            row.SubItems[5].Text = item.StartedAt.HasValue ? item.StartedAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : item.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") + "（未开始）";
            row.SubItems[6].Text = item.EndedAt.HasValue ? item.EndedAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : "—";
            row.SubItems[7].Text = item.LocalPath; row.SubItems[8].Text = item.Detail;
            row.ToolTipText = "进度：" + item.PercentText + "　下载速度：" + item.SpeedText + "　等价带宽：" + item.BandwidthText + "\r\n下载时间：" + row.SubItems[5].Text + "\r\n结束时间：" + row.SubItems[6].Text + "\r\n下载路径：" + item.LocalPath + "\r\n" + item.Detail;
            row.ForeColor = item.State == "失败" ? UiStyle.DangerText : item.State == "已取消" ? UiStyle.TextMuted : UiStyle.Text;
            downloads.Invalidate(row.Bounds);
            if (row.Selected) UpdateTaskDetails();
        }

        /// <summary>只取消任务列表中的选中下载；“取消任务”仍是取消整批。</summary>
        private void CancelSelectedDownloads()
        {
            if (downloadQueue == null) return;
            foreach (ListViewItem row in downloads.SelectedItems) { var item = (OssDownloadItem)row.Tag; downloadQueue.Cancel(item); UpdateDownloadRow(item); }
        }

        /// <summary>防止远端文件名越出本地下载目录或落入 Windows 特殊设备名称。</summary>
        /// <param name="folder">用户选择的下载目录。</param><param name="name">远端文件名。</param><returns>安全本地路径。</returns>
        internal static string LocalFile(string folder, string name)
        {
            if (String.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(".") || name.EndsWith(" ") || name == "." || name == ".." ||
                System.Text.RegularExpressions.Regex.IsMatch(name.Split('.')[0].TrimEnd(' '), "^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new ArgumentException("远端文件名不能安全保存到 Windows：" + name + "。请先重命名或使用其他客户端。");
            return Path.Combine(Path.GetFullPath(folder), name);
        }

        /// <summary>重命名单文件，先复制验证再删除原对象。</summary>
        private async void RenameSelected()
        {
            var selected = SelectedFiles(); if (selected.Count != 1) { if (selected.Count > 1) ShowError("重命名一次只支持一个文件。"); return; }
            var item = selected[0]; var name = AskName(item.Name); if (name == null) return;
            if (name == item.Name) return;
            string destination;
            try { destination = RenamePath(currentPath, name); }
            catch (ArgumentException ex) { ShowError(ex.Message); return; }
            var source = OssFileClient.Join(currentPath, item.Path);
            await Execute("重命名文件", (token, progress) => Task.Run(() => files.RenameFile(source, destination, token, progress)), true);
        }

        /// <summary>创建当前目录中的子目录，桶根列表不允许创建新桶。</summary>
        private async void CreateDirectory()
        {
            if (Busy) return;
            if (bucketBased && String.IsNullOrEmpty(currentPath)) { ShowError("请先进入存储桶，再创建目录。"); return; }
            var name = AskName("", "创建目录"); if (name == null) return;
            string path;
            try { path = RenamePath(currentPath, name); }
            catch (ArgumentException ex) { ShowError(ex.Message); return; }
            await Execute("创建目录", (token, progress) => Task.Run(() => files.CreateDirectory(path, token, progress)), true);
        }

        /// <summary>在连接根目录创建存储桶；名称规则与后端一致，创建前二次确认，创建后重新列举根核验。</summary>
        private async void CreateBucket()
        {
            if (Busy) return;
            if (!bucketBased) { ShowError("只有 S3／OSS 连接支持创建存储桶。"); return; }
            if (currentPath != "") { ShowError("请回到连接根目录（桶列表）再创建存储桶。"); return; }
            var name = AskName("", "创建存储桶"); if (name == null) return;
            if (MessageBox.Show(this, "将在连接 " + remote + " 下创建存储桶：" + name
                + "\r\n名称需为3—63位小写字母、数字、点或短横线；同名桶可能在账号甚至区域外已被占用，创建后不能改名。确认创建？",
                "确认创建存储桶", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await Execute("创建存储桶", (token, progress) => Task.Run(() => files.CreateBucket(name, token, progress)), true);
        }

        /// <summary>为单个对象前缀配置服务器端生命周期到期删除；提示S3前缀规则对相似及未来对象同样生效。</summary>
        private async void SetObjectExpiry()
        {
            var selected = SelectedFiles();
            if (!bucketBased || selected.Count != 1) { ShowError("请在存储桶内仅选择一个文件，再设置有效期。"); return; }
            var full = OssFileClient.Join(currentPath, selected[0].Path); var slash = full.IndexOf('/');
            if (slash <= 0 || slash == full.Length - 1) { ShowError("请先进入存储桶并选择存储桶中的文件。"); return; }
            var bucket = full.Substring(0, slash); var key = full.Substring(slash + 1);
            using (var dialog = new Form { Text = "设置对象有效期", Width = 430, Height = 185, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var label = new Label { Left = 18, Top = 16, Width = 380, Height = 42, Text = "对象最后修改后多少天开始自动删除？（1—3650天）" };
                var days = new NumericUpDown { Left = 20, Top = 64, Width = 110, Minimum = 1, Maximum = 3650, Value = 30 };
                var ok = new Button { Text = "继续", DialogResult = DialogResult.OK, Left = 238, Top = 101, Width = 76 };
                var cancelDialog = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 322, Top = 101, Width = 76 };
                dialog.Controls.AddRange(new Control[] { label, days, ok, cancelDialog }); dialog.AcceptButton = ok; dialog.CancelButton = cancelDialog;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var count = (int)days.Value;
                var warning = "将向存储桶“" + bucket + "”写入服务端生命周期规则。\r\n规则按对象名前缀匹配：" + key + "\r\n任何以该名称开头的现有或未来对象都可能在最后修改后 " + count + " 天自动删除。删除不可撤销。\r\n其他生命周期规则将保留；此操作会修改远端桶配置。确认继续？";
                if (MessageBox.Show(this, warning, "确认自动删除规则", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                OssBucketClient bucketClient; try { bucketClient = files.Bucket(bucket); } catch (Exception ex) { ShowError(ex.Message); return; }
                await Execute("设置对象有效期", (token, progress) => Task.Run(() => bucketClient.SetObjectExpiry(key, count, token)), true);
            }
        }

        /// <summary>读取桶权限后按所选模式落地：私有与公共只读走简化写入，自定义进入可视化策略编辑器。</summary>
        private async void BucketPermissions()
        {
            if (Busy) return;
            var name = BucketScope();
            if (name == null) return;
            OssBucketClient client;
            try { client = files.Bucket(name); } catch (Exception ex) { ShowError(ex.Message); return; }
            OssBucketState state = null;
            await Execute("读取桶权限", async (token, progress) => { state = await Task.Run(() => client.Read(token)); }, false);
            if (state == null || IsDisposed) return;
            string target;
            string policy;
            using (var dialog = new BucketPermissionDialog(name, state, client.SupportsPolicy))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                target = dialog.Target;
                policy = dialog.Policy;
            }
            if (target == "custom") { await WritePolicy(client, name, state, policy); return; }
            if (target == state.Mode) { status.Text = "桶 " + name + " 权限未修改。"; return; }
            // 从自定义降到简化模式等于删掉本工具看不懂的授权，必须在确认框里单独说明。
            var dropped = state.Mode == "custom"
                ? "\r\n当前自定义授权会被一并替换，写入后无法恢复；权限若还来自桶 ACL、IAM 或公共访问阻止，替换后仍按服务端结果生效。"
                : "";
            var warning = (target == "public-read"
                ? "存储桶“" + name + "”将允许匿名读取桶内全部对象，包括之后新上传的对象；不授权列举、上传、删除或修改权限。\r\n任何拿到对象地址的人都能下载。"
                : "存储桶“" + name + "”将删除本工具识别的公共只读策略，对象只保留认证账号访问。") + dropped + "\r\n确认继续？";
            if (MessageBox.Show(this, warning, "确认修改桶访问权限", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (client.SupportsPolicy)
            {
                await WritePolicy(client, name, state, target == "public-read" ? OssBucketClient.PublicPolicy(name) : "");
                return;
            }
            OssBucketState applied = null;
            await Execute("设置桶权限", async (token, progress) => { applied = await Task.Run(() => client.ApplyMode(target, state, state.Mode == "custom", token)); }, false,
                () => "桶 " + name + " 访问权限已更新：" + BucketPermissionDialog.Describe(applied));
            if (applied == null || IsDisposed) return;
            permissionPath = name; permissionText = "桶 " + name + " · " + BucketPermissionDialog.Describe(applied); bucketInfo.Text = permissionText;
        }

        /// <summary>写入桶策略正文：先展示当前与写入后的逐条摘要，写前重读比对快照，写后逐字读回核验。</summary>
        /// <param name="client">桶权限客户端。</param><param name="name">桶名称。</param>
        /// <param name="state">界面读取并展示过的快照。</param><param name="policy">正文；空串表示删除全部策略，null 表示不写入。</param>
        private async Task WritePolicy(OssBucketClient client, string name, OssBucketState state, string policy)
        {
            if (policy == null) return;
            var summary = "桶：" + name + "\r\n当前策略：\r\n" + OssPolicyDoc.Describe(state.Policy)
                + "\r\n\r\n写入后：\r\n" + (policy == "" ? "（删除全部桶策略）" : OssPolicyDoc.Describe(policy))
                + "\r\n\r\n写入前会重新读取，期间被其他客户端改动则不写入任何配置。确认提交？";
            if (MessageBox.Show(this, summary, "确认写入桶策略", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            OssBucketState applied = null;
            await Execute("写入桶策略", async (token, progress) => { applied = await Task.Run(() => client.ApplyPolicy(policy, state, token)); }, false,
                () => "桶 " + name + " 策略已写入并读回核验：" + BucketPermissionDialog.Describe(applied));
            if (applied == null || IsDisposed) return;
            permissionPath = name; permissionText = "桶 " + name + " · " + BucketPermissionDialog.Describe(applied); bucketInfo.Text = permissionText;
        }

        /// <summary>确定权限操作作用的桶：已进入桶时用当前桶，否则要求选中一个桶。</summary>
        /// <returns>桶名称，或空值表示已给出提示。</returns>
        private string BucketScope()
        {
            if (currentPath != "") return currentPath.Split('/')[0];
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag as OssEntry).ToList();
            if (selected.Count != 1 || selected[0] == null || !selected[0].IsBucket) { ShowError("请选中一个存储桶，或先进入桶后操作。"); return null; }
            return selected[0].Name;
        }

        /// <summary>逐条可视化编辑桶 Bucket Policy，正文由界面实时生成，写入前展示当前与写入后的摘要。</summary>
        private async void BucketPolicyEdit()
        {
            if (Busy) return;
            var name = BucketScope();
            if (name == null) return;
            OssBucketClient client;
            try { client = files.Bucket(name); } catch (Exception ex) { ShowError(ex.Message); return; }
            if (!client.SupportsPolicy) { ShowError("该后端用桶 ACL 管理访问权限，没有 Bucket Policy 可编辑；请使用“桶访问权限设置…”。"); return; }
            OssBucketState state = null;
            await Execute("读取桶策略", async (token, progress) => { state = await Task.Run(() => client.Read(token)); }, false);
            if (state == null || IsDisposed) return;
            string desired;
            bool clear;
            using (var dialog = new BucketPolicyDialog(name, state))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                desired = dialog.Desired; clear = dialog.Clear;
            }
            await WritePolicy(client, name, state, clear ? "" : desired);
        }

        /// <summary>验证重命名输入，避免无效名称从异步界面事件抛出未处理异常。</summary>
        /// <param name="parent">当前目录。</param><param name="name">输入的新文件名。</param><returns>同目录目标路径。</returns>
        internal static string RenamePath(string parent, string name)
        {
            if (String.IsNullOrEmpty(name) || name.Contains("/") || name.Contains("\\")) throw new ArgumentException("请输入文件名，不能包含目录分隔符。");
            return OssFileClient.Join(parent, name);
        }

        /// <summary>弹出新文件名输入框。</summary>
        /// <param name="name">原名称。</param><param name="caption">操作标题。</param><returns>新名称或空值。</returns>
        private string AskName(string name, string caption = "重命名文件")
        {
            using (var dialog = new Form { Text = caption, Size = new Size(510, 180), StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog })
            {
                var input = new TextBox { Text = name, Location = new Point(18, 22), Width = 460 };
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(280, 70) };
                var back = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(380, 70) };
                dialog.Controls.AddRange(new Control[] { input, ok, back }); dialog.AcceptButton = ok; dialog.CancelButton = back;
                return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
            }
        }

        /// <summary>按当前页实际选中对象删除；目录额外确认递归范围，失败项不冒充成功。</summary>
        private async void DeleteSelected()
        {
            if (Busy) return;
            var selected = list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag as OssEntry).Where(x => x != null).ToList();
            if (selected.Count == 0) return;
            if (selected.Any(x => x.IsBucket)) { ShowError("不能删除存储桶。请进入桶内选择文件或子目录。"); return; }
            var path = currentPath;
            var preview = String.Join("\r\n", selected.Take(8).Select(x => files.Target(OssFileClient.Join(path, x.Path))));
            if (MessageBox.Show(this, "将永久删除所选 " + selected.Count + " 个对象（仅当前页已选中项）：\r\n" + preview + (selected.Count > 8 ? "\r\n…其余 " + (selected.Count - 8) + " 项" : "") + "\r\n\r\n目录包含全部子项；不会进入 Windows 回收站，是否继续？", "确认批量删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            if (selected.Any(x => x.IsDir) && AskName("", "递归删除目录：输入 删除 以确认") != "删除") return;
            var completed = 0; var failures = new List<string>();
            await Execute("批量删除", (token, progress) => Task.Run(() =>
            {
                foreach (var item in selected)
                {
                    token.ThrowIfCancellationRequested(); progress(item.Name);
                    try { var target = OssFileClient.Join(path, item.Path); if (item.IsDir) files.DeleteDirectory(target, token, progress); else files.DeleteFile(target, token, progress); completed++; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { failures.Add(item.Name); }
                }
                if (failures.Count > 0) throw new InvalidOperationException("已核验删除 " + completed + " 项，失败 " + failures.Count + " 项：" + String.Join("、", failures.Take(8)));
            }), true, () => "批量删除完成，已核验 " + completed + " 项；未选中对象不受影响。");
        }

        /// <summary>复制对象绝对URL；保留右键复制rclone路径，不生成签名或绕过访问权限。</summary>
        /// <param name="absolute">是否复制HTTP绝对链接。</param>
        private void CopyPath(bool absolute = true)
        {
            try
            {
                var paths = list.SelectedItems.Cast<ListViewItem>().Where(x => x.Tag is OssEntry).Select(x => OssFileClient.Join(currentPath, ((OssEntry)x.Tag).Path)).ToArray();
                if (paths.Length == 0) paths = new[] { currentPath };
                Clipboard.SetText(String.Join(Environment.NewLine, paths.Select(x => absolute ? files.Url(x) : files.Target(x))));
                status.Text = absolute ? "已复制绝对链接；私有对象仍需认证，该链接不带签名。" : "已复制rclone对象路径。";
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        /// <summary>在系统文件管理器（优先本机已安装的 Pulse）中打开已挂载路径，不自动创建或改变挂载。</summary>
        private async void OpenMounted()
        {
            try { var folder = await Task.Run(() => ResolveMountedFolder()); if (!IsDisposed) OpenLocalFolder(folder); }
            catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
        }

        /// <summary>实时识别挂载及远端根路径，不复用打开窗口时可能为空或已过期的盘符。</summary>
        /// <returns>当前目录的真实挂载路径；当前目录不在挂载范围时拒绝打开。</returns>
        internal string ResolveMountedFolder()
        {
            MountedLocation mounted;
            if (!MainForm.GetMountLocations().TryGetValue(remote, out mounted)) throw new InvalidOperationException("未识别到该连接的有效挂载。请在连接管理中检查挂载状态；无需关闭文件窗口。");
            if (!Environment.GetLogicalDrives().Contains(mounted.Drive + "\\", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("挂载进程仍在运行，但盘符未就绪，请稍后刷新。");
            var path = currentPath;
            if (!String.IsNullOrEmpty(mounted.Root))
            {
                if (path == mounted.Root) path = "";
                else if (path.StartsWith(mounted.Root + "/", StringComparison.Ordinal)) path = path.Substring(mounted.Root.Length + 1);
                else throw new InvalidOperationException("该盘符只挂载了子目录，当前目录不在挂载范围内。");
            }
            var folder = mounted.Drive + "\\";
            foreach (var segment in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) folder = LocalFile(folder, segment);
            return folder;
        }

        /// <summary>交给统一入口打开本地位置：优先本机文件管理器，失败时说明原因，不静默失败。</summary>
        /// <param name="folder">本地或挂载路径。</param>
        internal static void OpenLocalFolder(string folder)
        {
            FileManagerLaunch.Open(folder, null);
        }

        /// <summary>串行执行本窗口任务，进度切回界面线程，失败和取消均不宣称回滚。</summary>
        /// <param name="action">操作名称。</param><param name="work">异步工作。</param><param name="reload">是否重新读取远端目录。</param>
        /// <param name="completion">可选的精确统计文本，不将部分取消或失败宣称全成功。</param>
        /// <returns>任务。</returns>
        private async Task Execute(string action, Func<CancellationToken, Action<string>, Task> work, bool reload, Func<string> completion = null)
        {
            if (Busy) return;
            // 独立窗口或隔离界面测试没有主消息循环时，同样保证 await 返回界面线程。
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext)) SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var cancellation = new CancellationTokenSource(); active = cancellation;
            UpdateActionHover(null, -1, ""); list.Invalidate();
            if (reload) { usage.Clear(); RenderEntries(); }
            foreach (var button in actions) button.Enabled = false;
            pathBox.Enabled = overwrite.Enabled = parallelDownloads.Enabled = menu.Enabled = false; cancel.Enabled = true;
            status.Text = action + "中…";

            IProgress<string> reporter = new Progress<string>(text => { if (!IsDisposed && active == cancellation) status.Text = action + "：" + text; });
            var result = ""; var failed = false;
            try { await work(cancellation.Token, reporter.Report); result = completion == null ? action + "完成（已核验）" : completion(); }
            catch (OperationCanceledException) { result = action == "统计占用" ? "占用统计已取消，未生成完整结果；远端文件未修改。" : action + "已取消；已完成部分不会回滚，重命名可能保留新旧对象，请刷新检查。"; }
            catch (Exception ex) { failed = true; var reason = ex.Message.TrimEnd('。', '，', ';', '；'); result = action + "失败：" + reason + (action == "统计占用" ? "；只读统计未修改远端文件。" : "。批量操作可能部分完成，请刷新检查。"); }
            try
            {
                if (reload && !closeAfterCancel)
                {
                    var loaded = await Task.Run(() => files.List(currentPath, cancellation.Token)); entries = loaded; RenderEntries();
                }
            }
            catch (Exception) { result += " 当前目录未刷新，请手动刷新。"; }
            if (IsDisposed || Disposing) { active = null; cancellation.Dispose(); return; }
            foreach (var button in actions) button.Enabled = true;
            pathBox.Enabled = overwrite.Enabled = parallelDownloads.Enabled = menu.Enabled = true; cancel.Enabled = false;
            status.Text = result;
            status.ForeColor = failed ? UiStyle.DangerText : UiStyle.TextMuted;
            history.AppendText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + result + Environment.NewLine);
            if (failed) showHistory.Checked = true;
            // 窗口只保留有限任务历史，不记录凭据和完整 rclone 命令。
            if (history.TextLength > 24000) history.Text = history.Text.Substring(history.TextLength - 12000);
            active = null; cancellation.Dispose();
            backPath.Enabled = pathIndex > 0; forwardPath.Enabled = pathIndex >= 0 && pathIndex + 1 < paths.Count;
            list.Invalidate();
            if (closeAfterCancel) Close();
        }

        /// <summary>取消当前窗口任务，不结束任何挂载进程。</summary>
        private void Cancel() { if (active != null) { status.Text = "正在取消本次任务…"; active.Cancel(); cancel.Enabled = false; } }

        /// <summary>显示中文操作错误。</summary>
        /// <param name="message">错误说明。</param>
        private void ShowError(string message) { status.Text = message; MessageBox.Show(this, message, "OSS 文件管理", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}
