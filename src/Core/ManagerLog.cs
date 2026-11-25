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

    internal static class ManagerLog
    {
        private static readonly object SyncRoot = new object();
        private static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rclone-oss-manager.log");

        /// <summary>记录不含任何凭据的本机操作结果，供故障排查使用。</summary>
        internal static void Write(string action, string remote, string drive, string result)
        {
            var detail = result == "success" ? "成功" : ErrorText.ToChinese(result);
            try { lock (SyncRoot) File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + action + " | " + (remote ?? String.Empty) + " | " + (drive ?? String.Empty) + " | " + detail + Environment.NewLine, new UTF8Encoding(false)); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
