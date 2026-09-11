using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DshNotifyicon.Services
{
    /// <summary>
    /// 极简滚动日志：%APPDATA%\DshNotifyicon\DshNotifyicon.log（与 settings.json、crash-*.log 同目录，
    /// 托盘"打开设置目录"即可看到）。单文件上限 128 KB，超限轮转为 .1，只保留一份备份。
    /// 一条一行、带毫秒时间戳；启动令牌等敏感参数落盘前打码。
    /// 任何写入异常都被吞掉：日志失败绝不影响主流程。
    /// </summary>
    public static class LogFile
    {
        const long MaxBytes = 128 * 1024;
        const int MaxLineChars = 1000;

        static readonly object Gate = new object();
        /// <summary>UTF-8 带 BOM：日志含中文，便于记事本/PowerShell 5.1 等本机工具直接读对。</summary>
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(true);
        /// <summary>dsh 启动横幅里的 ?token=… 是浏览器会话凭证，不落盘。</summary>
        static readonly Regex TokenRx = new Regex(@"(token=)[A-Za-z0-9_\-]+", RegexOptions.Compiled);
        static long _bytes = -1;

        /// <summary>日志文件路径。</summary>
        public static string Path
        {
            get { return System.IO.Path.Combine(SettingsService.SettingsDir, "DshNotifyicon.log"); }
        }

        /// <summary>追加一行日志（自动补时间戳 + 换行）。</summary>
        public static void Write(string line)
        {
            try
            {
                if (string.IsNullOrEmpty(line)) return;
                var text = TokenRx.Replace(line, "$1***");
                if (text.Length > MaxLineChars) text = text.Substring(0, MaxLineChars) + "…(截断)";
                text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + text + Environment.NewLine;

                lock (Gate)
                {
                    var path = Path;
                    if (_bytes < 0)
                    {
                        Directory.CreateDirectory(SettingsService.SettingsDir);
                        var fi = new FileInfo(path);
                        _bytes = fi.Exists ? fi.Length : 0;
                    }
                    if (_bytes > MaxBytes) Rotate(path);
                    File.AppendAllText(path, text, Utf8);
                    _bytes += Utf8.GetByteCount(text);
                }
            }
            catch { } // 磁盘不可写等：放弃写文件，界面日志照旧
        }

        /// <summary>把当前文件挪成 .1（覆盖旧备份）；失败也不丢内容，下次写入时再试。</summary>
        static void Rotate(string path)
        {
            try
            {
                var bak = path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(path, bak);
            }
            catch { }
            try { _bytes = File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { _bytes = 0; }
        }
    }
}
