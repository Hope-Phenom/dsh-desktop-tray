using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DshNotifyicon.Services;
using Newtonsoft.Json.Linq;

namespace DshNotifyicon
{
    /// <summary>
    /// 主窗口：环境 / 服务 / 设置 / 通知增强 / 关于 五页。关闭即隐藏到托盘；所有操作经服务层异步执行。
    /// 界面语言：启动时按设置应用（auto = 跟随系统），语言切换后由 Loc.Changed 刷新全部静态文案。
    /// </summary>
    public partial class MainWindow : Window
    {
        bool _allowClose;
        bool _loadingUi;
        const int MaxLogLines = 2000;

        // 长操作（安装/更新）状态
        CancellationTokenSource _opCts;
        bool _opActive;

        public MainWindow()
        {
            InitializeComponent();
            Loc.Changed += (s, e) =>
            {
                try
                {
                    if (Dispatcher.CheckAccess()) ApplyLanguage();
                    else Dispatcher.BeginInvoke(new Action(ApplyLanguage));
                }
                catch { }
            };
            ApplyLanguage();
        }

        /// <summary>静态文案整体刷新（构造时 + 语言切换时调用）。</summary>
        void ApplyLanguage()
        {
            Title = Loc.T("app.name");
            tabEnv.Header = Loc.T("tab.env");
            tabService.Header = Loc.T("tab.service");
            tabSettings.Header = Loc.T("tab.settings");
            tabNotify.Header = Loc.T("tab.notify");
            tabAbout.Header = Loc.T("tab.about");

            btnCheck.Content = Loc.T("env.check");
            gbPath.Header = Loc.T("env.pathGroup");
            gbNode.Header = Loc.T("env.nodeGroup");
            btnInstallNode.Content = Loc.T("env.installNode");
            gbMirror.Header = Loc.T("env.mirrorGroup");
            SetComboItem(cmbMirror, 0, Loc.T("env.mirrorDefault"));
            SetComboItem(cmbMirror, 1, Loc.T("env.mirrorNpmmirror"));
            SetComboItem(cmbMirror, 2, Loc.T("env.mirrorCustom"));
            txtCustomMirror.ToolTip = Loc.T("env.mirrorTooltip");
            btnApplyMirror.Content = Loc.T("env.applyMirror");
            btnGlobalRegistry.Content = Loc.T("env.globalNpmrc");
            gbDsh.Header = Loc.T("env.dshGroup");
            btnInstallDsh.Content = Loc.T("env.installDsh");
            btnCheckUpdate.Content = Loc.T("env.checkUpdate");
            gbPnpm.Header = Loc.T("env.pnpmGroup");
            btnInstallPnpm.Content = Loc.T("env.installPnpm");

            lblPort.Text = Loc.T("svc.port");
            chkRandomPort.Content = Loc.T("svc.randomPort");
            lblBind.Text = Loc.T("svc.bindAddr");
            btnStart.Content = Loc.T("svc.start");
            btnStop.Content = Loc.T("svc.stop");
            btnRestart.Content = Loc.T("svc.restart");
            btnOpenUi.Content = Loc.T("svc.openUi");
            lblLog.Text = Loc.T("svc.logLabel");

            chkAutoOpen.Content = Loc.T("set.autoOpen");
            chkAutoStart.Content = Loc.T("set.autoStart");
            chkShowMain.Content = Loc.T("set.showMain");
            chkAutoStartDsh.Content = Loc.T("set.autoStartDsh");
            lblExitNote.Text = Loc.T("set.exitNote");
            gbLang.Header = Loc.T("set.langGroup");
            SetComboItem(cmbLang, 0, Loc.T("set.langAuto"));
            SetComboItem(cmbLang, 1, Loc.T("set.langZh"));
            SetComboItem(cmbLang, 2, Loc.T("set.langEn"));
            lblLangHint.Text = Loc.T("set.langHint");
            gbTray.Header = Loc.T("set.trayGroup");
            lblDoubleClick.Text = Loc.T("set.doubleClick");
            SetComboItem(cmbTrayDoubleClick, 0, Loc.T("set.doubleClickMain"));
            SetComboItem(cmbTrayDoubleClick, 1, Loc.T("set.doubleClickWeb"));
            lblDoubleClickHint.Text = Loc.T("set.doubleClickHint");
            gbAdvanced.Header = Loc.T("set.advanced");
            lblTrusted.Text = Loc.T("set.trustedHosts");
            lblNodePath.Text = Loc.T("set.nodePath");
            gbCleanup.Header = Loc.T("set.cleanup");
            lblCleanupDesc.Text = Loc.T("set.cleanupDesc");
            btnCleanup.Content = Loc.T("set.cleanupBtn");
            btnSaveSettings.Content = Loc.T("set.save");
            btnOpenSettingsDir.Content = Loc.T("set.openDir");

            chkNotifyEnable.Content = Loc.T("notify.enable");
            chkNotifySubagents.Content = Loc.T("notify.subagents");
            chkNotifyTray.Content = Loc.T("notify.tray");
            gbExternalHook.Header = Loc.T("notify.externalGroup");
            chkNotifyExternal.Content = Loc.T("notify.externalEnable");
            lblHookCommand.Text = Loc.T("notify.command");
            lblHookArgs.Text = Loc.T("notify.arguments");
            lblHookHint.Text = Loc.T("notify.hint");
            btnInstallNotifyPlugin.Content = Loc.T("notify.installPlugin");
            btnUninstallNotifyPlugin.Content = Loc.T("notify.uninstallPlugin");
            btnTestNotify.Content = Loc.T("notify.test");
            btnSaveNotify.Content = Loc.T("notify.save");

            txtAboutName.Text = Loc.T("about.name");
            txtAboutDesc.Text = Loc.T("about.desc");
            gbTech.Header = Loc.T("about.techGroup");
            txtTech1.Text = Loc.T("about.tech1");
            txtTech2.Text = Loc.T("about.tech2");
            txtTech3.Text = Loc.T("about.tech3");
            gbLinks.Header = Loc.T("about.linksGroup");
            runLinkDh.Text = Loc.T("about.linkDh");
            runLinkRepo.Text = Loc.T("about.linkRepo");
            runLinkSite.Text = Loc.T("about.linkSite");
            gbLicense.Header = Loc.T("about.licenseGroup");
            txtLicense.Text = Loc.T("about.license");
            txtCredits.Text = Loc.T("about.credits");
            if (txtAboutVersion.Text.Length == 0) txtAboutVersion.Text = AboutVersionText();
        }

