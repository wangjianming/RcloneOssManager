using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>记住上次窗口位置与大小；恢复前按当前显示器裁剪，换显示器后不会留下看不见的窗口。</summary>
    internal sealed class WindowGeometry
    {
        /// <summary>标题栏至少要有这么多像素落在某个显示器内，窗口才算找得回来。</summary>
        internal const int ReachableWidth = 120, ReachableHeight = 20, TitleStripHeight = 40;

        public int Left { get; set; }
        public int Top { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }

        /// <summary>位置文件；读写失败只是放弃记忆窗口，不影响正常使用。</summary>
        /// <returns>JSON文件路径。</returns>
        internal static string FilePath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RcloneOssManager", "window.json");
        }

        /// <summary>读取上次位置；文件缺失或格式错误时返回空值，由调用方使用窗体默认值。</summary>
        /// <param name="path">文件路径。</param><returns>位置或空值。</returns>
        internal static WindowGeometry Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var value = new JavaScriptSerializer().Deserialize<WindowGeometry>(File.ReadAllText(path, Encoding.UTF8));
                if (value == null || value.Width <= 0 || value.Height <= 0) return null;
                return value;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        /// <summary>原子写入位置；失败不抛出，避免关窗时因磁盘问题卡住退出。</summary>
        /// <param name="path">文件路径。</param><param name="value">位置。</param>
        internal static void Save(string path, WindowGeometry value)
        {
            if (value == null) return;
            string stage = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(stage, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(stage, path, null); else File.Move(stage, path);
                stage = null;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            finally
            {
                try { if (stage != null && File.Exists(stage)) File.Delete(stage); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>把期望的位置裁剪到可见区域：先限制尺寸，标题栏不可达时居中到最大显示器。</summary>
        /// <param name="wanted">上次的位置。</param><param name="areas">各显示器工作区。</param><param name="minimum">窗体最小尺寸。</param>
        /// <returns>可直接应用的位置。</returns>
        internal static Rectangle Clamp(Rectangle wanted, Rectangle[] areas, Size minimum)
        {
            if (areas == null || areas.Length == 0) return wanted;
            var area = Largest(areas);
            var minimumWidth = Math.Max(1, minimum.Width);
            var minimumHeight = Math.Max(1, minimum.Height);
            var width = Math.Min(Math.Max(wanted.Width, minimumWidth), Math.Max(minimumWidth, area.Width));
            var height = Math.Min(Math.Max(wanted.Height, minimumHeight), Math.Max(minimumHeight, area.Height));
            var candidate = new Rectangle(wanted.X, wanted.Y, width, height);
            if (Reachable(candidate, areas)) return candidate;
            return new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
        }

        /// <summary>标题栏是否有足够区域落在某个显示器上。</summary>
        /// <param name="candidate">窗口位置。</param><param name="areas">各显示器工作区。</param><returns>是否可达。</returns>
        internal static bool Reachable(Rectangle candidate, Rectangle[] areas)
        {
            var strip = new Rectangle(candidate.Left, candidate.Top, candidate.Width, TitleStripHeight);
            foreach (var area in areas)
            {
                var overlap = Rectangle.Intersect(strip, area);
                if (overlap.Width >= ReachableWidth && overlap.Height >= ReachableHeight) return true;
            }
            return false;
        }

        /// <summary>面积最大的显示器工作区。</summary>
        /// <param name="areas">各显示器工作区。</param><returns>最大工作区。</returns>
        private static Rectangle Largest(Rectangle[] areas)
        {
            var best = areas[0];
            for (var i = 1; i < areas.Length; i++)
                if ((long)areas[i].Width * areas[i].Height > (long)best.Width * best.Height) best = areas[i];
            return best;
        }

        /// <summary>当前所有显示器的工作区，供裁剪使用。</summary>
        /// <returns>工作区数组。</returns>
        internal static Rectangle[] ScreenAreas()
        {
            try
            {
                var screens = Screen.AllScreens;
                var areas = new Rectangle[screens.Length];
                for (var i = 0; i < screens.Length; i++) areas[i] = screens[i].WorkingArea;
                return areas;
            }
            catch (InvalidOperationException) { return new Rectangle[0]; }
            catch (Exception) { return new Rectangle[0]; }
        }

        /// <summary>把保存的位置应用到窗体；设置失败时保持窗体原有布局。</summary>
        /// <param name="form">目标窗体。</param><param name="saved">保存的位置，可空。</param>
        internal static void ApplyTo(Form form, WindowGeometry saved)
        {
            if (form == null || saved == null) return;
            try
            {
                var bounds = Clamp(new Rectangle(saved.Left, saved.Top, saved.Width, saved.Height), ScreenAreas(), form.MinimumSize);
                form.StartPosition = FormStartPosition.Manual;
                form.Bounds = bounds;
                if (saved.Maximized) form.WindowState = FormWindowState.Maximized;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>以当前真实位置构造可保存的快照；最大化时记录还原后的尺寸。</summary>
        /// <param name="form">源窗体。</param><returns>位置快照。</returns>
        internal static WindowGeometry Capture(Form form)
        {
            if (form == null) return null;
            var bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            var value = new WindowGeometry();
            value.Left = bounds.Left; value.Top = bounds.Top; value.Width = bounds.Width; value.Height = bounds.Height;
            value.Maximized = form.WindowState == FormWindowState.Maximized;
            return value;
        }
    }
}
