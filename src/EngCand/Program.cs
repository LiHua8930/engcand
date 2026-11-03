using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// 程序入口与总装线：托盘常驻 + 全局键盘钩子 + 词缓冲 + 搜狗状态观察 + 候选弹窗。
    /// 具体模块实现分散在 Core/ 与 UI/ 目录，本文件只做接线。
    /// </summary>
    internal static class Program
    {
        static Mutex singleInstance;
        static Diagnostics diag;
        static AppConfig cfg;
        static string appDir;
        static string dataDir;

        static CandidateEngine engine;
        static KeyboardHook hook;
        static WordBuffer wordBuffer;
        static SogouStateObserver sogou;
        static CandidateForm popup;
        static TrayController tray;
        static HotkeyManager hotkeys;
        static System.Windows.Forms.Timer popupTimer;
        static System.Windows.Forms.Timer heartbeatTimer;

        static List<Candidate> currentCandidates = new List<Candidate>();
        static bool popupVisible;
        static bool shuttingDown;
        static bool injecting;

        // ---- 中/英文模式跟踪（Shift 点按切换 + 搜狗组词窗口双信号）----
        static bool? trackedChinese;                 // true=中文模式, false=英文模式, null=未知
        static bool shiftIsDown;
        static bool shiftChordUsed;                  // Shift 按住期间是否按过其它键（大写/组合键不算切换）
        static DateTime shiftDownUtc;
        static DateTime lastModeToggleUtc;
        static DateTime lastChineseEvidenceUtc;
        static bool hasChineseEvidence;

        // 自绘界面（微信/浏览器）没有系统插入符：用"最近一次搜狗中文候选条的位置"近似输入位置。
        static IntPtr cachedAnchorHwnd = IntPtr.Zero;
        static Rectangle cachedAnchorRect = Rectangle.Empty;
        static DateTime cachedAnchorUtc = DateTime.MinValue;

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        [STAThread]
        static void Main(string[] args)
        {
            bool selftest = args.Any(a => a == "--selftest");
            bool verbose = args.Any(a => a == "--verbose" || a == "--diagnostics");
            bool allowInjected = args.Any(a => a == "--accept-injected");   // 仅供自动化端到端测试

            appDir = ResolveAppDir(args);
            diag = new Diagnostics(appDir, verbose);
            Diagnostics.Current = diag;

            cfg = ConfigStore.Load(appDir);
            dataDir = FindDataDir();
            diag.Info("=== EngCand 启动 dataDir=" + (dataDir ?? "(未找到)") + " verbose=" + verbose + " ===");

            engine = new CandidateEngine(dataDir, appDir, cfg, diag);
            engine.Initialize();

            if (selftest)
            {
                SelfTest.Run(dataDir, appDir, diag);
                return;
            }
            if (args.Any(a => a == "--detect-test"))
            {
                TestModes.RunDetectTest(appDir);
                return;
            }
            if (args.Any(a => a == "--ui-test"))
            {
                TestModes.RunUiTest(cfg, appDir);
                return;
            }
            if (args.Any(a => a == "--caret-test"))
            {
                TestModes.RunCaretTest(appDir);
                return;
            }

            bool createdNew;
            singleInstance = new Mutex(true, @"Local\EngCand.SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("英文候选助手已经在运行（见任务栏托盘图标）。", "英文候选助手",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            }
            catch
            {
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 候选弹窗
            popup = new CandidateForm(cfg);
            popup.CandidatePicked += OnCandidatePicked;
            popup.Dismissed += delegate { popupVisible = false; };

            // 键盘钩子 + 词缓冲
            wordBuffer = new WordBuffer();
            hook = new KeyboardHook();
            hook.TreatInjectedAsReal = allowInjected;
            hook.KeyDown += OnKeyDown;
            hook.KeyUp += OnKeyUp;

            // 搜狗状态观察（识别“正在用中文组词”）
            sogou = new SogouStateObserver(cfg.SogouComposeWindowPrefixes, 40);
            sogou.Changed += OnSogouChanged;

            // 弹窗延迟计时器
            popupTimer = new System.Windows.Forms.Timer();
            popupTimer.Interval = Math.Max(20, cfg.PopupDelayMs);
            popupTimer.Tick += OnPopupTimerTick;

            // 诊断心跳（仅 --verbose 时写日志，用于排查搜狗状态识别）
            heartbeatTimer = new System.Windows.Forms.Timer();
            heartbeatTimer.Interval = 1000;
            heartbeatTimer.Tick += delegate
            {
                if (sogou == null || diag == null) return;
                diag.Debug("hb composing=" + sogou.ChineseComposing +
                           " age=" + (long)(DateTime.UtcNow - sogou.LastChineseSignalUtc).TotalMilliseconds +
                           " det=" + sogou.LastDetail);
            };
            heartbeatTimer.Start();

            // 托盘
            tray = new TrayController(cfg);
            tray.ToggleEnabledRequested += delegate { ToggleEnabled(); };
            tray.SettingsRequested += delegate { OpenSettings(); };
            tray.DiagnosticsRequested += delegate { OpenDiagnostics(); };
            tray.ExitRequested += delegate { Shutdown(); };
            tray.ModeRequested += OnModeRequested;

            // 热键：Ctrl+Alt+E 开关；Ctrl+Alt+D 诊断信息
            hotkeys = new HotkeyManager();
            hotkeys.ToggleRequested += delegate { ToggleEnabled(); };
            hotkeys.DiagnosticsRequested += delegate { ShowDiagnosticsBalloon(); };

            bool hookOk = false;
            try
            {
                hook.Install();
                hookOk = true;
            }
            catch (Exception ex)
            {
                diag.Error("键盘钩子安装失败: " + ex.Message);
            }
            try
            {
                sogou.Start();
            }
            catch (Exception ex)
            {
                diag.Error("SogouStateObserver 启动失败: " + ex.Message);
            }
            try
            {
                hotkeys.Register();
            }
            catch (Exception ex)
            {
                diag.Error("热键注册失败: " + ex.Message);
            }

            RefreshTrayStatus();

            if (!engine.Ready)
            {
                tray.ShowBalloon("英文候选助手", "没有找到词典数据（data\\words.tsv）。请确认 data 目录和 EngCand.exe 在一起。");
            }
            else if (hookOk)
            {
                tray.ShowBalloon("英文候选助手已启动",
                    "英文模式下打字会弹出候选：数字键 1-9 选词，Esc 关闭。Ctrl+Alt+E 开关，托盘菜单可设置。");
            }

            diag.Info("启动完成 hook=" + hookOk + " sogou=" + sogou.SogouDetected +
                      " words=" + engine.WordCount + " phrases=" + engine.PhraseCount);

            Application.ApplicationExit += delegate { SafeCleanup(); };
            Application.Run(new ApplicationContext());
        }

        // ------------------------------------------------------------------
        // 键盘事件
        // ------------------------------------------------------------------
        static void OnKeyDown(object sender, HookKeyEventArgs e)
        {
            if (injecting) return;
            if (e.IsInjected) return;
            if (shuttingDown) return;

            // Shift 点按跟踪（用于中/英文模式切换判断）：按住期间按过其它键就当作大写/组合键。
            // 带 Ctrl/Alt/Win 的 Shift（如 Ctrl+Shift 切换输入语言）不算中英文切换。
            if (IsShiftVk(e.VkCode))
            {
                if (!shiftIsDown)
                {
                    shiftIsDown = true;
                    shiftChordUsed = e.Ctrl || e.Alt || e.Win;
                    shiftDownUtc = DateTime.UtcNow;
                }
                return;
            }
            if (shiftIsDown) shiftChordUsed = true;

            // 全局热键注册失败时的兜底（正常情况下 RegisterHotKey 生效，这里不会收到 Ctrl+Alt+E）
            if (e.Ctrl && e.Alt && e.Key == Keys.E)
            {
                e.Handled = true;
                ToggleEnabled();
                return;
            }
            if (e.Ctrl && e.Alt && e.Key == Keys.D)
            {
                e.Handled = true;
                ShowDiagnosticsBalloon();
                return;
            }

            if (!cfg.Enabled)
            {
                if (popupVisible) HidePopup();
                return;
            }

            // 中文模式下（搜狗正在组词）：清空英文词缓冲。
            // 否则中文拼音字母（如 nihao）会残留，切回英文后第一次打词会拼成 "nihaohel" 而查不到候选。
            if (IsChineseMode())
            {
                wordBuffer.Reset();
                UpdateAnchorCache();
                if (popupVisible) HidePopup();
                return;
            }

            if (popupVisible)
            {
                if (e.Key == Keys.Escape)
                {
                    HidePopup();
                    e.Handled = true;
                    return;
                }
                int idx = NumberKeyIndex(e);
                if (idx >= 0)
                {
                    e.Handled = true;
                    if (idx < currentCandidates.Count) Pick(idx);
                    else HidePopup();
                    return;
                }
            }

            bool wasPopupVisible = popupVisible;
            wordBuffer.OnKey(e);

            // 显示候选的条件
            TypingState state = wordBuffer.Snapshot();
            bool wantSuggestion;
            if (!cfg.Enabled)
            {
                wantSuggestion = false;
            }
            else if (state.CurrentWord.Length >= cfg.MinPrefixLength)
            {
                wantSuggestion = true;
            }
            else if (cfg.SuggestNextWord && state.AtWordStartAfterSpace && state.PreviousWord.Length >= 2)
            {
                wantSuggestion = true;
            }
            else
            {
                wantSuggestion = false;
            }

            if (!wantSuggestion)
            {
                if (wasPopupVisible) HidePopup();
                return;
            }

            // 中文组词中：立即收起，不弹
            if (IsChineseMode())
            {
                if (wasPopupVisible) HidePopup();
                return;
            }

            schedulePopupRefresh();
        }

        static void OnKeyUp(object sender, HookKeyEventArgs e)
        {
            if (injecting) return;
            if (e.IsInjected) return;
            if (shuttingDown) return;
            if (!IsShiftVk(e.VkCode)) return;

            bool wasDown = shiftIsDown;
            bool chord = shiftChordUsed || e.Ctrl || e.Alt || e.Win;
            double heldMs = (DateTime.UtcNow - shiftDownUtc).TotalMilliseconds;
            shiftIsDown = false;
            shiftChordUsed = false;

            // 短按、且期间没有按其它键 => 用户在用 Shift 切换中/英文（搜狗默认行为）。
            if (wasDown && !chord && heldMs < 400) OnShiftTap();
        }

        /// <summary>Shift 点按：翻转跟踪的中/英文模式，并清空英文词缓冲。</summary>
        static void OnShiftTap()
        {
            bool wasChinese = IsChineseMode();
            trackedChinese = !wasChinese;
            lastModeToggleUtc = DateTime.UtcNow;
            if (trackedChinese.Value)
            {
                lastChineseEvidenceUtc = DateTime.UtcNow;
                hasChineseEvidence = true;
            }
            if (wordBuffer != null) wordBuffer.Reset();
            HidePopup();
            if (diag != null) diag.Debug("shift tap -> trackedChinese=" + trackedChinese.Value +
                                          " composing=" + (sogou != null && sogou.ChineseComposing));
        }

        static bool IsShiftVk(int vk)
        {
            return vk == 0x10 || vk == 0xA0 || vk == 0xA1;
        }

        static void schedulePopupRefresh()
        {
            if (popupTimer == null) return;
            int delay = Math.Max(20, cfg.PopupDelayMs);
            // 模式未知时多等一会，让搜狗组词窗口有机会出现，避免中文模式下误弹候选。
            if (cfg.Mode == "auto" && trackedChinese == null) delay = Math.Max(delay, 220);
            popupTimer.Stop();
            popupTimer.Interval = delay;
            popupTimer.Start();
        }

        static void OnPopupTimerTick(object sender, EventArgs e)
        {
            popupTimer.Stop();
            if (!cfg.Enabled || shuttingDown) { HidePopup(); return; }

            TypingState state = wordBuffer.Snapshot();
            bool hasPrefix = state.CurrentWord.Length >= cfg.MinPrefixLength;
            bool wantNext = cfg.SuggestNextWord && state.AtWordStartAfterSpace && state.PreviousWord.Length >= 2;
            if (!hasPrefix && !wantNext) { HidePopup(); return; }

            if (IsChineseMode()) { HidePopup(); return; }

            List<Candidate> list = new List<Candidate>(engine.Query(state, Math.Max(1, Math.Min(9, cfg.MaxCandidates))));
            if (list.Count == 0) { HidePopup(); return; }

            Rectangle caret;
            bool caretOk = CaretLocator.TryGetCaretRect(out caret);
            Point anchor;
            bool anchorIsCaret = caretOk;
            if (caretOk)
            {
                anchor = new Point(caret.Left, caret.Bottom);
            }
            else if (TryGetCachedAnchor(out anchor))
            {
                // 微信/浏览器等自绘界面：退回"该应用里上次搜狗中文候选条出现的位置"，
                // 它通常就在输入框/插入符附近，比鼠标位置更贴近打字位置。
                anchorIsCaret = true;
            }
            else
            {
                anchor = CaretLocator.GetFallbackAnchor();
            }

            currentCandidates = list;
            popup.ShowCandidates(list, anchor, anchorIsCaret);
            popupVisible = true;
            diag.Debug("popup prefix=[" + state.CurrentWord + "] candidates=" + list.Count +
                       " chinese=" + IsChineseMode() +
                       " composing=" + (sogou != null && sogou.ChineseComposing) +
                       " age=" + (sogou != null ? (long)(DateTime.UtcNow - sogou.LastChineseSignalUtc).TotalMilliseconds : -1) +
                       " det=" + (sogou != null ? sogou.LastDetail : ""));
        }

        static void OnSogouChanged(object sender, EventArgs e)
        {
            bool composing = sogou != null && sogou.ChineseComposing;
            if (diag != null && sogou != null)
                diag.Debug("sogou changed composing=" + composing + " det=" + sogou.LastDetail);
            if (composing)
            {
                // 搜狗正在组词 => 确认处于中文模式，并清掉可能已混入的拼音字母。
                trackedChinese = true;
                lastChineseEvidenceUtc = DateTime.UtcNow;
                hasChineseEvidence = true;
                if (wordBuffer != null) wordBuffer.Reset();
                if (popupVisible) HidePopup();
                UpdateAnchorCache();
            }
            // 注意：组词结束（false）时不要清词缓冲，否则会把用户刚输入的英文词清掉。
        }

        static int NumberKeyIndex(HookKeyEventArgs e)
        {
            int vk = e.VkCode;
            if (vk >= 0x31 && vk <= 0x39) return vk - 0x31;      // 1..9
            if (vk >= 0x61 && vk <= 0x69) return vk - 0x61;      // numpad 1..9
            return -1;
        }

        // ------------------------------------------------------------------
        // 选词 / 注入
        // ------------------------------------------------------------------
        static void OnCandidatePicked(object sender, int index)
        {
            Pick(index);
        }

        static void Pick(int index)
        {
            if (index < 0 || index >= currentCandidates.Count) return;
            Candidate c = currentCandidates[index];
            TypingState state = wordBuffer.Snapshot();
            int backspaces = state.CurrentWord.Length;
            string insert = c.InsertText ?? c.Text ?? "";
            if (state.CurrentWordCapitalized && insert.Length > 0)
            {
                insert = char.ToUpperInvariant(insert[0]) + (insert.Length > 1 ? insert.Substring(1) : "");
            }
            HidePopup();
            diag.Info("pick [" + state.CurrentWord + "] -> [" + insert + "] backspaces=" + backspaces +
                      " source=" + c.Source);
            try
            {
                engine.OnCandidateSelected(state, c);
            }
            catch (Exception ex)
            {
                diag.Error("engine.OnCandidateSelected: " + ex.Message);
            }
            injecting = true;
            try
            {
                TextInjector.ReplaceText(backspaces, insert, 0);
            }
            catch (Exception ex)
            {
                diag.Error("TextInjector.ReplaceText: " + ex.Message);
            }
            finally
            {
                injecting = false;
            }
            wordBuffer.Reset();
        }

        static void HidePopup()
        {
            popupVisible = false;
            currentCandidates = new List<Candidate>();
            if (popup != null) popup.HidePopup();
        }

        /// <summary>把"搜狗中文候选条当前所在位置"缓存为近似插入符位置（微信/浏览器等自绘界面用）。</summary>
        static void UpdateAnchorCache()
        {
            try
            {
                if (sogou == null) return;
                Rectangle r = sogou.LastComposeRect;
                if (r.IsEmpty) return;
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                cachedAnchorHwnd = fg;
                cachedAnchorRect = r;
                cachedAnchorUtc = DateTime.UtcNow;
            }
            catch
            {
            }
        }

        static bool TryGetCachedAnchor(out Point anchor)
        {
            anchor = Point.Empty;
            if (cachedAnchorRect.IsEmpty) return false;
            if ((DateTime.UtcNow - cachedAnchorUtc).TotalHours > 3) return false;
            if (GetForegroundWindow() != cachedAnchorHwnd) return false;
            anchor = new Point(cachedAnchorRect.Left, cachedAnchorRect.Top);
            return true;
        }

        // ------------------------------------------------------------------
        // 模式判断
        // ------------------------------------------------------------------
        static bool IsChineseMode()
        {
            if (cfg.Mode == "chinese") return true;
            if (cfg.Mode == "english") return false;
            if (sogou == null) return trackedChinese == true;

            bool composing = sogou.ChineseComposing;
            if (composing)
            {
                // 刚用 Shift 切到英文时，搜狗组词窗口可能还有残留（约 200~500ms），以 Shift 跟踪为准。
                if (trackedChinese.HasValue && !trackedChinese.Value &&
                    (DateTime.UtcNow - lastModeToggleUtc).TotalMilliseconds < 600)
                {
                    return false;
                }
                return true;
            }

            if (trackedChinese.HasValue)
            {
                if (trackedChinese.Value)
                {
                    // 中文模式：长时间没有中文证据时自动失效，避免与输入法实际状态脱节。
                    if (!hasChineseEvidence ||
                        (DateTime.UtcNow - lastChineseEvidenceUtc).TotalSeconds > 90)
                    {
                        trackedChinese = null;
                        if (diag != null) diag.Debug("tracked mode expired -> auto");
                        return false;
                    }
                    return true;
                }
                return false;
            }

            // 未知状态：只有 220ms 内的搜狗组词残留才按中文处理。
            if ((DateTime.UtcNow - sogou.LastChineseSignalUtc).TotalMilliseconds < 220) return true;
            return false;
        }

        // ------------------------------------------------------------------
        // 托盘动作
        // ------------------------------------------------------------------
        static void ToggleEnabled()
        {
            cfg.Enabled = !cfg.Enabled;
            ConfigStore.Save(appDir, cfg);
            if (!cfg.Enabled) HidePopup();
            RefreshTrayStatus();
            if (tray != null)
                tray.ShowBalloon("英文候选助手", cfg.Enabled ? "已启用英文候选" : "已暂停英文候选");
            diag.Info("enabled=" + cfg.Enabled);
        }

        static void OnModeRequested(object sender, string mode)
        {
            if (string.IsNullOrEmpty(mode)) return;
            cfg.Mode = mode;
            ConfigStore.Save(appDir, cfg);
            if (cfg.Mode == "chinese") HidePopup();
            if (wordBuffer != null) wordBuffer.Reset();
            RefreshTrayStatus();
            string text = mode == "auto" ? "自动识别中/英文模式" : (mode == "english" ? "强制英文候选（不管输入法状态）" : "强制中文模式（不弹候选）");
            if (tray != null) tray.ShowBalloon("英文候选助手", "当前模式：" + text);
            diag.Info("mode=" + mode);
        }

        static void OpenSettings()
        {
            try
            {
                AppConfig updated;
                bool ok = SettingsForm.Edit(null, cfg, out updated);
                if (ok && updated != null)
                {
                    CopyConfig(updated, cfg);
                    ConfigStore.Save(appDir, cfg);
                    popup.ApplyConfig(cfg);
                    popupTimer.Interval = Math.Max(20, cfg.PopupDelayMs);
                    RefreshTrayStatus();
                    diag.Info("settings saved: max=" + cfg.MaxCandidates + " min=" + cfg.MinPrefixLength +
                              " words=" + cfg.SuggestWords + " phrases=" + cfg.SuggestPhrases +
                              " next=" + cfg.SuggestNextWord + " delay=" + cfg.PopupDelayMs);
                }
            }
            catch (Exception ex)
            {
                diag.Error("打开设置失败: " + ex.Message);
                MessageBox.Show("打开设置失败：" + ex.Message, "英文候选助手", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static void OpenDiagnostics()
        {
            try
            {
                if (diag != null && diag.LogPath != null)
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + diag.LogPath + "\"");
            }
            catch
            {
            }
        }

        static void ShowDiagnosticsBalloon()
        {
            if (tray == null) return;
            string mode = IsChineseMode() ? "中文/组词中" : "英文";
            string text = "词典：" + engine.WordCount + " 词 / " + engine.PhraseCount + " 短语\n" +
                          "输入模式：" + mode + "（自动判断）\n" +
                          "搜狗窗口识别：" + (sogou != null && sogou.SogouDetected ? "已检测到" : "未检测到") + "\n" +
                          "热键：Ctrl+Alt+E 开关，Ctrl+Alt+D 本提示";
            tray.ShowBalloon("英文候选助手 · 状态", text);
        }

        static void RefreshTrayStatus()
        {
            if (tray == null) return;
            string modeText = cfg.Mode == "auto" ? "自动" : (cfg.Mode == "english" ? "英文" : "中文");
            string extra = engine != null && engine.Ready ? (engine.WordCount + "词") : "无词典";
            tray.UpdateStatus(cfg.Enabled, modeText, extra);
        }

        static void Shutdown()
        {
            shuttingDown = true;
            diag.Info("=== 退出 ===");
            try { SafeCleanup(); } catch { }
            Application.Exit();
        }

        static void SafeCleanup()
        {
            try { if (hook != null) hook.Uninstall(); } catch { }
            try { if (sogou != null) sogou.Stop(); } catch { }
            try { if (hotkeys != null) hotkeys.Dispose(); } catch { }
            try { if (tray != null) tray.Dispose(); } catch { }
            try { if (engine != null) engine.SaveLearning(); } catch { }
            try { if (singleInstance != null) singleInstance.ReleaseMutex(); } catch { }
        }

        static void CopyConfig(AppConfig src, AppConfig dst)
        {
            dst.Enabled = src.Enabled;
            dst.MaxCandidates = src.MaxCandidates;
            dst.MinPrefixLength = src.MinPrefixLength;
            dst.SuggestWords = src.SuggestWords;
            dst.SuggestPhrases = src.SuggestPhrases;
            dst.SuggestNextWord = src.SuggestNextWord;
            dst.PopupDelayMs = src.PopupDelayMs;
            dst.PlaySound = src.PlaySound;
            dst.LearnUserSelections = src.LearnUserSelections;
            dst.Mode = src.Mode;
            dst.UseImmFallback = src.UseImmFallback;
        }

        /// <summary>
        /// 配置/日志/学习记录目录：默认 %APPDATA%\EngCand；
        /// 支持 --userdir=路径 覆盖（自动化测试/绿色版用）；都不可写时退化到 exe 同目录 userdata。
        /// </summary>
        static string ResolveAppDir(string[] args)
        {
            string overridePath = null;
            foreach (string a in args)
            {
                if (a.StartsWith("--userdir=", StringComparison.OrdinalIgnoreCase))
                {
                    overridePath = a.Substring("--userdir=".Length).Trim('"');
                    break;
                }
            }
            if (!string.IsNullOrEmpty(overridePath))
            {
                try
                {
                    Directory.CreateDirectory(overridePath);
                    return Path.GetFullPath(overridePath);
                }
                catch
                {
                }
            }
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EngCand");
            try
            {
                Directory.CreateDirectory(appData);
                return appData;
            }
            catch
            {
            }
            try
            {
                string fallback = Path.Combine(AppContext.BaseDirectory, "userdata");
                Directory.CreateDirectory(fallback);
                return fallback;
            }
            catch
            {
            }
            return appData;
        }

        static string FindDataDir()
        {
            string baseDir = AppContext.BaseDirectory;
            string[] candidates = new string[]
            {
                Path.Combine(baseDir, "data"),
                Path.Combine(baseDir, "..", "data"),
                Path.Combine(baseDir, "..", "..", "data"),
                Path.Combine(baseDir, "..", "..", "..", "data"),
                Path.Combine(Directory.GetCurrentDirectory(), "data"),
                Path.Combine(Directory.GetCurrentDirectory(), "english-cand", "data")
            };
            foreach (string c in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(c);
                    if (File.Exists(Path.Combine(full, "words.tsv"))) return full;
                }
                catch
                {
                }
            }
            return null;
        }
    }
}
