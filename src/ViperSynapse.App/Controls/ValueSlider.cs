using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace ViperSynapse.App.Controls;

/// <summary>
/// Synapse 4's slider: a green track, the value in a small green bubble riding above the thumb, and the ends of the
/// range written under the track (0 / 100, LOW / HIGH). Used for brightness, idle times, battery levels and the
/// tracking distance.
/// </summary>
public sealed class ValueSlider : Grid
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ValueSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<ValueSlider, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<ValueSlider, double>(nameof(Maximum), 100);
    public static readonly StyledProperty<double> StepProperty = AvaloniaProperty.Register<ValueSlider, double>(nameof(Step), 1);
    public static readonly StyledProperty<string?> MinLabelProperty = AvaloniaProperty.Register<ValueSlider, string?>(nameof(MinLabel));
    public static readonly StyledProperty<string?> MaxLabelProperty = AvaloniaProperty.Register<ValueSlider, string?>(nameof(MaxLabel));
    public static readonly StyledProperty<string?> MidLabelProperty = AvaloniaProperty.Register<ValueSlider, string?>(nameof(MidLabel));
    public static readonly StyledProperty<bool> ShowBubbleProperty = AvaloniaProperty.Register<ValueSlider, bool>(nameof(ShowBubble), true);
    public static readonly StyledProperty<bool> ShowLabelsProperty = AvaloniaProperty.Register<ValueSlider, bool>(nameof(ShowLabels), true);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string? MinLabel { get => GetValue(MinLabelProperty); set => SetValue(MinLabelProperty, value); }
    public string? MaxLabel { get => GetValue(MaxLabelProperty); set => SetValue(MaxLabelProperty, value); }
    public string? MidLabel { get => GetValue(MidLabelProperty); set => SetValue(MidLabelProperty, value); }
    public bool ShowBubble { get => GetValue(ShowBubbleProperty); set => SetValue(ShowBubbleProperty, value); }
    public bool ShowLabels { get => GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

    readonly Slider _slider = new() { IsSnapToTickEnabled = true, Margin = new Thickness(0) };
    readonly Border _bubble = new()
    {
        Background = new SolidColorBrush(Color.Parse("#44D62C")),
        CornerRadius = new CornerRadius(2),
        Padding = new Thickness(6, 1),
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    readonly TextBlock _bubbleText = new() { Foreground = Brushes.Black, FontSize = 10 };
    readonly TextBlock _min = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")) };
    readonly TextBlock _mid = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")), HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBlock _max = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")), HorizontalAlignment = HorizontalAlignment.Right };
    bool _syncing;

    public ValueSlider()
    {
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
        _bubble.Child = _bubbleText;
        Children.Add(_bubble);
        SetRow(_slider, 1);
        Children.Add(_slider);
        var labels = new Grid();
        labels.Children.Add(_min);
        labels.Children.Add(_mid);
        labels.Children.Add(_max);
        SetRow(labels, 2);
        Children.Add(labels);
        // The bubble can only be placed once the track has a width; place it again whenever that changes.
        _slider.SizeChanged += (_, _) => PlaceBubble();
        _slider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_syncing)
                Value = _slider.Value;
            if (e.Property == RangeBase.ValueProperty)
                PlaceBubble();
        };
        Sync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MinimumProperty || change.Property == MaximumProperty
            || change.Property == StepProperty || change.Property == MinLabelProperty || change.Property == MaxLabelProperty
            || change.Property == MidLabelProperty || change.Property == ShowBubbleProperty || change.Property == ShowLabelsProperty)
            Sync();
    }

    void Sync()
    {
        _syncing = true;
        _slider.Minimum = Minimum;
        _slider.Maximum = Maximum;
        _slider.TickFrequency = Step;
        _slider.SmallChange = Step;
        _slider.Value = Value;
        _syncing = false;
        _min.Text = MinLabel ?? Minimum.ToString("0", CultureInfo.InvariantCulture);
        _max.Text = MaxLabel ?? Maximum.ToString("0", CultureInfo.InvariantCulture);
        _mid.Text = MidLabel ?? "";
        _bubble.IsVisible = ShowBubble;
        _min.IsVisible = _mid.IsVisible = _max.IsVisible = ShowLabels;
        _bubbleText.Text = Value.ToString(Step < 1 ? "0.0" : "0", CultureInfo.InvariantCulture);
        PlaceBubble();
    }

    /// <summary>Keeps the bubble over the thumb: the thumb travels the track less its own width.</summary>
    void PlaceBubble()
    {
        const double thumb = 20;
        double width = _slider.Bounds.Width;
        if (width <= 0 || Maximum <= Minimum)
            return;
        double t = (Math.Clamp(Value, Minimum, Maximum) - Minimum) / (Maximum - Minimum);
        double center = thumb / 2 + t * (width - thumb);
        _bubble.Measure(Size.Infinity);
        _bubble.Margin = new Thickness(Math.Clamp(center - _bubble.DesiredSize.Width / 2, 0, Math.Max(0, width - _bubble.DesiredSize.Width)), 0, 0, 2);
    }
}
