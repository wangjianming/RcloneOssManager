using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RcloneOssManager
{
    /// <summary>单文件下载状态；取消只针对该项独立令牌，不影响其他文件。</summary>
    internal sealed class OssDownloadItem : IDisposable
    {
        /// <summary>远端列表项。</summary>
        internal readonly OssEntry Entry;
        /// <summary>此项取消信号，等待和传输中均可取消。</summary>
        internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
        /// <summary>展示的任务状态。</summary>
        internal string State = "等待下载";
        /// <summary>真实进度或错误，不记录认证数据。</summary>
        internal string Detail = "";
        /// <summary>任务是否已经到达终态。</summary>
        internal bool Finished;
        /// <summary>排队、实际开始及结束的本机时间；未开始项目不伪造开始时间。</summary>
        internal readonly DateTime CreatedAt = DateTime.Now;
        internal DateTime? StartedAt, EndedAt;
        /// <summary>本项完整本地目标路径，任务结束后仍供记录和打开目录使用。</summary>
        internal string LocalPath = "";
        /// <summary>原批次的本地根及完整源路径，重试不依赖当前浏览目录。</summary>
        internal string RootFolder = "", SourcePath = "";
        /// <summary>最近一次真实传输统计，结束后速率清零但保留已传输量。</summary>
        internal OssTransferStats Stats;
        /// <summary>只有落盘核验成功才能显示100%，未知总量不伪造百分比。</summary>
        internal double? Percent { get { return State == "已完成" ? 100 : Stats != null && Stats.Total > 0 ? (double?)Math.Min(99.9, Math.Max(0, Stats.Bytes * 100 / Stats.Total)) : null; } }
        /// <summary>进度条及文本列共用同一百分比。</summary>
        internal string PercentText { get { return Percent.HasValue ? Percent.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "%" : "—"; } }
        /// <summary>实际下载速度及其等价Mbps；不是运营商线路速率或网卡总吞吐。</summary>
        internal string SpeedText { get { return Stats == null ? "—" : OssFileClient.Bytes(Stats.Speed) + "/s"; } }
        internal string BandwidthText { get { return Stats == null ? "—" : (Stats.Speed * 8 / 1000000).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " Mbps"; } }
        /// <summary>初始化单项任务。</summary>
        /// <param name="entry">选中的远端文件。</param>
        internal OssDownloadItem(OssEntry entry) { Entry = entry; }
        /// <summary>释放独立取消信号，由队列结束后调用。</summary>
        public void Dispose() { Cancellation.Dispose(); }
    }

    /// <summary>有界并行下载队列；每项独立取消，失败或取消不阻止其他文件。</summary>
    internal sealed class OssDownloadQueue : IDisposable
    {
        /// <summary>本批任务，顺序与用户选择一致。</summary>
        internal readonly List<OssDownloadItem> Items;
        /// <summary>同一时间最多运行的文件传输数。</summary>
        private readonly int parallel;
        /// <summary>建立队列，不访问远端。</summary>
        /// <param name="entries">已验证的远端文件。</param><param name="folder">可选下载目录，开始前固定完整目标路径。</param><param name="maximumParallel">并行文件数，限制为1至4。</param>
        internal OssDownloadQueue(IEnumerable<OssEntry> entries, string folder = null, int maximumParallel = 3) { Items = entries.Select(x => new OssDownloadItem(x) { RootFolder = folder == null ? "" : Path.GetFullPath(folder), LocalPath = folder == null ? "" : OssFileForm.LocalRelativeFile(folder, x.Name) }).ToList(); parallel = Math.Max(1, Math.Min(4, maximumParallel)); }

        /// <summary>取消一项；已结束项目不变，不取消其他下载。</summary>
        /// <param name="item">队列中的目标项。</param>
        internal void Cancel(OssDownloadItem item)
        {
            if (!Items.Contains(item) || item.Finished) return;
            item.Cancellation.Cancel(); item.State = "正在取消";
        }

        /// <summary>逐项下载并刷新界面；全部令牌只取消本批，不触及挂载进程。</summary>
        /// <param name="files">远端客户端。</param><param name="parent">远端当前路径。</param>
        /// <param name="folder">本地下载目录。</param><param name="overwrite">明确确认后的覆盖选项。</param>
        /// <param name="all">整批取消信号。</param><param name="changed">界面状态更新回调。</param><returns>队列执行任务。</returns>
        internal async Task RunAsync(OssFileClient files, string parent, string folder, bool overwrite, CancellationToken all, Action<OssDownloadItem> changed)
        {
            var next = -1;
            Func<int> takeNext = () => Interlocked.Increment(ref next);
            var workers = Enumerable.Range(0, Math.Min(parallel, Items.Count)).Select(_ => RunWorkerAsync(takeNext, files, parent, folder, overwrite, all, changed)).ToArray();
            await Task.WhenAll(workers);
        }

        /// <summary>顺序领取不同任务，但不将界面进度上下文移入后台线程。</summary>
        /// <param name="takeNext">线程安全的队列索引分配器。</param><param name="files">远端客户端。</param><param name="parent">远端父路径。</param><param name="folder">本地目录。</param><param name="overwrite">替换策略。</param><param name="all">批量取消。</param><param name="changed">界面刷新。</param><returns>工作线程任务。</returns>
        private async Task RunWorkerAsync(Func<int> takeNext, OssFileClient files, string parent, string folder, bool overwrite, CancellationToken all, Action<OssDownloadItem> changed)
        {
            while (true)
            {
                var index = takeNext(); if (index >= Items.Count) return;
                await RunItemAsync(Items[index], files, parent, folder, overwrite, all, changed);
            }
        }

        /// <summary>执行单个任务，独立传播取消并刷新线程安全的UI进度回调。</summary>
        /// <param name="item">当前任务。</param><param name="files">现有连接客户端。</param><param name="parent">远端父路径。</param><param name="folder">本地目录。</param><param name="overwrite">替换策略。</param><param name="all">批量取消令牌。</param><param name="changed">界面刷新。</param><returns>异步任务。</returns>
        private static async Task RunItemAsync(OssDownloadItem item, OssFileClient files, string parent, string folder, bool overwrite, CancellationToken all, Action<OssDownloadItem> changed)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(all, item.Cancellation.Token))
            {
                if (!linked.IsCancellationRequested) { item.StartedAt = DateTime.Now; item.State = "下载中"; }
                var isUrl = item.Entry.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || item.Entry.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                item.SourcePath = isUrl ? item.Entry.Path : OssFileClient.Join(parent, item.Entry.Path);
                item.RootFolder = Path.GetFullPath(folder);
                if (item.LocalPath == "") item.LocalPath = OssFileForm.LocalRelativeFile(folder, item.Entry.Name);
                changed(item);
                IProgress<string> progress = new Progress<string>(text => { if (!item.Finished) { item.Detail = text; changed(item); } });
                IProgress<OssTransferStats> statistics = new Progress<OssTransferStats>(stats =>
                {
                    if (item.Finished) return;
                    // rclone发现对象前可能报告零总量，保留已读取的源大小，不从中文文本反推数值。
                    if (stats.Total <= 0 && item.Stats != null) stats.Total = item.Stats.Total;
                    item.Stats = stats; changed(item);
                });
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    await Task.Run(() =>
                    {
                        // 真正写入前再次拒绝目录联接；创建父目录不替换任何已有文件。
                        OssFileForm.LocalRelativeFile(folder, item.Entry.Name);
                        if (item.Entry.IsDir) { Directory.CreateDirectory(item.LocalPath); return; }
                        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath));
                        OssFileForm.LocalRelativeFile(folder, item.Entry.Name);
                        if (isUrl)
                            files.DownloadUrl(item.Entry.Path, item.LocalPath, overwrite, linked.Token, progress.Report, statistics.Report);
                        else files.DownloadFile(item.SourcePath, item.LocalPath, overwrite, linked.Token, progress.Report, statistics.Report);
                    });
                    item.State = "已完成"; item.Detail = item.Entry.IsDir ? "目录已创建（远端未修改）" : "文件大小已核验";
                    if (item.Stats != null && item.Stats.Total > 0) item.Stats.Bytes = item.Stats.Total;
                }
                catch (OperationCanceledException) { item.State = "已取消"; item.Detail = all.IsCancellationRequested ? "整批下载已取消" : "仅此项取消，其他任务继续"; }
                catch (Exception ex) { item.State = "失败"; item.Detail = ex.Message; }
                finally { item.Finished = true; item.EndedAt = DateTime.Now; if (item.Stats != null) item.Stats.Speed = 0; changed(item); }
            }
        }

        /// <summary>统计实际完成、取消和失败项目，不把部分完成宣称全部成功。</summary>
        /// <returns>中文结果。</returns>
        internal string Summary() { return "下载结束：完成 " + Items.Count(x => x.State == "已完成") + "，取消 " + Items.Count(x => x.State == "已取消") + "，失败 " + Items.Count(x => x.State == "失败"); }
        /// <summary>释放所有单项令牌。</summary>
        public void Dispose() { foreach (var item in Items) item.Dispose(); }
    }
}
