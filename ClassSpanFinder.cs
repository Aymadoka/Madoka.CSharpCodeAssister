#nullable enable

using System;

namespace Madoka.CSharpCodeAssister
{
    internal static class ClassSpanFinder
    {
        public static (int Start, int Length)? TryFindEnclosingClass(string text, int caretPosition)
        {
            if (caretPosition < 0 || caretPosition > text.Length)
                return null;

            var classKeywordIndex = FindClassKeywordBeforeCaret(text, caretPosition);
            while (classKeywordIndex >= 0)
            {
                var openBraceIndex = text.IndexOf('{', classKeywordIndex);
                if (openBraceIndex >= 0)
                {
                    var closeBraceIndex = FindMatchingCloseBrace(text, openBraceIndex);
                    if (closeBraceIndex >= 0
                        && caretPosition >= openBraceIndex
                        && caretPosition <= closeBraceIndex)
                    {
                        return (classKeywordIndex, closeBraceIndex - classKeywordIndex + 1);
                    }
                }

                classKeywordIndex = FindClassKeywordBeforeCaret(text, classKeywordIndex - 1);
            }

            return null;
        }

        private static int FindClassKeywordBeforeCaret(string text, int caretPosition)
        {
            var searchEnd = Math.Min(caretPosition, text.Length);
            for (var index = searchEnd - 1; index >= 4; index--)
            {
                if (!IsClassKeywordAt(text, index - 4))
                    continue;

                var keywordStart = index - 4;
                if (keywordStart == 0 || !char.IsLetterOrDigit(text[keywordStart - 1]))
                    return keywordStart;
            }

            return -1;
        }

        private static bool IsClassKeywordAt(string text, int startIndex)
        {
            if (startIndex < 0 || startIndex + 5 > text.Length)
                return false;

            return string.Compare(text, startIndex, "class", 0, 5, StringComparison.Ordinal) == 0
                && (startIndex + 5 >= text.Length || !char.IsLetterOrDigit(text[startIndex + 5]));
        }

        private static int FindMatchingCloseBrace(string text, int openBraceIndex)
        {
            var depth = 0;
            for (var index = openBraceIndex; index < text.Length; index++)
            {
                switch (text[index])
                {
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                            return index;
                        break;
                }
            }

            return -1;
        }
    }
}
