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

    internal sealed class AboutForm : Form
    {
        private readonly MainForm owner;
        private readonly Label versions = new Label();
        private readonly Button upgrade = MainForm.ButtonOf("检查并升级", UiStyle.Accent, UiStyle.OnAccent);
        internal AboutForm(MainForm value)
        {
            owner = value; Text = "关于 OSS 连接管理器"; Icon = RuntimePaths.LoadIcon();
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(640, 350); MinimumSize = Size;
            MaximizeBox = false; MinimizeBox = false; KeyPreview = true; AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 10F); BackColor = UiStyle.Surface; ForeColor = UiStyle.Text;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 20), ColumnCount = 1, RowCount = 6 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.Controls.Add(new Label { Text = "OSS 连接管理器", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 17F, FontStyle.Bold), ForeColor = UiStyle.AccentText }, 0, 0);
            versions.Text = "rclone 当前版本：读取中…\r\n官方最新版本：读取中…"; versions.Dock = DockStyle.Fill; layout.Controls.Add(versions, 0, 1);
            layout.Controls.Add(PathPanel("rclone 安装位置", RcloneClient.RclonePath), 0, 2);
            layout.Controls.Add(PathPanel("配置文件（路径可选中复制）", RcloneClient.ConfigPath), 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            upgrade.Text = "检查更新中…"; upgrade.Enabled = false; upgrade.Size = new Size(155, 36);
            upgrade.Click += delegate { Close(); owner.UpgradeRclone(); }; buttons.Controls.Add(upgrade); layout.Controls.Add(buttons, 0, 5);
            Controls.Add(layout);
            KeyDown += delegate(object sender, KeyEventArgs args) { if (args.KeyCode == Keys.Escape) Close(); };
            Shown += async delegate
            {
                string current;
                try { current = await Task.Run(() => owner.GetCurrentVersion()); } catch { current = "未知"; }
                if (IsDisposed) return;
                versions.Text = "rclone 当前版本：" + current + "\r\n官方最新版本：读取中…";
                try
                {
                    var latest = await Task.Run(() => RcloneClient.GetLatestVersion());
                    if (IsDisposed) return;
                    versions.Text = "rclone 当前版本：" + current + "\r\n官方最新版本：" + latest;
                    upgrade.Enabled = RcloneClient.IsNewerVersion(latest, current);
                    upgrade.Text = upgrade.Enabled ? "升级到 " + latest : "当前已是最新版";
                }
                catch
                {
                    if (IsDisposed) return;
                    versions.Text = "rclone 当前版本：" + current + "\r\n官方最新版本：暂时无法获取，请检查网络。";
                    upgrade.Text = "重试检查更新"; upgrade.Enabled = true;
                }
            };
        }
        /// <summary>将路径与说明分行布局，路径支持选择复制和横向滚动。</summary>
        /// <param name="caption">路径说明。</param><param name="path">本机路径。</param><returns>路径面板。</returns>
        private static Control PathPanel(string caption, string path)
        {
            var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 6) };
            panel.Controls.Add(new TextBox { Text = path, ReadOnly = true, Dock = DockStyle.Bottom, BorderStyle = BorderStyle.FixedSingle, BackColor = UiStyle.Card, Height = 28 });
            panel.Controls.Add(new Label { Text = caption, Dock = DockStyle.Top, Height = 23, ForeColor = UiStyle.TextMuted });
            return panel;
        }
    }
}
