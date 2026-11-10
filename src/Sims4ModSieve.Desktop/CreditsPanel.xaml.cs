using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace Sims4ModSieve.Desktop;

public partial class CreditsPanel : UserControl
{
    internal const int MaximumHearts = 4;
    internal const int MaximumParticles = 160;
    private const int BurstSize = 70;
    private const int PetalsPerBurst = 15;
    private static readonly Brush[] ConfettiBrushes = [Brush("#FF6B8A"), Brush("#FF9F43"), Brush("#FFD93D"),
        Brush("#4ADE80"), Brush("#38BDF8"), Brush("#A78BFA"), Brush("#F472B6")];
    private readonly CreditsInteractionState interaction = new();
    private readonly List<Particle> particles = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherTimer frameTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private double? messageBirth;

    public CreditsPanel()
    {
        InitializeComponent();
        frameTimer.Tick += RenderFrame;
    }

    private sealed class Particle
    {
        public required FrameworkElement Element { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required RotateTransform Rotation { get; init; }
        public Point Origin { get; init; }
        public double Birth { get; init; }
        public double Lifetime { get; init; }
        public double VelocityX { get; init; }
        public double VelocityY { get; init; }
        public double Gravity { get; init; }
        public double Spin { get; init; }
        public double Drift { get; init; }
        public double Phase { get; init; }
        public double InitialAngle { get; init; }
        public double Drag { get; init; }
        public double TerminalVelocity { get; init; }
        public double FadeDuration { get; init; }
        public double? HeartFadeStart { get; set; }
        public Point? NextHeartOrigin { get; set; }
        public double ReplacementRequestedAt { get; set; }
        public bool IsHeart { get; init; }
        public bool IsRibbon { get; init; }
    }

    private static Brush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private void Surface_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        EmitHeart(e.GetPosition(EffectsOverlay));
    }

    internal void EmitHeart(Point origin)
    {
        var hearts = particles.Where(particle => particle.IsHeart).ToArray();
        if (hearts.Length < MaximumHearts) AddHeart(origin);
        else
        {
            var oldest = hearts.OrderBy(heart => heart.HeartFadeStart.HasValue)
                .ThenBy(heart => heart.HeartFadeStart.HasValue ? heart.ReplacementRequestedAt : heart.Birth).First();
            oldest.HeartFadeStart ??= clock.Elapsed.TotalSeconds;
            oldest.NextHeartOrigin = origin;
            oldest.ReplacementRequestedAt = clock.Elapsed.TotalSeconds;
        }
    }

    private void AddHeart(Point origin)
    {
        var heart = new Path
        {
            Data = (Geometry)FindResource("CreditsHeartGeometry"), Fill = Brush("#CA92A6"),
            Stretch = Stretch.Uniform, Width = Random.Shared.Next(10, 16), Height = 15, Tag = "Heart"
        };
        AddParticle(heart, origin, true, false, 1.5,
            36 + Random.Shared.NextDouble() * 24, -70 - Random.Shared.NextDouble() * 25, 0, -12, 0, fadeDuration: 1.5);
    }

