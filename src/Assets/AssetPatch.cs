using System.Text.RegularExpressions;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>An Umbraco file the package corrects, and the changes it makes to that file.</summary>
/// <remarks><see cref="BackOfficeAssetPatches"/> explains how to read an entry.</remarks>
internal sealed class AssetPatch
{
    /// <summary>
    /// The file's path in the web root. <see cref="AssetPatcher.HashPattern"/> stands for the build hash in a bundle's
    /// name.
    /// </summary>
    internal required string Path { get; init; }

    /// <summary>
    /// The first Umbraco release the entry applies to, such as <c>18.2.0</c>. Without it, the entry applies from the
    /// oldest supported release.
    /// </summary>
    internal string? From { get; init; }

    /// <summary>
    /// The first Umbraco release the entry no longer applies to, such as <c>18.3.0</c>. Without it, the entry applies up
    /// to the newest supported release.
    /// </summary>
    internal string? Before { get; init; }

    /// <summary>
    /// Text the file must contain. It picks the right bundle when several share the name in <see cref="Path"/>, as
    /// Umbraco 17.5, 17.6, 18.0 and 18.1 each have two <c>constants-????????.js</c> chunks.
    /// </summary>
    internal string? Contains { get; init; }

    /// <summary>
    /// A source file the bundle is built from. The tests check it against the bundle's source map and use it to find
    /// the bundle again when Umbraco renames it.
    /// </summary>
    internal string? Source { get; init; }

    /// <summary>
    /// Moves the file's quoted <c>/umbraco/backoffice/</c> URLs under the site's cache-busted assets path before the
    /// edits apply.
    /// </summary>
    internal bool MovesAssetUrls { get; init; }

    /// <summary>The replacements, applied in order.</summary>
    internal AssetEdit[] Edits { get; init; } = [];

    /// <summary>Code placed at the start of the first line, followed by a space, so the file keeps its line numbers.</summary>
    internal string? Preamble { get; init; }

    /// <summary>Returns whether the entry applies to the Umbraco release <paramref name="umbraco"/>, such as 18.2.1.</summary>
    internal bool AppliesTo(Version umbraco)
        => (From is null || umbraco >= Version.Parse(From)) && (Before is null || umbraco < Version.Parse(Before));

    /// <summary>
    /// Returns the file's path, followed by the entry's releases when it has them, as in
    /// <c>/umbraco/backoffice/packages/block/chunk-????????.js [18.2.0, 18.3.0)</c>. It identifies the patch in test names
    /// and messages.
    /// </summary>
    public override string ToString() => From is null && Before is null ? Path : $"{Path} [{From}, {Before})";
}

/// <summary>Replaces code of a known shape when it occurs the expected number of times.</summary>
/// <remarks>
/// Umbraco's minified bundles rename variables between builds. A <c>@name</c> placeholder in <see cref="Find"/>
/// matches one whole identifier or <c>#private</c> name, and a repeated <c>@name</c> must match the same identifier.
/// <see cref="Replace"/> inserts those identifiers, or values supplied when the edit is applied, such as
/// <c>@prefix</c>. <c>@@</c> stands for a literal <c>@</c>, as in <c>@@umbraco-cms</c>. Line breaks in both count as
/// <c>\n</c>, so a checkout with CRLF line endings patches the same way.
/// </remarks>
internal sealed class AssetEdit
{
    /// <summary>A <see cref="Count"/> that expects at least one match and changes every match.</summary>
    internal const int Every = 0;

    private Regex? _pattern;

    /// <summary>The line of Umbraco's source this edit changes.</summary>
    internal SourceLine? Origin { get; init; }

    /// <summary>The code to find, with <c>@name</c> placeholders for minified identifiers.</summary>
    internal required string Find { get; init; }

    /// <summary>The replacement code, which can use the placeholders of <see cref="Find"/>.</summary>
    internal required string Replace { get; init; }

    /// <summary>The number of matches expected, or <see cref="Every"/>.</summary>
    internal int Count { get; init; } = 1;

    /// <summary>The regular expression compiled from <see cref="Find"/> on first use.</summary>
    internal Regex Pattern => _pattern ??= AssetPatcher.Compile(Find);

    /// <summary>Returns the code the edit finds, which identifies it in test names and messages.</summary>
    public override string ToString() => Find;
}

/// <summary>A line of Umbraco's source, identified by its file and a fragment of its code.</summary>
internal sealed class SourceLine
{
    /// <summary>
    /// The source file as the bundle's source map names it, or the served path of a file that ships without a map.
    /// </summary>
    internal required string File { get; init; }

    /// <summary>A fragment of the line's code.</summary>
    internal required string Code { get; init; }
}
