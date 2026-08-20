#nullable enable

using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Madoka.CSharpCodeAssister
{
    internal static class ClassSpanFinder
    {
        public static (int Start, int Length)? TryFindEnclosingType(string text, int caretPosition)
        {
            if (caretPosition < 0 || caretPosition > text.Length)
                return null;

            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();

            var token = root.FindToken(caretPosition);
            if (token.RawKind == (int)SyntaxKind.None)
            {
                token = root.FindToken(Math.Max(0, caretPosition - 1));
                if (token.RawKind == (int)SyntaxKind.None)
                    return null;
            }

            var typeDeclaration = token.Parent?
                .AncestorsAndSelf()
                .OfType<BaseTypeDeclarationSyntax>()
                .FirstOrDefault();

            if (typeDeclaration is null)
                return null;

            return (typeDeclaration.SpanStart, typeDeclaration.Span.Length);
        }
    }
}