    private void ConfettiButton_Click(object sender, RoutedEventArgs e)
    {
        var surprise = interaction.ClickConfetti();
        var origin = ConfettiButton.TranslatePoint(new Point(ConfettiButton.ActualWidth / 2, ConfettiButton.ActualHeight / 2), EffectsOverlay);
        if (surprise == CreditsSurprise.Message)
        {
            SecretMessage.Visibility = Visibility.Visible;
            messageBirth = clock.Elapsed.TotalSeconds;
            StartRendering();
        }
        else if (surprise != CreditsSurprise.None && particles.Count(particle => !particle.IsHeart) < MaximumParticles)
        {
            var icon = CreateSurprise(surprise);
            AddParticle(icon, origin, false, false, 3.4, Random.Shared.Next(-40, 41), -235, 150, 45, 10);
        }
        var height = Math.Max(40, origin.Y - 18);
        const double drag = 2.6;
        for (var index = 0; index < BurstSize && particles.Count(particle => !particle.IsHeart) < MaximumParticles; index++)
        {
            var petal = (index + 1) * PetalsPerBurst / BurstSize != index * PetalsPerBurst / BurstSize;
            FrameworkElement shape = petal ? CreatePetal() : new Rectangle
            {
                Fill = ConfettiBrushes[Random.Shared.Next(ConfettiBrushes.Length)],
                RadiusX = 1, RadiusY = 1, Width = Random.Shared.Next(5, 8), Height = Random.Shared.Next(12, 19)
            };
            shape.Tag = petal ? "Petal" : "Ribbon";
            var angle = Math.Clamp(-75 + index * 150.0 / (BurstSize - 1) + Random.Shared.NextDouble() * 2 - 1, -75, 75) * Math.PI / 180;
            var terminal = height / (petal ? 3.7 : 3.0);
            var speed = height * drag;
            // 按当前页面高度求初速度，让中央粒子的最高点接近页顶。
            for (var iteration = 0; iteration < 5; iteration++) speed = height * drag + terminal * Math.Log(1 + speed / terminal);
            var lift = Math.Abs(angle) < 0.06 ? 1 : 0.78 + Random.Shared.NextDouble() * 0.22;
            var vx = (EffectsOverlay.ActualWidth / 2 - 12) * drag * Math.Sin(angle) / Math.Sin(75 * Math.PI / 180);
            if (index is > 0 and < BurstSize - 1) vx *= 0.75 + Random.Shared.NextDouble() * 0.25;
            AddParticle(shape, origin, false, !petal, 4, vx, -Math.Cos(angle) * speed * lift,
                0, Random.Shared.Next(petal ? -35 : -240, petal ? 36 : 241),
                petal ? 12 : 5, Random.Shared.Next(360), drag, index * 0.0004,
                terminal * Math.Cos(angle) * lift, 1);
        }
    }

