using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>连接页和文件页共享的配色、列表与菜单样式，避免界面之间互相依赖。</summary>
    internal static class UiStyle
    {
        /// <summary>文件页文字按钮及单行输入框的96DPI基准规格。</summary>
        internal const int ButtonWidth = 96, ButtonHeight = 32;

        /// <summary>当前是否使用深色主题；启动时按保存的设置初始化。</summary>
        internal static bool IsDark { get; set; }

        /// <summary>窗口底色。</summary>
        internal static Color Surface { get { return IsDark ? Color.FromArgb(30, 30, 30) : Color.FromArgb(243, 243, 243); } }

        /// <summary>卡片与列表底色。</summary>
        internal static Color Card { get { return IsDark ? Color.FromArgb(37, 37, 38) : Color.White; } }

        /// <summary>分隔线与控件描边。</summary>
        internal static Color Border { get { return IsDark ? Color.FromArgb(63, 63, 70) : Color.FromArgb(224, 224, 224); } }

        /// <summary>列表内更浅的分隔线。</summary>
        internal static Color BorderSoft { get { return IsDark ? Color.FromArgb(51, 51, 55) : Color.FromArgb(236, 236, 236); } }

        /// <summary>正文颜色。</summary>
        internal static Color Text { get { return IsDark ? Color.FromArgb(232, 232, 232) : Color.FromArgb(27, 27, 27); } }

        /// <summary>次要说明文字。</summary>
        internal static Color TextMuted { get { return IsDark ? Color.FromArgb(160, 160, 160) : Color.FromArgb(106, 106, 106); } }

        /// <summary>主操作色。</summary>
        internal static Color Accent { get { return IsDark ? Color.FromArgb(59, 130, 246) : Color.FromArgb(15, 108, 189); } }

        /// <summary>主操作悬停色。</summary>
        internal static Color AccentHover { get { return IsDark ? Color.FromArgb(37, 99, 235) : Color.FromArgb(17, 94, 163); } }

        /// <summary>主操作的浅色底，用于次要按钮。</summary>
        internal static Color AccentSoft { get { return IsDark ? Color.FromArgb(30, 58, 95) : Color.FromArgb(235, 243, 250); } }

        /// <summary>浅色按钮上的主操作文字色。</summary>
        internal static Color AccentText { get { return IsDark ? Color.FromArgb(122, 184, 245) : Color.FromArgb(20, 86, 148); } }

        /// <summary>主操作按钮上的文字色；明暗主题都用白色。</summary>
        internal static Color OnAccent { get { return Color.White; } }

        /// <summary>选中行底色。</summary>
        internal static Color Selection { get { return IsDark ? Color.FromArgb(38, 79, 120) : Color.FromArgb(225, 239, 251); } }

        /// <summary>选中行左侧强调条。</summary>
        internal static Color SelectionBar { get { return IsDark ? Color.FromArgb(59, 130, 246) : Color.FromArgb(15, 108, 189); } }

        /// <summary>危险操作底色。</summary>
        internal static Color DangerSoft { get { return IsDark ? Color.FromArgb(74, 31, 31) : Color.FromArgb(253, 235, 235); } }

        /// <summary>危险操作文字色。</summary>
        internal static Color DangerText { get { return IsDark ? Color.FromArgb(248, 113, 113) : Color.FromArgb(163, 45, 45); } }

        /// <summary>成功状态文字色，用于状态栏和进度终态。</summary>
        internal static Color SuccessText { get { return IsDark ? Color.FromArgb(74, 222, 128) : Color.FromArgb(21, 122, 75); } }

        /// <summary>进行中状态文字色。</summary>
        internal static Color InfoText { get { return AccentText; } }

        /// <summary>列表行悬停时的浅色高亮。</summary>
        internal static Color Hover { get { return IsDark ? Color.FromArgb(42, 45, 49) : Color.FromArgb(238, 244, 250); } }

        /// <summary>悬停行左侧强调条，弱于选中态。</summary>
        internal static Color HoverBar { get { return IsDark ? Color.FromArgb(59, 95, 133) : Color.FromArgb(122, 177, 227); } }

        /// <summary>进度条底槽颜色。</summary>
        internal static Color ProgressTrack { get { return IsDark ? Color.FromArgb(51, 56, 64) : Color.FromArgb(228, 233, 240); } }

        /// <summary>进度条进行中颜色。</summary>
        internal static Color ProgressRunning { get { return IsDark ? Color.FromArgb(59, 130, 246) : Color.FromArgb(15, 108, 189); } }

        /// <summary>进度条成功颜色。</summary>
        internal static Color ProgressDone { get { return IsDark ? Color.FromArgb(74, 222, 128) : Color.FromArgb(81, 171, 120); } }

        /// <summary>进度条失败颜色。</summary>
        internal static Color ProgressFail { get { return IsDark ? Color.FromArgb(248, 113, 113) : Color.FromArgb(214, 96, 96); } }

        /// <summary>统一各窗口状态栏文字颜色，完成绿色、失败红色、进行中灰色。</summary>
        /// <param name="label">状态栏标签。</param><param name="text">展示文本。</param><param name="state">success、error、info 或空表示普通。</param>
        internal static void SetStatus(Label label, string text, string state)
        {
            label.Text = text;
            label.ForeColor = state == "success" ? SuccessText : state == "error" ? DangerText : state == "info" ? InfoText : TextMuted;
        }

        /// <summary>整格绘制条纹和文字，避免系统主题仅绘制文字背景而漏掉空白区域。</summary>
        /// <param name="e">实际单元格绘制参数。</param>
        /// <param name="hoveredIndex">当前鼠标悬停行号，负数表示无悬停。</param>
        internal static void DrawCell(DrawListViewSubItemEventArgs e, int hoveredIndex)
        {
            var hovered = hoveredIndex >= 0 && e.Item.Index == hoveredIndex && !e.Item.Selected;
            var background = e.Item.Selected ? Selection : hovered ? Hover : e.Item.BackColor;
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            if (e.Item.Selected && e.ColumnIndex == 0)
            {
                var width = Math.Max(2, 3 * e.Item.ListView.DeviceDpi / 96);
                var height = Math.Max(0, e.Bounds.Height - 2);
                using (var bar = new SolidBrush(SelectionBar)) e.Graphics.FillRectangle(bar, new Rectangle(e.Bounds.Left, e.Bounds.Top + 1, width, height));
            }
            else if (hovered && e.ColumnIndex == 0)
            {
                var width = Math.Max(2, 3 * e.Item.ListView.DeviceDpi / 96);
                using (var bar = new SolidBrush(HoverBar)) e.Graphics.FillRectangle(bar, new Rectangle(e.Bounds.Left, e.Bounds.Top + 1, width, Math.Max(0, e.Bounds.Height - 2)));
            }
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            if (e.Header.TextAlign == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;
            else if (e.Header.TextAlign == HorizontalAlignment.Center) flags |= TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, e.SubItem.Font, Rectangle.Inflate(e.Bounds, -8, 0), e.Item.Selected ? Text : e.Item.ForeColor, flags);
            // 列右缘竖向网格线；最后一列与列表边框重合时不画。
            if (e.ColumnIndex < e.Item.ListView.Columns.Count - 1)
                using (var pen = new Pen(BorderSoft)) e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
        }

        /// <summary>整格绘制条纹和文字；不跟踪悬停时使用此简化入口。</summary>
        /// <param name="e">实际单元格绘制参数。</param>
        internal static void DrawCell(DrawListViewSubItemEventArgs e) { DrawCell(e, -1); }

        /// <summary>交替浅色背景，系统仍保留选中高亮。</summary>
        /// <param name="index">显示顺序。</param><returns>当前行背景。</returns>
        internal static Color StripeColor(int index) { return index % 2 == 0 ? Card : (IsDark ? Color.FromArgb(42, 42, 44) : Color.FromArgb(249, 250, 251)); }

        /// <summary>自绘浅色表头，仅保留底部一条分隔线，视觉更干净。</summary>
        /// <param name="e">实际列表表头绘制参数。</param>
        internal static void DrawHeader(DrawListViewColumnHeaderEventArgs e, string suffix)
        {
            using (var brush = new SolidBrush(IsDark ? Color.FromArgb(45, 45, 48) : Color.FromArgb(250, 250, 250))) e.Graphics.FillRectangle(brush, e.Bounds);
            using (var pen = new Pen(BorderSoft))
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            using (var font = new Font(e.Font, FontStyle.Regular))
                TextRenderer.DrawText(e.Graphics, e.Header.Text + suffix, font, Rectangle.Inflate(e.Bounds, -6, 0), IsDark ? Text : Color.FromArgb(59, 59, 59), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
        internal static void DrawHeader(DrawListViewColumnHeaderEventArgs e) { DrawHeader(e, ""); }

        /// <summary>用隐藏图标列表抬高明细行高度，改善行间呼吸感；不改变数据与排序。</summary>
        /// <param name="list">目标明细列表。</param><param name="height96">96DPI下行高像素。</param>
        internal static void SetRowHeight(ListView list, int height96)
        {
            var scale = Math.Max(1, list.DeviceDpi) / 96.0;
            var height = Math.Max(16, (int)Math.Round(height96 * scale));
            list.SmallImageList = new ImageList { ImageSize = new Size(1, height), ColorDepth = ColorDepth.Depth32Bit };
        }

        /// <summary>圆角矩形路径，按传入边界绘制；半径为零时退化为直角。</summary>
        /// <param name="bounds">目标矩形。</param><param name="radius">圆角半径。</param>
        /// <returns>调用者负责释放的路径。</returns>
        internal static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0) { path.AddRectangle(bounds); return path; }
            var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>按比例把颜色混合到另一个颜色，用于悬停、按下和禁用状态。</summary>
        /// <param name="from">起始颜色。</param><param name="to">目标颜色。</param><param name="amount">目标占比0到1。</param>
        /// <returns>混合后的颜色。</returns>
        internal static Color Blend(Color from, Color to, double amount)
        {
            var ratio = amount < 0 ? 0 : amount > 1 ? 1 : amount;
            return Color.FromArgb(
                (int)Math.Round(from.R + (to.R - from.R) * ratio),
                (int)Math.Round(from.G + (to.G - from.G) * ratio),
                (int)Math.Round(from.B + (to.B - from.B) * ratio));
        }

        /// <summary>感知亮度，用于判断按钮底色是深色还是浅色。</summary>
        /// <param name="color">颜色。</param><returns>0到1的亮度。</returns>
        internal static double Luminance(Color color)
        {
            return (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        }

        /// <summary>统一窗口外观：浅色底、Win11圆角与标题栏配色；系统不支持时静默跳过。</summary>
        /// <param name="form">目标窗体。</param>
        internal static void ApplyWindow(Form form)
        {
            if (form == null) return;
            form.BackColor = Surface;
            form.ForeColor = Text;
            form.HandleCreated += delegate { StyleChrome(form.Handle); };
            if (form.IsHandleCreated) StyleChrome(form.Handle);
        }

        /// <summary>按系统能力设置DWM属性；Windows 10没有标题栏配色，设置失败即忽略。</summary>
        /// <param name="hwnd">窗口句柄。</param>
        internal static void StyleChrome(IntPtr hwnd)
        {
            NativeMethods.TrySetWindowAttribute(hwnd, NativeMethods.DwmwaCornerPreference, NativeMethods.DwmcpRound);
            NativeMethods.TrySetWindowAttribute(hwnd, NativeMethods.DwmwaBorderColor, NativeMethods.ColorRef(Border));
            if (WindowsBuild() < 22000) return;
            NativeMethods.TrySetWindowAttribute(hwnd, NativeMethods.DwmwaCaptionColor, NativeMethods.ColorRef(Surface));
            NativeMethods.TrySetWindowAttribute(hwnd, NativeMethods.DwmwaTextColor, NativeMethods.ColorRef(Text));
        }

        /// <summary>当前系统构建号；读取失败时按旧系统处理。</summary>
        /// <returns>构建号。</returns>
        internal static int WindowsBuild()
        {
            try { return Environment.OSVersion.Version.Build; }
            catch (InvalidOperationException) { return 0; }
        }

        /// <summary>绘制无需字体或外部资源的统一线条图标，按实际单元格及DPI缩放。</summary>
        /// <param name="graphics">列表画布。</param><param name="bounds">操作按钮区域。</param>
        /// <param name="action">下载、链接、统计、属性的零起始索引。</param><param name="color">正常、选中或禁用颜色。</param>
        internal static void DrawActionIcon(Graphics graphics, Rectangle bounds, int action, Color color)
        {
            var state = graphics.Save();
            try
            {
                var size = Math.Min(bounds.Height - 8, Math.Min(bounds.Width - 10, 19F * graphics.DpiX / 96F));
                if (size <= 0) return;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.TranslateTransform(bounds.Left + (bounds.Width - size) / 2, bounds.Top + (bounds.Height - size) / 2); graphics.ScaleTransform(size / 16F, size / 16F);
                using (var pen = new Pen(color, 1.5F) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                {
                    if (action == 0) { graphics.DrawLine(pen, 8, 1, 8, 10); graphics.DrawLines(pen, new[] { new PointF(4, 6), new PointF(8, 10), new PointF(12, 6) }); graphics.DrawLines(pen, new[] { new PointF(2, 10), new PointF(2, 14), new PointF(14, 14), new PointF(14, 10) }); }
                    else if (action == 1) { graphics.DrawEllipse(pen, 1, 6, 9, 7); graphics.DrawEllipse(pen, 6, 1, 9, 7); graphics.DrawLine(pen, 6, 10, 10, 6); }
                    else if (action == 2) { graphics.DrawLines(pen, new[] { new PointF(1, 2), new PointF(1, 14), new PointF(15, 14) }); graphics.DrawRectangle(pen, 4, 8, 2, 6); graphics.DrawRectangle(pen, 8, 5, 2, 9); graphics.DrawRectangle(pen, 12, 2, 2, 12); }
                    else { graphics.DrawEllipse(pen, 1, 1, 14, 14); graphics.DrawLine(pen, 8, 7, 8, 12); using (var brush = new SolidBrush(color)) graphics.FillEllipse(brush, 7, 4, 2, 2); }
                }
            }
            finally { graphics.Restore(state); }
        }

        /// <summary>绑定响应式列宽：保留DPI最小宽度，剩余空间按权重分配，不预留虚假的滚动条空隙。</summary>
        /// <param name="list">明细列表。</param><param name="minimum">96DPI下各列最小宽度。</param><param name="weights">各列弹性权重，名称和路径优先。</param>
        internal static void BindColumns(ListView list, int[] minimum, int[] weights)
        {
            var changing = false;
            Action fit = delegate
            {
                if (changing || list.IsDisposed || list.Columns.Count != minimum.Length || list.ClientSize.Width <= 0) return;
                changing = true;
                try
                {
                    var scale = list.DeviceDpi / 96.0;
                    var widths = minimum.Select((value, i) => Math.Max((int)Math.Ceiling(value * scale), TextRenderer.MeasureText(list.Columns[i].Text, list.Font).Width + (int)(12 * scale))).ToArray();
                    // 末列预留2像素，避免总宽与可视宽完全相等时ListView在边缘临界状态弹出横向滚动条。
                    var space = Math.Max(0, list.ClientSize.Width - widths.Sum() - 2);
                    var total = weights.Sum(); var used = 0;
                    for (var i = 0; i < widths.Length; i++)
                    {
                        var extra = i == widths.Length - 1 ? space - used : space * weights[i] / total;
                        used += extra;
                        if (list.Columns[i].Width != widths[i] + extra) list.Columns[i].Width = widths[i] + extra;
                    }
                    // 窄窗口不足以容纳时间、进度等最小宽度时允许横向滚动，不压成不可读列。
                }
                finally { changing = false; }
            };
            list.ClientSizeChanged += delegate { fit(); };
            list.HandleCreated += delegate { fit(); };
            list.FontChanged += delegate { fit(); };
            list.Layout += delegate { fit(); };
            fit();
        }

        /// <summary>菜单使用相同间隔色，悬停优先显示蓝色，不隐藏禁用文本。</summary>
        internal sealed class StripeMenuRenderer : ToolStripProfessionalRenderer
        {
            /// <summary>绘制菜单项背景，不改动系统文字和快捷键渲染。</summary>
            /// <param name="e">菜单绘制参数。</param>
            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var color = e.Item.Selected && e.Item.Enabled ? AccentSoft : StripeColor(e.ToolStrip.Items.IndexOf(e.Item));
                using (var brush = new SolidBrush(color)) e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
            }
        }
    }
}
