using System;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TBDMovies.UI.Controls;

/// <summary>
/// A native WinUI 3 panel that positions child elements sequentially from left to right,
/// wrapping to a new line when the margin of the containing box is reached.
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(WrapPanel),
        new PropertyMetadata(Orientation.Horizontal, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is WrapPanel panel)
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var isHorizontal = Orientation == Orientation.Horizontal;
        var colSpacing = ColumnSpacing;
        var rowSpacing = RowSpacing;

        var availableItemSize = new Size(
            double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? double.PositiveInfinity : availableSize.Height);

        double currentLineWidth = 0;
        double currentLineHeight = 0;
        double maxLineWidth = 0;
        double totalHeight = 0;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            child.Measure(availableItemSize);
            var desired = child.DesiredSize;

            if (isHorizontal)
            {
                if (currentLineWidth > 0 && currentLineWidth + desired.Width > availableSize.Width)
                {
                    maxLineWidth = Math.Max(maxLineWidth, currentLineWidth - colSpacing);
                    totalHeight += currentLineHeight + rowSpacing;
                    currentLineWidth = desired.Width + colSpacing;
                    currentLineHeight = desired.Height;
                }
                else
                {
                    currentLineWidth += desired.Width + colSpacing;
                    currentLineHeight = Math.Max(currentLineHeight, desired.Height);
                }
            }
            else
            {
                if (currentLineHeight > 0 && currentLineHeight + desired.Height > availableSize.Height)
                {
                    totalHeight = Math.Max(totalHeight, currentLineHeight - rowSpacing);
                    maxLineWidth += currentLineWidth + colSpacing;
                    currentLineHeight = desired.Height + rowSpacing;
                    currentLineWidth = desired.Width;
                }
                else
                {
                    currentLineHeight += desired.Height + rowSpacing;
                    currentLineWidth = Math.Max(currentLineWidth, desired.Width);
                }
            }
        }

        if (isHorizontal)
        {
            maxLineWidth = Math.Max(maxLineWidth, currentLineWidth > 0 ? currentLineWidth - colSpacing : 0);
            totalHeight += currentLineHeight;
            return new Size(
                double.IsPositiveInfinity(availableSize.Width) ? maxLineWidth : Math.Min(availableSize.Width, maxLineWidth),
                totalHeight);
        }
        else
        {
            totalHeight = Math.Max(totalHeight, currentLineHeight > 0 ? currentLineHeight - rowSpacing : 0);
            maxLineWidth += currentLineWidth;
            return new Size(
                maxLineWidth,
                double.IsPositiveInfinity(availableSize.Height) ? totalHeight : Math.Min(availableSize.Height, totalHeight));
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var isHorizontal = Orientation == Orientation.Horizontal;
        var colSpacing = ColumnSpacing;
        var rowSpacing = RowSpacing;

        double currentX = 0;
        double currentY = 0;
        double currentLineHeight = 0;
        double currentLineWidth = 0;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            var desired = child.DesiredSize;

            if (isHorizontal)
            {
                if (currentX > 0 && currentX + desired.Width > finalSize.Width)
                {
                    currentX = 0;
                    currentY += currentLineHeight + rowSpacing;
                    currentLineHeight = 0;
                }

                child.Arrange(new Rect(currentX, currentY, desired.Width, desired.Height));
                currentX += desired.Width + colSpacing;
                currentLineHeight = Math.Max(currentLineHeight, desired.Height);
            }
            else
            {
                if (currentY > 0 && currentY + desired.Height > finalSize.Height)
                {
                    currentY = 0;
                    currentX += currentLineWidth + colSpacing;
                    currentLineWidth = 0;
                }

                child.Arrange(new Rect(currentX, currentY, desired.Width, desired.Height));
                currentY += desired.Height + rowSpacing;
                currentLineWidth = Math.Max(currentLineWidth, desired.Width);
            }
        }

        return finalSize;
    }
}
