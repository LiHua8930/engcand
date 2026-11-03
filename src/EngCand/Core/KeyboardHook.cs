using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// 全局低级键盘钩子（WH_KEYBOARD_LL）。⚠ 本文件由 core-dev 实现，公共 API 不要改。
    /// 契约：
    ///  - Install() 必须在带消息循环的 UI 线程调用；Uninstall() 可重复调用。
    ///  - KeyDown/KeyUp 事件在 UI 线程触发（钩子回调在安装线程执行）。
    ///  - 把 HookKeyEventArgs.Handled 置 true 时，必须吞掉该按键：
    ///      * IsDown=true 时 return 1（不 CallNextHookEx）；
    ///      * 同时记住该 VkCode，在下一次同 VkCode 的 KeyUp 时也吞掉（return 1）。
    ///  - 不能吞掉 IsInjected 的按键（避免吞掉自己 SendInput 注入的字符）。
    ///  - 修饰键状态用 GetAsyncKeyState 或 hook 参数自己维护（Alt/Ctrl/Shift/Win）。
    ///
    /// 实现补充：
    ///  - 事件回调里抛出的异常会被吞掉，保证不把异常抛回 native 钩子回调（否则会崩掉消息循环）。
    ///  - 钩子 delegate 保存在字段里并持续引用，避免被 GC 回收导致 native 回调野指针。
    ///  - 卸载成功后 active=false，残留回调只 CallNextHookEx、不再触发事件。
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_KEYUP = 0x0101;
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_SYSKEYUP = 0x0105;

        const int LLKHF_EXTENDED = 0x01;
        const int LLKHF_LOWER_IL_INJECTED = 0x02;
        const int LLKHF_INJECTED = 0x10;

        const int VK_SHIFT = 0x10;
        const int VK_CONTROL = 0x11;
        const int VK_MENU = 0x12;
        const int VK_LWIN = 0x5B;
        const int VK_RWIN = 0x5C;

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        public event EventHandler<HookKeyEventArgs> KeyDown;
        public event EventHandler<HookKeyEventArgs> KeyUp;

        public bool Installed { get; private set; }

        /// <summary>
        /// 仅测试/自动化模式使用：把"注入键"也当作真实按键处理（Program 用 injecting 标志
        /// 挡住自身注入的字符，不会递归）。生产模式保持 false。
        /// </summary>
        public bool TreatInjectedAsReal { get; set; }

        LowLevelKeyboardProc hookProc;
        IntPtr hookHandle = IntPtr.Zero;
        volatile bool active;

        // 已被 KeyDown 吞掉的 VkCode：对应的下一次 KeyUp 也要吞掉。
        readonly HashSet<int> swallowedKeyDownVk = new HashSet<int>();

        public void Install()
        {
            if (Installed && hookHandle != IntPtr.Zero) return;

            // 保持 delegate 强引用（字段），否则 GC 回收后 native 调用会崩。
            if (hookProc == null) hookProc = HookCallback;

            IntPtr hMod = IntPtr.Zero;
            try { hMod = GetModuleHandleW(null); }
            catch { hMod = IntPtr.Zero; }

            hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, hookProc, hMod, 0);
            if (hookHandle == IntPtr.Zero)
            {
                int err1 = Marshal.GetLastWin32Error();
                // 个别环境（沙箱/受限权限）用进程模块句柄装不上时，退化为 hMod=0 再试一次。
                hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, hookProc, IntPtr.Zero, 0);
                if (hookHandle == IntPtr.Zero)
                {
                    int err2 = Marshal.GetLastWin32Error();
                    throw new System.ComponentModel.Win32Exception(err2,
                        "SetWindowsHookEx(WH_KEYBOARD_LL) 失败（错误 " + err2 + "；首次尝试错误 " + err1 + "）");
                }
            }

            swallowedKeyDownVk.Clear();
            active = true;
            Installed = true;
        }

        public void Uninstall()
        {
            IntPtr handle = hookHandle;
            hookHandle = IntPtr.Zero;
            Installed = false;
            active = false;
            swallowedKeyDownVk.Clear();

            if (handle != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(handle); } catch { }
            }
        }

        public void Dispose()
        {
            try { Uninstall(); } catch { }
        }

        // ------------------------------------------------------------------
        // 钩子回调（native 调用，异常绝不能外泄）
        // ------------------------------------------------------------------
        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (!active) return CallNextHookEx(hookHandle, nCode, wParam, lParam);
            if (nCode < 0) return CallNextHookEx(hookHandle, nCode, wParam, lParam);

            try
            {
                int msg = wParam.ToInt32();
                bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                if (isDown || isUp)
                {
                    KBDLLHOOKSTRUCT info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    int vk = info.vkCode;
                    bool injected = (info.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;

                    HookKeyEventArgs e = new HookKeyEventArgs();
                    e.VkCode = vk;
                    e.Key = (Keys)vk;
                    e.IsDown = isDown;
                    e.IsInjected = injected && !TreatInjectedAsReal;
                    e.Ctrl = IsPhysicallyDown(VK_CONTROL) || (isDown && IsCtrlVk(vk));
                    e.Alt = IsPhysicallyDown(VK_MENU) || (isDown && IsAltVk(vk));
                    e.Shift = IsPhysicallyDown(VK_SHIFT) || (isDown && IsShiftVk(vk));
                    e.Win = IsPhysicallyDown(VK_LWIN) || IsPhysicallyDown(VK_RWIN) || (isDown && (vk == VK_LWIN || vk == VK_RWIN));

                    bool swallow = false;

                    if (isDown)
                    {
                        EventHandler<HookKeyEventArgs> handler = KeyDown;
                        if (handler != null)
                        {
                            try { handler(this, e); } catch { }
                        }

                        // 只有被当作真实按键的事件才执行"吞键"语义（生产模式下注入键永远放行）。
                        if (!e.IsInjected && e.Handled)
                        {
                            swallowedKeyDownVk.Add(vk);
                            swallow = true;
                        }
                    }
                    else
                    {
                        bool autoSwallow = false;
                        if (!e.IsInjected && swallowedKeyDownVk.Remove(vk)) autoSwallow = true;
                        if (autoSwallow) e.Handled = true;

                        EventHandler<HookKeyEventArgs> handler = KeyUp;
                        if (handler != null)
                        {
                            try { handler(this, e); } catch { }
                        }

                        if (!e.IsInjected && (autoSwallow || e.Handled)) swallow = true;
                    }

                    if (swallow) return (IntPtr)1;
                }
            }
            catch
            {
                // 钩子回调不能抛异常：吞掉后继续链式传递。
            }

            return CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        static bool IsPhysicallyDown(int vk)
        {
            try { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
            catch { return false; }
        }

        static bool IsCtrlVk(int vk) { return vk == 0x11 || vk == 0xA2 || vk == 0xA3; }
        static bool IsAltVk(int vk) { return vk == 0x12 || vk == 0xA4 || vk == 0xA5; }
        static bool IsShiftVk(int vk) { return vk == 0x10 || vk == 0xA0 || vk == 0xA1; }
    }
}
