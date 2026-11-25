using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ComData = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RcloneOssManager
{
    /// <summary>Shell虚拟文件：描述符不下载，目标请求FileContents时才下载并提供磁盘文件流。</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class OssDragData : ComData, IDisposable
    {
        // Shell格式号、描述符及会话缓存；不把大文件完整装入内存。
        private readonly short contents = (short)DataFormats.GetFormat("FileContents").Id;
        private readonly DataObject metadata = new DataObject();
        private readonly List<FORMATETC> formats = new List<FORMATETC>();
        private readonly Dictionary<int, string> downloaded = new Dictionary<int, string>();
        private readonly OssFileClient files;
        private readonly List<OssEntry> entries;
        private readonly string parent, cache;
        private readonly CancellationToken token;
        private readonly Action<string> progress;
        private readonly MemoryStream descriptors;
        private bool disposed;
        /// <summary>目标读取失败时保留可见原因，OLE不能将异常当成下载成功。</summary>
        internal string LastError { get; private set; }

        /// <summary>验证所选名称，创建独立缓存会话；不修改远端。</summary>
        /// <param name="client">远端客户端。</param><param name="path">父目录。</param><param name="selection">所选文件。</param>
        /// <param name="cancellation">取消信号。</param><param name="report">进度回调。</param><param name="cacheRoot">可选隔离缓存根目录。</param>
        internal OssDragData(OssFileClient client, string path, List<OssEntry> selection, CancellationToken cancellation, Action<string> report, string cacheRoot = null)
        {
            files = client; parent = path; entries = selection; token = cancellation; progress = report;
            if (entries.Count == 0 || entries.Any(x => x.IsDir)) throw new ArgumentException("拖出仅支持文件，不支持目录或存储桶。");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                OssFileForm.LocalFile(Path.GetTempPath(), entry.Name); OssFileClient.ValidatePath(entry.Path);
                if (entry.Name.Length >= 260 || !seen.Add(entry.Name)) throw new ArgumentException("拖出文件名过长或存在Windows大小写冲突。");
            }
            var root = cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RcloneOssManager", "drag-downloads");
            Directory.CreateDirectory(root); CleanOld(root);
            cache = Path.Combine(root, Guid.NewGuid().ToString("N"));
            descriptors = new MemoryStream();
            using (var writer = new BinaryWriter(descriptors, System.Text.Encoding.Unicode, true))
            {
                writer.Write(entries.Count);
                foreach (var entry in entries)
                {
                    // FILEDESCRIPTORW为固定Unicode结构；时间、大小仅在后端值有效时声明。
                    var descriptor = new FileDescriptor { Flags = 0x80004004, Attributes = 128, Name = entry.Name };
                    if (entry.Size >= 0) { descriptor.Flags |= 0x40; descriptor.SizeHigh = (uint)((ulong)entry.Size >> 32); descriptor.SizeLow = (uint)entry.Size; }
                    DateTimeOffset time;
                    if (DateTimeOffset.TryParse(entry.ModTime, out time) && time.Year >= 1601)
                    { var value = time.UtcDateTime.ToFileTimeUtc(); descriptor.Flags |= 0x20; descriptor.WriteTime = new System.Runtime.InteropServices.ComTypes.FILETIME { dwLowDateTime = (int)value, dwHighDateTime = (int)(value >> 32) }; }
                    var size = Marshal.SizeOf(typeof(FileDescriptor)); var pointer = Marshal.AllocHGlobal(size);
                    try { Marshal.StructureToPtr(descriptor, pointer, false); var bytes = new byte[size]; Marshal.Copy(pointer, bytes, 0, size); writer.Write(bytes); }
                    finally { Marshal.FreeHGlobal(pointer); }
                }
            }
            descriptors.Position = 0; metadata.SetData("FileGroupDescriptorW", descriptors);
            metadata.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
            formats.Add(Format("FileGroupDescriptorW", -1, TYMED.TYMED_HGLOBAL));
            formats.Add(Format("Preferred DropEffect", -1, TYMED.TYMED_HGLOBAL));
            for (var index = 0; index < entries.Count; index++) formats.Add(Format("FileContents", index, TYMED.TYMED_ISTREAM));
        }

        /// <summary>构造Shell格式。</summary>
        /// <param name="name">格式名。</param><param name="index">文件索引。</param><param name="medium">介质。</param><returns>格式描述。</returns>
        private static FORMATETC Format(string name, int index, TYMED medium) { return new FORMATETC { cfFormat = (short)DataFormats.GetFormat(name).Id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = medium }; }
        /// <summary>返回本次缓存路径，仅用于隔离验证及清理。</summary><returns>会话目录。</returns>
        internal string CachePath() { return cache; }

        /// <summary>目标请求真实文件流；后台下载期间泵送界面，取消仍可生效。</summary>
        /// <param name="format">目标格式及文件索引。</param><param name="medium">目标负责释放的介质。</param>
        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            medium = new STGMEDIUM(); Marshal.ThrowExceptionForHR(QueryGetData(ref format));
            if (format.cfFormat != contents) { ((ComData)metadata).GetData(ref format, out medium); return; }
            try
            {
                token.ThrowIfCancellationRequested();
                var index = format.lindex; string local;
                if (!downloaded.TryGetValue(index, out local))
                {
                    Directory.CreateDirectory(cache); var entry = entries[index]; local = OssFileForm.LocalFile(cache, entry.Name);
                    var target = local;
                    var task = Task.Run(() => files.DownloadFile(OssFileClient.Join(parent, entry.Path), target, false, token, progress));
                    // 等待句柄替代固定 Sleep 轮询：下载完成或取消都能立即返回，期间仍泵送界面消息。
                    var completed = ((IAsyncResult)task).AsyncWaitHandle;
                    while (!task.IsCompleted) { WaitHandle.WaitAny(new WaitHandle[] { completed }, 20); Application.DoEvents(); }
                    task.GetAwaiter().GetResult(); downloaded.Add(index, local);
                }
                IStream stream; Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(local, 0x20, 0, false, IntPtr.Zero, out stream));
                try { medium.tymed = TYMED.TYMED_ISTREAM; medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)); }
                finally { Marshal.ReleaseComObject(stream); }
            }
            catch (Exception ex) { LastError = ex.Message; throw new COMException("拖出下载失败：" + ex.Message, unchecked((int)0x80004005)); }
        }
        /// <summary>只声明有效格式和文件索引，未知格式交给标准DataObject。</summary>
        /// <param name="format">目标请求。</param><returns>HRESULT。</returns>
        public int QueryGetData(ref FORMATETC format)
        {
            if (disposed) return unchecked((int)0x80004005);
            if (format.cfFormat != contents) return ((ComData)metadata).QueryGetData(ref format);
            if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return unchecked((int)0x8004006B);
            if (format.lindex < 0 || format.lindex >= entries.Count) return unchecked((int)0x80040068);
            return (format.tymed & TYMED.TYMED_ISTREAM) != 0 ? 0 : unchecked((int)0x80040069);
        }
        /// <summary>枚举可读格式。</summary><param name="direction">枚举方向。</param><returns>格式枚举器。</returns>
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) { return direction == DATADIR.DATADIR_GET ? new FormatEnumerator(formats.ToArray()) : ((ComData)metadata).EnumFormatEtc(direction); }
        /// <summary>不支持将文件流写入调用者介质。</summary><param name="format">格式。</param><param name="medium">介质。</param>
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) { throw new COMException("不支持该介质", unchecked((int)0x80040069)); }
        /// <summary>保持调用者格式。</summary><param name="input">输入格式。</param><param name="output">输出格式。</param><returns>同格式状态。</returns>
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x40130; }
        /// <summary>接受目标的复制结果元数据，不因此删除远端。</summary><param name="format">格式。</param><param name="medium">介质。</param><param name="release">是否转移所有权。</param>
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) { ((ComData)metadata).SetData(ref format, ref medium, release); }
        /// <summary>不提供异步通知。</summary><param name="format">格式。</param><param name="flags">通知选项。</param><param name="sink">通知目标。</param><param name="connection">连接号。</param><returns>不支持状态。</returns>
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        /// <summary>无通知连接可释放。</summary><param name="connection">连接号。</param>
        public void DUnadvise(int connection) { throw new COMException("不支持通知", unchecked((int)0x80040003)); }
        /// <summary>无通知连接。</summary><param name="enumerator">枚举器。</param><returns>不支持状态。</returns>
        public int EnumDAdvise(out IEnumSTATDATA enumerator) { enumerator = null; return unchecked((int)0x80040003); }

        /// <summary>释放会话缓存；目标尚持有文件流时留待下次清理，不能提前截断其读取。</summary>
        public void Dispose() { disposed = true; descriptors.Dispose(); try { if (Directory.Exists(cache)) Directory.Delete(cache, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        /// <summary>只清理超过一天的本工具GUID会话，跳过链接及无法删除的活动文件。</summary><param name="root">缓存根目录。</param>
        private static void CleanOld(string root)
        {
            foreach (var path in Directory.GetDirectories(root))
            {
                Guid id; var info = new DirectoryInfo(path);
                if (!Guid.TryParseExact(info.Name, "N", out id) || (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-1)) continue;
                try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, bool create, IntPtr template, out IStream stream);
        /// <summary>Windows FILEDESCRIPTORW布局，不包含托管引用之外的可变缓冲区。</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        private struct FileDescriptor
        {
            public uint Flags; public Guid ClassId; public int Width, Height, X, Y; public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
            public uint SizeHigh, SizeLow;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        }
        /// <summary>Shell格式枚举器，Clone保留当前位置。</summary>
        [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
        private sealed class FormatEnumerator : IEnumFORMATETC
        {
            private readonly FORMATETC[] items; private int position;
            /// <summary>绑定格式数组。</summary><param name="values">格式集合。</param>
            internal FormatEnumerator(FORMATETC[] values) { items = values; }
            /// <summary>读取下一批格式。</summary><param name="count">数量。</param><param name="values">输出数组。</param><param name="fetched">实际数量。</param><returns>读取状态。</returns>
            public int Next(int count, FORMATETC[] values, int[] fetched) { var read = 0; while (read < count && position < items.Length) values[read++] = items[position++]; if (fetched != null) fetched[0] = read; return read == count ? 0 : 1; }
            /// <summary>跳过格式。</summary><param name="count">数量。</param><returns>跳过状态。</returns>
            public int Skip(int count) { var remaining = items.Length - position; position += Math.Min(count, remaining); return count <= remaining ? 0 : 1; }
            /// <summary>重置枚举。</summary><returns>成功状态。</returns>
            public int Reset() { position = 0; return 0; }
            /// <summary>复制当前位置。</summary><param name="copy">副本。</param>
            public void Clone(out IEnumFORMATETC copy) { copy = new FormatEnumerator(items) { position = position }; }
        }
    }

    /// <summary>OLE拖拽源接口；仅供Windows Shell调用。</summary>
    [ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IOssDropSource
    {
        /// <summary>Escape取消，松开左键完成。</summary><param name="escape">Escape状态。</param><param name="keys">鼠标状态。</param><returns>OLE状态。</returns>
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
        /// <summary>使用系统默认光标。</summary><param name="effect">操作效果。</param><returns>默认光标状态。</returns>
        [PreserveSig] int GiveFeedback(uint effect);
    }
    /// <summary>只允许复制的OLE拖出操作，不接受目标的移动语义。</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class OssDropSource : IOssDropSource
    {
        private readonly CancellationToken token;
        /// <summary>绑定取消信号。</summary><param name="cancellation">当前任务取消信号。</param>
        internal OssDropSource(CancellationToken cancellation) { token = cancellation; }
        /// <summary>启动原生OLE拖拽。</summary><param name="data">文件数据源。</param><param name="token">取消信号。</param><returns>实际目标效果。</returns>
        internal static uint Start(OssDragData data, CancellationToken token) { Application.OleRequired(); uint effect; Marshal.ThrowExceptionForHR(DoDragDrop(data, new OssDropSource(token), 1, out effect)); return effect; }
        /// <summary>取消或结束拖拽。</summary><param name="escape">Escape状态。</param><param name="keys">鼠标状态。</param><returns>OLE状态。</returns>
        public int QueryContinueDrag(bool escape, uint keys) { return escape || token.IsCancellationRequested ? 0x40101 : (keys & 1) == 0 ? 0x40100 : 0; }
        /// <summary>默认反馈光标。</summary><param name="effect">操作效果。</param><returns>默认光标状态。</returns>
        public int GiveFeedback(uint effect) { return 0x40102; }
        [DllImport("ole32.dll")]
        private static extern int DoDragDrop(ComData data, IOssDropSource source, uint allowed, out uint effect);
    }
}
