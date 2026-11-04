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
        public Rect ReturnBounds { get; private set; }
        public BitmapSource Picture { get { return bitmap; } }

        public PinnedImageWindow(CaptureItem item, Rect origin, double tilt, BitmapSource picture, LineTheme theme, int colorIndex)
        {
            Item = item;
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
            Frame.CacheMode = new BitmapCache { RenderAtScale = dpi };
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
            var surface = new Grid();
            surface.Children.Add(Frame); surface.Children.Add(PinButton);
            Content = surface;
            ApplyTheme(theme, colorIndex);
            Frame.ToolTip = resetHint;
            ToolTipService.SetInitialShowDelay(Frame, 700);
            ToolTipService.SetIsEnabled(Frame, false);
            Frame.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (e.ClickCount == 2) { ResetSize(); return; }
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
            Closed += delegate { CompositionTarget.Rendering -= Render; };
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

        void StartRendering()
        {
            if (rendering) return;
            rendering = true;
            CompositionTarget.Rendering += Render;
            Render(null, EventArgs.Empty);
        }

        void Render(object sender, EventArgs args)
        {
            if (positioning) return; // 正在改窗口时插进来的帧直接跳过，等这次改完下一帧再走
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
                rendering = false; lastZoomFrame = -1;
                CompositionTarget.Rendering -= Render;
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
            if (left == placedLeft && top == placedTop && width == placedWidth && height == placedHeight) return;
            bool resize = width != placedWidth || height != placedHeight;
            placedLeft = left; placedTop = top; placedWidth = width; placedHeight = height;
            // 位置和大小一次改完；再把同样的尺寸告诉 WPF，它就不会再单独改宽、改高（原版每滚一格窗口要变 3 次）
            WindowsNative.SetWindowPos(handle, IntPtr.Zero, left, top, width, height, resize ? 0x14u : 0x15u);
            if (resize) { Width = width / dpi; Height = height / dpi; }
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
            StartRendering();
        }

        public Rect PhotoRect()
        {
            return new Rect(Frame.PointToScreen(new Point()), Frame.PointToScreen(new Point(Frame.ActualWidth, Frame.ActualHeight)));
        }
    }
}
