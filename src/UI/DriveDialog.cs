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

    internal static class DriveDialog
    {
        /// <summary>让用户选择挂载盘符及资源管理器中显示的本地磁盘名称。</summary>
        internal static MountRequest Ask(IWin32Window owner, string currentDrive, string defaultVolumeName)
        {
            using (var form = new Form { Text = "挂载设置", Size = new Size(390, 248), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, Font = new Font("Microsoft YaHei UI", 9F) })
            {
                var list = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(124, 24), Size = new Size(220, 28) };
                var occupied = new HashSet<string>(Environment.GetLogicalDrives().Select(x => x.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
                foreach (var letter in Enumerable.Range('D', 23).Select(x => ((char)x).ToString() + ":").Where(x => x.Equals(currentDrive, StringComparison.OrdinalIgnoreCase) || !occupied.Contains(x))) list.Items.Add(letter);
                if (list.Items.Count == 0) { MessageBox.Show(owner, "没有可用盘符。", "挂载", MessageBoxButtons.OK, MessageBoxIcon.Information); return null; }
                list.SelectedItem = list.Items.Cast<object>().FirstOrDefault(x => String.Equals(Convert.ToString(x), currentDrive, StringComparison.OrdinalIgnoreCase)) ?? list.Items[0];
                var volumeName = new TextBox { Location = new Point(124, 67), Size = new Size(220, 28), Text = defaultVolumeName ?? String.Empty, Font = new Font("Microsoft YaHei UI", 10F) };
                volumeName.KeyDown += delegate(object sender, KeyEventArgs args) { if (args.Control && args.KeyCode == Keys.A) { volumeName.SelectAll(); args.SuppressKeyPress = true; } };
                var hint = new Label { Text = "即资源管理器中盘符旁显示的名称；网络盘无法在资源管理器中改名，修改后需重新挂载生效。", Location = new Point(124, 100), Size = new Size(230, 44), ForeColor = Color.DimGray };
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(170, 150), Size = new Size(80, 30) }; var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(260, 150), Size = new Size(80, 30) };
                form.Controls.AddRange(new Control[] { new Label { Text = "挂载盘符", Location = new Point(28, 27), Size = new Size(85, 24) }, new Label { Text = "本地磁盘名称", Location = new Point(28, 71), Size = new Size(95, 24) }, list, volumeName, hint, ok, cancel }); form.AcceptButton = ok; form.CancelButton = cancel;
                return form.ShowDialog(owner) == DialogResult.OK ? new MountRequest { Drive = Convert.ToString(list.SelectedItem), VolumeName = volumeName.Text.Trim() } : null;
            }
        }
    }
}
