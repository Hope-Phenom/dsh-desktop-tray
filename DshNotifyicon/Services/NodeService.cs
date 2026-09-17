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
        /// <summary>手动指定了位置但校验不通过的原因（null = 未指定或通过）。NodeExe 此时来自自动检测。</summary>
        public string OverrideIssue;
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

    /// <summary>结构解析结果：Path = 命中文件（null = 未命中）；Error = 未命中的原因（null = 未指定或解析成功）。</summary>
    public class ToolPathResult
    {
        public string Path;
        public string Error;
    }

    /// <summary>完整校验结果：Error 为 null 才算通过；Version = 探到的工具版本。</summary>
    public class ToolCheck
    {
        public string Path;
        public string Version;
        public string Error;
    }

    /// <summary>
    /// "手动指定位置"的解析与校验。输入既可以是可执行文件本身，也可以是它所在的目录
    /// （用户往往直接把安装目录粘进来）。
    ///
    /// 分两层，因为成本差一个量级：
    /// 1) Resolve —— 只看文件名与存在性，不启动进程（拼 PATH 时会被频繁调用）；
    /// 2) CheckAsync —— 再真跑一次 --version 确认它确实是那个工具。
    /// 只做存在性判断是不够的：同名空文件、残缺安装、被替换成别的程序都会"看起来正常"，
    /// 然后在使用处报一个与指定位置毫不相干的错。两层都把原因带出来，由调用方回退并点名。
    /// </summary>
    public static class ToolPath
    {
        /// <summary>node 的版本探测超时（正常远小于 1s）。</summary>
        public const int NodeProbeTimeoutMs = 15000;

        /// <summary>
        /// pnpm 的版本探测超时：corepack 托管的 pnpm.cmd 首次运行要自己下载，
        /// 给足时间，免得把一个能用（只是首次慢）的 pnpm 误判成无效。
        /// 对话框与体检必须用同一个值，否则同一个位置会得出两种结论。
        /// </summary>
        public const int PnpmProbeTimeoutMs = 30000;

        /// <summary>
        /// 结构解析。目录 → 逐个候选文件名找；文件 → 文件名必须就是候选名之一
        /// （常见错误是在同一目录里点了别的 exe）。空输入不算错误（= 未指定）。
        /// </summary>
        public static ToolPathResult Resolve(string input, params string[] fileNames)
        {
            var r = new ToolPathResult();
            var v = PathGuard.StripQuotes(input);
            if (v.Length == 0) return r; // 未指定
            if (!PathGuard.IsSafe(v)) { r.Error = Loc.T("path.illegal"); return r; }
            try
            {
                if (Directory.Exists(v))
                {
                    foreach (var name in fileNames)
                    {
                        var p = Path.Combine(v, name);
                        if (File.Exists(p)) { r.Path = p; return r; }
                    }
                    r.Error = Loc.T("path.noExeInDir", string.Join(" / ", fileNames), v);
                    return r;
                }
                if (!File.Exists(v)) { r.Error = Loc.T("path.notExist", v); return r; }
                var actual = Path.GetFileName(v);
                foreach (var name in fileNames)
                {
                    if (string.Equals(actual, name, StringComparison.OrdinalIgnoreCase)) { r.Path = v; return r; }
                }
                // 名字不对就直接到此为止：不必再去跑它（选到记事本这类 GUI 程序会白等一个超时）
                r.Error = Loc.T("path.wrongName", string.Join(" / ", fileNames), actual);
                return r;
            }
            catch { r.Error = Loc.T("path.illegal"); return r; } // 非法字符等：Path.Combine 会抛
        }

        /// <summary>
        /// 结构解析 + 权威校验：真的把它跑起来问版本。--version 输出里任一行能解析成版本号才算数，
        /// 名字对但跑不起来（空文件、残缺安装、同名占位程序）同样会被挡住。
        /// Error = null 表示未指定或通过。
        /// </summary>
        public static async Task<ToolCheck> CheckAsync(string input, int timeoutMs, params string[] fileNames)
        {
            var r = Resolve(input, fileNames);
            var c = new ToolCheck { Path = r.Path, Error = r.Error };
            if (c.Path == null) return c;
            c.Version = await ProbeVersionAsync(c.Path, timeoutMs);
            if (c.Version == null)
            {
                // 点名被拒的那个文件（pnpm 的候选名有两个，写死第一个会把 pnpm.cmd 说成 pnpm.exe）
                var actual = Path.GetFileName(c.Path);
                c.Path = null;
                c.Error = Loc.T("path.notRunnable", actual);
            }
            return c;
        }

        /// <summary>
        /// 跑 &lt;exe&gt; --version，返回输出里第一个能解析成版本号的行。
        /// 跑不起来、超时、输出不像版本号一律返回 null。
        /// .cmd/.bat 必须经 cmd.exe 才能启动（CreateProcess 不认批处理）。
        /// 这里有两处讲究：
        /// 1) cmd.exe 取绝对路径 —— 本工具专门跑在 PATH 可能损坏的机器上，裸 "cmd.exe" 解析不到
        ///    就会把一个完全正常的 pnpm.cmd 判成无效；
        /// 2) 用 cmd 的规范形式 /d /s /c ""&lt;path&gt;" --version"，路径必须被成对引号包住：
        ///    否则 /c 之后 cmd 的剥引号规则会把含 &amp; ( ) 的路径截断成另一条命令（实测）。
        /// 已知限制：路径里若含 %（会被 cmd 当变量展开）仍可能出问题，Windows 工具路径里属病态情形。
        /// </summary>
        public static async Task<string> ProbeVersionAsync(string exePath, int timeoutMs)
        {
            try
            {
                var ext = (Path.GetExtension(exePath) ?? "").ToLowerInvariant();
                var spec = ext == ".cmd" || ext == ".bat"
                    ? new ProcessSpec
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                        Arguments = "/d /s /c \"\"" + exePath + "\" --version\"",
                        TimeoutMs = timeoutMs
                    }
                    : new ProcessSpec { FileName = exePath, Arguments = "--version", TimeoutMs = timeoutMs };
                var r = await ProcessRunner.RunAsync(spec, CancellationToken.None, null);
                if (r.TimedOut || r.Cancelled || r.ExitCode != 0) return null;
                foreach (var raw in (r.Output ?? "").Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length > 0 && Semver.IsValid(line)) return line;
                }
                return null;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// 当前生效的"手动指定路径"。持有活的 Settings 引用而不是复制字段值：
    /// 用户在环境页改完立刻生效，不会留下过期副本。由 AppServices 构造时注入；
    /// --smoke 等没有设置对象的场景保持 null = 全部自动检测。
    /// </summary>
    public static class ToolPathOverrides
    {
        /// <summary>当前设置对象。写发生在 UI 线程（构造/环境页），读发生在后台线程，故标 volatile。</summary>
        public static volatile Settings Current;

        public static string Node { get { return Current == null ? "" : Current.NodePath; } }
        public static string Pnpm { get { return Current == null ? "" : Current.PnpmPath; } }
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
        /// 检测 node/npm：手动指定优先（node.exe 或其所在目录都接受，无效则记 OverrideInvalid
        /// 并回退），再扫描刷新后的 PATH（envPath，Node 安装后立即生效），
        /// 最后扫当前进程 PATH 与常见安装路径；读取版本。
        /// </summary>
        public static async Task<NodeInfo> DetectAsync(string nodeExeOverride = null, string envPath = null)
        {
            var info = new NodeInfo();
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 脏候选（含非法路径字符）在这里就被挡掉：拼路径前过滤，而不是等 Path.Combine 抛异常
            Action<string> addCand = (p) => { if (PathGuard.IsSafe(p) && seen.Add(p)) candidates.Add(p); };
            string overrideVersion = null;
            try
            {
                // 手动指定优先，但必须校验通过（文件名对 + 真能跑出版本号）；不通过就回退自动检测，
                // 并把原因带出去让体检点名——静默回退会让用户以为指定的那个 node 正在被使用。
                var check = await ToolPath.CheckAsync(nodeExeOverride, ToolPath.NodeProbeTimeoutMs, "node.exe");
                info.OverrideIssue = check.Error;
                overrideVersion = check.Version;
                addCand(check.Path);
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

            // 校验手动指定时已经跑过一次 --version，别重复启动进程
            info.NodeVersion = overrideVersion;
            try
            {
                if (info.NodeVersion == null)
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
