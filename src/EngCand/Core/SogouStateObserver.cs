using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// 搜狗输入法状态观察：轮询系统顶层窗口，判断搜狗当前是否正在显示组词/候选窗口
    /// （即用户此刻在中文模式下打拼音）。这是跨应用识别中文模式的主要手段：
    ///  - 中文模式打拼音时，目标应用进程里会出现可见的 SoPY_Comp 窗口（搜狗候选条）；
    ///  - 英文模式下搜狗不显示该窗口。
    /// ⚠ 本文件由 core-dev 实现，公共 API 不要改。
    ///
    /// 实现契约：
    ///  - 构造函数保存 prefixes 与轮询间隔（毫秒，>=20），不启动。
    ///  - Start() 用 WinForms 的 System.Windows.Forms.Timer（或等价 UI 线程计时器）开始轮询；
    ///    Stop() 停止；Dispose() = Stop()。
    ///  - 每轮用 EnumWindows 枚举顶层窗口，对每个 IsWindowVisible(h)==true 的窗口调用 GetClassNameW，
    ///    若类名以 prefixes 中任一项开头（如 "SoPY_Comp"），且窗口 rect 宽高 > 0，则视为"搜狗正在组词"。
    ///  - 命中时：ChineseComposing=true；LastChineseSignalUtc=DateTime.UtcNow；LastDetail=类名+rect。
    ///    本轮未命中：ChineseComposing=false（LastChineseSignalUtc 保留）。
    ///  - 任意轮次见到 SoPY_*/Sogou_TSF_UI 类窗口即 SogouDetected=true（不论是否可见）。
    ///  - ChineseComposing 状态变化时触发 Changed 事件（UI 线程）。
    ///  - 异常必须吞掉（EnumWindows/GetClassName 失败不能抛到计时器线程）。
    ///  - 轮询要轻量：每轮 EnumWindows 一次，不做多余分配；40ms 间隔下 CPU 占用应可忽略。
    ///
    /// 实现补充：
    ///  - Start() 会先同步跑一轮轮询（这样启动日志里的 SogouDetected 立即准确），再启动计时器。
    ///  - 每轮复用同一个 EnumWindows 回调 delegate 与 StringBuilder；类名首字母不是 'S' 的窗口
    ///    直接跳过、不分配字符串（系统顶层窗口上百个，避免每轮大量小对象）。
    ///  - 找到组词窗口后提前结束本轮枚举（EnumWindows 回调返回 false）。
    ///  - Changed 事件处理异常同样吞掉，避免打断 WinForms 消息循环。
    /// </summary>
    public sealed class SogouStateObserver : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        public event EventHandler Changed;

        readonly string[] prefixes;
        readonly int pollIntervalMs;

        readonly EnumWindowsProc enumWindowsCallback;
        readonly StringBuilder classNameBuffer = new StringBuilder(256);
        readonly List<string> prefixCache;   // 构造时物化，避免每轮访问外部数组

        System.Windows.Forms.Timer timer;
        bool running;
        bool polling;
        bool disposed;

        // 单轮扫描结果（复用字段，避免每轮回调里分配闭包）。
        bool scanComposing;
        bool scanDetected;
        string scanDetail;
        System.Drawing.Rectangle scanRect;

        public SogouStateObserver(string[] composeClassPrefixes, int pollIntervalMs = 40)
        {
            this.prefixes = composeClassPrefixes != null && composeClassPrefixes.Length > 0
                ? composeClassPrefixes
                : new string[] { "SoPY_Comp", "SoPY_UI" };
            this.pollIntervalMs = Math.Max(20, pollIntervalMs);

            prefixCache = new List<string>();
            foreach (string p in this.prefixes)
            {
                if (!string.IsNullOrEmpty(p)) prefixCache.Add(p);
            }

            enumWindowsCallback = EnumWindowProc;
            LastDetail = "";
        }

        public bool ChineseComposing { get; private set; }
        public DateTime LastChineseSignalUtc { get; private set; }
        public bool SogouDetected { get; private set; }
        public string LastDetail { get; private set; }

        /// <summary>
        /// 最近一次看到的搜狗组词/候选窗口的屏幕矩形（英文模式下也保留最后一次的值）。
        /// 微信/浏览器这类自绘界面拿不到文本插入符时，可以拿它作为"输入位置"的近似。
        /// </summary>
        public System.Drawing.Rectangle LastComposeRect { get; private set; }

        public void Start()
        {
            if (disposed) return;

            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer();
                timer.Tick += OnTimerTick;
            }
            timer.Interval = pollIntervalMs;

            running = true;

            // 立即跑一轮：Program 启动后会马上读 SogouDetected / ChineseComposing 打日志。
            try { Poll(); } catch { }

            try { timer.Start(); } catch { }
        }

        public void Stop()
        {
            running = false;
            System.Windows.Forms.Timer t = timer;
            if (t != null)
            {
                try { t.Stop(); } catch { }
            }
        }

        public void Dispose()
        {
            try { Stop(); } catch { }

            disposed = true;
            System.Windows.Forms.Timer t = timer;
            timer = null;
            if (t != null)
            {
                try
                {
                    t.Tick -= OnTimerTick;
                    t.Dispose();
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------
        // 轮询
        // ------------------------------------------------------------------
        void OnTimerTick(object sender, EventArgs e)
        {
            if (!running || disposed) return;
            try { Poll(); } catch { }
        }

        void Poll()
        {
            if (polling) return;
            polling = true;
            try
            {
                scanComposing = false;
                scanDetected = false;
                scanDetail = null;
                scanRect = System.Drawing.Rectangle.Empty;

                try { EnumWindows(enumWindowsCallback, IntPtr.Zero); }
                catch { }

                bool composing = scanComposing;
                string detail = scanDetail;
                System.Drawing.Rectangle rect = scanRect;

                if (scanDetected) SogouDetected = true;

                if (composing)
                {
                    LastChineseSignalUtc = DateTime.UtcNow;
                    if (!rect.IsEmpty) LastComposeRect = rect;
                    if (!string.IsNullOrEmpty(detail)) LastDetail = detail;
                }

                bool changed = ChineseComposing != composing;
                ChineseComposing = composing;
                if (changed)
                {
                    EventHandler handler = Changed;
                    if (handler != null)
                    {
                        try { handler(this, EventArgs.Empty); } catch { }
                    }
                }
            }
            catch
            {
                // 轮询异常绝不能抛到计时器线程。
            }
            finally
            {
                polling = false;
            }
        }

        bool EnumWindowProc(IntPtr hWnd, IntPtr lParam)
        {
            try
            {
                StringBuilder sb = classNameBuffer;
                sb.Length = 0;
                int len = GetClassNameW(hWnd, sb, sb.Capacity);
                if (len <= 0) return true;

                // 搜狗的窗口类名都以 'S' 开头；先做一次字符判断，避免给上百个无关窗口分配字符串。
                if (sb[0] != 'S') return true;

                string cls = sb.ToString();

                if (!scanDetected
                    && (cls.StartsWith("SoPY_", StringComparison.Ordinal)
                        || cls.StartsWith("Sogou_TSF_UI", StringComparison.Ordinal)))
                {
                    scanDetected = true;
                }

                if (!scanComposing && IsComposingClass(cls) && IsWindowVisible(hWnd))
                {
                    RECT rect;
                    if (GetWindowRect(hWnd, out rect))
                    {
                        int w = rect.right - rect.left;
                        int h = rect.bottom - rect.top;
                        if (w > 0 && h > 0)
                        {
                            scanComposing = true;
                            scanRect = System.Drawing.Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
                            scanDetail = cls + " (" + rect.left + "," + rect.top + "," +
                                         rect.right + "," + rect.bottom + ") " + w + "x" + h;
                        }
                    }
                }

                // 已找到组词窗口，且"搜狗存在"事实也已确认，可以提前结束本轮。
                if (scanComposing && (scanDetected || SogouDetected)) return false;
                return true;
            }
            catch
            {
                return true;
            }
        }

        bool IsComposingClass(string cls)
        {
            List<string> list = prefixCache;
            for (int i = 0; i < list.Count; i++)
            {
                if (cls.StartsWith(list[i], StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
