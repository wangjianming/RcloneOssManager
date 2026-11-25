using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>圆角扁平按钮：自绘底色与文字，悬停/按下/禁用都有明确反馈，不依赖系统主题。</summary>
    internal class FlatButton : Button
    {
        private bool hovering, pressing;

        /// <summary>固定的圆角半径（像素）；0 表示按 96DPI 的 6 像素自动缩放。</summary>
        internal int CornerRadius { get; set; }

        internal FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            DoubleBuffered = true;
        }

        /// <summary>按状态计算底色：深色按钮悬停变亮，浅色按钮悬停变深，禁用时向窗口底色靠拢。</summary>
        /// <returns>当前应绘制的底色。</returns>
        internal Color BackgroundForState()
        {
            var baseColor = BackColor;
            if (!Enabled) return UiStyle.Blend(baseColor, UiStyle.Surface, 0.55);
            var dark = UiStyle.Luminance(baseColor) < 0.5;
            if (pressing) return dark ? UiStyle.Blend(baseColor, Color.Black, 0.18) : UiStyle.Blend(baseColor, Color.Black, 0.10);
            if (hovering) return dark ? UiStyle.Blend(baseColor, Color.White, 0.12) : UiStyle.Blend(baseColor, Color.Black, 0.05);
            return baseColor;
        }

        /// <summary>文字颜色随底色明暗与可用状态调整，保证对比度。</summary>
        /// <returns>当前应绘制的文字色。</returns>
        internal Color ForegroundForState()
        {
            if (!Enabled) return UiStyle.Blend(BackColor, Color.Black, UiStyle.Luminance(BackColor) < 0.5 ? 0.35 : 0.45);
            var fore = ForeColor;
            if (UiStyle.Luminance(BackgroundForState()) < 0.5 && UiStyle.Luminance(fore) < 0.5)
                return UiStyle.Blend(fore, Color.White, 0.75);
            if (UiStyle.Luminance(BackgroundForState()) > 0.5 && UiStyle.Luminance(fore) > 0.92)
                return UiStyle.Text;
            return fore;
        }

        /// <summary>按 DPI 缩放的圆角半径。</summary>
        /// <returns>像素半径。</returns>
        internal int RadiusForState()
        {
            if (CornerRadius > 0) return CornerRadius;
            var scaled = (int)Math.Round(6.0 * DeviceDpi / 96.0);
            return Math.Max(2, Math.Min(scaled, Math.Min(Width, Height) / 3));
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; pressing = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { if (mevent.Button == MouseButtons.Left) { pressing = true; Invalidate(); } base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { if (pressing) { pressing = false; Invalidate(); } base.OnMouseUp(mevent); }
        protected override void OnEnabledChanged(EventArgs e) { hovering = false; pressing = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width <= 0 || Height <= 0) return;
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            var radius = RadiusForState();

            // 先铺父容器底色，圆角外侧不会留下上一帧的残影。
            using (var backdrop = new SolidBrush(Parent == null ? UiStyle.Surface : Parent.BackColor))
                graphics.FillRectangle(backdrop, new Rectangle(0, 0, Width, Height));

            using (var path = UiStyle.RoundedPath(bounds, radius))
            using (var brush = new SolidBrush(BackgroundForState()))
                graphics.FillPath(brush, path);

            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            var textBounds = Rectangle.Inflate(bounds, -3, 0);
            TextRenderer.DrawText(graphics, Text, Font, textBounds, ForegroundForState(), flags);

            if (Focused && ShowFocusCues)
            {
                var inner = Rectangle.Inflate(bounds, -3, -3);
                if (inner.Width > 0 && inner.Height > 0)
                {
                    using (var pen = new Pen(UiStyle.Blend(ForegroundForState(), BackgroundForState(), 0.55), 1F) { DashStyle = DashStyle.Dot })
                    using (var focus = UiStyle.RoundedPath(inner, Math.Max(1, radius - 2)))
                        graphics.DrawPath(pen, focus);
                }
            }
        }
    }

    /// <summary>无文字的图标按钮：在FlatButton自绘完成后追加矢量图标。</summary>
    internal sealed class IconFlatButton : FlatButton
    {
        /// <summary>在底色、文字与焦点框之后执行的自定义绘制。</summary>
        private readonly Action<PaintEventArgs> customPaint;

        internal IconFlatButton(Action<PaintEventArgs> customPaint) { this.customPaint = customPaint; }

        /// <summary>FlatButton的OnPaint不经过基类链路，Paint事件不会被触发，图标改在此处追加绘制。</summary>
        /// <param name="e">绘制参数。</param>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (customPaint != null) customPaint(e);
        }
    }
}
