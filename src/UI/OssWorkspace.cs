using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RcloneOssManager
{
    internal sealed partial class OssFileForm
    {
        /// <summary>地址行依次为向上、后退、前进、完整路径、复制、转到和刷新，导航图标集中在输入框两侧。</summary>
        /// <returns>单行地址栏。</returns>
        private Control InitAddressBar()
        {
            var bar = new TableLayoutPanel { Name = "address", Dock = DockStyle.Fill, RowCount = 1, ColumnCount = 7, Margin = new Padding(0) };
            bar.RowStyles.Add(new RowStyle(SizeType.Absolute, UiStyle.ButtonHeight));
            for (var i = 0; i < 7; i++) bar.ColumnStyles.Add(new ColumnStyle(i == 3 ? SizeType.Percent : SizeType.Absolute, i == 3 ? 100 : 38));
            bar.Controls.Add(NavigationButton("navUp", "向上一级（Alt+↑ 或 Backspace）", 0, GoParent), 0, 0);
            backPath = NavigationButton("navBack", "后退（Alt+←）", 1, () => MoveHistory(-1)); bar.Controls.Add(backPath, 1, 0);
            forwardPath = NavigationButton("navForward", "前进（Alt+→）", 2, () => MoveHistory(1)); bar.Controls.Add(forwardPath, 2, 0);
            pathBox.Name = "fullPath"; pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; pathBox.Height = UiStyle.ButtonHeight; pathBox.Margin = new Padding(0, 0, 6, 0); pathBox.Text = DisplayPath(""); bar.Controls.Add(pathBox, 3, 0);
            copyPathButton = NavigationButton("copyFullPath", "复制当前目录完整路径（含服务地址与端口，不含认证）", 3, CopyCurrentPath); bar.Controls.Add(copyPathButton, 4, 0);
            bar.Controls.Add(NavigationButton("navGo", "转到地址（Enter）", 4, NavigateAddress), 5, 0);
            bar.Controls.Add(NavigationButton("navRefresh", "刷新（F5）", 5, () => Navigate(currentPath)), 6, 0);
            status.Click += delegate { CopyStatusPath(); };
            return bar;
        }

        /// <summary>用矢量绘图创建图标按钮，不依赖外部图标字体或图片。</summary>
        /// <param name="name">控件标识。</param><param name="tip">中文提示。</param><param name="icon">图形索引。</param><param name="action">点击操作。</param><returns>等高按钮。</returns>
        private Button NavigationButton(string name, string tip, int icon, Action action)
        {
            // FlatButton自绘不触发Paint事件，图标绘制通过IconFlatButton的自定义绘制钩子执行。
            IconFlatButton button = null;
            button = new IconFlatButton(delegate(PaintEventArgs e)
            {
                var x = button.ClientSize.Width / 2; var y = button.ClientSize.Height / 2;
                using (var pen = new Pen(button.Enabled ? UiStyle.AccentText : Color.Gray, 1.6F))
                {
                    if (icon == 3) { e.Graphics.DrawRectangle(pen, x - 6, y - 3, 10, 10); e.Graphics.DrawLines(pen, new[] { new Point(x - 3, y - 5), new Point(x - 3, y - 7), new Point(x + 7, y - 7), new Point(x + 7, y + 3), new Point(x + 5, y + 3) }); }
                    else if (icon == 5) { e.Graphics.DrawArc(pen, x - 7, y - 7, 14, 14, 40, 285); e.Graphics.DrawLines(pen, new[] { new Point(x + 7, y - 7), new Point(x + 7, y - 1), new Point(x + 1, y - 1) }); }
                    else if (icon == 0) { e.Graphics.DrawLine(pen, x, y + 7, x, y - 7); e.Graphics.DrawLines(pen, new[] { new Point(x - 6, y - 1), new Point(x, y - 7), new Point(x + 6, y - 1) }); }
                    else { var direction = icon == 1 ? -1 : 1; e.Graphics.DrawLine(pen, x - 7, y, x + 7, y); e.Graphics.DrawLines(pen, new[] { new Point(x + direction, y - 6), new Point(x + direction * 7, y), new Point(x + direction, y + 6) }); }
                }
            });
            button.Name = name; button.Size = new Size(32, UiStyle.ButtonHeight); button.Margin = new Padding(0, 0, 6, 0); button.AccessibleName = tip;
            button.BackColor = UiStyle.AccentSoft; button.ForeColor = UiStyle.AccentText; button.Cursor = Cursors.Hand;
            button.Click += delegate { if (!Busy) action(); };
            actionTip.SetToolTip(button, tip); actions.Add(button);
            return button;
        }

        /// <summary>生成不含认证和签名参数的完整HTTP路径并还原可读字符；非HTTP后端保留rclone路径。</summary>
        /// <param name="path">连接内相对路径。</param><returns>地址栏直接显示的完整路径。</returns>
        private string DisplayPath(string path)
        {
            try { return Uri.UnescapeDataString(files.Url(path)); }
            catch (InvalidOperationException) { return files.Target(path); }
        }

        /// <summary>唯一选中文件时取该文件，否则返回空；用于展示和复制当前文件的绝对路径。</summary>
        /// <returns>选中的文件，未选、多选或目录时为 null。</returns>
        private OssEntry SelectedFile()
        {
            if (list.SelectedItems.Count != 1) return null;
            var entry = list.SelectedItems[0].Tag as OssEntry;
            return entry != null && !entry.IsDir ? entry : null;
        }

        /// <summary>选中单个文件时状态栏展示其完整绝对路径，复制按钮同步指向该文件；地址栏仍只显示目录。</summary>
        private void RefreshSelectionPath()
        {
            var entry = SelectedFile();
            statusFileUrl = entry == null ? null : DisplayPath(entry.Path);
            copyPathButton.AccessibleName = entry == null ? "复制当前目录完整路径（含服务地址与端口，不含认证）" : "复制选中文件“" + entry.Name + "”的完整路径（含服务地址与端口，不含认证）";
            actionTip.SetToolTip(copyPathButton, copyPathButton.AccessibleName);
            status.Cursor = entry == null ? Cursors.Default : Cursors.Hand;
            if (entry != null) { status.Text = "当前文件（点击状态栏复制）：" + statusFileUrl; actionTip.SetToolTip(status, "点击复制完整文件路径"); }
        }

        /// <summary>点击状态栏复制当前展示的文件绝对路径；未选中文件时不动作。</summary>
        private void CopyStatusPath()
        {
            if (statusFileUrl == null) return;
            try { Clipboard.SetText(statusFileUrl); status.Text = "已复制当前文件完整路径；私有对象仍需认证。"; }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        /// <summary>唯一选中文件时复制该文件绝对路径，否则复制当前目录；端点原有的端口保持不变。</summary>
        private void CopyCurrentPath()
        {
            try
            {
                var entry = SelectedFile();
                Clipboard.SetText(DisplayPath(entry == null ? currentPath : entry.Path));
                status.Text = entry == null ? "已复制当前目录完整路径；私有对象仍需认证。" : "已复制选中文件“" + entry.Name + "”的完整路径；私有对象仍需认证。";
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        /// <summary>为唯一选中的文件生成7天有效的签名共享链接并复制；签名是临时凭据，只经用户明确操作离开本机。</summary>
        private async void ShareSelected()
        {
            if (Busy) return;
            var entry = SelectedFile();
            if (entry == null) { status.Text = "请先选中一个文件，再生成共享链接。"; return; }
            await Execute("生成共享链接", async (token, progress) =>
            {
                var url = await Task.Run(() => files.ShareLink(entry.Path, token));
                Clipboard.SetText(url);
                status.Text = "已复制共享链接（7天有效）；签名是临时凭据，请只分享给可信对象。";
            }, false);
        }

        /// <summary>地址行只做导航，不将粘贴的地址自动当作文件下载或执行。</summary>
        private void NavigateAddress()
        {
            if (Busy) return;
            try { NavigateDirectoryOnly(ParseLocation(pathBox.Text, DisplayPath(""), files.Target(""))); }
            catch (ArgumentException ex)
            {
                // 虚拟主机式S3的桶名位于域名中；当前桶的完整地址也必须能按Enter原样导航。
                if (bucketBased && currentPath != "")
                {
                    var bucket = currentPath.Split('/')[0];
                    try { var path = ParseLocation(pathBox.Text, DisplayPath(bucket), files.Target(bucket) + "/"); NavigateDirectoryOnly(OssFileClient.Join(bucket, path)); return; }
                    catch (ArgumentException) { }
                }
                ShowError(ex.Message);
            }
        }

        /// <summary>地址栏只到目录：粘贴对象级地址时转到所在目录，绝对路径不展示文件名。</summary>
        /// <param name="path">解析后的相对路径。</param>
        private void NavigateDirectoryOnly(string path)
        {
            OssEntry found = null;
            try { found = files.Find(path, CancellationToken.None); } catch (Exception) { found = null; }
            if (found != null && !found.IsDir)
            {
                var parent = path.LastIndexOf('/') < 0 ? "" : path.Substring(0, path.LastIndexOf('/'));
                pendingNotice = "地址指向文件“" + found.Name + "”，已转到所在目录；下载对象请用“更多 → 下载地址”。";
                Navigate(parent); return;
            }
            Navigate(path);
        }

        /// <summary>将本连接的完整URL或rclone路径还原为相对目录，拒绝跨站地址和认证查询。</summary>
        /// <param name="text">用户输入。</param><param name="root">本连接HTTP根路径。</param><param name="target">本连接rclone根标识。</param><returns>经验证的相对路径。</returns>
        internal static string ParseLocation(string text, string root, string target)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith(target, StringComparison.Ordinal)) text = text.Substring(target.Length);
            else
            {
                Uri uri;
                if (Uri.TryCreate(text, UriKind.Absolute, out uri))
                {
                    Uri baseUri;
                    if (!Uri.TryCreate(root.TrimEnd('/') + "/", UriKind.Absolute, out baseUri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || uri.Scheme != baseUri.Scheme || uri.Host != baseUri.Host || uri.Port != baseUri.Port || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "") throw new ArgumentException("请输入本连接的目录地址，不接受其他服务、认证或签名参数。");
                    var prefix = baseUri.AbsolutePath;
                    if (uri.AbsolutePath.TrimEnd('/') == prefix.TrimEnd('/')) text = "";
                    else if (uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) text = Uri.UnescapeDataString(uri.AbsolutePath.Substring(prefix.Length));
                    else throw new ArgumentException("目录地址不属于当前连接。");
                }
            }
            text = text.TrimEnd('/'); OssFileClient.ValidatePath(text); return text;
        }
    }
}
