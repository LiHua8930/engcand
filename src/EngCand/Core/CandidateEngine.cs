using System;
using System.Collections.Generic;
using System.Linq;

namespace EngCand
{
    /// <summary>候选引擎：词典查询 + 排序 + 用户选词学习。由 Lead 维护。</summary>
    public sealed class CandidateEngine : ICandidateEngine
    {
        readonly string dataDir;
        readonly string appDir;
        readonly AppConfig cfg;
        readonly Diagnostics diag;

        Lexicon lex;
        WordTrie wordTrie;
        PhraseTrie phraseTrie;
        LearningStore learning;
        HashSet<string> knownWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<Lexicon.NextEntry>> phraseByFirst =
            new Dictionary<string, List<Lexicon.NextEntry>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<Lexicon.NextEntry>> phraseByFirstTwo =
            new Dictionary<string, List<Lexicon.NextEntry>>(StringComparer.OrdinalIgnoreCase);
        int maxBigramScore = 1;

        public CandidateEngine(string dataDir, string appDir, AppConfig cfg, Diagnostics diag)
        {
            this.dataDir = dataDir;
            this.appDir = appDir;
            this.cfg = cfg ?? new AppConfig();
            this.diag = diag;
        }

        public bool Ready { get; private set; }
        public int WordCount { get; private set; }
        public int PhraseCount { get; private set; }

        public void Initialize()
        {
            try
            {
                lex = Lexicon.Load(dataDir, diag);
                wordTrie = new WordTrie();
                foreach (Lexicon.WordEntry w in lex.Words)
                {
                    wordTrie.Add(w);
                    knownWords.Add(w.Word);
                }

                phraseTrie = new PhraseTrie();
                foreach (Lexicon.PhraseEntry p in lex.Phrases)
                {
                    phraseTrie.Add(p);
                    BuildPhraseContinuation(p);
                }

                foreach (KeyValuePair<string, List<Lexicon.NextEntry>> kv in lex.NextByWord)
                {
                    foreach (Lexicon.NextEntry e in kv.Value)
                        if (e.Score > maxBigramScore) maxBigramScore = e.Score;
                }

                learning = new LearningStore(appDir);
                learning.Load();

                Ready = lex.LoadedFromFile && lex.Words.Count > 0;
                WordCount = lex.Words.Count;
                PhraseCount = lex.Phrases.Count;
                if (diag != null)
                {
                    diag.Info("引擎初始化: ready=" + Ready + " words=" + WordCount + " phrases=" + PhraseCount +
                              " bigramHeads=" + lex.NextByWord.Count + " learned=" + learning.Count);
                }
            }
            catch (Exception ex)
            {
                Ready = false;
                WordCount = 0;
                PhraseCount = 0;
                if (diag != null) diag.Error("引擎初始化失败: " + ex);
            }
        }

        void BuildPhraseContinuation(Lexicon.PhraseEntry p)
        {
            string[] toks = p.Phrase.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length < 2) return;

            string rem1 = string.Join(" ", toks, 1, toks.Length - 1);
            AddNext(phraseByFirst, p.FirstToken, new Lexicon.NextEntry
            {
                Next = toks[1],
                Insert = rem1,
                Score = p.Score,
                IsPhrase = true
            });

            if (toks.Length >= 3)
            {
                string key2 = toks[0] + " " + toks[1];
                string rem2 = string.Join(" ", toks, 2, toks.Length - 2);
                AddNext(phraseByFirstTwo, key2, new Lexicon.NextEntry
                {
                    Next = toks[2],
                    Insert = rem2,
                    Score = p.Score,
                    IsPhrase = true
                });
            }
        }

        static void AddNext(Dictionary<string, List<Lexicon.NextEntry>> map, string key, Lexicon.NextEntry e)
        {
            if (string.IsNullOrEmpty(key)) return;
            List<Lexicon.NextEntry> list;
            if (!map.TryGetValue(key, out list))
            {
                list = new List<Lexicon.NextEntry>();
                map[key] = list;
            }
            if (list.Count < 24) list.Add(e);
        }

        // ------------------------------------------------------------------
        // 查询
        // ------------------------------------------------------------------
        public IList<Candidate> Query(TypingState state, int max)
        {
            List<Candidate> result = new List<Candidate>();
            if (state == null || max <= 0) return result;

            List<Candidate> words = new List<Candidate>();
            List<Candidate> phrases = new List<Candidate>();
            string prefix = (state.CurrentWord ?? "").ToLowerInvariant().Trim();

            if (prefix.Length > 0)
            {
                if (cfg.SuggestWords) QueryWordsByPrefix(prefix, words);
                if (cfg.SuggestPhrases) QueryPhrasesByPrefix(prefix, phrases);
            }
            else if (state.AtWordStartAfterSpace && state.PreviousWord.Length >= 2)
            {
                QueryNextWord(state, words, phrases);
            }

            // 合并排序
            List<Candidate> all = new List<Candidate>(words.Count + phrases.Count);
            all.AddRange(words);
            all.AddRange(phrases);
            all.Sort(delegate (Candidate a, Candidate b) { return b.Score.CompareTo(a.Score); });

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Candidate c in all)
            {
                string key = (c.InsertText ?? "").Trim();
                if (key.Length == 0) continue;
                if (!seen.Add(key)) continue;
                result.Add(c);
                if (result.Count >= max) break;
            }

