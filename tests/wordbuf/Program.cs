// EngCand 核心输入层自测（core-dev 的 task-2 验收测试）。
//
// 覆盖：
//  1. WordBuffer 纯逻辑行为序列（不依赖窗口/前台焦点，安全）。
//  2. SogouStateObserver：本进程注册一个类名 "SoPY_Comp" 的可见小窗口（屏幕外 8x8），
//     验证轮询能识别"正在组词"、隐藏后恢复、Changed 事件、前缀过滤、Stop/Dispose 幂等。
//  3. KeyboardHook：Install/Uninstall 幂等（不注入任何按键）。
//  4. TextInjector + KeyboardHook 注入事件：只在"本测试窗口确实位于前台且持有焦点"时执行，
//     否则 SKIP —— 绝不向其它程序注入字符。验证 WM_CHAR 序列、代理对、退格、TextBox 端到端。
//
// 构建：dotnet build tests\wordbuf\EngCandCoreTests.csproj -c Debug
// 运行：bin\Debug\net10.0-windows\win-x64\EngCandCoreTests.exe [logPath]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using EngCand;

namespace EngCandCoreTests
{
    internal static class Program
    {
        const int VK_CAPITAL = 0x14;
        const int VK_BACK = 0x08;

        static readonly StringBuilder log = new StringBuilder();
        static int passed;
        static int failed;
        static int skipped;
        static bool capsLockOn;

        [DllImport("user32.dll")]
        static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        // 只把前台焦点强制切到"本测试进程自己的窗口"；失败就跳过注入测试，绝不注入到其它程序。
        static bool ForceForeground(IntPtr hwnd)
        {
            try
            {
                if (GetForegroundWindow() == hwnd) return true;
                SetForegroundWindow(hwnd);
                if (GetForegroundWindow() == hwnd) return true;

                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                uint fgPid;
                uint fgThread = GetWindowThreadProcessId(fg, out fgPid);
                uint myThread = GetCurrentThreadId();
                if (fgThread == 0 || fgThread == myThread) return GetForegroundWindow() == hwnd;
                if (!AttachThreadInput(myThread, fgThread, true)) return false;
                try
                {
                    SetForegroundWindow(hwnd);
                    SetFocus(hwnd);
                }
                finally
                {
                    AttachThreadInput(myThread, fgThread, false);
                }
                return GetForegroundWindow() == hwnd;
            }
            catch
            {
                return false;
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string logPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "core-tests.log");
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            Application.EnableVisualStyles();
            capsLockOn = (GetKeyState(VK_CAPITAL) & 1) != 0;

            log.AppendLine("EngCand core input-layer tests (core-dev / task-2)");
            log.AppendLine("time=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                           "  capsLock=" + capsLockOn +
                           "  foreground=0x" + GetForegroundWindow().ToString("X"));

            TestWordBuffer();
            TestSogouObserver();
            TestKeyboardHookInstall();
            TestTextInjectorStructure();
            TestInjectionAndHookEvents();

            log.AppendLine();
            log.AppendLine("RESULT: " + (failed == 0 ? "PASS" : "FAIL") +
                           "  passed=" + passed + " failed=" + failed + " skipped=" + skipped);
            string text = log.ToString();
            Console.Write(text);
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(logPath, text, Encoding.UTF8);
                Console.WriteLine("log written: " + Path.GetFullPath(logPath));
            }
            catch (Exception ex)
            {
                Console.WriteLine("log write failed: " + ex.Message);
            }
            return failed == 0 ? 0 : 1;
        }

