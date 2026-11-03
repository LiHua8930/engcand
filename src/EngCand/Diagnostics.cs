using System;
using System.IO;
using System.Text;

namespace EngCand
{
    /// <summary>轻量日志。默认写 %APPDATA%\EngCand\engcand.log，单文件超过 2MB 自动轮转。</summary>
    public sealed class Diagnostics
    {
        readonly object gate = new object();
        readonly bool verbose;
        public string LogPath { get; private set; }

        public Diagnostics(string appDir, bool verbose)
        {
            this.verbose = verbose;
            try
            {
                Directory.CreateDirectory(appDir);
                string path = Path.Combine(appDir, "engcand.log");
                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        string bak = Path.Combine(appDir, "engcand.old.log");
                        if (File.Exists(bak)) File.Delete(bak);
                        File.Move(path, bak);
                    }
                }
                catch
                {
                }
                LogPath = path;
            }
            catch
            {
                LogPath = null;
            }
        }

        void Write(string level, string msg)
        {
            try
            {
                string line = DateTime.Now.ToString("MM-dd HH:mm:ss.fff") + " [" + level + "] " + msg + Environment.NewLine;
                lock (gate)
                {
                    if (LogPath != null) File.AppendAllText(LogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        public void Info(string msg) { Write("INFO", msg); }
        public void Warn(string msg) { Write("WARN", msg); }
        public void Error(string msg) { Write("ERR ", msg); }
        public void Debug(string msg) { if (verbose) Write("DBG ", msg); }

        public static Diagnostics Current;
    }
}
