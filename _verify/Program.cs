using Madoka.CSharpCodeAssister;
using Microsoft.CodeAnalysis.CSharp;

var responsePath = args.Length > 0
    ? args[0]
    : Environment.GetEnvironmentVariable("CREATE_FROM_TEST_FILE");

if (string.IsNullOrWhiteSpace(responsePath) || !File.Exists(responsePath))
{
    Console.Error.WriteLine("Usage: dotnet run -- <path-to-csharp-file>");
    Console.Error.WriteLine("Or set the CREATE_FROM_TEST_FILE environment variable.");
    return 1;
}

var responseSource = await File.ReadAllTextAsync(responsePath);
var caret = responseSource.IndexOf('{', responseSource.IndexOf("CreateFrom", StringComparison.Ordinal)) + 1;

var sourceContext = await CreateFromSourceFileResolver.TryResolveAsync(
    responseSource,
    caret,
    responsePath,
    CancellationToken.None);

Console.WriteLine($"Additional trees: {sourceContext.AdditionalSyntaxTrees.Count}");
foreach (var tree in sourceContext.AdditionalSyntaxTrees)
{
    Console.WriteLine($"  - {tree.FilePath}");
}

var additionalRoots = sourceContext.AdditionalSyntaxTrees
    .Select(tree => tree.GetCompilationUnitRoot())
    .ToList();

var attempt = CreateFromMappingGenerator.TryGenerateWithDiagnostics(
    responseSource,
    caret,
    semanticModel: null,
    additionalRoots);

Console.WriteLine(attempt.Result is null ? $"FAIL: {attempt.FailureMessage}" : $"OK: {attempt.Result.ReplacementText.Split('\n').Length} lines");
return 0;