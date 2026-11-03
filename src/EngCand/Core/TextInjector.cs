using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace EngCand
{
    /// <summary>
    /// 文本注入：通过 SendInput 发退格 + Unicode 字符，把用户刚输入的前缀替换成候选词。
    /// ⚠ 本文件由 core-dev 实现，公共 API 不要改。
    ///
    /// 契约：
    ///  - ReplaceText(backspaces, text, delayMs)：
    ///      先发 backspaces 个 VK_BACK（keydown+keyup），再按字符逐个用
    ///      KEYEVENTF_UNICODE 的 KEYBDINPUT 发 text（每个字符 down+up，代理对要正确处理）。
    ///      delayMs>0 时每步之间 Thread.Sleep(delayMs)。
    ///      成功返回 true；SendInput 返回值不足时返回 false 并尽力继续。
    ///  - TypeUnicode(text, delayMs)：只发字符，不按退格。
    ///  - 注入事件会被全局钩子看到，但 KeyboardHook 自己通过 LLKHF_INJECTED 标记 IsInjected，
    ///    上层不会把它当用户输入（不用担心递归）。
    ///  - 不要用 keybd_event，用 SendInput（更可靠、更少被游戏/安全软件拦）。
    ///  - 在 UI 线程调用；单次注入总耗时应在几十毫秒内。
    ///
    /// 实现补充：
    ///  - INPUT 结构按完整 union（MOUSEINPUT/KEYBDINPUT/HARDWAREINPUT）定义，保证
    ///    cbSize 与 native sizeof(INPUT) 一致（x64=40 字节），否则 SendInput 直接失败。
    ///  - 代理对（UTF-16 高/低代理）的两个 code unit 放在同一次 SendInput 调用里连续发出，
    ///    避免目标窗口把中间插入的事件当成独立字符。
    ///  - 不检查前台窗口；调用方（Program.Pick）负责在合适的时机调用。
    /// </summary>
    public static class TextInjector
    {
        const uint INPUT_KEYBOARD = 1;

        const uint KEYEVENTF_KEYUP = 0x0002;
        const uint KEYEVENTF_UNICODE = 0x0004;

        const ushort VK_BACK = 0x08;

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static bool ReplaceText(int backspaces, string text, int delayMs = 0)
        {
            bool ok = true;
            try
            {
                if (backspaces > 0)
                {
                    INPUT[] batch = new INPUT[2];
                    batch[0] = MakeVirtualKey(VK_BACK, false);
                    batch[1] = MakeVirtualKey(VK_BACK, true);
                    for (int i = 0; i < backspaces; i++)
                    {
                        if (!SendBatch(batch, 2)) ok = false;   // 失败也尽力继续
                        SleepStep(delayMs);
                    }
                }

                if (!string.IsNullOrEmpty(text))
                {
                    if (!TypeUnicodeCore(text, delayMs)) ok = false;
                }
            }
            catch
            {
                // SendInput 理论上不抛异常；这里保证注入失败不会把上层选词流程打断。
                ok = false;
            }
            return ok;
        }

        public static bool TypeUnicode(string text, int delayMs = 0)
        {
            try
            {
                return TypeUnicodeCore(text, delayMs);
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 内部实现
        // ------------------------------------------------------------------
        static bool TypeUnicodeCore(string text, int delayMs)
        {
            if (string.IsNullOrEmpty(text)) return true;

            bool ok = true;
            INPUT[] batch = new INPUT[4];
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    // 代理对：两个 code unit 连续发出（高代理 down/up，低代理 down/up），
                    // 同一次 SendInput 调用保证中间不会插入其它事件。
                    char low = text[i + 1];
                    batch[0] = MakeUnicodeChar(c, false);
                    batch[1] = MakeUnicodeChar(c, true);
                    batch[2] = MakeUnicodeChar(low, false);
                    batch[3] = MakeUnicodeChar(low, true);
                    if (!SendBatch(batch, 4)) ok = false;
                    i += 2;
                }
                else
                {
                    batch[0] = MakeUnicodeChar(c, false);
                    batch[1] = MakeUnicodeChar(c, true);
                    if (!SendBatch(batch, 2)) ok = false;
                    i++;
                }

                SleepStep(delayMs);
            }
            return ok;
        }

        static INPUT MakeVirtualKey(ushort vk, bool keyUp)
        {
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.u.ki.wVk = vk;
            input.u.ki.wScan = 0;
            input.u.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0;
            input.u.ki.time = 0;
            input.u.ki.dwExtraInfo = IntPtr.Zero;
            return input;
        }

        static INPUT MakeUnicodeChar(char c, bool keyUp)
        {
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.u.ki.wVk = 0;                 // KEYEVENTF_UNICODE 时 wVk 必须为 0
            input.u.ki.wScan = c;               // 直接给 UTF-16 code unit
            input.u.ki.dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0u);
            input.u.ki.time = 0;
            input.u.ki.dwExtraInfo = IntPtr.Zero;
            return input;
        }

        static bool SendBatch(INPUT[] inputs, int count)
        {
            if (count <= 0) return true;
            uint sent = SendInput((uint)count, inputs, Marshal.SizeOf(typeof(INPUT)));
            return sent == (uint)count;
        }

        static void SleepStep(int delayMs)
        {
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
    }
}
