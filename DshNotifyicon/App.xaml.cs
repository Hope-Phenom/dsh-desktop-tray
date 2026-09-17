using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DshNotifyicon.Services;
using Newtonsoft.Json.Linq;

namespace DshNotifyicon
{
    /// <summary>
    /// 应用入口：单实例（Mutex + EventWaitHandle 激活已有实例）、托盘生命周期、
    /// DSH 状态事件接线、--smoke 无 UI 冒烟模式（用于自动化验证）。
    /// </summary>
    public partial class App : Application
    {
        public static AppServices Services;

        const string MutexName = "DshNotifyicon_SingleInstance";
        const string SignalName = "DshNotifyicon_ShowSignal";

        Mutex _mutex;
        EventWaitHandle _showSignal;
        bool _smoke;
        bool _exitStopDone;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 全局兜底：任何线程的未处理异常都落盘（配合 legacyUnhandledExceptionPolicy，
            // 线程池异常不再直接杀死进程），便于后续诊断迭代
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                WriteCrashLog(args.ExceptionObject as Exception, "AppDomain");

            _smoke = e.Args != null && Array.IndexOf(e.Args, "--smoke") >= 0;

            if (_smoke)
            {
                RunSmoke();
                return;
            }

            bool firstRun = !File.Exists(SettingsService.SettingsPath);
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                try { EventWaitHandle.OpenExisting(SignalName).Set(); } catch { }
                Shutdown();
                return;
            }
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            // 每次启动写一条分隔行，把日志按运行场次切开
            LogFile.Write("===== DshNotifyicon " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version + " 启动 =====");
            var watcher = new Thread(() =>
            {
                while (_showSignal.WaitOne())
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(() => Services.Main.ShowOrActivate()));
                    }
                    catch { }
                }
            });
            watcher.IsBackground = true;
            watcher.Start();

            var settings = SettingsService.Load();
            // 界面语言：auto = 跟随系统；显式 zh/en 覆盖。在创建任何 UI 前应用。
            Loc.Apply(settings.Language);
            Services = new AppServices(settings);

            var actions = new TrayActions
            {
                Start = () => Services.Main.StartFromTray(),
                Stop = () => Services.Main.StopFromTray(),
                Restart = () => Services.Main.RestartFromTray(),
                OpenUi = () => Services.Main.OpenUiFromTray(),
                CopyUrl = () => Services.Main.CopyUrlFromTray(),
                ShowWindow = () => Services.Main.ShowOrActivate(),
                ShowEnv = () => Services.Main.ShowEnvTab(),
                Exit = () => ExitWithDsh(),
                ToggleAutoStart = (v) => Services.ToggleAutoStart(v)
            };
            Services.Tray = new TrayIcon(actions, settings);

            WireDshEvents();

            if (settings.AutoStartDshOnLaunch)
            {
                Dispatcher.BeginInvoke(new Action(() => Services.Main.StartFromTray()));
            }

            SessionEnding += (s, se) => StopDshSync();

            DispatcherUnhandledException += (s, args) =>
            {
                WriteCrashLog(args.Exception, "Dispatcher");
                try { Services.Tray.ShowBalloon(Loc.T("app.name"), Loc.T("app.errBalloon", args.Exception.Message)); } catch { }
                args.Handled = true;
            };

            // 上次崩溃（24h 内）留下日志时，启动后提示一次，便于配合排查
            try
            {
                var dir = SettingsService.SettingsDir;
                if (Directory.Exists(dir))
                {
                    var recent = Directory.GetFiles(dir, "crash-*.log")
                        .Where(f => File.GetLastWriteTime(f) > DateTime.Now.AddHours(-24)).ToArray();
                    if (recent.Length > 0)
                        Dispatcher.BeginInvoke(new Action(() =>
                            Services.Tray.ShowBalloon(Loc.T("app.name"),
                                Loc.T("app.crashRecent", string.Join("; ", recent.Select(f => System.IO.Path.GetFileName(f)))))));
                }
            }
            catch { }

            if (firstRun || settings.ShowMainWindowOnStartup)
                Services.Main.ShowOrActivate();
        }

        /// <summary>崩溃/异常落盘：异常详情 + 最近日志快照 → %APPDATA%\DshNotifyicon\crash-*.log，并托盘提示路径。</summary>
        void WriteCrashLog(Exception ex, string source)
        {
            try
            {
                var dir = SettingsService.SettingsDir;
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                var sb = new StringBuilder();
                sb.AppendLine("[source] " + source);
                sb.AppendLine("[time] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("[exception] " + (ex != null ? ex.ToString() : "null"));
                sb.AppendLine("[version] " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);
                if (Services != null)
                {
                    sb.AppendLine("[recent-log]");
                    try { foreach (var l in Services.Dsh.SnapshotLog()) sb.AppendLine(l); } catch { }
                }
                File.WriteAllText(path, sb.ToString());
                try
                {
                    if (Services != null && Services.Tray != null)
                        Services.Tray.ShowBalloon(Loc.T("app.name"), Loc.T("app.crashInternal", path));
                }
                catch { }
            }
            catch { }
        }

        void WireDshEvents()
        {
            Services.Dsh.StateChanged += state =>
            {
                Services.Tray.SetState(state, Services.Dsh.Url);
                Services.Main.UpdateServiceState(state);
            };
            Services.Dsh.Ready += url =>
            {
                Services.Tray.SetState(DshState.Running, url);
                Services.Tray.ShowBalloon(Loc.T("app.startedTitle"), Loc.T("app.startedText", url));
                // 打开浏览器要用带启动令牌的入口：dsh web 的裸根路径没有会话 cookie 时回 401
                if (Services.Settings.AutoOpenBrowser) Services.OpenUrl(Services.Dsh.BrowserUrl ?? url);
            };
            Services.Dsh.Exited += info =>
            {
                Services.Tray.SetState(DshState.Idle, null);
                Services.Tray.ShowBalloon(Loc.T("app.exitedTitle"), info);
            };
            // dsh 输出已由 DshProcessManager 落盘，这里只送界面（TraceDshLog 不重复写文件）
            Services.Dsh.LogLine += line => Services.Main.TraceDshLog(line);
            Services.Dsh.Notification += OnDshNotification;
        }

        /// <summary>处理 dsh 通知增强插件输出。</summary>
        void OnDshNotification(string json)
        {
            try
            {
                var s = Services.Settings;
                if (!s.EnableNotifications) return;

                var obj = JObject.Parse(json);
                var sessionId = (string)obj["sessionId"] ?? "";
                var parentSessionId = (string)obj["parentSessionId"];
                var turn = obj["turn"] != null ? obj["turn"].Value<int>() : 0;
                var reason = (string)obj["reason"] ?? "unknown";
                var durationMs = obj["durationMs"] != null ? obj["durationMs"].Value<long>() : 0L;
                var sessionTitle = (string)obj["title"] ?? "";
                if (string.IsNullOrEmpty(sessionTitle)) sessionTitle = Loc.T("notify.untitled");

                var notifyTitle = parentSessionId == null
                    ? Loc.T("notify.turnEndTitle")
                    : Loc.T("notify.subTurnEndTitle");
                var text = parentSessionId == null
                    ? Loc.T("notify.turnEndText", sessionTitle, sessionId, turn, reason, FormatDuration(durationMs))
                    : Loc.T("notify.subTurnEndText", sessionTitle, sessionId, parentSessionId, turn, reason, FormatDuration(durationMs));

                if (s.EnableTrayNotification)
                    Services.Tray.ShowBalloon(notifyTitle, text);

                if (s.EnableExternalHook && !string.IsNullOrWhiteSpace(s.ExternalHookCommand))
                {
                    var args = ExpandHookTemplate(s.ExternalHookArguments, obj);
                    _ = RunExternalHookAsync(s.ExternalHookCommand, args);
                }
            }
            catch (Exception ex)
            {
                Services.Main.TraceLog(Loc.T("notify.parseFail", ex.Message));
            }
        }

        async Task RunExternalHookAsync(string command, string arguments)
        {
            try
            {
                await ProcessRunner.RunAsync(new ProcessSpec
                {
                    FileName = command,
                    Arguments = arguments,
                    TimeoutMs = 30000
                }, CancellationToken.None, null);
            }
            catch (Exception ex)
            {
                Services.Main.TraceLog(Loc.T("notify.hookFail", ex.Message));
            }
        }

        static string ExpandHookTemplate(string template, JObject data)
        {
            if (string.IsNullOrEmpty(template)) return "";
            var turn = data["turn"];
            var durationMs = data["durationMs"];
            var s = template
                .Replace("{event}", (string)data["event"] ?? "")
                .Replace("{title}", (string)data["title"] ?? "")
                .Replace("{sessionId}", (string)data["sessionId"] ?? "")
                .Replace("{parentSessionId}", (string)data["parentSessionId"] ?? "")
                .Replace("{turn}", turn == null ? "" : turn.ToString())
                .Replace("{reason}", (string)data["reason"] ?? "")
                .Replace("{durationMs}", durationMs == null ? "" : durationMs.ToString());
            return s;
        }

        static string FormatDuration(long ms)
        {
            if (ms < 1000) return ms + " ms";
            return (ms / 1000.0).ToString("0.0") + " s";
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (Services != null)
            {
                SettingsService.Save(Services.Settings);
                // 如果 ExitWithDsh 已经处理过停止，则不再阻塞等待；
                // 其他退出路径（系统注销/异常退出）仍走兜底 StopDshSync。
                if (!_exitStopDone) StopDshSync();
                if (Services.Tray != null) Services.Tray.Dispose();
            }
            base.OnExit(e);
        }

        /// <summary>
        /// 托盘"退出"：先关闭 dsh 服务再退出，避免遗留用户难以清理的 node 进程。
        /// 异步等待避免阻塞 UI 线程导致“卡死”；超时后也继续退出，
        /// 残留实例会在下次启动时被外部实例扫描发现并提示处理。
        /// </summary>
        async void ExitWithDsh()
        {
            try
            {
                Services.Main.ForceClose();
                await Task.WhenAny(Services.Dsh.StopAsync(), Task.Delay(TimeSpan.FromSeconds(8)));
                _exitStopDone = true;
                Shutdown();
            }
            catch
            {
                try { Environment.Exit(0); } catch { }
            }
        }

        /// <summary>限时停止 dsh（退出/注销时用；子进程独立，不会随本进程消失）。</summary>
        void StopDshSync()
        {
            try
            {
                Task.Run(() => Services.Dsh.StopAsync()).Wait(TimeSpan.FromSeconds(8));
            }
            catch { }
        }

        /// <summary>
        /// --smoke 模式：不创建托盘/窗口，执行环境检查 + dsh 真实启停，
        /// 结果写入 %TEMP%\DshNotifyiconSmoke.txt，退出码 0/1。
        /// 整体在 Task.Run 中执行（无 UI 同步上下文），内部可安全同步等待。
        /// </summary>
        void RunSmoke()
        {
            var sb = new StringBuilder();
            int exit;
            try
            {
                exit = Task.Run(() =>
                {
                    var b = new StringBuilder();
                    int code = 0;
                    try
                    {
                        var settings = new Settings();
                        var envPath = NodeService.RefreshPath();
                        b.AppendLine("== env check ==");
                        var node = NodeService.DetectAsync(settings.NodePath, envPath).GetAwaiter().GetResult();
                        b.AppendLine("nodeExe: " + (node.NodeExe ?? "MISSING"));
                        b.AppendLine("node: " + (node.NodeVersion ?? "?") + " npm: " + (node.NpmVersion ?? "?"));

                        b.AppendLine("== 手动指定位置（override）==");
                        // 目录形式必须解析出同一个 node.exe：用户把安装目录粘进来是常态
                        if (node.NodeExe != null)
                        {
                            var nodeDir = Path.GetDirectoryName(node.NodeExe);
                            var byDir = ToolPath.Resolve(nodeDir, "node.exe").Path;
                            b.AppendLine("resolve(dir=" + nodeDir + ") -> " + (byDir ?? "MISSING"));
                            if (!string.Equals(byDir, node.NodeExe, StringComparison.OrdinalIgnoreCase))
                            {
                                b.AppendLine("ASSERT FAIL: 目录形式没有解析出同一个 node.exe");
                                code = 1;
                            }
                        }
                        // 名字不对的文件必须被拒：选错程序的常见情形，不能"存在就当 node 用"
                        var wrongName = Path.Combine(Path.GetTempPath(), "dsh-smoke-not-a-node.exe");
                        try
                        {
                            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), wrongName, true);
                            var wr = ToolPath.Resolve(wrongName, "node.exe");
                            b.AppendLine("resolve(wrong name) -> " + (wr.Path ?? "REJECTED") + " reason=" + (wr.Error ?? "-"));
                            if (wr.Path != null) { b.AppendLine("ASSERT FAIL: 名字不对的文件没有被拒"); code = 1; }
                        }
                        catch (Exception ex) { b.AppendLine("  (名字不对用例跳过: " + ex.Message + ")"); }
                        finally { try { File.Delete(wrongName); } catch { } }

                        // 故意无效的指定：必须回退到自动检测，并带上原因（界面据此点名提示）
                        var bogus = Path.Combine(Path.GetTempPath(), "dsh-no-such-tool-dir");
                        b.AppendLine("bogus override: " + bogus + " (exists=" + Directory.Exists(bogus) + ")");
                        var fallback = NodeService.DetectAsync(bogus, envPath).GetAwaiter().GetResult();
                        b.AppendLine("node override(bogus) -> " + (fallback.NodeExe ?? "MISSING") + " issue=" + (fallback.OverrideIssue ?? "-"));
                        if (fallback.OverrideIssue == null)
                        {
                            b.AppendLine("ASSERT FAIL: 无效指定没有带出原因");
                            code = 1;
                        }
                        if (!string.Equals(fallback.NodeExe, node.NodeExe, StringComparison.OrdinalIgnoreCase))
                        {
                            b.AppendLine("ASSERT FAIL: 无效指定没有回退到与自动检测相同的结果");
                            code = 1;
                        }
                        // 名字对但跑不起来（空文件）同样必须被拒：只查存在性会在这里放行
                        var deadDir = Path.Combine(Path.GetTempPath(), "dsh-smoke-dead-node");
                        try
                        {
                            Directory.CreateDirectory(deadDir);
                            File.WriteAllText(Path.Combine(deadDir, "node.exe"), "");
                            var dead = NodeService.DetectAsync(deadDir, envPath).GetAwaiter().GetResult();
                            b.AppendLine("node override(dead exe) -> " + (dead.NodeExe ?? "MISSING") + " issue=" + (dead.OverrideIssue ?? "-"));
                            if (dead.OverrideIssue == null) { b.AppendLine("ASSERT FAIL: 跑不起来的同名文件没有被拒"); code = 1; }
                            if (!string.Equals(dead.NodeExe, node.NodeExe, StringComparison.OrdinalIgnoreCase))
                            { b.AppendLine("ASSERT FAIL: 同名无效文件没有回退"); code = 1; }
                        }
                        catch (Exception ex) { b.AppendLine("  (同名无效用例跳过: " + ex.Message + ")"); }
                        finally { try { Directory.Delete(deadDir, true); } catch { } }

                        // 空指定 = 自动检测，绝不能被当成"无效指定"（否则界面会无端报警）
                        var empty = NodeService.DetectAsync("", envPath).GetAwaiter().GetResult();
                        if (empty.OverrideIssue != null)
                        {
                            b.AppendLine("ASSERT FAIL: 空指定被误判为无效");
                            code = 1;
                        }
                        var pnpmBogus = NpmService.FindPnpmAsync(envPath, bogus).GetAwaiter().GetResult();
                        b.AppendLine("pnpm override(bogus) -> " + (pnpmBogus.Path ?? "MISSING") + " issue=" + (pnpmBogus.OverrideIssue ?? "-"));
                        if (pnpmBogus.OverrideIssue == null)
                        {
                            b.AppendLine("ASSERT FAIL: pnpm 无效指定没有带出原因");
                            code = 1;
                        }
                        var pnpmAuto = NpmService.FindPnpmAsync(envPath, "").GetAwaiter().GetResult();
                        b.AppendLine("pnpm override(empty) -> " + (pnpmAuto.Path ?? "MISSING") + " issue=" + (pnpmAuto.OverrideIssue ?? "-"));
                        if (pnpmAuto.OverrideIssue != null)
                        {
                            b.AppendLine("ASSERT FAIL: pnpm 空指定被误判为无效");
                            code = 1;
                        }
                        // 回归点：被拒的指定绝不能进入交给子进程的 PATH。旧实现把它前置进全局 PATH，
                        // 于是"回退自动检测"又沿着同一条 PATH 把被拒的文件捡回来：体检假 ✓、
                        // dsh 拿着坏 node 启动失败、子进程里的裸 pnpm 命中坏 shim。
                        var deadPnpmDir = Path.Combine(Path.GetTempPath(), "dsh-smoke-dead-pnpm");
                        try
                        {
                            Directory.CreateDirectory(deadPnpmDir);
                            File.WriteAllText(Path.Combine(deadPnpmDir, "pnpm.cmd"), "@echo off\r\n");
                            var childPath = NpmService.ChildPathAsync(envPath, deadPnpmDir).GetAwaiter().GetResult();
                            b.AppendLine("child path (dead pnpm override) head=" + childPath.Split(';')[0]);
                            if (childPath.IndexOf(deadPnpmDir, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                b.AppendLine("ASSERT FAIL: 被拒的指定进入了子进程 PATH");
                                code = 1;
                            }
                        }
                        catch (Exception ex) { b.AppendLine("  (被拒指定不进 PATH 的用例跳过: " + ex.Message + ")"); }
                        finally { try { Directory.Delete(deadPnpmDir, true); } catch { } }
                        // 反向对照：可用的手动指定 pnpm 必须进子进程 PATH（否则 dsh 的 spawnSync("pnpm") 找不到它）
                        if (pnpmAuto.Path != null)
                        {
                            var dir = Path.GetDirectoryName(pnpmAuto.Path);
                            var childPath2 = NpmService.ChildPathAsync(envPath, pnpmAuto.Path).GetAwaiter().GetResult();
                            if (!childPath2.StartsWith(dir + ";", StringComparison.OrdinalIgnoreCase))
                            {
                                b.AppendLine("ASSERT FAIL: 可用的手动指定 pnpm 没有进入子进程 PATH");
                                code = 1;
                            }
                        }
                        // 回归点：NpmService 必须真的读到手动指定的 node。历史上这里写死过 null，
                        // 结果是"体检显示指定了 node，镜像源/dsh 版本却仍走 PATH"。
                        // 用真 node.exe（能通过 --version 校验）放在没有 npm-cli.js 的临时目录里：
                        // override 生效时 npm 会因找不到 npm-cli.js 报错；被忽略时反而会成功。
                        var fakeDir = Path.Combine(Path.GetTempPath(), "dsh-smoke-node-link");
                        var link = Path.Combine(fakeDir, "node.exe");
                        var prevOverride = ToolPathOverrides.Current;
                        string npmErr = null;
                        bool linked = false;
                        try
                        {
                            if (node.NodeExe != null)
                            {
                                Directory.CreateDirectory(fakeDir);
                                linked = MaterializeNode(link, node.NodeExe);
                                if (!linked) b.AppendLine("  (既建不了硬链接也拷不了 node.exe，跳过该回归点)");
                            }
                            if (linked)
                            {
                                ToolPathOverrides.Current = new Settings { NodePath = fakeDir };
                                try { NpmService.GetRegistryAsync("", envPath).GetAwaiter().GetResult(); }
                                catch (Exception ex) { npmErr = ex.Message; }
                            }
                        }
                        catch (Exception ex) { b.AppendLine("  (npm override 回归点跳过: " + ex.Message + ")"); }
                        finally
                        {
                            ToolPathOverrides.Current = prevOverride;
                            try { Directory.Delete(fakeDir, true); } catch { }
                        }
                        if (linked)
                        {
                            b.AppendLine("npm under hardlinked node override -> " + (npmErr ?? "NO ERROR"));
                            // 只要 override 生效，这个调用就必然失败（该目录没有 npm-cli.js）。
                            // 不匹配错误文案：那样断言就与本地化字符串耦合了。
                            if (npmErr == null)
                            {
                                b.AppendLine("ASSERT FAIL: NpmService 没有使用手动指定的 node");
                                code = 1;
                            }
                        }
                        var reg = NpmService.GetRegistryAsync("", envPath).GetAwaiter().GetResult();
                        b.AppendLine("registry: " + reg.Trim());
                        var local = NpmService.GetDshLocalVersionAsync(envPath).GetAwaiter().GetResult();
                        var latest = NpmService.GetDshLatestVersionAsync("", envPath).GetAwaiter().GetResult();
                        b.AppendLine("dsh local: " + (local.Length > 0 ? local : "MISSING") + " latest: " + latest.Trim());
                        var binJs = NpmService.ResolveDshBinJsAsync(envPath).GetAwaiter().GetResult();
                        b.AppendLine("dsh binJs: " + (binJs ?? "MISSING"));
                        var dshVer = DshCapabilities.VersionFromBinJs(binJs);
                        b.AppendLine("dsh version: " + (dshVer.Length > 0 ? dshVer : "UNKNOWN") + " no-open: " + DshCapabilities.SupportsNoOpen(dshVer));

                        b.AppendLine("== dsh start/stop (random port) ==");
                        var mgr = new DshProcessManager();
                        mgr.LogLine += line => b.AppendLine("  [dsh] " + line);
                        var pre = mgr.PreflightAsync(0, true).GetAwaiter().GetResult();
                        b.AppendLine("preflight: " + pre.Kind + " (instances=" + pre.Instances.Count + ")");
                        var started = mgr.StartAsync(0, true, "", node.NodeExe, binJs, envPath).GetAwaiter().GetResult();
                        b.AppendLine("start result: " + started + " url: " + mgr.Url);
                        if (!started) code = 1;
                        if (mgr.Url != null)
                        {
                            bool ok = HttpProbe(mgr.Url + "/");
                            b.AppendLine("http GET " + mgr.Url + "/ : " + ok);
                            if (!ok) code = 1;
                        }
                        mgr.StopAsync().GetAwaiter().GetResult();
                        b.AppendLine("stop done, state=" + mgr.State);
                        if (mgr.State != DshState.Idle) code = 1;
                        b.AppendLine("== SMOKE " + (code == 0 ? "PASS" : "FAIL") + " ==");
                    }
                    catch (Exception ex)
                    {
                        code = 1;
                        b.AppendLine("== SMOKE FAILED ==");
                        b.AppendLine(ex.ToString());
                    }
                    sb.Append(b.ToString());
                    return code;
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                exit = 1;
                sb.AppendLine("== SMOKE FAILED ==");
                sb.AppendLine(ex.ToString());
            }
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "DshNotifyiconSmoke.txt"), sb.ToString());
            }
            catch { }
            Environment.Exit(exit);
        }

        /// <summary>
        /// 在指定位置放一个真 node.exe（冒烟回归点用）：优先硬链接（瞬时、零占用），
        /// 硬链接不可用（某些文件系统/受限环境）时退化为拷贝 —— 宁可慢一点，
        /// 也不能让这条回归守卫静默消失。
        /// </summary>
        static bool MaterializeNode(string link, string realNode)
        {
            try
            {
                if (File.Exists(link)) File.Delete(link);
                ProcessRunner.RunAsync(new ProcessSpec
                {
                    FileName = "cmd.exe",
                    Arguments = "/c mklink /H " + ProcessRunner.Quote(link) + " " + ProcessRunner.Quote(realNode),
                    TimeoutMs = 20000
                }, CancellationToken.None, null).GetAwaiter().GetResult();
                if (File.Exists(link)) return true;
            }
            catch { }
            try
            {
                File.Copy(realNode, link, true);
                return File.Exists(link);
            }
            catch { return false; }
        }

        static bool HttpProbe(string url)
        {
            try
            {
                using (var http = new System.Net.Http.HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(5);
                    var resp = http.GetAsync(url).GetAwaiter().GetResult();
                    return resp.IsSuccessStatusCode;
                }
            }
            catch { return false; }
        }
    }
}
