#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Madoka.CSharpCodeAssister
{
    internal static class CreateFromSourceFileResolver
    {
        internal sealed class ResolvedSourceContext
        {
            public IReadOnlyList<SyntaxTree> AdditionalSyntaxTrees { get; init; } = Array.Empty<SyntaxTree>();
            public string? SourceTypeName { get; init; }
            public string? TargetTypeName { get; init; }
        }

        public static async Task<ResolvedSourceContext> TryResolveAsync(
            string currentDocumentText,
            int caretPosition,
            string currentDocumentPath,
            CancellationToken cancellationToken)
        {
            var context = CreateFromMappingGenerator.TryGetCreateFromContext(currentDocumentText, caretPosition);
            if (context is null)
                return new ResolvedSourceContext();

            if (TypeIsDefinedInDocument(context.SourceTypeName, currentDocumentText))
            {
                return new ResolvedSourceContext
                {
                    SourceTypeName = context.SourceTypeName,
                    TargetTypeName = context.TargetTypeName,
                };
            }

            var projectPath = FindProjectFileForDocument(currentDocumentPath);
            if (projectPath is null)
            {
                return new ResolvedSourceContext
                {
                    SourceTypeName = context.SourceTypeName,
                    TargetTypeName = context.TargetTypeName,
                };
            }

            var projectPaths = CollectProjectReferenceGraph(projectPath);
            var sourceFiles = FindTypeDefinitionFiles(context.SourceTypeName, projectPaths);
            if (sourceFiles.Count == 0)
            {
                return new ResolvedSourceContext
                {
                    SourceTypeName = context.SourceTypeName,
                    TargetTypeName = context.TargetTypeName,
                };
            }

            var additionalTrees = new List<SyntaxTree>();
            var loadedGlobalUsingsProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourceFile in sourceFiles)
            {
                var sourceProjectDirectory = FindProjectDirectoryForFile(sourceFile, projectPaths);
                if (sourceProjectDirectory is not null
                    && loadedGlobalUsingsProjects.Add(sourceProjectDirectory))
                {
                    var globalUsingsPath = Path.Combine(sourceProjectDirectory, "GlobalUsings.cs");
                    if (File.Exists(globalUsingsPath))
                    {
                        var globalUsingsText = await ReadFileTextAsync(globalUsingsPath, cancellationToken);
                        if (globalUsingsText is not null)
                        {
                            additionalTrees.Add(CSharpSyntaxTree.ParseText(globalUsingsText, path: globalUsingsPath));
                        }
                    }
                }

                var sourceText = await ReadFileTextAsync(sourceFile, cancellationToken);
                if (sourceText is null)
                    continue;

                additionalTrees.Add(CSharpSyntaxTree.ParseText(sourceText, path: sourceFile));
            }

            return new ResolvedSourceContext
            {
                AdditionalSyntaxTrees = additionalTrees,
                SourceTypeName = context.SourceTypeName,
                TargetTypeName = context.TargetTypeName,
            };
        }

        private static bool TypeIsDefinedInDocument(string typeName, string documentText)
        {
            var root = CSharpSyntaxTree.ParseText(documentText).GetCompilationUnitRoot();
            return root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Any(type => type.Identifier.Text == typeName);
        }

        private static List<string> FindTypeDefinitionFiles(string typeName, IReadOnlyList<string> projectPaths)
        {
            var matchedFiles = new List<string>();
            var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var projectPath in projectPaths)
            {
                var projectDirectory = Path.GetDirectoryName(projectPath);
                if (string.IsNullOrEmpty(projectDirectory) || !Directory.Exists(projectDirectory))
                    continue;

                foreach (var candidate in EnumerateTypeDefinitionCandidates(projectDirectory, typeName))
                {
                    if (!visitedFiles.Add(NormalizePath(candidate)))
                        continue;

                    if (FileDefinesType(candidate, typeName))
                    {
                        matchedFiles.Add(candidate);
                    }
                }
            }

            return matchedFiles;
        }

        private static IEnumerable<string> EnumerateTypeDefinitionCandidates(string projectDirectory, string typeName)
        {
            foreach (var exactNameMatch in Directory.EnumerateFiles(
                projectDirectory,
                typeName + ".cs",
                SearchOption.AllDirectories))
            {
                if (!IsBuildArtifactPath(exactNameMatch))
                    yield return exactNameMatch;
            }

            foreach (var sourceFile in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildArtifactPath(sourceFile))
                    continue;

                if (sourceFile.EndsWith(typeName + ".cs", StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return sourceFile;
            }
        }

        private static bool FileDefinesType(string filePath, string typeName)
        {
            string sourceText;
            try
            {
                sourceText = File.ReadAllText(filePath);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            if (!sourceText.Contains(typeName, StringComparison.Ordinal))
                return false;

            var root = CSharpSyntaxTree.ParseText(sourceText, path: filePath).GetCompilationUnitRoot();
            return root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Any(type => type.Identifier.Text == typeName);
        }

        private static IReadOnlyList<string> CollectProjectReferenceGraph(string projectPath)
        {
            var projectPaths = new List<string>();
            var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectProjectReferenceGraphCore(projectPath, visitedProjects, projectPaths);
            return projectPaths;
        }

        private static void CollectProjectReferenceGraphCore(
            string projectPath,
            HashSet<string> visitedProjects,
            List<string> projectPaths)
        {
            var normalizedProjectPath = NormalizePath(projectPath);
            if (!visitedProjects.Add(normalizedProjectPath))
                return;

            projectPaths.Add(normalizedProjectPath);

            foreach (var referencedProjectPath in GetProjectReferencesFromCsproj(projectPath))
            {
                CollectProjectReferenceGraphCore(referencedProjectPath, visitedProjects, projectPaths);
            }
        }

        private static IEnumerable<string> GetProjectReferencesFromCsproj(string projectPath)
        {
            if (!File.Exists(projectPath))
                yield break;

            XDocument projectDocument;
            try
            {
                projectDocument = XDocument.Load(projectPath);
            }
            catch (IOException)
            {
                yield break;
            }
            catch (UnauthorizedAccessException)
            {
                yield break;
            }

            var root = projectDocument.Root;
            if (root is null)
                yield break;

            XNamespace msbuildNamespace = root.Name.Namespace;
            foreach (var projectReference in root.Descendants(msbuildNamespace + "ProjectReference"))
            {
                var include = projectReference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                    continue;

                yield return ResolveProjectPath(projectPath, include);
            }
        }

        private static string? FindProjectDirectoryForFile(string filePath, IReadOnlyList<string> projectPaths)
        {
            var normalizedFilePath = NormalizePath(filePath);
            string? bestMatch = null;
            var bestLength = -1;

            foreach (var projectPath in projectPaths)
            {
                var projectDirectory = Path.GetDirectoryName(projectPath);
                if (string.IsNullOrEmpty(projectDirectory))
                    continue;

                var normalizedProjectDirectory = NormalizePath(projectDirectory);
                if (!normalizedFilePath.StartsWith(normalizedProjectDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (normalizedProjectDirectory.Length > bestLength)
                {
                    bestLength = normalizedProjectDirectory.Length;
                    bestMatch = projectDirectory;
                }
            }

            return bestMatch;
        }

        private static string? FindProjectFileForDocument(string documentPath)
        {
            var directory = Path.GetDirectoryName(documentPath);
            while (!string.IsNullOrEmpty(directory))
            {
                var projectFiles = Directory.GetFiles(directory, "*.csproj");
                if (projectFiles.Length == 1)
                    return NormalizePath(projectFiles[0]);

                if (projectFiles.Length > 1)
                {
                    var fileName = Path.GetFileName(documentPath);
                    foreach (var projectFile in projectFiles)
                    {
                        try
                        {
                            if (File.ReadAllText(projectFile).Contains(fileName, StringComparison.OrdinalIgnoreCase))
                                return NormalizePath(projectFile);
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }

                    return NormalizePath(projectFiles[0]);
                }

                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }

        private static string ResolveProjectPath(string baseProjectPath, string referencedProjectPath)
        {
            if (Path.IsPathRooted(referencedProjectPath))
                return NormalizePath(referencedProjectPath);

            var baseDirectory = Path.GetDirectoryName(baseProjectPath);
            if (string.IsNullOrEmpty(baseDirectory))
                return NormalizePath(referencedProjectPath);

            var combinedPath = Path.Combine(baseDirectory, referencedProjectPath);
            if (!combinedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && File.Exists(combinedPath + ".csproj"))
            {
                combinedPath += ".csproj";
            }

            return NormalizePath(combinedPath);
        }

        private static bool IsBuildArtifactPath(string filePath)
        {
            return filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string?> ReadFileTextAsync(string filePath, CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                return await File.ReadAllTextAsync(filePath, cancellationToken);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path);
        }
    }
}
