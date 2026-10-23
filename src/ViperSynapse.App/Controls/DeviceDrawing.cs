using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ViperSynapse.App.ViewModels;

namespace ViperSynapse.App.Controls;

/// <summary>
/// The device picture in the middle of Customize, drawn from geometry (no product photos): the Tartarus V2
/// Pro from its manual's key grid, a mouse as a silhouette with its side grid. Keys light up green when
/// hovered or chosen; clicking one opens its binding, as clicking the picture does in Synapse.
/// </summary>
public sealed class DeviceDrawing : Control
{
    public static readonly StyledProperty<DeviceViewModel?> DeviceProperty =
        AvaloniaProperty.Register<DeviceDrawing, DeviceViewModel?>(nameof(Device));

    public static readonly StyledProperty<KeyRowViewModel?> SelectedProperty =
        AvaloniaProperty.Register<DeviceDrawing, KeyRowViewModel?>(nameof(Selected), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Paint mode (per-key lighting): each key filled with its LED colour, and a click paints instead of
    /// selecting. Null is the ordinary binding view.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<int, Color>?> PaintColorsProperty =
        AvaloniaProperty.Register<DeviceDrawing, IReadOnlyDictionary<int, Color>?>(nameof(PaintColors));

    public IReadOnlyDictionary<int, Color>? PaintColors
    {
        get => GetValue(PaintColorsProperty);
        set => SetValue(PaintColorsProperty, value);
    }

    /// <summary>A key clicked in paint mode.</summary>
    public event Action<KeyRowViewModel>? KeyPainted;

    public DeviceViewModel? Device
    {
        get => GetValue(DeviceProperty);
        set => SetValue(DeviceProperty, value);
    }

    public KeyRowViewModel? Selected
    {
        get => GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    static DeviceDrawing()
    {
        AffectsRender<DeviceDrawing>(DeviceProperty, SelectedProperty, PaintColorsProperty);
    }

    // Shapes in layout units, laid out once per device; Render scales them into the control.
    // A callout (Anchor set) is a label beside the picture with a line to its button, as Synapse labels a mouse.
    readonly record struct KeyShape(KeyRowViewModel Row, Rect Box, double Slant, bool Round, Point? Anchor = null, bool LabelOnLeft = false);

    List<KeyShape> _keys = [];
    List<(Geometry Shape, IBrush Fill, IPen? Pen)> _body = [];
    Rect _extent;
    KeyRowViewModel? _hover;

    static readonly IBrush KeyFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#2A2A2A"), 0), new GradientStop(Color.Parse("#181818"), 1) },
    };
    static readonly IBrush BodyFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#1B1B1B"), 0), new GradientStop(Color.Parse("#0B0B0B"), 1) },
    };
    static readonly IBrush RestFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#202020"), 0), new GradientStop(Color.Parse("#121212"), 1) },
    };
    static readonly IPen KeyPen = new Pen(new SolidColorBrush(Color.Parse("#3C3C3C")), 1);
    static readonly IPen BodyPen = new Pen(new SolidColorBrush(Color.Parse("#383838")), 1.5);
    static readonly IBrush Green = new SolidColorBrush(Color.Parse("#44D62C"));
    static readonly IBrush GreenGlow = new SolidColorBrush(Color.Parse("#44D62C"), 0.18);
    static readonly IPen GreenPen = new Pen(new SolidColorBrush(Color.Parse("#44D62C")), 2);
    static readonly IBrush Label = new SolidColorBrush(Color.Parse("#E6E6E6"));
    static readonly IBrush LabelDim = new SolidColorBrush(Color.Parse("#7A7A7A"));
    static readonly Typeface Face = new(FontFamily.Default);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DeviceProperty)
        {
            if (change.OldValue is DeviceViewModel old)
                foreach (var row in old.Rows) row.PropertyChanged -= RowChanged;
            if (change.NewValue is DeviceViewModel device)
                foreach (var row in device.Rows) row.PropertyChanged += RowChanged;
            Layout();
        }
    }

    void RowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    void Layout()
    {
        _keys = [];
        _body = [];
        var device = Device;
        if (device == null)
            return;
        if (device.Model?.ProductId == 0x02EB)
            LayoutTartarus(device);
        else if (device.Model?.Type == "mouse")
            LayoutMouse(device);
        else
            LayoutGrid(device);
        _extent = _body.Select(b => b.Shape.Bounds).Concat(_keys.Select(k => k.Box))
            .Aggregate(default(Rect), (a, b) => a == default ? b : a.Union(b)).Inflate(12);
        InvalidateVisual();
    }

    /// <summary>The manual's grid: six columns, the first wider; 23 and 27 are long slanted keys; the thumb
    /// cluster (Hypershift, stick) sits below the last column; the wrist rest below the grid.</summary>
    void LayoutTartarus(DeviceViewModel device)
    {
        const double u = 50, gap = 6, first = 64;
        double X(int col) => col == 0 ? 0 : first + gap + (col - 1) * (u + gap);
        double Y(int row) => row * (u + gap);
        // Numbers repeat across groups (the thumbstick rows are 1-5 too): only the keypad and its other keys are drawn here.
        var byNumber = device.Rows.Where(r => r.Key.Group is "main" or "other").ToDictionary(r => r.Key.Number);
        void Key(int number, int col, int row, double width = 0, double slant = 0)
        {
            if (byNumber.TryGetValue(number, out var r))
                _keys.Add(new KeyShape(r, new Rect(X(col), Y(row), width > 0 ? width : col == 0 ? first : u, u), slant, false));
        }
        int n = 1;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 6; col++)
                Key(n++, col, row);
        for (int col = 0; col < 4; col++)
            Key(19 + col, col, 3);
        Key(23, 4, 3, 2 * u + gap, 18);
        for (int col = 0; col < 3; col++)
            Key(24 + col, col, 4);
        Key(27, 3, 4, 2 * u + gap, 22);

        double gridRight = X(5) + u, gridBottom = Y(4) + u;
        var body = new StreamGeometry();
        using (var g = body.Open())
        {
            g.BeginFigure(new Point(-18, -18), true);
            g.LineTo(new Point(gridRight + 40, -18));
            g.LineTo(new Point(gridRight + 40, gridBottom - 10));
            g.LineTo(new Point(gridRight + 10, gridBottom + 150));
            g.LineTo(new Point(gridRight - 120, gridBottom + 170));
            g.LineTo(new Point(-18, gridBottom + 30));
            g.EndFigure(true);
        }
        _body.Add((body, BodyFill, BodyPen));
        // The LED strip down the right edge.
        _body.Add((new RectangleGeometry(new Rect(gridRight + 22, 20, 6, 3 * (u + gap)), 3, 3), new SolidColorBrush(Color.Parse("#44D62C"), 0.55), null));
        // The wrist rest.
        _body.Add((new RectangleGeometry(new Rect(10, gridBottom + 38, gridRight - 130, 210), 40, 40), RestFill, BodyPen));
        // The thumb cluster: Hypershift (key 28 in the list), keymap and profile buttons, the analog stick.
        double cx = gridRight - 40, cy = gridBottom + 70;
        _body.Add((new EllipseGeometry(new Rect(cx - 8, cy - 8, 88, 88)), new SolidColorBrush(Color.Parse("#0E0E0E")), BodyPen));
        _body.Add((new EllipseGeometry(new Rect(cx + 10, cy + 10, 52, 52)), KeyFill, KeyPen));
        _body.Add((new EllipseGeometry(new Rect(cx - 66, cy + 52, 18, 18)), KeyFill, KeyPen));
        _body.Add((new EllipseGeometry(new Rect(cx - 52, cy + 80, 18, 18)), KeyFill, KeyPen));
        if (byNumber.TryGetValue(28, out var hypershift))
            _keys.Add(new KeyShape(hypershift, new Rect(cx - 74, cy + 6, 34, 34), 0, true));
    }

    /// <summary>A top-down mouse with its main buttons, and the side grid beside it when it has one.</summary>
    void LayoutMouse(DeviceViewModel device)
    {
        if (!device.Rows.Any(r => r.Key.Group == "side"))
        {
            LayoutMouseCallouts(device);
            return;
        }
        var mouse = new StreamGeometry();
        using (var g = mouse.Open())
        {
            g.BeginFigure(new Point(60, 0), true);
            g.CubicBezierTo(new Point(130, 0), new Point(150, 60), new Point(150, 140));
            g.CubicBezierTo(new Point(150, 240), new Point(130, 300), new Point(75, 300));
            g.CubicBezierTo(new Point(20, 300), new Point(0, 240), new Point(0, 140));
            g.CubicBezierTo(new Point(0, 60), new Point(-10, 0), new Point(60, 0));
            g.EndFigure(true);
        }
        _body.Add((mouse, BodyFill, BodyPen));
        _body.Add((new LineGeometry(new Point(75, 4), new Point(75, 110)), Brushes.Transparent, KeyPen));
        _body.Add((new LineGeometry(new Point(4, 110), new Point(146, 110)), Brushes.Transparent, KeyPen));
        var rows = device.Rows.Where(r => r.Key.Group == "mouse").ToList();
        void At(string label, Rect box, bool round)
        {
            var r = rows.FirstOrDefault(x => x.Key.Label == label);
            if (r != null) _keys.Add(new KeyShape(r, box, 0, round));
        }
        At("Left Click", new Rect(14, 20, 52, 80), false);
        At("Right Click", new Rect(84, 20, 52, 80), false);
        At("Scroll Click", new Rect(66, 40, 18, 34), true);
        var side = device.Rows.Where(r => r.Key.Group == "side").ToList();
        for (int i = 0; i < side.Count; i++)
            _keys.Add(new KeyShape(side[i], new Rect(200 + i % 3 * 44, 40 + i / 3 * 44, 38, 38), 0, false));
        if (side.Count > 0)
            _body.Add((new RectangleGeometry(new Rect(188, 28, 3 * 44 + 10, (side.Count + 2) / 3 * 44 + 10), 14, 14), BodyFill, BodyPen));
    }

    /// <summary>Where each button sits on the top-down mouse of <see cref="LayoutMouseCallouts"/>, by its catalogue label.</summary>
    /// The side and the place in its column follow Synapse 4's Basilisk V4 Pro picture.
    static readonly Dictionary<string, (Point At, bool Left, int Order)> MouseAnchors = new()
    {
        ["Left Click"] = (new Point(38, 60), true, 0),
        ["Scroll Click"] = (new Point(75, 58), true, 1),
        ["Repeat Scroll Left"] = (new Point(68, 46), true, 2),
        ["Cycle Up Profile"] = (new Point(75, 230), true, 3),
        ["Hypershift Clutch"] = (new Point(3, 128), true, 4),
        ["AI Prompt Master"] = (new Point(10, 34), true, 5),
        ["Mouse Button 5"] = (new Point(3, 162), true, 6),
        ["Right Click"] = (new Point(112, 60), false, 0),
        ["Scroll Up"] = (new Point(75, 34), false, 1),
        ["Repeat Scroll Right"] = (new Point(82, 46), false, 2),
        ["Scroll Down"] = (new Point(75, 84), false, 3),
        ["Scroll Mode Toggle"] = (new Point(75, 100), false, 4),
        ["Cycle Up Sensitivity Stages"] = (new Point(75, 122), false, 5),
        ["Mouse Button 4"] = (new Point(3, 200), false, 6),
    };

    /// <summary>A mouse with no side grid, as Synapse 4 shows one: the top-down mouse in the middle, each button's
    /// function in a column beside it with a line to the button.</summary>
    void LayoutMouseCallouts(DeviceViewModel device)
    {
        var mouse = new StreamGeometry();
        using (var g = mouse.Open())
        {
            g.BeginFigure(new Point(60, 0), true);
            g.CubicBezierTo(new Point(130, 0), new Point(150, 60), new Point(150, 140));
            g.CubicBezierTo(new Point(150, 240), new Point(130, 300), new Point(75, 300));
            g.CubicBezierTo(new Point(20, 300), new Point(0, 240), new Point(0, 140));
            g.CubicBezierTo(new Point(0, 60), new Point(-10, 0), new Point(60, 0));
            g.EndFigure(true);
        }
        _body.Add((mouse, BodyFill, BodyPen));
        _body.Add((new LineGeometry(new Point(75, 4), new Point(75, 110)), Brushes.Transparent, KeyPen));
        _body.Add((new LineGeometry(new Point(4, 110), new Point(146, 110)), Brushes.Transparent, KeyPen));
        // The wheel, and the thumb buttons on the left side (the clutch in front of the two side buttons).
        _body.Add((new RectangleGeometry(new Rect(67, 30, 16, 58), 8, 8), KeyFill, KeyPen));
        if (device.Rows.Any(r => r.Key.Label == "Hypershift Clutch"))
            _body.Add((new RectangleGeometry(new Rect(-4, 116, 8, 24), 4, 4), KeyFill, KeyPen));
        _body.Add((new RectangleGeometry(new Rect(-4, 144, 8, 38), 4, 4), KeyFill, KeyPen));
        _body.Add((new RectangleGeometry(new Rect(-4, 184, 8, 34), 4, 4), KeyFill, KeyPen));

        const double rowHeight = 34, labelWidth = 200, gap = 70;
        var rows = device.Rows.Where(r => r.Key.Group is "mouse" or "other").ToList();
        var left = rows.Where(r => MouseAnchors.TryGetValue(r.Key.Label, out var a) ? a.Left : rows.IndexOf(r) % 2 == 0).ToList();
        var right = rows.Except(left).ToList();
        void Column(List<KeyRowViewModel> column, bool onLeft)
        {
            double top = 150 - column.Count * rowHeight / 2;
            for (int i = 0; i < column.Count; i++)
            {
                var row = column[i];
                var anchor = MouseAnchors.TryGetValue(row.Key.Label, out var a) ? a.At : new Point(75, 150);
                double x = onLeft ? -gap - labelWidth : 150 + gap;
                _keys.Add(new KeyShape(row, new Rect(x, top + i * rowHeight, labelWidth, rowHeight - 6), 0, false, anchor, onLeft));
            }
        }
        // Each column in Synapse's order; buttons it does not place go last, top to bottom.
        double Place(KeyRowViewModel r) => MouseAnchors.TryGetValue(r.Key.Label, out var a) ? a.Order : 100 + rows.IndexOf(r);
        Column(left.OrderBy(Place).ToList(), true);
        Column(right.OrderBy(Place).ToList(), false);
    }

    void LayoutGrid(DeviceViewModel device)
    {
        int i = 0;
        foreach (var row in device.Rows)
        {
            _keys.Add(new KeyShape(row, new Rect(i % 8 * 56, i / 8 * 56, 50, 50), 0, false));
            i++;
        }
    }

    Matrix Transform()
    {
        if (_extent.Width <= 0 || _extent.Height <= 0 || Bounds.Width <= 0)
            return Matrix.Identity;
        double scale = Math.Min(Bounds.Width / _extent.Width, Bounds.Height / _extent.Height);
        scale = Math.Min(scale, 1.6);
        double ox = (Bounds.Width - _extent.Width * scale) / 2 - _extent.X * scale;
        double oy = (Bounds.Height - _extent.Height * scale) / 2 - _extent.Y * scale;
        return Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(ox, oy);
    }

    public override void Render(DrawingContext context)
    {
        // A dot grid behind the device, as on Synapse's canvas.
        var dot = new SolidColorBrush(Color.Parse("#333333"));
        for (double y = 12; y < Bounds.Height; y += 24)
            for (double x = 12; x < Bounds.Width; x += 24)
                context.FillRectangle(dot, new Rect(x, y, 1.5, 1.5));

        using (context.PushTransform(Transform()))
        {
            foreach (var (shape, fill, pen) in _body)
                context.DrawGeometry(fill, pen, shape);
            foreach (var key in _keys)
                DrawKey(context, key);
        }
    }

    void DrawKey(DrawingContext context, KeyShape key)
    {
        bool chosen = key.Row == Selected, hovered = key.Row == _hover;
        if (key.Anchor is { } anchor)
        {
            DrawCallout(context, key, anchor, chosen || hovered);
            return;
        }
        var shape = KeyGeometry(key);
        if (chosen || hovered)
            context.DrawGeometry(GreenGlow, null, KeyGeometry(key with { Box = key.Box.Inflate(4) }));
        context.DrawGeometry(KeyFill, chosen ? GreenPen : hovered ? new Pen(Green, 1.2) : KeyPen, shape);
        if (PaintColors is { } paint && paint.TryGetValue(key.Row.Key.Number, out var led))
            context.DrawGeometry(new SolidColorBrush(led, 0.85), null, shape);
        if (key.Round)
            return;
        var number = Text(key.Row.Number, 10, key.Row.Changed ? Green : LabelDim);
        context.DrawText(number, new Point(key.Box.X + 6, key.Box.Y + 4));
        // Long labels (Right Click, Caps Lock) step down a size before they are cut short.
        var value = Text(Shorten(key.Row.Value), key.Row.Value.Length > 7 ? 9.5 : 11, key.Row.Changed ? Green : Label);
        if (value.Width > key.Box.Width - key.Slant - 6)
            value = Text(Shorten(key.Row.Value), 8.5, key.Row.Changed ? Green : Label);
        context.DrawText(value, new Point(key.Box.X + (key.Box.Width - key.Slant - value.Width) / 2, key.Box.Bottom - value.Height - 7));
    }

    /// <summary>The label, right-aligned on the left column and left-aligned on the right, and its leader line: across
    /// to the edge of the picture's column, then to the button, ending in a dot.</summary>
    void DrawCallout(DrawingContext context, KeyShape key, Point anchor, bool lit)
    {
        var brush = lit || key.Row.Changed ? Green : Label;
        // A firmware-only button has no binding to show: its name says what it does.
        string label = key.Row.Bindable ? key.Row.Value : key.Row.Key.Label;
        var brushFinal = key.Row.Bindable ? brush : LabelDim;
        var text = Text(label, 12, brushFinal);
        if (text.Width > key.Box.Width - 14)
            text = Text(label.Length > 24 ? label[..23] + "…" : label, 12, brushFinal);
        double y = key.Box.Center.Y;
        double textX = key.LabelOnLeft ? key.Box.Right - 14 - text.Width : key.Box.X + 14;
        if (lit)
            context.DrawGeometry(GreenGlow, null, new RectangleGeometry(new Rect(textX - 6, key.Box.Y, text.Width + 12, key.Box.Height), 4, 4));
        context.DrawText(text, new Point(textX, y - text.Height / 2));
        var pen = lit ? new Pen(Green, 1.2) : new Pen(LabelDim, 0.8);
        var start = new Point(key.LabelOnLeft ? key.Box.Right - 6 : key.Box.X + 6, y);
        var elbow = new Point(key.LabelOnLeft ? key.Box.Right + 30 : key.Box.X - 30, y);
        context.DrawLine(pen, start, elbow);
        context.DrawLine(pen, elbow, anchor);
        context.DrawEllipse(lit ? Green : LabelDim, null, start, 2, 2);
        context.DrawEllipse(lit ? Green : Label, null, anchor, 2.5, 2.5);
    }

    static Geometry KeyGeometry(KeyShape key)
    {
        if (key.Round)
            return new EllipseGeometry(key.Box);
        if (key.Slant <= 0)
            return new RectangleGeometry(key.Box, 6, 6);
        var b = key.Box;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(b.X + 6, b.Y), true);
            c.LineTo(new Point(b.Right - 6, b.Y));
            c.QuadraticBezierTo(new Point(b.Right, b.Y), new Point(b.Right, b.Y + 6));
            c.LineTo(new Point(b.Right - key.Slant, b.Bottom - 6));
            c.QuadraticBezierTo(new Point(b.Right - key.Slant - 2, b.Bottom), new Point(b.Right - key.Slant - 8, b.Bottom));
            c.LineTo(new Point(b.X + 6, b.Bottom));
            c.QuadraticBezierTo(new Point(b.X, b.Bottom), new Point(b.X, b.Bottom - 6));
            c.LineTo(new Point(b.X, b.Y + 6));
            c.QuadraticBezierTo(new Point(b.X, b.Y), new Point(b.X + 6, b.Y));
            c.EndFigure(true);
        }
        return g;
    }

    static string Shorten(string value) => value.Length > 12 ? value[..11] + "…" : value;

    static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush);

    KeyRowViewModel? HitTest(Point p)
    {
        if (!Transform().TryInvert(out var inverse))
            return null;
        var local = p.Transform(inverse);
        return _keys.LastOrDefault(k => k.Box.Contains(local) && k.Row.Bindable).Row;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitTest(e.GetPosition(this));
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (HitTest(e.GetPosition(this)) is { } hit)
        {
            if (PaintColors != null)
                KeyPainted?.Invoke(hit);
            else
                Selected = hit;
            e.Handled = true;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }
}
