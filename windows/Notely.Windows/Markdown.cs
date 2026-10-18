using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Notely.Windows;

internal static class Markdown
{
    public static FlowDocument Render(string text, double size)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = size,
            Foreground = Brushes.WhiteSmoke, PagePadding = new Thickness(2), ColumnWidth = double.PositiveInfinity };
        var parts = text.Split("```");
        for (var i = 0; i < parts.Length; i++)
        {
            if ((i & 1) != 0)
            {
                var code = parts[i];
                var firstNewline = code.IndexOf('\n');
                if (firstNewline >= 0 && !code[..firstNewline].Contains(' ')) code = code[(firstNewline + 1)..];
                doc.Blocks.Add(new Paragraph(new Run(code.Trim('\r', '\n'))) { FontFamily = new FontFamily("Consolas"), FontSize = size - 1,
                    Background = new SolidColorBrush(Color.FromRgb(40, 44, 52)), Foreground = Brushes.LightBlue,
                    Padding = new Thickness(8), Margin = new Thickness(0, 4, 0, 8) });
                continue;
            }
            var lines = parts[i].Split('\n');
            for (var line = 0; line < lines.Length; line++)
            {
                if (lines[line].TrimStart().StartsWith('|'))
                {
                    var table = new Table { CellSpacing = 0, FontSize = size - 2, Margin = new Thickness(0, 4, 0, 8) };
                    var group = new TableRowGroup(); table.RowGroups.Add(group);
                    var rowIndex = 0;
                    while (line < lines.Length && lines[line].TrimStart().StartsWith('|'))
                    {
                        var cells = lines[line++].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                        if (cells.All(c => Regex.IsMatch(c, @"^[-: ]+$"))) continue;
                        var row = new TableRow(); group.Rows.Add(row);
                        foreach (var cell in cells)
                        {
                            var p = Inline(cell);
                            if (rowIndex == 0) p.FontWeight = FontWeights.SemiBold;
                            row.Cells.Add(new TableCell(p) { Padding = new Thickness(5), BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(0, 0, 0, 1) });
                        }
                        rowIndex++;
                    }
                    line--; doc.Blocks.Add(table);
                }
                else doc.Blocks.Add(Inline(lines[line]));
            }
        }
        return doc;
    }
    private static Paragraph Inline(string text)
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 3), LineHeight = double.NaN };
        foreach (var part in Regex.Split(text.TrimEnd('\r'), @"(\*\*[^*]+\*\*|`[^`]+`)"))
        {
            if (part.StartsWith("**") && part.EndsWith("**")) p.Inlines.Add(new Bold(new Run(part[2..^2])));
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length >= 2)
                p.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Consolas"), Foreground = Brushes.LightBlue });
            else p.Inlines.Add(new Run(part));
        }
        return p;
    }
}
