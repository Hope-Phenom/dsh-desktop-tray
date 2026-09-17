using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DshNotifyicon.Services;
using Microsoft.Win32;

namespace DshNotifyicon
{
    /// <summary>
    /// 应用级服务容器：设置、DSH 进程管理、主窗口、托盘，以及跨层辅助方法。
    /// </summary>
    public class AppServices
    {
        public readonly Settings Settings;
        public readonly DshProcessManager Dsh = new DshProcessManager();
        public MainWindow Main;
        public TrayIcon Tray;

        string _envPath;
        string _binJs;

        public AppServices(Settings settings)
        {
            Settings = settings;
            // NpmService 等拿不到设置对象的地方，靠这个引用取"手动指定路径"。
            // 持有活对象而非副本：用户在环境页改完立刻生效，不会留下过期值。
            ToolPathOverrides.Current = settings;
            Main = new MainWindow();
        }

        /// <summary>
        /// 刷新后的 PATH。注意这里**不**夹带手动指定的目录：手动指定的工具可能已被卸载/损坏，
        /// 一旦把它的目录前置，本工具自己的"回退自动检测"就会沿着同一条 PATH 把被拒的文件捡回来
        /// （体检显示 ✓、dsh 拿着坏 node 启动失败）。要用手动指定的 pnpm 时，
        /// 由 NpmService 校验通过后只注入到那一个子进程上（见 ChildPathAsync）。
        /// </summary>
        public string EnvPath
        {
            get { return _envPath ?? (_envPath = NodeService.RefreshPath()); }
        }

        /// <summary>刷新 PATH 缓存；同时丢弃 bin.js 缓存（换了 node 之后全局前缀可能完全不同）。</summary>
        public void RefreshEnvPath()
        {
            _envPath = NodeService.RefreshPath();
            _binJs = null;
        }

        /// <summary>解析 dsh bin.js 路径（缓存）。未安装返回 null。</summary>
        public async Task<string> DshBinJsAsync()
        {
            if (!string.IsNullOrEmpty(_binJs) && File.Exists(_binJs)) return _binJs;
            _binJs = await NpmService.ResolveDshBinJsAsync(EnvPath);
            return _binJs;
        }

        public void ToggleAutoStart(bool enable)
        {
            Settings.AutoStartOnLogin = enable;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (enable)
                        key.SetValue("DshNotifyicon", "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else
                        key.DeleteValue("DshNotifyicon", false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("autostart.fail", ex.Message), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public void OpenUrl(string url)
        {
            try
            {
                var psi = new ProcessStartInfo(url) { UseShellExecute = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("browser.fail", ex.Message), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        public void CopyUrl(string url)
        {
            try
            {
                Clipboard.SetText(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Loc.T("copy.fail", ex.Message), Loc.T("app.name"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
