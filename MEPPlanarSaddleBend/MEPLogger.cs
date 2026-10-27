using System;
using System.IO;
using System.Text;

namespace MEPTools.MEPPlanarSaddleBend
{
    /// <summary>
    /// 轻量日志：内存累积 + 即时写文件。所有 I/O 都 try/catch，
    /// 绝不因日志异常影响主流程。
    /// </summary>
    public sealed class MEPLogger
    {
        private static readonly MEPLogger _instance = new MEPLogger();
        public static MEPLogger Instance { get { return _instance; } }

        private readonly object _lock = new object();
        private readonly StringBuilder _sb = new StringBuilder();
        private StreamWriter _writer;
        private string _logFilePath;

        private MEPLogger() { }

        public string LogFilePath { get { return _logFilePath; } }

        public void StartSession(string title)
        {
            lock (_lock)
            {
                try
                {
                    CloseWriter();
                    _sb.Clear();

                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MEPTools", "SaddleBendLogs");
                    Directory.CreateDirectory(dir);

                    string name = "saddle_bend_" +
                        DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log";
                    _logFilePath = Path.Combine(dir, name);

                    _writer = new StreamWriter(_logFilePath, false, new UTF8Encoding(false))
                    { AutoFlush = true };

                    AppendLine("==================================================");
                    AppendLine(" MEP Saddle Bend - Debug Log");
                    AppendLine(" Title   : " + title);
                    AppendLine(" Time    : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    AppendLine(" Revit   : 2022 (net48)");
                    AppendLine(" LogFile : " + _logFilePath);
                    AppendLine("==================================================");
                }
                catch
                {
                    _writer = null;
                    AppendLine("[LOG] 文件日志初始化失败，仅使用内存调试信息。");
                }
            }
        }

        public void AppendLine(string text)
        {
            lock (_lock)
            {
                string line = (text == null) ? "" : text;
                _sb.AppendLine(line);
                if (_writer != null)
                {
                    try
                    {
                        _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line);
                    }
                    catch { }
                }
            }
        }

        public void AppendLine() { AppendLine(string.Empty); }

        public void EndSession(string summary)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(summary))
                {
                    AppendLine("==================================================");
                    AppendLine(" RESULT: " + summary);
                }
                if (!string.IsNullOrEmpty(_logFilePath))
                    AppendLine(" LogFile: " + _logFilePath);
                AppendLine("==================================================");
                CloseWriter();
            }
        }

        public string GetFullText()
        {
            lock (_lock) { return _sb.ToString(); }
        }

        public void LogException(string tag, Exception ex)
        {
            if (ex == null) return;
            AppendLine("[EXC] " + tag + ": " + ex.Message);
            AppendLine("[EXC] Stack: " + ex.StackTrace);
            if (ex.InnerException != null)
                AppendLine("[EXC] Inner: " + ex.InnerException.Message);
        }

        private void CloseWriter()
        {
            if (_writer != null)
            {
                try { _writer.Flush(); _writer.Dispose(); }
                catch { }
                _writer = null;
            }
        }
    }
}