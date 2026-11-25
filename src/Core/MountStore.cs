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

    internal static class MountStore
    {
        private static readonly object SyncRoot = new object();
        internal static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rclone-mounts.txt");

        /// <summary>将一个受校验的本机挂载记录转换为稳定的磁盘恢复格式。</summary>
        internal static string Serialize(string name, string drive) { return Serialize(name, drive, name); }

        /// <summary>将连接、盘符与本地磁盘名称转换为自启动记录格式。</summary>
        internal static string Serialize(string name, string drive, string volumeName) { return name + "|" + drive + "|" + NormalizeVolumeName(name, volumeName); }

        /// <summary>读取本机挂载记录，忽略格式不合法的行以保证自启动不中断。</summary>
        internal static Dictionary<string, MountRecord> Read()
        {
            lock (SyncRoot)
            {
            var map = new Dictionary<string, MountRecord>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(FilePath)) return map;
            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                var parts = line.Split('|');
                if ((parts.Length == 2 || parts.Length == 3) && IsValid(parts[0], parts[1], parts.Length == 3 ? parts[2] : parts[0])) map[parts[0]] = new MountRecord { Drive = parts[1].ToUpperInvariant(), VolumeName = NormalizeVolumeName(parts[0], parts.Length == 3 ? parts[2] : parts[0]) };
            }
            return map;
            }
        }

        /// <summary>新增或更新一个连接的自动恢复盘符。</summary>
        internal static void Set(string name, string drive) { Set(name, drive, name); }

        /// <summary>根据运行中的 rclone 进程同步盘符，同时保留用户设置的本地磁盘名称。</summary>
        internal static void SyncDrive(string name, string drive)
        {
            if (String.IsNullOrWhiteSpace(name) || String.IsNullOrWhiteSpace(drive)) return;
            lock (SyncRoot) { var map = Read(); MountRecord record; map.TryGetValue(name, out record); map[name] = new MountRecord { Drive = drive.ToUpperInvariant(), VolumeName = record == null ? name : record.VolumeName }; Write(map); }
        }

        /// <summary>新增或更新一个连接的自动恢复盘符及本地磁盘名称。</summary>
        internal static void Set(string name, string drive, string volumeName)
        {
            if (!IsValid(name, drive, volumeName)) return;
            lock (SyncRoot) { var map = Read(); map[name] = new MountRecord { Drive = drive.ToUpperInvariant(), VolumeName = NormalizeVolumeName(name, volumeName) }; Write(map); }
        }

        /// <summary>删除连接对应的自动恢复记录。</summary>
        internal static void Remove(string name)
        {
            lock (SyncRoot) { var map = Read(); if (map.Remove(name)) Write(map); }
        }

        /// <summary>同步修改本机挂载记录中的连接名称。</summary>
        /// <param name="oldName">旧名称。</param><param name="newName">新名称。</param>
        internal static void Rename(string oldName, string newName)
        {
            lock (SyncRoot)
            {
                var map = Read();
                MountRecord record;
                if (!map.TryGetValue(oldName, out record)) return;
                map.Remove(oldName); map[newName] = record; Write(map);
            }
        }

        /// <summary>卸载盘符后删除与该盘符关联的自动恢复记录。</summary>
        internal static void RemoveByDrive(string drive)
        {
            lock (SyncRoot)
            {
                var map = Read();
                foreach (var name in map.Where(x => x.Value.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToList()) map.Remove(name);
                Write(map);
            }
        }

        /// <summary>将空名称回退为连接名，并拒绝记录分隔符与控制字符。</summary>
        internal static string NormalizeVolumeName(string name, string volumeName) { var value = String.IsNullOrWhiteSpace(volumeName) ? name : volumeName.Trim(); return value.Replace("|", String.Empty).Replace("\r", String.Empty).Replace("\n", String.Empty); }
        private static bool IsValid(string name, string drive, string volumeName) { return !String.IsNullOrWhiteSpace(name) && System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_-]*$") && !String.IsNullOrWhiteSpace(drive) && System.Text.RegularExpressions.Regex.IsMatch(drive, "(?i)^[A-Z]:$") && !String.IsNullOrWhiteSpace(NormalizeVolumeName(name, volumeName)); }
        private static void Write(Dictionary<string, MountRecord> map) { File.WriteAllLines(FilePath, map.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => Serialize(x.Key, x.Value.Drive, x.Value.VolumeName)), new UTF8Encoding(false)); }
    }
}
