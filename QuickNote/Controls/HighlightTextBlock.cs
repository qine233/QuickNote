using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QuickNote.Services;

namespace QuickNote.Controls;

/// <summary>
/// A TextBlock that paints the current search terms. The script rows bind
/// <see cref="Source"/> to the title/preview and <see cref="Highlight"/> to the
/// live query text, so every hit becomes a marked run instead of the user having
/// to scan the whole line for it.
/// </summary>
public class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(string),
        typeof(HighlightTextBlock),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(
        nameof(Highlight),
        typeof(string),
        typeof(HighlightTextBlock),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush),
        typeof(Brush),
        typeof(HighlightTextBlock),
        new PropertyMetadata(null, OnContentChanged));

    /// <summary>Text to show.</summary>
    public string Source
    {
        get => (string)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Query whose terms get highlighted; empty for plain text.</summary>
    public string Highlight
    {
        get => (string)GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    /// <summary>Background of a matched run; falls back to a soft amber.</summary>
    public Brush? HighlightBrush
    {
        get => (Brush?)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    private static readonly Brush DefaultHighlightBrush = CreateDefaultBrush();

    private static Brush CreateDefaultBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x8A));
        brush.Freeze();
        return brush;
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HighlightTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();

        var text = Source ?? string.Empty;
        if (text.Length == 0)
            return;

        var ranges = SearchHighlighter.FindRanges(text, Highlight);
        if (ranges.Count == 0)
        {
            Inlines.Add(new Run(text));
            return;
        }

        var brush = HighlightBrush ?? DefaultHighlightBrush;
        var cursor = 0;
        foreach (var (start, length) in ranges)
        {
            if (start > cursor)
                Inlines.Add(new Run(text[cursor..start]));

            Inlines.Add(new Run(text.Substring(start, length))
            {
                Background = brush,
                FontWeight = FontWeights.SemiBold,
            });

            cursor = start + length;
        }

        if (cursor < text.Length)
            Inlines.Add(new Run(text[cursor..]));
    }
}
