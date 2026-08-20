using Microsoft.VisualStudio.Extensibility;
using System;

namespace Madoka.CSharpCodeAssister
{
    [VisualStudioContribution]
    internal class ExtensionEntrypoint : Extension
    {
        public override ExtensionConfiguration ExtensionConfiguration => new()
        {
            RequiresInProcessHosting = false,
            Metadata = new ExtensionMetadata(
                "Madoka.CSharpCodeAssister.8a7e4c5d-3f12-4a9b-b8c6-d5e1f2a3b4c0",
                new Version(1, 6, 0),
                "Aymadoka",
                "Madoka C# Code Assister",
                "C# class member utilities: format XML doc comments, adjust property setters, add constructors, remove validation attributes, and generate CreateFrom mappings.")
            {
                InstallationTargetVersion = "[17.14,18.0)",
                Tags = new[] { "csharp", "refactoring", "documentation", "properties", "formatting" },
            },
        };
    }
}
