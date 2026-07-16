using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

// System.Windows.Documents, Markdig.Syntax and System.Collections.Generic each define a
// type called Block or List. Alias rather than fully-qualify at every use site.
using WpfBlock = System.Windows.Documents.Block;
using WpfList = System.Windows.Documents.List;
using MdBlock = Markdig.Syntax.Block;

namespace FlowBoard.Views.Markdown;

/// <summary>
/// Renders a Markdown string into a WPF FlowDocument.
///
/// Written by hand against Markdig's AST rather than pulling in Markdig.Wpf: that package
/// is a thin, largely unmaintained wrapper, and the subset of Markdown a kanban card
/// description actually uses is small enough that owning the renderer is cheaper than
/// owning the dependency. Anything unrecognised degrades to its plain text rather than
/// disappearing — a description must never silently lose content.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UseTaskLists()
        .Build();

    public static FlowDocument Render(string markdown, Style? linkStyle = null)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            LineHeight = 20,
            IsOptimalParagraphEnabled = true,
            IsHyphenationEnabled = false
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            doc.Blocks.Add(new Paragraph(new Run("No description.")) { Foreground = Brush("TextTertiaryBrush") });
            return doc;
        }

        var ast = Markdig.Markdown.Parse(markdown, Pipeline);
        foreach (var block in ast)
            if (Convert(block) is { } converted)
                doc.Blocks.Add(converted);

        return doc;
    }

    private static WpfBlock? Convert(MdBlock block) => block switch
    {
        HeadingBlock h => Heading(h),
        ParagraphBlock p => Para(p.Inline, 0, 0, 8),
        ListBlock l => BulletList(l),
        QuoteBlock q => Quote(q),
        CodeBlock c => Code(c),
        ThematicBreakBlock => Rule(),
        _ => null
    };

    private static WpfBlock Heading(HeadingBlock h)
    {
        var size = h.Level switch { 1 => 19.0, 2 => 16.0, 3 => 14.5, _ => 13.5 };
        var p = Para(h.Inline, 0, h.Level == 1 ? 0 : 10, 6);
        p.FontSize = size;
        p.FontWeight = FontWeights.SemiBold;
        return p;
    }

    private static Paragraph Para(ContainerInline? inlines, double left, double top, double bottom)
    {
        var p = new Paragraph { Margin = new Thickness(left, top, 0, bottom) };
        if (inlines is not null) Emit(inlines, p.Inlines);
        return p;
    }

    private static WpfBlock BulletList(ListBlock list)
    {
        var wpfList = new WpfList
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(18, 0, 0, 0)
        };

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var li = new ListItem();
            foreach (var child in item)
                if (Convert(child) is { } converted)
                    li.Blocks.Add(converted);

            if (li.Blocks.Count == 0) li.Blocks.Add(new Paragraph());
            wpfList.ListItems.Add(li);
        }

        return wpfList;
    }

    private static WpfBlock Quote(QuoteBlock quote)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 2, 0, 2),
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = Brush("TextTertiaryBrush"),
            Foreground = Brush("TextSecondaryBrush")
        };

        foreach (var child in quote)
            if (Convert(child) is { } converted)
                section.Blocks.Add(converted);

        return section;
    }

    private static WpfBlock Code(CodeBlock code)
    {
        var text = code is FencedCodeBlock or CodeBlock
            ? string.Join(Environment.NewLine, code.Lines.Lines.Take(code.Lines.Count).Select(l => l.ToString()))
            : string.Empty;

        return new Paragraph(new Run(text))
        {
            FontFamily = Font("UtilityFont"),
            FontSize = 12,
            Background = Brush("LaneBackgroundBrush"),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };
    }

    private static WpfBlock Rule() => new Paragraph
    {
        BorderThickness = new Thickness(0, 1, 0, 0),
        BorderBrush = Brush("CardStrokeBrush"),
        Margin = new Thickness(0, 4, 0, 12)
    };

    private static void Emit(ContainerInline container, InlineCollection target)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;

                case EmphasisInline em:
                {
                    var span = new Span();
                    // Markdig encodes bold as a doubled delimiter, not a distinct node.
                    if (em.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold;
                    else span.FontStyle = FontStyles.Italic;
                    Emit(em, span.Inlines);
                    target.Add(span);
                    break;
                }

                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = Font("UtilityFont"),
                        Background = Brush("LaneBackgroundBrush")
                    });
                    break;

                case LinkInline link:
                {
                    var hyperlink = new Hyperlink { NavigateUri = SafeUri(link.Url) };
                    Emit(link, hyperlink.Inlines);
                    if (hyperlink.Inlines.Count == 0) hyperlink.Inlines.Add(new Run(link.Url ?? ""));

                    hyperlink.RequestNavigate += (_, e) => { Launcher.Open(e.Uri.ToString()); e.Handled = true; };
                    target.Add(hyperlink);
                    break;
                }

                case LineBreakInline lb:
                    if (lb.IsHard) target.Add(new LineBreak());
                    else target.Add(new Run(" "));
                    break;

                case ContainerInline nested:
                    Emit(nested, target);
                    break;

                default:
                    // Unknown node: emit its source text rather than dropping it.
                    if (inline is LeafInline leaf) target.Add(new Run(leaf.ToString() ?? ""));
                    break;
            }
        }
    }

    private static Uri? SafeUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static FontFamily Font(string key) =>
        Application.Current?.TryFindResource(key) as FontFamily ?? new FontFamily("Consolas");
}

/// <summary>
/// Opens a URL or local path with the shell.
///
/// Card links are user-authored strings, and ShellExecute on an arbitrary string is a way
/// to launch arbitrary things. So: absolute http/https/mailto/file only, and existing
/// local paths. Anything else is refused rather than handed to the shell to interpret.
/// </summary>
public static class Launcher
{
    public static void Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;

        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("http" or "https" or "mailto" or "file")) return;
        }
        else if (!System.IO.File.Exists(target) && !System.IO.Directory.Exists(target))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"Couldn't open:\n{target}", "FlowBoard",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
