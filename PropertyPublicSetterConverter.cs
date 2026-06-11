#nullable enable

using System;
using System.Text.RegularExpressions;

namespace Madoka.CSharpCodeAssister
{
    internal static class PropertyPublicSetterConverter
    {
        private static readonly Regex PrivateSetAccessorRegex = new Regex(
            @"(?<prefix>\{\s*get\s*;\s*)private\s+set(?<suffix>\s*;\s*\})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string? TryConvert(string text, int caretPosition)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var classSpan = ClassSpanFinder.TryFindEnclosingClass(text, caretPosition);
            if (classSpan is null)
                return null;

            var classText = text.Substring(classSpan.Value.Start, classSpan.Value.Length);
            var convertedClassText = PrivateSetAccessorRegex.Replace(classText, "${prefix}set${suffix}");
            if (convertedClassText == classText)
                return null;

            return text.Substring(0, classSpan.Value.Start)
                + convertedClassText
                + text.Substring(classSpan.Value.Start + classSpan.Value.Length);
        }
    }
}
