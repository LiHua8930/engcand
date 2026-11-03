using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// 光标定位：拿到前台窗口里输入光标的屏幕矩形，用于把候选框放在打字位置附近。
    /// ⚠ 本文件由 ui-dev 实现，公共 API 不要改。
    ///
    /// 契约：
    ///  - TryGetCaretRect(out rect)：
    ///      1) GetForegroundWindow → GetWindowThreadProcessId → GetGUIThreadInfo，
    ///         若 gti.hwndCaret != 0 且 rcCaret 宽高合理（例如宽<=4 且高在 8..200 之间），
    ///         用 rcCaret 作为候选（注意兼容 rcCaret 是屏幕坐标还是相对客户区坐标：
    ///         用一个合理性判断/GetWindowRect 校正，失败则 ClientToScreen 换算）；
    ///      2) 失败则尝试 GetCaretPos（要求前台线程 AttachThreadInput 后调用，跨线程）；
    ///      3) 都失败返回 false。
    ///  - GetFallbackAnchor()：拿不到光标时的兜底锚点，返回鼠标当前位置
    ///    （若鼠标在屏幕外或为 0,0 且无意义，返回工作区右下角偏左上的安全点）。
    ///  - 必须在 UI 线程调用，不得抛异常（内部 try/catch 返 false）。
    /// </summary>
    public static class CaretLocator
    {
        // ------------------------------------------------------------------
        // 公开 API
        // ------------------------------------------------------------------

        public static bool TryGetCaretRect(out Rectangle rect)
        {
            rect = Rectangle.Empty;
            try
            {
                IntPtr hwndFg = Native.GetForegroundWindow();
                if (hwndFg == IntPtr.Zero) return false;

                uint pid;
                uint tid = Native.GetWindowThreadProcessId(hwndFg, out pid);
                if (tid == 0) return false;

                // ---- 1) GetGUIThreadInfo：优先方案 ----
                IntPtr hwndCaret = IntPtr.Zero;
                IntPtr hwndFocus = IntPtr.Zero;
                Native.GUITHREADINFO gti = new Native.GUITHREADINFO();
                gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
                if (Native.GetGUIThreadInfo(tid, ref gti))
                {
                    hwndCaret = gti.hwndCaret;
                    hwndFocus = gti.hwndFocus;
                    if (gti.hwndCaret != IntPtr.Zero)
                    {
                        Rectangle raw = RectFrom(gti.rcCaret);
                        if (IsPlausibleSize(raw) && TryResolveCaretRect(raw, gti.hwndCaret, gti.hwndFocus, hwndFg, out rect))
                            return true;
                    }
                }

                // ---- 2) GetCaretPos（附到前台线程后调用，跨线程取光标） ----
                if (TryGetCaretPosScreen(tid, hwndFg, hwndFocus, out rect))
                    return true;

                rect = Rectangle.Empty;
                return false;
            }
            catch
            {
                rect = Rectangle.Empty;
                return false;
            }
        }

        public static Point GetFallbackAnchor()
        {
            try
            {
                // 拿不到文本插入符（浏览器/微信/QQ 等自绘界面）时的兜底：
                //  1) 鼠标在前台窗口内 -> 出现在鼠标附近（用户注意力所在处）
                //  2) 否则 -> 前台窗口左下区域（聊天类应用的输入框通常在底部）
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Point cursor = Cursor.Position;
                IntPtr fg = Native.GetForegroundWindow();
                Rectangle fgRect;
                bool hasFg = TryGetWindowRect(fg, out fgRect);

                if (hasFg && !fgRect.IsEmpty)
                {
                    Rectangle waFg = Screen.FromRectangle(fgRect).WorkingArea;
                    if (fgRect.Contains(cursor))
                    {
                        Point p = new Point(cursor.X + S2(14), cursor.Y + S2(20));
                        return ClampInside(p, waFg);
                    }
                    // 窗口左下（聊天输入区常见位置），并往上抬一点避免贴住窗口边
                    Point bottom = new Point(fgRect.Left + S2(120), fgRect.Bottom - S2(150));
                    if (bottom.Y < fgRect.Top) bottom.Y = fgRect.Top + S2(40);
                    return ClampInside(bottom, waFg);
                }

                if (SystemInformation.VirtualScreen.Contains(cursor) && !(cursor.X == 0 && cursor.Y == 0))
                    return ClampInside(new Point(cursor.X + S2(14), cursor.Y + S2(20)), wa);
                return ClampInside(new Point(wa.Left + 200, wa.Top + 200), wa);
            }
            catch
            {
                return new Point(200, 200);
            }
        }

        static int S2(int v)
        {
            return v;
        }

        static Point ClampInside(Point p, Rectangle wa)
        {
            int x = Math.Max(wa.Left, Math.Min(p.X, wa.Right - 40));
            int y = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - 40));
            return new Point(x, y);
        }

        // ------------------------------------------------------------------
        // rcCaret 坐标兼容：可能是屏幕坐标，也可能是相对 hwndCaret/hwndFocus 客户区的坐标
        // ------------------------------------------------------------------

        static bool TryResolveCaretRect(Rectangle raw, IntPtr caretWnd, IntPtr focusWnd, IntPtr fgWnd, out Rectangle result)
        {
            result = Rectangle.Empty;
            Rectangle vs = SystemInformation.VirtualScreen;

            // 自绘界面（微信/QQ/部分浏览器）会把插入符报告成窗口原点 (0,0)，看起来就是屏幕左上角。
            // 只要换算后的位置贴着某个相关窗口的左上角，就当作不可信，直接放弃（走鼠标/窗口兜底）。
            if (LooksLikeBogusCaret(raw, caretWnd, focusWnd, fgWnd)) return false;

            // 客户区坐标换算优先（本机实测 rcCaret 是相对 hwndCaret 的客户区坐标）
            Point converted;
            if (TryClientToScreen(caretWnd, raw.Left, raw.Top, out converted))
            {
                Rectangle r = new Rectangle(converted.X, converted.Y, Math.Max(1, raw.Width), Math.Max(1, raw.Height));
                if (IsInsideWindow(caretWnd, Inflate(r, 8)) && !LooksLikeBogusCaret(r, caretWnd, focusWnd, fgWnd))
                {
                    result = r;
                    return true;
                }
            }
            if (TryClientToScreen(focusWnd, raw.Left, raw.Top, out converted))
            {
                Rectangle r = new Rectangle(converted.X, converted.Y, Math.Max(1, raw.Width), Math.Max(1, raw.Height));
                if (IsInsideWindow(focusWnd, Inflate(r, 8)) && !LooksLikeBogusCaret(r, caretWnd, focusWnd, fgWnd))
                {
                    result = r;
                    return true;
                }
            }
            if (TryClientToScreen(fgWnd, raw.Left, raw.Top, out converted))
            {
                Rectangle r = new Rectangle(converted.X, converted.Y, Math.Max(1, raw.Width), Math.Max(1, raw.Height));
                if (IsInsideWindow(fgWnd, Inflate(r, 4)) && !LooksLikeBogusCaret(r, caretWnd, focusWnd, fgWnd))
                {
                    result = r;
                    return true;
                }
            }

            // 兼容 rcCaret 为屏幕坐标的情况
            Point center = CenterOf(raw);
            if (vs.Contains(center) && !LooksLikeBogusCaret(raw, caretWnd, focusWnd, fgWnd))
            {
                Rectangle wr;
                if (TryGetWindowRect(caretWnd, out wr) && Inflate(wr, 8).Contains(center))
                {
                    result = Normalize(raw);
                    return true;
                }
                if (TryGetWindowRect(focusWnd, out wr) && Inflate(wr, 8).Contains(center))
                {
                    result = Normalize(raw);
                    return true;
                }
                if (TryGetWindowRect(fgWnd, out wr) && Inflate(wr, 4).Contains(center))
                {
                    result = Normalize(raw);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 判定插入符矩形是否是"假光标"：位于某个相关窗口（caret/focus/前台）左上角 12px 内。
        /// 自绘控件（微信/QQ/浏览器）常见这种情况——它们根本没有系统插入符。
        /// </summary>
        static bool LooksLikeBogusCaret(Rectangle caret, params IntPtr[] hwnds)
        {
            if (caret.IsEmpty) return true;
            const int tol = 12;
            foreach (IntPtr h in hwnds)
            {
                if (h == IntPtr.Zero) continue;
                Rectangle wr;
                if (!TryGetWindowRect(h, out wr)) continue;
                if (Math.Abs(caret.Left - wr.Left) <= tol && Math.Abs(caret.Top - wr.Top) <= tol) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // GetCaretPos 兜底
        // ------------------------------------------------------------------

        static bool TryGetCaretPosScreen(uint targetThreadId, IntPtr fgWnd, IntPtr focusWndHint, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            uint currentThreadId = Native.GetCurrentThreadId();
            bool crossThread = targetThreadId != currentThreadId;
            bool attached = false;
            try
            {
                // 跨线程读光标必须 AttachThreadInput 成功；失败时 GetFocus/GetCaretPos
                // 返回的是本线程的焦点/光标（通常 (0,0)），拿来换算会得到错误位置，直接放弃。
                if (crossThread)
                {
                    try { attached = Native.AttachThreadInput(currentThreadId, targetThreadId, true); }
                    catch { }
                    if (!attached) return false;
                }

                IntPtr focus = Native.GetFocus();
                if (focus == IntPtr.Zero) focus = focusWndHint;
                if (focus == IntPtr.Zero) return false;

                uint pid;
                uint focusThread = Native.GetWindowThreadProcessId(focus, out pid);
                if (focusThread != targetThreadId && focusThread != currentThreadId)
                    return false;

                Native.POINT pt;
                if (!Native.GetCaretPos(out pt)) return false;

                Native.POINT sp = pt;
                if (!Native.ClientToScreen(focus, ref sp)) return false;

                int h = GetCaretHeightPixels(focus);
                Rectangle r = new Rectangle(sp.X, sp.Y, 1, h);
                if (!IsPlausibleSize(r)) return false;
                if (!SystemInformation.VirtualScreen.Contains(r.Location)) return false;

                // 再校验一次位置合理性：光标应落在焦点窗口/前台窗口附近。
                if (!IsNearWindow(focus, r, 24) && !IsNearWindow(fgWnd, r, 24)) return false;

                // 自绘界面（微信/QQ/部分浏览器）没有真实插入符：GetFocus 返回顶层窗口、
                // GetCaretPos 给 (0,0)，结果会贴到窗口/屏幕左上角 —— 判为无效，交给兜底。
                if (focus == fgWnd) return false;
                if (LooksLikeBogusCaret(r, focus, fgWnd)) return false;

                rect = r;
                return true;
            }
            catch
            {
                rect = Rectangle.Empty;
                return false;
            }
            finally
            {
                if (attached)
                {
                    try { Native.AttachThreadInput(currentThreadId, targetThreadId, false); }
                    catch { }
                }
            }
        }

        static bool IsNearWindow(IntPtr hwnd, Rectangle rect, int tolerance)
        {
            Rectangle wr;
            if (!TryGetWindowRect(hwnd, out wr)) return false;
            wr.Inflate(tolerance, tolerance);
            return wr.Contains(rect.Location);
        }

        /// <summary>光标高度：从焦点窗口的字体估算（拿不到就用 16px）。只影响弹窗放在光标上/下方的距离。</summary>
        static int GetCaretHeightPixels(IntPtr focus)
        {
            try
            {
                if (focus == IntPtr.Zero) return 16;
                IntPtr hfont = Native.SendMessage(focus, Native.WM_GETFONT, IntPtr.Zero, IntPtr.Zero);
                if (hfont != IntPtr.Zero)
                {
                    Native.LOGFONT lf = new Native.LOGFONT();
                    int got = Native.GetObject(hfont, Marshal.SizeOf(typeof(Native.LOGFONT)), ref lf);
                    if (got > 0)
                    {
                        int h = Math.Abs(lf.lfHeight);
                        if (h >= 6 && h <= 300) return h;
                    }
                }
            }
            catch
            {
            }
            return 16;
        }

        // ------------------------------------------------------------------
        // 小工具
        // ------------------------------------------------------------------

        static bool IsPlausibleSize(Rectangle r)
        {
            if (r.Width < 0 || r.Height < 0) return false;
            if (r.Width > 8) return false;              // 光标通常 1-2px 宽，最多容忍 8px
            if (r.Height < 6 || r.Height > 300) return false;
            if (r.Width == 0 && r.Height == 0) return false;
            return true;
        }

        static Rectangle Normalize(Rectangle r)
        {
            int l = Math.Min(r.Left, r.Right);
            int t = Math.Min(r.Top, r.Bottom);
            int w = Math.Abs(r.Width);
            int h = Math.Abs(r.Height);
            if (w <= 0) w = 1;
            if (h <= 0) h = 1;
            return new Rectangle(l, t, w, h);
        }

        static Rectangle RectFrom(Native.RECT r)
        {
            return Normalize(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
        }

        static Point CenterOf(Rectangle r)
        {
            return new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        }

        static Rectangle Inflate(Rectangle r, int amount)
        {
            r.Inflate(amount, amount);
            return r;
        }

        static bool TryGetWindowRect(IntPtr hwnd, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                Native.RECT r;
                if (!Native.GetWindowRect(hwnd, out r)) return false;
                rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                return rect.Width > 0 && rect.Height > 0;
            }
            catch
            {
                return false;
            }
        }

        static bool IsInsideWindow(IntPtr hwnd, Rectangle candidate)
        {
            Rectangle wr;
            if (!TryGetWindowRect(hwnd, out wr)) return false;
            return wr.Contains(candidate.Location) || wr.IntersectsWith(candidate);
        }

        static bool TryClientToScreen(IntPtr hwnd, int x, int y, out Point screen)
        {
            screen = Point.Empty;
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                Native.POINT p = new Native.POINT();
                p.X = x;
                p.Y = y;
                if (!Native.ClientToScreen(hwnd, ref p)) return false;
                screen = new Point(p.X, p.Y);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Win32 互操作
        // ------------------------------------------------------------------

        static class Native
        {
            public const int WM_GETFONT = 0x0031;

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct GUITHREADINFO
            {
                public int cbSize;
                public uint flags;
                public IntPtr hwndActive;
                public IntPtr hwndFocus;
                public IntPtr hwndCapture;
                public IntPtr hwndMenuOwner;
                public IntPtr hwndMoveSize;
                public IntPtr hwndCaret;
                public RECT rcCaret;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct LOGFONT
            {
                public int lfHeight;
                public int lfWidth;
                public int lfEscapement;
                public int lfOrientation;
                public int lfWeight;
                public byte lfItalic;
                public byte lfUnderline;
                public byte lfStrikeOut;
                public byte lfCharSet;
                public byte lfOutPrecision;
                public byte lfClipPrecision;
                public byte lfQuality;
                public byte lfPitchAndFamily;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                public string lfFaceName;
            }

            [DllImport("user32.dll")]
            public static extern IntPtr GetForegroundWindow();

            [DllImport("user32.dll")]
            public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

            [DllImport("user32.dll")]
            public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

            [DllImport("user32.dll")]
            public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

            [DllImport("user32.dll")]
            public static extern bool GetCaretPos(out POINT lpPoint);

            [DllImport("user32.dll")]
            public static extern IntPtr GetFocus();

            [DllImport("user32.dll", EntryPoint = "SendMessageW")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetObjectW")]
            public static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref LOGFONT lpvObject);

            [DllImport("kernel32.dll")]
            public static extern uint GetCurrentThreadId();
        }
    }
}
