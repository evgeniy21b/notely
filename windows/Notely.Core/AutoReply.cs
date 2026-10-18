namespace Notely.Core;

internal enum AutoDecision { Wait, Skip, Answer }
internal static class AutoReply
{
    public static AutoDecision Decide(string text, bool complete)
    {
        var value = text.Trim().Trim('*', '_', '`', '"', '\'', '.', '!', ' ', '\r', '\n').Trim();
        var skip = value.Equals("SKIP", StringComparison.OrdinalIgnoreCase);
        if (complete) return value.Length == 0 || skip ? AutoDecision.Skip : AutoDecision.Answer;
        // Hold fragmented SKIP and its Markdown wrappers, but release real answers
        // such as "SKIP LOCKED" once distinguishable from the exact sentinel.
        return text.Length <= 64 && (skip || "SKIP".StartsWith(value, StringComparison.OrdinalIgnoreCase))
            ? AutoDecision.Wait : AutoDecision.Answer;
    }
}
