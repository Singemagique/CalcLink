using Microsoft.UI.Xaml;

namespace CalcLink.App.Helpers;

/// <summary>
/// Visual state trigger that is active while <see cref="Target"/>'s width is in
/// [<see cref="MinWidth"/>, <see cref="MaxWidth"/>) DIP. Unlike <see cref="AdaptiveTrigger"/> (which watches the window), this follows the element itself,
/// so a page reacts to the space it actually gets next to the navigation pane.
/// </summary>
public sealed partial class ElementWidthTrigger : StateTriggerBase
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(FrameworkElement), typeof(ElementWidthTrigger),
        new PropertyMetadata(null, (d, e) => ((ElementWidthTrigger)d).OnTargetChanged(e.OldValue as FrameworkElement, e.NewValue as FrameworkElement)));

    public static readonly DependencyProperty MinWidthProperty = DependencyProperty.Register(
        nameof(MinWidth), typeof(double), typeof(ElementWidthTrigger),
        new PropertyMetadata(0.0, (d, _) => ((ElementWidthTrigger)d).Update()));

    public static readonly DependencyProperty MaxWidthProperty = DependencyProperty.Register(
        nameof(MaxWidth), typeof(double), typeof(ElementWidthTrigger),
        new PropertyMetadata(double.PositiveInfinity, (d, _) => ((ElementWidthTrigger)d).Update()));

    public FrameworkElement? Target
    {
        get => (FrameworkElement?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public double MinWidth
    {
        get => (double)GetValue(MinWidthProperty);
        set => SetValue(MinWidthProperty, value);
    }

    /// <summary>Exclusive upper bound; the trigger is active while MinWidth &lt;= width &lt; MaxWidth.</summary>
    public double MaxWidth
    {
        get => (double)GetValue(MaxWidthProperty);
        set => SetValue(MaxWidthProperty, value);
    }

    private void OnTargetChanged(FrameworkElement? oldTarget, FrameworkElement? newTarget)
    {
        if (oldTarget is not null)
        {
            oldTarget.SizeChanged -= OnSizeChanged;
        }
        if (newTarget is not null)
        {
            newTarget.SizeChanged += OnSizeChanged;
        }
        Update();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Update();

    // Before the first layout ActualWidth is 0; stay inactive until the element has a real size.
    private void Update() =>
        SetActive(Target is { ActualWidth: > 0 } t && t.ActualWidth >= MinWidth && t.ActualWidth < MaxWidth);
}
