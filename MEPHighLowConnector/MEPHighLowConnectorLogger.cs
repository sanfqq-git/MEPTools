using System;
using System.IO;
using System.Text;

namespace MEPTools.MEPHighLowConnector
{
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
                        "MEPTools", "ConnectorLogs");
                    Directory.CreateDirectory(dir);
                    string name = "connector_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log";
                    _logFilePath = Path.Combine(dir, name);
                    _writer = new StreamWriter(_logFilePath, false, new UTF8Encoding(false)) { AutoFlush = true };
                    AppendLine("==================================================");
                    AppendLine(" MEP Connector - Debug Log");
                    AppendLine(" Title   : " + title);
                    AppendLine(" Time    : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
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
                    try { _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line); }
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

        public string GetFullText() { lock (_lock) return _sb.ToString(); }

        public void LogException(string tag, Exception ex)
        {
            if (ex == null) return;
            AppendLine("[EXC] " + tag + ": " + ex.Message);
            AppendLine("[EXC] Stack: " + ex.StackTrace);
            if (ex.InnerException != null) AppendLine("[EXC] Inner: " + ex.InnerException.Message);
        }

        private void CloseWriter()
        {
            if (_writer != null)
            {
                try { _writer.Flush(); _writer.Dispose(); } catch { }
                _writer = null;
            }
        }
    }
}