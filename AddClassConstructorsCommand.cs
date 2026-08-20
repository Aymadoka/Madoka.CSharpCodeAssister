using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Madoka.CSharpCodeAssister
{
    [VisualStudioContribution]
    internal class AddClassConstructorsCommand : Command
    {
        private static readonly Guid TextEditorContext = new("5EFC7975-14BC-11CF-9B2B-00AA00573819");

        public override CommandConfiguration CommandConfiguration => new("%AddClassConstructorsCommand.DisplayName%")
        {
            TooltipText = "%AddClassConstructorsCommand.ToolTipText%",
            Placements = new[]
            {
                CommandPlacement.KnownPlacements.ExtensionsMenu,
            },
            Shortcuts = new CommandShortcutConfiguration[]
            {
                new(
                    ModifierKey.Control,
                    Key.D4,
                    ModifierKey.Control,
                    Key.D4,
                    TextEditorContext),
            },
            EnabledWhen = ActivationConstraint.ClientContext(
                ClientContextKey.Shell.ActiveEditorContentType,
                "CSharp")
        };

        public AddClassConstructorsCommand(VisualStudioExtensibility extensibility)
            : base(extensibility)
        {
        }

        public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        {
            var textView = await Extensibility.Editor().GetActiveTextViewAsync(context, cancellationToken);
            if (textView is null)
                return;

            var document = textView.Document;
            if (document is null)
                return;

            var originalText = document.Text.CopyToString();
            var caretPosition = textView.Selection.Extent.Start.Offset;
            var result = ClassConstructorGenerator.TryGenerate(originalText, caretPosition);
            if (result is null)
                return;

            var insertRange = new TextRange(
                new TextPosition(document, result.InsertPosition),
                new TextPosition(document, result.InsertPosition));

            await Extensibility.Editor().EditAsync(
                batch =>
                {
                    var editor = document.AsEditable(batch);
                    editor.Replace(insertRange, result.TextToInsert);
                },
                cancellationToken);
        }
    }
}
