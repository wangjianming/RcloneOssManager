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

    public static class ErrorText
    {
        /// <summary>将常见错误归类为带准确类别的中文提示，避免把本地或权限问题一律说成连接失败，也不回显原始敏感上下文。</summary>
        public static string ToChinese(string error)
        {
            var value = (error ?? String.Empty).Trim();
            if (value.Length == 0) return "操作未完成：未返回具体原因，请查看管理器日志。";
            value = value.ToLowerInvariant();
            if (value.Contains("x509") || value.Contains("certificate") || value.Contains("tls")) return "证书校验失败：Endpoint 的 TLS 证书不受信任或已过期，请检查系统时间和证书链。";
            if (value.Contains("signaturedoesnotmatch") || value.Contains("签名不匹配")) return "签名校验失败：请核对 Access Key、Secret Key、Endpoint 和区域设置。";
            if (value.Contains("invalidaccesskeyid") || value.Contains("authentication") || value.Contains("accessdenied") || value.Contains("forbidden") || value.Contains("403")) return "访问被拒绝：账号或密钥无效，或当前账号没有该操作权限。";
            if (value.Contains("no such host") || value.Contains("dns") || value.Contains("name resolution")) return "地址无法解析：请检查 Endpoint 拼写和 DNS。";
            if (value.Contains("timeout") || value.Contains("deadline exceeded") || value.Contains("超时")) return "请求超时：请检查网络质量和 Endpoint 是否可达。";
            if (value.Contains("connection refused") || value.Contains("connect") || value.Contains("network") || value.Contains("网络不可达")) return "网络连接失败：Endpoint 无法访问，请检查网络和端口。";
            if (value.Contains("bucket") && value.Contains("not found")) return "存储桶不存在：目标 bucket 名称有误或当前账号无权访问。";
            if (value.Contains("does not exist") || value.Contains("nosuchkey") || (value.Contains("object") && value.Contains("not found"))) return "目标不存在：文件或目录已被删除或名称有误。";
            if (value.Contains("no space left") || value.Contains("disk full") || value.Contains("not enough space")) return "本地磁盘空间不足：请清理缓存盘后重试。";
            if (value.Contains("access is denied") || value.Contains("permission denied") || value.Contains("being used")) return "本地文件访问被拒绝：请关闭正在占用该文件的程序后重试。";
            if (value.Contains("winfsp") || value.Contains("mount")) return "本地挂载未就绪：请确认 WinFsp 已安装且盘符未被占用。";
            return "操作未完成：请检查 Endpoint、账号、密码、网络和访问权限，详见管理器日志。";
        }

        /// <summary>将本机挂载卸载错误转换为准确中文提示，不混用连接测试文案。</summary>
        public static string ToUnmountChinese(string error)
        {
            var value = (error ?? String.Empty).ToLowerInvariant();
            if (value.Contains("not mounted") || value.Contains("mountpoint is not mounted")) return "本地磁盘未挂载或已经释放。";
            if (value.Contains("access is denied") || value.Contains("access denied") || value.Contains("permission denied")) return "访问被拒绝，请关闭正在使用该盘的程序后重试。";
            if (value.Contains("timeout")) return "卸载操作超时，请关闭正在使用该盘的程序后重试。";
            if (value.Contains("busy") || value.Contains("in use")) return "本地磁盘仍在使用，请关闭正在访问该盘的文件窗口后重试。";
            return "本机挂载服务未能释放盘符，请查看管理器日志和该连接的操作日志。";
        }
    }
}
