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
    internal static class CreateFromMappingGenerator
    {
        internal sealed class GenerationResult
        {
            public int ReplaceStart { get; init; }
            public int ReplaceLength { get; init; }
            public string ReplacementText { get; init; } = string.Empty;
        }

        internal sealed class GenerationAttempt
        {
            public GenerationResult? Result { get; init; }
            public string? FailureMessage { get; init; }
        }

        public static GenerationResult? TryGenerate(string text, int caretPosition, SemanticModel? semanticModel = null)
        {
            return TryGenerateWithDiagnostics(text, caretPosition, semanticModel).Result;
        }

        internal sealed class CreateFromContext
        {
            public MethodDeclarationSyntax Method { get; init; } = null!;
            public CompilationUnitSyntax Root { get; init; } = null!;
            public string SourceTypeName { get; init; } = string.Empty;
            public string TargetTypeName { get; init; } = string.Empty;
        }

        internal static CreateFromContext? TryGetCreateFromContext(string text, int caretPosition)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            var root = CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var method = FindCreateFromMethod(root, caretPosition);
            if (method is null || method.Body is null || method.Body.Statements.Count > 0)
                return null;

            var sourceTypeName = GetTypeShortName(method.ParameterList.Parameters[0].Type);
            var targetTypeName = GetTypeShortName(method.ReturnType);
            if (string.IsNullOrEmpty(sourceTypeName) || string.IsNullOrEmpty(targetTypeName))
                return null;

            return new CreateFromContext
            {
                Method = method,
                Root = root,
                SourceTypeName = sourceTypeName,
                TargetTypeName = targetTypeName,
            };
        }

        public static GenerationAttempt TryGenerateWithDiagnostics(
            string text,
            int caretPosition,
            SemanticModel? semanticModel = null,
            IReadOnlyList<CompilationUnitSyntax>? additionalSyntaxRoots = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new GenerationAttempt
                {
                    FailureMessage = "The active document is empty.",
                };
            }

            var root = semanticModel?.SyntaxTree.GetCompilationUnitRoot()
                ?? CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();
            var method = FindCreateFromMethod(root, caretPosition);
            if (method is null)
            {
                return new GenerationAttempt
                {
                    FailureMessage = "Place the caret inside an empty static CreateFrom method body.",
                };
            }

            if (method.Body is null)
            {
                return new GenerationAttempt
                {
                    FailureMessage = "CreateFrom must use a block body with { }.",
                };
            }

            if (method.Body.Statements.Count > 0)
            {
                return new GenerationAttempt
                {
                    FailureMessage = "CreateFrom method body must be empty.",
                };
            }

            additionalSyntaxRoots ??= Array.Empty<CompilationUnitSyntax>();

            var syntaxResult = TryGenerateFromSyntax(text, root, method, additionalSyntaxRoots);
            if (syntaxResult is not null)
                return new GenerationAttempt { Result = syntaxResult };

            if (semanticModel is not null)
            {
                var semanticResult = TryGenerateFromSemanticModel(text, method, semanticModel);
                if (semanticResult is not null)
                    return new GenerationAttempt { Result = semanticResult };

                var semanticFailure = DescribeSemanticFailure(method, semanticModel);
                if (semanticFailure is not null)
                {
                    return new GenerationAttempt { FailureMessage = semanticFailure };
                }
            }

            if (additionalSyntaxRoots.Count == 0)
            {
                return new GenerationAttempt
                {
                    FailureMessage = "Could not find the source type definition file in referenced projects.",
                };
            }

            return new GenerationAttempt
            {
                FailureMessage = "No matching writable properties were found between the parameter type and return type.",
            };
        }

        private static string? DescribeSemanticFailure(
            MethodDeclarationSyntax method,
            SemanticModel semanticModel)
        {
            var sourceParameter = method.ParameterList.Parameters[0];
            ITypeSymbol? sourceType;
            ITypeSymbol? targetType;

            if (semanticModel.GetDeclaredSymbol(method) is IMethodSymbol methodSymbol)
            {
                sourceType = UnwrapType(methodSymbol.Parameters[0].Type);
                targetType = UnwrapType(methodSymbol.ReturnType);
            }
            else
            {
                sourceType = sourceParameter.Type is null
                    ? null
                    : semanticModel.GetTypeInfo(sourceParameter.Type).Type;
                targetType = method.ReturnType is null
                    ? null
                    : semanticModel.GetTypeInfo(method.ReturnType).Type;
                if (sourceType is not null)
                    sourceType = UnwrapType(sourceType);
                if (targetType is not null)
                    targetType = UnwrapType(targetType);
            }

            if (sourceType is null || sourceType.TypeKind == TypeKind.Error)
            {
                var typeName = sourceParameter.Type?.ToString() ?? "parameter type";
                return $"Could not resolve source type '{typeName}'. Build the solution so referenced projects are compiled.";
            }

            if (targetType is null || targetType.TypeKind == TypeKind.Error)
            {
                var typeName = method.ReturnType?.ToString() ?? "return type";
                return $"Could not resolve return type '{typeName}'.";
            }

            if (GetMatchingPropertyNames(sourceType, targetType).Count == 0)
            {
                return "No matching writable properties were found between the parameter type and return type.";
            }

            return null;
        }

        private static GenerationResult? TryGenerateFromSemanticModel(
            string text,
            MethodDeclarationSyntax method,
            SemanticModel semanticModel)
        {
            var sourceParameter = method.ParameterList.Parameters[0];
            ITypeSymbol? sourceType;
            ITypeSymbol? targetType;

            if (semanticModel.GetDeclaredSymbol(method) is IMethodSymbol methodSymbol)
            {
                sourceType = UnwrapType(methodSymbol.Parameters[0].Type);
                targetType = UnwrapType(methodSymbol.ReturnType);
            }
            else
            {
                sourceType = sourceParameter.Type is null
                    ? null
                    : semanticModel.GetTypeInfo(sourceParameter.Type).Type;
                targetType = method.ReturnType is null
                    ? null
                    : semanticModel.GetTypeInfo(method.ReturnType).Type;
                if (sourceType is not null)
                    sourceType = UnwrapType(sourceType);
                if (targetType is not null)
                    targetType = UnwrapType(targetType);
            }

            if (sourceType is null || targetType is null
                || sourceType.TypeKind == TypeKind.Error || targetType.TypeKind == TypeKind.Error)
            {
                return null;
            }

            var matchingProperties = GetMatchingPropertyNames(sourceType, targetType);
            if (matchingProperties.Count == 0)
                return null;

            return BuildResult(text, method, matchingProperties);
        }

        private static GenerationResult? TryGenerateFromSyntax(
            string text,
            CompilationUnitSyntax root,
            MethodDeclarationSyntax method,
            IReadOnlyList<CompilationUnitSyntax> additionalSyntaxRoots)
        {
            var sourceParameter = method.ParameterList.Parameters[0];
            var sourceTypeName = GetTypeShortName(sourceParameter.Type);
            var targetTypeName = GetTypeShortName(method.ReturnType);
            if (string.IsNullOrEmpty(sourceTypeName) || string.IsNullOrEmpty(targetTypeName))
                return null;

            var syntaxRoots = additionalSyntaxRoots.Prepend(root);
            var matchingProperties = GetMatchingProperties(syntaxRoots, sourceTypeName, targetTypeName);
            if (matchingProperties.Count == 0)
                return null;

            return BuildResult(text, method, matchingProperties);
        }

        private static GenerationResult BuildResult(
            string text,
            MethodDeclarationSyntax method,
            IReadOnlyList<string> matchingProperties)
        {
            var sourceParameter = method.ParameterList.Parameters[0];
            var memberIndent = GetIndent(text, method.Body!.OpenBraceToken.SpanStart);
            var lineEnding = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var bodyText = BuildMethodBody(
                method.ReturnType.ToString(),
                sourceParameter.Identifier.Text,
                matchingProperties,
                memberIndent,
                lineEnding,
                ShouldAddNullCheck(sourceParameter.Type));

            return new GenerationResult
            {
                ReplaceStart = method.Body.FullSpan.Start,
                ReplaceLength = method.Body.FullSpan.Length,
                ReplacementText = bodyText,
            };
        }

        private static MethodDeclarationSyntax? FindCreateFromMethod(CompilationUnitSyntax root, int caretPosition)
        {
            var method = GetMethodAtCaret(root, caretPosition);
            if (IsEligibleCreateFromMethod(method))
                return method;

            return root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(candidate =>
                    IsEligibleCreateFromMethod(candidate)
                    && candidate.Body is not null
                    && candidate.Body.Span.Contains(caretPosition));
        }

        private static MethodDeclarationSyntax? GetMethodAtCaret(CompilationUnitSyntax root, int caretPosition)
        {
            if (caretPosition < 0 || caretPosition > root.FullSpan.End)
                return null;

            var token = root.FindToken(caretPosition);
            if (token.RawKind == (int)SyntaxKind.None)
            {
                token = root.FindToken(Math.Max(0, caretPosition - 1));
                if (token.RawKind == (int)SyntaxKind.None)
                    return null;
            }

            return token.Parent?.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        }

        private static bool IsEligibleCreateFromMethod(MethodDeclarationSyntax? method)
        {
            return method is not null
                && method.Modifiers.Any(SyntaxKind.StaticKeyword)
                && method.Identifier.Text == "CreateFrom"
                && method.ParameterList.Parameters.Count == 1
                && method.Body is not null
                && method.Body.Statements.Count == 0;
        }

        private static List<string> GetMatchingPropertyNames(ITypeSymbol sourceType, ITypeSymbol targetType)
        {
            var sourceProperties = GetInstanceProperties(sourceType)
                .Where(IsReadableProperty)
                .GroupBy(property => property.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var matchingProperties = new List<string>();
            foreach (var targetProperty in GetInstanceProperties(targetType))
            {
                if (!IsWritableProperty(targetProperty))
                    continue;

                if (sourceProperties.ContainsKey(targetProperty.Name))
                    matchingProperties.Add(targetProperty.Name);
            }

            return matchingProperties;
        }

        private static IEnumerable<IPropertySymbol> GetInstanceProperties(ITypeSymbol type)
        {
            return type.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(property => !property.IsStatic);
        }

        private static bool IsReadableProperty(IPropertySymbol property)
        {
            return property.GetMethod is not null;
        }

        private static bool IsWritableProperty(IPropertySymbol property)
        {
            return property.SetMethod is not null;
        }

        private static ITypeSymbol UnwrapType(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } namedType
                && namedType.TypeArguments.Length == 1)
            {
                return namedType.TypeArguments[0];
            }

            return type;
        }

        private static List<string> GetMatchingProperties(
            IEnumerable<CompilationUnitSyntax> syntaxRoots,
            string sourceTypeName,
            string targetTypeName)
        {
            var sourceProperties = syntaxRoots
                .SelectMany(root => GetPropertiesForTypeName(root, sourceTypeName))
                .Where(IsReadableProperty)
                .GroupBy(property => property.Identifier.Text, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var matchingProperties = new List<string>();
            var addedProperties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var targetProperty in syntaxRoots.SelectMany(root => GetPropertiesForTypeName(root, targetTypeName)))
            {
                if (!IsWritableProperty(targetProperty))
                    continue;

                if (sourceProperties.ContainsKey(targetProperty.Identifier.Text)
                    && addedProperties.Add(targetProperty.Identifier.Text))
                {
                    matchingProperties.Add(targetProperty.Identifier.Text);
                }
            }

            return matchingProperties;
        }

        private static IEnumerable<PropertyDeclarationSyntax> GetPropertiesForTypeName(
            CompilationUnitSyntax root,
            string typeName)
        {
            return root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(type => type.Identifier.Text == typeName)
                .SelectMany(type => type.Members.OfType<PropertyDeclarationSyntax>());
        }

        private static bool IsReadableProperty(PropertyDeclarationSyntax property)
        {
            if (property.Modifiers.Any(SyntaxKind.StaticKeyword))
                return false;

            if (property.ExpressionBody is not null)
                return true;

            if (property.AccessorList is null)
                return true;

            return property.AccessorList.Accessors.Any(accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration));
        }

        private static bool IsWritableProperty(PropertyDeclarationSyntax property)
        {
            if (property.Modifiers.Any(SyntaxKind.StaticKeyword))
                return false;

            if (property.ExpressionBody is not null)
                return false;

            if (property.AccessorList is null)
                return true;

            return property.AccessorList.Accessors.Any(accessor =>
                accessor.IsKind(SyntaxKind.SetAccessorDeclaration) ||
                accessor.IsKind(SyntaxKind.InitAccessorDeclaration));
        }

        private static string GetTypeShortName(TypeSyntax? typeSyntax)
        {
            if (typeSyntax is null)
                return string.Empty;

            return typeSyntax switch
            {
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                QualifiedNameSyntax qualified => qualified.Right.ToString(),
                NullableTypeSyntax nullable => GetTypeShortName(nullable.ElementType),
                PredefinedTypeSyntax predefined => predefined.Keyword.Text,
                GenericNameSyntax generic => generic.Identifier.Text,
                _ => typeSyntax.ToString(),
            };
        }

        private static bool ShouldAddNullCheck(TypeSyntax? typeSyntax)
        {
            if (typeSyntax is null)
                return true;

            if (typeSyntax is NullableTypeSyntax)
                return true;

            if (typeSyntax is PredefinedTypeSyntax predefined)
                return !IsPredefinedValueType(predefined);

            return true;
        }

        private static bool IsPredefinedValueType(PredefinedTypeSyntax predefined)
        {
            return predefined.Keyword.Kind() switch
            {
                SyntaxKind.BoolKeyword => true,
                SyntaxKind.ByteKeyword => true,
                SyntaxKind.SByteKeyword => true,
                SyntaxKind.ShortKeyword => true,
                SyntaxKind.UShortKeyword => true,
                SyntaxKind.IntKeyword => true,
                SyntaxKind.UIntKeyword => true,
                SyntaxKind.LongKeyword => true,
                SyntaxKind.ULongKeyword => true,
                SyntaxKind.FloatKeyword => true,
                SyntaxKind.DoubleKeyword => true,
                SyntaxKind.DecimalKeyword => true,
                SyntaxKind.CharKeyword => true,
                _ => false,
            };
        }

        private static string GetIndent(string text, int position)
        {
            var lineStart = text.LastIndexOf('\n', Math.Min(position, text.Length - 1));
            if (lineStart < 0)
                lineStart = 0;
            else
                lineStart++;

            var indent = new StringBuilder();
            for (var index = lineStart; index < text.Length && (text[index] == ' ' || text[index] == '\t'); index++)
                indent.Append(text[index]);

            indent.Append("    ");
            return indent.ToString();
        }

        private static string BuildMethodBody(
            string returnTypeText,
            string sourceParameterName,
            IReadOnlyList<string> matchingProperties,
            string memberIndent,
            string lineEnding,
            bool addNullCheck)
        {
            var sb = new StringBuilder();
            sb.Append('{').Append(lineEnding);

            if (addNullCheck)
            {
                sb.Append(memberIndent).Append("if (").Append(sourceParameterName).Append(" == null)").Append(lineEnding);
                sb.Append(memberIndent).Append('{').Append(lineEnding);
                sb.Append(memberIndent).Append("    return null;").Append(lineEnding);
                sb.Append(memberIndent).Append('}').Append(lineEnding);
                sb.Append(lineEnding);
            }

            sb.Append(memberIndent).Append("var result = new ").Append(returnTypeText).Append(lineEnding);
            sb.Append(memberIndent).Append('{').Append(lineEnding);

            foreach (var propertyName in matchingProperties)
            {
                sb.Append(memberIndent)
                    .Append("    ")
                    .Append(propertyName)
                    .Append(" = ")
                    .Append(sourceParameterName)
                    .Append('.')
                    .Append(propertyName)
                    .Append(',')
                    .Append(lineEnding);
            }

            sb.Append(memberIndent).Append("};").Append(lineEnding);
            sb.Append(lineEnding);
            sb.Append(memberIndent).Append("return result;").Append(lineEnding);

            var outerIndent = memberIndent.Length >= 4 ? memberIndent[..^4] : string.Empty;
            sb.Append(outerIndent).Append('}');
            return sb.ToString();
        }
    }
}
