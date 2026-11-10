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
    private static readonly Brush[] ConfettiBrushes = [Brush("#B59CC5"), Brush("#D8AE8C"), Brush("#DDB6C1"), Brush("#A8BDA7"), Brush("#D6BF7A")];
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
            Shape shape = petal
                ? new Path { Data = Geometry.Parse("M 0,5 C 1,-2 9,-2 9,3 C 9,8 3,10 0,5 Z"), Stretch = Stretch.Fill }
                : new Rectangle();
            shape.Fill = ConfettiBrushes[Random.Shared.Next(ConfettiBrushes.Length)];
            shape.Width = petal ? 9 : 5;
            shape.Height = petal ? 11 : 13;
            shape.Tag = petal ? "Petal" : "Ribbon";
            AddParticle(shape, origin, false, !petal, petal ? 3.8 : 2.5,
                Random.Shared.Next(-95, 96), -Random.Shared.Next(petal ? 150 : 180, petal ? 220 : 290),
                petal ? 115 : 230, petal ? 60 : 240, petal ? 18 : 3);
        }
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
        double lifetime, double vx, double vy, double gravity, double spin, double drift)
    {
        var scale = new ScaleTransform();
        var rotation = new RotateTransform();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { scale, rotation } };
        element.IsHitTestVisible = false;
        var particle = new Particle
        {
            Element = element, Scale = scale, Rotation = rotation, Origin = origin,
            Birth = clock.Elapsed.TotalSeconds, Lifetime = lifetime, VelocityX = vx, VelocityY = vy,
            Gravity = gravity, Spin = spin, Drift = drift, IsHeart = heart, IsRibbon = ribbon
        };
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
            var y = particle.Origin.Y + particle.VelocityY * age + particle.Gravity * age * age / 2;
            if (age >= particle.Lifetime || y > EffectsOverlay.ActualHeight + particle.Element.Height)
            {
                EffectsOverlay.Children.Remove(particle.Element);
                particles.RemoveAt(index);
                continue;
            }
            Canvas.SetLeft(particle.Element, particle.Origin.X + particle.VelocityX * age + Math.Sin(age * 5) * particle.Drift - particle.Element.Width / 2);
            Canvas.SetTop(particle.Element, y - particle.Element.Height / 2);
            particle.Rotation.Angle = particle.Spin * age;
            if (particle.IsRibbon) particle.Scale.ScaleX = Math.Cos(age * 11);
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
