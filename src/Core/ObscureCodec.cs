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

    internal static class ObscureCodec
    {
        private static readonly byte[] Key = { 0x9c, 0x93, 0x5b, 0x48, 0x73, 0x0a, 0x55, 0x4d, 0x6b, 0xfd, 0x7c, 0x63, 0xc8, 0x86, 0xa9, 0x2b, 0xd3, 0x90, 0x19, 0x8e, 0xb8, 0x12, 0x8a, 0xfb, 0xf4, 0xde, 0x16, 0x2b, 0x8b, 0x95, 0xf6, 0x38 };

        /// <summary>解开 rclone 的 obscured 保存值；非混淆值按明文返回以兼容旧配置。</summary>
        internal static string Reveal(string value)
        {
            if (String.IsNullOrEmpty(value) || !value.StartsWith("obscured:", StringComparison.OrdinalIgnoreCase)) return value ?? String.Empty;
            var input = value.Substring("obscured:".Length).Replace('-', '+').Replace('_', '/');
            input = input.PadRight(input.Length + (4 - input.Length % 4) % 4, '=');
            var cipherText = Convert.FromBase64String(input);
            if (cipherText.Length < 16) throw new InvalidOperationException("保存的密码格式无效。");
            var counter = new byte[16]; Array.Copy(cipherText, counter, 16);
            var plainText = new byte[cipherText.Length - 16];
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None; aes.Key = Key;
                using (var encryptor = aes.CreateEncryptor())
                {
                    for (var offset = 0; offset < plainText.Length; offset += 16)
                    {
                        var stream = new byte[16]; encryptor.TransformBlock(counter, 0, counter.Length, stream, 0);
                        var length = Math.Min(16, plainText.Length - offset);
                        for (var index = 0; index < length; index++) plainText[offset + index] = (byte)(cipherText[16 + offset + index] ^ stream[index]);
                        for (var index = counter.Length - 1; index >= 0 && ++counter[index] == 0; index--) { }
                    }
                }
            }
            return Encoding.UTF8.GetString(plainText);
        }
    }
}
