using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>
/// Checks each patch against Umbraco's source maps: the bundle is built from the sources the patch names, and every
/// match of an edit was generated from the line the edit names. Failures say what changed upstream.
/// </summary>
public sealed class PatchOriginTests(AssetPatchTests.Assets assets) : IClassFixture<AssetPatchTests.Assets>
{
    /// <summary>The path of every patch, one test case each.</summary>
    public static TheoryData<string> Patches => new(BackOfficeAssetPatches.Patches.Select(patch => patch.Path));

    /// <summary>
    /// Checks that the patch for <paramref name="path"/> finds one file built from the source the patch names, and that
    /// every match of each edit was generated from the source line the edit names. A hashed bundle must name a source,
    /// and only a file without a source map may have edits that name no line.
    /// </summary>
    [Theory]
    [MemberData(nameof(Patches))]
    public async Task Patch_changes_the_Umbraco_source_it_names(string path)
    {
        var patch = BackOfficeAssetPatches.Patches.Single(patch => patch.Path == path);
        var files = AssetPatcher.Locate(assets.Files, patch);
        if (files.Length != 1)
            Assert.Fail($"Expected one Umbraco file for {path}, found {files.Length}. {await WhereSourcesAreAsync(patch)}");
        var (file, info) = files[0];
        var generated = await ReadAsync(info);
        var map = await SourceMap.ReadAsync(assets.Files.GetFileInfo(file + ".map"));

        if (patch.Source is { } source && map?.Sources.Contains(source) != true)
            Assert.Fail($"{file} is no longer built from {source}. {await WhereSourcesAreAsync(patch)}");
        if (map is not null && path.Contains(AssetPatcher.HashPattern, StringComparison.Ordinal))
            Assert.True(patch.Source is not null || patch.Edits.Any(edit => edit.Origin is not null),
                $"{path} must name a source, so that tests can find the bundle when Umbraco renames it.");

        foreach (var edit in patch.Edits)
        {
            if (edit.Origin is not { } origin)
            {
                Assert.True(map is null, $"{path}: {edit} must name the line of Umbraco's source it changes.");
                continue;
            }

            var matches = edit.Pattern.Matches(generated);
            if (matches.Count == 0)
                Assert.Fail($"{path}: {edit} no longer matches. {Triage(map, file, generated, origin)}");
            foreach (Match match in matches)
            {
                var covered = CoveredLines(map, file, generated, match).Distinct().ToArray();
                Assert.True(covered.Any(line => line.File == origin.File && line.Text.Contains(origin.Code, StringComparison.Ordinal)),
                    $"{path}: {edit} was generated from\n  {string.Join("\n  ", covered.Select(line => $"{line.File}: {line.Text.Trim()}"))}\n"
                    + $"not from {origin.File}: {origin.Code}");
            }
        }
    }

    /// <summary>
    /// Returns the original lines that produced <paramref name="match"/>. Without a source map, the file is its own
    /// source, so returns the generated lines the match spans.
    /// </summary>
    private static IEnumerable<(string File, string Text)> CoveredLines(SourceMap? map, string file, string generated, Match match)
    {
        if (map is null)
        {
            var first = match.Index == 0 ? 0 : generated.LastIndexOf('\n', match.Index - 1) + 1;
            var last = generated.IndexOf('\n', match.Index + match.Length);
            foreach (var line in generated[first..(last < 0 ? generated.Length : last)].Split('\n'))
                yield return (file, line);
            yield break;
        }
        foreach (var (source, line) in map.OriginalLines(generated, match.Index, match.Index + match.Length))
            yield return (map.Sources[source], map.Line(source, line) ?? "");
    }

    /// <summary>
    /// Returns a message saying whether only the minified output changed, so the pattern needs updating, or Umbraco
    /// changed the code that <paramref name="origin"/> names, so the patch needs another look.
    /// </summary>
    private static string Triage(SourceMap? map, string file, string generated, SourceLine origin)
    {
        if (map is null)
            return generated.Contains(origin.Code, StringComparison.Ordinal)
                ? $"{file} still contains {origin.Code}, so the pattern needs updating."
                : $"{file} no longer contains {origin.Code}: Umbraco changed this code, so check whether the patch is still needed.";
        var source = Array.IndexOf(map.Sources, origin.File);
        if (source < 0)
            return $"The bundle is no longer built from {origin.File}.";
        return map.Find(source, origin.Code) is { } line
            ? $"{origin.File}:{line.Number} still reads \"{line.Text}\", so only the minified output changed: update the pattern."
            : $"{origin.File} no longer contains {origin.Code}: Umbraco changed this code, so check whether the patch is still needed.";
    }

    /// <summary>
    /// Returns a message naming the bundles now built from the sources <paramref name="patch"/> names, which locates a
    /// file that Umbraco renamed or regrouped.
    /// </summary>
    private async Task<string> WhereSourcesAreAsync(AssetPatch patch)
    {
        var sources = patch.Edits.Select(edit => edit.Origin?.File).Append(patch.Source).OfType<string>().ToHashSet();
        if (sources.Count == 0)
            return "The patch names no source to search for.";
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, _) in assets.Enumerate("/umbraco"))
        {
            if (!path.EndsWith(".js", StringComparison.Ordinal)) continue;
            if (sources.Contains(path) || (await SourceMap.ReadAsync(assets.Files.GetFileInfo(path + ".map")))?.Sources.Any(sources.Contains) == true)
                found.Add(AssetPatcher.NormalizePath(path));
        }
        return found.Count == 0
            ? $"No bundle is built from {string.Join(", ", sources)} any more."
            : $"{string.Join(", ", sources)} now builds {string.Join(", ", found)}.";
    }

    /// <summary>Reads <paramref name="file"/> as text.</summary>
    private static async Task<string> ReadAsync(IFileInfo file)
    {
        using var reader = new StreamReader(file.CreateReadStream());
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