    private static FrameworkElement CreatePetal()
    {
        var petal = new Grid { Width = Random.Shared.Next(14, 21), Height = Random.Shared.Next(14, 21) };
        var drawing = new Canvas { Width = 20, Height = 24 };
        drawing.Children.Add(new Path
        {
            Data = Geometry.Parse("M 10,23 C 7,18 1,13 2,6 C 2,1 6,0 9,3 L 10,6 L 12,2 C 17,-1 20,3 18,9 C 17,15 12,19 10,23 Z"),
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0.1, 0), EndPoint = new Point(0.85, 1),
                GradientStops = { new GradientStop(Color.FromRgb(251, 232, 238), 0),
                    new GradientStop(Color.FromRgb(236, 186, 206), 0.55), new GradientStop(Color.FromRgb(210, 143, 171), 1) }
            }
        });
        drawing.Children.Add(new Path
        {
            Data = Geometry.Parse("M 10,21 C 9,16 12,11 11,8"),
            Stroke = Brush("#CB8EAB"), StrokeThickness = 0.6, Opacity = 0.45
        });
        petal.Children.Add(new Viewbox { Child = drawing });
        return petal;
    }

    private FrameworkElement CreateSurprise(CreditsSurprise surprise)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var color = surprise switch
        {
            CreditsSurprise.Clawd => "#D97757",
            CreditsSurprise.Star => "#F2C230",
            CreditsSurprise.ChatGpt => "#171717",
            _ => "#4D6BFE"
        };
        canvas.Children.Add(new Path { Data = (Geometry)FindResource($"Credits{surprise}Geometry"), Fill = Brush(color) });
        if (surprise == CreditsSurprise.Clawd)
            canvas.Children.Add(new Path { Data = (Geometry)FindResource("CreditsClawdEyesGeometry"), Fill = Brushes.Black });
        return new Viewbox { Child = canvas, Width = 26, Height = 26, Tag = surprise.ToString() };
    }

    private void AddParticle(FrameworkElement element, Point origin, bool heart, bool ribbon,
        double lifetime, double vx, double vy, double gravity, double spin, double drift,
        double initialAngle = 0, double drag = 0, double delay = 0,
        double terminalVelocity = 0, double fadeDuration = 0.7)
    {
        var scale = new ScaleTransform();
        var rotation = new RotateTransform();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { scale, rotation } };
        element.IsHitTestVisible = false;
        var particle = new Particle
        {
            Element = element, Scale = scale, Rotation = rotation, Origin = origin,
            Birth = clock.Elapsed.TotalSeconds + delay, Lifetime = lifetime, VelocityX = vx, VelocityY = vy,
            Gravity = gravity, Spin = spin, Drift = drift, IsHeart = heart, IsRibbon = ribbon,
            Phase = Random.Shared.NextDouble() * Math.Tau, InitialAngle = initialAngle, Drag = drag,
            TerminalVelocity = terminalVelocity, FadeDuration = fadeDuration
        };
        element.Opacity = delay > 0 ? 0 : 1;
        Canvas.SetLeft(element, origin.X - element.Width / 2);
        Canvas.SetTop(element, origin.Y - element.Height / 2);
        particles.Add(particle);
        EffectsOverlay.Children.Add(element);
        StartRendering();
    }

    private void StartRendering()
    {
        frameTimer.Start();
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        var now = clock.Elapsed.TotalSeconds;
        for (var index = particles.Count - 1; index >= 0; index--)
        {
            var particle = particles[index];
            var age = now - particle.Birth;
            if (age < 0) continue;
            var travel = particle.TerminalVelocity > 0 ? (1 - Math.Exp(-particle.Drag * age)) / particle.Drag
                : age / (1 + particle.Drag * age);
            var y = particle.Origin.Y + particle.VelocityY * travel
                + (particle.TerminalVelocity > 0 ? particle.TerminalVelocity * (age - travel) : particle.Gravity * age * age / 2);
            if (age >= particle.Lifetime || y > EffectsOverlay.ActualHeight + particle.Element.Height
                || particle.HeartFadeStart is { } fadeStart && now - fadeStart >= 0.2)
            {
                EffectsOverlay.Children.Remove(particle.Element);
                particles.RemoveAt(index);
                if (particle.NextHeartOrigin is { } nextOrigin) AddHeart(nextOrigin);
                continue;
            }
            var sway = Math.Sin(age * 3 + particle.Phase) * particle.Drift * Math.Min(1, age * 2);
            Canvas.SetLeft(particle.Element, particle.Origin.X + particle.VelocityX * travel + sway - particle.Element.Width / 2);
            Canvas.SetTop(particle.Element, y - particle.Element.Height / 2);
            particle.Rotation.Angle = particle.InitialAngle + particle.Spin * age + sway;
            if (particle.IsRibbon) particle.Scale.ScaleX = Math.Cos(age * 9 + particle.Phase);
            particle.Element.Opacity = Math.Min(1, (particle.Lifetime - age) / particle.FadeDuration);
            if (particle.HeartFadeStart is { } retiring)
                particle.Element.Opacity *= Math.Max(0, 1 - (now - retiring) / 0.2);
        }
        if (messageBirth is { } birth)
        {
            var age = now - birth;
            SecretMessage.Opacity = Math.Clamp(Math.Min(age / 0.5, (6 - age) / 1.5), 0, 1);
            if (age >= 6)
            {
                SecretMessage.Visibility = Visibility.Collapsed;
                messageBirth = null;
            }
        }
        if (particles.Count == 0 && messageBirth is null) StopRendering();
    }

    private void StopRendering()
    {
        frameTimer.Stop();
    }

    private void ResetCelebration()
    {
        StopRendering();
        particles.Clear();
        EffectsOverlay.Children.Clear();
        SecretMessage.Visibility = Visibility.Collapsed;
        SecretMessage.Opacity = 0;
        messageBirth = null;
        interaction.Reset();
    }

    private void Panel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsInitialized && !IsVisible) ResetCelebration();
    }

    private void Panel_Unloaded(object sender, RoutedEventArgs e) => ResetCelebration();
}
