using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// 候选弹窗（无边框、置顶、不抢焦点）。⚠ 本文件由 ui-dev 实现，公共 API 不要改。
    ///
    /// 契约：
    ///  - 窗口样式必须包含 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST，
    ///    重写 ShowWithoutActivation => true，创建后只能 Show() 不能 Activate()/Focus()；
    ///    否则会抢走用户正在打字的输入焦点。
    ///  - ShowCandidates(list, anchorScreen, anchorIsCaret)：
    ///      * anchorScreen 是候选框左上角期望位置（anchorIsCaret=true 时通常传光标右下角）；
    ///      * 自动把窗口定位到不超出工作区（下/上翻转，左右收边）；
    ///      * 显示编号 1..N + 候选文字；短语类候选项右侧加"短语"小标记；
    ///      * 若 list 为空则隐藏。
    ///  - 鼠标点击候选：触发 CandidatePicked(index)（0 基），然后隐藏（不能激活窗口）。
    ///    窗口收到 WM_MOUSEACTIVATE 时必须返回 MA_NOACTIVATE，并在鼠标点击时手动判定命中项。
    ///  - HidePopup()：隐藏；隐藏后 PopupVisible=false。
    ///  - ApplyConfig(cfg)：保存引用并在下次显示时应用（最多候选数、字体/宽度等）。
    ///  - 不使用 FormBorderStyle=FixedToolWindow 之外的花哨依赖；高 DPI 用 Font 自动缩放。
    ///  - 事件 CandidatePicked 的 sender 传 this，参数是 int index。
    /// </summary>
    public sealed class CandidateForm : Form
    {
        public event EventHandler<int> CandidatePicked;
        public event EventHandler Dismissed;

        // ---- Win32 样式 / 消息 ----
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_TOPMOST = 0x00000008;
        const int WM_MOUSEACTIVATE = 0x0021;
        const int WM_DPICHANGED = 0x02E0;
        const int MA_NOACTIVATE = 3;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;

        AppConfig cfg;

        readonly List<Candidate> items = new List<Candidate>();
        readonly List<ItemLayout> layouts = new List<ItemLayout>();

        Font fontWord;
        Font fontNumber;
        Font fontBadge;
        int fontDpi = -1;
        bool dwmRounded;

        int hoverIndex = -1;
        int pressedIndex = -1;

        Point lastAnchor;
        bool lastAnchorIsCaret;
        bool hasPlacement;

        /// <summary>一条候选的绘制/命中区域（客户区坐标）。</summary>
        sealed class ItemLayout
        {
            public Rectangle Bounds;
            public Rectangle Number;
            public Rectangle Word;
            public Rectangle Badge;
            public bool HasBadge;
            public string Text;
        }

        public CandidateForm(AppConfig cfg)
        {
            this.cfg = cfg ?? new AppConfig();

            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Background;
            DoubleBuffered = true;
            Cursor = Cursors.Default;
            Text = "英文候选助手候选条";
            MinimizeBox = false;
            MaximizeBox = false;

            EnsureFonts();
        }

        public bool PopupVisible { get; private set; }
        public int Count { get; private set; }

        // ------------------------------------------------------------------
        // 公开 API
        // ------------------------------------------------------------------

        public void ShowCandidates(IList<Candidate> candidates, Point anchorScreen, bool anchorIsCaret)
        {
            if (IsDisposed) return;
            if (candidates == null || candidates.Count == 0)
            {
                HidePopup();
                return;
            }

            List<Candidate> next = new List<Candidate>(candidates.Count);
            int max = MaxCandidateCount();
            for (int i = 0; i < candidates.Count && next.Count < max; i++)
            {
                Candidate candidate = candidates[i];
                if (candidate == null) continue;
                next.Add(candidate);
            }
            if (next.Count == 0)
            {
                HidePopup();
                return;
            }

            items.Clear();
            items.AddRange(next);

            lastAnchor = anchorScreen;
            lastAnchorIsCaret = anchorIsCaret;
            hasPlacement = true;
            ShowAt(anchorScreen, anchorIsCaret);
        }

        public void HidePopup()
        {
            bool wasVisible = PopupVisible || Visible;
            PopupVisible = false;
            Count = 0;
            hoverIndex = -1;
            pressedIndex = -1;
            items.Clear();
            layouts.Clear();
            try
            {
                if (IsHandleCreated && Visible) Hide();
            }
            catch
            {
            }
            if (wasVisible)
            {
                try
                {
                    EventHandler handler = Dismissed;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
                catch
                {
                }
            }
        }

        public void ApplyConfig(AppConfig cfg)
        {
            this.cfg = cfg ?? new AppConfig();
            if (!PopupVisible || !hasPlacement || items.Count == 0) return;
            try
            {
                ShowAt(lastAnchor, lastAnchorIsCaret);
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // 显示 / 定位
        // ------------------------------------------------------------------

        void ShowAt(Point anchor, bool anchorIsCaret)
        {
            Rectangle wa = Screen.FromPoint(anchor).WorkingArea;
            int maxTotalWidth = Math.Min(S(Theme.MaxPopupWidth), Math.Max(S(180), wa.Width - S(16)));
            Size size = BuildLayout(maxTotalWidth);
            Point location = Place(anchor, anchorIsCaret, size, wa);

            hoverIndex = -1;
            pressedIndex = -1;

            bool wasVisible = Visible;
            SuspendLayout();
            try
            {
                if (ClientSize != size) ClientSize = size;
                Location = location;
                UpdateRoundedRegion();
            }
            finally
            {
                ResumeLayout(false);
            }

            PopupVisible = true;
            Count = layouts.Count;

            if (!wasVisible)
            {
                // 只 Show()，绝不 Activate()/Focus()：窗口带 WS_EX_NOACTIVATE + ShowWithoutActivation。
                try { Show(); } catch { }
            }
            else
            {
                Invalidate();
            }
        }

        Point Place(Point anchor, bool anchorIsCaret, Size size, Rectangle wa)
        {
            int x = anchor.X;
            int y = anchorIsCaret ? anchor.Y + S(2) : anchor.Y;

            // 光标下方空间不够：翻到光标上方（用字体行高估算光标高度）
            if (anchorIsCaret && y + size.Height > wa.Bottom)
            {
                int lineHeight = FontHeightPixels();
                y = anchor.Y - lineHeight - S(2) - size.Height;
            }
            if (y + size.Height > wa.Bottom) y = wa.Bottom - size.Height;
            if (y < wa.Top) y = wa.Top;
            if (x + size.Width > wa.Right) x = wa.Right - size.Width;
            if (x < wa.Left) x = wa.Left;
            return new Point(x, y);
        }

        int FontHeightPixels()
        {
            EnsureFonts();
            int h = fontWord != null ? fontWord.Height : S(18);
            if (h < S(8)) h = S(8);
            return h;
        }

        // ------------------------------------------------------------------
        // 布局：横向排列 IME 风格候选条，超宽自动换行，宽度上限 520
        // ------------------------------------------------------------------

        Size BuildLayout(int maxTotalWidth)
        {
            EnsureFonts();

            int count = Math.Min(items.Count, MaxCandidateCount());
            layouts.Clear();
            if (count <= 0) return new Size(S(80), S(30));

            int padX = S(12);
            int padY = S(7);
            int itemPadX = S(8);
            int itemGap = S(2);
            int rowGap = S(3);
            int numberGap = S(6);
            int badgeGap = S(6);
            int badgePadX = S(5);
            int maxContentWidth = Math.Max(S(80), maxTotalWidth - padX * 2);

            string[] numbers = new string[count];
            Size[] numberSizes = new Size[count];
            Size[] wordSizes = new Size[count];
            Size[] badgeSizes = new Size[count];
            bool[] isPhrase = new bool[count];
            int[] widths = new int[count];
            int maxTextHeight = 0;
            int maxBadgeHeight = 0;

            TextFormatFlags measureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

            for (int i = 0; i < count; i++)
            {
                Candidate candidate = items[i];
                numbers[i] = (i + 1).ToString();
                numberSizes[i] = Measure(numbers[i], fontNumber, measureFlags);
                wordSizes[i] = Measure(candidate.Text ?? "", fontWord, measureFlags);
                isPhrase[i] = candidate.Kind == CandidateKind.Phrase;
                if (isPhrase[i])
                {
                    badgeSizes[i] = Measure("短语", fontBadge, measureFlags);
                    if (badgeSizes[i].Height > maxBadgeHeight) maxBadgeHeight = badgeSizes[i].Height;
                }
                if (wordSizes[i].Height > maxTextHeight) maxTextHeight = wordSizes[i].Height;

                int badgePart = isPhrase[i] ? badgeGap + badgeSizes[i].Width + badgePadX * 2 : 0;
                int width = itemPadX * 2 + numberSizes[i].Width + numberGap + wordSizes[i].Width + badgePart;
                if (width > maxContentWidth) width = maxContentWidth;
                widths[i] = width;
            }

            int rowHeight = Math.Max(S(30), Math.Max(maxTextHeight, maxBadgeHeight) + S(10));
            int badgeHeight = Math.Max(S(16), maxBadgeHeight + S(3));
            if (badgeHeight > rowHeight - S(4)) badgeHeight = Math.Max(S(10), rowHeight - S(4));

            int x = padX;
            int y = padY;
            int rowWidth = 0;
            int maxRowWidth = 0;

            for (int i = 0; i < count; i++)
            {
                int width = widths[i];
                if (rowWidth > 0 && rowWidth + itemGap + width > maxContentWidth)
                {
                    if (rowWidth > maxRowWidth) maxRowWidth = rowWidth;
                    x = padX;
                    y += rowHeight + rowGap;
                    rowWidth = 0;
                }
                if (rowWidth > 0)
                {
                    x += itemGap;
                    rowWidth += itemGap;
                }

                Rectangle itemRect = new Rectangle(x, y, width, rowHeight);
                int innerLeft = itemRect.Left + itemPadX;
                int innerWidth = Math.Max(S(8), itemRect.Width - itemPadX * 2);

                int numberWidth = Math.Min(numberSizes[i].Width, innerWidth);
                Rectangle numberRect = new Rectangle(innerLeft, itemRect.Top, numberWidth, itemRect.Height);
                innerLeft += numberWidth + numberGap;

                int badgeWidth = isPhrase[i] ? badgeSizes[i].Width + badgePadX * 2 : 0;
                int wordWidth = innerWidth - numberWidth - numberGap;
                if (isPhrase[i]) wordWidth -= badgeGap + badgeWidth;
                if (wordWidth < S(6)) wordWidth = S(6);
                Rectangle wordRect = new Rectangle(innerLeft, itemRect.Top, wordWidth, itemRect.Height);

                Rectangle badgeRect = Rectangle.Empty;
                if (isPhrase[i])
                {
                    int bx = innerLeft + wordWidth + badgeGap;
                    int maxBx = itemRect.Right - itemPadX / 2 - badgeWidth;
                    if (bx > maxBx) bx = Math.Max(itemRect.Left + S(2), maxBx);
                    int by = itemRect.Top + (itemRect.Height - badgeHeight) / 2;
                    badgeRect = new Rectangle(bx, by, badgeWidth, badgeHeight);
                }

                ItemLayout layout = new ItemLayout();
                layout.Bounds = itemRect;
                layout.Number = numberRect;
                layout.Word = wordRect;
                layout.Badge = badgeRect;
                layout.HasBadge = isPhrase[i];
                layout.Text = items[i].Text ?? "";
                layouts.Add(layout);

                x += width;
                rowWidth += width;
            }
            if (rowWidth > maxRowWidth) maxRowWidth = rowWidth;

            int rows = (y - padY) / (rowHeight + rowGap) + 1;
            int totalWidth = Math.Min(maxTotalWidth, maxRowWidth + padX * 2);
            int totalHeight = rows * rowHeight + (rows - 1) * rowGap + padY * 2;
            return new Size(Math.Max(S(60), totalWidth), Math.Max(S(24), totalHeight));
        }

        static Size Measure(string text, Font font, TextFormatFlags flags)
        {
            return TextRenderer.MeasureText(text ?? "", font, new Size(4096, 4096), flags);
        }

        /// <summary>96 DPI 逻辑像素 → 当前 DPI 设备像素（自绘时所有间距都经此换算）。</summary>
        int S(int value)
        {
            int dpi = DeviceDpi;
            if (dpi <= 0) dpi = 96;
            if (dpi == 96) return value;
            return (int)Math.Round(value * (dpi / 96.0));
        }

        int MaxCandidateCount()
        {
            int max = cfg != null ? cfg.MaxCandidates : 9;
            if (max < 1) max = 1;
            if (max > 9) max = 9;
            return max;
        }

        int HitTest(Point point)
        {
            for (int i = 0; i < layouts.Count; i++)
            {
                if (layouts[i].Bounds.Contains(point)) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------
        // 绘制
        // ------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            // 完全自绘（不调用 base.OnPaint，避免背景擦除闪烁）
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle rc = ClientRectangle;
            if (rc.Width <= 1 || rc.Height <= 1) return;

            int radius = BorderRadius();

            using (GraphicsPath background = RoundedRect(rc, radius))
            using (LinearGradientBrush brush = new LinearGradientBrush(
                       new Rectangle(rc.X, rc.Y, Math.Max(2, rc.Width), Math.Max(2, rc.Height)),
                       Color.FromArgb(252, 252, 254), Color.FromArgb(246, 247, 250), 90f))
            {
                g.FillPath(brush, background);
            }

            // 顶部 1px 高光，制造轻微"卡片"层次
            using (Pen highlight = new Pen(Color.FromArgb(96, 255, 255, 255), 1f))
            {
                int inset = radius + S(2);
                if (rc.Width > inset * 2)
                    g.DrawLine(highlight, inset, rc.Top + 1, rc.Right - inset, rc.Top + 1);
            }

            using (GraphicsPath borderPath = RoundedRect(new Rectangle(rc.X, rc.Y, rc.Width - 1, rc.Height - 1), radius))
            using (Pen border = new Pen(Theme.Border, 1f))
            {
                g.DrawPath(border, borderPath);
            }

            if (layouts.Count == 0) return;

            TextFormatFlags numberFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                                         TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter |
                                         TextFormatFlags.VerticalCenter;
            TextFormatFlags wordFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                                        TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter |
                                        TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
            TextFormatFlags badgeFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                                         TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter |
                                         TextFormatFlags.VerticalCenter;

            for (int i = 0; i < layouts.Count; i++)
            {
                ItemLayout layout = layouts[i];
                bool hovered = i == hoverIndex;

                if (hovered)
                {
                    Rectangle hoverRect = new Rectangle(
                        layout.Bounds.X,
                        layout.Bounds.Y + S(1),
                        Math.Max(1, layout.Bounds.Width),
                        Math.Max(1, layout.Bounds.Height - S(2)));
                    using (GraphicsPath hoverPath = RoundedRect(hoverRect, S(6)))
                    using (SolidBrush hoverBrush = new SolidBrush(Theme.HoverBackground))
                    {
                        g.FillPath(hoverBrush, hoverPath);
                    }
                }

                TextRenderer.DrawText(g, (i + 1).ToString(), fontNumber, layout.Number,
                    hovered ? Theme.Accent : Theme.NumberText, numberFlags);

                TextRenderer.DrawText(g, layout.Text, fontWord, layout.Word, Theme.Text, wordFlags);

                if (layout.HasBadge)
                {
                    using (GraphicsPath badgePath = RoundedRect(layout.Badge, S(4)))
                    using (SolidBrush badgeBrush = new SolidBrush(Theme.PhraseBadge))
                    {
                        g.FillPath(badgeBrush, badgePath);
                    }
                    Rectangle borderRect = new Rectangle(
                        layout.Badge.X, layout.Badge.Y,
                        Math.Max(1, layout.Badge.Width - 1), Math.Max(1, layout.Badge.Height - 1));
                    using (GraphicsPath badgeBorderPath = RoundedRect(borderRect, S(4)))
                    using (Pen badgePen = new Pen(Theme.BadgeBorder, 1f))
                    {
                        g.DrawPath(badgePen, badgeBorderPath);
                    }
                    TextRenderer.DrawText(g, "短语", fontBadge, layout.Badge, Theme.BadgeText, badgeFlags);
                }
            }
        }

        int BorderRadius()
        {
            return dwmRounded ? S(8) : S(Theme.CornerRadius);
        }

        static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            int diameter = Math.Min(Math.Min(radius * 2, rect.Width), rect.Height);
            if (diameter <= 1)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ------------------------------------------------------------------
        // 鼠标：不激活窗口，手动判定命中项
        // ------------------------------------------------------------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = HitTest(e.Location);
            if (index != hoverIndex)
            {
                hoverIndex = index;
                Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            pressedIndex = -1;
            if (hoverIndex != -1)
            {
                hoverIndex = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) pressedIndex = HitTest(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            int index = HitTest(e.Location);
            int pressed = pressedIndex;
            pressedIndex = -1;
            if (index < 0 || index != pressed) return;

            // 先触发选中事件（Program 会注入文本），再隐藏
            try
            {
                EventHandler<int> handler = CandidatePicked;
                if (handler != null) handler(this, index);
            }
            catch
            {
            }
            HidePopup();
        }

        // ------------------------------------------------------------------
        // 不抢焦点 / Win32 消息
        // ------------------------------------------------------------------

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                // 关键：鼠标点击不激活本窗口，前台窗口/输入焦点保持不变
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }

            base.WndProc(ref m);

            if (m.Msg == WM_DPICHANGED)
            {
                try
                {
                    fontDpi = -1;
                    if (PopupVisible && hasPlacement) ShowAt(lastAnchor, lastAnchorIsCaret);
                }
                catch
                {
                }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryEnableDwmRounding();
            UpdateRoundedRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRoundedRegion();
        }

        /// <summary>Windows 11：让 DWM 把窗口裁成圆角（失败则退回 Region 裁剪）。</summary>
        void TryEnableDwmRounding()
        {
            dwmRounded = false;
            try
            {
                if (Environment.OSVersion.Version.Build < 22000) return;
                int preference = DWMWCP_ROUND;
                int hr = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
                dwmRounded = hr == 0;
            }
            catch
            {
                dwmRounded = false;
            }
        }

        void UpdateRoundedRegion()
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                if (dwmRounded)
                {
                    if (Region != null)
                    {
                        Region old = Region;
                        Region = null;
                        old.Dispose();
                    }
                    return;
                }
                int width = ClientSize.Width;
                int height = ClientSize.Height;
                if (width <= 1 || height <= 1) return;
                using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, width, height), S(Theme.CornerRadius)))
                {
                    Region old = Region;
                    Region = new Region(path);
                    if (old != null) old.Dispose();
                }
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // 字体（按 DeviceDpi 换算成像素单位，Per-Monitor V2 下尺寸准确）
        // ------------------------------------------------------------------

        void EnsureFonts()
        {
            int dpi = DeviceDpi;
            if (dpi <= 0) dpi = 96;
            if (fontWord != null && fontDpi == dpi) return;
            DisposeFonts();
            fontDpi = dpi;
            float pointsToPixels = dpi / 72f;
            fontWord = new Font(Theme.FontFamily, 10f * pointsToPixels, FontStyle.Regular, GraphicsUnit.Pixel);
            fontNumber = new Font("Segoe UI", 8.5f * pointsToPixels, FontStyle.Bold, GraphicsUnit.Pixel);
            fontBadge = new Font(Theme.FontFamily, 7f * pointsToPixels, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        void DisposeFonts()
        {
            try { if (fontWord != null) fontWord.Dispose(); } catch { }
            try { if (fontNumber != null) fontNumber.Dispose(); } catch { }
            try { if (fontBadge != null) fontBadge.Dispose(); } catch { }
            fontWord = null;
            fontNumber = null;
            fontBadge = null;
            fontDpi = -1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeFonts();
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------
        // Win32
        // ------------------------------------------------------------------

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
