using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RcloneOssManager
{
    internal sealed partial class OssFileForm
    {
        /// <summary>显示层分页只创建有限行，排序及筛选对本层已列举的所有对象生效。</summary>
        private int PageSize = 500;
        /// <summary>显示分页设置及跳页，不改变后端本层完整列举语义。</summary>
        private readonly ComboBox pageSize = new ComboBox { Name = "pageSize", DropDownStyle = ComboBoxStyle.DropDownList, Width = 76 };
        private readonly NumericUpDown pageJump = new NumericUpDown { Name = "pageJump", Minimum = 1, Maximum = 1, Width = 64 };
        private readonly CheckBox selectPage = new CheckBox { Name = "selectPage", Text = "全选本页", AutoSize = true, Margin = new Padding(6, 7, 8, 0) };
        private bool syncingSelection;
        /// <summary>导航完成后需要落地的一次性提示，例如文件地址已转为所在目录。</summary>
        private string pendingNotice;
        /// <summary>复制按钮随唯一选中文件在目录路径与文件路径之间切换。</summary>
        private Button copyPathButton;
        private int pageIndex, sortColumn, pathIndex = -1, requestedHistory = -1;
        // 空目录提示行的标记，用于把该行排除在选择和行操作之外。
        private static readonly object EmptyRow = new object();
        private bool sortDescending;
        private readonly List<string> paths = new List<string>();
        private Button previousPage, nextPage, backPath, forwardPath;
        private readonly Label pageInfo = new Label { AutoSize = true, Margin = new Padding(8, 7, 8, 0) };
        private readonly Label bucketInfo = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, Margin = new Padding(0), ForeColor = UiStyle.TextMuted };
        /// <summary>缓存来源独占一行，不与桶权限或文件列表共享绘制区域。</summary>
        private readonly Label cacheInfo = new Label { Name = "cacheInfo", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiStyle.TextMuted, Margin = new Padding(0) };
        private CancellationTokenSource permissionRead;
        private string permissionPath = "", permissionText = "";
        private OssQuickResult quick = new OssQuickResult();
        private DateTime lastRowAction = DateTime.MinValue;
        /// <summary>固定操作顺序，图标、提示与点击共用同一索引。</summary>
        private static readonly string[] ActionLabels = { "下载", "复制链接", "属性", "统计占用" };
        /// <summary>图标悬停时显示中文操作说明及原有统计摘要。</summary>
        private readonly ToolTip actionTip = new ToolTip { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 8000, ShowAlways = true };
        /// <summary>使用短延迟跟踪提示，避免仅依赖列表自动提示造成悬停不显示。</summary>
        private readonly System.Windows.Forms.Timer hoverTimer = new System.Windows.Forms.Timer { Interval = 350 };
        /// <summary>最后一次命中的图标位置及提示内容，停留在同一图标时不反复重置计时。</summary>
        private Point hoverPoint;
        private string hoverText = "";
        /// <summary>悬停只重绘单元格，不刷新整表。</summary>
        private ListViewItem hoveredActionRow;
        private int hoveredAction = -1;
        /// <summary>独立行高留出图标点击区域，不更改对象模型。</summary>
        private readonly ImageList rowSpacing = new ImageList { ImageSize = new Size(1, 32), ColorDepth = ColorDepth.Depth32Bit };
        /// <summary>目录大小慢速填充：约3秒发起一个统计请求，串行执行避免对服务端产生瞬时压力。</summary>
        private readonly System.Windows.Forms.Timer usageFillTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly Queue<string> usageQueue = new Queue<string>();
        private readonly HashSet<string> pendingUsage = new HashSet<string>(StringComparer.Ordinal);
        private CancellationTokenSource usageFillCancel;
        private bool usageFilling;

        /// <summary>配置路径导航、显示层分页和最后一列行级操作。</summary>
        /// <param name="browser">文件浏览布局。</param>
        private void InitBrowser(TableLayoutPanel browser)
        {
            var navigation = new TableLayoutPanel { Name = "navigationInfo", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
            navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340)); navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            navigation.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); navigation.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            filter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; filter.Height = UiStyle.ButtonHeight; filter.Margin = new Padding(0, 5, 14, 0);
            navigation.Controls.Add(filter, 0, 0); navigation.SetRowSpan(filter, 2);
            bucketInfo.TextAlign = ContentAlignment.MiddleLeft; bucketInfo.Visible = bucketBased;
            bucketInfo.MouseEnter += delegate { actionTip.SetToolTip(bucketInfo, bucketInfo.Text); };
            cacheInfo.MouseEnter += delegate { actionTip.SetToolTip(cacheInfo, cacheInfo.Text); };
            navigation.Controls.Add(bucketInfo, 1, 0); navigation.Controls.Add(cacheInfo, 1, 1); browser.Controls.Add(navigation, 0, 1);
            var pages = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
            previousPage = MakeButton("上一页", () => { if (pageIndex > 0) { pageIndex--; RenderEntries(); } });
            nextPage = MakeButton("下一页", () => { pageIndex++; RenderEntries(); });
            foreach (var b in new[] { previousPage, nextPage }) b.Size = new Size(UiStyle.ButtonWidth, UiStyle.ButtonHeight);
            pageSize.Items.AddRange(new object[] { 100, 200, 500, 1000 }); pageSize.SelectedItem = PageSize;
            pageSize.SelectedIndexChanged += delegate { PageSize = (int)pageSize.SelectedItem; pageIndex = 0; RenderEntries(); };
            selectPage.CheckedChanged += delegate { if (!syncingSelection && !Busy) SelectCurrentPage(selectPage.Checked); };
            list.SelectedIndexChanged += delegate { if (syncingSelection) return; syncingSelection = true; selectPage.Checked = list.Items.Cast<ListViewItem>().Where(x => x.Tag is OssEntry).Any() && list.Items.Cast<ListViewItem>().Where(x => x.Tag is OssEntry).All(x => x.Selected); syncingSelection = false; RefreshSelectionPath(); };
            pages.Controls.Add(selectPage); pages.Controls.Add(previousPage); pages.Controls.Add(nextPage); pages.Controls.Add(pageInfo);
            pages.Controls.Add(new Label { Text = "每页", AutoSize = true, Margin = new Padding(4, 7, 4, 0) }); pages.Controls.Add(pageSize);
            pages.Controls.Add(new Label { Text = "跳至", AutoSize = true, Margin = new Padding(6, 7, 4, 0) }); pages.Controls.Add(pageJump);
            var jump = MakeButton("跳转", () => { pageIndex = (int)pageJump.Value - 1; RenderEntries(); }); jump.Width = 62; pages.Controls.Add(jump); browser.Controls.Add(pages, 0, 3);
            list.ColumnClick += delegate(object sender, ColumnClickEventArgs e)
            {
                if (e.Column >= 4) return;
                sortDescending = e.Column == sortColumn && !sortDescending; sortColumn = e.Column; pageIndex = 0;
                RenderEntries();
            };
            list.OwnerDraw = true;
            list.SmallImageList = rowSpacing;
            // 排序方向只画在表头上，列文本保持干净，复制列名和使用朗读工具时不会带上箭头。
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e) { UiStyle.DrawHeader(e, e.Header.Index == sortColumn && e.Header.Index < 4 ? (sortDescending ? " ▼" : " ▲") : ""); };
            list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { if (list.View != View.Details) e.DrawDefault = true; };
            list.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e)
            {
                if (e.ColumnIndex != 4) { UiStyle.DrawCell(e, list.HoveredIndex); return; }
                using (var brush = new SolidBrush(e.Item.Selected ? UiStyle.Selection : e.Item.BackColor)) e.Graphics.FillRectangle(brush, e.Bounds);
                var entry = e.Item.Tag as OssEntry; if (entry == null) return;
                for (var i = 0; i < ActionLabels.Length; i++) if (ActionAvailable(entry, i))
                {
                    var bounds = ActionBounds(e.Bounds, i);
                    if (!Busy && hoveredActionRow == e.Item && hoveredAction == i)
                        using (var brush = new SolidBrush(e.Item.Selected ? UiStyle.SelectionBar : UiStyle.AccentSoft)) e.Graphics.FillRectangle(brush, Rectangle.Inflate(bounds, -1, -1));
                    UiStyle.DrawActionIcon(e.Graphics, bounds, i == 2 ? 3 : i == 3 ? 2 : i, Busy ? UiStyle.TextMuted : e.Item.Selected ? UiStyle.Text : UiStyle.AccentText);
                }
            };
            list.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left || Busy) return;
                var hit = list.HitTest(e.Location);
                if (hit.Item == null || hit.SubItem == null || hit.Item.SubItems.IndexOf(hit.SubItem) != 4 || !(hit.Item.Tag is OssEntry)) return;
                for (var i = 0; i < ActionLabels.Length; i++) if (ActionBounds(hit.SubItem.Bounds, i).Contains(e.Location))
                {
                    var entry = (OssEntry)hit.Item.Tag; if (!ActionAvailable(entry, i)) return;
                    UpdateActionHover(null, -1, "");
                    lastRowAction = DateTime.UtcNow; list.SelectedItems.Clear(); hit.Item.Selected = true;
                    if (i == 0) DownloadFiles(); else if (i == 1) CopyPath(); else if (i == 2) ShowProperties(); else CountUsage();
                    break;
                }
            };
            list.MouseMove += delegate(object sender, MouseEventArgs e)
            {
                hoverPoint = e.Location;
                var hit = list.HitTest(e.Location); var entry = hit.Item == null ? null : hit.Item.Tag as OssEntry;
                if (!Busy && entry != null && hit.SubItem != null && hit.Item.SubItems.IndexOf(hit.SubItem) == 4)
                    for (var i = 0; i < ActionLabels.Length; i++) if (ActionAvailable(entry, i) && ActionBounds(hit.SubItem.Bounds, i).Contains(e.Location)) { UpdateActionHover(hit.Item, i, ActionLabels[i] + " · " + entry.Name); return; }
                UpdateActionHover(null, -1, hit.Item == null ? "" : hit.Item.ToolTipText);
            };
            list.MouseLeave += delegate { UpdateActionHover(null, -1, ""); };
            list.MouseWheel += delegate { UpdateActionHover(null, -1, ""); };
            list.SizeChanged += delegate { UpdateActionHover(null, -1, ""); };
            Deactivate += delegate { UpdateActionHover(null, -1, ""); };
            hoverTimer.Tick += delegate
            {
                hoverTimer.Stop();
                if (IsDisposed || Disposing || Busy || hoverText == "" || !list.ClientRectangle.Contains(list.PointToClient(Cursor.Position))) return;
                // 跟踪提示绑定窗体，避开ListView自己的提示管理。
                actionTip.Show(hoverText, this, PointToClient(list.PointToScreen(new Point(hoverPoint.X + 12, hoverPoint.Y + 22))), 4500);
            };
            list.SelectedIndexChanged += delegate { if (!syncingSelection) RefreshBucketInfo(); };
            usageFillTimer.Tick += delegate { FillNextUsage(); };
            FormClosed += delegate
            {
                if (permissionRead != null) permissionRead.Cancel();
                if (usageFillCancel != null) usageFillCancel.Cancel();
                usageFillTimer.Stop(); hoverTimer.Stop(); hoverTimer.Dispose(); actionTip.Dispose(); rowSpacing.Dispose();
            };
            RenderNavigation();
        }

        /// <summary>计算操作列固定四段点击区域，绘制、悬停与命中检测使用同一坐标。</summary>
        /// <param name="bounds">单元格范围。</param><param name="index">动作索引。</param><returns>动作范围。</returns>
        private static Rectangle ActionBounds(Rectangle bounds, int index)
        {
            var slot = bounds.Width / ActionLabels.Length; var width = Math.Min(slot, bounds.Height);
            return new Rectangle(bounds.Left + slot * index + (slot - width) / 2, bounds.Top + 2, width, Math.Max(0, bounds.Height - 4));
        }

        /// <summary>只为适用对象显示操作；桶不能整桶下载，文件不需要目录统计。</summary>
        /// <param name="entry">行对象。</param><param name="action">动作索引。</param><returns>是否适用。</returns>
        private static bool ActionAvailable(OssEntry entry, int action) { return entry != null && (action != 0 || !entry.IsBucket) && (action != 3 || entry.IsDir); }

        /// <summary>更新手形光标和提示，翻页时清除旧引用以免指向已移除行。</summary>
        /// <param name="row">悬停行，可空。</param><param name="action">图标索引，无图标时为负。</param><param name="tip">中文说明。</param>
        private void UpdateActionHover(ListViewItem row, int action, string tip)
        {
            var changed = hoveredActionRow != row || hoveredAction != action || hoverText != tip;
            if (hoveredActionRow != row || hoveredAction != action)
            {
                if (hoveredActionRow != null && hoveredActionRow.ListView == list) list.Invalidate(hoveredActionRow.SubItems[4].Bounds);
                hoveredActionRow = row; hoveredAction = action;
                if (row != null) list.Invalidate(row.SubItems[4].Bounds);
            }
            list.Cursor = row == null ? Cursors.Default : Cursors.Hand;
            if (changed)
            {
                hoverTimer.Stop(); actionTip.Hide(this); hoverText = tip;
                if (tip != "" && !Busy) hoverTimer.Start();
            }
        }

        /// <summary>排序后取本页，目录始终在文件之前，数值不按格式化字符串排序。</summary>
        /// <returns>至多500个模型。</returns>
        private IEnumerable<OssEntry> PageEntries()
        {
            var items = entries.Where(x => (x.Name ?? "").IndexOf(filter.Text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            items.Sort(CompareEntries);
            var count = Math.Max(1, (items.Count + PageSize - 1) / PageSize); pageIndex = Math.Max(0, Math.Min(pageIndex, count - 1));
            previousPage.Enabled = pageIndex > 0; nextPage.Enabled = pageIndex + 1 < count;
            pageJump.Maximum = count; pageJump.Value = pageIndex + 1;
            pageInfo.Text = "第 " + (pageIndex + 1) + " / " + count + " 页 · " + items.Count + " 项";
            return items.Skip(pageIndex * PageSize).Take(PageSize);
        }

        /// <summary>仅选择当前展示页真实对象，不将上一级导航算作可操作对象。</summary>
        /// <param name="selected">是否选中。</param>
        private void SelectCurrentPage(bool selected)
        {
            list.BeginUpdate(); syncingSelection = true;
            try { foreach (ListViewItem row in list.Items) row.Selected = selected && row.Tag is OssEntry; }
            finally { selectPage.Checked = selected && list.Items.Cast<ListViewItem>().Any(x => x.Tag is OssEntry); syncingSelection = false; list.EndUpdate(); RefreshBucketInfo(); RefreshSelectionPath(); }
        }

        /// <summary>比较目录优先的列表值；时间缺失不用于伪造新旧排序。</summary>
        /// <param name="left">左模型。</param><param name="right">右模型。</param><returns>排序关系。</returns>
        private int CompareEntries(OssEntry left, OssEntry right)
        {
            if (left.IsDir != right.IsDir) return left.IsDir ? -1 : 1;
            var result = 0;
            if (sortColumn == 1) result = EntrySize(left).CompareTo(EntrySize(right));
            else if (sortColumn == 2) result = StringComparer.OrdinalIgnoreCase.Compare(Path.GetExtension(left.Name), Path.GetExtension(right.Name));
            else if (sortColumn == 3) result = StringComparer.Ordinal.Compare(FormatEntryTime(left), FormatEntryTime(right));
            if (result == 0) result = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (result == 0) result = StringComparer.Ordinal.Compare(left.Name, right.Name);
            return sortDescending ? -result : result;
        }

        /// <summary>桶／目录按已有统计排序，未知大小保留为负值。</summary>
        /// <param name="entry">列表模型。</param><returns>字节大小或未知值。</returns>
        private long EntrySize(OssEntry entry) { OssUsage value; return entry.IsDir && usage.TryGetValue(OssFileClient.Join(currentPath, entry.Path), out value) ? value.Bytes : entry.Size; }

        /// <summary>隐藏目录缺失时间和rclone默认2000年占位，不隐藏真实文件的历史日期。</summary>
        /// <param name="entry">文件或目录。</param><returns>时间文本或空字符串。</returns>
        internal static string FormatEntryTime(OssEntry entry)
        {
            if (entry.IsDir)
            {
                DateTimeOffset time;
                if (!DateTimeOffset.TryParse(entry.ModTime, out time) || time.Year <= 1 || time.UtcDateTime == new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)) return "—";
            }
            return FormatTime(entry.ModTime);
        }

        /// <summary>只有成功导航才提交历史；回退和前进不重复添加路径。</summary>
        /// <param name="path">已加载路径。</param>
        private void RecordPath(string path)
        {
            if (requestedHistory >= 0 && requestedHistory < paths.Count && paths[requestedHistory] == path) { pathIndex = requestedHistory; requestedHistory = -1; return; }
            if (pathIndex >= 0 && paths[pathIndex] == path) return;
            if (pathIndex + 1 < paths.Count) paths.RemoveRange(pathIndex + 1, paths.Count - pathIndex - 1);
            paths.Add(path); pathIndex = paths.Count - 1;
        }

        /// <summary>移动历史指针，失败时保持原指针。</summary>
        /// <param name="step">负值回退，正值前进。</param>
        private void MoveHistory(int step) { var next = pathIndex + step; if (Busy || next < 0 || next >= paths.Count) return; requestedHistory = next; Navigate(paths[next]); }

        /// <summary>同步前进后退按钮可用状态；路径跳转经地址栏输入或列表导航完成。</summary>
        private void RenderNavigation()
        {
            backPath.Enabled = pathIndex > 0; forwardPath.Enabled = pathIndex >= 0 && pathIndex + 1 < paths.Count;
        }

        /// <summary>串行低频统计目录大小：每次计时只发起一个请求，完成前不再发新请求；界面忙时顺延到下一轮。</summary>
        private async void FillNextUsage()
        {
            if (usageFilling || Busy || IsDisposed || Disposing) return;
            if (usageQueue.Count == 0) { usageFillTimer.Stop(); return; }
            var path = usageQueue.Dequeue();
            if (usage.ContainsKey(path)) { pendingUsage.Remove(path); return; }
            pendingUsage.Remove(path);
            usageFilling = true;
            var cancellation = new CancellationTokenSource(); usageFillCancel = cancellation;
            try
            {
                var total = await Task.Run(() => files.GetUsage(path, cancellation.Token));
                if (IsDisposed || Disposing) return;
                usage[path] = total;
                UpdateUsageRow(path, total);
                UiStyle.SetStatus(status, "已统计 " + files.Target(path) + "：" + total.Summary, "info");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed && !Disposing) UiStyle.SetStatus(status, "目录大小统计失败：" + files.Target(path) + "（" + ex.Message.TrimEnd('。') + "）", "warn"); }
            finally
            {
                usageFilling = false;
                if (usageFillCancel == cancellation) usageFillCancel = null;
                cancellation.Dispose();
            }
        }

        /// <summary>只改目标目录行的大小列与提示，不重建列表，避免滚动与选中状态抖动。</summary>
        private void UpdateUsageRow(string path, OssUsage total)
        {
            foreach (ListViewItem row in list.Items)
            {
                var entry = row.Tag as OssEntry;
                if (entry == null || !entry.IsDir) continue;
                var key = String.IsNullOrEmpty(currentPath) ? entry.Path : currentPath + "/" + entry.Path;
                if (key != path) continue;
                row.SubItems[1].Text = total.SizeText;
                row.ToolTipText = total.Summary;
                return;
            }
        }

        /// <summary>读取MinIO后台缓存并只回填本层桶，未授权时不会递归扫描。</summary>
        /// <param name="token">本次导航取消。</param><returns>查询任务。</returns>
        private async Task LoadQuickUsage(CancellationToken token)
        {
            if (!bucketBased) return;
            quick = await Task.Run(() => files.QuickUsage(token));
            token.ThrowIfCancellationRequested();
            cacheInfo.Text = quick.Reason;
            foreach (var path in usage.Where(x => x.Value.Source != "").Select(x => x.Key).ToArray()) usage.Remove(path);
            foreach (var pair in quick.Buckets) usage[pair.Key] = pair.Value;
            RenderEntries();
        }

        /// <summary>随当前桶变化读取只读访问模式；旧请求取消且不得覆盖新桶的显示。</summary>
        private async void RefreshBucketInfo()
        {
            if (!bucketBased || IsDisposed) return;
            var name = currentPath == "" ? list.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag as OssEntry).Where(x => x != null && x.IsBucket).Select(x => x.Name).FirstOrDefault() : currentPath.Split('/')[0];
            name = name ?? "";
            if (permissionPath == name && permissionRead != null) return;
            if (permissionPath == name && permissionText != "") { bucketInfo.Text = permissionText; return; }
            if (permissionRead != null) permissionRead.Cancel();
            permissionPath = name; permissionText = "";
            if (name == "") { bucketInfo.Text = "选择存储桶可查看和修改访问权限"; return; }
            var cancellation = new CancellationTokenSource(); permissionRead = cancellation;
            bucketInfo.Text = "桶 " + name + " · 正在读取访问权限…";
            try
            {
                var state = await Task.Run(() => files.Bucket(name).Read(cancellation.Token));
                if (!IsDisposed && permissionRead == cancellation) { permissionText = "桶 " + name + " · " + BucketPermissionDialog.Describe(state); bucketInfo.Text = permissionText; }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!IsDisposed && permissionRead == cancellation) { permissionText = "桶 " + name + " · 访问权限未知（无读取权限或接口不可用）"; bucketInfo.Text = permissionText; } }
            finally { if (permissionRead == cancellation) permissionRead = null; cancellation.Dispose(); }
        }

        /// <summary>显式读取所选对象属性；目录只显示已有信息，不触发全桶列举。</summary>
        private async void ShowProperties()
        {
            if (Busy || list.SelectedItems.Count != 1 || !(list.SelectedItems[0].Tag is OssEntry)) return;
            var entry = (OssEntry)list.SelectedItems[0].Tag; var path = OssFileClient.Join(currentPath, entry.Path); string text = null;
            if (entry.IsDir) { OssUsage total; usage.TryGetValue(path, out total); text = "路径：" + files.Target(path) + "\r\n类型：" + (entry.IsBucket ? "存储桶" : "目录") + "\r\n修改时间：" + FormatEntryTime(entry) + "\r\n大小：" + (total == null ? "尚未统计" : total.Summary); }
            else await Execute("读取对象属性", async (token, progress) => { text = await Task.Run(() => files.GetProperties(path, token)); }, false);
            if (text != null && !IsDisposed) using (var dialog = new ObjectInfoForm(entry.Name, DisplayPath(path), text)) dialog.ShowDialog(this);
        }

        /// <summary>构造本地相对层级并检查设备名、越界及重解析点，禁止通过目录联接写出用户目标。</summary>
        /// <param name="folder">用户选择的根目录。</param><param name="name">使用正斜杠的相对路径。</param><returns>安全本地完整路径。</returns>
        internal static string LocalRelativeFile(string folder, string name)
        {
            OssFileClient.ValidatePath(name); if (String.IsNullOrEmpty(name)) throw new ArgumentException("本地相对路径为空。");
            var path = Path.GetFullPath(folder); CheckLocalNode(path);
            foreach (var segment in name.Split('/')) { path = LocalFile(path, segment); CheckLocalNode(path); }
            return path;
        }

        /// <summary>现有目录或文件若是重解析点，不允许下载沿其写入其他位置。</summary>
        /// <param name="path">本地候选节点。</param>
        private static void CheckLocalNode(string path) { if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("下载目标包含目录联接或符号链接，已停止写入。"); }

        /// <summary>增加独立重试和已完成记录清理入口，运行中不得重试或移除当前回调行。</summary>
        private void InitTaskActions()
        {
            taskTools.Controls.Add(MakeButton("重试所选", RetryDownloads));
            taskTools.Controls.Add(MakeButton("清除已完成", ClearDownloads));
        }

        /// <summary>只清除成功完成的记录，保留失败、取消及运行中记录。</summary>
        private void ClearDownloads()
        {
            foreach (var row in downloads.Items.Cast<ListViewItem>().Where(x => x.Tag is OssDownloadItem && ((OssDownloadItem)x.Tag).Finished && ((OssDownloadItem)x.Tag).State == "已完成" && (downloadQueue == null || !downloadQueue.Items.Contains((OssDownloadItem)x.Tag))).ToArray()) downloads.Items.Remove(row);
            foreach (ListViewItem row in downloads.Items) row.BackColor = UiStyle.StripeColor(row.Index);
            UpdateTaskDetails(); UpdateTaskPanelVisibility();
        }

        /// <summary>失败或取消项目按其原始源路径和本地根目录重试，不依赖当前浏览路径。</summary>
        private async void RetryDownloads()
        {
            if (Busy) return;
            var selected = downloads.SelectedItems.Cast<ListViewItem>().Select(x => x.Tag as OssDownloadItem).Where(x => x != null && x.Finished && (x.State == "失败" || x.State == "已取消")).ToList();
            if (selected.Count == 0) { status.Text = "请选中已失败或已取消的任务重试。"; return; }
            if (!ConfirmOverwrite()) return;
            foreach (var group in selected.GroupBy(x => x.RootFolder))
            {
                if (String.IsNullOrEmpty(group.Key)) continue;
                var entries = group.Select(x => new OssEntry { Name = x.Entry.Name, Path = x.SourcePath, Size = x.Entry.Size, IsDir = x.Entry.IsDir }).ToList();
                try { await DownloadSelection(entries, "", group.Key, overwrite.Checked); } catch (Exception ex) { ShowError(ex.Message); break; }
            }
        }
    }
}
