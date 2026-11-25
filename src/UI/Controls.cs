using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RcloneOssManager
{

    /// <summary>启用列表双缓冲并跟踪鼠标悬停行，供自绘单元格渲染浅色高亮。</summary>
    internal sealed class BufferedListView : ListView
    {
        private int hoveredIndex = -1;

        /// <summary>当前悬停行号；无悬停或列表不可用时为负数。</summary>
        internal int HoveredIndex { get { return hoveredIndex; } }

        internal BufferedListView()
        {
            DoubleBuffered = true;
            MouseMove += delegate(object sender, MouseEventArgs e)
            {
                var item = GetItemAt(e.X, e.Y);
                var index = item == null ? -1 : item.Index;
                if (index == hoveredIndex) return;
                var previous = hoveredIndex;
                hoveredIndex = index;
                if (previous >= 0 && previous < Items.Count) Invalidate(Items[previous].Bounds);
                if (index >= 0) Invalidate(item.Bounds);
            };
            MouseLeave += delegate
            {
                var previous = hoveredIndex;
                hoveredIndex = -1;
                if (previous >= 0 && previous < Items.Count) Invalidate(Items[previous].Bounds);
            };
        }
    }
}
