using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    internal sealed class PinnedImageWindow : Window
    {
        public readonly CaptureItem Item;
        internal readonly Border Frame = new Border();
        internal readonly Button PinButton = new Button();
        readonly Thumbtack pin = new Thumbtack();
        readonly TranslateTransform pinLift = new TranslateTransform(0, -6);
        readonly TranslateTransform paperLift = new TranslateTransform();
        readonly Image photo = new Image();
        readonly RotateTransform turn = new RotateTransform();
        readonly ScaleTransform zoom = new ScaleTransform();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly double startWidth, startHeight, initialTilt;
        BitmapSource bitmap;
        IntPtr handle;
        readonly Point fallStart, landing;
        readonly Forms.Screen display;
        readonly double displayScale;
        Point anchor, moveStart, anchorStart;
        double pictureScale, dropStarted = -1;
        // 目标逻辑尺寸：先按它一次性改窗口位置和大小，再同步给 WPF 的 Width/Height，避免分三步改导致来回跳
        double logicalWidth, logicalHeight;
        // 滚轮只改目标比例；每帧最多改一次窗口，当前比例平滑追向目标（快速连滚时多个事件并进同一帧）
        double targetScale, lastZoomFrame = -1;
        bool zoomAroundCursor;
        Point zoomCursor;
        const double ZoomTimeConstant = 0.06; // 秒：约 0.18 秒走完 95%，跟手又不突兀
        readonly ToolTip resetHint = new ToolTip { Content = "双击恢复原始大小", Style = LightTheme.HintStyle };
        int placedLeft = int.MinValue, placedTop = int.MinValue, placedWidth, placedHeight;
        bool moving, rendering, returning;
        // 缩放「舞台」：缩放时窗口先一次撑到够装下起点和终点（再留些余量），动画中只挪动、缩放窗口里的内容，
        // 停下来后再把窗口收回刚好的大小。实测每帧改分层窗口尺寸约 28ms（每秒 21~37 帧，偶尔一卡 135ms），
        // 窗口不动只改内容稳定 60 帧；改窗口尺寸的那一下还可能闪一帧旧画面，所以越少越好。
        readonly Grid surface = new Grid();
        readonly CacheMode frameCache;
        bool staged;
        int stageLeft, stageTop, stageWidth, stageHeight;
        readonly DispatcherTimer stageCommit = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        public Rect ReturnBounds { get; private set; }
        public BitmapSource Picture { get { return bitmap; } }
        // 右键菜单：动作和绳子上的卡片走同一套（App.Act），managed＝图片在收集文件夹里（删除＝移到回收站）
        readonly Action<ShotAction> execute;
        readonly bool managed;
        LightMenu menu;
        bool deleting, closed;
        readonly ToolTip copiedHint = new ToolTip { Content = "已复制", Style = LightTheme.HintStyle, Placement = System.Windows.Controls.Primitives.PlacementMode.Center };
        readonly System.Windows.Threading.DispatcherTimer copiedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };

        public PinnedImageWindow(CaptureItem item, Rect origin, double tilt, BitmapSource picture, LineTheme theme, int colorIndex, Action<ShotAction> execute = null, bool managed = true)
        {
            Item = item;
            this.execute = execute;
            this.managed = managed;
            initialTilt = tilt;
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)(origin.X + origin.Width / 2), (int)origin.Y));
            double dpi = WindowsNative.MonitorScale(screen);
            display = screen; displayScale = dpi;
            startWidth = Math.Max(1, origin.Width / dpi - 14);
            startHeight = Math.Max(1, origin.Height / dpi - 14);
            anchor = fallStart = new Point(origin.X + origin.Width / 2, origin.Y - 7 * dpi);
            Title = "桌面贴图 · " + Path.GetFileName(item.Path);
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowActivated = false;
            ShowInTaskbar = false;
            Topmost = true;
            IsHitTestVisible = false;
            Frame.Margin = new Thickness(12, 24, 12, 12);
            Frame.Padding = new Thickness(6);
            Frame.Background = Brushes.White;
            Frame.BorderBrush = LightTheme.Brush("#DCE1E9");
            Frame.BorderThickness = new Thickness(1);
            Frame.CornerRadius = new CornerRadius(12);
            Frame.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.2 };
            Frame.CacheMode = frameCache = new BitmapCache { RenderAtScale = dpi };
            Frame.RenderTransformOrigin = new Point(0.5, 0);
            var transform = new TransformGroup();
            transform.Children.Add(zoom); transform.Children.Add(turn); transform.Children.Add(paperLift);
            Frame.RenderTransform = transform;
            Frame.Cursor = Cursors.SizeAll;
            photo.Stretch = Stretch.Uniform;
            photo.SizeChanged += delegate { photo.Clip = new RectangleGeometry(new Rect(0, 0, photo.ActualWidth, photo.ActualHeight), 7, 7); };
            var layer = new Grid();
            layer.Children.Add(photo);
            Frame.Child = layer;
            PinButton.Content = pin;
            PinButton.Width = PinButton.Height = 32;
            PinButton.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='Transparent'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>");
            PinButton.HorizontalAlignment = HorizontalAlignment.Center;
            PinButton.VerticalAlignment = VerticalAlignment.Top;
            PinButton.Focusable = false;
            PinButton.Cursor = Cursors.Hand;
            PinButton.Opacity = 0;
            PinButton.RenderTransform = pinLift;
            PinButton.ToolTip = new ToolTip { Content = "挂回晾衣绳", Style = LightTheme.HintStyle,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Right, HorizontalOffset = 4 };
            ToolTipService.SetInitialShowDelay(PinButton, 650);
            System.Windows.Automation.AutomationProperties.SetName(PinButton, "挂回晾衣绳");
            PinButton.Click += delegate { ReturnToRope(); };
            surface.Children.Add(Frame); surface.Children.Add(PinButton);
            stageCommit.Tick += delegate { stageCommit.Stop(); if (!rendering) CommitStage(); };
            Content = surface;
            ApplyTheme(theme, colorIndex);
            Frame.ToolTip = resetHint;
            ToolTipService.SetInitialShowDelay(Frame, 700);
            ToolTipService.SetIsEnabled(Frame, false);
            Frame.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (e.ClickCount == 2) { ResetSize(); return; }
                // 拖动要整窗移动，先停掉缩放动画、把窗口收回刚好的大小
                if (staged) { StopRendering(); targetScale = pictureScale; CommitStage(); }
                moving = true;
                var cursor = WindowsNative.Cursor();
                moveStart = new Point(cursor.X, cursor.Y);
                anchorStart = anchor;
                LiftPin(true);
                Frame.CaptureMouse();
            };
            Frame.MouseMove += delegate
            {
                if (!moving) return;
                var cursor = WindowsNative.Cursor();
                anchor = anchorStart + (new Point(cursor.X, cursor.Y) - moveStart);
                Position();
            };
            Frame.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true; moving = false; LiftPin(false); Frame.ReleaseMouseCapture();
            };
            Frame.LostMouseCapture += delegate { if (moving) { moving = false; LiftPin(false); } };
            PreviewMouseWheel += ResizePicture;
            AttachMenu();
            SourceInitialized += delegate
            {
                handle = new WindowInteropHelper(this).Handle;
                WindowsNative.PrepareOverlay(handle);
                HwndSource.FromHwnd(handle).AddHook(delegate(IntPtr window, int message, IntPtr w, IntPtr l, ref bool handled)
                {
                    if (message == 0x21) { handled = true; return new IntPtr(3); }
                    return IntPtr.Zero;
                });
                Position();
            };
            Loaded += delegate { dropStarted = clock.Elapsed.TotalSeconds; StartRendering(); };
            Closing += delegate { ReturnBounds = PhotoRect(); };
            Closed += delegate { StopRendering(); stageCommit.Stop(); };
            bitmap = picture;
            photo.Source = bitmap;
            pictureScale = targetScale = FitScale(bitmap, screen);
            double landingWidth = (bitmap.PixelWidth * pictureScale + 38) * dpi;
            double landingHeight = (bitmap.PixelHeight * pictureScale + 50) * dpi;
            landing = new Point(
                Math.Max(screen.WorkingArea.Left + landingWidth / 2, Math.Min(screen.WorkingArea.Right - landingWidth / 2, fallStart.X)),
                Math.Max(screen.WorkingArea.Top + 17 * dpi, Math.Min(screen.WorkingArea.Bottom - landingHeight + 17 * dpi, fallStart.Y + Math.Max(180 * dpi, screen.WorkingArea.Height * 0.25))));
            Width = logicalWidth = bitmap.PixelWidth * pictureScale + 38;
            Height = logicalHeight = bitmap.PixelHeight * pictureScale + 50;
            zoom.ScaleX = (startWidth + 14) / (Width - 24);
            zoom.ScaleY = (startHeight + 14) / (Height - 36);
            turn.Angle = initialTilt;
            ReturnBounds = origin;
        }

        // 刚贴出来时的大小（也是「双击恢复」的目标）：不超过原图，且不超过 560×420、屏幕的 3/4
        static double FitScale(BitmapSource picture, Forms.Screen screen)
        {
            double dpi = WindowsNative.MonitorScale(screen);
            return Math.Min(1, Math.Min(Math.Min(560, screen.WorkingArea.Width / dpi * 0.75) / picture.PixelWidth,
                Math.Min(420, screen.WorkingArea.Height / dpi * 0.75) / picture.PixelHeight));
        }

        Forms.Screen CurrentScreen() { return Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y)); }

        void UpdateResetHint()
        {
            bool zoomed = Math.Abs(targetScale - FitScale(bitmap, CurrentScreen())) > 1e-6;
            ToolTipService.SetIsEnabled(Frame, zoomed);
            if (!zoomed) resetHint.IsOpen = false;
        }

        // 双击：恢复到刚贴出来时的大小，图钉位置不动
        void ResetSize()
        {
            if (returning || !IsHitTestVisible) return;
            targetScale = FitScale(bitmap, CurrentScreen());
            zoomAroundCursor = false; // 以图钉为中心平滑缩回
            UpdateResetHint();
            PrepareStage();
            StartRendering();
        }

        public void ApplyTheme(LineTheme theme, int colorIndex)
        {
            pin.Apply(theme, colorIndex);
        }

        void LiftPin(bool lifted)
        {
            pinLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(lifted ? -4 : 0, TimeSpan.FromSeconds(0.14)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            paperLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(lifted ? -1 : 0, TimeSpan.FromSeconds(0.14)));
            ((DropShadowEffect)Frame.Effect).BeginAnimation(DropShadowEffect.ShadowDepthProperty, new DoubleAnimation(lifted ? 6 : 4, TimeSpan.FromSeconds(0.14)));
        }

        void ReturnToRope()
        {
            if (returning) return;
            returning = true;
            PinButton.IsEnabled = false;
            ToolTipService.SetIsEnabled(PinButton, false);
            ((ToolTip)PinButton.ToolTip).IsOpen = false;
            pinLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, TimeSpan.FromSeconds(0.14)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.14));
            fade.Completed += delegate { Close(); };
            PinButton.BeginAnimation(OpacityProperty, fade);
        }

        public void UpdateImage()
        {
            bitmap = ReadPicture(Item.Path);
            photo.Source = bitmap;
            if (handle != IntPtr.Zero)
            {
                var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y));
                double dpi = WindowsNative.MonitorScale(screen);
                pictureScale = targetScale = Math.Min(targetScale, Math.Min((screen.WorkingArea.Width / dpi - 40) / bitmap.PixelWidth,
                    (screen.WorkingArea.Height / dpi - 52) / bitmap.PixelHeight));
                StartRendering();
            }
        }

        public static BitmapSource ReadPicture(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                bitmap.Freeze();
                return bitmap;
            }
        }

        // 缩放动画：每一步做完就在「程序空闲」优先级排下一步（低于输入），一步接一步地跑，滚轮永远先处理。
        // - 不挂渲染回调：WPF 里渲染回调优先级高于输入，每帧改窗口（约 15ms，大图到 30~50ms）会把滚轮饿住
        //   （实测第 3 格延迟 631ms、之后 1.8 秒内收不到）；订阅渲染回调还会让 WPF 每帧整窗重画，更慢
        // - 不用计时器：受系统计时精度限制帧数偏少
        // 实测（真实滚轮输入 15 格/每 20ms）：全部收到，延迟最大约 10~20ms
        // 落下动画不需要响应输入，仍直接跟随渲染回调。
        // 2026-11-04 补：有了缩放舞台以后每一步只改排版（不改窗口），很便宜，空闲循环会空转（实测一秒上千步），
        // 所以舞台上的缩放改回跟随渲染回调、每帧一步；不在舞台上（比如拖动中滚轮）仍走空闲循环。
        bool zoomLoop, stepQueued;
        TimeSpan lastRenderingTime = TimeSpan.MinValue;

        void StartRendering()
        {
            if (rendering)
            {
                // 正在空闲循环里追、这时上了舞台：换成每帧一步
                if (zoomLoop && staged) { zoomLoop = false; CompositionTarget.Rendering += Render; }
                return;
            }
            rendering = true;
            if (IsHitTestVisible && !staged) zoomLoop = true;
            else CompositionTarget.Rendering += Render;
            Render(null, EventArgs.Empty);
            QueueZoomStep();
        }

        void QueueZoomStep()
        {
            if (!rendering || !zoomLoop || stepQueued) return;
            stepQueued = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate
            {
                stepQueued = false;
                if (!rendering || !zoomLoop) return;
                Render(this, EventArgs.Empty);
                QueueZoomStep();
            }));
        }

        void StopRendering()
        {
            rendering = false; lastZoomFrame = -1; zoomLoop = false;
            CompositionTarget.Rendering -= Render;
        }

        void Render(object sender, EventArgs args)
        {
            if (positioning) return; // 正在改窗口时插进来的帧直接跳过，等这次改完下一帧再走
            // 渲染回调同一帧可能来不止一次，同一帧只走一步
            var frameArgs = args as RenderingEventArgs;
            if (frameArgs != null) { if (frameArgs.RenderingTime == lastRenderingTime) return; lastRenderingTime = frameArgs.RenderingTime; }
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = Math.Max(0, now - dropStarted);
            double t = Math.Min(1, elapsed / 0.40);
            double progress = t * t * t * (10 + t * (-15 + 6 * t));
            if (IsHitTestVisible) StepZoom(now);
            logicalWidth = bitmap.PixelWidth * pictureScale + 38;
            logicalHeight = bitmap.PixelHeight * pictureScale + 50;
            zoom.ScaleX = (startWidth + 14) / (logicalWidth - 24) * (1 - progress) + progress;
            zoom.ScaleY = (startHeight + 14) / (logicalHeight - 36) * (1 - progress) + progress;
            turn.Angle = initialTilt * (1 - progress);
            if (!IsHitTestVisible)
            {
                double settle = Math.Max(0, Math.Min(1, (elapsed - 0.40) / 0.14));
                double bounce = Math.Sin(Math.PI * settle);
                anchor = fallStart + (landing - fallStart) * progress - new Vector(0, 2 * displayScale * bounce * bounce);
                PinButton.Opacity = settle;
                pinLift.Y = -6 * (1 - settle);
                if (elapsed >= 0.54) IsHitTestVisible = true;
            }
            Position();
            if (IsHitTestVisible && pictureScale == targetScale)
            {
                StopRendering();
                // 停稳一小会儿再收窗口：连着滚的下一格多半马上就来，不必收了又撑
                if (staged) { stageCommit.Stop(); stageCommit.Start(); }
            }
        }

        // 每帧一步：当前比例按指数平滑追向目标（在对数空间插值，放大缩小同样匀速）；以光标为中心时同步算出新锚点
        void StepZoom(double now)
        {
            if (pictureScale == targetScale) return;
            double dt = lastZoomFrame < 0 ? 1 / 60.0 : Math.Max(0.001, Math.Min(0.05, now - lastZoomFrame));
            lastZoomFrame = now;
            double previous = pictureScale;
            double k = 1 - Math.Exp(-dt / ZoomTimeConstant);
            double next = Math.Exp(Math.Log(previous) + (Math.Log(targetScale) - Math.Log(previous)) * k);
            if (Math.Abs(next - targetScale) <= targetScale * 0.002) next = targetScale;
            pictureScale = next;
            if (!zoomAroundCursor || placedWidth <= 0) return;
            double dpi = WindowsNative.MonitorScale(CurrentScreen());
            // 用不取整的锚点推算当前左上角，多次缩放不会累积取整误差
            var exactTopLeft = new Point(anchor.X - placedWidth / 2.0, anchor.Y - 17 * dpi);
            var topLeft = ZoomRules.AroundCursor(zoomCursor, exactTopLeft,
                bitmap.PixelWidth * previous, bitmap.PixelHeight * previous, bitmap.PixelWidth * next, bitmap.PixelHeight * next, dpi);
            double newWidth = Math.Round((bitmap.PixelWidth * next + 38) * dpi);
            anchor = new Point(topLeft.X + newWidth / 2.0, topLeft.Y + 17 * dpi);
        }

        // 改窗口时 WPF 会同步重绘并在里面再触发一帧 Render；不防重入的话调用一层套一层，
        // 退出时外层把早已过时的尺寸再设回去，窗口就被一路拉回缩小（实测嵌套二三十层、单次 791ms）
        bool positioning;

        void Position()
        {
            if (positioning) return;
            positioning = true;
            try { PositionCore(); }
            finally { positioning = false; }
        }

        void PositionCore()
        {
            var screen = IsHitTestVisible ? Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y)) : display;
            double dpi = IsHitTestVisible ? WindowsNative.MonitorScale(screen) : displayScale;
            int width = (int)Math.Round(logicalWidth * dpi), height = (int)Math.Round(logicalHeight * dpi);
            int left = (int)Math.Round(anchor.X - width / 2.0), top = (int)Math.Round(anchor.Y - 17 * dpi);
            if (IsHitTestVisible)
            {
                int clampedLeft = Math.Max(screen.WorkingArea.Left, Math.Min(screen.WorkingArea.Right - width, left));
                int clampedTop = Math.Max(screen.WorkingArea.Top, Math.Min(screen.WorkingArea.Bottom - height, top));
                // 被屏幕边缘推回来时记住新位置；否则缩小时会跳回屏幕外那个旧位置
                if ((clampedLeft != left || clampedTop != top) && !moving) anchor = new Point(clampedLeft + width / 2.0, clampedTop + 17 * dpi);
                left = clampedLeft; top = clampedTop;
            }
            if (staged) { PlaceOnStage(left, top, width, height, dpi); return; }
            if (left == placedLeft && top == placedTop && width == placedWidth && height == placedHeight) return;
            bool resize = width != placedWidth || height != placedHeight;
            placedLeft = left; placedTop = top; placedWidth = width; placedHeight = height;
            // 位置和大小一次改完；再把同样的尺寸告诉 WPF，它就不会再单独改宽、改高（原版每滚一格窗口要变 3 次）
            WindowsNative.SetWindowPos(handle, IntPtr.Zero, left, top, width, height, resize ? 0x14u : 0x15u);
            if (resize) { Width = width / dpi; Height = height / dpi; }
        }

        // 按比例 scale 时贴图窗口应在的位置和大小（物理像素，已夹在工作区内）；不改任何状态，用来提前撑好舞台
        Int32Rect PlannedRect(double scale, Forms.Screen screen, double dpi)
        {
            int width = (int)Math.Round((bitmap.PixelWidth * scale + 38) * dpi), height = (int)Math.Round((bitmap.PixelHeight * scale + 50) * dpi);
            double left = anchor.X - width / 2.0, top = anchor.Y - 17 * dpi;
            if (zoomAroundCursor && placedWidth > 0)
            {
                var topLeft = ZoomRules.AroundCursor(zoomCursor, new Point(anchor.X - placedWidth / 2.0, anchor.Y - 17 * dpi),
                    bitmap.PixelWidth * pictureScale, bitmap.PixelHeight * pictureScale, bitmap.PixelWidth * scale, bitmap.PixelHeight * scale, dpi);
                left = topLeft.X; top = topLeft.Y;
            }
            int x = (int)Math.Round(left), y = (int)Math.Round(top);
            x = Math.Max(screen.WorkingArea.Left, Math.Min(screen.WorkingArea.Right - width, x));
            y = Math.Max(screen.WorkingArea.Top, Math.Min(screen.WorkingArea.Bottom - height, y));
            return new Int32Rect(x, y, width, height);
        }

        // 缩放开始前调用：保证窗口已撑到能装下「现在」和「目标」两个位置（放大时再多留两格的余量），
        // 这样整段动画都不用再改窗口尺寸
        void PrepareStage()
        {
            if (!IsHitTestVisible || moving || returning || handle == IntPtr.Zero || placedWidth <= 0) return;
            var screen = CurrentScreen();
            double dpi = WindowsNative.MonitorScale(screen);
            stageCommit.Stop();
            if (!staged)
            {
                staged = true;
                stageLeft = placedLeft; stageTop = placedTop; stageWidth = placedWidth; stageHeight = placedHeight;
                Frame.CacheMode = null; // 内容每帧改尺寸时位图缓存每帧重建，白费功夫（实测关掉更稳）
                surface.HorizontalAlignment = HorizontalAlignment.Left; surface.VerticalAlignment = VerticalAlignment.Top;
                surface.Width = placedWidth / dpi; surface.Height = placedHeight / dpi;
                surface.Margin = new Thickness(0);
            }
            var need = PlannedRect(targetScale, screen, dpi);
            if (StageContains(need)) return;
            // 装不下才撑；放大时一次多留 4 格（1.12⁴ ≈ 1.57 倍）的余量，连着往上滚不用每格都撑一次
            var grown = Union(need, new Int32Rect(stageLeft, stageTop, stageWidth, stageHeight));
            if (targetScale > pictureScale)
            {
                double maximum = Math.Min((screen.WorkingArea.Width / dpi - 40) / bitmap.PixelWidth, (screen.WorkingArea.Height / dpi - 52) / bitmap.PixelHeight);
                grown = Union(grown, PlannedRect(Math.Min(maximum, targetScale * Math.Pow(1.12, 4)), screen, dpi));
            }
            GrowStage(grown, dpi, screen);
        }

        static Int32Rect Union(Int32Rect a, Int32Rect b)
        {
            int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
            return new Int32Rect(left, top, Math.Max(a.X + a.Width, b.X + b.Width) - left, Math.Max(a.Y + a.Height, b.Y + b.Height) - top);
        }

        bool StageContains(Int32Rect r)
        {
            return r.X >= stageLeft && r.Y >= stageTop && r.X + r.Width <= stageLeft + stageWidth && r.Y + r.Height <= stageTop + stageHeight;
        }

        void GrowStage(Int32Rect r, double dpi, Forms.Screen screen)
        {
            // 四周多留 8 像素：预估位置和逐帧实际位置会有一两像素的取整差，别为这点差再撑一次
            // 舞台不超出工作区（贴图本身也被夹在工作区内，超出部分没用）
            int left = Math.Max(screen.WorkingArea.Left, r.X - 8), top = Math.Max(screen.WorkingArea.Top, r.Y - 8);
            int right = Math.Min(screen.WorkingArea.Right, r.X + r.Width + 8), bottom = Math.Min(screen.WorkingArea.Bottom, r.Y + r.Height + 8);
            if (left > r.X || top > r.Y || right < r.X + r.Width || bottom < r.Y + r.Height)
            {
                // 要装的东西本身就超出了这块屏幕的工作区（例如跨屏），不夹
                left = Math.Min(left, r.X); top = Math.Min(top, r.Y); right = Math.Max(right, r.X + r.Width); bottom = Math.Max(bottom, r.Y + r.Height);
            }
            stageLeft = left; stageTop = top; stageWidth = right - left; stageHeight = bottom - top;
            bool wasPositioning = positioning;
            positioning = true; // 改窗口时 WPF 会同步重绘并插进一帧 Render，挡掉（坑 2）
            try
            {
                ResizeWithoutFlash(stageLeft, stageTop, stageWidth, stageHeight, dpi, delegate
                {
                    surface.Margin = new Thickness((placedLeft - stageLeft) / dpi, (placedTop - stageTop) / dpi, 0, 0);
                });
            }
            finally { positioning = wasPositioning; }
        }

        // 改分层窗口的位置和大小，同时把内容挪到对应位置。
        // 普通 SetWindowPos 会让系统先把「旧画面」放到新位置上显示一帧（实测 16 次里闪 3~4 次，图片往左上一跳）；
        // 带 NOCOPYBITS|NOREDRAW 改、排好内容后再补一次 FRAMECHANGED，实测 32 次 0 闪（2026-11-04）
        void ResizeWithoutFlash(int left, int top, int width, int height, double dpi, Action layout)
        {
            WindowsNative.SetWindowPos(handle, IntPtr.Zero, left, top, width, height, 0x14 | 0x100 | 0x8);
            Width = width / dpi; Height = height / dpi;
            layout();
            WindowsNative.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x37);
        }

        // 动画中每帧：只挪动、缩放窗口里的内容，窗口本身不动
        void PlaceOnStage(int left, int top, int width, int height, double dpi)
        {
            if (!StageContains(new Int32Rect(left, top, width, height)))
                GrowStage(Union(new Int32Rect(left, top, width, height), new Int32Rect(stageLeft, stageTop, stageWidth, stageHeight)), dpi, CurrentScreen());
            placedLeft = left; placedTop = top; placedWidth = width; placedHeight = height;
            surface.Margin = new Thickness((left - stageLeft) / dpi, (top - stageTop) / dpi, 0, 0);
            surface.Width = width / dpi; surface.Height = height / dpi;
        }

        // 动画停稳后：窗口收回刚好包住贴图的大小，内容铺满，恢复位图缓存
        void CommitStage()
        {
            if (!staged) return;
            staged = false;
            stageCommit.Stop();
            if (handle == IntPtr.Zero) return;
            double dpi = WindowsNative.MonitorScale(CurrentScreen());
            bool wasPositioning = positioning;
            positioning = true;
            try
            {
                ResizeWithoutFlash(placedLeft, placedTop, placedWidth, placedHeight, dpi, delegate
                {
                    surface.Margin = new Thickness(0);
                    surface.Width = surface.Height = double.NaN;
                    surface.HorizontalAlignment = HorizontalAlignment.Stretch; surface.VerticalAlignment = VerticalAlignment.Stretch;
                    Frame.CacheMode = frameCache;
                });
            }
            finally { positioning = wasPositioning; }
        }

        void ResizePicture(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var screen = WindowsNative.ScreenAtPointer();
            double dpi = WindowsNative.MonitorScale(screen);
            double maximum = Math.Min((screen.WorkingArea.Width / dpi - 40) / bitmap.PixelWidth,
                (screen.WorkingArea.Height / dpi - 52) / bitmap.PixelHeight);
            // 只改目标比例（几乎不花时间），真正改窗口交给每帧的 StepZoom：快速连滚不会积压、也不会一格一跳
            double next = Math.Max(Math.Min(0.08, maximum), Math.Min(maximum, targetScale * Math.Pow(1.12, e.Delta / 120.0)));
            if (next == targetScale) return;
            targetScale = next;
            var cursor = WindowsNative.Cursor();
            bool overPicture = cursor.X >= placedLeft && cursor.X < placedLeft + placedWidth && cursor.Y >= placedTop && cursor.Y < placedTop + placedHeight;
            // 以光标为中心缩放：光标指着的那一点缩放后仍在光标下；光标不在贴图上时保持原版的「以图钉为中心」
            zoomAroundCursor = IsHitTestVisible && placedWidth > 0 && overPicture;
            zoomCursor = new Point(cursor.X, cursor.Y);
            UpdateResetHint();
            PrepareStage();
            StartRendering();
        }

        public Rect PhotoRect()
        {
            return new Rect(Frame.PointToScreen(new Point()), Frame.PointToScreen(new Point(Frame.ActualWidth, Frame.ActualHeight)));
        }

        // ---------- 右键菜单 ----------
        void AttachMenu()
        {
            Closed += delegate { closed = true; copiedTimer.Stop(); copiedHint.IsOpen = false; if (menu != null) menu.IsOpen = false; };
            copiedTimer.Tick += delegate { copiedTimer.Stop(); copiedHint.IsOpen = false; };
            copiedHint.PlacementTarget = Frame;
            Frame.MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (execute == null || returning || deleting || !IsHitTestVisible) return;
                if (menu == null) menu = BuildMenu();
                menu.SetDetails(bitmap.PixelWidth + " × " + bitmap.PixelHeight);
                menu.PlacementTarget = Frame;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                // 和绳子上的卡片一样先把窗口拉到前台，菜单才能用键盘、点别处才会收起
                WindowsNative.SetForegroundWindow(handle);
                menu.IsOpen = true;
            };
        }

        LightMenu BuildMenu()
        {
            var built = new LightMenu("贴图", "");
            built.AddAction("复制图片", "copy", Copy);
            built.AddAction("打开", "open", delegate { execute(ShotAction.Open); });
            built.AddAction("编辑", "edit", delegate { execute(ShotAction.Edit); });
            built.AddAction("保存副本…", "save", delegate { execute(ShotAction.Save); });
            built.AddAction("打开位置", "folder", delegate { execute(ShotAction.Locate); });
            built.Separator();
            built.AddAction("挂回晾衣绳", "startup", ReturnToRope);
            built.AddAction(managed ? "删除（移到回收站）" : "移除记录（保留原图）", "trash", Delete, false, true);
            return built;
        }

        void Copy()
        {
            execute(ShotAction.Copy);
            if (closed) return;
            copiedHint.IsOpen = true;
            copiedTimer.Stop(); copiedTimer.Start();
        }

        // 删除：贴图往下一沉、淡出，再交给和绳子上「移到回收站」同一个动作（成功后贴图窗口会被关掉）
        void Delete()
        {
            if (deleting || returning) return;
            deleting = true;
            Frame.IsHitTestVisible = false; PinButton.IsEnabled = false;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            paperLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, TimeSpan.FromSeconds(0.18)) { EasingFunction = ease });
            pinLift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(22, TimeSpan.FromSeconds(0.18)) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.18));
            fade.Completed += delegate
            {
                execute(ShotAction.Trash);
                if (closed) return;
                // 没删成（比如文件被占用，App 会弹提示）：贴图恢复原样
                deleting = false;
                Frame.IsHitTestVisible = true; PinButton.IsEnabled = true;
                ((UIElement)Content).BeginAnimation(OpacityProperty, null);
                paperLift.BeginAnimation(TranslateTransform.YProperty, null);
                pinLift.BeginAnimation(TranslateTransform.YProperty, null);
            };
            ((UIElement)Content).BeginAnimation(OpacityProperty, fade);
        }
    }
}
