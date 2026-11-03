using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// 托盘图标 + 右键菜单。⚠ 本文件由 ui-dev 实现，公共 API 不要改。
    ///
    /// 契约：
    ///  - 构造时创建 NotifyIcon（显示为常驻托盘），图标可用 SystemIcons.Application 或
    ///    代码绘制一个 16/32px "En" 圆角图标（用 Bitmap Graphics 绘制，不需要外部 ico 文件）。
    ///  - 右键菜单项：启用/暂停（勾选状态）、模式子菜单（自动识别/强制英文/强制中文，单选）、
    ///    设置…、状态信息（弹气泡）、打开日志、退出。
    ///    点击后触发对应事件（事件参数忽略；ModeRequested 传 "auto"/"english"/"chinese"）。
    ///  - UpdateStatus(enabled, modeText, extra)：更新托盘 ToolTipText（<=63 字符）与"启用"勾选、
    ///    模式单选状态；菜单可能尚未创建时安全忽略。
    ///  - ShowBalloon(title, text)：NotifyIcon.ShowBalloonTip(3000)（不弹模态框）。
    ///  - Dispose()：隐藏并释放 NotifyIcon。
    ///  - 所有事件在 UI 线程触发。
    /// </summary>
    public sealed class TrayController : IDisposable
    {
        public event EventHandler ToggleEnabledRequested;
        public event EventHandler SettingsRequested;
        public event EventHandler DiagnosticsRequested;
        public event EventHandler ExitRequested;
        public event EventHandler<string> ModeRequested;

        const int MaxTooltipChars = 63;
        const int MaxBalloonTextChars = 250;

        readonly AppConfig cfg;
        readonly NotifyIcon notifyIcon;
        readonly ContextMenuStrip menu;
        readonly Font menuFont;

        ToolStripMenuItem itemEnabled;
        ToolStripMenuItem itemModeRoot;
        ToolStripMenuItem itemAuto;
        ToolStripMenuItem itemEnglish;
        ToolStripMenuItem itemChinese;
        ToolStripMenuItem itemSettings;
        ToolStripMenuItem itemStatus;
        ToolStripMenuItem itemLog;
        ToolStripMenuItem itemExit;

        Icon iconEnabled;
        Icon iconDisabled;
        Icon fallbackIcon;

        bool disposed;
        bool lastEnabled = true;
        string lastModeText = "自动";
        string lastExtra = "";

        public TrayController(AppConfig cfg)
        {
            this.cfg = cfg ?? new AppConfig();

            menuFont = Theme.UiFont(9f);
            menu = new ContextMenuStrip();
            menu.Font = menuFont;
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            BuildMenu();

            try { iconEnabled = CreateTrayIcon(true); } catch { iconEnabled = null; }
            try { iconDisabled = CreateTrayIcon(false); } catch { iconDisabled = null; }
            if (iconDisabled == null) iconDisabled = iconEnabled;
            if (iconEnabled == null)
            {
                fallbackIcon = (Icon)SystemIcons.Application.Clone();
                iconEnabled = fallbackIcon;
                iconDisabled = fallbackIcon;
            }

            notifyIcon = new NotifyIcon();
            notifyIcon.Text = "英文候选助手";
            notifyIcon.Icon = iconEnabled;
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.DoubleClick += delegate { RaiseToggle(); };
            notifyIcon.Visible = true;

            SyncFromConfig();
        }

        // ------------------------------------------------------------------
        // 公开方法
        // ------------------------------------------------------------------

        public void UpdateStatus(bool enabled, string modeText, string extra)
        {
            if (disposed) return;
            lastEnabled = enabled;
            if (!string.IsNullOrEmpty(modeText)) lastModeText = modeText.Trim();
            lastExtra = extra ?? "";
            try
            {
                if (itemEnabled != null) itemEnabled.Checked = enabled;
                string mode = NormalizeMode(modeText);
                if (mode != null) SetModeChecks(mode);

                Icon icon = enabled ? iconEnabled : iconDisabled;
                if (icon != null && notifyIcon != null) notifyIcon.Icon = icon;

                string tip = "英文候选助手 · " + (enabled ? "已启用" : "已暂停") + " · " + lastModeText;
                if (lastExtra.Length > 0) tip += " · " + lastExtra;
                SetTooltip(tip);
            }
            catch
            {
                // 托盘状态更新失败不影响主流程
            }
        }

        public void ShowBalloon(string title, string text)
        {
            if (disposed || notifyIcon == null) return;
            try
            {
                string t = (title ?? "英文候选助手").Trim();
                if (t.Length == 0) t = "英文候选助手";
                if (t.Length > MaxTooltipChars) t = t.Substring(0, MaxTooltipChars);

                string body = text ?? "";
                if (body.Length > MaxBalloonTextChars) body = body.Substring(0, MaxBalloonTextChars - 1) + "…";

                if (!notifyIcon.Visible) notifyIcon.Visible = true;
                notifyIcon.ShowBalloonTip(3000, t, body, ToolTipIcon.Info);
            }
            catch
            {
                // 气泡通知失败静默（例如系统通知被关闭）
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (notifyIcon != null)
                {
                    notifyIcon.Visible = false;
                    notifyIcon.ContextMenuStrip = null;
                    notifyIcon.Dispose();
                }
            }
            catch
            {
            }
            try { if (menu != null) menu.Dispose(); } catch { }
            try { if (menuFont != null) menuFont.Dispose(); } catch { }
            try
            {
                if (iconEnabled != null && iconEnabled != fallbackIcon && iconEnabled != iconDisabled) iconEnabled.Dispose();
            }
            catch
            {
            }
            try
            {
                if (iconDisabled != null && iconDisabled != iconEnabled && iconDisabled != fallbackIcon) iconDisabled.Dispose();
            }
            catch
            {
            }
            try { if (fallbackIcon != null) fallbackIcon.Dispose(); } catch { }
            iconEnabled = null;
            iconDisabled = null;
            fallbackIcon = null;
            GC.SuppressFinalize(this);
        }

        // ------------------------------------------------------------------
        // 菜单
        // ------------------------------------------------------------------

        void BuildMenu()
        {
            itemEnabled = new ToolStripMenuItem("启用英文候选");
            itemEnabled.Checked = true;
            itemEnabled.Click += delegate { RaiseToggle(); };

            // 注意：.NET 10 WinForms 的 ToolStripMenuItem 没有 RadioCheck 属性，
            // 这里手动维护三个模式项的互斥勾选（CheckOnClick 关闭，点击后由 SelectMode 统一设置）。
            itemAuto = new ToolStripMenuItem("自动识别（跟随输入法）");
            itemAuto.CheckOnClick = false;
            itemAuto.Checked = true;
            itemAuto.Click += delegate { SelectMode("auto"); };

            itemEnglish = new ToolStripMenuItem("强制英文候选");
            itemEnglish.CheckOnClick = false;
            itemEnglish.Click += delegate { SelectMode("english"); };

            itemChinese = new ToolStripMenuItem("强制中文模式（不弹候选）");
            itemChinese.CheckOnClick = false;
            itemChinese.Click += delegate { SelectMode("chinese"); };

            itemModeRoot = new ToolStripMenuItem("工作模式");
            itemModeRoot.DropDownItems.AddRange(new ToolStripItem[]
            {
                itemAuto, itemEnglish, itemChinese
            });

            itemSettings = new ToolStripMenuItem("设置…");
            itemSettings.Click += delegate { RaiseSettings(); };

            itemStatus = new ToolStripMenuItem("状态信息");
            itemStatus.Click += delegate { ShowStatusBalloon(); };

            itemLog = new ToolStripMenuItem("打开日志");
            itemLog.Click += delegate { RaiseDiagnostics(); };

            itemExit = new ToolStripMenuItem("退出");
            itemExit.Click += delegate { RaiseExit(); };

            menu.Items.AddRange(new ToolStripItem[]
            {
                itemEnabled,
                new ToolStripSeparator(),
                itemModeRoot,
                new ToolStripSeparator(),
                itemSettings,
                itemStatus,
                itemLog,
                new ToolStripSeparator(),
                itemExit
            });

            // 每次展开菜单时用最新配置同步勾选状态（Program 也可能直接改 cfg）
            menu.Opening += delegate { SyncFromConfig(); };
        }

        void SyncFromConfig()
        {
            try
            {
                if (cfg == null) return;
                lastEnabled = cfg.Enabled;
                if (itemEnabled != null) itemEnabled.Checked = cfg.Enabled;
                string mode = NormalizeMode(cfg.Mode);
                if (mode != null) SetModeChecks(mode);
            }
            catch
            {
            }
        }

        void SetModeChecks(string mode)
        {
            if (itemAuto != null) itemAuto.Checked = mode == "auto";
            if (itemEnglish != null) itemEnglish.Checked = mode == "english";
            if (itemChinese != null) itemChinese.Checked = mode == "chinese";
        }

        void SelectMode(string mode)
        {
            SetModeChecks(mode);
            RaiseMode(mode);
        }

        /// <summary>"auto"/"english"/"chinese"（也兼容 Program 传入的"自动/英文/中文"）。</summary>
        static string NormalizeMode(string modeText)
        {
            if (string.IsNullOrEmpty(modeText)) return null;
            string s = modeText.Trim().ToLowerInvariant();
            if (s.Length == 0) return null;
            if (s == "auto" || s.Contains("自动")) return "auto";
            if (s == "english" || s.Contains("英文") || s.Contains("英")) return "english";
            if (s == "chinese" || s.Contains("中文") || s.Contains("中")) return "chinese";
            return null;
        }

        void ShowStatusBalloon()
        {
            string enabledText = lastEnabled ? "已启用" : "已暂停";
            string mode = string.IsNullOrEmpty(lastModeText) ? "自动" : lastModeText;
            string body = "当前状态：" + enabledText + "\n" +
                          "工作模式：" + mode + "\n";
            if (!string.IsNullOrEmpty(lastExtra)) body += "词典：" + lastExtra + "\n";
            body += "热键：Ctrl+Alt+E 开关，Ctrl+Alt+D 状态";
            ShowBalloon("英文候选助手 · 状态", body);
        }

        // ------------------------------------------------------------------
        // 事件触发（统一 try/catch，避免打断消息循环）
        // ------------------------------------------------------------------

        void RaiseToggle()
        {
            try
            {
                EventHandler h = ToggleEnabledRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
            catch
            {
            }
        }

        void RaiseSettings()
        {
            try
            {
                EventHandler h = SettingsRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
            catch
            {
            }
        }

        void RaiseDiagnostics()
        {
            try
            {
                EventHandler h = DiagnosticsRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
            catch
            {
            }
        }

        void RaiseExit()
        {
            try
            {
                EventHandler h = ExitRequested;
                if (h != null) h(this, EventArgs.Empty);
            }
            catch
            {
            }
        }

        void RaiseMode(string mode)
        {
            try
            {
                EventHandler<string> h = ModeRequested;
                if (h != null) h(this, mode);
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // 托盘图标：纯代码绘制的 "En" 圆角图标（多尺寸 ICO，无外部文件）
        // ------------------------------------------------------------------

        void SetTooltip(string tip)
        {
            if (notifyIcon == null) return;
            string text = (tip ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length == 0) text = "英文候选助手";
            if (text.Length > MaxTooltipChars) text = text.Substring(0, MaxTooltipChars);
            notifyIcon.Text = text;
        }

        static Icon CreateTrayIcon(bool enabled)
        {
            try
            {
                return BuildMultiSizeIcon(new int[] { 16, 20, 24, 32 }, enabled);
            }
            catch
            {
            }
            // 兜底：单张 32px 图标
            try
            {
                using (Bitmap bmp = RenderIconBitmap(32, enabled))
                {
                    IntPtr h = bmp.GetHicon();
                    try
                    {
                        using (Icon tmp = Icon.FromHandle(h))
                        {
                            return (Icon)tmp.Clone();
                        }
                    }
                    finally
                    {
                        DestroyIcon(h);
                    }
                }
            }
            catch
            {
            }
            return (Icon)SystemIcons.Application.Clone();
        }

        static Icon BuildMultiSizeIcon(int[] sizes, bool enabled)
        {
            List<byte[]> entries = new List<byte[]>(sizes.Length);
            foreach (int size in sizes) entries.Add(BuildDibIconEntry(size, enabled));

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((ushort)0);              // reserved
                w.Write((ushort)1);              // type = icon
                w.Write((ushort)sizes.Length);   // count

                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int s = sizes[i];
                    w.Write((byte)(s >= 256 ? 0 : s));   // width
                    w.Write((byte)(s >= 256 ? 0 : s));   // height
                    w.Write((byte)0);                    // color count
                    w.Write((byte)0);                    // reserved
                    w.Write((ushort)1);                  // planes
                    w.Write((ushort)32);                 // bit count
                    w.Write((uint)entries[i].Length);
                    w.Write((uint)offset);
                    offset += entries[i].Length;
                }
                foreach (byte[] e in entries) w.Write(e);
                w.Flush();
                ms.Position = 0;
                using (Icon raw = new Icon(ms))
                {
                    return (Icon)raw.Clone();
                }
            }
        }

        /// <summary>生成一个 32bpp DIB 图标项（BITMAPINFOHEADER + BGRA 自下而上 + AND 掩码）。</summary>
        static byte[] BuildDibIconEntry(int size, bool enabled)
        {
            using (Bitmap bmp = RenderIconBitmap(size, enabled))
            {
                int xorSize = size * size * 4;
                int maskRowBytes = ((size + 31) / 32) * 4;
                int andSize = maskRowBytes * size;

                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter w = new BinaryWriter(ms))
                {
                    w.Write(40);                        // biSize
                    w.Write(size);                      // biWidth
                    w.Write(size * 2);                  // biHeight（XOR + AND）
                    w.Write((ushort)1);                 // biPlanes
                    w.Write((ushort)32);                // biBitCount
                    w.Write(0);                         // biCompression = BI_RGB
                    w.Write(xorSize + andSize);         // biSizeImage
                    w.Write(0);                         // biXPelsPerMeter
                    w.Write(0);                         // biYPelsPerMeter
                    w.Write(0);                         // biClrUsed
                    w.Write(0);                         // biClrImportant

                    BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        int stride = data.Stride;
                        byte[] row = new byte[size * 4];
                        for (int y = size - 1; y >= 0; y--)
                        {
                            Marshal.Copy(data.Scan0 + y * stride, row, 0, row.Length);
                            w.Write(row);
                        }

                        byte[] maskRow = new byte[maskRowBytes];
                        for (int y = size - 1; y >= 0; y--)
                        {
                            Array.Clear(maskRow, 0, maskRowBytes);
                            IntPtr rowPtr = data.Scan0 + y * stride;
                            for (int x = 0; x < size; x++)
                            {
                                if (Marshal.ReadByte(rowPtr, x * 4 + 3) == 0)
                                    maskRow[x >> 3] |= (byte)(0x80 >> (x & 7));
                            }
                            w.Write(maskRow);
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }

                    w.Flush();
                    return ms.ToArray();
                }
            }
        }

        static Bitmap RenderIconBitmap(int size, bool enabled)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                int margin = size >= 24 ? 1 : 0;
                Rectangle rect = new Rectangle(margin, margin, size - margin * 2, size - margin * 2);
                int radius = Math.Max(3, (int)Math.Round(size * 0.25));

                Color top = enabled ? Color.FromArgb(62, 148, 226) : Color.FromArgb(168, 174, 184);
                Color bottom = enabled ? Color.FromArgb(0, 94, 178) : Color.FromArgb(128, 134, 146);

                using (GraphicsPath path = RoundedRect(rect, radius))
                {
                    using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(0, 0, size, size), top, bottom, 90f))
                        g.FillPath(brush, path);
                }

                using (Font font = new Font("Segoe UI", size * 0.52f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (StringFormat sf = new StringFormat())
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString("En", font, textBrush, new RectangleF(0, 0, size, size), sf);
                }
            }
            return bmp;
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            int d = Math.Min(Math.Min(radius * 2, r.Width), r.Height);
            if (d <= 1)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr hIcon);
    }
}