        // ==================================================================
        // 1) WordBuffer
        // ==================================================================
        static void TestWordBuffer()
        {
            Note("== WordBuffer ==");
            Note("CapsLock=" + capsLockOn + "（大写测试按 Shift XOR CapsLock 取期望值）");

            // --- 基本字母 ---
            WordBuffer b = new WordBuffer();
            Type(b, "hao");
            Check("wb_letters_hao", b.CurrentWord == "hao" && !b.IsEmpty, State(b));

            b = new WordBuffer();
            Type(b, "nihao");
            Check("wb_letters_nihao", b.CurrentWord == "nihao", State(b));

            // --- 空格提交 ---
            b = new WordBuffer();
            Type(b, "hao");
            b.OnKey(Key(0x20));
            TypingState s = b.Snapshot();
            Check("wb_space_commit", s.CurrentWord == "" && s.PreviousWord == "hao" &&
                                     s.AtWordStartAfterSpace && b.IsEmpty, State(b));

            // --- PreviousWord2 ---
            b = new WordBuffer();
            Type(b, "thank"); b.OnKey(Key(0x20));
            Type(b, "you"); b.OnKey(Key(0x20));
            s = b.Snapshot();
            Check("wb_prev2", s.PreviousWord == "you" && s.PreviousWord2 == "thank" && s.CurrentWord == "",
                  State(b));

            // --- 首字母大写标记 ---
            b = new WordBuffer();
            Type(b, "hello", true);
            s = b.Snapshot();
            Check("wb_capitalized_flag", s.CurrentWord == "hello" && s.CurrentWordCapitalized,
                  State(b));
            b.OnKey(Key(0x20));
            s = b.Snapshot();
            Check("wb_capitalized_cleared_after_commit", s.PreviousWord == "hello" && !s.CurrentWordCapitalized,
                  State(b));
            Type(b, "world");
            Check("wb_lowercase_not_capitalized", !b.Snapshot().CurrentWordCapitalized, State(b));

            // --- 撇号 don't ---
            b = new WordBuffer();
            Type(b, "don");
            b.OnKey(Key(0xDE));           // VK_OEM_QUOTES 无 Shift = '
            Type(b, "t");
            Check("wb_apostrophe_dont", b.CurrentWord == "don't", State(b));

            // --- 连字符 well-known ---
            b = new WordBuffer();
            Type(b, "well");
            b.OnKey(Key(0xBD));           // VK_OEM_MINUS 无 Shift = -
            Type(b, "known");
            Check("wb_hyphen_wellknown", b.CurrentWord == "well-known", State(b));

            // --- 退格 ---
            b = new WordBuffer();
            Type(b, "world");
            b.OnKey(Key(VK_BACK));
            b.OnKey(Key(VK_BACK));
            Check("wb_backspace_2", b.CurrentWord == "wor", State(b));
            Type(b, "ld");
            for (int i = 0; i < 5; i++) b.OnKey(Key(VK_BACK));
            Check("wb_backspace_all", b.CurrentWord == "" && !b.Snapshot().CurrentWordCapitalized, State(b));

            // --- 退格跨词边界（尽力） ---
            b = new WordBuffer();
            Type(b, "hello");
            b.OnKey(Key(0x20));
            b.OnKey(Key(VK_BACK));        // 空缓冲退格 -> 回到 hello 并删一个字符
            Check("wb_backspace_cross_word1", b.CurrentWord == "hell" && b.Snapshot().PreviousWord == "",
                  State(b));
            b.OnKey(Key(VK_BACK));
            Check("wb_backspace_cross_word2", b.CurrentWord == "hel", State(b));

            // --- 注入键 / KeyUp 必须忽略 ---
            b = new WordBuffer();
            Type(b, "cat");
            b.OnKey(Key(0x58, true, true));   // X，IsInjected=true
            b.OnKey(Key(0x59, false));        // Y，KeyUp
            Check("wb_ignored_injected_and_keyup", b.CurrentWord == "cat", State(b));

            // --- 数字：提交当前词，不进入缓冲 ---
            b = new WordBuffer();
            Type(b, "hello");
            b.OnKey(Key(0x35));               // '5'
            s = b.Snapshot();
            Check("wb_digit_commits", s.CurrentWord == "" && s.PreviousWord == "hello", State(b));
            b = new WordBuffer();
            b.OnKey(Key(0x35));
            Check("wb_digit_empty_nochange", b.CurrentWord == "" && b.Snapshot().PreviousWord == "", State(b));
            Type(b, "abc");
            Check("wb_digit_then_letters", b.CurrentWord == "abc", State(b));

            // --- 标点提交 + SentenceStart ---
            b = new WordBuffer();
            Type(b, "hello");
            b.OnKey(Key(0xBC));               // ','
            s = b.Snapshot();
            Check("wb_comma_commit", s.PreviousWord == "hello" && !s.SentenceStart, State(b));

            b = new WordBuffer();
            Type(b, "world");
            b.OnKey(Key(0xBE));               // '.'
            s = b.Snapshot();
            Check("wb_period_sentence_start", s.PreviousWord == "world" && s.SentenceStart, State(b));
            b.OnKey(Key(0x20));               // 契约：其它分隔符（含空格）之后 SentenceStart=false
            Check("wb_space_after_period_sentence_false", !b.Snapshot().SentenceStart, State(b));

            b = new WordBuffer();
            Type(b, "are");
            b.OnKey(Key(0xBF, true, false, true));   // Shift+/ = '?'
            Check("wb_question_sentence_start", b.Snapshot().SentenceStart, State(b));

            b = new WordBuffer();
            Type(b, "wow");
            b.OnKey(Key(0x31, true, false, true));   // Shift+1 = '!'
            Check("wb_exclam_sentence_start", b.Snapshot().SentenceStart, State(b));

            // --- Tab / Enter 提交 ---
            b = new WordBuffer();
            Type(b, "hi");
            b.OnKey(Key(0x09));               // Tab
            Type(b, "there");
            b.OnKey(Key(0x0D));               // Enter
            s = b.Snapshot();
            Check("wb_tab_enter_commit", s.PreviousWord == "there" && s.PreviousWord2 == "hi", State(b));

            // --- 小键盘数字（0x60-0x69）也按"其它键"提交 ---
            b = new WordBuffer();
            Type(b, "hello");
            b.OnKey(Key(0x66));               // 小键盘 6
            Check("wb_numpad_digit_commits", b.Snapshot().PreviousWord == "hello",
                  State(b));
            b = new WordBuffer();
            Type(b, "abc");
            b.OnKey(Key(0x6D));               // 小键盘 -
            Check("wb_numpad_minus_commits", b.Snapshot().PreviousWord == "abc", State(b));

            // --- Esc / 方向键 / 功能键忽略 ---
            b = new WordBuffer();
            Type(b, "abc");
            b.OnKey(Key(0x1B));               // Esc
            b.OnKey(Key(0x25));               // Left
            b.OnKey(Key(0x28));               // Down
            b.OnKey(Key(0x74));               // F5
            b.OnKey(Key(0x2E));               // Delete
            Check("wb_ignore_esc_arrows_fkeys", b.CurrentWord == "abc" && b.Snapshot().PreviousWord == "",
                  State(b));

            // --- Ctrl 组合键不污染缓冲 ---
            b = new WordBuffer();
            Type(b, "c");
            b.OnKey(Key(0x43, true, false, false, true));  // Ctrl+C
            b.OnKey(Key(0x56, true, false, false, true));  // Ctrl+V
            Check("wb_ctrl_shortcut_ignored", b.CurrentWord == "c", State(b));

            // --- Snapshot 是副本 ---
            b = new WordBuffer();
            Type(b, "abc");
            TypingState snap = b.Snapshot();
            Type(b, "def");
            Check("wb_snapshot_is_copy", snap.CurrentWord == "abc" && b.CurrentWord == "abcdef", State(b));

            // --- Reset ---
            b = new WordBuffer();
            Type(b, "hello"); b.OnKey(Key(0x20));
            b.Reset();
            s = b.Snapshot();
            Check("wb_reset", b.IsEmpty && s.CurrentWord == "" && s.PreviousWord == "" && s.PreviousWord2 == "" &&
                             !s.AtWordStartAfterSpace && s.SentenceStart && !s.CurrentWordCapitalized, State(b));
        }

