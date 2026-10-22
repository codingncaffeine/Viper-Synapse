using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ViperSynapse.App.Controls;

/// <summary>Synapse's live thumbstick circle: the stick's travel as a ring, its position as a green dot, and in 4-way
/// and 8-way mode faint guides for the directions.</summary>
public sealed class StickView : Control
{
    public static readonly StyledProperty<double> XProperty = AvaloniaProperty.Register<StickView, double>(nameof(X));
    public static readonly StyledProperty<double> YProperty = AvaloniaProperty.Register<StickView, double>(nameof(Y));
    public static readonly StyledProperty<int> DirectionsProperty = AvaloniaProperty.Register<StickView, int>(nameof(Directions));

    public double X { get => GetValue(XProperty); set => SetValue(XProperty, value); }
    public double Y { get => GetValue(YProperty); set => SetValue(YProperty, value); }

    /// <summary>0 for analog, 4 or 8 for the direction modes.</summary>
    public int Directions { get => GetValue(DirectionsProperty); set => SetValue(DirectionsProperty, value); }

    static StickView() => AffectsRender<StickView>(XProperty, YProperty, DirectionsProperty);

    static readonly IPen Ring = new Pen(new SolidColorBrush(Color.Parse("#9A9A9A")), 1.5);
    static readonly IPen Guide = new Pen(new SolidColorBrush(Color.Parse("#3A3A3A")), 1);
    static readonly IBrush Dot = new SolidColorBrush(Color.Parse("#44D62C"));
    static readonly IBrush Glow = new SolidColorBrush(Color.Parse("#44D62C"), 0.25);

    public override void Render(DrawingContext context)
    {
        double r = Math.Min(Bounds.Width, Bounds.Height) / 2 - 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, Ring, c, r, r);
        if (Directions > 0)
        {
            for (int i = 0; i < Directions; i++)
            {
                double a = Math.PI * 2 * i / Directions + (Directions == 4 ? Math.PI / 4 : Math.PI / 8);
                context.DrawLine(Guide, c, new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r));
            }
            context.DrawEllipse(null, Guide, c, r * 0.5, r * 0.5);   // the press threshold
        }
        var p = new Point(c.X + Math.Clamp(X, -1, 1) * r, c.Y + Math.Clamp(Y, -1, 1) * r);
        context.DrawEllipse(Glow, null, p, 9, 9);
        context.DrawEllipse(Dot, null, p, 4.5, 4.5);
    }
}
