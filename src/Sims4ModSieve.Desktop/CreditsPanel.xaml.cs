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
    internal const int MaximumHearts = 30;
    internal const int MaximumParticles = 80;
    private const double PetalRatio = 0.25;
    private static readonly Brush[] ConfettiBrushes = [Brush("#AA8DBC"), Brush("#C9A563"), Brush("#D493AD"), Brush("#98B7A3")];
    private static readonly Geometry[] RibbonCurves =
    [
        Geometry.Parse("M 4,1 C 12,5 -2,9 4,14 C 11,19 -2,23 5,28"),
        Geometry.Parse("M 3,1 C -3,6 10,8 5,14 C -1,20 10,23 4,28"),
        Geometry.Parse("M 2,1 C 11,6 -3,13 5,18 C 10,22 2,25 4,28")
    ];
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
        var origin = e.GetPosition(EffectsOverlay);
        var count = interaction.ClickHeart(DateTimeOffset.UtcNow);
        for (var index = 0; index < count && particles.Count(particle => particle.IsHeart) < MaximumHearts; index++)
        {
            var heart = new Path
            {
                Data = (Geometry)FindResource("CreditsHeartGeometry"), Fill = Brush("#CA92A6"),
                Stretch = Stretch.Uniform, Width = Random.Shared.Next(10, 16), Height = 15,
                Tag = "Heart"
            };
            AddParticle(heart, origin, true, false, 1.5 + Random.Shared.NextDouble() * 0.4,
                28 + Random.Shared.NextDouble() * 32, -48 - Random.Shared.NextDouble() * 30, 0, -12, 0);
        }
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
        for (var index = 0; index < 9 && particles.Count(particle => !particle.IsHeart) < MaximumParticles; index++)
        {
            var petal = Random.Shared.NextDouble() < PetalRatio;
            FrameworkElement shape = petal ? CreatePetal() : new Path
            {
                Data = RibbonCurves[Random.Shared.Next(RibbonCurves.Length)],
                Stroke = ConfettiBrushes[Random.Shared.Next(ConfettiBrushes.Length)],
                StrokeThickness = 1.6 + Random.Shared.NextDouble() * 0.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.Fill, Width = Random.Shared.Next(7, 11), Height = Random.Shared.Next(20, 30)
            };
            shape.Tag = petal ? "Petal" : "Ribbon";
            var angle = (-62 + index * 15.5 + Random.Shared.Next(-5, 6)) * Math.PI / 180;
            var speed = petal ? Random.Shared.Next(210, 270) : Random.Shared.Next(280, 360);
            AddParticle(shape, origin, false, !petal, petal ? 3.8 : 3.2,
                Math.Sin(angle) * speed, -Math.Cos(angle) * speed,
                petal ? 100 : 175, Random.Shared.Next(petal ? -35 : -140, petal ? 36 : 141),
                petal ? 18 : 7, Random.Shared.Next(360), petal ? 0.55 : 0.35, index * 0.009);
        }
    }

    private static FrameworkElement CreatePetal()
    {
        var petal = new Grid { Width = Random.Shared.Next(9, 14), Height = Random.Shared.Next(13, 19) };
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
        double initialAngle = 0, double drag = 0, double delay = 0)
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
            Phase = Random.Shared.NextDouble() * Math.Tau, InitialAngle = initialAngle, Drag = drag
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
            var travel = age / (1 + particle.Drag * age);
            var y = particle.Origin.Y + particle.VelocityY * travel + particle.Gravity * age * age / 2;
            if (age >= particle.Lifetime || y > EffectsOverlay.ActualHeight + particle.Element.Height)
            {
                EffectsOverlay.Children.Remove(particle.Element);
                particles.RemoveAt(index);
                continue;
            }
            var sway = Math.Sin(age * 3 + particle.Phase) * particle.Drift * Math.Min(1, age * 2);
            Canvas.SetLeft(particle.Element, particle.Origin.X + particle.VelocityX * travel + sway - particle.Element.Width / 2);
            Canvas.SetTop(particle.Element, y - particle.Element.Height / 2);
            particle.Rotation.Angle = particle.InitialAngle + particle.Spin * age + sway;
            if (particle.IsRibbon) particle.Scale.ScaleX = 0.7 + Math.Cos(age * 7 + particle.Phase) * 0.3;
            particle.Element.Opacity = Math.Min(1, (particle.Lifetime - age) / 0.7);
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