        static HookKeyEventArgs Key(int vk, bool down = true, bool injected = false, bool shift = false, bool ctrl = false)
        {
            HookKeyEventArgs e = new HookKeyEventArgs();
            e.VkCode = vk;
            e.Key = (Keys)vk;
            e.IsDown = down;
            e.IsInjected = injected;
            e.Shift = shift;
            e.Ctrl = ctrl;
            return e;
        }

        // 打一个小写字母。Shift 的取值保证"实际输出小写"（Shift XOR CapsLock = 0）。
        static void Type(WordBuffer b, string s, bool firstUpper = false)
        {
            for (int i = 0; i < s.Length; i++)
            {
                bool upper = firstUpper && i == 0;
                bool shift = upper ? !capsLockOn : capsLockOn;
                b.OnKey(Key(char.ToUpperInvariant(s[i]), true, false, shift));
            }
        }

        static string State(WordBuffer b)
        {
            TypingState s = b.Snapshot();
            return "cur=[" + s.CurrentWord + "] prev=[" + s.PreviousWord + "] prev2=[" + s.PreviousWord2 +
                   "] atStart=" + s.AtWordStartAfterSpace + " sentStart=" + s.SentenceStart +
                   " cap=" + s.CurrentWordCapitalized;
        }

        // ==================================================================
        // 2) SogouStateObserver（用自己进程的假 SoPY_Comp 窗口验证）
        // ==================================================================
        static void TestSogouObserver()
        {
            Note("== SogouStateObserver ==");
            FakeSogouWindow fake = null;
            SogouStateObserver obs = null;
            try
            {
                obs = new SogouStateObserver(null, 40);
                int changes = 0;
                obs.Changed += delegate { changes++; };
                obs.Start();                 // Start 内部会先同步跑一轮
                Pump(120);
                Note("startup(real desktop): composing=" + obs.ChineseComposing +
                     " detected=" + obs.SogouDetected +
                     " detail=[" + obs.LastDetail + "] changes=" + changes);

                fake = new FakeSogouWindow("SoPY_Comp", 8, 8);
                Pump(250);
                Check("sogou_composing_detected", obs.ChineseComposing,
                      "composing=" + obs.ChineseComposing + " detail=[" + obs.LastDetail + "] changes=" + changes);
                Check("sogou_detail_has_class", obs.LastDetail != null &&
                      obs.LastDetail.IndexOf("SoPY_Comp", StringComparison.Ordinal) >= 0,
                      "detail=[" + obs.LastDetail + "]");
                Check("sogou_last_signal_fresh",
                      (DateTime.UtcNow - obs.LastChineseSignalUtc).TotalMilliseconds < 1500,
                      "LastChineseSignalUtc=" + obs.LastChineseSignalUtc.ToString("HH:mm:ss.fff") + "Z");
                Check("sogou_detected_true", obs.SogouDetected, "detected=" + obs.SogouDetected);
                Check("sogou_changed_fired", changes >= 1, "changes=" + changes);

                fake.Hide();
                Pump(250);
                Check("sogou_after_hide_false", !obs.ChineseComposing,
                      "composing=" + obs.ChineseComposing + " changes=" + changes);
                Check("sogou_detected_sticky", obs.SogouDetected, "detected=" + obs.SogouDetected);
                DateTime lastSignal = obs.LastChineseSignalUtc;

                fake.ShowNoActivate();
                Pump(250);
                Check("sogou_after_show_true", obs.ChineseComposing,
                      "composing=" + obs.ChineseComposing + " changes=" + changes);
                Check("sogou_signal_refreshed", obs.LastChineseSignalUtc >= lastSignal,
                      "before=" + lastSignal.ToString("HH:mm:ss.fff") + " after=" +
                      obs.LastChineseSignalUtc.ToString("HH:mm:ss.fff"));
                Check("sogou_changed_per_transition", changes >= 3, "changes=" + changes + " (期望 true,false,true)");

                // 前缀过滤：可见窗口类名不匹配配置前缀 -> 不算组词；但 SoPY_* 仍应被检测到
                SogouStateObserver other = new SogouStateObserver(new string[] { "NotSogouPrefix" }, 40);
                try
                {
                    other.Start();
                    Pump(150);
                    Check("sogou_prefix_filter", !other.ChineseComposing && other.SogouDetected,
                          "composing=" + other.ChineseComposing + " detected=" + other.SogouDetected);
                    other.Stop();
                    other.Stop();
                    other.Dispose();
                    other.Dispose();
                    Check("sogou_stop_dispose_idempotent", true, "两次 Stop/Dispose 未抛异常");
                }
                finally
                {
                    try { other.Stop(); other.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Check("sogou_no_exception", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (obs != null)
                {
                    try { obs.Stop(); obs.Stop(); obs.Dispose(); obs.Dispose(); }
                    catch (Exception ex) { Check("sogou_dispose_no_throw", false, ex.Message); }
                }
                if (fake != null) fake.Dispose();
            }
        }

        // ==================================================================
        // 3) KeyboardHook 安装/幂等（不注入按键）
        // ==================================================================
        static void TestKeyboardHookInstall()
        {
            Note("== KeyboardHook install/uninstall（不注入按键）==");
            KeyboardHook hook = new KeyboardHook();
            try
            {
                try
                {
                    hook.Install();
                }
                catch (Exception ex)
                {
                    Skip("hook_install", "SetWindowsHookEx(WH_KEYBOARD_LL) 失败：" + ex.Message);
                    return;
                }
                Check("hook_installed", hook.Installed, "Installed=" + hook.Installed);
                hook.Install();
                Check("hook_install_twice_ok", hook.Installed, "Installed=" + hook.Installed);
                hook.Uninstall();
                Check("hook_uninstalled", !hook.Installed, "Installed=" + hook.Installed);
                hook.Uninstall();
                hook.Uninstall();
                Check("hook_uninstall_twice_ok", !hook.Installed, "Installed=" + hook.Installed);
                hook.Install();
                Check("hook_reinstall_ok", hook.Installed, "Installed=" + hook.Installed);
                hook.Uninstall();
                Check("hook_reuninstall_ok", !hook.Installed, "Installed=" + hook.Installed);
                hook.Dispose();
                hook.Dispose();
                Check("hook_dispose_idempotent", !hook.Installed, "Dispose 两次未抛异常");
            }
            catch (Exception ex)
            {
                Check("hook_no_exception", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ==================================================================
        // 4) TextInjector 结构自检（不注入任何按键）
        // ==================================================================
        static void TestTextInjectorStructure()
        {
            Note("== TextInjector 结构自检（不注入按键）==");
            Type inputType = typeof(TextInjector).GetNestedType("INPUT",
                System.Reflection.BindingFlags.NonPublic);
            int size = inputType == null ? -1 : Marshal.SizeOf(inputType);
            int expected = IntPtr.Size == 8 ? 40 : 28;
            Check("injector_input_struct_size", size == expected,
                  "Marshal.SizeOf(INPUT)=" + size + " 期望=" + expected +
                  "（与 native sizeof(INPUT) 不一致时 SendInput 会直接失败）");
            System.Reflection.MethodInfo replace = typeof(TextInjector).GetMethod("ReplaceText",
                new Type[] { typeof(int), typeof(string), typeof(int) });
            System.Reflection.MethodInfo typeUnicode = typeof(TextInjector).GetMethod("TypeUnicode",
                new Type[] { typeof(string), typeof(int) });
            Check("injector_public_api", replace != null && typeUnicode != null &&
                  replace.IsStatic && typeUnicode.IsStatic,
                  "ReplaceText(int,string,int)/TypeUnicode(string,int) 存在且为 static");
        }

        // ==================================================================
        // 5) TextInjector + 注入事件观察（仅在本窗口拥有前台焦点时执行）
        // ==================================================================
        static void TestInjectionAndHookEvents()
        {
            Note("== TextInjector + KeyboardHook injected-event（仅本窗口为前台时执行）==");
            CaptureForm form = new CaptureForm();
            try
            {
                form.Show();
                form.Activate();
                ForceForeground(form.Handle);
                Application.DoEvents();
                Thread.Sleep(150);
                Application.DoEvents();
                IntPtr fg = GetForegroundWindow();
                if (fg != form.Handle)
                {
                    Skip("foreground_injection",
                         "前台不是本测试窗口（foreground=0x" + fg.ToString("X") +
                         " testWindow=0x" + form.Handle.ToString("X") + "），为避免向其它程序注入按键，跳过注入测试");
                    return;
                }

                form.FocusCapture();
                Application.DoEvents();

                KeyboardHook hook = new KeyboardHook();
                int injectedDownCount = 0;
                hook.KeyDown += delegate(object sender, HookKeyEventArgs e)
                {
                    if (e.IsInjected) injectedDownCount++;
                };
                hook.Install();
                try
                {
                    // --- 4a. TypeUnicode: BMP + 代理对 ---
                    form.ResetCapture();
                    bool ok1 = TextInjector.TypeUnicode("a\uD83D\uDE00");   // a + 😀
                    Pump(350);
                    Check("inject_unicode_return", ok1, "TypeUnicode(\"a😀\")=" + ok1);
                    string chars = form.CapturedCharsText();
                    Check("inject_unicode_wmchar", chars == "a,0xD83D,0xDE00",
                          "WM_CHAR=[" + chars + "]（期望 a,0xD83D,0xDE00）");
                    Check("hook_saw_injected_unicode", injectedDownCount >= 3,
                          "注入 KeyDown 事件数=" + injectedDownCount + "（3 个 UTF-16 code unit）");

                    // --- 4b. ReplaceText: 退格 + 字符 ---
                    form.ResetCapture();
                    bool ok2 = TextInjector.ReplaceText(2, "xy");
                    Pump(350);
                    Check("inject_replace_return", ok2, "ReplaceText(2,\"xy\")=" + ok2);
                    Check("inject_replace_backspaces", form.CapturedVkDownCount(VK_BACK) == 2,
                          "VK_BACK down=" + form.CapturedVkDownCount(VK_BACK) + "（期望 2）");
                    // VK_BACK 在 TextBox 中同样产生 WM_CHAR 0x08，所以只断言字符部分末尾是 x,y
                    string repChars = form.CapturedCharsText();
                    Check("inject_replace_chars", repChars == "x,y" || repChars.EndsWith("x,y"),
                          "WM_CHAR=[" + repChars + "]（期望 x,y 或 0x0008,...,x,y）");

                    // --- 4c. TextBox 端到端 ---
                    form.SetupTextBox("hello");
                    Pump(200);
                    if (!form.TextBoxFocused())
                    {
                        Skip("inject_textbox", "TextBox 未获得焦点");
                    }
                    else
                    {
                        bool ok3 = TextInjector.ReplaceText(2, "p");
                        Pump(350);
                        Check("inject_textbox_e2e", ok3 && form.TextBoxText == "help",
                              "TextBox.Text=[" + form.TextBoxText + "] ok=" + ok3 +
                              "（期望 [help]：hello -2 退格 +p）");
                    }

                    // --- 4d. delayMs 参数路径 ---
                    form.ResetCapture();
                    bool ok4 = TextInjector.TypeUnicode("z", 5);
                    // CI 环境下消息泵回放可能更慢：轮询等待字符到达（最多 2 秒）
                    string delayed = "";
                    for (int w = 0; w < 40; w++)
                    {
                        Pump(50);
                        delayed = form.CapturedCharsText();
                        if (delayed.Length > 0) break;
                    }
                    Check("inject_delay_path", ok4 && delayed == "z",
                          "WM_CHAR=[" + delayed + "] ok=" + ok4);
                }
                finally
                {
                    hook.Uninstall();
                }
            }
            catch (Exception ex)
            {
                Check("inject_no_exception", false, ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { form.Close(); } catch { }
            }
        }

        // ==================================================================
        // 工具
        // ==================================================================
        static void Pump(int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
        }

        static void Check(string name, bool ok, string detail)
        {
            if (ok) { passed++; log.AppendLine("PASS  " + name + "  " + detail); }
            else { failed++; log.AppendLine("FAIL  " + name + "  " + detail); }
        }

        static void Skip(string name, string reason)
        {
            skipped++;
            log.AppendLine("SKIP  " + name + "  " + reason);
        }

        static void Note(string text)
        {
            log.AppendLine("---- " + text);
        }
    }

    // ----------------------------------------------------------------------
    // 前台捕获窗口：记录 WM_CHAR / WM_KEYDOWN，验证注入结果
    // ----------------------------------------------------------------------
    internal sealed class CaptureForm : Form
    {
        readonly CaptureControl capture;
        TextBox tb;

        public CaptureForm()
        {
            Text = "EngCand core test";
            StartPosition = FormStartPosition.Manual;
            Size = new Size(420, 140);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(0, wa.Right - Width - 40), Math.Max(0, wa.Bottom - Height - 40));
            TopMost = true;
            ShowInTaskbar = false;

            capture = new CaptureControl();
            Controls.Add(capture);

            Shown += delegate
            {
                Activate();
                capture.Focus();
            };
        }

        public void FocusCapture()
        {
            Activate();
            capture.Focus();
        }

        public void ResetCapture() { capture.Reset(); }

        public string CapturedCharsText() { return capture.CharsText(); }

        public int CapturedVkDownCount(int vk) { return capture.VkDownCount(vk); }

        public void SetupTextBox(string initial)
        {
            if (tb == null)
            {
                tb = new TextBox();
                tb.Dock = DockStyle.Top;
                Controls.Add(tb);
            }
            tb.Visible = true;
            tb.Text = initial ?? "";
            tb.SelectionStart = tb.TextLength;
            tb.SelectionLength = 0;
            tb.Focus();
        }

        public bool TextBoxFocused() { return tb != null && tb.Focused; }

        public string TextBoxText { get { return tb == null ? "" : tb.Text; } }
    }

    internal sealed class CaptureControl : Control
    {
        const int WM_CHAR = 0x0102;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;

        readonly List<int> chars = new List<int>();
        readonly List<int> vkDowns = new List<int>();

        public CaptureControl()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Dock = DockStyle.Fill;
            BackColor = Color.White;
        }

        public void Reset()
        {
            chars.Clear();
            vkDowns.Clear();
        }

        public string CharsText()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < chars.Count; i++)
            {
                if (i > 0) sb.Append(',');
                int c = chars[i];
                if (c >= 0x20 && c < 0x7F) sb.Append((char)c);
                else sb.Append("0x").Append(c.ToString("X4"));
            }
            return sb.ToString();
        }

        public int VkDownCount(int vk)
        {
            int n = 0;
            for (int i = 0; i < vkDowns.Count; i++) if (vkDowns[i] == vk) n++;
            return n;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CHAR)
            {
                chars.Add((int)(long)m.WParam);
            }
            else if (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN)
            {
                vkDowns.Add((int)(long)m.WParam);
            }
            base.WndProc(ref m);
        }
    }

    // ----------------------------------------------------------------------
    // 假搜狗候选窗口：注册类名 SoPY_Comp 的 8x8 屏幕外窗口
    // ----------------------------------------------------------------------
    internal sealed class FakeSogouWindow : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool UnregisterClassW(string className, IntPtr hInstance);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string moduleName);