            // 保证短语至少占一席（用户明确要英文短语候选）
            if (cfg.SuggestPhrases && phrases.Count > 0 && result.Count >= 2 && !HasPhrase(result))
            {
                Candidate best = phrases[0];
                string key = (best.InsertText ?? "").Trim();
                bool already = result.Any(c => string.Equals((c.InsertText ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase));
                if (!already) result[result.Count - 1] = best;
            }
            return result;
        }

        static bool HasPhrase(List<Candidate> list)
        {
            foreach (Candidate c in list) if (c.Kind == CandidateKind.Phrase) return true;
            return false;
        }

        void QueryWordsByPrefix(string prefix, List<Candidate> words)
        {
            int maxScore = 1;
            foreach (Lexicon.WordEntry e in wordTrie.Prefix(prefix, 48))
            {
                int norm = NormWord(e.Score);
                if (norm > maxScore) maxScore = norm;
                string display = e.Word;
                int boost = cfg.LearnUserSelections ? learning.GetBoost(e.Word) : 0;
                words.Add(new Candidate(display, e.Word, CandidateKind.Word, norm + boost, "word"));
            }

            // 用户学过、但不在词典里的词
            HashSet<string> have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Candidate c in words) have.Add(c.InsertText);
            foreach (KeyValuePair<string, int> kv in learning.Entries())
            {
                string t = kv.Key;
                if (t.IndexOf(' ') >= 0) continue;
                if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (have.Contains(t)) continue;
                int score = 50000 + Math.Min(250000, kv.Value * 25000);
                words.Add(new Candidate(t, t, CandidateKind.Word, score, "learned"));
            }
        }

        void QueryPhrasesByPrefix(string prefix, List<Candidate> phrases)
        {
            foreach (Lexicon.PhraseEntry p in phraseTrie.Prefix(prefix, 24))
            {
                int norm = (int)(NormPhrase(p.Score) * 0.92);
                int boost = cfg.LearnUserSelections ? learning.GetBoost(p.Phrase) : 0;
                phrases.Add(new Candidate(p.Phrase, p.Phrase, CandidateKind.Phrase, norm + boost, "phrase"));
            }
        }

        void QueryNextWord(TypingState state, List<Candidate> words, List<Candidate> phrases)
        {
            string prev = (state.PreviousWord ?? "").ToLowerInvariant();
            string prev2 = (state.PreviousWord2 ?? "").ToLowerInvariant();
            if (prev.Length < 2) return;

            // 1) 二元组：下一个词
            List<Lexicon.NextEntry> list;
            if (lex.NextByWord.TryGetValue(prev, out list))
            {
                foreach (Lexicon.NextEntry e in list)
                {
                    int norm = (int)((long)e.Score * 1000000L / Math.Max(1, maxBigramScore));
                    int boost = cfg.LearnUserSelections ? learning.GetBoost(e.Next) : 0;
                    words.Add(new Candidate(e.Next, e.Next, CandidateKind.Word, (int)(norm * 0.9) + boost, "next"));
                }
            }

            // 2) 短语续写：只打了一个词，例如 "thank " → "you very much"
            List<Lexicon.NextEntry> plist;
            if (phraseByFirst.TryGetValue(prev, out plist))
            {
                foreach (Lexicon.NextEntry e in plist)
                {
                    int norm = (int)(NormPhrase(e.Score) * 0.85);
                    phrases.Add(new Candidate(e.Insert, e.Insert, CandidateKind.Phrase, norm, "phrase-next1"));
                }
            }

            // 3) 短语续写：打了两个词，例如 "as soon " → "as possible"
            if (prev2.Length >= 1)
            {
                List<Lexicon.NextEntry> p2;
                if (phraseByFirstTwo.TryGetValue(prev2 + " " + prev, out p2))
                {
                    foreach (Lexicon.NextEntry e in p2)
                    {
                        int norm = (int)(NormPhrase(e.Score) * 0.85);
                        phrases.Add(new Candidate(e.Insert, e.Insert, CandidateKind.Phrase, norm, "phrase-next2"));
                    }
                }
            }
        }

        int NormWord(int score)
        {
            return (int)((long)score * 1000000L / Math.Max(1, lex.MaxWordScore));
        }

        int NormPhrase(int score)
        {
            return (int)((long)score * 1000000L / Math.Max(1, lex.MaxPhraseScore));
        }

        // ------------------------------------------------------------------
        // 学习
        // ------------------------------------------------------------------
        public void OnCandidateSelected(TypingState state, Candidate candidate)
        {
            if (!cfg.LearnUserSelections || candidate == null || learning == null) return;
            string text = candidate.Text;
            if (string.IsNullOrEmpty(text)) return;
            if (candidate.Kind == CandidateKind.Phrase) learning.Add(text);
            else learning.Add(text);
            learning.Save();
            if (diag != null) diag.Debug("learned: " + text);
        }

        public void OnWordCommitted(string word)
        {
            if (!cfg.LearnUserSelections || learning == null || string.IsNullOrEmpty(word)) return;
            word = word.ToLowerInvariant();
            if (word.Length < 3) return;
            if (knownWords.Contains(word)) return;   // 词典里已有的常见词不需要额外学习
            learning.Add(word);
            learning.Save();
        }

        public void SaveLearning()
        {
            if (learning != null) learning.Save();
        }

        // ==================================================================
        // 前缀 Trie
        // ==================================================================
        sealed class WordTrie
        {
            sealed class Node
            {
                public Dictionary<char, Node> Children;
                public List<Lexicon.WordEntry> Top;
            }

            const int Cap = 48;
            readonly Node root = new Node();

            public void Add(Lexicon.WordEntry e)
            {
                if (e == null || string.IsNullOrEmpty(e.Word)) return;
                Node node = root;
                string w = e.Word;
                for (int i = 0; i < w.Length; i++)
                {
                    if (node.Children == null) node.Children = new Dictionary<char, Node>();
                    char c = w[i];
                    Node next;
                    if (!node.Children.TryGetValue(c, out next))
                    {
                        next = new Node();
                        node.Children[c] = next;
                    }
                    node = next;
                    AddTop(node, e);
                }
            }

            static void AddTop(Node node, Lexicon.WordEntry e)
            {
                if (node.Top == null) node.Top = new List<Lexicon.WordEntry>(8);
                if (node.Top.Count < Cap)
                {
                    node.Top.Add(e);
                    return;
                }
                int min = 0;
                for (int i = 1; i < node.Top.Count; i++)
                    if (node.Top[i].Score < node.Top[min].Score) min = i;
                if (node.Top[min].Score >= e.Score) return;
                node.Top[min] = e;
            }

            public List<Lexicon.WordEntry> Prefix(string prefix, int max)
            {
                List<Lexicon.WordEntry> result = new List<Lexicon.WordEntry>();
                if (string.IsNullOrEmpty(prefix)) return result;
                Node node = root;
                for (int i = 0; i < prefix.Length; i++)
                {
                    if (node.Children == null) return result;
                    Node next;
                    if (!node.Children.TryGetValue(prefix[i], out next)) return result;
                    node = next;
                }
                if (node.Top == null) return result;
                result.AddRange(node.Top);
                result.Sort(delegate (Lexicon.WordEntry a, Lexicon.WordEntry b) { return b.Score.CompareTo(a.Score); });
                if (result.Count > max) result.RemoveRange(max, result.Count - max);
                return result;
            }
        }

        sealed class PhraseTrie
        {
            sealed class Node
            {
                public Dictionary<char, Node> Children;
                public List<Lexicon.PhraseEntry> Top;
            }

            const int Cap = 24;
            readonly Node root = new Node();

            public void Add(Lexicon.PhraseEntry e)
            {
                if (e == null || string.IsNullOrEmpty(e.FirstToken)) return;
                Node node = root;
                string w = e.FirstToken;
                for (int i = 0; i < w.Length; i++)
                {
                    if (node.Children == null) node.Children = new Dictionary<char, Node>();
                    char c = w[i];
                    Node next;
                    if (!node.Children.TryGetValue(c, out next))
                    {
                        next = new Node();
                        node.Children[c] = next;
                    }
                    node = next;
                    AddTop(node, e);
                }
            }

            static void AddTop(Node node, Lexicon.PhraseEntry e)
            {
                if (node.Top == null) node.Top = new List<Lexicon.PhraseEntry>(4);
                if (node.Top.Count < Cap)
                {
                    node.Top.Add(e);
                    return;
                }
                int min = 0;
                for (int i = 1; i < node.Top.Count; i++)
                    if (node.Top[i].Score < node.Top[min].Score) min = i;
                if (node.Top[min].Score >= e.Score) return;
                node.Top[min] = e;
            }

            public List<Lexicon.PhraseEntry> Prefix(string prefix, int max)
            {
                List<Lexicon.PhraseEntry> result = new List<Lexicon.PhraseEntry>();
                if (string.IsNullOrEmpty(prefix)) return result;
                Node node = root;
                for (int i = 0; i < prefix.Length; i++)
                {
                    if (node.Children == null) return result;
                    Node next;
                    if (!node.Children.TryGetValue(prefix[i], out next)) return result;
                    node = next;
                }
                if (node.Top == null) return result;
                result.AddRange(node.Top);
                result.Sort(delegate (Lexicon.PhraseEntry a, Lexicon.PhraseEntry b) { return b.Score.CompareTo(a.Score); });
                if (result.Count > max) result.RemoveRange(max, result.Count - max);
                return result;
            }
        }
    }
}
