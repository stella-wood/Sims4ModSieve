using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Sims4ModSieve.Desktop;

public partial class CreditsPanel : UserControl
{
    internal const int MaximumHearts = 4;
    private const int BurstSize = 70;
    private const int PetalsPerBurst = 15;
    private static readonly Brush[] PaperBrushes = [Brush("#FF6B8A"), Brush("#FF9F43"), Brush("#FFD93D"),
        Brush("#4ADE80"), Brush("#38BDF8"), Brush("#A78BFA"), Brush("#F472B6")];
    private static readonly Drawing[] PaperDrawings = PaperBrushes.Select(brush => FreezeDrawing(
        new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(-0.5, -0.5, 1, 1), 0.13, 0.07)))).ToArray();
    private static readonly Drawing PetalDrawing = CreatePetalDrawing();
    private readonly CreditsInteractionState interaction = new();
    private readonly List<Particle> particles = [];
    private readonly Dictionary<CreditsSurprise, Drawing> surpriseDrawings = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Drawing heartDrawing;
    private bool rendering;
    private bool drawingDirty;
    private double? messageBirth;

    public CreditsPanel()
    {
        InitializeComponent();
        heartDrawing = NormalizeDrawing(new GeometryDrawing(Brush("#F16B9A"), null,
            (Geometry)FindResource("CreditsHeartGeometry")), 24, 24);
        EffectsOverlay.Particles = particles;
    }

    internal sealed class Particle
    {
        public required Drawing Drawing { get; init; }
        public required string Tag { get; init; }
        public required Size Size { get; init; }
        public MatrixTransform Transform { get; } = new();
        public DrawingGroup VisualDrawing { get; } = new();
        public Point Origin { get; init; }
        public Point Position { get; set; }
        public double Birth { get; init; }
        public double Lifetime { get; init; }
        public double VelocityX { get; set; }
        public double VelocityY { get; set; }
        public double Gravity { get; init; }
        public double Spin { get; init; }
        public double Phase { get; init; }
        public double Flutter { get; init; }
        public double TerminalVelocity { get; init; }
        public double Drag { get; init; }
        public double InitialAngle { get; init; }
        public double Opacity { get; set; } = 1;
        public double ScaleX { get; set; } = 1;
        public double Age { get; set; }
        public double LastUpdate { get; set; }
        public double? HeartFadeStart { get; set; }
        public Point? NextHeartOrigin { get; set; }
        public double ReplacementRequestedAt { get; set; }
        public bool IsHeart => Tag == "Heart";
        public bool IsRibbon => Tag == "Ribbon";
        public bool IsPaper => Tag is "Ribbon" or "Petal";
    }

    private static Brush Brush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private static Drawing FreezeDrawing(Drawing drawing) { drawing.Freeze(); return drawing; }

    private static Drawing NormalizeDrawing(Drawing drawing, double width, double height) => FreezeDrawing(
        new DrawingGroup { Transform = new MatrixTransform(1 / width, 0, 0, 1 / height, -0.5, -0.5), Children = { drawing } });

    private void Surface_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source && (ReferenceEquals(source, ConfettiButton) || ConfettiButton.IsAncestorOf(source))) return;
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

    private void AddHeart(Point origin) => AddParticle(heartDrawing, "Heart", new Size(Random.Shared.Next(10, 16), 15),
        origin, 1.5, 36 + Random.Shared.NextDouble() * 24, -70 - Random.Shared.NextDouble() * 25, 0, -12);

    private void ConfettiButton_Click(object sender, RoutedEventArgs e)
    {
        var now = clock.Elapsed.TotalSeconds;
        if (!interaction.TryClickConfetti(now, out var surprise)) return;
        ConfettiButton.IsEnabled = interaction.CanClick(now);
        var origin = ConfettiButton.TranslatePoint(new Point(ConfettiButton.ActualWidth / 2, ConfettiButton.ActualHeight / 2), EffectsOverlay);
        var height = Math.Max(40, origin.Y - 18);
        if (surprise == CreditsSurprise.Message)
        {
            SecretMessage.Visibility = Visibility.Visible;
            messageBirth = now;
        }
        else if (surprise != CreditsSurprise.None)
        {
            var lane = surprise switch { CreditsSurprise.Clawd => -0.75, CreditsSurprise.Star => -0.25, CreditsSurprise.ChatGpt => 0.25, _ => 0.75 };
            var terminal = height / 3.4;
            var speed = height * 2.6;
            for (var iteration = 0; iteration < 5; iteration++) speed = height * 2.6 + terminal * Math.Log(1 + speed / terminal);
            AddParticle(GetSurpriseDrawing(surprise), surprise.ToString(), new Size(26, 26), origin, 3.4,
                lane * EffectsOverlay.ActualWidth * 2.6 * 0.38, -speed * 0.8, 0, 45, terminal * 0.8, 2.6);
        }
        for (var index = 0; index < BurstSize; index++)
        {
            var petal = (index + 1) * PetalsPerBurst / BurstSize != index * PetalsPerBurst / BurstSize;
            var spread = index % 10 == 0 ? -0.98 : index % 10 == 9 ? 0.98 : -1 + (index % 10 + Random.Shared.NextDouble()) / 5;
            var rise = index == BurstSize / 2 ? 0.98 : (0.52 + Random.Shared.NextDouble() * 0.46) * (1 - Math.Abs(spread) * 0.15);
            var gravity = 185 * height / 300 * (petal ? 0.65 : 1);
            var upward = Math.Sqrt(2 * gravity * height * rise);
            var peakTime = upward / gravity;
            var vx = (EffectsOverlay.ActualWidth / 2 - 12) * spread * 0.65 / (1 - Math.Exp(-0.65 * peakTime));
            AddParticle(petal ? PetalDrawing : PaperDrawings[Random.Shared.Next(PaperDrawings.Length)], petal ? "Petal" : "Ribbon",
                petal ? new Size(Random.Shared.Next(14, 21), Random.Shared.Next(14, 21)) : new Size(Random.Shared.Next(5, 8), Random.Shared.Next(12, 19)),
                origin, 5.4, vx, -upward, gravity, (Random.Shared.NextDouble() - 0.5) * 8 * 180 / Math.PI);
        }
        StartRendering();
    }

    private static Drawing CreatePetalDrawing()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new LinearGradientBrush
        {
            StartPoint = new Point(0.1, 0), EndPoint = new Point(0.85, 1),
            GradientStops = { new GradientStop(Color.FromRgb(251, 232, 238), 0),
                new GradientStop(Color.FromRgb(236, 186, 206), 0.55), new GradientStop(Color.FromRgb(210, 143, 171), 1) }
        }, null, Geometry.Parse("M 10,23 C 7,18 1,13 2,6 C 2,1 6,0 9,3 L 10,6 L 12,2 C 17,-1 20,3 18,9 C 17,15 12,19 10,23 Z")));
        drawing.Children.Add(new GeometryDrawing(null, new Pen(Brush("#73CB8EAB"), 0.6), Geometry.Parse("M 10,21 C 9,16 12,11 11,8")));
        return NormalizeDrawing(drawing, 20, 24);
    }

    private Drawing GetSurpriseDrawing(CreditsSurprise surprise)
    {
        if (surpriseDrawings.TryGetValue(surprise, out var drawing)) return drawing;
        var color = surprise switch { CreditsSurprise.Clawd => "#D97757", CreditsSurprise.Star => "#F2C230", CreditsSurprise.ChatGpt => "#171717", _ => "#4D6BFE" };
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brush(color), null, (Geometry)FindResource($"Credits{surprise}Geometry")));
        if (surprise == CreditsSurprise.Clawd)
            group.Children.Add(new GeometryDrawing(Brushes.Black, null, (Geometry)FindResource("CreditsClawdEyesGeometry")));
        drawing = NormalizeDrawing(group, 24, 24);
        surpriseDrawings.Add(surprise, drawing);
        return drawing;
    }

    private void AddParticle(Drawing drawing, string tag, Size size, Point origin, double lifetime,
        double vx, double vy, double gravity, double spin, double terminalVelocity = 0, double drag = 0)
    {
        var now = clock.Elapsed.TotalSeconds;
        var particle = new Particle
        {
            Drawing = drawing, Tag = tag, Size = size, Origin = origin, Position = origin, Birth = now, LastUpdate = now,
            Lifetime = lifetime, VelocityX = vx, VelocityY = vy, Gravity = gravity, Spin = spin,
            Phase = Random.Shared.NextDouble() * Math.Tau, Flutter = 3 + Random.Shared.NextDouble() * 5,
            InitialAngle = tag is "Heart" or "Clawd" or "Star" or "ChatGpt" or "DeepSeek" ? 0 : Random.Shared.Next(360),
            TerminalVelocity = terminalVelocity, Drag = drag
        };
        UpdateTransform(particle, particle.InitialAngle);
        particle.VisualDrawing.Children.Add(drawing);
        particle.VisualDrawing.Transform = particle.Transform;
        particles.Add(particle);
        drawingDirty = true;
        StartRendering();
    }

    private static void UpdateTransform(Particle particle, double angle)
    {
        var matrix = Matrix.Identity;
        matrix.Scale(particle.Size.Width * particle.ScaleX, particle.Size.Height);
        matrix.Rotate(angle);
        matrix.Translate(particle.Position.X, particle.Position.Y);
        particle.Transform.Matrix = matrix;
    }

    private void StartRendering()
    {
        if (rendering) return;
        rendering = true;
        CompositionTarget.Rendering += RenderFrame;
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        var now = clock.Elapsed.TotalSeconds;
        ConfettiButton.IsEnabled = interaction.CanClick(now);
        for (var index = particles.Count - 1; index >= 0; index--)
        {
            var particle = particles[index];
            var age = now - particle.Birth;
            if (particle.IsPaper)
            {
                // 沿用参考页的时间步上限，避免切回窗口时粒子突然跳落。
                var dt = Math.Min(now - particle.LastUpdate, 0.034);
                particle.LastUpdate = now;
                particle.Age += dt;
                age = particle.Age;
                particle.VelocityX *= Math.Exp(-0.65 * dt);
                particle.VelocityY += particle.Gravity * dt;
                var flip = particle.Phase + particle.Flutter * age;
                particle.Position = new Point(particle.Position.X + (particle.VelocityX + Math.Sin(flip) * 28) * dt,
                    particle.Position.Y + particle.VelocityY * dt);
                particle.ScaleX = Math.Cos(flip);
            }
            else
            {
                var travel = particle.TerminalVelocity > 0 ? (1 - Math.Exp(-particle.Drag * age)) / particle.Drag : age;
                var sway = particle.IsHeart ? 0 : Math.Sin(age * 3 + particle.Phase) * 10 * Math.Min(1, age * 2);
                particle.Position = new Point(particle.Origin.X + particle.VelocityX * travel + sway,
                    particle.Origin.Y + particle.VelocityY * travel + particle.TerminalVelocity * (age - travel));
            }
            if (age >= particle.Lifetime || particle.Position.Y > EffectsOverlay.ActualHeight + particle.Size.Height
                || particle.HeartFadeStart is { } fadeStart && now - fadeStart >= 0.2)
            {
                particles.RemoveAt(index);
                drawingDirty = true;
                if (particle.NextHeartOrigin is { } nextOrigin) AddHeart(nextOrigin);
                continue;
            }
            particle.Opacity = Math.Min(1, (particle.Lifetime - age) / (particle.IsHeart ? 1.5 : particle.IsPaper ? 1 : 0.7));
            if (particle.HeartFadeStart is { } retiring) particle.Opacity *= Math.Max(0, 1 - (now - retiring) / 0.2);
            particle.VisualDrawing.Opacity = particle.Opacity;
            UpdateTransform(particle, particle.InitialAngle + particle.Spin * age);
        }
        if (messageBirth is { } birth)
        {
            var age = now - birth;
            SecretMessage.Opacity = Math.Clamp(Math.Min(age / 0.5, (6 - age) / 1.5), 0, 1);
            if (age >= 6) { SecretMessage.Visibility = Visibility.Collapsed; messageBirth = null; }
        }
        if (drawingDirty) { EffectsOverlay.DrawFrame(); drawingDirty = false; }
        if (particles.Count == 0 && messageBirth is null && interaction.CanClick(now)) StopRendering();
    }

    private void StopRendering() { CompositionTarget.Rendering -= RenderFrame; rendering = false; }

    private void ResetCelebration()
    {
        StopRendering();
        particles.Clear();
        EffectsOverlay.DrawFrame();
        drawingDirty = false;
        SecretMessage.Visibility = Visibility.Collapsed;
        SecretMessage.Opacity = 0;
        messageBirth = null;
        interaction.Reset();
        ConfettiButton.IsEnabled = true;
    }

    private void Panel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsInitialized && !IsVisible) ResetCelebration();
    }

    private void Panel_Unloaded(object sender, RoutedEventArgs e) => ResetCelebration();
}

public sealed class CreditsEffectsLayer : FrameworkElement
{
    private readonly DrawingVisual visual = new();

    public CreditsEffectsLayer() => AddVisualChild(visual);

    internal IReadOnlyList<CreditsPanel.Particle> Particles { get; set; } = [];
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => visual;

    internal void DrawFrame()
    {
        using var drawingContext = visual.RenderOpen();
        foreach (var particle in Particles)
            drawingContext.DrawDrawing(particle.VisualDrawing);
    }
}