        static WndProcDelegate wndProcKeepAlive;

        readonly string className;
        readonly IntPtr hInstance;

        public IntPtr Handle { get; private set; }

        public FakeSogouWindow(string className, int width, int height)
        {
            this.className = className;
            hInstance = GetModuleHandleW(null);

            if (wndProcKeepAlive == null)
            {
                wndProcKeepAlive = delegate(IntPtr h, uint m, IntPtr w, IntPtr l)
                {
                    return DefWindowProcW(h, m, w, l);
                };
            }

            WNDCLASSEXW wc = new WNDCLASSEXW();
            wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEXW));
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcKeepAlive);
            wc.hInstance = hInstance;
            wc.lpszClassName = className;
            RegisterClassExW(ref wc);   // 已注册时返回 0，忽略（下面 CreateWindowEx 用同名类）

            const uint WS_POPUP = 0x80000000;
            const uint WS_VISIBLE = 0x10000000;
            const uint WS_EX_TOOLWINDOW = 0x00000080;
            const uint WS_EX_NOACTIVATE = 0x08000000;

            Handle = CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                className, "", WS_POPUP | WS_VISIBLE,
                -1200, -1200, width, height,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (Handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateWindowExW 失败，Win32Error=" + Marshal.GetLastWin32Error());
            }
        }

        public void Hide() { if (Handle != IntPtr.Zero) ShowWindow(Handle, 0); }

        public void ShowNoActivate() { if (Handle != IntPtr.Zero) ShowWindow(Handle, 4); }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                try { DestroyWindow(Handle); } catch { }
                Handle = IntPtr.Zero;
            }
            try { UnregisterClassW(className, hInstance); } catch { }
        }
    }
}
