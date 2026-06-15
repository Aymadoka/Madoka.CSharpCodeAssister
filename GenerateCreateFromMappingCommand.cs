using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Madoka.CSharpCodeAssister
{
    [VisualStudioContribution]
    internal class GenerateCreateFromMappingCommand : Command
    {
        private static readonly Guid TextEditorContext = new("5EFC7975-14BC-11CF-9B2B-00AA00573819");

        public override CommandConfiguration CommandConfiguration => new("%GenerateCreateFromMappingCommand.DisplayName%")
        {
            TooltipText = "%GenerateCreateFromMappingCommand.ToolTipText%",
            Placements = new[]
            {
                CommandPlacement.KnownPlacements.ExtensionsMenu,
            },
            Shortcuts = new CommandShortcutConfiguration[]
            {
                new(
                    ModifierKey.Control,
                    Key.D1,
                    ModifierKey.Control,
                    Key.D6,
                    TextEditorContext),
            },
            EnabledWhen = ActivationConstraint.ClientContext(
                ClientContextKey.Shell.ActiveEditorContentType,
                "CSharp")
        };

        public GenerateCreateFromMappingCommand(VisualStudioExtensibility extensibility)
            : base(extensibility)
        {
        }

        public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        {
            try
            {
                var textView = await Extensibility.Editor().GetActiveTextViewAsync(context, cancellationToken);
                if (textView is null)
                    return;

                var document = textView.Document;
                if (document is null)
                    return;

                var originalText = document.Text.CopyToString();
                var caretPosition = textView.Selection.Extent.Start.Offset;
                var documentPath = document.Uri?.LocalPath;
                if (string.IsNullOrEmpty(documentPath))
                {
                    await ShowFailureAsync(
                        "Could not determine the file path. Save the document to disk and try again.",
                        cancellationToken);
                    return;
                }

                var sourceContext = await CreateFromSourceFileResolver.TryResolveAsync(
                    originalText,
                    caretPosition,
                    documentPath,
                    cancellationToken);

                var additionalSyntaxRoots = sourceContext.AdditionalSyntaxTrees
                    .Select(tree => tree.GetCompilationUnitRoot())
                    .ToList();

                var attempt = CreateFromMappingGenerator.TryGenerateWithDiagnostics(
                    originalText,
                    caretPosition,
                    semanticModel: null,
                    additionalSyntaxRoots);

                if (attempt.Result is null)
                {
                    SemanticModel semanticModel = null;
                    var compilation = await CreateFromProjectCompilationBuilder.TryCreateAsync(
                        Extensibility,
                        context,
                        originalText,
                        documentPath,
                        sourceContext.AdditionalSyntaxTrees,
                        cancellationToken);

                    if (compilation is not null)
                    {
                        var normalizedDocumentPath = Path.GetFullPath(documentPath);
                        var existingTree = compilation.SyntaxTrees.FirstOrDefault(tree =>
                            tree.FilePath is not null
                            && string.Equals(Path.GetFullPath(tree.FilePath), normalizedDocumentPath, StringComparison.OrdinalIgnoreCase))
                            ?? compilation.SyntaxTrees.First();

                        var syntaxTree = existingTree.WithChangedText(SourceText.From(originalText));
                        if (!ReferenceEquals(existingTree, syntaxTree))
                        {
                            compilation = compilation.ReplaceSyntaxTree(existingTree, syntaxTree);
                        }

                        semanticModel = compilation.GetSemanticModel(syntaxTree);
                    }

                    attempt = CreateFromMappingGenerator.TryGenerateWithDiagnostics(
                        originalText,
                        caretPosition,
                        semanticModel,
                        additionalSyntaxRoots);
                }

                if (attempt.Result is null)
                {
                    await ShowFailureAsync(
                        attempt.FailureMessage ?? "Could not generate CreateFrom mapping. Place the caret inside an empty static CreateFrom method body.",
                        cancellationToken);
                    return;
                }

                var result = attempt.Result;
                var replaceRange = new TextRange(
                    new TextPosition(document, result.ReplaceStart),
                    new TextPosition(document, result.ReplaceStart + result.ReplaceLength));

                await Extensibility.Editor().EditAsync(
                    batch =>
                    {
                        var editor = document.AsEditable(batch);
                        editor.Replace(replaceRange, result.ReplacementText);
                    },
                    cancellationToken);
            }
            catch (Exception exception)
            {
                await ShowFailureAsync(
                    string.Format(
                        System.Globalization.CultureInfo.CurrentCulture,
                        "CreateFrom mapping failed: {0}",
                        exception.Message),
                    cancellationToken);
            }
        }

        private async Task ShowFailureAsync(string message, CancellationToken cancellationToken)
        {
            await Extensibility.Shell().ShowPromptAsync(message, PromptOptions.OK, cancellationToken);
        }
    }
}
