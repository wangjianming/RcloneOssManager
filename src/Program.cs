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
    internal static class Program
    {
        private static Mutex singleInstance;
        private static EventWaitHandle showSignal;

        [STAThread]
        private static void Main(string[] argv)
        {
            try { UiStyle.IsDark = String.Equals(RuntimePaths.Current.Theme, "dark", StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { UiStyle.IsDark = false; }
            var background = argv != null && argv.Any(x => String.Equals(x, "--background", StringComparison.OrdinalIgnoreCase));
            bool created;
            try { singleInstance = new Mutex(true, SingleInstanceChannel.MutexName(background), out created); }
            catch (UnauthorizedAccessException) { created = true; }
            if (!created)
            {
                // 已经有一个管理器在运行：把它的窗口提到前台，而不是静默退出。
                if (!background) SingleInstanceChannel.RequestShow();
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs args)
            {
                MessageBox.Show(ErrorText.ToChinese(args.Exception.Message), "OSS 管理器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            if (background)
            {
                try { RcloneClient.RestoreRecordedMounts(); }
                catch (Exception ex) { ManagerLog.Write("开机恢复挂载失败", "", "", ex.Message); }
                return;
            }
            showSignal = CreateShowSignal();
            if (argv != null && argv.Any(x => x == "--settings")) { using (var dialog = new RuntimeSettingsForm()) dialog.ShowDialog(); return; }
            try
            {
                var form = new MainForm(background);
                SingleInstanceChannel.StartWatcher(form, showSignal);
                Application.Run(form);
            }
            catch (Exception ex)
            {
                if (MessageBox.Show(ErrorText.ToChinese(ex.Message) + "\r\n\r\n是否打开路径设置？保存后请重新启动程序。", "OSS 管理器", MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
                    using (var dialog = new RuntimeSettingsForm()) dialog.ShowDialog();
            }
        }

        /// <summary>创建唤醒信标；被占用或权限不足时返回空值，管理器仍可正常单开使用。</summary>
        /// <returns>命名事件或空值。</returns>
        private static EventWaitHandle CreateShowSignal()
        {
            try { return new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceChannel.ShowEventName); }
            catch (UnauthorizedAccessException) { return null; }
            catch (IOException) { return null; }
        }
    }
}
