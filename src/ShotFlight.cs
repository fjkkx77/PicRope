using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    // 飞行动画用的整屏透明窗口。新建并显示一个这样的窗口要 50~100ms（实测，跟面积关系不大），
    // 正好卡在截图上绳的那一瞬间；所以窗口用完不关，清空后藏起来留着下次用（再次显示约 2.5ms），
    // 程序启动后空闲时先备好一个。动画本身的代码没有改。
    internal sealed class ShotFlight : Window
    {
        static readonly List<ShotFlight> idle = new List<ShotFlight>();
        const int IdleLimit = 2;
        ShotCard card;
        Rect screenOrigin;
        Forms.Screen screen;
        readonly Canvas stage = new Canvas(), frame = new Canvas();
        readonly Border border = new Border();
        readonly Image photo = new Image();
        readonly ClothesClip clip = new ClothesClip();
        readonly RotateTransform turn = new RotateTransform();
        readonly DropShadowEffect shadow = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 4 };
        readonly Stopwatch clock = new Stopwatch();
        readonly DispatcherTimer hideLater = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        Action landed;
        Func<Rect> basketTarget;
        Rect origin;
        IntPtr handle;
        bool completed, arrived, active, closed;
        public bool Falling { get; private set; }
        public bool Collecting { get { return basketTarget != null; } }
        // 这一趟飞行结束（正常落地、被取消或窗口被关掉）时触发一次
        public event Action Finished;

        // 取一个窗口来飞：优先用备好的，没有才新建
        public static ShotFlight Take(Forms.Screen screen, ShotCard card, Rect origin, bool falling, Action landed, BitmapSource image = null, Func<Rect> basketTarget = null)
        {
            ShotFlight flight = null;
            double scale = WindowsNative.MonitorScale(screen);
            while (idle.Count > 0 && flight == null)
            {
                flight = idle[idle.Count - 1]; idle.RemoveAt(idle.Count - 1);
                if (flight.closed) { flight = null; continue; }
                // 换到缩放比例不同的屏幕：藏着的窗口未必及时换算 DPI，不冒险复用，关掉新建（多屏才会遇到）
                if (Math.Abs(WindowsNative.MonitorScale(flight.screen) - scale) > 0.001) { flight.Close(); flight = null; }
            }
            if (flight == null) flight = new ShotFlight();
            flight.Prepare(screen, card, origin, falling, landed, image, basketTarget);
            return flight;
        }

        // 程序空闲时先建好一个窗口（显示一次再藏起来），第一次上绳就不用现建
        public static void Prewarm()
        {
            if (idle.Any(waiting => !waiting.closed)) return;
            var flight = new ShotFlight();
            flight.screen = Forms.Screen.PrimaryScreen;
            flight.frame.Visibility = Visibility.Hidden;
            flight.PlaceOnScreen();
            flight.Show();
            idle.Add(flight);
            flight.hideLater.Start();
        }

        public static void CloseIdle()
        {
            foreach (var flight in idle.ToArray()) flight.Close();
            idle.Clear();
        }

        ShotFlight()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowActivated = false;
            ShowInTaskbar = false;
            Topmost = true;
            IsHitTestVisible = false;
            Content = stage;
            stage.Children.Add(frame);
            frame.RenderTransform = turn;
            photo.Stretch = Stretch.Fill;
            border.Child = photo;
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(220, 225, 233));
            border.Effect = shadow;
            frame.Children.Add(border);
            frame.Children.Add(clip);
            SourceInitialized += delegate
            {
                handle = new WindowInteropHelper(this).Handle;
                WindowsNative.PrepareOverlay(handle);
                WindowsNative.PassMouse(handle, true);
                PlaceOnScreen();
            };
            // 藏之前先等清空的画面真正画上去：分层窗口再次显示时会先露出藏起来前的最后一帧
            hideLater.Tick += delegate { hideLater.Stop(); if (!active && !closed) Hide(); };
            Closed += delegate
            {
                closed = true;
                hideLater.Stop();
                idle.Remove(this);
                if (active) Finish(false);
            };
        }

        void Prepare(Forms.Screen screen, ShotCard card, Rect origin, bool falling, Action landed, BitmapSource image, Func<Rect> basketTarget)
        {
            this.screen = screen;
            this.card = card;
            screenOrigin = origin;
            this.landed = landed;
            this.basketTarget = basketTarget;
            Falling = falling;
            completed = arrived = false;
            ApplyTheme(card.Appearance);
            photo.Source = image ?? card.Item.Image.Thumbnail;
            frame.Clip = null;
            Opacity = 1;
        }

        void PlaceOnScreen()
        {
            double scale = WindowsNative.MonitorScale(screen);
            Width = screen.Bounds.Width / scale;
            Height = screen.Bounds.Height / scale;
            if (handle != IntPtr.Zero)
                WindowsNative.SetWindowPos(handle, new IntPtr(-1), screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height, 0x10);
        }

        // 开始飞（代替原来的 Show）
        public void Play()
        {
            active = true;
            hideLater.Stop();
            PlaceOnScreen();
            frame.Visibility = Visibility.Visible;
            if (!IsVisible) Show(); // 新窗口：Show 时触发 SourceInitialized 摆好位置
            origin = Local(screenOrigin);
            clock.Restart();
            Render(null, EventArgs.Empty);
            if (active) CompositionTarget.Rendering += Render;
        }

        // 中途取消（代替原来的 Close）：和原来关窗口一样，照常通知落地
        public void Stop() { if (active) Finish(true); }

        // 一趟结束：停动画、清空画面、通知落地，然后把窗口留着下次用
        void Finish(bool recycle)
        {
            active = false;
            CompositionTarget.Rendering -= Render;
            frame.Visibility = Visibility.Hidden;
            Complete();
            if (Finished != null) Finished();
            Finished = null;
            card = null; landed = null; basketTarget = null; photo.Source = null;
            if (!recycle || closed) return;
            if (idle.Count(flight => !flight.closed) >= IdleLimit) { Close(); return; }
            idle.Add(this);
            hideLater.Start();
        }

        Rect Local(Rect rectangle)
        {
            return new Rect(PointFromScreen(rectangle.TopLeft), PointFromScreen(rectangle.BottomRight));
        }

        public void ApplyTheme(LineTheme theme) { if (card != null) clip.Apply(theme, card.ColorIndex); }

        void Complete()
        {
            if (completed) return;
            completed = true;
            if (Collecting && !arrived) return;
            if (landed != null) landed();
        }

        void Render(object sender, EventArgs args)
        {
            if (!active) return;
            if (!Falling && !Collecting && card.IsDisposed) { Finish(true); return; }
            double seconds = clock.Elapsed.TotalSeconds;
            if (Collecting) { RenderCollection(Math.Min(1, seconds / 0.64)); return; }
            double progress = Math.Min(1, seconds / (Falling ? 0.55 : 0.65));
            // 五次曲线让起飞与抵达的速度、加速度都平滑归零。
            double ease = progress * progress * progress * (10 + progress * (-15 + 6 * progress));
            Rect target = Falling ? origin : Local(card.PhotoRect());
            double width = origin.Width + (target.Width - origin.Width) * ease;
            double height = origin.Height + (target.Height - origin.Height) * ease;
            double x = origin.X + origin.Width / 2 + (target.X + target.Width / 2 - origin.X - origin.Width / 2) * ease;
            double y = origin.Y + (target.Y - origin.Y) * ease - 30 * Math.Sin(Math.PI * ease);
            double chrome = Math.Max(0, Math.Min(1, (ease - 0.3) / 0.6));
            chrome = chrome * chrome * (3 - 2 * chrome);
            turn.Angle = card.Tilt * ease;
            if (Falling)
            {
                width = origin.Width; height = origin.Height;
                x = origin.X + width / 2;
                y = origin.Y + 520 * progress * progress;
                turn.Angle = card.Tilt + (card.Tilt < 0 ? -32 : 32) * progress * progress;
                chrome = 1;
                Opacity = 1 - progress * progress;
            }
            frame.Width = border.Width = width;
            frame.Height = border.Height = height;
            Canvas.SetLeft(frame, x - width / 2);
            Canvas.SetTop(frame, y);
            turn.CenterX = width / 2;
            turn.CenterY = -12;
            border.Background = new SolidColorBrush(Color.FromArgb((byte)(245 * chrome), 255, 255, 255));
            border.BorderThickness = new Thickness(chrome);
            border.CornerRadius = new CornerRadius(12 * (Falling ? 1 : ease));
            photo.Margin = new Thickness(6 * (Falling ? 1 : ease));
            photo.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(1, width - photo.Margin.Left * 2 - chrome * 2), Math.Max(1, height - photo.Margin.Top * 2 - chrome * 2)), 7 * (Falling ? 1 : ease), 7 * (Falling ? 1 : ease));
            shadow.Opacity = 0.2 * chrome;
            clip.Opacity = chrome;
            Canvas.SetLeft(clip, width / 2 - 8);
            Canvas.SetTop(clip, -20);
            if (progress < 1) return;
            Complete();
            if (Falling) { Finish(true); return; }
            Opacity = Math.Max(0, 1 - (seconds - 0.65) / 0.16);
            if (seconds >= 0.81) Finish(true);
        }

        void RenderCollection(double progress)
        {
            Rect mouth = Local(basketTarget());
            double ease = progress * progress * progress * (10 + progress * (-15 + 6 * progress));
            double smallWidth = Math.Min(22, Math.Max(10, origin.Width * 0.15));
            double smallHeight = smallWidth * origin.Height / origin.Width;
            double width = origin.Width + (smallWidth - origin.Width) * ease;
            double height = origin.Height + (smallHeight - origin.Height) * ease;
            double startX = origin.X + origin.Width / 2, endX = mouth.X + mouth.Width / 2;
            double x = startX + (endX - startX) * ease;
            double lift = Math.Min(38, Math.Max(10, origin.Y - 10));
            double y = origin.Y + origin.Height / 2 + (mouth.Y + smallHeight / 2 + 4 - origin.Y - origin.Height / 2) * ease
                - lift * Math.Sin(Math.PI * ease);
            double top = y - height / 2;
            frame.Width = border.Width = width; frame.Height = border.Height = height;
            Canvas.SetLeft(frame, x - width / 2); Canvas.SetTop(frame, top);
            turn.CenterX = width / 2; turn.CenterY = height / 2;
            turn.Angle = card.Tilt * (1 - ease) + Math.Sign(endX - startX) * 10 * Math.Sin(Math.PI * ease);
            border.Background = Brushes.White; border.BorderThickness = new Thickness(1);
            border.CornerRadius = new CornerRadius(Math.Min(9, width * 0.09));
            photo.Margin = new Thickness(Math.Min(6, width * 0.04));
            shadow.Opacity = 0.16 * (1 - ease); clip.Opacity = 0;
            Opacity = 1 - 0.8 * Math.Max(0, (progress - 0.82) / 0.18);
            frame.Clip = null;
            // 图片跨过篮口后逐渐被篮身遮住，而不是在篮子前方淡出。
            if (progress > 0.68 && Math.Abs(x - endX) < mouth.Width * 0.6)
                frame.Clip = new RectangleGeometry(new Rect(0, 0, width, Math.Max(0, Math.Min(height, mouth.Y - top))));
            if (progress < 1) return;
            arrived = true; Complete(); Finish(true);
        }
    }
}
