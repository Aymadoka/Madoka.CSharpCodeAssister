#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Madoka.CSharpCodeAssister
{
    internal static class PropertyAttributeRemover
    {
        private static readonly HashSet<string> AttributesToRemove = new(StringComparer.OrdinalIgnoreCase)
        {
            "Required",
            "StringLength",
            "MaxLength",
            "MinLength",
            "Display",
            "Description",
        };

        public static string? TryRemove(string text, int caretPosition)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var tree = CSharpSyntaxTree.ParseText(text);
            var root = tree.GetCompilationUnitRoot();
            var token = root.FindToken(caretPosition);
            if (token.RawKind == (int)SyntaxKind.None)
                return null;

            var classDecl = token.Parent?.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl is null)
                return null;

            var changes = new List<TextChange>();
            foreach (var property in classDecl.Members.OfType<PropertyDeclarationSyntax>())
                CollectAttributeRemovals(property, changes);

            if (changes.Count == 0)
                return null;

            changes.Sort((a, b) => b.Span.Start.CompareTo(a.Span.Start));

            var sourceText = SourceText.From(text);
            foreach (var change in changes)
                sourceText = sourceText.WithChanges(change);

            return sourceText.ToString();
        }

        private static void CollectAttributeRemovals(PropertyDeclarationSyntax property, List<TextChange> changes)
        {
            foreach (var attributeList in property.AttributeLists)
            {
                var attributes = attributeList.Attributes;
                var removeIndices = Enumerable.Range(0, attributes.Count)
                    .Where(index => ShouldRemove(attributes[index]))
                    .ToArray();

                if (removeIndices.Length == 0)
                    continue;

                if (removeIndices.Length == attributes.Count)
                {
                    changes.Add(new TextChange(
                        TextSpan.FromBounds(GetRemovalStart(attributeList), GetRemovalEnd(attributeList)),
                        string.Empty));
                    continue;
                }

                foreach (var index in removeIndices.OrderByDescending(i => i))
                {
                    changes.Add(new TextChange(GetAttributeRemovalSpan(attributes, index), string.Empty));
                }
            }
        }

        private static int GetRemovalStart(AttributeListSyntax attributeList)
        {
            var start = attributeList.Span.Start;
            var leading = attributeList.GetLeadingTrivia();
            var lastLineBreakEnd = 0;

            foreach (var trivia in leading)
            {
                if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                    lastLineBreakEnd = trivia.Span.End;
            }

            for (var i = leading.Count - 1; i >= 0; i--)
            {
                var trivia = leading[i];
                if (trivia.Span.Start < lastLineBreakEnd)
                    break;

                if (trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                    start = trivia.Span.Start;
                else
                    break;
            }

            return start;
        }

        private static int GetRemovalEnd(AttributeListSyntax attributeList)
        {
            var end = attributeList.Span.End;
            foreach (var trivia in attributeList.GetTrailingTrivia())
            {
                if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                    return trivia.Span.End;

                if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                    end = trivia.Span.End;
                else
                    break;
            }

            return end;
        }

        private static TextSpan GetAttributeRemovalSpan(SeparatedSyntaxList<AttributeSyntax> attributes, int index)
        {
            var attribute = attributes[index];
            if (attributes.Count == 1)
                return attribute.Span;

            if (index < attributes.Count - 1)
            {
                var separator = attributes.GetSeparator(index);
                return TextSpan.FromBounds(attribute.Span.Start, separator.Span.End);
            }

            var precedingSeparator = attributes.GetSeparator(index - 1);
            return TextSpan.FromBounds(precedingSeparator.Span.Start, attribute.Span.End);
        }

        private static bool ShouldRemove(AttributeSyntax attribute)
        {
            return AttributesToRemove.Contains(GetAttributeShortName(attribute));
        }

        private static string GetAttributeShortName(AttributeSyntax attribute)
        {
            return attribute.Name switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                QualifiedNameSyntax qualified => qualified.Right.ToString(),
                _ => attribute.Name.ToString(),
            };
        }
    }
}

