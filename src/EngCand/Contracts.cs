using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>候选类型：单词 / 短语。</summary>
    public enum CandidateKind
    {
        Word = 0,
        Phrase = 1
    }

    /// <summary>
    /// 一条候选项。Text 是展示文本；InsertText 是选中后真正插入的文本
    /// （插入时会替换掉当前正在输入的词 prefix，见 Program.Pick）。
    /// </summary>
    public sealed class Candidate
    {
        public string Text;
        public string InsertText;
        public CandidateKind Kind;
        public int Score;
        public string Source;

        public Candidate() { }

        public Candidate(string text, string insertText, CandidateKind kind, int score, string source)
        {
            Text = text ?? "";
            InsertText = insertText ?? text ?? "";
            Kind = kind;
            Score = score;
            Source = source ?? "";
        }

        public override string ToString()
        {
            return Text + " [" + Kind + "," + Score + "," + Source + "]";
        }
    }

    /// <summary>English 词缓冲快照，供候选引擎查询。</summary>
    public sealed class TypingState
    {
        /// <summary>当前正在输入的单词（小写，只含 a-z ' -），可能为空。</summary>
        public string CurrentWord = "";
        /// <summary>上一个已完成的单词（小写），可能为空。</summary>
        public string PreviousWord = "";
        /// <summary>上上个已完成的单词（小写），可能为空。</summary>
        public string PreviousWord2 = "";
        /// <summary>true 表示刚打完一个词并输入了分隔符（空格/标点），当前词为空。</summary>
        public bool AtWordStartAfterSpace;
        /// <summary>true 表示当前词是句子（或行）的第一个词。</summary>
        public bool SentenceStart;
        /// <summary>true 表示用户输入当前词时首字母是大写。</summary>
        public bool CurrentWordCapitalized;

        public TypingState Clone()
        {
            TypingState s = new TypingState();
            s.CurrentWord = CurrentWord;
            s.PreviousWord = PreviousWord;
            s.PreviousWord2 = PreviousWord2;
            s.AtWordStartAfterSpace = AtWordStartAfterSpace;
            s.SentenceStart = SentenceStart;
            s.CurrentWordCapitalized = CurrentWordCapitalized;
            return s;
        }
    }

    /// <summary>候选引擎接口（由 Lead 实现：Core/CandidateEngine.cs）。</summary>
    public interface ICandidateEngine
    {
        bool Ready { get; }
        int WordCount { get; }
        int PhraseCount { get; }

        /// <summary>根据当前输入状态返回最多 max 条候选（已排序，第 0 条为最佳）。</summary>
        IList<Candidate> Query(TypingState state, int max);

        /// <summary>用户选中了某条候选（用于学习/统计）。</summary>
        void OnCandidateSelected(TypingState state, Candidate candidate);

        /// <summary>用户完整输入并提交了一个单词（未选候选）。</summary>
        void OnWordCommitted(string word);
    }

    /// <summary>
    /// 全局键盘钩子事件参数。IsDown=false 表示 KeyUp。
    /// Handled=true 时该按键会被吞掉（不会传给目标程序）；KeyDown 被吞掉后，
    /// 对应的 KeyUp 也会被自动吞掉（KeyboardHook 负责处理）。
    /// </summary>
    public sealed class HookKeyEventArgs : EventArgs
    {
        public int VkCode;
        public Keys Key;
        public bool IsDown;
        public bool IsInjected;
        public bool Ctrl;
        public bool Alt;
        public bool Shift;
        public bool Win;
        public bool Handled;
    }
}
