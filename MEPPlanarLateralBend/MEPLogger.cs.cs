using System;
using System.IO;
using System.Text;

namespace MEPTools.LateralBend
{
    /// <summary>
    /// 轻量日志调试类：把调试信息同时累积到内存（供 TaskDialog 弹窗展示）
    /// 并即时按行写入本地文本文件（供崩溃后回看）。
    /// 单例使用：MEPLogger.Instance。所有文件写入均包 try/catch，
    /// 绝不因日志 I/O 异常影响 Revit 命令主体流程。
    /// 运行环境：C# / Revit 2022 二次开发。
    /// </summary>
    public sealed class MEPLogger
    {
        private static readonly MEPLogger _instance = new MEPLogger();
        /// <summary>全局单例。</summary>
        public static MEPLogger Instance { get { return _instance; } }

        private readonly object _lock = new object();
        private readonly StringBuilder _sb = new StringBuilder();
        private StreamWriter _writer;
        private string _logFilePath;

        private MEPLogger() { }

        /// <summary>本次会话日志文件完整路径（未开启时为空）。</summary>
        public string LogFilePath { get { return _logFilePath; } }

        /// <summary>
        /// 开启一次会话日志：在 %LocalAppData%\MEPTools\LateralBendLogs 下
        /// 生成带时间戳的 .log 文件并写表头。重复调用会先关闭上一会话。
        /// </summary>
        public void StartSession(string title)
        {
            lock (_lock)
            {
                try
                {
                    CloseWriter();

                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MEPTools", "LateralBendLogs");
                    Directory.CreateDirectory(dir);

                    string name = "lateral_bend_" +
                        DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log";
                    _logFilePath = Path.Combine(dir, name);

                    _writer = new StreamWriter(_logFilePath, false, new UTF8Encoding(false))
                    { AutoFlush = true };

                    AppendLine("==================================================");
                    AppendLine(" MEP 45 Lateral Bend - Debug Log");
                    AppendLine(" Title   : " + title);
                    AppendLine(" Time    : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    AppendLine(" Revit   : 2022 (net48)");
                    AppendLine(" LogFile : " + _logFilePath);
                    AppendLine("==================================================");
                }
                catch
                {
                    // 日志初始化失败时静默：仅内存累积，不影响主流程
                    _writer = null;
                    AppendLine("[LOG] 文件日志初始化失败，仅使用内存调试信息。");
                }
            }
        }

        /// <summary>追加一行（带时间戳）到内存与文件。</summary>
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
                    catch
                    {
                        // 单行写入失败不影响后续，最多丢一行
                    }
                }
            }
        }

        /// <summary>无参换行（与 StringBuilder.AppendLine() 对齐，便于无侵入替换）。</summary>
        public void AppendLine()
        {
            AppendLine(string.Empty);
        }

        /// <summary>结束会话：写尾并关闭文件流。务必在命令 finally 中调用。</summary>
        public void EndSession(string summary)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(summary))
                    AppendLine("==================================================");
                if (!string.IsNullOrEmpty(summary))
                    AppendLine(" RESULT: " + summary);
                if (!string.IsNullOrEmpty(_logFilePath))
                    AppendLine(" LogFile: " + _logFilePath);
                AppendLine("==================================================");
                CloseWriter();
            }
        }

        /// <summary>返回本次会话的全部内存文本（供 TaskDialog 展示）。</summary>
        public string GetFullText()
        {
            lock (_lock) { return _sb.ToString(); }
        }

        /// <summary>追加一行异常详情（含堆栈）。</summary>
        public void LogException(string tag, Exception ex)
        {
            if (ex == null) return;
            AppendLine(string.Format("[EXC] {0}: {1}", tag, ex.Message));
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
