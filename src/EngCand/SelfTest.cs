using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace EngCand
{
    /// <summary>
    /// 自检模式（--selftest）：加载词典、跑若干前缀查询，把结果写到
    /// %APPDATA%\EngCand\selftest.log 和 exe 同目录 selftest.log；退出码 0=通过。
    /// </summary>
    internal static class SelfTest
    {
        public static void Run(string dataDir, string appDir, Diagnostics diag)
        {
            StringBuilder sb = new StringBuilder();
            bool ok = true;
            try
            {
                AppConfig cfg = new AppConfig();
                CandidateEngine engine = new CandidateEngine(dataDir, appDir, cfg, diag);
                engine.Initialize();
                sb.AppendLine("dataDir = " + (dataDir ?? "(null)"));
                sb.AppendLine("ready = " + engine.Ready);
                sb.AppendLine("words = " + engine.WordCount + "  phrases = " + engine.PhraseCount);
                if (!engine.Ready) ok = false;

                string[] samples = new string[] { "th", "he", "hel", "go", "con", "pro", "imp", "as", "thank", "wonder" };
                foreach (string s in samples)
                {
                    TypingState st = new TypingState();
                    st.CurrentWord = s;
                    IList<Candidate> list = engine.Query(st, 8);
                    sb.AppendLine("query[" + s + "] => " + Format(list));
                    if (list.Count == 0) ok = false;
                }

                TypingState empty = new TypingState();
                empty.CurrentWord = "";
                empty.PreviousWord = "thank";
                empty.AtWordStartAfterSpace = true;
                IList<Candidate> next = engine.Query(empty, 6);
                sb.AppendLine("next[thank] => " + Format(next));

                TypingState cap = new TypingState();
                cap.CurrentWord = "hel";
                cap.CurrentWordCapitalized = true;
                IList<Candidate> capList = engine.Query(cap, 5);
                sb.AppendLine("cap[hel] => " + Format(capList));

                sb.AppendLine("RESULT: " + (ok ? "PASS" : "FAIL"));
            }
            catch (Exception ex)
            {
                ok = false;
                sb.AppendLine("EXCEPTION: " + ex);
                sb.AppendLine("RESULT: FAIL");
            }

            string text = sb.ToString();
            try
            {
                Directory.CreateDirectory(appDir);
                File.WriteAllText(Path.Combine(appDir, "selftest.log"), text, Encoding.UTF8);
            }
            catch
            {
            }
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.log"), text, Encoding.UTF8);
            }
            catch
            {
            }
            if (diag != null) diag.Info("selftest:\n" + text);
            Environment.ExitCode = ok ? 0 : 1;
            Environment.Exit(Environment.ExitCode);
        }

        static string Format(IList<Candidate> list)
        {
            if (list == null || list.Count == 0) return "(empty)";
            return string.Join(" | ", list.Select(c => c.Text + "{" + (c.Kind == CandidateKind.Phrase ? "P" : "W") + "," + c.Score + "}"));
        }
    }
}
