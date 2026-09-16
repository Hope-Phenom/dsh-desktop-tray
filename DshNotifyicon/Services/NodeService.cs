using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace DshNotifyicon.Services
{
    public class NodeInfo
    {
        /// <summary>node.exe 完整路径；null = 未安装。</summary>
        public string NodeExe;
        public string NodeVersion;
        public string NpmVersion;
        /// <summary>npm-cli.js 完整路径（node 直调 npm 用）。</summary>
        public string NpmCliJs;
    }

    /// <summary>
    /// 路径段体检。要点：.NET Framework 的 Path.Combine / Path.GetDirectoryName 会校验非法路径
    /// 字符（" &lt; &gt; | 与 0x00-0x1F），而 File.Exists / Directory.Exists 不校验。
    /// 于是 PATH 里只要有一段脏数据（安装器写入的带引号条目、手改 PATH 留下的控制字符……），
    /// Path.Combine(dir, "node.exe") 就会抛 ArgumentException("路径中具有非法字符。")：
    /// 因为异常发生在 DshProcessManager 第一条日志之前，日志里只剩启动横幅，
    /// 界面上表现为"启动失败/检查失败: 路径中具有非法字符。"，工具整体不可用。
    /// 数字、空格、中文都不在非法字符集内，所以纯数字用户名本身无害。
    /// 结论：所有来自 PATH / 环境变量的路径段，动手拼路径前必须先过这里。
    /// </summary>
    public static class PathGuard
    {
        static readonly char[] Invalid = Path.GetInvalidPathChars();

        /// <summary>该路径段不含 .NET 非法路径字符（空段返回 false，调用方按"跳过"处理）。</summary>
        public static bool IsSafe(string segment)
        {
            return !string.IsNullOrEmpty(segment) && segment.IndexOfAny(Invalid) < 0;
        }

        /// <summary>
        /// 去掉环境变量值外层可能存在的成对引号。setx DSH_HOME "\"D:\dsh\"" 这类写法会把引号
        /// 一起写进值里，直接拿去拼路径必然触发"路径中具有非法字符。"。
        /// </summary>
        public static string StripQuotes(string value)
        {
            var v = (value ?? "").Trim();
            if (v.Length >= 2 && v[0] == '"')
            {
                var end = v.IndexOf('"', 1);
                if (end > 1) return v.Substring(1, end - 1).Trim();
            }
            return v;
        }

        /// <summary>控制字符渲染成 &lt;0x0A&gt; 形式，否则在日志/界面里根本看不见。</summary>
        public static string Describe(string segment)
        {
            var sb = new StringBuilder();
            foreach (var c in segment ?? "")
            {
                if (c < 0x20) sb.Append("<0x" + ((int)c).ToString("X2") + ">");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 列出含非法字符的 PATH 条目。按 HKCU → HKLM → 当前进程取值，展开环境变量后去重，
        /// 便于体检直接点名问题出在哪一层（只读注册表，不需要管理员）。
        /// </summary>
        public static List<string> UnsafeSegments()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            Add(seen, list, "HKCU", Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User));
            Add(seen, list, "HKLM", Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine));
            Add(seen, list, "ENV", Environment.GetEnvironmentVariable("Path"));
            return list;
        }

        static void Add(HashSet<string> seen, List<string> list, string scope, string pathEnv)
        {
            if (string.IsNullOrEmpty(pathEnv)) return;
            foreach (var seg in pathEnv.Split(';'))
            {
                if (seg.Trim().Length == 0) continue; // 结尾分号等空段不算问题
                var expanded = Environment.ExpandEnvironmentVariables(seg.Trim());
                if (IsSafe(expanded)) continue;
                if (seen.Add(expanded)) list.Add("[" + scope + "] " + Describe(expanded));
            }
        }
    }

    /// <summary>
    /// Node.js 运行环境：检测（PATH + 常见路径兜底）、winget/MSI 一键安装、PATH 刷新。
    /// </summary>
    public static class NodeService
    {
        /// <summary>
        /// 刷新 PATH：合并注册表用户/系统 Path（REG_EXPAND_SZ 展开）与当前进程 PATH，去重。
        /// winget/MSI 安装 Node 后，新 PATH 对后续子进程立即可见。
        /// </summary>
        public static string RefreshPath()
        {
            var parts = new List<string>();
            Action<string> add = (v) =>
            {
                if (string.IsNullOrEmpty(v)) return;
                foreach (var seg in v.Split(';'))
                {
                    var s = seg.Trim();
                    // 含 .NET 非法路径字符的脏段一律丢弃：它既不可能解析出可执行文件，
                    // 又会让后续 Path.Combine 直接抛"路径中具有非法字符。"（详见 PathGuard）
                    if (s.Length > 0 && PathGuard.IsSafe(s)) parts.Add(s);
                }
            };
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey("Environment"))
                {
                    if (k != null) add(Environment.ExpandEnvironmentVariables((string)k.GetValue("Path", "")));
                }
            }
            catch { }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
                {
                    if (k != null) add(Environment.ExpandEnvironmentVariables((string)k.GetValue("Path", "")));
                }
            }
            catch { }
            add(Environment.GetEnvironmentVariable("Path"));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var merged = new List<string>();
            foreach (var p in parts) if (seen.Add(p)) merged.Add(p);
            return string.Join(";", merged);
        }

        /// <summary>
        /// 检测 node/npm：优先扫描刷新后的 PATH（envPath，Node 安装后立即生效），
        /// 再扫当前进程 PATH 与常见安装路径；读取版本。
        /// </summary>
        public static async Task<NodeInfo> DetectAsync(string nodeExeOverride = null, string envPath = null)
        {
            var info = new NodeInfo();
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 脏候选（含非法路径字符）在这里就被挡掉：拼路径前过滤，而不是等 Path.Combine 抛异常
            Action<string> addCand = (p) => { if (PathGuard.IsSafe(p) && seen.Add(p)) candidates.Add(p); };
            try
            {
                if (!string.IsNullOrEmpty(nodeExeOverride)) addCand(nodeExeOverride);
                AddFromPath(envPath, addCand);
                AddFromPath(Environment.GetEnvironmentVariable("Path"), addCand);
                addCand(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"));
                addCand(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"));
                addCand(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm", "node.exe"));
            }
            catch { } // 兜底：候选路径构建绝不阻断检测（脏段已被 PathGuard 过滤）

            string nodeExe = null;
            foreach (var c in candidates)
            {
                if (File.Exists(c)) { nodeExe = c; break; }
            }
            if (nodeExe == null) return info;
            info.NodeExe = nodeExe;

            try
            {
                var npmCli = Path.Combine(Path.GetDirectoryName(nodeExe), "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(npmCli)) info.NpmCliJs = npmCli;
            }
            catch { } // GetDirectoryName 同样校验非法字符：拿不到 npm-cli.js 也不该中断检测

            try
            {
                info.NodeVersion = (await ProcessRunner.RunAsync(
                    new ProcessSpec { FileName = nodeExe, Arguments = "--version", TimeoutMs = 15000 },
                    CancellationToken.None, null)).Output.Trim();
            }
            catch { }
            if (info.NpmCliJs != null)
            {
                try
                {
                    info.NpmVersion = (await ProcessRunner.RunAsync(
                        new ProcessSpec
                        {
                            FileName = nodeExe,
                            Arguments = ProcessRunner.Quote(info.NpmCliJs) + " --version",
                            TimeoutMs = 15000
                        },
                        CancellationToken.None, null)).Output.Trim();
                }
                catch { }
            }
            return info;
        }

        static void AddFromPath(string pathEnv, Action<string> addCand)
        {
            if (string.IsNullOrEmpty(pathEnv)) return;
            foreach (var seg in pathEnv.Split(';'))
            {
                var dir = seg.Trim();
                if (dir.Length == 0) continue;
                // 必须先过滤再 Path.Combine：脏段会让 Combine 抛"路径中具有非法字符。"，
                // 这条链上没有任何 catch，一路冒到界面的"启动失败/检查失败"弹窗。
                if (!PathGuard.IsSafe(dir)) continue;
                addCand(Path.Combine(dir, "node.exe"));
            }
        }

        /// <summary>
        /// 一键安装 Node.js：优先 winget（OpenJS.NodeJS.LTS），失败回退官方 MSI（index.json 取最新 LTS）。
        /// 安装后调用方需 RefreshPath() 再检测。
        /// </summary>
        public static async Task<bool> InstallNodeAsync(Action<string> log, CancellationToken ct)
        {
            // 1) winget
            try
            {
                log(Loc.T("node.wingetTry"));
                var r = await ProcessRunner.RunAsync(new ProcessSpec
                {
                    FileName = "winget.exe",
                    Arguments = "install --id OpenJS.NodeJS.LTS -e --silent --accept-package-agreements --accept-source-agreements --disable-interactivity",
                    TimeoutMs = 15 * 60 * 1000
                }, ct, log);
                if (!r.TimedOut && !r.Cancelled && r.ExitCode == 0) return true;
                log(Loc.T("node.wingetFail", r.ExitCode));
            }
            catch (Exception ex)
            {
                log(Loc.T("node.wingetUnavailable", ex.Message));
            }

            // 2) 官方 MSI
            try
            {
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(60);
                    log(Loc.T("node.fetchLts"));
                    var idx = await http.GetStringAsync("https://nodejs.org/dist/index.json");
                    string version = null;
                    foreach (var item in JArray.Parse(idx))
                    {
                        if (item["lts"] != null && item["lts"].Type != JTokenType.Null)
                        {
                            version = (string)item["version"];
                            break;
                        }
                    }
                    if (version == null) { log(Loc.T("node.ltsFail")); return false; }
                    var url = "https://nodejs.org/dist/" + version + "/node-" + version + "-x64.msi";
                    var msi = Path.Combine(Path.GetTempPath(), "node-" + version + "-x64.msi");
                    log(Loc.T("node.download", url));
                    var bytes = await http.GetByteArrayAsync(url);
                    File.WriteAllBytes(msi, bytes);
                    log(Loc.T("node.downloadDone"));
                    var mr = await ProcessRunner.RunAsync(new ProcessSpec
                    {
                        FileName = "msiexec.exe",
                        Arguments = "/i " + ProcessRunner.Quote(msi) + " /qn",
                        TimeoutMs = 10 * 60 * 1000
                    }, ct, log);
                    if (mr.ExitCode == 0 || mr.ExitCode == 3010) return true;
                    if (mr.ExitCode == 1602)
                        log(Loc.T("node.installCancelled", Loc.T("node.downloadUrl")));
                    else
                        log(Loc.T("node.msiFail", mr.ExitCode, Loc.T("node.downloadUrl")));
                    return false;
                }
            }
            catch (Exception ex)
            {
                log(Loc.T("node.msiFailMsg", ex.Message));
                return false;
            }
        }
    }
}
