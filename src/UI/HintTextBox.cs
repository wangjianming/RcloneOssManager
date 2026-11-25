using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>保留单行输入与Enter导航，支持等高布局和旧版.NET原生占位提示。</summary>
    internal sealed class HintTextBox : TextBox
    {
        /// <summary>Windows单行编辑控件的占位提示消息。</summary>
        private const int SetCueBanner = 0x1501;
        /// <summary>只作为提示显示，不写入Text，也不参与查询或导航。</summary>
        private string hint = "";
        private bool hovering;
        /// <summary>输入框为空时显示的提示，焦点移入后仍保留。</summary>
        internal string Hint { get { return hint; } set { hint = value ?? ""; if (IsHandleCreated) ApplyHint(); } }

        /// <summary>关闭系统默认高度锁定，使单行输入框与按钮使用相同高度；边框由本控件自绘。</summary>
        internal HintTextBox() { AutoSize = false; Height = UiStyle.ButtonHeight; BorderStyle = BorderStyle.None; }

        /// <summary>句柄重建时恢复原生提示，不使用Text模拟占位文字。</summary>
        /// <param name="e">句柄创建事件。</param>
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyHint(); RefreshFrame(); }

        /// <summary>通过Unicode原生消息设置提示，避免引入新框架或第三方控件。</summary>
        private void ApplyHint() { SendMessage(Handle, SetCueBanner, (IntPtr)1, hint); }

        /// <summary>在原生单行编辑区上下分配空白，文字、光标和占位提示共同居中，左右为自绘边框留位。</summary>
        /// <param name="m">窗口消息；只调整非客户区计算，不启用多行输入。</param>
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x83 && m.LParam != IntPtr.Zero)
            {
                var rect = (NativeRect)Marshal.PtrToStructure(m.LParam, typeof(NativeRect));
                var pad = Math.Max(0, (rect.Bottom - rect.Top - Font.Height - 2) / 2);
                rect.Top += pad; rect.Bottom -= pad;
                rect.Left += FrameWidth; rect.Right -= FrameWidth;
                Marshal.StructureToPtr(rect, m.LParam, false);
            }
            else if (m.Msg == 0x85)
            {
                // 系统在None模式下不绘制边框，焦点、悬停变化后通过FRAMECHANGED重入此消息。
                DrawFrame();
            }
        }

        /// <summary>边框三态颜色：聚焦主色、悬停加深、普通浅灰。</summary>
        /// <returns>当前边框颜色。</returns>
        private Color BorderColorForState() { return Focused ? UiStyle.Accent : hovering ? UiStyle.Blend(UiStyle.Border, UiStyle.Text, 0.35) : UiStyle.Border; }

        /// <summary>按DPI缩放的边框像素宽度。</summary>
        private int FrameWidth { get { return Math.Max(1, DeviceDpi / 96); } }

        /// <summary>在窗口非客户区绘制1像素自绘边框。</summary>
        private void DrawFrame()
        {
            if (!IsHandleCreated) return;
            var rect = new RECT();
            if (!GetWindowRect(Handle, ref rect)) return;
            var hdc = GetWindowDC(Handle);
            try
            {
                using (var graphics = Graphics.FromHdc(hdc))
                using (var pen = new Pen(BorderColorForState()) { Width = FrameWidth })
                    graphics.DrawRectangle(pen, 0, 0, rect.Right - rect.Left - 1, rect.Bottom - rect.Top - 1);
            }
            catch { }
            finally { ReleaseDC(Handle, hdc); }
        }

        /// <summary>触发非客户区重算与重绘，用于三态边框刷新。</summary>
        private void RefreshFrame() { if (IsHandleCreated) SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x37); }

        /// <summary>尺寸或字体变化后重新计算客户区，支持窗口缩放和DPI字体调整。</summary>
        /// <param name="e">尺寸变化参数。</param>
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RefreshFrame(); }

        /// <summary>字体变化后保持编辑区垂直居中。</summary>
        /// <param name="e">字体变化参数。</param>
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); RefreshFrame(); }

        /// <summary>悬停、聚焦切换时刷新边框颜色。</summary>
        /// <param name="e">事件参数。</param>
        protected override void OnMouseEnter(EventArgs e) { hovering = true; RefreshFrame(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; RefreshFrame(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { RefreshFrame(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { RefreshFrame(); base.OnLostFocus(e); }

        /// <summary>只重新布局原生边框，不移动、不激活输入框。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { internal int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr handle, ref RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { internal int Left, Top, Right, Bottom; }

        /// <summary>取窗口设备上下文以绘制非客户区；用后必须释放。</summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr handle, IntPtr hdc);

        /// <summary>向本进程编辑框发送占位文本。</summary>
        /// <param name="handle">编辑框句柄。</param><param name="message">消息。</param><param name="showOnFocus">焦点内显示标志。</param><param name="text">提示文字。</param><returns>原生消息结果。</returns>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr showOnFocus, string text);
    }
}
