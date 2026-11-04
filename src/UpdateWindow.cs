using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenLine
{
    internal sealed class UpdateWindow : Window
    {
        readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        readonly TextBlock status = new TextBlock { Foreground = LightTheme.Brush("#65717F"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 8) };
        readonly ProgressBar progress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 16) };
        readonly Button install;
        string downloaded;
        bool busy, handingOff, customConfirmed;

        public UpdateWindow(UpdateRelease release, AppUpdates updates, Func<string, Task<bool>> apply)
        {
            Title = "更新图绳"; Width = 520; SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI"); UseLayoutRounding = true;
            AutomationProperties.SetAutomationId(this, "AppUpdate");
            var body = new StackPanel();
            var heading = new Grid();
            heading.Children.Add(new TextBlock { Text = "发现新版本", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = LightTheme.Brush("#283343") });
            var close = LightTheme.Button("", Close, "quiet"); close.Content = LightTheme.CloseIcon();
            close.MinWidth = 0; close.Width = close.Height = 28; close.Padding = new Thickness(0); close.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetName(close, "关闭更新窗口"); heading.Children.Add(close); body.Children.Add(heading);
            body.Children.Add(new TextBlock { Text = "图绳 " + AppUpdates.DisplayVersion + " → " + release.Version + "（官方版）", FontSize = 14, Foreground = LightTheme.Brush("#65717F"), Margin = new Thickness(0, 10, 0, 14) });
            // 定制版提醒：安装官方更新会覆盖本机的定制功能，先提醒，再要求二次确认
            var customNotice = new StackPanel();
            customNotice.Children.Add(new TextBlock { Text = "你正在用的是定制版", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = LightTheme.Brush("#7A4B00") });
            customNotice.Children.Add(new TextBlock {
                Text = "安装官方更新会换回官方版，你加的定制功能会消失。\n图片、文字和设置都会保留。\n建议先让 Claude 把定制合并到新版本，再更新。",
                FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Foreground = LightTheme.Brush("#7A4B00"), Margin = new Thickness(0, 4, 0, 0) });
            var customBox = new Border { Child = customNotice, Background = LightTheme.Brush("#FFF6E5"), BorderBrush = LightTheme.Brush("#F0C674"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 16) };
            AutomationProperties.SetAutomationId(customBox, "CustomBuildNotice");
            body.Children.Add(customBox);
            body.Children.Add(new ScrollViewer { MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new TextBlock { Text = String.IsNullOrWhiteSpace(release.Notes) ? "新版本已准备好。" : release.Notes,
                    FontSize = 13, LineHeight = 22, TextWrapping = TextWrapping.Wrap, Foreground = LightTheme.Brush("#343D49") } });
            status.Text = "下载完成后重启更新，保留图片和设置。"; body.Children.Add(status); body.Children.Add(progress);
            var actions = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            var details = LightTheme.Button("查看发布说明", delegate { Process.Start(new ProcessStartInfo(release.Url) { UseShellExecute = true }); }, "quiet");
            details.HorizontalAlignment = HorizontalAlignment.Left; details.Padding = new Thickness(0); actions.Children.Add(details);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var later = LightTheme.Button("稍后", Close, "quiet"); later.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(later);
            install = LightTheme.Button("下载并安装", delegate { }, "primary"); buttons.Children.Add(install);
            AutomationProperties.SetName(install, "下载并安装更新");
            install.Click += async delegate
            {
                if (busy) return;
                if (!customConfirmed)
                {
                    customConfirmed = true;
                    install.Content = "仍然安装官方版"; AutomationProperties.SetName(install, "确认安装官方版");
                    status.Text = "再点一次才会开始下载；不想丢定制功能就点「稍后」。";
                    status.Foreground = LightTheme.Brush("#B25E00");
                    return;
                }
                status.Foreground = LightTheme.Brush("#65717F");
                busy = true; install.IsEnabled = false;
                try
                {
                    if (downloaded == null)
                    {
                        progress.Visibility = Visibility.Visible; status.Text = "正在下载更新…"; later.Content = "取消";
                        downloaded = await updates.DownloadAsync(release, new Progress<int>(delegate(int percent) { progress.Value = percent; status.Text = percent < 100 ? "正在下载更新 " + percent + "%" : "正在校验安装包…"; }), cancellation.Token);
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    handingOff = true; later.IsEnabled = close.IsEnabled = false;
                    status.Text = "正在准备重启…";
                    if (!await apply(downloaded))
                    {
                        handingOff = false; status.Text = "更新包已下载，完成图片编辑后可继续安装。";
                        install.Content = "重启并安装";
                    }
                }
                catch (OperationCanceledException) { if (IsVisible) status.Text = "下载已取消。"; }
                catch (Exception error) { handingOff = false; status.Text = "更新未完成：" + error.Message; install.Content = downloaded == null ? "重试下载" : "重试安装"; }
                finally { busy = false; install.IsEnabled = true; later.IsEnabled = close.IsEnabled = true; later.Content = "稍后"; if (!IsVisible && !handingOff && downloaded != null) AppUpdates.Discard(downloaded); }
            };
            actions.Children.Add(buttons); body.Children.Add(actions);
            var card = LightTheme.Card(body); card.Padding = new Thickness(24); Content = card;
            Closed += delegate { cancellation.Cancel(); if (!handingOff && downloaded != null) AppUpdates.Discard(downloaded); };
        }
    }
}
