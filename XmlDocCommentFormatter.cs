using System.Text.RegularExpressions;

namespace Madoka.CSharpCodeAssister
{
    public static class XmlDocCommentFormatter
    {
        private static readonly Regex MultiLineXmlDocRegex = new Regex(
            @"^(\s*///\s*)<(\w+)((?:""[^""]*""|'[^']*'|[^>""'])*)>\s*\r?\n\s*///\s*([^\r\n]*?)\s*\r?\n\s*///\s*</\2>",
            RegexOptions.Multiline | RegexOptions.Compiled);

        public static string Format(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var result = MultiLineXmlDocRegex.Replace(text, "$1<$2$3>$4</$2>");

            return result;
        }
    }
}
