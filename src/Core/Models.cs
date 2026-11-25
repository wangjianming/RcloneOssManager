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

    internal sealed class RemoteDefinition
    {
        /// <summary>稳定的内部标识；不随用户修改显示名称而变化。</summary>
        internal string Name;
        /// <summary>界面友好名称，通过 rclone description 保存。</summary>
        internal string DisplayName;
        internal string Type, Endpoint, Account, Secret, Region;
    }

    internal sealed class MountRecord
    {
        internal string Drive;
        internal string VolumeName;
    }

    /// <summary>当前 rclone 挂载的盘符及其远端子目录，不保存认证信息。</summary>
    internal sealed class MountedLocation
    {
        /// <summary>已挂载盘符。</summary>
        internal string Drive;
        /// <summary>远端挂载根路径，空值表示连接根目录。</summary>
        internal string Root;
    }

    internal sealed class MountRequest
    {
        internal string Drive;
        internal string VolumeName;
    }
}
