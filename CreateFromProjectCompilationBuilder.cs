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
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.ProjectSystem.Query;

namespace Madoka.CSharpCodeAssister
{
    internal static class CreateFromProjectCompilationBuilder
    {
        public static async Task<Compilation?> TryCreateAsync(
            VisualStudioExtensibility extensibility,
            IClientContext context,
            string currentDocumentText,
            string? currentDocumentPath,
            IReadOnlyList<SyntaxTree>? additionalSyntaxTrees,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(currentDocumentPath) || string.IsNullOrEmpty(currentDocumentText))
                return null;

            var syntaxTrees = new List<SyntaxTree>
            {
                CSharpSyntaxTree.ParseText(currentDocumentText, path: currentDocumentPath),
            };

            if (additionalSyntaxTrees is not null)
            {
                var existingPaths = new HashSet<string>(
                    syntaxTrees
                        .Where(tree => tree.FilePath is not null)
                        .Select(tree => NormalizePath(tree.FilePath!)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var syntaxTree in additionalSyntaxTrees)
                {
                    if (syntaxTree.FilePath is not null
                        && !existingPaths.Add(NormalizePath(syntaxTree.FilePath)))
                    {
                        continue;
                    }

                    syntaxTrees.Add(syntaxTree);
                }
            }

            var metadataReferencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IProjectSnapshot? project = null;
            try
            {
                project = await context.GetActiveProjectAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
            }

            var projectPath = !string.IsNullOrWhiteSpace(project?.Path)
                ? NormalizePath(project.Path)
                : FindProjectFileForDocument(currentDocumentPath);

            if (project is not null)
            {
                try
                {
                    await CollectResolvedAssemblyReferencesAsync(
                        project,
                        metadataReferencePaths,
                        cancellationToken);
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                var workspace = extensibility.Workspaces();
                var activeConfiguration = await TryGetActiveConfigurationAsync(workspace, cancellationToken);
                var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (project is not null)
                {
                    try
                    {
                        await CollectReferencedProjectAssembliesAsync(
                            workspace,
                            project,
                            projectPath,
                            activeConfiguration,
                            visitedProjects,
                            metadataReferencePaths,
                            cancellationToken);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                CollectReferencedProjectAssembliesFromCsproj(
                    projectPath,
                    activeConfiguration,
                    visitedProjects,
                    metadataReferencePaths);
            }

            var references = GetMetadataReferences();
            foreach (var referencePath in metadataReferencePaths)
            {
                if (!File.Exists(referencePath))
                    continue;

                references = references
                    .Append(MetadataReference.CreateFromFile(referencePath))
                    .ToArray();
            }

            return CSharpCompilation.Create(
                "MadokaCreateFromMapping",
                syntaxTrees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        }

        private static async Task<string> TryGetActiveConfigurationAsync(
            WorkspacesExtensibility workspace,
            CancellationToken cancellationToken)
        {
            try
            {
                var solutions = await workspace.QuerySolutionAsync(
                    solution => solution.With(solution => solution.ActiveConfiguration),
                    cancellationToken);

                foreach (var solution in solutions)
                {
                    if (!string.IsNullOrWhiteSpace(solution.ActiveConfiguration))
                        return solution.ActiveConfiguration;
                }
            }
            catch (InvalidOperationException)
            {
            }

            return "Debug";
        }

        private static void CollectReferencedProjectAssembliesFromCsproj(
            string projectPath,
            string activeConfiguration,
            HashSet<string> visitedProjects,
            HashSet<string> metadataReferencePaths)
        {
            if (!visitedProjects.Add(NormalizePath(projectPath)))
                return;

            TryAddBuiltProjectReferenceWithDependencies(
                projectPath,
                activeConfiguration,
                metadataReferencePaths);

            foreach (var referencedProjectPath in GetProjectReferencesFromCsproj(projectPath))
            {
                CollectReferencedProjectAssembliesFromCsproj(
                    referencedProjectPath,
                    activeConfiguration,
                    visitedProjects,
                    metadataReferencePaths);
            }
        }

        private static async Task CollectReferencedProjectAssembliesAsync(
            WorkspacesExtensibility workspace,
            IProjectSnapshot project,
            string projectPath,
            string activeConfiguration,
            HashSet<string> visitedProjects,
            HashSet<string> metadataReferencePaths,
            CancellationToken cancellationToken)
        {
            if (!visitedProjects.Add(NormalizePath(projectPath)))
                return;

            var referencedProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await foreach (var reference in project.ProjectReferences
                               .With(reference => reference.ReferencedProjectPath)
                               .QueryAsync(cancellationToken))
            {
                var referencedProjectPath = reference.Value.ReferencedProjectPath;
                if (!string.IsNullOrWhiteSpace(referencedProjectPath))
                {
                    referencedProjectPaths.Add(ResolveProjectPath(projectPath, referencedProjectPath));
                }
            }

            foreach (var referencedProjectPath in GetProjectReferencesFromCsproj(projectPath))
            {
                referencedProjectPaths.Add(referencedProjectPath);
            }

            foreach (var referencedProjectPath in referencedProjectPaths)
            {
                TryAddBuiltProjectReferenceWithDependencies(
                    referencedProjectPath,
                    activeConfiguration,
                    metadataReferencePaths);

                var referencedProjects = await workspace.QueryProjectByPathAsync(
                    snapshot => snapshot.With(snapshot => snapshot.Path),
                    referencedProjectPath,
                    cancellationToken);

                foreach (var referencedProject in referencedProjects)
                {
                    if (string.IsNullOrWhiteSpace(referencedProject.Path))
                        continue;

                    await CollectReferencedProjectAssembliesAsync(
                        workspace,
                        referencedProject,
                        referencedProject.Path,
                        activeConfiguration,
                        visitedProjects,
                        metadataReferencePaths,
                        cancellationToken);
                }
            }
        }

        private static async Task CollectResolvedAssemblyReferencesAsync(
            IProjectSnapshot project,
            HashSet<string> metadataReferencePaths,
            CancellationToken cancellationToken)
        {
            await foreach (var configuration in project.ActiveConfigurations
                               .With(configuration => configuration.AssemblyReferences
                                   .With(reference => reference.Path)
                                   .With(reference => reference.Resolved))
                               .QueryAsync(cancellationToken))
            {
                foreach (var assemblyReference in configuration.Value.AssemblyReferences)
                {
                    if (!assemblyReference.Resolved)
                        continue;

                    var referencePath = assemblyReference.Path;
                    if (string.IsNullOrWhiteSpace(referencePath) || !File.Exists(referencePath))
                        continue;

                    metadataReferencePaths.Add(NormalizePath(referencePath));
                }
            }
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

        private static void TryAddBuiltProjectReferenceWithDependencies(
            string projectPath,
            string activeConfiguration,
            HashSet<string> metadataReferences)
        {
            var outputAssemblyPath = TryResolveOutputAssemblyPath(projectPath, activeConfiguration);
            if (string.IsNullOrWhiteSpace(outputAssemblyPath) || !File.Exists(outputAssemblyPath))
                return;

            metadataReferences.Add(NormalizePath(outputAssemblyPath));

            var outputDirectory = Path.GetDirectoryName(outputAssemblyPath);
            if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
                return;

            foreach (var dependencyPath in Directory.EnumerateFiles(outputDirectory, "*.dll"))
            {
                metadataReferences.Add(NormalizePath(dependencyPath));
            }
        }

        private static string? TryResolveOutputAssemblyPath(string projectPath, string activeConfiguration)
        {
            var resolvedProjectPath = ResolveProjectPath(projectPath, projectPath);
            if (!File.Exists(resolvedProjectPath))
                return null;

            try
            {
                var projectDirectory = Path.GetDirectoryName(resolvedProjectPath);
                if (string.IsNullOrEmpty(projectDirectory))
                    return null;

                var projectDocument = XDocument.Load(resolvedProjectPath);
                var root = projectDocument.Root;
                if (root is null)
                    return null;

                XNamespace msbuildNamespace = root.Name.Namespace;

                var assemblyName = root
                    .Descendants(msbuildNamespace + "AssemblyName")
                    .Select(element => element.Value.Trim())
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

                if (string.IsNullOrWhiteSpace(assemblyName))
                {
                    assemblyName = Path.GetFileNameWithoutExtension(resolvedProjectPath);
                }

                var targetFrameworks = root
                    .Descendants(msbuildNamespace + "TargetFramework")
                    .Select(element => element.Value.Trim())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Concat(root
                        .Descendants(msbuildNamespace + "TargetFrameworks")
                        .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (targetFrameworks.Count == 0)
                {
                    targetFrameworks.Add("net10.0");
                    targetFrameworks.Add("net8.0");
                }

                var configurations = new[] { activeConfiguration, "Debug", "Release" }
                    .Where(configuration => !string.IsNullOrWhiteSpace(configuration))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                foreach (var configuration in configurations)
                {
                    foreach (var targetFramework in targetFrameworks)
                    {
                        var candidates = new[]
                        {
                            Path.Combine(projectDirectory, "bin", configuration, targetFramework, assemblyName + ".dll"),
                            Path.Combine(projectDirectory, "obj", configuration, targetFramework, "ref", assemblyName + ".dll"),
                            Path.Combine(projectDirectory, "obj", configuration, targetFramework, "refint", assemblyName + ".dll"),
                        };

                        foreach (var candidate in candidates)
                        {
                            if (File.Exists(candidate))
                                return candidate;
                        }
                    }

                    var legacyOutput = Path.Combine(
                        projectDirectory,
                        "bin",
                        configuration,
                        assemblyName + ".dll");

                    if (File.Exists(legacyOutput))
                        return legacyOutput;
                }

                var binDirectory = Path.Combine(projectDirectory, "bin");
                if (Directory.Exists(binDirectory))
                {
                    var discoveredAssembly = Directory
                        .EnumerateFiles(binDirectory, assemblyName + ".dll", SearchOption.AllDirectories)
                        .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                        .FirstOrDefault();

                    if (discoveredAssembly is not null)
                        return discoveredAssembly;
                }

                var objDirectory = Path.Combine(projectDirectory, "obj");
                if (Directory.Exists(objDirectory))
                {
                    var discoveredReferenceAssembly = Directory
                        .EnumerateFiles(objDirectory, assemblyName + ".dll", SearchOption.AllDirectories)
                        .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                        .FirstOrDefault();

                    if (discoveredReferenceAssembly is not null)
                        return discoveredReferenceAssembly;
                }

                return null;
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

        private static string ResolveProjectPath(string baseProjectPath, string referencedProjectPath)
        {
            if (Path.IsPathRooted(referencedProjectPath))
                return NormalizePath(referencedProjectPath);

            var baseDirectory = Path.GetDirectoryName(baseProjectPath);
            if (string.IsNullOrEmpty(baseDirectory))
                return NormalizePath(referencedProjectPath);

            var combinedPath = Path.Combine(baseDirectory, referencedProjectPath);
            if (!combinedPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && !combinedPath.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                && !combinedPath.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            {
                var csprojCandidate = combinedPath + ".csproj";
                if (File.Exists(csprojCandidate))
                    return NormalizePath(csprojCandidate);
            }

            return NormalizePath(combinedPath);
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path);
        }

        private static MetadataReference[] GetMetadataReferences()
        {
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedAssemblies
                && !string.IsNullOrWhiteSpace(trustedAssemblies))
            {
                return trustedAssemblies
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                    .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    .Select(path => MetadataReference.CreateFromFile(path))
                    .ToArray();
            }

            return
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            ];
        }
    }
}
