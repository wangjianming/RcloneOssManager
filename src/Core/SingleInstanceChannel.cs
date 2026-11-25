using System;
using System.Threading;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>单实例通道：第二次启动不再静默退出，而是把已运行的窗口提到前台。</summary>
    internal static class SingleInstanceChannel
    {
        /// <summary>交互启动使用的互斥体名称。</summary>
        internal const string InteractiveMutex = "RcloneOssManager";

        /// <summary>开机恢复挂载使用的独立互斥体，避免后台进程吞掉用户真正的启动请求。</summary>
        internal const string BackgroundMutex = "RcloneOssManager-Background";

        /// <summary>通知已运行实例置前的命名事件。</summary>
        internal const string ShowEventName = "RcloneOssManager-Show";

        /// <summary>按启动模式选择互斥体，后台恢复与交互启动互不阻塞。</summary>
        /// <param name="background">是否开机恢复挂载模式。</param><returns>互斥体名称。</returns>
        internal static string MutexName(bool background)
        {
            return background ? BackgroundMutex : InteractiveMutex;
        }

        /// <summary>由第二个进程调用：授权前台并唤醒已运行实例；任何失败都不抛出。</summary>
        internal static void RequestShow()
        {
            try { NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            try
            {
                using (var signal = EventWaitHandle.OpenExisting(ShowEventName)) signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.IO.IOException) { }
        }

        /// <summary>为已运行实例启动唤醒线程；事件句柄由调用方持有到进程结束。</summary>
        /// <param name="fallback">没有其他可见窗口时使用的窗口。</param><param name="signal">命名事件。</param>
        internal static void StartWatcher(Form fallback, EventWaitHandle signal)
        {
            if (fallback == null || signal == null) return;
            var thread = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        if (!signal.WaitOne()) continue;
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (AbandonedMutexException) { }
                    try
                    {
                        if (fallback.IsDisposed) return;
                        fallback.BeginInvoke((MethodInvoker)delegate { ActivateExisting(fallback); });
                    }
                    catch (ObjectDisposedException) { return; }
                    catch (InvalidOperationException) { return; }
                }
            });
            thread.IsBackground = true;
            thread.Name = "OSS-ShowWatcher";
            thread.Start();
        }

        /// <summary>把最近使用且可见的窗口提到前台；被困在后台时不抢走对话框的所有权。</summary>
        /// <param name="fallback">兜底窗口。</param><returns>是否找到并激活了窗口。</returns>
        internal static bool ActivateExisting(Form fallback)
        {
            var target = MostRecentForm(fallback);
            if (target == null || target.IsDisposed) return false;
            try
            {
                if (target.WindowState == FormWindowState.Minimized) target.WindowState = FormWindowState.Normal;
                if (!target.Visible) target.Show();
                target.BringToFront();
                var activated = NativeMethods.ActivateWindow(target.Handle);
                if (!activated) target.Activate();
                return true;
            }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        /// <summary>优先返回拥有焦点的窗口，其次是最新打开的可见窗口。</summary>
        /// <param name="fallback">兜底窗口。</param><returns>目标窗口，可能为空。</returns>
        internal static Form MostRecentForm(Form fallback)
        {
            Form newest = null;
            FormCollection forms;
            try { forms = Application.OpenForms; }
            catch (InvalidOperationException) { return fallback; }
            for (var i = forms.Count - 1; i >= 0; i--)
            {
                var form = forms[i];
                if (form == null || form.IsDisposed || !form.IsHandleCreated || !form.Visible) continue;
                if (form.ContainsFocus) return form;
                if (newest == null) newest = form;
            }
            return newest ?? (fallback != null && !fallback.IsDisposed ? fallback : null);
        }
    }
}
