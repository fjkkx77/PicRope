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
        int placedLeft = int.MinValue, placedTop = int.MinValue, placedWidth, placedHeight;
        bool moving, rendering, returning;
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
            Frame.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                e.Handled = true;
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
            Closed += delegate { CompositionTarget.Rendering -= Render; };
            bitmap = picture;
            photo.Source = bitmap;
            pictureScale = Math.Min(1, Math.Min(Math.Min(560, screen.WorkingArea.Width / dpi * 0.75) / bitmap.PixelWidth,
                Math.Min(420, screen.WorkingArea.Height / dpi * 0.75) / bitmap.PixelHeight));
            double landingWidth = (bitmap.PixelWidth * pictureScale + 38) * dpi;
            double landingHeight = (bitmap.PixelHeight * pictureScale + 50) * dpi;
            landing = new Point(
                Math.Max(screen.WorkingArea.Left + landingWidth / 2, Math.Min(screen.WorkingArea.Right - landingWidth / 2, fallStart.X)),
                Math.Max(screen.WorkingArea.Top + 17 * dpi, Math.Min(screen.WorkingArea.Bottom - landingHeight + 17 * dpi, fallStart.Y + Math.Max(180 * dpi, screen.WorkingArea.Height * 0.25))));
            Width = bitmap.PixelWidth * pictureScale + 38;
            Height = bitmap.PixelHeight * pictureScale + 50;
            zoom.ScaleX = (startWidth + 14) / (Width - 24);
            zoom.ScaleY = (startHeight + 14) / (Height - 36);
            turn.Angle = initialTilt;
            ReturnBounds = origin;
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
                pictureScale = Math.Min(pictureScale, Math.Min((screen.WorkingArea.Width / dpi - 40) / bitmap.PixelWidth,
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
            double now = clock.Elapsed.TotalSeconds;
            double elapsed = Math.Max(0, now - dropStarted);
            double t = Math.Min(1, elapsed / 0.40);
            double progress = t * t * t * (10 + t * (-15 + 6 * t));
            Width = bitmap.PixelWidth * pictureScale + 38;
            Height = bitmap.PixelHeight * pictureScale + 50;
            zoom.ScaleX = (startWidth + 14) / (Width - 24) * (1 - progress) + progress;
            zoom.ScaleY = (startHeight + 14) / (Height - 36) * (1 - progress) + progress;
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
            if (IsHitTestVisible)
            {
                rendering = false;
                CompositionTarget.Rendering -= Render;
            }
        }

        void Position()
        {
            var screen = IsHitTestVisible ? Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.X, (int)anchor.Y)) : display;
            double dpi = IsHitTestVisible ? WindowsNative.MonitorScale(screen) : displayScale;
            int width = (int)Math.Round(Width * dpi), height = (int)Math.Round(Height * dpi);
            int left = (int)Math.Round(anchor.X - width / 2.0), top = (int)Math.Round(anchor.Y - 17 * dpi);
            if (IsHitTestVisible)
            {
                left = Math.Max(screen.WorkingArea.Left, Math.Min(screen.WorkingArea.Right - width, left));
                top = Math.Max(screen.WorkingArea.Top, Math.Min(screen.WorkingArea.Bottom - height, top));
            }
            if (left == placedLeft && top == placedTop && width == placedWidth && height == placedHeight) return;
            bool resize = width != placedWidth || height != placedHeight;
            placedLeft = left; placedTop = top; placedWidth = width; placedHeight = height;
            WindowsNative.SetWindowPos(handle, IntPtr.Zero, left, top, width, height, resize ? 0x14u : 0x15u);
        }

        void ResizePicture(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            var screen = WindowsNative.ScreenAtPointer();
            double dpi = WindowsNative.MonitorScale(screen);
            double maximum = Math.Min((screen.WorkingArea.Width / dpi - 40) / bitmap.PixelWidth,
                (screen.WorkingArea.Height / dpi - 52) / bitmap.PixelHeight);
            pictureScale = Math.Max(Math.Min(0.08, maximum), Math.Min(maximum, pictureScale * Math.Pow(1.12, e.Delta / 120.0)));
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
