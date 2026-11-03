using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// 真机诊断模式（Lead 维护）：
    ///   --detect-test：在弹出的测试窗口里自动输入 nihao / 切 Shift / 输入 hello，
    ///                  记录 SogouStateObserver 是否识别到中文组词，验证模式识别链路。
    ///   --ui-test    ：弹出候选窗 2 秒，验证不抢焦点。
    /// 两者都不会向其它程序发按键（每一步都校验前台窗口是自己）。
    /// </summary>
    internal static class TestModes
    {
        // ------------------------------------------------------------------
        // --detect-test
        // ------------------------------------------------------------------
        internal static void RunDetectTest(string appDir)
        {
            ApplyWinFormsInit();
            StringBuilder log = new StringBuilder();
            log.AppendLine("== detect-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==");
            DetectForm form = new DetectForm(log, appDir);
            Application.Run(form);
        }

        internal static void RunUiTest(AppConfig cfg, string appDir)
        {
            ApplyWinFormsInit();
            StringBuilder log = new StringBuilder();
            log.AppendLine("== ui-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==");
            try
            {
                CandidateForm popup = new CandidateForm(cfg);
                IntPtr before = GetForegroundWindow();
                List<Candidate> list = new List<Candidate>();
                list.Add(new Candidate("hello", "hello", CandidateKind.Word, 900000, "test"));
                list.Add(new Candidate("help", "help", CandidateKind.Word, 800000, "test"));
                list.Add(new Candidate("thank you", "thank you", CandidateKind.Phrase, 700000, "test"));
                popup.ShowCandidates(list, new Point(400, 400), false);
                Application.DoEvents();
                System.Threading.Thread.Sleep(500);
                IntPtr after = GetForegroundWindow();
                log.AppendLine("fgBefore=0x" + before.ToString("X") + " fgAfter=0x" + after.ToString("X"));
                log.AppendLine("focusStolen=" + (before != after));
                log.AppendLine("candidatePopupVisible=" + popup.PopupVisible + " count=" + popup.Count);
                popup.HidePopup();
                log.AppendLine("hidden ok, PopupVisible=" + popup.PopupVisible);
                popup.Dispose();
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION: " + ex);
            }
            WriteLog(appDir, "ui-test.log", log.ToString());
            Environment.Exit(0);
        }

        internal static void ApplyWinFormsInit()
        {
            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }

        internal static void RunCaretTest(string appDir)
        {
            StringBuilder log = new StringBuilder();
            log.AppendLine("== caret-test " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==");
            try
            {
                IntPtr fg = GetForegroundWindow();
                log.AppendLine("foreground=0x" + fg.ToString("X") + " class=" + ClassName(fg) + " title=[" + WindowTitle(fg) + "]");
                Rectangle caret;
                bool ok = CaretLocator.TryGetCaretRect(out caret);
                log.AppendLine("TryGetCaretRect -> " + ok + " rect=" + caret);
                Point fallback = CaretLocator.GetFallbackAnchor();
                log.AppendLine("GetFallbackAnchor -> " + fallback);
                log.AppendLine("cursor=" + Cursor.Position);
                // 打印前台窗口 rect，便于判断兜底位置是否合理
                RECT wr;
                if (GetWindowRect(fg, out wr))
                    log.AppendLine("fgRect=(" + wr.Left + "," + wr.Top + "," + wr.Right + "," + wr.Bottom + ")");
                log.AppendLine("screenWorkingArea=" + Screen.PrimaryScreen.WorkingArea);
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION: " + ex);
            }
            WriteLog(appDir, "caret-test.log", log.ToString());
            Environment.Exit(0);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT rect);

        static string WindowTitle(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(256);
            GetWindowTextW(h, sb, 256);
            return sb.ToString();
        }

        internal static void WriteLog(string appDir, string name, string text)
        {
            try
            {
                File.WriteAllText(Path.Combine(appDir, name), text, Encoding.UTF8);
            }
            catch
            {
            }
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, name), text, Encoding.UTF8);
            }
            catch
            {
            }
        }

        // ------------------------------------------------------------------
        // 检测用窗体
        // ------------------------------------------------------------------
        sealed class DetectForm : Form
        {
            readonly TextBox tb;
            readonly StringBuilder log;
            readonly string appDir;
            readonly System.Windows.Forms.Timer timer;
            readonly SogouStateObserver observer = new SogouStateObserver(new string[] { "SoPY_Comp", "SoPY_UI" }, 40);
            int step = -1;
            bool aborted;

            public DetectForm(StringBuilder log, string appDir)
            {
                this.log = log;
                this.appDir = appDir;
                Text = "英文候选助手 · 模式识别测试（自动关闭）";
                Width = 520;
                Height = 220;
                StartPosition = FormStartPosition.CenterScreen;
                TopMost = true;
                tb = new TextBox();
                tb.Multiline = true;
                tb.Dock = DockStyle.Fill;
                tb.Font = new Font("Consolas", 12f);
                Controls.Add(tb);
                timer = new System.Windows.Forms.Timer();
                timer.Interval = 550;
                timer.Tick += OnTick;
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                ForceForeground();
                tb.Focus();
                observer.Start();
                log.AppendLine("observer started, SogouDetected=" + observer.SogouDetected);
                timer.Start();
            }

            void ForceForeground()
            {
                IntPtr fg = GetForegroundWindow();
                uint fgThread = GetWindowThreadProcessId(fg, out _);
                uint curThread = GetCurrentThreadId();
                bool attached = false;
                try
                {
                    attached = AttachThreadInput(curThread, fgThread, true);
                    SetForegroundWindow(Handle);
                    BringWindowToTop(Handle);
                }
                catch
                {
                }
                finally
                {
                    if (attached) AttachThreadInput(curThread, fgThread, false);
                }
            }

            bool IsSelfForeground()
            {
                return GetForegroundWindow() == Handle;
            }

            void OnTick(object sender, EventArgs e)
            {
                step++;
                try
                {
                    switch (step)
                    {
                        case 0:
                            log.AppendLine("[0] focusSelf=" + IsSelfForeground() + " fgClass=" + ClassName(GetForegroundWindow()));
                            if (!IsSelfForeground()) { aborted = true; log.AppendLine("ABORT: 测试窗口没有拿到前台焦点，不发送任何按键。"); Finish(); return; }
                            log.AppendLine("[0] type nihao (当前输入法模式未知，可能是中文或英文)");
                            TypeAscii("nihao");
                            break;
                        case 1:
                            logState("[1] after nihao");
                            break;
                        case 2:
                            log.AppendLine("[2] press Escape, then Shift (toggle mode)");
                            Key(0x1B);
                            System.Threading.Thread.Sleep(120);
                            Key(0x10);
                            break;
                        case 3:
                            log.AppendLine("[3] type hello");
                            if (!Guard()) return;
                            TypeAscii("hello");
                            break;
                        case 4:
                            logState("[4] after hello");
                            break;
                        case 5:
                            log.AppendLine("[5] press Shift (restore mode), Escape");
                            Key(0x10);
                            System.Threading.Thread.Sleep(120);
                            Key(0x1B);
                            break;
                        case 6:
                            logState("[6] final");
                            Finish();
                            return;
                    }
                }
                catch (Exception ex)
                {
                    log.AppendLine("EXCEPTION: " + ex);
                    Finish();
                }
            }

            bool Guard()
            {
                if (!IsSelfForeground())
                {
                    log.AppendLine("ABORT: 前台焦点已不是测试窗口，停止发送按键。fgClass=" + ClassName(GetForegroundWindow()));
                    aborted = true;
                    Finish();
                    return false;
                }
                return true;
            }

            void logState(string tag)
            {
                log.AppendLine(tag + " fgSelf=" + IsSelfForeground() + " text=[" + tb.Text + "]");
                log.AppendLine(tag + " SogouDetected=" + observer.SogouDetected +
                              " ChineseComposing=" + observer.ChineseComposing +
                              " lastSignal=" + observer.LastChineseSignalUtc.ToString("HH:mm:ss.fff") +
                              " detail=" + observer.LastDetail);
                log.AppendLine(tag + " visibleSoPY=" + VisibleSoPYWindows());
            }

            string VisibleSoPYWindows()
            {
                List<string> found = new List<string>();
                EnumWindows(delegate (IntPtr h, IntPtr lp)
                {
                    if (!IsWindowVisible(h)) return true;
                    StringBuilder cls = new StringBuilder(128);
                    GetClassNameW(h, cls, 128);
                    string c = cls.ToString();
                    if (c.StartsWith("SoPY_", StringComparison.Ordinal) || c.StartsWith("Sogou_", StringComparison.Ordinal))
                        found.Add(c + "@0x" + h.ToString("X"));
                    return true;
                }, IntPtr.Zero);
                return "[" + string.Join(", ", found.ToArray()) + "]";
            }

            void Finish()
            {
                timer.Stop();
                try { observer.Stop(); } catch { }
                log.AppendLine("RESULT: " + (aborted ? "ABORTED" : "DONE"));
                TestModes.WriteLog(appDir, "detect-test.log", log.ToString());
                Environment.Exit(aborted ? 2 : 0);
            }

            void TypeAscii(string s)
            {
                foreach (char c in s)
                {
                    if (!IsSelfForeground()) { aborted = true; log.AppendLine("ABORT(mid-typing): 焦点丢失"); Finish(); return; }
                    byte vk = (byte)char.ToUpperInvariant(c);
                    Key(vk);
                    System.Threading.Thread.Sleep(55);
                }
                Application.DoEvents();
            }

            void Key(byte vk)
            {
                keybd_event(vk, 0, 0, IntPtr.Zero);
                System.Threading.Thread.Sleep(20);
                keybd_event(vk, 0, 2, IntPtr.Zero);
            }

            protected override void OnFormClosed(FormClosedEventArgs e)
            {
                base.OnFormClosed(e);
                try { observer.Dispose(); } catch { }
                try { timer.Dispose(); } catch { }
            }
        }

        // ------------------------------------------------------------------
        // Win32
        // ------------------------------------------------------------------
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);

        static string ClassName(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(null)";
            StringBuilder sb = new StringBuilder(128);
            GetClassNameW(h, sb, 128);
            return sb.ToString();
        }
    }
}