        static void SetComboItem(ComboBox cmb, int index, string content)
        {
            if (cmb.Items.Count > index && cmb.Items[index] is ComboBoxItem)
                ((ComboBoxItem)cmb.Items[index]).Content = content;
        }

        static string AboutVersionText()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            return Loc.T("about.version", v, Environment.Is64BitProcess ? "x64" : "x86");
        }

        // ── 窗口生命周期 ──

        public void ShowOrActivate()
        {
            if (!Dispatcher.CheckAccess())
            {
                try { Dispatcher.BeginInvoke(new Action(ShowOrActivate)); } catch { return; }
                return;
            }
            try
            {
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                Topmost = true;
                Topmost = false;
            }
            catch { }
        }

        public void ShowEnvTab()
        {
            ShowOrActivate();
            tabMain.SelectedIndex = 0;
            _ = RunEnvCheckAsync();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        public void ForceClose()
        {
            _allowClose = true;
            Close();
        }

        void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettingsIntoUi();
            FillLogFromSnapshot();
            txtAboutVersion.Text = AboutVersionText();
            _ = RunEnvCheckAsync();
        }

        void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("link.fail", ex.Message), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            e.Handled = true;
        }

        // ── 环境页 ──

        async void BtnCheck_Click(object sender, RoutedEventArgs e)
        {
            await RunEnvCheckAsync();
        }

        async Task RunEnvCheckAsync()
        {
            try
            {
                btnCheck.IsEnabled = false;
                txtCheckStatus.Text = Loc.T("env.checking");
                var s = App.Services.Settings;
                var items = await EnvironmentCheckService.CheckAllAsync(s, App.Services.EnvPath, line => SetCheckStatus(line));
                ApplyEnvItems(items);
                txtCheckStatus.Text = Loc.T("env.checkDone");
            }
            catch (Exception ex)
            {
                txtCheckStatus.Text = Loc.T("env.checkFailed", ex.Message);
            }
            finally
            {
                btnCheck.IsEnabled = true;
            }
        }

        void SetCheckStatus(string line)
        {
            if (!Dispatcher.CheckAccess())
            {
                try { Dispatcher.BeginInvoke(new Action(() => SetCheckStatus(line))); } catch { return; }
                return;
            }
            try { txtCheckStatus.Text = line; } catch { }
        }

        void ApplyEnvItems(System.Collections.Generic.List<EnvItem> items)
        {
            foreach (var item in items)
            {
                var detail = item.Detail;
                switch (item.Name)
                {
                    case "PATH":
                        txtPath.Text = StatusPrefix(item.Status) + detail;
                        break;
                    case "Node.js":
                        txtNode.Text = StatusPrefix(item.Status) + detail;
                        btnInstallNode.Visibility = item.Status == EnvStatus.Missing ? Visibility.Visible : Visibility.Collapsed;
                        break;
                    case "npm 镜像源":
                        txtRegistry.Text = StatusPrefix(item.Status) + detail;
                        break;
                    case "dsh":
                        txtDsh.Text = StatusPrefix(item.Status) + detail;
                        btnInstallDsh.Content = item.Status == EnvStatus.Missing ? Loc.T("env.installDshShort") : Loc.T("env.updateDshShort");
                        break;
                    case "pnpm":
                        txtPnpm.Text = StatusPrefix(item.Status) + detail;
                        btnInstallPnpm.Visibility = item.Status == EnvStatus.Missing ? Visibility.Visible : Visibility.Collapsed;
                        break;
                }
            }
        }

        static string StatusPrefix(EnvStatus s)
        {
            switch (s)
            {
                case EnvStatus.Ok: return "✓ ";
                case EnvStatus.Missing: return "✗ ";
                case EnvStatus.Outdated: return "↑ ";
                default: return "! ";
            }
        }

        async void BtnInstallNode_Click(object sender, RoutedEventArgs e)
        {
            if (_opActive) { CancelOp(); return; }
            await RunLongOpAsync(
                Loc.T("env.installNodeTitle"),
                btnInstallNode, null,
                async (log, ct) =>
                {
                    var ok = await NodeService.InstallNodeAsync(log, ct);
                    if (!ok) throw new Exception(Loc.T("env.installNodeFail"));
                    App.Services.RefreshEnvPath();
                    log(Loc.T("env.verifyAfterInstall"));
                    // 轮询检测（最多 ~15s），确保新安装的 node 立即可见（走刷新后的 PATH）
                    var s = App.Services.Settings;
                    var node = await NodeService.DetectAsync(s.NodePath, App.Services.EnvPath);
                    for (int i = 0; i < 10 && node.NodeExe == null; i++)
                    {
                        await Task.Delay(1500);
                        node = await NodeService.DetectAsync(s.NodePath, App.Services.EnvPath);
                    }
                    log(node.NodeExe != null
                        ? Loc.T("env.nodeDetected", node.NodeVersion)
                        : Loc.T("env.nodeNotFound"));
                },
                RunEnvCheckAsync);
        }

        void CmbMirror_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            txtCustomMirror.IsEnabled = cmbMirror.SelectedIndex == 2;
        }

        string SelectedMirrorUrl()
        {
            switch (cmbMirror.SelectedIndex)
            {
                case 1: return "https://registry.npmmirror.com";
                case 2: return (txtCustomMirror.Text ?? "").Trim();
                default: return "";
            }
        }

        async void BtnApplyMirror_Click(object sender, RoutedEventArgs e)
        {
            var url = SelectedMirrorUrl();
            if (cmbMirror.SelectedIndex == 2 && url.Length == 0)
            {
                MessageBox.Show(Loc.T("env.customUrlReq"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            App.Services.Settings.MirrorUrl = url;
            SettingsService.Save(App.Services.Settings);
            txtCheckStatus.Text = Loc.T("env.mirrorApplied", url.Length == 0 ? Loc.T("env.followGlobal") : url);
            await RunEnvCheckAsync();
        }

        async void BtnGlobalRegistry_Click(object sender, RoutedEventArgs e)
        {
            var url = SelectedMirrorUrl();
            if (url.Length == 0)
            {
                MessageBox.Show(Loc.T("env.mirrorFirst"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var r = MessageBox.Show(
                Loc.T("env.globalNpmrcConfirm", url),
                Loc.T("env.globalNpmrcTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            try
            {
                await NpmService.SetGlobalRegistryAsync(url, App.Services.EnvPath, line => SetCheckStatus(line));
                txtCheckStatus.Text = Loc.T("env.globalNpmrcDone");
            }
            catch (Exception ex)
            {
                txtCheckStatus.Text = Loc.T("env.globalNpmrcFail", ex.Message);
            }
            await RunEnvCheckAsync();
        }

        async void BtnInstallDsh_Click(object sender, RoutedEventArgs e)
        {
            if (_opActive) { CancelOp(); return; }
            await RunLongOpAsync(
                Loc.T("env.dshInstallTitle"),
                btnInstallDsh, btnCheckUpdate,
                async (log, ct) =>
                {
                    await NpmService.InstallOrUpdateDshAsync(App.Services.Settings.MirrorUrl, App.Services.EnvPath, log, ct);
                    var v = await NpmService.GetDshLocalVersionAsync(App.Services.EnvPath);
                    log(Loc.T("env.installedVersion", v));
                },
                RunEnvCheckAsync);
        }

        async void BtnInstallPnpm_Click(object sender, RoutedEventArgs e)
        {
            if (_opActive) { CancelOp(); return; }
            await RunLongOpAsync(
                Loc.T("env.installPnpmTitle"),
                btnInstallPnpm, null,
                async (log, ct) =>
                {
                    await NpmService.EnsurePnpmAsync(App.Services.Settings.MirrorUrl, App.Services.EnvPath, log, ct);
                    App.Services.RefreshEnvPath();
                    log(Loc.T("env.installedVersion", "pnpm"));
                },
                RunEnvCheckAsync);
        }

        // ── 长操作统一交互（安装/更新）──

        void CancelOp()
        {
            try { if (_opCts != null) _opCts.Cancel(); } catch { }
        }

        /// <summary>
        /// 长操作统一交互：自动切到日志面板（logTab，默认服务页）透传原始输出（滚动+限行）、
        /// 环境页显示不确定进度条与截断状态行、操作按钮变为"取消"（点击即杀进程树）、
        /// 完成后若用户未手动切走标签页则回到 returnTab（默认环境页），再执行 after（通常为重新体检）。
        /// </summary>
        async Task RunLongOpAsync(string title, Button busyButton, Button disableButton,
            Func<Action<string>, CancellationToken, Task> op, Func<Task> after = null,
            int logTab = 1, int returnTab = 0)
        {
            if (_opActive) return;
            _opActive = true;
            _opCts = new CancellationTokenSource();
            var ct = _opCts.Token;
            var origContent = busyButton != null ? busyButton.Content : null;

            Action<string> progressSink = line =>
            {
                TraceLog(line);
                var s = line.Length > 80 ? line.Substring(0, 80) + "…" : line;
                if (txtCheckStatus.Text != s) txtCheckStatus.Text = s;
            };

            txtCheckStatus.Text = title + "…";
            if (busyButton != null) busyButton.Content = Loc.T("op.cancel");
            if (disableButton != null) disableButton.IsEnabled = false;
            prgOp.Visibility = Visibility.Visible;
            TraceLog("════ " + title + " ════");
            tabMain.SelectedIndex = logTab; // 日志面板透传进度

            bool succeeded = false;
            try
            {
                await op(progressSink, ct);
                succeeded = true;
                TraceLog(Loc.T("op.done", title));
                txtCheckStatus.Text = Loc.T("op.doneShort", title);
                if (tabMain.SelectedIndex == logTab) tabMain.SelectedIndex = returnTab; // 用户未手动切走则回目标页
            }
            catch (OperationCanceledException)
            {
                TraceLog(Loc.T("op.cancelled", title));
                txtCheckStatus.Text = Loc.T("op.cancelled", title);
            }
            catch (Exception ex)
            {
                TraceLog(Loc.T("op.failed", title, ex.Message));
                txtCheckStatus.Text = Loc.T("op.failedShort", title);
            }
            finally
            {
                _opActive = false;
                _opCts = null;
                if (busyButton != null) busyButton.Content = origContent;
                if (disableButton != null) disableButton.IsEnabled = true;
                prgOp.Visibility = Visibility.Collapsed;
            }
            if (succeeded && after != null) await after();
        }

        async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            await RunEnvCheckAsync();
        }

        // ── 服务页 ──

        async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            await StartCoreAsync(true);
        }

        async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            await App.Services.Dsh.StopAsync();
        }

        async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            await App.Services.Dsh.StopAsync();
            await StartCoreAsync(true);
        }

        async void BtnOpenUi_Click(object sender, RoutedEventArgs e)
        {
            OpenUiFromTray();
        }

        /// <summary>启动核心流程：校验 → 前置检查（对话框）→ StartAsync。供按钮与托盘共用。</summary>
        async Task<bool> StartCoreAsync(bool withPreflight)
        {
            try
            {
                var s = App.Services.Settings;

                // 窗口不可见（托盘启动）时直接用持久化设置，避免控件未初始化导致的误报弹窗；
                // 窗口可见时以输入框为准并即时持久化，托盘后续启动保持一致。
                int port = s.Port;
                bool random = s.RandomPort;
                if (IsLoaded)
                {
                    random = chkRandomPort.IsChecked == true;
                    if (!random)
                    {
                        if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
                        {
                            Ask(Loc.T("svc.portInvalid"), MessageBoxButton.OK, MessageBoxImage.Warning);
                            return false;
                        }
                    }
                    s.Port = port;
                    s.RandomPort = random;
                    SettingsService.Save(s);
                }
                if (port < 1 || port > 65535)
                {
                    Ask(Loc.T("svc.portInvalidSettings"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var node = await NodeService.DetectAsync(s.NodePath);
                if (node.NodeExe == null)
                {
                    Ask(Loc.T("svc.noNode"), MessageBoxButton.OK, MessageBoxImage.Information);
                    tabMain.SelectedIndex = 0;
                    return false;
                }
                var binJs = await App.Services.DshBinJsAsync();
                if (binJs == null)
                {
                    Ask(Loc.T("svc.noDsh"), MessageBoxButton.OK, MessageBoxImage.Information);
                    tabMain.SelectedIndex = 0;
                    return false;
                }

                if (withPreflight)
                {
                    var pre = await App.Services.Dsh.PreflightAsync(port, random);
                    if (pre.Kind == PreflightKind.PortBusy)
                    {
                        var r = Ask(Loc.T("svc.portBusyChoices", pre.Message),
                            MessageBoxButton.YesNo, MessageBoxImage.Question, Loc.T("svc.portBusyTitle"));
                        if (r == MessageBoxResult.Yes) App.Services.OpenUrl("http://127.0.0.1:" + port);
                        return false;
                    }
                    if (pre.Kind == PreflightKind.ExternalInstances)
                    {
                        var detail = "";
                        foreach (var inst in pre.Instances) detail += "  PID " + inst.Pid + "\n";
                        var r = Ask(
                            Loc.T("svc.externalChoices", pre.Message, detail),
                            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, Loc.T("svc.externalTitle"));
                        if (r == MessageBoxResult.No)
                        {
                            App.Services.OpenUrl("http://127.0.0.1:" + (random ? 3080 : port));
                            return false;
                        }
                        if (r == MessageBoxResult.Cancel) return false;
                        foreach (var inst in pre.Instances)
                        {
                            await KillExternalAsync(inst.Pid);
                        }
                    }
                }

                bool ok = await App.Services.Dsh.StartAsync(port, random, s.TrustedHosts, node.NodeExe, binJs, App.Services.EnvPath, s.NotifySubagents, s.EnableNotifications);
                return ok;
            }
            catch (Exception ex)
            {
                // 启动链路上的失败（典型：PATH 脏段让 Path.Combine 抛"路径中具有非法字符。"）
                // 发生在 DshProcessManager 写第一条日志之前；不在这里落盘的话日志里只剩启动横幅，
                // 用户拿不到任何线索。这里把完整异常（含堆栈）写进日志与界面日志面板。
                TraceLog(Loc.T("svc.startFailed", ex.ToString()));
                Ask(Loc.T("svc.startFailed", ex.Message), MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>
        /// 弹窗助手：窗口尚未显示（托盘启动）时先显示并激活主窗口作为 owner，
        /// 避免无主弹窗在 Windows 11 上一闪而过或跑到其他窗口后面。
        /// </summary>
        MessageBoxResult Ask(string text, MessageBoxButton buttons, MessageBoxImage icon, string title = null)
        {
            if (title == null) title = Loc.T("app.name");
            if (!IsLoaded) ShowOrActivate();
            return MessageBox.Show(this, text, title, buttons, icon);
        }

        static async Task KillExternalAsync(int pid)
        {
            try
            {
                await ProcessRunner.RunAsync(new ProcessSpec
                {
                    FileName = "taskkill.exe",
                    Arguments = "/PID " + pid + " /T /F",
                    TimeoutMs = 15000
                }, CancellationToken.None, null);
            }
            catch { }
        }

        /// <summary>服务状态刷新（任意线程可调）。</summary>
        public void UpdateServiceState(DshState state)
        {
            if (!Dispatcher.CheckAccess())
            {
                try { Dispatcher.BeginInvoke(new Action(() => UpdateServiceState(state))); } catch { return; }
                return;
            }
            try
            {
                bool running = state == DshState.Running;
                bool busy = state == DshState.Starting || state == DshState.Stopping;
                btnStart.IsEnabled = !running && !busy;
                btnStop.IsEnabled = running || state == DshState.Starting;
                btnRestart.IsEnabled = running;
                btnOpenUi.IsEnabled = running || App.Services.Dsh.Url != null;

                switch (state)
                {
                    case DshState.Running:
                        txtServiceStatus.Text = Loc.T("svc.running", App.Services.Dsh.Url);
                        break;
                    case DshState.Starting:
                        txtServiceStatus.Text = Loc.T("svc.starting");
                        break;
                    case DshState.Stopping:
                        txtServiceStatus.Text = Loc.T("svc.stopping");
                        break;
                    case DshState.Error:
                        txtServiceStatus.Text = Loc.T("svc.error");
                        App.Services.Tray.ShowBalloon(Loc.T("svc.startFailBalloonTitle"), Loc.T("svc.startFailBalloonText"));
                        break;
                    default:
                        txtServiceStatus.Text = Loc.T("svc.idle");
                        break;
                }
            }
            catch { }
        }

        // ── 日志 ──

        volatile bool _logQueued;

        /// <summary>
        /// dsh 自身的输出：已由 DshProcessManager 落盘，这里只上界面，避免重复写日志。
        /// </summary>
        public void TraceDshLog(string line) { TraceLogCore(line, false); }

        /// <summary>
        /// 日志追加（任意线程可调）。托盘自身的操作输出（npm / 插件 / 体检 / 清理）同时落盘；
        /// 界面部分突发输出合并去重：跨线程时若已有待处理的追加则丢弃本次，
        /// 防止安装/下载输出洪峰把 Dispatcher 队列塞爆导致界面卡死。
        /// </summary>
        public void TraceLog(string line) { TraceLogCore(line, true); }

        void TraceLogCore(string line, bool logToFile)
        {
            // 落盘不受"窗口是否已加载""界面是否正在合并去重"影响，保证文件里一条不丢
            if (logToFile) LogFile.Write(line);
            if (!Dispatcher.CheckAccess())
            {
                if (_logQueued) return;
                _logQueued = true;
                try { Dispatcher.BeginInvoke(new Action(() => { _logQueued = false; TraceLogCore(line, false); })); }
                catch { _logQueued = false; }
                return;
            }
            if (!IsLoaded) return;
            try
            {
                txtLog.AppendText(line + Environment.NewLine);
                TrimLog();
                txtLog.ScrollToEnd();
            }
            catch { }
        }

        void FillLogFromSnapshot()
        {
            var snap = App.Services.Dsh.SnapshotLog();
            if (snap.Length == 0) return;
            txtLog.AppendText(string.Join(Environment.NewLine, snap) + Environment.NewLine);
            TrimLog();
            txtLog.ScrollToEnd();
        }

        void TrimLog()
        {
            var t = txtLog.Text;
            int count = 0, cut = -1;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == '\n')
                {
                    count++;
                    if (count > MaxLogLines) { cut = i + 1; }
                }
            }
            if (cut > 0) txtLog.Text = t.Substring(cut);
        }

        // ── 托盘入口 ──

        public void StartFromTray() { _ = StartCoreAsync(true); }
        public void StopFromTray() { _ = App.Services.Dsh.StopAsync(); }
        public void RestartFromTray() { RestartFromTrayAsync(); }
        async void RestartFromTrayAsync()
        {
            await App.Services.Dsh.StopAsync();
            await StartCoreAsync(true);
        }

        public void OpenUiFromTray()
        {
            // 带令牌的入口优先：裸 URL 在没有会话 cookie 时会被 dsh 回 401
            var url = App.Services.Dsh.BrowserUrl;
            if (string.IsNullOrEmpty(url))
            {
                var s = App.Services.Settings;
                if (!s.RandomPort && s.Port > 0 && s.Port <= 65535) url = "http://127.0.0.1:" + s.Port;
            }
            if (string.IsNullOrEmpty(url))
                App.Services.Tray.ShowBalloon(Loc.T("svc.notRunning"), Loc.T("svc.startFirst"));
            else
                App.Services.OpenUrl(url);
        }

        public void CopyUrlFromTray()
        {
            var url = App.Services.Dsh.BrowserUrl;
            if (string.IsNullOrEmpty(url))
            {
                var s = App.Services.Settings;
                if (!s.RandomPort && s.Port > 0 && s.Port <= 65535) url = "http://127.0.0.1:" + s.Port;
            }
            if (string.IsNullOrEmpty(url))
                App.Services.Tray.ShowBalloon(Loc.T("svc.notRunning"), Loc.T("svc.startFirst"));
            else
                App.Services.CopyUrl(url);
        }

        // ── 设置页 ──

        void LoadSettingsIntoUi()
        {
            _loadingUi = true;
            try
            {
                var s = App.Services.Settings;
                txtPort.Text = s.Port.ToString();
                txtPort.IsEnabled = !s.RandomPort;
                chkRandomPort.IsChecked = s.RandomPort;
                chkAutoOpen.IsChecked = s.AutoOpenBrowser;
                chkAutoStart.IsChecked = s.AutoStartOnLogin;
                chkShowMain.IsChecked = s.ShowMainWindowOnStartup;
                chkAutoStartDsh.IsChecked = s.AutoStartDshOnLaunch;
                txtTrustedHosts.Text = s.TrustedHosts;
                txtNodePath.Text = s.NodePath;
                cmbLang.SelectedIndex = LangIndexFromSetting(s.Language);
                cmbTrayDoubleClick.SelectedIndex = s.TrayDoubleClickAction == "web" ? 1 : 0;
                if (s.MirrorUrl == "https://registry.npmmirror.com") cmbMirror.SelectedIndex = 1;
                else if (s.MirrorUrl.Length > 0) { cmbMirror.SelectedIndex = 2; txtCustomMirror.Text = s.MirrorUrl; }
                else cmbMirror.SelectedIndex = 0;
                chkNotifyEnable.IsChecked = s.EnableNotifications;
                chkNotifySubagents.IsChecked = s.NotifySubagents;
                chkNotifyTray.IsChecked = s.EnableTrayNotification;
                chkNotifyExternal.IsChecked = s.EnableExternalHook;
                txtHookCommand.Text = s.ExternalHookCommand;
                txtHookArgs.Text = s.ExternalHookArguments;
            }
            finally
            {
                _loadingUi = false;
            }
        }

        static int LangIndexFromSetting(string v)
        {
            if (string.Equals(v, "zh", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(v, "en", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }

        static string LangSettingFromIndex(int i)
        {
            if (i == 1) return "zh";
            if (i == 2) return "en";
            return "auto";
        }

        /// <summary>界面语言切换：立即生效 + 持久化。</summary>
        void CmbLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingUi) return;
            var v = LangSettingFromIndex(cmbLang.SelectedIndex);
            if (v == null) return;
            App.Services.Settings.Language = v;
            SettingsService.Save(App.Services.Settings);
            Loc.Apply(v);
        }

        /// <summary>双击托盘图标行为切换：立即生效 + 持久化。</summary>
        void CmbTrayDoubleClick_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingUi) return;
            var s = App.Services.Settings;
            s.TrayDoubleClickAction = cmbTrayDoubleClick.SelectedIndex == 1 ? "web" : "main";
            SettingsService.Save(s);
        }

        void ChkRandomPort_Changed(object sender, RoutedEventArgs e)
        {
            txtPort.IsEnabled = chkRandomPort.IsChecked != true;
        }

        async void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            var s = App.Services.Settings;
            int port;
            if (!int.TryParse(txtPort.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(Loc.T("svc.portInvalid"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            s.Port = port;
            s.RandomPort = chkRandomPort.IsChecked == true;
            s.AutoOpenBrowser = chkAutoOpen.IsChecked == true;
            s.ShowMainWindowOnStartup = chkShowMain.IsChecked == true;
            s.AutoStartDshOnLaunch = chkAutoStartDsh.IsChecked == true;
            s.TrustedHosts = (txtTrustedHosts.Text ?? "").Trim();
            s.NodePath = (txtNodePath.Text ?? "").Trim();
            s.Language = LangSettingFromIndex(cmbLang.SelectedIndex);
            s.TrayDoubleClickAction = cmbTrayDoubleClick.SelectedIndex == 1 ? "web" : "main";
            bool autoStartChanged = s.AutoStartOnLogin != (chkAutoStart.IsChecked == true);
            s.AutoStartOnLogin = chkAutoStart.IsChecked == true;
            if (autoStartChanged) App.Services.ToggleAutoStart(s.AutoStartOnLogin);
            SettingsService.Save(s);
            txtCheckStatus.Text = Loc.T("set.saved");
            MessageBox.Show(Loc.T("set.saved"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ── 通知增强页 ──

        void BtnSaveNotify_Click(object sender, RoutedEventArgs e)
        {
            var s = App.Services.Settings;
            s.EnableNotifications = chkNotifyEnable.IsChecked == true;
            s.NotifySubagents = chkNotifySubagents.IsChecked == true;
            s.EnableTrayNotification = chkNotifyTray.IsChecked == true;
            s.EnableExternalHook = chkNotifyExternal.IsChecked == true;
            s.ExternalHookCommand = (txtHookCommand.Text ?? "").Trim();
            s.ExternalHookArguments = (txtHookArgs.Text ?? "").Trim();
            SettingsService.Save(s);
            MessageBox.Show(Loc.T("set.saved"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void BtnTestNotify_Click(object sender, RoutedEventArgs e)
        {
            App.Services.Tray.ShowBalloon(Loc.T("notify.testTitle"), Loc.T("notify.testText"));
        }

        async void BtnInstallNotifyPlugin_Click(object sender, RoutedEventArgs e)
        {
            var pluginDir = FindNotifyPluginDir();
            if (pluginDir == null)
            {
                MessageBox.Show(Loc.T("notify.pluginNotFound"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 新版 dsh 的请求扩展提供方会读取所有启用插件 package.json 的 name/version，
            // 任一为空即对每个模型请求硬抛 REQUEST_EXTENSION。必须先补齐并校验，再交给 dsh。
            try
            {
                EnsurePluginManifestVersion(pluginDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("notify.manifestFixFail", ex.Message), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            bool stoppedDsh = false;
            try
            {
                var s = App.Services.Settings;
                var node = await NodeService.DetectAsync(s.NodePath);
                if (node.NodeExe == null)
                {
                    MessageBox.Show(Loc.T("svc.noNode"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var binJs = await App.Services.DshBinJsAsync();
                if (binJs == null)
                {
                    MessageBox.Show(Loc.T("svc.noDsh"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 绝不能让 pnpm 在 dsh 运行期间改写 profile 的 node_modules：
                // Windows 上会因文件占用失败，或留下半写/悬空的链接而损坏 profile。
                bool wasRunning = App.Services.Dsh.State != DshState.Idle;
                if (wasRunning)
                {
                    TraceLog(Loc.T("notify.stopForPlugin"));
                    await App.Services.Dsh.StopAsync();
                    stoppedDsh = true;
                }

                // dsh 的 plugin add 内部会 spawnSync("pnpm")，缺失时先自动安装 pnpm
                await NpmService.EnsurePnpmAsync(App.Services.Settings.MirrorUrl, App.Services.EnvPath, line => TraceLog(line), CancellationToken.None);
                App.Services.RefreshEnvPath();

                var spec = "link:" + pluginDir.Replace('\\', '/');
                var args = ProcessRunner.Quote(binJs) + " plugin --profile web add " + ProcessRunner.Quote(spec);
                TraceLog(Loc.T("notify.installing", pluginDir));
                var r = await ProcessRunner.RunAsync(new ProcessSpec
                {
                    FileName = node.NodeExe,
                    Arguments = args,
                    Environment = new Dictionary<string, string> { { "Path", App.Services.EnvPath } },
                    TimeoutMs = 120000
                }, CancellationToken.None, line => TraceLog(line));

                if (r.TimedOut || r.Cancelled || r.ExitCode != 0)
                {
                    // 失败也必须把之前停掉的 dsh 拉回来，绝不把用户的 dsh 留在停止状态
                    if (stoppedDsh) await RestartAfterPluginOpAsync();
                    MessageBox.Show(Loc.T("notify.installFail", r.ExitCode, (r.Error ?? "").Trim()),
                        Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                EnsureNotifyPatch();
                if (stoppedDsh)
                {
                    await RestartAfterPluginOpAsync();
                    MessageBox.Show(Loc.T("notify.installDoneRestarted"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(Loc.T("notify.installDone"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                if (stoppedDsh) await RestartAfterPluginOpAsync();
                MessageBox.Show(Loc.T("notify.installFail", "?", ex.Message), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        static string FindNotifyPluginDir()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "tools", "dsh-notify-hook"),
                Path.Combine(Directory.GetCurrentDirectory(), "tools", "dsh-notify-hook")
            };
            foreach (var c in candidates)
                if (Directory.Exists(c)) return c;

            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                var p = Path.Combine(dir.FullName, "tools", "dsh-notify-hook");
                if (Directory.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>当前程序集版本（3 段，如 1.2.4），作为插件清单 version 的唯一来源。</summary>
        static string AppVersionString()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : v.ToString(3);
        }

        /// <summary>
        /// 校验并写入 <paramref name="pluginDir"/> 下 package.json 的 version 字段，返回写入的版本号。
        /// 新版 dsh 的请求扩展提供方会读取每个启用插件的清单，要求 name 与 version 均非空，
        /// 否则对每个模型请求硬抛 REQUEST_EXTENSION；因此这里先校验 name，再按程序版本补齐 version
        /// （保留其它属性）并原子写回，使安装器不可能产出版本缺失的清单。
        /// </summary>
        static string EnsurePluginManifestVersion(string pluginDir)
        {
            var file = Path.Combine(pluginDir, "package.json");
            if (!File.Exists(file))
                throw new InvalidOperationException(Loc.T("notify.manifestMissing", file));

            var root = JObject.Parse(File.ReadAllText(file));
            var nameToken = root["name"];
            if (nameToken == null || nameToken.Type != JTokenType.String || string.IsNullOrWhiteSpace(nameToken.Value<string>()))
                throw new InvalidOperationException(Loc.T("notify.manifestNoName", file));

            var version = AppVersionString();
            var versionToken = root["version"];
            var current = versionToken == null || versionToken.Type != JTokenType.String ? null : versionToken.Value<string>();
            if (string.Equals(current, version, StringComparison.Ordinal)) return version;

            root["version"] = version;
            var json = root.ToString(Newtonsoft.Json.Formatting.Indented);
            if (!json.EndsWith("\n", StringComparison.Ordinal)) json += Environment.NewLine;
            WriteFileAtomic(file, json);
            return version;
        }

        /// <summary>原子写入文本文件：同目录写 .tmp，再 File.Replace（目标已存在）或 File.Move（目标不存在）。</summary>
        static void WriteFileAtomic(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (!File.Exists(path))
            {
                File.Move(tmp, path);
                return;
            }
            try
            {
                File.Replace(tmp, path, null);
            }
            catch (Exception)
            {
                // 个别文件系统（如部分网络盘）不支持 Replace，退化为覆盖写并清理临时文件
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
        }

        // cordis.patch.yml 的条目识别：正则作用于整行（允许行首/行尾空白），注释行一律不参与判定；
        // 完全不使用子串匹配，避免把注释或无关条目误判为插件条目。
        static readonly Regex PatchIdLineRx = new Regex(@"^\s*-\s*id\s*:\s*dsh-notify-hook\s*$", RegexOptions.Compiled);
        static readonly Regex PatchNameLineRx = new Regex(@"^\s*name\s*:\s*dsh-notify-hook\s*$", RegexOptions.Compiled);
        static readonly Regex PatchInsertHeaderRx = new Regex(@"^\s*-\s*insert\s*:\s*$", RegexOptions.Compiled);

        /// <summary>cordis.patch.yml 路径（DSH_HOME 未设置时回落 %USERPROFILE%\.dsh）；createDir 为 true 时确保 profile 目录存在。</summary>
        static string NotifyPatchPath(bool createDir)
        {
            var profileDir = Path.Combine(ResolveDshHome(), "profiles", "web");
            if (createDir && !Directory.Exists(profileDir)) Directory.CreateDirectory(profileDir);
            return Path.Combine(profileDir, "cordis.patch.yml");
        }

        /// <summary>
        /// dsh 数据目录：优先 DSH_HOME，回落 %USERPROFILE%\.dsh。
        /// DSH_HOME 常被写成带引号的形式（setx DSH_HOME "\"D:\dsh\""），引号会被一起写进值里，
        /// 随后 Path.Combine / Directory.CreateDirectory / Directory.Move 都会抛
        /// "路径中具有非法字符。"——这里先去引号，仍非法就抛出可读的错误（含实际取值与位置）。
        /// </summary>
        static string ResolveDshHome()
        {
            var home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrEmpty(home))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            var cleaned = PathGuard.StripQuotes(home);
            if (!PathGuard.IsSafe(cleaned))
                throw new InvalidOperationException(Loc.T("dsh.homeInvalid", PathGuard.Describe(cleaned)));
            return cleaned;
        }

        /// <summary>读取 patch 文件全部行；raw 为原始文本（文件不存在时为 null），用于判断是否真的需要写回。</summary>
        static List<string> ReadPatchLines(string patchPath, out string raw)
        {
            raw = File.Exists(patchPath) ? File.ReadAllText(patchPath) : null;
            return raw == null ? new List<string>() : new List<string>(File.ReadAllLines(patchPath));
        }

        /// <summary>按行写回（保持原文件换行风格，新文件用平台默认）；内容无变化时不写，确保无关行字节不变。</summary>
        static void WritePatchLines(string patchPath, string raw, List<string> lines)
        {
            var nl = raw == null ? Environment.NewLine : (raw.Contains("\r\n") ? "\r\n" : "\n");
            var content = lines.Count == 0 ? "" : string.Join(nl, lines) + nl;
            if (content == raw) return;
            WriteFileAtomic(patchPath, content);
        }

        /// <summary>注释行（# 开头，忽略缩进）不参与 YAML 结构判定。</summary>
        static bool IsPatchCommentLine(string line)
        {
            return line.TrimStart().StartsWith("#", StringComparison.Ordinal);
        }

        /// <summary>插件 id 行：- id: dsh-notify-hook。</summary>
        static bool IsPatchPluginIdLine(string line)
        {
            return !IsPatchCommentLine(line) && PatchIdLineRx.IsMatch(line);
        }

        /// <summary>插件 name 行：name: dsh-notify-hook。</summary>
        static bool IsPatchPluginNameLine(string line)
        {
            return !IsPatchCommentLine(line) && PatchNameLineRx.IsMatch(line);
        }

        /// <summary>insert 头行：- insert:。</summary>
        static bool IsPatchInsertHeader(string line)
        {
            return !IsPatchCommentLine(line) && PatchInsertHeaderRx.IsMatch(line);
        }

        /// <summary>行首缩进宽度（空格/制表符计数）。</summary>
        static int PatchIndent(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return i;
        }

        /// <summary>是否为 YAML 列表项起始行（"- xxx" 或 "-"）。</summary>
        static bool IsPatchItemLine(string line)
        {
            var t = line.TrimStart();
            return t == "-" || t.StartsWith("- ", StringComparison.Ordinal);
        }

        /// <summary>
        /// 确保 cordis.patch.yml 含有 dsh-notify-hook 的 insert 条目：按 id 行或 name 行精确判定是否已存在
        /// （绝不按子串匹配），仅在 [] 是文件里唯一真实内容时才移除该占位符，最后原子写回。
        /// </summary>
        static void EnsureNotifyPatch()
        {
            var patchPath = NotifyPatchPath(true);
            string raw;
            var lines = ReadPatchLines(patchPath, out raw);

            bool present = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (IsPatchPluginIdLine(lines[i]) || IsPatchPluginNameLine(lines[i])) { present = true; break; }
            }

            // Cordis patch 模板里的空列表占位符 [] 必须移除，否则与 insert 块并存会让 YAML 变成非法列表；
            // 但只有它确实是唯一真实内容（其余仅空行/注释）时才动它，避免误删用户数据。
            bool placeholderOnly = true;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length == 0 || t == "[]" || IsPatchCommentLine(line)) continue;
                placeholderOnly = false;
                break;
            }
            if (placeholderOnly) lines.RemoveAll(line => line.Trim() == "[]");

            if (!present)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length != 0)
                    lines.Add("");
                lines.Add("- insert:");
                lines.Add("    - id: dsh-notify-hook");
                lines.Add("      name: dsh-notify-hook");
            }

            WritePatchLines(patchPath, raw, lines);
        }

        /// <summary>
        /// 从 cordis.patch.yml 移除 dsh-notify-hook 条目：按 id 行（无则 name 行）定位，删除该项及其同级续行
        /// （缩进更深且不是新列表项的行，如 name 行）；仅当所在 - insert: 块已无任何剩余内容时才删除该头，
        /// 从而不会孤立/破坏含其它条目的块。
        /// </summary>
        static void RemoveNotifyPatch()
        {
            var patchPath = NotifyPatchPath(false);
            string raw;
            var lines = ReadPatchLines(patchPath, out raw);
            if (raw == null) return; // 文件不存在：不动

            int item = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (IsPatchPluginIdLine(lines[i])) { item = i; break; }
            }
            if (item < 0)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (IsPatchPluginNameLine(lines[i])) { item = i; break; }
                }
            }
            if (item < 0) return; // 没有插件条目：保持文件字节不变

            // 1) 删除条目行及其同级续行，遇到缩进不更深或新列表项即停
            int itemIndent = PatchIndent(lines[item]);
            int end = item + 1;
            while (end < lines.Count && PatchIndent(lines[end]) > itemIndent && !IsPatchItemLine(lines[end])) end++;
            lines.RemoveRange(item, end - item);

            // 2) 找最近的前置 - insert: 头（缩进严格更小）
            int header = -1;
            for (int i = item - 1; i >= 0; i--)
            {
                if (IsPatchInsertHeader(lines[i]) && PatchIndent(lines[i]) < itemIndent) { header = i; break; }
            }
            if (header >= 0)
            {
                // 该 insert 块内只要还剩下任何内容（其它条目、非条目子节点），就保留 - insert: 头
                int headerIndent = PatchIndent(lines[header]);
                bool blockHasContent = false;
                for (int i = header + 1; i < lines.Count; i++)
                {
                    if (PatchIndent(lines[i]) <= headerIndent) break; // 已离开该块
                    var t = lines[i].Trim();
                    if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                    blockHasContent = true;
                    break;
                }
                if (!blockHasContent)
                {
                    lines.RemoveAt(header);
                    // 同时吃掉该头之前的那行空行（它正是 EnsureNotifyPatch 添加的分隔空行），
                    // 使卸载后的文件与安装前逐字节一致；空行只是排版，删除不影响 YAML 语义。
                    if (header > 0 && lines[header - 1].Trim().Length == 0) lines.RemoveAt(header - 1);
                }
            }

            // 3) 已无任何真实内容时补回空列表占位符 []
            //    只补占位符，绝不丢弃用户已有的注释与排版：注释是 profile 模板的说明文字，
            //    早期实现用 lines.Clear() 会连注释一起清掉，把模板头部说明永久丢失。
            bool hasContent = false;
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (t.Length > 0 && !IsPatchCommentLine(line)) { hasContent = true; break; }
            }
            if (!hasContent)
            {
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) lines.Add("");
                lines.Add("[]");
            }

            WritePatchLines(patchPath, raw, lines);
        }

        async void BtnUninstallNotifyPlugin_Click(object sender, RoutedEventArgs e)
        {
            var r = Ask(
                Loc.T("notify.uninstallConfirm"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, Loc.T("notify.uninstallConfirmTitle"));
            if (r != MessageBoxResult.Yes) return;

            bool stoppedDsh = false;
            try
            {
                var s = App.Services.Settings;
                var node = await NodeService.DetectAsync(s.NodePath);
                if (node.NodeExe == null)
                {
                    MessageBox.Show(Loc.T("svc.noNode"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var binJs = await App.Services.DshBinJsAsync();
                if (binJs == null)
                {
                    MessageBox.Show(Loc.T("svc.noDsh"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // 与安装同理：先停 dsh，绝不运行中让 pnpm 改写 profile 的 node_modules
                bool wasRunning = App.Services.Dsh.State != DshState.Idle;
                if (wasRunning)
                {
                    TraceLog(Loc.T("notify.stopForPlugin"));
                    await App.Services.Dsh.StopAsync();
                    stoppedDsh = true;
                }

                // 卸载同样走 dsh plugin → pnpm，先确保 pnpm 可用
                await NpmService.EnsurePnpmAsync(App.Services.Settings.MirrorUrl, App.Services.EnvPath, line => TraceLog(line), CancellationToken.None);
                App.Services.RefreshEnvPath();

                var args = ProcessRunner.Quote(binJs) + " plugin --profile web remove dsh-notify-hook";
                TraceLog(Loc.T("notify.uninstalling"));
                var rr = await ProcessRunner.RunAsync(new ProcessSpec
                {
                    FileName = node.NodeExe,
                    Arguments = args,
                    Environment = new Dictionary<string, string> { { "Path", App.Services.EnvPath } },
                    TimeoutMs = 120000
                }, CancellationToken.None, line => TraceLog(line));

                if (rr.TimedOut || rr.Cancelled || rr.ExitCode != 0)
                {
                    if (stoppedDsh) await RestartAfterPluginOpAsync();
                    MessageBox.Show(Loc.T("notify.uninstallFail", rr.ExitCode, (rr.Error ?? "").Trim()),
                        Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                RemoveNotifyPatch();
                if (stoppedDsh)
                {
                    await RestartAfterPluginOpAsync();
                    MessageBox.Show(Loc.T("notify.uninstallDoneRestarted"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show(Loc.T("notify.uninstallDone"), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                if (stoppedDsh) await RestartAfterPluginOpAsync();
                MessageBox.Show(Loc.T("notify.uninstallFail", "?", ex.Message), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 插件安装/卸载中途失败（或异常）后把 dsh 重新拉起，恢复用户原有的运行状态。
        /// 重启失败由 StartCoreAsync 自行提示，不影响调用方紧接着展示的原始错误。
        /// </summary>
        async Task RestartAfterPluginOpAsync()
        {
            TraceLog(Loc.T("notify.restarting"));
            await StartCoreAsync(true);
        }

        void BtnOpenSettingsDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(SettingsService.SettingsDir);
                Process.Start("explorer.exe", SettingsService.SettingsDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("set.openDirFail", ex.Message), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 清理 dsh 环境：停止 dsh → npm 卸载全局包 → 数据目录改名备份（含凭据）→ 移除开机自启。
        /// 全程确认 + 日志透传；不卸载 Node.js。
        /// </summary>
        async void BtnCleanup_Click(object sender, RoutedEventArgs e)
        {
            if (_opActive) { CancelOp(); return; }
            var r = Ask(
                Loc.T("cleanup.confirm"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, Loc.T("cleanup.confirmTitle"));
            if (r != MessageBoxResult.Yes) return;

            await RunLongOpAsync(Loc.T("cleanup.title"), btnCleanup, null, async (log, ct) =>
            {
                // 1. 停止 dsh
                if (App.Services.Dsh.State != DshState.Idle)
                {
                    log(Loc.T("cleanup.stopLog"));
                    await App.Services.Dsh.StopAsync();
                }
                else
                {
                    log(Loc.T("cleanup.skipStop"));
                }

                // 2. 卸载全局 npm 包
                log(Loc.T("cleanup.uninstallLog"));
                await NpmService.UninstallDshAsync(App.Services.EnvPath, log, ct);
                log(Loc.T("cleanup.uninstallDone"));

                // 3. 数据目录改名备份（尊重 DSH_HOME 覆盖；非法取值由 ResolveDshHome 给出可读错误）
                var home = ResolveDshHome();
                if (System.IO.Directory.Exists(home))
                {
                    var bak = home + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    log(Loc.T("cleanup.backupLog", bak));
                    System.IO.Directory.Move(home, bak);
                }
                else
                {
                    log(Loc.T("cleanup.noHome", home));
                }

                // 4. 移除开机自启
                if (App.Services.Settings.AutoStartOnLogin) App.Services.ToggleAutoStart(false);
                log(Loc.T("cleanup.done", SettingsService.SettingsDir));
            }, RunEnvCheckAsync, logTab: 1, returnTab: 2);
        }
    }
}
