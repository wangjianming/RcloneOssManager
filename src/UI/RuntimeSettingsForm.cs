using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>独立配置 rclone、连接配置与文件管理器路径，支持配置包导入导出。</summary>
    internal sealed class RuntimeSettingsForm : Form
    {
        private readonly TextBox executable = new HintTextBox();
        private readonly TextBox configuration = new HintTextBox();
        private readonly TextBox fileManager = new HintTextBox();
        private readonly Label result = new Label { Dock = DockStyle.Fill, ForeColor = Color.DimGray };

        /// <summary>构建路径与配置界面，不读取或显示 rclone 认证内容。</summary>
        internal RuntimeSettingsForm()
        {
            Text = "路径与配置"; ClientSize = new Size(760, 500); MinimumSize = new Size(650, 500); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 10F); BackColor = Color.FromArgb(245, 247, 251); Icon = RuntimePaths.LoadIcon(); MinimizeBox = false; MaximizeBox = false;
            executable.Text = RuntimePaths.Current.RcloneExecutable; configuration.Text = RuntimePaths.Current.RcloneConfiguration; fileManager.Text = RuntimePaths.Current.FileManagerExecutable;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 8, ColumnCount = 1 };
            foreach (var height in new[] { 36, 56, 56, 56, 66, 44, 42 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = "程序、配置与文件管理器分离，换电脑不需要相同盘符", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(30, 76, 125) }, 0, 0);
            layout.Controls.Add(PathRow("rclone.exe", executable, "rclone.exe|rclone.exe|可执行文件|*.exe"), 0, 1);
            layout.Controls.Add(PathRow("rclone 配置", configuration, "rclone配置|*.conf|所有文件|*.*"), 0, 2);
            layout.Controls.Add(PathRow("文件管理器（可选）", fileManager, "文件管理器|*.exe|所有文件|*.*"), 0, 3);
            layout.Controls.Add(new Label { Text = "单文件发行版已内嵌 rclone。文件管理器留空时自动探测 Pulse，其次 Directory Opus，也可直接指定 dopus.exe。\r\n路径保存后重启程序生效；配置包包含 rclone.conf 认证信息，请妥善保管。", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 4);
            var transfer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
            var exportButton = MainForm.ButtonOf("导出配置包…", Color.FromArgb(235, 242, 250), Color.FromArgb(36, 88, 138)); exportButton.Size = new Size(112, 32);
            var importButton = MainForm.ButtonOf("导入配置包…", Color.FromArgb(235, 242, 250), Color.FromArgb(36, 88, 138)); importButton.Size = new Size(112, 32);
            exportButton.Click += delegate
            {
                using (var dialog = new SaveFileDialog { Filter = "配置包|*.zip", FileName = "RcloneOssManager-config.zip" })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    try { result.ForeColor = Color.FromArgb(32, 125, 75); result.Text = ConfigurationTransfer.Export(dialog.FileName); }
                    catch (Exception ex) { result.ForeColor = Color.Firebrick; result.Text = ex.Message; }
                }
            };
            importButton.Click += delegate
            {
                using (var dialog = new OpenFileDialog { Filter = "配置包|*.zip", CheckFileExists = true })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    if (MessageBox.Show(this, "导入会覆盖当前设置、rclone 配置和挂载记录，原文件会备份为 .bak。确定继续？", "导入配置包", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
                    try { result.ForeColor = Color.FromArgb(32, 125, 75); result.Text = ConfigurationTransfer.Import(dialog.FileName); }
                    catch (Exception ex) { result.ForeColor = Color.Firebrick; result.Text = ex.Message; }
                }
            };
            transfer.Controls.Add(exportButton); transfer.Controls.Add(importButton); layout.Controls.Add(transfer, 0, 5);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = MainForm.ButtonOf("关闭", Color.FromArgb(235, 242, 250), Color.FromArgb(36, 88, 138)); cancel.Size = new Size(96, 32); cancel.DialogResult = DialogResult.Cancel;
            var save = MainForm.ButtonOf("保存路径", Color.FromArgb(36, 104, 178), Color.White); save.Size = new Size(96, 32);
            save.Click += delegate { try { RuntimePaths.Current.Save(executable.Text.Trim(), configuration.Text.Trim(), fileManager.Text.Trim()); result.ForeColor = Color.FromArgb(32, 125, 75); result.Text = "已保存并读回核验，请关闭后重新打开程序。既有挂载未改变。"; } catch (Exception ex) { result.ForeColor = Color.Firebrick; result.Text = ex.Message; } };
            buttons.Controls.Add(cancel); buttons.Controls.Add(save); layout.Controls.Add(buttons, 0, 6); layout.Controls.Add(result, 0, 7); Controls.Add(layout); CancelButton = cancel;
        }

        /// <summary>构建等高的路径输入与文件选择按钮。</summary>
        /// <param name="caption">字段名。</param><param name="input">路径输入框。</param><param name="filter">文件筛选。</param><returns>路径行。</returns>
        private Control PathRow(string caption, TextBox input, string filter)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 4, 0, 4) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            row.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0); input.Anchor = AnchorStyles.Left | AnchorStyles.Right; input.Height = 32; row.Controls.Add(input, 1, 0);
            var choose = MainForm.ButtonOf("浏览…", Color.FromArgb(235, 242, 250), Color.FromArgb(36, 88, 138)); choose.Size = new Size(76, 32); choose.Anchor = AnchorStyles.Left;
            choose.Click += delegate { using (var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, FileName = input.Text }) if (dialog.ShowDialog(this) == DialogResult.OK) input.Text = dialog.FileName; }; row.Controls.Add(choose, 2, 0); return row;
        }
    }
}
