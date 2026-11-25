using System;
using System.IO;
using System.IO.Compression;

namespace RcloneOssManager
{
    /// <summary>把路径设置、rclone 配置与挂载记录打包导入导出，便于换机迁移；不改变远端数据。</summary>
    internal static class ConfigurationTransfer
    {
        internal const string SettingsEntry = "RcloneOssManager.settings.json";
        internal const string RcloneConfEntry = "rclone.conf";
        internal const string MountsEntry = "rclone-mounts.txt";

        /// <summary>导出当前设置、rclone 配置和挂载记录到一个 zip 文件。</summary>
        /// <param name="targetPath">目标 zip 路径。</param><returns>面向用户的结果说明。</returns>
        internal static string Export(string targetPath)
        {
            if (String.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("请选择导出文件。");
            var paths = RuntimePaths.Current;
            var mounts = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rclone-mounts.txt");
            var stage = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var count = 0;
            try
            {
                using (var stream = File.Create(stage))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    if (Add(zip, SettingsEntry, paths.SettingsFile)) count++;
                    if (Add(zip, RcloneConfEntry, paths.RcloneConfiguration)) count++;
                    if (Add(zip, MountsEntry, mounts)) count++;
                }
                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(stage, targetPath);
                return "已导出 " + count + " 个文件。rclone.conf 含连接认证信息，请妥善保管该配置包。";
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }

        /// <summary>从 zip 导入设置、rclone 配置和挂载记录，覆盖前把原文件备份为 .bak。</summary>
        /// <param name="sourcePath">来源 zip 路径。</param><returns>面向用户的结果说明。</returns>
        internal static string Import(string sourcePath)
        {
            if (String.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) throw new ArgumentException("找不到要导入的配置包。");
            var paths = RuntimePaths.Current;
            var restored = 0;
            using (var zip = ZipFile.OpenRead(sourcePath))
            {
                if (Extract(zip, SettingsEntry, paths.SettingsFile)) restored++;
                if (Extract(zip, RcloneConfEntry, paths.RcloneConfiguration)) restored++;
                if (Extract(zip, MountsEntry, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rclone-mounts.txt"))) restored++;
            }
            if (restored == 0) throw new InvalidDataException("配置包里没有可识别的设置、rclone 配置或挂载记录。");
            return "已导入 " + restored + " 个文件，原文件已备份为 .bak。请关闭后重新打开程序。";
        }

        private static bool Add(ZipArchive zip, string entryName, string source)
        {
            if (String.IsNullOrWhiteSpace(source) || !File.Exists(source)) return false;
            zip.CreateEntryFromFile(source, entryName, CompressionLevel.Optimal);
            return true;
        }

        private static bool Extract(ZipArchive zip, string entryName, string target)
        {
            var entry = zip.GetEntry(entryName);
            if (entry == null) return false;
            var folder = Path.GetDirectoryName(target);
            if (!String.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);
            if (File.Exists(target))
            {
                var backup = target + ".bak";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(target, backup);
            }
            entry.ExtractToFile(target, true);
            return true;
        }
    }
}
