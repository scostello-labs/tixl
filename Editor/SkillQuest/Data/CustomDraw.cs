#nullable enable
using ImGuiNET;
using T3.Core.DataTypes.Vector;
using T3.Core.Utils;

namespace T3.Editor.SkillQuest.Data;

internal static class CustomDraw
{
    private static readonly int[] _wrapLineIndices = new int[10];

    // The method now accepts a Span of ReadOnlySpans for the wrapped lines to avoid allocations
    public static void AddWrappedCenteredText(ImDrawListPtr dl, string text, Vector2 position, int wrapCharCount, Color color)
    {
        var textLength = text.Length;
        var currentLineStart = 0;
        var lineCount = 0;

        // Step 1: Calculate wrap indices
        while (currentLineStart < textLength && lineCount < _wrapLineIndices.Length)
        {
            var lineEnd = currentLineStart + wrapCharCount;

            if (lineEnd > textLength)
            {
                _wrapLineIndices[lineCount] = currentLineStart;
                lineCount++;
                break;
            }

            // Search backwards to find the last space or punctuation within the wrap length
            var wrapPoint = (lineEnd - 1).ClampMin(0);
            while (wrapPoint > 0 && wrapPoint > currentLineStart && !IsValidLineBreakCharacter(text[wrapPoint]))
            {
                wrapPoint--;
            }

            if (wrapPoint == currentLineStart)
            {
                wrapPoint = lineEnd; // Force wrap at max length if no valid break found
            }

            _wrapLineIndices[lineCount] = currentLineStart;
            currentLineStart = wrapPoint;
            lineCount++;
        }

        // Step 2: Draw wrapped text centered horizontally and vertically
        var lineHeight = ImGui.GetTextLineHeight();
        var totalHeight = lineHeight * lineCount;
        var yStart = position.Y - totalHeight / 2.0f; // Center vertically

        for (var i = 0; i < lineCount; i++)
        {
            // Calculate the slice for the line using the stored indices
            var startIdx = _wrapLineIndices[i];
            var endIdx = (i + 1 < lineCount) ? _wrapLineIndices[i + 1] : text.Length;
            var lineSpan = text.AsSpan(startIdx, endIdx - startIdx); // Slice the original text

            var textWidth = ImGui.CalcTextSize(lineSpan).X;
            var xStart = position.X - textWidth / 2.0f; // Center horizontally

            // Draw the line at the correct position
            dl.AddText(new Vector2(xStart, yStart + i * lineHeight), color, lineSpan);
        }

        return;

        bool IsValidLineBreakCharacter(char c)
        {
            return char.IsWhiteSpace(c) || c == '-' || c == '.' || c == ',' || c == ';' || c == '!' || c == '?';
        }
    }
    
    private static readonly Vector2[] _pointsForNgon = new Vector2[MaxNgonCorners];
    private const int MaxNgonCorners = 8;

    public static void AddNgonRotated(this ImDrawListPtr dl, Vector2 center, float radius, uint color, bool filled = true, int count = 6,
                                      float startAngle = -MathF.PI / 2f)
    {
        count = count.ClampMax(MaxNgonCorners);

        for (var i = 0; i < count; i++)
        {
            var a = startAngle + i * (2 * MathF.PI / count);
            _pointsForNgon[i] = new Vector2(
                                            center.X + MathF.Cos(a) * radius,
                                            center.Y + MathF.Sin(a) * radius
                                           );
        }

        if (filled)
        {
            dl.AddConvexPolyFilled(ref _pointsForNgon[0], count, color);
        }
        else
        {
            dl.AddPolyline(ref _pointsForNgon[0], count, color, ImDrawFlags.Closed, 2);
        }
    }
}