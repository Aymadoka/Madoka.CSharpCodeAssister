using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace Madoka.CSharpCodeAssister
{
    [VisualStudioContribution]
    internal class MakePropertiesPublicSetCommand : Command
    {
        private static readonly Guid TextEditorContext = new("5EFC7975-14BC-11CF-9B2B-00AA00573819");

        public override CommandConfiguration CommandConfiguration => new("%MakePropertiesPublicSetCommand.DisplayName%")
        {
            TooltipText = "%MakePropertiesPublicSetCommand.ToolTipText%",
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
                    Key.D3,
                    TextEditorContext),
            },
            EnabledWhen = ActivationConstraint.ClientContext(
                ClientContextKey.Shell.ActiveEditorContentType,
                "CSharp")
        };

        public MakePropertiesPublicSetCommand(VisualStudioExtensibility extensibility)
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
            var convertedText = PropertyPublicSetterConverter.TryConvert(originalText, caretPosition);
            if (convertedText is null || convertedText == originalText)
                return;

            var fullRange = new TextRange(
                new TextPosition(document, 0),
                document.Length);

            await Extensibility.Editor().EditAsync(
                batch =>
                {
                    var editor = document.AsEditable(batch);
                    editor.Replace(fullRange, convertedText);
                },
                cancellationToken);
        }
    }
}
