using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EngCand
{
    /// <summary>用户选词学习记录，保存在 %APPDATA%\EngCand\learning.json。由 Lead 维护。</summary>
    public sealed class LearningStore
    {
        readonly string path;
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool dirty;
        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = false };

        public LearningStore(string appDir)
        {
            try
            {
                if (string.IsNullOrEmpty(appDir)) appDir = ".";
                Directory.CreateDirectory(appDir);
                path = Path.Combine(appDir, "learning.json");
            }
            catch
            {
                path = Path.Combine(".", "learning.json");
            }
        }

        public int Count
        {
            get { return counts.Count; }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path);
                Dictionary<string, int> data = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                if (data != null)
                {
                    counts = new Dictionary<string, int>(data, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void Add(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = text.Trim().ToLowerInvariant();
            if (text.Length == 0 || text.Length > 60) return;
            int c;
            counts.TryGetValue(text, out c);
            if (c > 100000) return;
            counts[text] = c + 1;
            dirty = true;
        }

        /// <summary>被选中的候选获得加分（25k/次，封顶 250k）。</summary>
        public int GetBoost(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int c;
            if (!counts.TryGetValue(text.Trim(), out c)) return 0;
            int boost = c * 25000;
            return boost > 250000 ? 250000 : boost;
        }

        public IEnumerable<KeyValuePair<string, int>> Entries()
        {
            List<KeyValuePair<string, int>> snapshot = new List<KeyValuePair<string, int>>(counts);
            return snapshot;
        }

        public void Save()
        {
            if (!dirty) return;
            try
            {
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(counts, JsonOpts));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                dirty = false;
            }
            catch
            {
            }
        }
    }
}
