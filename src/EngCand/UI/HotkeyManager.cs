using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// 全局热键（RegisterHotKey）：Ctrl+Alt+E 开关、Ctrl+Alt+D 状态气泡。
    /// ⚠ 本文件由 ui-dev 实现，公共 API 不要改。
    ///
    /// 契约：
    ///  - Register() 在 UI 线程调用：创建一个隐藏的 NativeWindow（或隐藏 Form）
    ///    通过 RegisterHotKey 注册 MOD_CONTROL|MOD_ALT 的 E 与 D；
    ///    在 WndProc 里处理 WM_HOTKEY(0x0312) 触发对应事件。
    ///  - 注册失败（被其它程序占用/权限）不抛异常，静默失败即可（Program 有 LL 钩子兜底）。
    ///  - Dispose()：UnregisterHotKey 并销毁隐藏窗口；可重复调用。
    ///  - 事件必须用 try/catch 包裹触发，避免异常打断消息循环。
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        public event EventHandler ToggleRequested;
        public event EventHandler DiagnosticsRequested;

        // 应用级热键 ID（必须在 0x0000-0xBFFF 之间）
        const int HotkeyIdToggle = 0xB001;
        const int HotkeyIdDiagnostics = 0xB002;

        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x0001;
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_NOREPEAT = 0x4000;
        const uint VK_E = 0x45;
        const uint VK_D = 0x44;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        HotkeyWindow window;
        bool registeredToggle;
        bool registeredDiagnostics;
        bool disposed;

        /// <summary>隐藏的只收消息的窗口：WM_HOTKEY 会被发送到它的 WndProc。</summary>
        sealed class HotkeyWindow : NativeWindow
        {
            public HotkeyManager Owner;

            public HotkeyWindow()
            {
                CreateHandle(new CreateParams
                {
                    Caption = "EngCand.HotkeyWindow",
                    X = 0,
                    Y = 0,
                    Width = 0,
                    Height = 0,
                    Style = 0,
                    ExStyle = WS_EX_TOOLWINDOW,
                    Parent = IntPtr.Zero
                });
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    HotkeyManager owner = Owner;
                    if (owner != null) owner.OnHotkey(m.WParam.ToInt32());
                    return;
                }
                base.WndProc(ref m);
            }
        }

        public void Register()
        {
            if (disposed || window != null) return;
            try
            {
                window = new HotkeyWindow();
                window.Owner = this;
                IntPtr hwnd = window.Handle;
                if (hwnd == IntPtr.Zero) return;

                registeredToggle = RegisterHotKey(hwnd, HotkeyIdToggle, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_E);
                registeredDiagnostics = RegisterHotKey(hwnd, HotkeyIdDiagnostics, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_D);
            }
            catch
            {
                // 注册失败静默：Program 的全局键盘钩子会兜底
            }
        }

        void OnHotkey(int id)
        {
            if (disposed) return;
            try
            {
                if (id == HotkeyIdToggle)
                {
                    EventHandler handler = ToggleRequested;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
                else if (id == HotkeyIdDiagnostics)
                {
                    EventHandler handler = DiagnosticsRequested;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
            }
            catch
            {
                // 事件处理异常不能打断消息循环
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            HotkeyWindow w = window;
            window = null;
            try
            {
                if (w != null && w.Handle != IntPtr.Zero)
                {
                    IntPtr hwnd = w.Handle;
                    if (registeredToggle) UnregisterHotKey(hwnd, HotkeyIdToggle);
                    if (registeredDiagnostics) UnregisterHotKey(hwnd, HotkeyIdDiagnostics);
                    w.DestroyHandle();
                }
            }
            catch
            {
            }
            registeredToggle = false;
            registeredDiagnostics = false;
            GC.SuppressFinalize(this);
        }

        // ------------------------------------------------------------------
        // Win32
        // ------------------------------------------------------------------

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
