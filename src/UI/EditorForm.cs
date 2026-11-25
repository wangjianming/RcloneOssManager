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

    internal sealed class EditorForm : Form
    {
        private readonly RcloneClient client; private bool exists; private string originalName;
        /// <summary>稳定的配置标识，编辑显示名称时不改变运行中挂载引用。</summary>
        private readonly string connectionName;
        /// <summary>回填时的连接参数，用于识别仅修改名称而不重写认证的操作。</summary>
        private RemoteDefinition loadedDefinition;
        private readonly TextBox name = Box(); private readonly ComboBox type = new ComboBox(); private readonly TextBox endpoint = Box(); private readonly TextBox account = Box(); private readonly TextBox secret = Box(); private readonly TextBox region = Box(); private readonly ComboBox drive = new ComboBox(); private readonly TextBox volumeName = Box();
        private readonly Button testButton = new Button { Text = "测试" }, saveButton = new Button { Text = "保存" }, mountButton = new Button { Text = "挂载" }, cancelButton = new Button { Text = "取消" };
        private readonly Label endpointHint = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(400, 0) };
        private readonly Label resultLabel = new Label { Dock = DockStyle.Fill, ForeColor = Color.DimGray, AutoEllipsis = true };
        private readonly Dictionary<string, string> mounts;
        private readonly Dictionary<string, MountRecord> records;
        private bool busy, testing, closeAfterTest;
        private CancellationTokenSource testCancellation;
        /// <summary>构建新增或编辑页面；编辑时以明文回填本机已保存的密码。</summary>
        /// <param name="value">连接客户端。</param><param name="remote">编辑的连接名；新增时为空。</param>
        /// <param name="activeMounts">挂载状态快照。</param><param name="savedMounts">本机名称记录。</param>
        internal EditorForm(RcloneClient value, string remote, Dictionary<string, string> activeMounts = null, Dictionary<string, MountRecord> savedMounts = null)
        {
            client = value; exists = !String.IsNullOrWhiteSpace(remote); originalName = remote;
            connectionName = exists ? remote : RcloneClient.NewRemoteName();
            mounts = activeMounts ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            records = savedMounts ?? new Dictionary<string, MountRecord>(StringComparer.OrdinalIgnoreCase);
            Text = exists ? "编辑 OSS 连接" : "新增 OSS 连接"; Icon = RuntimePaths.LoadIcon(); StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(660, 640); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            BackColor = Color.FromArgb(247, 249, 252); Font = new Font("Microsoft YaHei UI", 10F); KeyPreview = true; AutoScaleMode = AutoScaleMode.Dpi;
            type.DropDownStyle = ComboBoxStyle.DropDownList; type.Items.AddRange(new object[] { "MinIO / S3 兼容", "阿里云 OSS", "其他 S3 兼容", "WebDAV", "SFTP", "FTP" }); type.SelectedIndex = 0;
            var occupied = new HashSet<string>(Environment.GetLogicalDrives().Select(x => x.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
            drive.DropDownStyle = ComboBoxStyle.DropDownList; drive.Items.Add("不挂载"); foreach (var letter in Enumerable.Range('D', 23).Select(x => ((char)x).ToString() + ":").Where(x => !occupied.Contains(x))) drive.Items.Add(letter); drive.SelectedIndex = 0;
            secret.UseSystemPasswordChar = false; AddCtrlA(name); AddCtrlA(endpoint); AddCtrlA(account); AddCtrlA(secret); AddCtrlA(region);
            AddCtrlA(volumeName);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 11 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var captions = new[] { "连接名称", "连接类型", "Endpoint / 主机", "账号 / Access Key", "密码 / Secret Key", "Region（可留空）", "挂载盘符", "本地磁盘名称" }; var inputs = new Control[] { name, type, endpoint, account, secret, region, drive, volumeName };
            for (var i = 0; i < captions.Length; i++)
            {
                var row = i > 2 ? i + 1 : i;
                layout.Controls.Add(new Label { Text = captions[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
                inputs[i].Dock = DockStyle.Top; inputs[i].Margin = new Padding(3, 9, 3, 3); layout.Controls.Add(inputs[i], 1, row);
            }
            for (var row = 0; row < 9; row++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, row == 0 ? 70 : row == 7 ? 78 : 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.Controls.Remove(name);
            var namePanel = new Panel { Dock = DockStyle.Fill };
            name.Dock = DockStyle.Top; name.Margin = new Padding(0);
            namePanel.Controls.Add(new Label { Text = "支持中文、数字、空格和符号；修改名称不影响挂载。", Dock = DockStyle.Bottom, Height = 30, ForeColor = Color.DimGray }); namePanel.Controls.Add(name); layout.Controls.Add(namePanel, 1, 0);
            layout.Controls.Add(endpointHint, 1, 3);
            layout.Controls.Remove(volumeName);
            var volumePanel = new Panel { Dock = DockStyle.Fill };
            volumeName.Dock = DockStyle.Top; volumeName.Margin = new Padding(0);
            volumePanel.Controls.Add(new Label { Text = "资源管理器中盘符旁显示的名称；网络盘只能在挂载时设置，改名需重新挂载生效。", Dock = DockStyle.Bottom, Height = 30, ForeColor = Color.DimGray }); volumePanel.Controls.Add(volumeName); layout.Controls.Add(volumePanel, 1, 7);
            resultLabel.Text = "测试只读访问，不保存输入；密码按当前设置明文展示。";
            layout.Controls.Add(resultLabel, 0, 9); layout.SetColumnSpan(resultLabel, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            foreach (var button in new[] { cancelButton, mountButton, saveButton, testButton }) { button.Size = new Size(94, 36); buttons.Controls.Add(button); }
            layout.Controls.Add(buttons, 0, 10); layout.SetColumnSpan(buttons, 2); Controls.Add(layout);
            type.SelectedIndexChanged += delegate { endpointHint.Text = EndpointExample(Convert.ToString(type.SelectedItem)); };
            endpointHint.Text = EndpointExample(Convert.ToString(type.SelectedItem));
            testButton.Click += delegate { ExecuteTest(); }; saveButton.Click += delegate { SaveOnly(); }; mountButton.Click += delegate { SaveAndMount(); };
            cancelButton.Click += delegate { if (testing && testCancellation != null) testCancellation.Cancel(); else Close(); };
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy && !testing) { e.Cancel = true; resultLabel.Text = "正在保存或挂载，请等待完成后关闭。"; }
                else if (testCancellation != null)
                {
                    e.Cancel = true; closeAfterTest = true;
                    resultLabel.Text = "正在取消测试并清理临时配置，完成后自动关闭…";
                    testCancellation.Cancel();
                }
            };
            if (exists) { LoadRemote(remote); loadedDefinition = Definition(); }
        }
        /// <summary>从本机配置回填编辑字段；按用户设置明文显示密码，不访问网络磁盘。</summary>
        private void LoadRemote(string remote)
        {
            var data = client.GetMetadata(remote); name.Text = client.GetDisplayNames()[remote]; name.ReadOnly = false; string current; if (data.TryGetValue("type", out current)) type.SelectedItem = current == "webdav" ? "WebDAV" : current == "sftp" ? "SFTP" : current == "ftp" ? "FTP" : data.ContainsKey("provider") && data["provider"].Equals("Alibaba", StringComparison.OrdinalIgnoreCase) ? "阿里云 OSS" : data.ContainsKey("provider") && data["provider"].Equals("Other", StringComparison.OrdinalIgnoreCase) ? "其他 S3 兼容" : "MinIO / S3 兼容"; endpoint.Text = data.ContainsKey("url") ? data["url"] : data.ContainsKey("host") ? data["host"] : data.ContainsKey("endpoint") ? data["endpoint"] : String.Empty; account.Text = data.ContainsKey("user") ? data["user"] : data.ContainsKey("access_key_id") ? data["access_key_id"] : String.Empty; secret.Text = ObscureCodec.Reveal(data.ContainsKey("secret_access_key") ? data["secret_access_key"] : data.ContainsKey("pass") ? data["pass"] : String.Empty); region.Text = data.ContainsKey("region") ? data["region"] : String.Empty;
            string mountedDrive;
            if (mounts.TryGetValue(remote, out mountedDrive))
            {
                if (drive.Items.Cast<object>().All(x => !String.Equals(Convert.ToString(x), mountedDrive, StringComparison.OrdinalIgnoreCase))) drive.Items.Add(mountedDrive);
                drive.SelectedItem = mountedDrive;
            }
            MountRecord record; volumeName.Text = records.TryGetValue(remote, out record) ? record.VolumeName : remote;
        }
        private RemoteDefinition Definition() { return new RemoteDefinition { Name = connectionName, DisplayName = name.Text.Trim(), Type = Convert.ToString(type.SelectedItem), Endpoint = endpoint.Text.Trim(), Account = account.Text.Trim(), Secret = secret.Text, Region = region.Text.Trim() }; }
        /// <summary>识别只有显示名称变化的保存，避免改写密码格式或要求填写额外认证字段。</summary>
        /// <param name="value">当前界面快照。</param><returns>连接参数是否未改变。</returns>
        private bool SameSettings(RemoteDefinition value)
        {
            return loadedDefinition != null && value.Type == loadedDefinition.Type && value.Endpoint == loadedDefinition.Endpoint && value.Account == loadedDefinition.Account && value.Secret == loadedDefinition.Secret && value.Region == loadedDefinition.Region;
        }
        /// <summary>执行编辑页测试，并将所有异常转换为窗口提示。</summary>
        private async void ExecuteTest()
        {
            if (busy) return;
            var definition = Definition(); SetBusy(true, true); testCancellation = new CancellationTokenSource();
            var cancellation = testCancellation;
            resultLabel.Text = "正在测试连接…可取消，测试不会保存输入。"; resultLabel.ForeColor = Color.DimGray;
            try { await Task.Run(() => client.TestDefinition(definition, exists, cancellation.Token, originalName)); if (!IsDisposed) { resultLabel.Text = "连接测试成功。"; resultLabel.ForeColor = Color.DarkGreen; } }
            catch (OperationCanceledException) { if (!IsDisposed) resultLabel.Text = "测试已取消，配置未改动。"; }
            catch (Exception ex) { if (!IsDisposed) { resultLabel.Text = ex.Message; resultLabel.ForeColor = Color.Firebrick; } ManagerLog.Write("编辑页测试失败", definition.Name, "", ex.Message); }
            finally
            {
                cancellation.Dispose(); testCancellation = null;
                if (!IsDisposed) { SetBusy(false, false); if (closeAfterTest) { closeAfterTest = false; Close(); } }
            }
        }
        /// <summary>后台保存输入，失败保留编辑内容。</summary>
        private async void SaveOnly() { await SaveAsync(false); }
        /// <summary>后台保存并挂载，避免阻塞窗口消息循环。</summary>
        private async void SaveAndMount() { await SaveAsync(true); }
        /// <summary>保存或挂载的统一后台入口。</summary>
        /// <param name="mount">是否同时挂载。</param><returns>操作任务。</returns>
        private async Task SaveAsync(bool mount)
        {
            if (busy) return;
            var definition = Definition(); var selected = Convert.ToString(drive.SelectedItem); var label = volumeName.Text.Trim();
            if (mount && selected == "不挂载") { resultLabel.Text = "请先选择要挂载的本地盘符。"; return; }
            SetBusy(true, false); resultLabel.Text = mount ? "正在保存并挂载…" : "正在保存…";
            var saved = false;
            var labelChanged = false;
            try
            {
                await Task.Run(() =>
                {
                    lock (RcloneClient.MountSync)
                    {
                        if (exists && !mount && SameSettings(definition)) client.UpdateDisplayName(originalName, definition.DisplayName);
                        else if (exists) client.UpdateExisting(originalName, definition);
                        else client.Save(definition, false);
                        saved = true;
                        if (!mount && exists)
                        {
                            // 仅保存时同步磁盘名称变更；运行中的挂载继续显示旧名称，重新挂载后生效。
                            var normalized = MountStore.NormalizeVolumeName(definition.Name, label);
                            MountRecord record;
                            if (MountStore.Read().TryGetValue(definition.Name, out record) && record.VolumeName != normalized)
                            {
                                MountStore.Set(definition.Name, record.Drive, normalized);
                                string active; labelChanged = MainForm.GetMounts().TryGetValue(definition.Name, out active);
                            }
                        }
                        if (mount) { string current; MainForm.GetMounts().TryGetValue(definition.Name, out current); client.ChangeMount(definition.Name, current, selected, label); }
                    }
                });
                SetBusy(false, false);
                if (labelChanged)
                {
                    resultLabel.Text = "已保存。磁盘名称会在重新挂载后生效；当前挂载中的盘仍显示旧名称，可卸载后重新挂载。";
                    resultLabel.ForeColor = UiStyle.InfoText;
                    return;
                }
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { resultLabel.Text = ex.Message; resultLabel.ForeColor = Color.Firebrick; ManagerLog.Write("编辑页保存失败", definition.Name, "", ex.Message); }
            finally
            {
                if (!IsDisposed)
                {
                    // 保存成功即转入编辑态，即使后续挂载失败也允许修改并重试。
                    if (saved) { exists = true; originalName = definition.Name; loadedDefinition = definition; name.ReadOnly = false; Text = "编辑 OSS 连接"; }
                    SetBusy(false, false);
                }
            }
        }
        /// <summary>切换本编辑页的操作状态，不影响主列表其他连接。</summary>
        /// <param name="value">是否正在操作。</param><param name="isTest">是否允许取消的测试。</param>
        private void SetBusy(bool value, bool isTest)
        {
            busy = value; testing = value && isTest;
            testButton.Enabled = saveButton.Enabled = mountButton.Enabled = !value;
            foreach (var input in new Control[] { name, type, endpoint, account, secret, region, drive, volumeName }) input.Enabled = !value;
            cancelButton.Text = testing ? "取消测试" : "取消"; cancelButton.Enabled = !value || testing;
        }
        /// <summary>提供各协议可直接参考的主机格式，不将提示写入实际输入。</summary>
        /// <param name="type">连接类型。</param><returns>中文输入示例。</returns>
        internal static string EndpointExample(string type)
        {
            if (type == "SFTP" || type == "FTP") return "例如：192.168.1.10 或 files.example.com\n只填主机，不带协议和路径；使用默认端口。";
            if (type == "WebDAV") return "例如：https://files.example.com/dav/\n填写完整的 WebDAV 服务地址。";
            if (type == "阿里云 OSS") return "例如：https://oss-cn-hangzhou.aliyuncs.com\n填写 OSS Endpoint，不填写控制台地址。";
            return "例如：http://192.168.1.10:9000\n填写 S3 API 地址，不是 MinIO 控制台端口。";
        }
        private static TextBox Box() { return new TextBox { Font = new Font("Microsoft YaHei UI", 10F) }; }
        private static void AddCtrlA(TextBox box) { box.KeyDown += delegate(object sender, KeyEventArgs args) { if (args.Control && args.KeyCode == Keys.A) { box.SelectAll(); args.SuppressKeyPress = true; } }; }
    }
}
