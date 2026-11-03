using System;
using System.Drawing;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// 设置窗口（中文界面）。⚠ 本文件由 ui-dev 实现，公共 API 不要改。
    ///
    /// 契约：
    ///  - 控件（用 TableLayoutPanel/FlowLayoutPanel 排布，界面简洁即可）：
    ///      * 复选框：启用英文候选 / 单词候选 / 短语候选 / 打完词后联想短语 / 学习我的选词
    ///      * 数字：最多候选数（3-9，NumericUpDown）/ 最少输入字母（1-4，NumericUpDown）
    ///      * 数字：弹出延迟毫秒（30-300，NumericUpDown）
    ///      * 只读说明：热键 Ctrl+Alt+E 开关、Ctrl+Alt+D 状态；当前工作模式下拉框（自动/英文/中文）
    ///      * 按钮：保存 / 取消
    ///  - Edit(owner, cfg, out result)：以 cfg 的克隆填充界面；点"保存"时 result=新 AppConfig
    ///    （未修改的字段保持原值），返回 true；"取消"或关闭返回 false。
    ///  - 不做真实保存（保存由 Program 调 ConfigStore.Save 完成）。
    ///  - 窗口起点居中、不可最大化、字体用 "Microsoft YaHei UI" 9pt。
    /// </summary>
    public sealed class SettingsForm : Form
    {
        public AppConfig Result { get; private set; }

        readonly AppConfig original;
        readonly float uiScale;
        readonly Font uiFont;
        readonly Font sectionFont;

        readonly CheckBox chkEnabled;
        readonly CheckBox chkWords;
        readonly CheckBox chkPhrases;
        readonly CheckBox chkNextWord;
        readonly CheckBox chkLearn;

        readonly NumericUpDown numMax;
        readonly NumericUpDown numMin;
        readonly NumericUpDown numDelay;

        readonly ComboBox cboMode;

        readonly Button btnSave;
        readonly Button btnCancel;

        public SettingsForm(AppConfig cfg)
        {
            original = (cfg ?? new AppConfig()).Clone();

            int dpi = ResolveDpi(this);
            uiScale = dpi / 96f;
            float pointsToPixels = dpi / 72f;
            // 用"像素单位 + 手动缩放"而不是 AutoScaleMode：自定义布局在任何 DPI 下都可控。
            uiFont = new Font(Theme.FontFamily, 9f * pointsToPixels, FontStyle.Regular, GraphicsUnit.Pixel);
            sectionFont = new Font(Theme.FontFamily, 10f * pointsToPixels, FontStyle.Bold, GraphicsUnit.Pixel);

            Text = "英文候选助手 · 设置";
            Font = uiFont;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            ClientSize = new Size(Sc(480), Sc(475));

            // ---------------- 控件 ----------------
            chkEnabled = MakeCheck("启用英文候选（总开关）", "chkEnabled", original.Enabled);
            chkWords = MakeCheck("显示单词候选", "chkWords", original.SuggestWords);
            chkPhrases = MakeCheck("显示短语候选", "chkPhrases", original.SuggestPhrases);
            chkNextWord = MakeCheck("打完词后联想下一个词 / 短语", "chkNextWord", original.SuggestNextWord);
            chkLearn = MakeCheck("学习我的选词习惯", "chkLearn", original.LearnUserSelections);

            numMax = new NumericUpDown();
            numMax.Name = "numMaxCandidates";
            numMax.Minimum = 3;
            numMax.Maximum = 9;
            numMax.Value = Clamp(original.MaxCandidates, 3, 9);
            numMax.Width = Sc(90);

            numMin = new NumericUpDown();
            numMin.Name = "numMinPrefix";
            numMin.Minimum = 1;
            numMin.Maximum = 4;
            numMin.Value = Clamp(original.MinPrefixLength, 1, 4);
            numMin.Width = Sc(90);

            numDelay = new NumericUpDown();
            numDelay.Name = "numDelay";
            numDelay.Minimum = 30;
            numDelay.Maximum = 300;
            numDelay.Increment = 10;
            numDelay.Value = Clamp(original.PopupDelayMs, 30, 300);
            numDelay.Width = Sc(90);

            cboMode = new ComboBox();
            cboMode.Name = "cboMode";
            cboMode.DropDownStyle = ComboBoxStyle.DropDownList;
            cboMode.Items.AddRange(new object[]
            {
                "自动识别（跟随输入法）",
                "强制英文候选",
                "强制中文（不弹候选）"
            });
            cboMode.SelectedIndex = ModeIndex(original.Mode);
            cboMode.Width = Sc(220);

            btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.Name = "btnSave";
            btnSave.Width = Sc(92);
            btnSave.Height = Sc(32);
            btnSave.UseVisualStyleBackColor = true;

            btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Name = "btnCancel";
            btnCancel.Width = Sc(92);
            btnCancel.Height = Sc(32);
            btnCancel.UseVisualStyleBackColor = true;
            btnCancel.DialogResult = DialogResult.Cancel;

            // ---------------- 布局 ----------------
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.BackColor = Color.White;
            root.ColumnCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.Padding = new Padding(Sc(18), Sc(12), Sc(18), Sc(12));
            Controls.Add(root);

            int row = 0;
            int tab = 0;

            Label AddSection(string text)
            {
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Label label = new Label();
                label.Text = text;
                label.AutoSize = true;
                label.Font = sectionFont;
                label.ForeColor = Theme.Accent;
                label.Margin = new Padding(0, row == 0 ? 0 : Sc(14), 0, Sc(6));
                root.Controls.Add(label, 0, row);
                root.SetColumnSpan(label, 2);
                row++;
                return label;
            }

            void AddCheck(CheckBox box)
            {
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                box.TabIndex = tab++;
                box.Margin = new Padding(Sc(2), Sc(3), 0, Sc(3));
                root.Controls.Add(box, 0, row);
                root.SetColumnSpan(box, 2);
                row++;
            }

            void AddField(string labelText, Control control)
            {
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Label label = new Label();
                label.Text = labelText;
                label.AutoSize = true;
                label.Anchor = AnchorStyles.Left;
                label.Margin = new Padding(Sc(2), Sc(8), Sc(16), 0);
                root.Controls.Add(label, 0, row);

                control.Anchor = AnchorStyles.Left;
                control.Margin = new Padding(0, Sc(6), 0, 0);
                control.TabIndex = tab++;
                root.Controls.Add(control, 1, row);
                row++;
            }

            void AddReadOnlyRow(string labelText, string valueText)
            {
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Label label = new Label();
                label.Text = labelText;
                label.AutoSize = true;
                label.Anchor = AnchorStyles.Left;
                label.Margin = new Padding(Sc(2), Sc(7), Sc(16), 0);
                root.Controls.Add(label, 0, row);

                Label value = new Label();
                value.Text = valueText;
                value.AutoSize = true;
                value.Anchor = AnchorStyles.Left;
                value.ForeColor = Theme.SubText;
                value.Margin = new Padding(0, Sc(7), 0, 0);
                root.Controls.Add(value, 1, row);
                row++;
            }

            AddSection("候选显示");
            AddCheck(chkEnabled);
            AddCheck(chkWords);
            AddCheck(chkPhrases);
            AddCheck(chkNextWord);
            AddCheck(chkLearn);

            AddField("最多候选数（3-9）：", numMax);
            AddField("最少输入字母（1-4）：", numMin);
            AddField("弹出延迟（毫秒）：", numDelay);

            AddSection("工作模式与热键");
            AddField("当前工作模式：", cboMode);
            AddReadOnlyRow("开关热键：", "Ctrl + Alt + E（启用 / 暂停）");
            AddReadOnlyRow("状态热键：", "Ctrl + Alt + D（状态气泡）");

            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Label hint = new Label();
            hint.Text = "提示：候选条弹出后按数字键 1-9 选词，Esc 关闭；修改设置后点「保存」立即生效。";
            hint.AutoSize = true;
            hint.ForeColor = Theme.SubText;
            hint.MaximumSize = new Size(Sc(440), 0);
            hint.Margin = new Padding(Sc(2), Sc(16), 0, 0);
            root.Controls.Add(hint, 0, row);
            root.SetColumnSpan(hint, 2);
            row++;

            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            row++;

            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Sc(52)));
            FlowLayoutPanel buttonBar = new FlowLayoutPanel();
            buttonBar.Dock = DockStyle.Fill;
            buttonBar.FlowDirection = FlowDirection.RightToLeft;
            buttonBar.WrapContents = false;
            buttonBar.BackColor = Color.White;
            buttonBar.Margin = new Padding(0, Sc(10), 0, 0);
            btnCancel.TabIndex = tab++;
            btnSave.TabIndex = tab++;
            buttonBar.Controls.Add(btnCancel);
            buttonBar.Controls.Add(btnSave);
            root.Controls.Add(buttonBar, 0, row);
            root.SetColumnSpan(buttonBar, 2);
            row++;

            root.RowCount = row;
            AcceptButton = btnSave;
            CancelButton = btnCancel;
            btnSave.Click += delegate { SaveSettings(); };
        }

        // ------------------------------------------------------------------
        // 保存 / 取消
        // ------------------------------------------------------------------

        void SaveSettings()
        {
            AppConfig result = original.Clone();
            result.Enabled = chkEnabled.Checked;
            result.SuggestWords = chkWords.Checked;
            result.SuggestPhrases = chkPhrases.Checked;
            result.SuggestNextWord = chkNextWord.Checked;
            result.LearnUserSelections = chkLearn.Checked;
            result.MaxCandidates = (int)numMax.Value;
            result.MinPrefixLength = (int)numMin.Value;
            result.PopupDelayMs = (int)numDelay.Value;
            result.Mode = ModeFromIndex(cboMode.SelectedIndex);
            Result = result;
            DialogResult = DialogResult.OK;
        }

        public static bool Edit(IWin32Window owner, AppConfig cfg, out AppConfig result)
        {
            result = null;
            using (SettingsForm form = new SettingsForm(cfg))
            {
                if (owner != null) form.StartPosition = FormStartPosition.CenterParent;
                DialogResult dr = owner != null ? form.ShowDialog(owner) : form.ShowDialog();
                if (dr == DialogResult.OK && form.Result != null)
                {
                    result = form.Result;
                    return true;
                }
                return false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { uiFont.Dispose(); } catch { }
                try { sectionFont.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------
        // 小工具
        // ------------------------------------------------------------------

        static CheckBox MakeCheck(string text, string name, bool value)
        {
            CheckBox box = new CheckBox();
            box.Text = text;
            box.Name = name;
            box.Checked = value;
            box.AutoSize = true;
            return box;
        }

        /// <summary>96 DPI 逻辑像素 → 当前 DPI 设备像素。</summary>
        int Sc(int value)
        {
            if (uiScale <= 0f) return value;
            return (int)Math.Round(value * uiScale);
        }

        /// <summary>窗口句柄创建前 DeviceDpi 可能不准，用屏幕 DC 的 DPI 兜底取较大者。</summary>
        static int ResolveDpi(Control control)
        {
            int dpi = 0;
            try { if (control != null) dpi = control.DeviceDpi; } catch { }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    int screenDpi = (int)Math.Round(g.DpiX);
                    if (screenDpi > dpi) dpi = screenDpi;
                }
            }
            catch
            {
            }
            if (dpi <= 0) dpi = 96;
            return dpi;
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        static int ModeIndex(string mode)
        {
            if (mode == "english") return 1;
            if (mode == "chinese") return 2;
            return 0;
        }

        static string ModeFromIndex(int index)
        {
            if (index == 1) return "english";
            if (index == 2) return "chinese";
            return "auto";
        }
    }
}
