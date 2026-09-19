using System.Text.RegularExpressions;

namespace Backend.Shared.Logging;

public static partial class SensitiveDataFilter
{
    public static string SanitizeAndLimit(string value, int maxLength = 64 * 1024)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sanitized = SensitiveJsonValueRegex().Replace(value, "$1***$3");
        return sanitized.Length <= maxLength
            ? sanitized
            : sanitized[..maxLength] + $"<truncated total={sanitized.Length}>";
    }

    [GeneratedRegex("(\"(?:password|token|authorization)\"\\s*:\\s*\")(.*?)(\")", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveJsonValueRegex();
}
