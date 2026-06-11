#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Madoka.CSharpCodeAssister
{
    internal static class ClassConstructorGenerator
    {
        internal sealed class GenerationResult
        {
            public int InsertPosition { get; init; }
            public string TextToInsert { get; init; } = string.Empty;
        }

        public static GenerationResult? TryGenerate(string text, int caretPosition)
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

            if (classDecl.Modifiers.Any(SyntaxKind.StaticKeyword))
                return null;

            if (HasExistingConstructor(classDecl))
                return null;

            var properties = GetAssignableProperties(classDecl);
            var className = classDecl.Identifier.Text;
            var memberIndent = GetMemberIndent(text, classDecl);
            var lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var generatedText = BuildConstructors(className, properties, memberIndent, lineEnding);
            var insertPosition = classDecl.Members.Count > 0
                ? classDecl.Members[0].FullSpan.Start
                : classDecl.CloseBraceToken.FullSpan.Start;

            return new GenerationResult
            {
                InsertPosition = insertPosition,
                TextToInsert = generatedText,
            };
        }

        private static bool HasExistingConstructor(ClassDeclarationSyntax classDecl)
        {
            if (classDecl.ParameterList?.Parameters.Count > 0)
                return true;

            return classDecl.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Any(constructor => !constructor.Modifiers.Any(SyntaxKind.StaticKeyword));
        }

        private static List<PropertyDeclarationSyntax> GetAssignableProperties(ClassDeclarationSyntax classDecl)
        {
            return classDecl.Members
                .OfType<PropertyDeclarationSyntax>()
                .Where(IsAssignableInstanceProperty)
                .ToList();
        }

        private static bool IsAssignableInstanceProperty(PropertyDeclarationSyntax property)
        {
            if (property.Modifiers.Any(SyntaxKind.StaticKeyword))
                return false;

            if (property.ExpressionBody is not null)
                return false;

            if (property.AccessorList is null)
                return false;

            var accessors = property.AccessorList.Accessors;
            if (accessors.Any(accessor =>
                    accessor.IsKind(SyntaxKind.SetAccessorDeclaration) ||
                    accessor.IsKind(SyntaxKind.InitAccessorDeclaration)))
            {
                return true;
            }

            return accessors.All(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration));
        }

        private static string GetMemberIndent(string text, ClassDeclarationSyntax classDecl)
        {
            var lineStart = text.LastIndexOf('\n', Math.Min(classDecl.SpanStart, text.Length - 1));
            if (lineStart < 0)
                lineStart = 0;
            else
                lineStart++;

            var indent = new StringBuilder();
            for (var i = lineStart; i < text.Length && (text[i] == ' ' || text[i] == '\t'); i++)
                indent.Append(text[i]);

            indent.Append("    ");
            return indent.ToString();
        }

        private static string BuildConstructors(
            string className,
            IReadOnlyList<PropertyDeclarationSyntax> properties,
            string memberIndent,
            string lineEnding)
        {
            var sb = new StringBuilder();
            sb.Append(lineEnding);
            sb.Append(memberIndent).Append("private ").Append(className).Append("()").Append(lineEnding);
            sb.Append(memberIndent).Append('{').Append(lineEnding);
            sb.Append(memberIndent).Append("    /* This constructor is for deserialization / AutoMap purpose */")
                .Append(lineEnding);
            sb.Append(memberIndent).Append('}').Append(lineEnding);

            if (properties.Count == 0)
                return sb.ToString();

            sb.Append(lineEnding);
            sb.Append(memberIndent).Append("public ").Append(className).Append('(').Append(lineEnding);

            for (var i = 0; i < properties.Count; i++)
            {
                var property = properties[i];
                var parameterName = ToParameterName(property.Identifier.Text);
                var comma = i < properties.Count - 1 ? "," : string.Empty;
                sb.Append(memberIndent)
                    .Append("    ")
                    .Append(property.Type)
                    .Append(' ')
                    .Append(parameterName)
                    .Append(comma)
                    .Append(lineEnding);
            }

            sb.Append(memberIndent).Append(')').Append(lineEnding);
            sb.Append(memberIndent).Append('{').Append(lineEnding);

            foreach (var property in properties)
            {
                var parameterName = ToParameterName(property.Identifier.Text);
                sb.Append(memberIndent)
                    .Append("    ")
                    .Append(property.Identifier.Text)
                    .Append(" = ")
                    .Append(parameterName)
                    .Append(';')
                    .Append(lineEnding);
            }

            sb.Append(memberIndent).Append('}').Append(lineEnding);
            return sb.ToString();
        }

        private static string ToParameterName(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                return propertyName;

            var parameterName = char.ToLowerInvariant(propertyName[0]) + propertyName[1..];
            if (SyntaxFacts.IsKeywordKind(SyntaxFacts.GetKeywordKind(parameterName)))
                parameterName = "@" + parameterName;

            return parameterName;
        }
    }
}
