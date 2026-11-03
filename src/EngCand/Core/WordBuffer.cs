using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// English 词缓冲：跟踪用户当前正在输入的单词、上一个词、句首状态。
    /// ⚠ 本文件由 core-dev 实现，公共 API 不要改。
    ///
    /// 行为契约（喂 KeyboardHook 的 KeyDown，顺序发生）：
    ///  - a-z / A-Z：追加到 CurrentWord（小写保存）；若 CurrentWord 原本为空且首字母大写，
    ///    则 CurrentWordCapitalized = true。
    ///  - 退格：删掉 CurrentWord 最后一个字符；若 CurrentWord 为空则向上一个词退格（尽力，不强制）。
    ///  - 空格/回车/Tab/常见标点（, . ; : ! ? " ' 括号等）：把 CurrentWord 提交为 PreviousWord
    ///    （PreviousWord2 = 旧 PreviousWord），CurrentWord 清空，AtWordStartAfterSpace = true。
    ///    句末标点（. ! ?）之后 SentenceStart = true；其它分隔符之后 SentenceStart = false。
    ///  - 数字/其它键：不改缓冲（但 CurrentWord 非空时按分隔符提交，避免英文词后跟数字时错位）。
    ///  - Esc/方向键/功能键等：忽略。
    ///  - 只处理 e.IsDown == true 且 !e.IsInjected 的事件。
    ///  - CurrentWord 允许包含 a-z、'、-（例如 don't、well-known）；其它字符视为分隔符。
    ///  - Snapshot() 返回当前状态副本（TypingState）。
    ///  - Reset() 清空所有状态（选词注入后调用）。
    ///
    /// 实现补充（不改变上面的契约）：
    ///  - Ctrl/Alt/Win 组合键视为快捷键，不进入词缓冲（Shift 不算，Shift 用于大写）。
    ///  - 首字母大写判断 = Shift 状态 XOR CapsLock 锁定状态。
    ///  - 撇号/连字符只在"单词内部"（CurrentWord 非空、且连字符前是字母）才并入当前词；
    ///    位于词首时按分隔符处理，避免把成对引号/独立破折号吃进单词。
    ///  - 连续退格跨过词边界时，把上一个词恢复成当前词再删一个字符（尽力而为）。
    /// </summary>
    public sealed class WordBuffer
    {
        const int VK_BACK = 0x08;
        const int VK_TAB = 0x09;
        const int VK_RETURN = 0x0D;
        const int VK_SPACE = 0x20;
        const int VK_CAPITAL = 0x14;

        const int VK_1 = 0x31;              // 主键盘 '1' / Shift+1 = '!'
        const int VK_DECIMAL = 0x6E;        // 小键盘 '.'
        const int VK_OEM_SEMICOLON = 0xBA;  // ; :
        const int VK_OEM_PLUS = 0xBB;       // = +
        const int VK_OEM_COMMA = 0xBC;      // , <
        const int VK_OEM_MINUS = 0xBD;      // - _
        const int VK_OEM_PERIOD = 0xBE;     // . >
        const int VK_OEM_QUESTION = 0xBF;   // / ?
        const int VK_OEM_TILDE = 0xC0;      // ` ~
        const int VK_OEM_OPEN_BRACKET = 0xDB;   // [ {
        const int VK_OEM_BACKSLASH = 0xDC;      // \ |
        const int VK_OEM_CLOSE_BRACKET = 0xDD;  // ] }
        const int VK_OEM_QUOTES = 0xDE;         // ' "

        [DllImport("user32.dll")]
        static extern short GetKeyState(int nVirtKey);

        readonly StringBuilder current = new StringBuilder(32);

        string previousWord = "";
        string previousWord2 = "";

        bool sentenceStart = true;
        bool atWordStartAfterSpace;
        bool currentWordCapitalized;

        // 回溯（退格跨词边界）时恢复用：记录 PreviousWord / PreviousWord2 的大小写与句首状态。
        bool prevCapitalized;
        bool prevSentenceStart;
        bool prev2Capitalized;
        bool prev2SentenceStart;

        /// <summary>当前正在输入的单词（小写，只含 a-z ' -），可能为空。</summary>
        public string CurrentWord
        {
            get { return current.ToString(); }
        }

        /// <summary>当前缓冲是否为空（无未提交字符）。</summary>
        public bool IsEmpty
        {
            get { return current.Length == 0; }
        }

        public TypingState Snapshot()
        {
            TypingState s = new TypingState();
            s.CurrentWord = current.ToString();
            s.PreviousWord = previousWord;
            s.PreviousWord2 = previousWord2;
            s.AtWordStartAfterSpace = atWordStartAfterSpace;
            s.SentenceStart = sentenceStart;
            s.CurrentWordCapitalized = currentWordCapitalized;
            return s;
        }

        public void OnKey(HookKeyEventArgs e)
        {
            if (e == null) return;
            if (!e.IsDown) return;        // KeyUp 不处理
            if (e.IsInjected) return;     // 自己注入的字符不处理

            // 快捷键（Ctrl/Alt/Win 组合）不是文本输入；Shift 允许（用于大写字母）。
            if (e.Ctrl || e.Alt || e.Win) return;

            int vk = e.VkCode;

            // ---- 字母 ----
            if (vk >= 'A' && vk <= 'Z')
            {
                AppendLetter(vk, e.Shift);
                return;
            }

            // ---- 编辑 / 分隔键 ----
            switch (vk)
            {
                case VK_BACK:
                    Backspace();
                    return;
                case VK_SPACE:
                case VK_TAB:
                case VK_RETURN:
                    CommitSeparator(false);
                    return;
                case VK_OEM_PERIOD:     // . >
                case VK_DECIMAL:        // 小键盘 .
                    CommitSeparator(true);
                    return;
                case VK_OEM_QUESTION:   // / ?（Shift 时是句末问号）
                    CommitSeparator(e.Shift);
                    return;
                case VK_1:              // Shift+1 = '!' 句末；否则数字按"其它键"处理
                    if (e.Shift)
                    {
                        CommitSeparator(true);
                        return;
                    }
                    CommitIfTyping();
                    return;
                case VK_OEM_QUOTES:     // ' "（Shift 时是双引号）
                    if (!e.Shift && current.Length > 0)
                    {
                        // 单词内部的撇号：don't / it's
                        current.Append('\'');
                        return;
                    }
                    CommitSeparator(false);
                    return;
                case VK_OEM_MINUS:      // - _（单词内部的连字符：well-known）
                    if (current.Length > 0 && IsAsciiLetter(current[current.Length - 1]))
                    {
                        current.Append('-');
                        return;
                    }
                    CommitSeparator(false);
                    return;
                case VK_OEM_COMMA:
                case VK_OEM_SEMICOLON:
                case VK_OEM_PLUS:
                case VK_OEM_TILDE:
                case VK_OEM_OPEN_BRACKET:
                case VK_OEM_BACKSLASH:
                case VK_OEM_CLOSE_BRACKET:
                    CommitSeparator(false);
                    return;
            }

            // ---- 功能键 / 方向键 / 输入法键等：忽略 ----
            if (IsIgnoredKey(vk)) return;

            // ---- 数字（主键盘 + 小键盘）：数字不进入缓冲，但会按分隔符提交当前词 ----
            if ((vk >= '0' && vk <= '9') || (vk >= 0x60 && vk <= 0x6F))
            {
                CommitIfTyping();
                return;
            }

            // ---- 其它可打印字符（OEM 遗留键等）：按标点/分隔符处理 ----
            if (vk >= 0xBA && vk <= 0xE2)
            {
                CommitSeparator(false);
                return;
            }

            // ---- 其它任何键：不改缓冲（当前词非空时按分隔符提交，避免与后续输入错位） ----
            CommitIfTyping();
        }

        public void Reset()
        {
            current.Length = 0;
            previousWord = "";
            previousWord2 = "";
            sentenceStart = true;
            atWordStartAfterSpace = false;
            currentWordCapitalized = false;
            prevCapitalized = false;
            prevSentenceStart = false;
            prev2Capitalized = false;
            prev2SentenceStart = false;
        }

        // ------------------------------------------------------------------
        // 内部实现
        // ------------------------------------------------------------------

        void AppendLetter(int vk, bool shift)
        {
            char c = (char)('a' + (vk - 'A'));
            if (current.Length == 0)
            {
                currentWordCapitalized = shift ^ IsCapsLockOn();
                atWordStartAfterSpace = false;
            }
            current.Append(c);
        }

        void CommitSeparator(bool sentenceEnd)
        {
            if (current.Length > 0)
            {
                previousWord2 = previousWord;
                prev2Capitalized = prevCapitalized;
                prev2SentenceStart = prevSentenceStart;

                previousWord = current.ToString();
                prevCapitalized = currentWordCapitalized;
                prevSentenceStart = sentenceStart;

                current.Length = 0;
                currentWordCapitalized = false;
            }
            atWordStartAfterSpace = true;
            sentenceStart = sentenceEnd;
        }

        /// <summary>数字等"其它键"：空缓冲时什么都不改；有未提交单词时按分隔符提交。</summary>
        void CommitIfTyping()
        {
            if (current.Length == 0) return;
            CommitSeparator(false);
        }

        void Backspace()
        {
            if (current.Length > 0)
            {
                current.Length = current.Length - 1;
                if (current.Length == 0) currentWordCapitalized = false;
                return;
            }

            // CurrentWord 已空：尽力回溯到上一个词，把上一个词恢复成当前词后删掉一个字符。
            if (previousWord.Length == 0) return;

            string restored = previousWord.Length > 1
                ? previousWord.Substring(0, previousWord.Length - 1)
                : "";
            current.Length = 0;
            current.Append(restored);
            currentWordCapitalized = prevCapitalized;
            sentenceStart = prevSentenceStart;

            previousWord = previousWord2;
            prevCapitalized = prev2Capitalized;
            prevSentenceStart = prev2SentenceStart;

            previousWord2 = "";
            prev2Capitalized = false;
            prev2SentenceStart = false;

            atWordStartAfterSpace = current.Length == 0;
        }

        static bool IsAsciiLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        static bool IsCapsLockOn()
        {
            try { return (GetKeyState(VK_CAPITAL) & 1) != 0; }
            catch { return false; }
        }

        static bool IsIgnoredKey(int vk)
        {
            if (vk >= 0x10 && vk <= 0x14) return true;      // Shift/Ctrl/Alt/Pause/CapsLock
            if (vk >= 0x15 && vk <= 0x19) return true;      // 输入法键（Hangul/Hanja 等）
            if (vk == 0x1B) return true;                    // Esc
            if (vk >= 0x1C && vk <= 0x1F) return true;      // IME 转换/无转换键
            if (vk >= 0x21 && vk <= 0x2E) return true;      // PgUp/PgDn/End/Home/方向/Insert/Delete/PrintScreen
            if (vk >= 0x5B && vk <= 0x5F) return true;      // Win/Apps/Sleep
            if (vk >= 0x70 && vk <= 0x87) return true;      // F1..F24（含 F13-F24）
            if (vk >= 0x90 && vk <= 0x93) return true;      // NumLock/ScrollLock 等
            if (vk >= 0xA0 && vk <= 0xA5) return true;      // L/R Shift/Ctrl/Alt
            if (vk >= 0xA6 && vk <= 0xB7) return true;      // 浏览器/音量/媒体键
            return false;
        }
    }
}
