using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tesserafin.Providers.Tests.ProviderAuth
{
    /// <summary>
    /// Compiles a synthetic single-file assembly into a disposable directory, so the
    /// provider-authentication audit can be red-checked against real compiler output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The controls have to be <em>compiled</em>, not hand-written into a fixture file, because the
    /// evasions this audit must defeat — concatenation, interpolation and fragment-splitting — are
    /// resolved by the C# compiler before they reach the assembly. A control that skipped the
    /// compiler would prove nothing about them.
    /// </para>
    /// <para>
    /// Every control's credential-shaped value is assembled at run time from fragments here, and
    /// the generated source and the assembly it produces exist only inside a temporary directory
    /// that is deleted when the test finishes. No credential-shaped literal is ever committed.
    /// </para>
    /// </remarks>
    public static class ControlFixtureCompiler
    {
        /// <summary>Compiles source into an assembly file inside <paramref name="directory"/>.</summary>
        /// <param name="directory">Disposable directory to emit into.</param>
        /// <param name="name">Assembly name; also the file stem.</param>
        /// <param name="source">The C# source to compile.</param>
        /// <returns>The path to the emitted assembly.</returns>
        public static string Compile(string directory, string name, string source)
        {
            ArgumentException.ThrowIfNullOrEmpty(directory);
            ArgumentException.ThrowIfNullOrEmpty(name);

            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var references = new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "netstandard.dll" }
                .Select(file => Path.Combine(runtimeDirectory, file))
                .Where(File.Exists)
                .Select(path => MetadataReference.CreateFromFile(path))
                .ToArray();

            var compilation = CSharpCompilation.Create(
                name,
                [CSharpSyntaxTree.ParseText(source)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

            var path = Path.Combine(directory, name + ".dll");
            var result = compilation.Emit(path);
            if (!result.Success)
            {
                var diagnostics = string.Join(
                    Environment.NewLine,
                    result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
                throw new InvalidOperationException($"control fixture '{name}' did not compile:{Environment.NewLine}{diagnostics}");
            }

            return path;
        }
    }
}
