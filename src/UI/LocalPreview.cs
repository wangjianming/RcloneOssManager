using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RcloneOssManager
{
    /// <summary>只允许常见文档及媒体通过本机应用预览，禁止远端程序与脚本执行。</summary>
    internal static class LocalPreview
    {
        internal const long MaxBytes = 128L * 1024 * 1024;
        private const string TextTypes = ".txt .md .log .csv .json .xml .yaml .yml .ini .conf .sql .java .cs .py .js .ts .css";
        private const string OpenTypes = ".png .jpg .jpeg .gif .bmp .webp .tif .tiff .pdf .docx .xlsx .pptx .mp4 .mkv .avi .mov .mp3 .wav .flac .ogg .m4a";

        /// <summary>检查扩展名白名单；脚本只通过记事本读取，不按关联执行。</summary>
        /// <param name="name">文件名。</param><returns>是否支持预览。</returns>
        internal static bool CanPreview(string name) { var ext = Path.GetExtension(name).ToLowerInvariant(); return ext.Length > 1 && (TextTypes.Split(' ').Contains(ext) || OpenTypes.Split(' ').Contains(ext)); }

        /// <summary>为每次预览创建独立缓存位置，避免覆盖用户文件或正在打开的预览。</summary>
        /// <param name="name">原始文件名。</param><returns>安全的本地缓存文件。</returns>
        internal static string CacheFile(string name)
        {
            if (!CanPreview(name)) throw new ArgumentException("此类型不支持安全预览，请先下载后自行打开。");
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RcloneOssManager", "preview", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder); return OssFileForm.LocalFile(folder, name);
        }

        /// <summary>准备只读缓存及互联网来源标记，交给本地应用但不上传编辑结果。</summary>
        /// <param name="file">已经核验的本地缓存。</param><returns>待启动的本地进程参数。</returns>
        internal static ProcessStartInfo OpenInfo(string file)
        {
            if (!CanPreview(file) || !File.Exists(file)) throw new ArgumentException("预览文件不存在或类型不受支持。");
            using (var handle = CreateFile(file + ":Zone.Identifier", 0x40000000, 1, IntPtr.Zero, 2, 0x80, IntPtr.Zero))
                if (!handle.IsInvalid) using (var stream = new FileStream(handle, FileAccess.Write)) { var bytes = System.Text.Encoding.UTF8.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"); stream.Write(bytes, 0, bytes.Length); }
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            if (TextTypes.Split(' ').Contains(Path.GetExtension(file).ToLowerInvariant()))
                return new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), "\"" + file + "\"") { UseShellExecute = true };
            return new ProcessStartInfo(file) { UseShellExecute = true, Verb = "open" };
        }

        /// <summary>通过Win32写入NTFS来源数据流，避开.NET Framework对冒号路径的限制。</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }

    internal sealed partial class OssFileForm
    {
        /// <summary>显式确认后下载单个预览副本并调用本地工具，不执行远端程序、不回写远端。</summary>
        private async void PreviewSelected()
        {
            if (Busy || list.SelectedItems.Count != 1) return;
            var entry = list.SelectedItems[0].Tag as OssEntry;
            if (entry == null || entry.IsDir) { status.Text = "请选择一个文件预览。"; return; }
            if (!LocalPreview.CanPreview(entry.Name) || entry.Size < 0 || entry.Size > LocalPreview.MaxBytes) { status.Text = "此类型不支持预览，或文件超过128 MiB，请使用普通下载。"; return; }
            if (MessageBox.Show(this, "将下载只读副本并使用本机记事本／默认文档或媒体应用打开。\r\n只预览可信文件，不执行程序、快捷方式或宏文档；编辑结果不会上传。\r\n文件：" + entry.Name, "本地预览", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            var path = OssFileClient.Join(currentPath, entry.Path); string local = null; var ready = false;
            await Execute("准备本地预览", async (token, progress) =>
            {
                local = LocalPreview.CacheFile(entry.Name);
                await Task.Run(() => files.DownloadFile(path, local, false, token, progress, null, LocalPreview.MaxBytes)); token.ThrowIfCancellationRequested(); ready = true;
            }, false);
            if (!ready || closeAfterCancel || IsDisposed) { if (local != null) try { Directory.Delete(Path.GetDirectoryName(local), true); } catch (IOException) { } return; }
            try { Process.Start(LocalPreview.OpenInfo(local)); status.Text = "已在本地工具打开只读预览，远端文件未修改。缓存：" + local; }
            catch (Exception) { status.Text = "预览副本已下载，但未找到可用本地应用。请安装／设置该文件类型的默认应用后重试。"; }
        }
    }
}
