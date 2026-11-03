using System;
using System.IO;
using System.Text.Json;

namespace EngCand
{
    /// <summary>用户配置（保存在 %APPDATA%\EngCand\config.json）。</summary>
    public sealed class AppConfig
    {
        /// <summary>总开关（托盘菜单/热键可切换）。</summary>
        public bool Enabled = true;

        /// <summary>最多显示多少条候选（1-9）。</summary>
        public int MaxCandidates = 8;

        /// <summary>至少输入几个字母才弹候选。</summary>
        public int MinPrefixLength = 2;

        public bool SuggestWords = true;
        public bool SuggestPhrases = true;

        /// <summary>打完一个词+空格后，是否联想下一个词/短语（默认关闭，避免打扰）。</summary>
        public bool SuggestNextWord = false;

        /// <summary>弹候选前的延迟毫秒数（等待输入法先处理按键，防止中文模式下误弹）。</summary>
        public int PopupDelayMs = 70;

        /// <summary>弹出候选时是否播放提示音（默认否）。</summary>
        public bool PlaySound = false;

        /// <summary>是否学习用户的选择，动态提升常用词。</summary>
        public bool LearnUserSelections = true;

        /// <summary>Sogou 正在组词（中文模式）时用于识别的窗口类名前缀。</summary>
        public string[] SogouComposeWindowPrefixes = new string[] { "SoPY_Comp", "SoPY_UI" };

        /// <summary>当前工作模式：auto（自动识别搜狗中/英模式）、english（强制按英文）、chinese（强制不弹）。</summary>
        public string Mode = "auto";

        /// <summary>是否在英文模式下也尝试用 IMM 转换状态做辅助判断。</summary>
        public bool UseImmFallback = true;

        public AppConfig Clone()
        {
            return (AppConfig)this.MemberwiseClone();
        }
    }

    public static class ConfigStore
    {
        static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions { WriteIndented = true };

        public static string PathFor(string appDir)
        {
            return Path.Combine(appDir, "config.json");
        }

        public static AppConfig Load(string appDir)
        {
            try
            {
                string p = PathFor(appDir);
                if (File.Exists(p))
                {
                    string json = File.ReadAllText(p);
                    AppConfig cfg = JsonSerializer.Deserialize<AppConfig>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch
            {
            }
            return new AppConfig();
        }

        public static void Save(string appDir, AppConfig cfg)
        {
            try
            {
                Directory.CreateDirectory(appDir);
                File.WriteAllText(PathFor(appDir), JsonSerializer.Serialize(cfg, JsonOpts));
            }
            catch
            {
            }
        }
    }
}
