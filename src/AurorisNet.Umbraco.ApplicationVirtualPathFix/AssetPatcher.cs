using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix;

/// <summary>Finds the files that <see cref="BackOfficeAssetPatches.Patches"/> lists and applies their changes.</summary>
internal static partial class AssetPatcher
{
    /// <summary>The root of Umbraco's backoffice assets in the web root.</summary>
    internal const string AssetsRoot = "/umbraco/backoffice";

    /// <summary>Stands for the eight-character build hash in a bundle's file name, as in <c>index-????????.js</c>.</summary>
    internal const string HashPattern = "-????????";

    /// <summary>
    /// Returns the patch for the web root file at <paramref name="path"/>, whatever its build hash, or
    /// <see langword="null"/> when the package serves the file unchanged.
    /// </summary>
    internal static AssetPatch? For(string path)
        => Array.Find(BackOfficeAssetPatches.Patches, patch => string.Equals(patch.Path, NormalizePath(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds the files in <paramref name="files"/> that <paramref name="patch"/> applies to, with their paths. A path
    /// containing <see cref="HashPattern"/> matches any build hash. A correct patch finds exactly one file.
    /// </summary>
    internal static (string Path, IFileInfo File)[] Locate(IFileProvider files, AssetPatch patch)
    {
        if (!patch.Path.Contains(HashPattern, StringComparison.Ordinal))
            return files.GetFileInfo(patch.Path) is { Exists: true, IsDirectory: false } file ? [(patch.Path, file)] : [];
        var directory = patch.Path[..patch.Path.LastIndexOf('/')];
        return files.GetDirectoryContents(directory)
            .Where(file => !file.IsDirectory && string.Equals(NormalizePath(directory + "/" + file.Name), patch.Path, StringComparison.OrdinalIgnoreCase))
            .Select(file => (directory + "/" + file.Name, file)).ToArray();
    }

    /// <summary>
    /// Replaces a bundle's build hash in <paramref name="path"/> with <see cref="HashPattern"/>. Only an eight-character
    /// hash before <c>.js</c> matches, so a stable file and a hashed chunk with the same stem keep different paths.
    /// </summary>
    internal static string NormalizePath(string path) => BundleHash().Replace(path, HashPattern);

    /// <summary>
    /// Applies <paramref name="patch"/> to <paramref name="source"/> for the paths in <paramref name="context"/>: moves
    /// the asset URLs if the patch says to, makes each edit and adds the preamble. A change that cannot be applied is
    /// skipped and reported to <paramref name="unapplied"/> with the file's path and the reason.
    /// </summary>
    internal static string Patch(AssetPatch patch, string source, PatchContext context, Action<string, string> unapplied)
    {
        if (patch.MovesAssetUrls)
        {
            // The new root lands in JavaScript strings and HTML template attributes. Percent-encoding quotes,
            // backticks and ampersands keeps unusual application paths valid in both.
            var root = new PathString(context.AssetsPath).ToUriComponent().Replace("'", "%27", StringComparison.Ordinal)
                .Replace("`", "%60", StringComparison.Ordinal).Replace("&", "%26", StringComparison.Ordinal) + "/";
            var matched = false;
            source = QuotedAssetRoot().Replace(source, match =>
            {
                matched = true;
                return match.Value[..1] + root;
            });
            if (!matched) unapplied(patch.Path, "no longer contains " + QuotedAssetRoot());
        }

        // @prefix and @backOfficePath insert the site's paths as JavaScript string literals.
        var values = new Dictionary<string, string>
        {
            ["prefix"] = JsonSerializer.Serialize(context.PathBase),
            ["backOfficePath"] = JsonSerializer.Serialize(context.BackOfficePath),
        };
        foreach (var edit in patch.Edits)
            source = Apply(source, patch.Path, edit, unapplied, values);
        return patch.Preamble is { } preamble ? Expand(preamble, null, values, preamble) + " " + source : source;
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to <paramref name="source"/> when its pattern matches the expected number of
    /// times. Otherwise returns the source unchanged and passes <paramref name="unapplied"/> the resource and a reason
    /// that completes "Umbraco's {resource} ...". <paramref name="values"/> supplies the placeholders, such as
    /// <c>@prefix</c>, that the pattern does not capture.
    /// </summary>
    internal static string Apply(string source, string resource, AssetEdit edit, Action<string, string> unapplied,
        IReadOnlyDictionary<string, string>? values = null)
    {
        var found = edit.Pattern.Count(source);
        if (found == 0 || (edit.Count != AssetEdit.Every && found != edit.Count))
        {
            unapplied(resource, found == 0
                ? "no longer contains " + edit.Find
                : $"matches {edit.Find} {found} times instead of {edit.Count}");
            return source;
        }
        return edit.Pattern.Replace(source, match => Expand(edit.Replace, match, values, edit.Find));
    }

    /// <summary>
    /// Converts an edit's <paramref name="find"/> code to a regular expression. A placeholder's first use captures one
    /// whole identifier or <c>#private</c> name, a repeat must match the same text, and <c>@@</c> matches <c>@</c>.
    /// </summary>
    internal static Regex Compile(string find)
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var pattern = Placeholder().Replace(Regex.Escape(find.ReplaceLineEndings("\n")), match =>
        {
            if (match.Value == "@@") return "@";
            var name = match.Groups[1].Value;
            // Each placeholder matches a whole identifier. One that starts the pattern must not follow a dot either,
            // which would make it a property name.
            var before = match.Index == 0 ? @"(?<![\w$#.])" : @"(?<![\w$#])";
            return before + (defined.Add(name) ? $@"(?<{name}>#?[A-Za-z_$][\w$]*)" : $@"\k<{name}>") + @"(?![\w$])";
        });
        return new Regex(pattern, RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Replaces each placeholder in <paramref name="template"/> with the identifier <paramref name="match"/> captured
    /// for it or its value in <paramref name="values"/>, and each <c>@@</c> with <c>@</c>. Throws
    /// <see cref="InvalidOperationException"/>, naming <paramref name="pattern"/>, for a placeholder that has neither.
    /// </summary>
    /// <remarks>
    /// Uses an evaluator because a .NET replacement string treats <c>${</c> in JavaScript as a substitution.
    /// </remarks>
    private static string Expand(string template, Match? match, IReadOnlyDictionary<string, string>? values, string pattern)
        => Placeholder().Replace(template.ReplaceLineEndings("\n"), placeholder =>
        {
            if (placeholder.Value == "@@") return "@";
            var name = placeholder.Groups[1].Value;
            return match?.Groups[name] is { Success: true } group ? group.Value
                : values?.GetValueOrDefault(name) ?? throw new InvalidOperationException($"{pattern} does not define {placeholder.Value}.");
        });

    /// <summary>Matches a bundle's build hash: a hyphen and eight characters before <c>.js</c> at the end of the path.</summary>
    [GeneratedRegex(@"-[A-Za-z0-9_-]{8}(?=\.js$)", RegexOptions.CultureInvariant)]
    private static partial Regex BundleHash();

    /// <summary>
    /// Matches <c>/umbraco/backoffice/</c> after an opening quote or backtick. Requiring the quote skips prose, such as
    /// the comment in the theme module that mentions the asset path.
    /// </summary>
    [GeneratedRegex("""["'`]/umbraco/backoffice/""", RegexOptions.CultureInvariant)]
    internal static partial Regex QuotedAssetRoot();

    /// <summary>Matches <c>@@</c>, or a placeholder such as <c>@serverUrl</c> and captures its name.</summary>
    [GeneratedRegex(@"@@|@([A-Za-z]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}

/// <summary>The site's paths that patched files refer to.</summary>
/// <param name="PathBase">The application virtual path, such as <c>/portal</c>.</param>
/// <param name="BackOfficePath">The backoffice path, virtual path included, such as <c>/portal/umbraco</c>.</param>
/// <param name="AssetsPath">The cache-busted assets path, such as <c>/portal/umbraco/backoffice/{hash}</c>.</param>
internal sealed record PatchContext(string PathBase, string BackOfficePath, string AssetsPath);
