using System;
using System.Drawing;
using System.Windows.Forms;

namespace RcloneOssManager
{
    /// <summary>常规属性使用可选择复制的键值表，不再用纯文本消息框呈现。</summary>
    internal sealed class ObjectInfoForm : Form
    {
        /// <summary>构建只读属性窗，不发起新的远端扫描或写操作。</summary>
        /// <param name="name">对象名。</param><param name="path">完整无签名地址。</param><param name="details">已脱敏的属性文本。</param>
        internal ObjectInfoForm(string name, string path, string details)
        {
            Text = "属性 — " + name; ClientSize = new Size(720, 460); MinimumSize = new Size(580, 360); StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 10F); BackColor = UiStyle.Surface; ForeColor = UiStyle.Text; MinimizeBox = false; MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, AutoEllipsis = true, Font = new Font(Font, FontStyle.Bold), ForeColor = UiStyle.AccentText, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            layout.Controls.Add(new HintTextBox { Text = path, Dock = DockStyle.Fill, ReadOnly = true, BackColor = UiStyle.Card, Margin = new Padding(0, 0, 0, 10) }, 0, 1);
            var grid = new DataGridView { Name = "propertyGrid", Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, ColumnHeadersVisible = false, BorderStyle = BorderStyle.None, BackgroundColor = UiStyle.Card, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = UiStyle.BorderSoft, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, SelectionMode = DataGridViewSelectionMode.CellSelect };
            grid.DefaultCellStyle.Padding = new Padding(8, 7, 8, 7); grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True; grid.AlternatingRowsDefaultCellStyle.BackColor = UiStyle.Hover;
            grid.Columns.Add(new DataGridViewTextBoxColumn { Width = 125, SortMode = DataGridViewColumnSortMode.NotSortable }); grid.Columns.Add(new DataGridViewTextBoxColumn { AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
            foreach (var line in details.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)) { var index = line.IndexOf('：'); grid.Rows.Add(index < 0 ? "说明" : line.Substring(0, index), index < 0 ? line : line.Substring(index + 1)); }
            layout.Controls.Add(grid, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            var close = MainForm.ButtonOf("关闭", UiStyle.Accent, UiStyle.OnAccent); close.Size = new Size(96, 32); close.DialogResult = DialogResult.Cancel;
            var copy = MainForm.ButtonOf("复制属性", UiStyle.AccentSoft, UiStyle.AccentText); copy.Size = new Size(96, 32); copy.Click += delegate { try { Clipboard.SetText(path + Environment.NewLine + details); } catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "剪贴板正被其他程序占用，请重试。"); } };
            buttons.Controls.Add(close); buttons.Controls.Add(copy); layout.Controls.Add(buttons, 0, 3); Controls.Add(layout); CancelButton = close;
        }
    }
}
