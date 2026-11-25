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

    /// <summary>独立保存界面排序，不改写 rclone 连接配置。</summary>
    internal static class ConnectionOrder
    {
        internal static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RcloneOssManager", "connection-order.txt");
        /// <summary>按保存的顺序排列连接，新连接保留原有相对顺序。</summary>
        /// <param name="names">当前连接名称。</param><param name="order">已保存的顺序。</param>
        /// <returns>去重并按用户顺序排列的连接。</returns>
        internal static List<string> Apply(IEnumerable<string> names, IEnumerable<string> order)
        {
            var remaining = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var result = new List<string>();
            foreach (var name in order)
            {
                var index = remaining.FindIndex(x => String.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                if (index < 0) continue;
                result.Add(remaining[index]); remaining.RemoveAt(index);
            }
            result.AddRange(remaining); return result;
        }
        /// <summary>读取本机排序；缺失或不可读时使用配置顺序。</summary>
        /// <param name="path">排序文件路径，可空。</param><returns>保存的名称顺序。</returns>
        internal static List<string> Read(string path = null)
        {
            var source = path ?? FilePath;
            try { return File.Exists(source) ? File.ReadAllLines(source, Encoding.UTF8).ToList() : new List<string>(); }
            catch (IOException) { return new List<string>(); } catch (UnauthorizedAccessException) { return new List<string>(); }
        }
        /// <summary>原子保存当前列表顺序。</summary><param name="names">当前列表名称快照。</param><param name="path">排序文件路径，可空。</param>
        internal static void Save(IEnumerable<string> names, string path = null)
        {
            var target = path ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var staged = target + ".new";
            try
            {
                File.WriteAllLines(staged, names, new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(staged, target, null); else File.Move(staged, target);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }

        /// <summary>同步修改排序文件中的连接名称。</summary>
        /// <param name="oldName">旧名称。</param><param name="newName">新名称。</param>
        internal static void Rename(string oldName, string newName, string path = null)
        {
            Save(Read(path).Select(x => String.Equals(x, oldName, StringComparison.OrdinalIgnoreCase) ? newName : x), path);
        }
    }
}
