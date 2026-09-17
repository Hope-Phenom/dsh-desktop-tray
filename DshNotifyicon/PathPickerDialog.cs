using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DshNotifyicon.Services;
using Microsoft.Win32;

namespace DshNotifyicon
{
    /// <summary>
    /// 手动指定工具位置的输入窗（代码构建，Node / pnpm 复用）。
    /// 输入既可以是可执行文件，也可以是它所在的目录——用户手头往往是安装目录。
    /// 只负责取值：保存设置、刷新 PATH、重跑体检都由调用方（MainWindow）负责。
    /// </summary>
    public class PathPickerDialog : Window
    {
        readonly TextBox _box = new TextBox();
        readonly string _fileNames;

        /// <param name="fileNames">
        /// 候选可执行文件名（多个用 ";" 分隔，如 "pnpm.exe;pnpm.cmd"），用于浏览对话框的过滤器；
        /// 校验用的文件名列表由调用方按同一份字符串拆分，保持一致。
        /// </param>
        public PathPickerDialog(string title, string hint, string current, string fileNames)
        {
            _fileNames = fileNames;

            Title = title;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray
            });

            _box.Text = current ?? "";
            _box.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(_box);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            buttons.Children.Add(MakeButton(Loc.T("env.pickBrowse"), 90, (s, e) => Browse()));
            buttons.Children.Add(MakeButton(Loc.T("env.pickClear"), 100, (s, e) => { Value = ""; DialogResult = true; }));
            var ok = MakeButton(Loc.T("env.pickOk"), 80, (s, e) => { Value = (_box.Text ?? "").Trim(); DialogResult = true; });
            ok.IsDefault = true;
            buttons.Children.Add(ok);
            var cancel = MakeButton(Loc.T("env.pickCancel"), 80, null);
            cancel.IsCancel = true;
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            Content = panel;
            Loaded += (s, e) => { _box.Focus(); _box.SelectAll(); };
        }

        /// <summary>用户确认后的值（已 Trim）；空串 = 恢复自动检测。取消时保持 null。</summary>
        public string Value { get; private set; }

        Button MakeButton(string text, double width, RoutedEventHandler onClick)
        {
            var b = new Button { Content = text, Width = width, Height = 28, Margin = new Thickness(8, 0, 0, 0) };
            if (onClick != null) b.Click += onClick;
            return b;
        }

        void Browse()
        {
            var dlg = new OpenFileDialog
            {
                Filter = _fileNames + "|" + _fileNames + "|" + Loc.T("env.pickAllFiles"),
                CheckFileExists = true
            };
            // 已经填了路径就让对话框直接落到那一处，省得用户重新翻目录
            var cur = PathGuard.StripQuotes(_box.Text);
            try
            {
                if (File.Exists(cur)) dlg.FileName = cur;
                else if (Directory.Exists(cur)) dlg.InitialDirectory = cur;
            }
            catch { } // 非法字符：忽略定位，仍可正常浏览
            if (dlg.ShowDialog(this) == true) _box.Text = dlg.FileName;
        }
    }
}
