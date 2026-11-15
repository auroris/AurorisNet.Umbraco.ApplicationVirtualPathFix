using System.Text.Json;
using Microsoft.Extensions.FileProviders;

namespace AurorisNet.Umbraco.ApplicationVirtualPathFix.Tests;

/// <summary>A bundle's source map: Umbraco's sources, their text, and which original lines produced generated code.</summary>
internal sealed class SourceMap
{
    private const string Base64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private readonly string _mappings;
    private readonly Dictionary<int, string[]> _lines = [];
    private List<Segment[]>? _segments;

    /// <summary>
    /// Creates a map from its normalized sources, their contents and its encoded mappings, which
    /// <see cref="OriginalLines"/> decodes when it first needs them.
    /// </summary>
    private SourceMap(string[] sources, string?[] contents, string mappings)
    {
        Sources = sources;
        Contents = contents;
        _mappings = mappings;
    }

    /// <summary>
    /// The sources as the map names them, with leading <c>../</c> segments removed, such as <c>src/packages/...</c>.
    /// </summary>
    public string[] Sources { get; }

    public string?[] Contents { get; }

    /// <summary>
    /// Reads the source map in <paramref name="file"/>, or returns <see langword="null"/> when the file is missing.
    /// </summary>
    public static async Task<SourceMap?> ReadAsync(IFileInfo file)
    {
        if (!file.Exists) return null;
        await using var stream = file.CreateReadStream();
        var json = await JsonSerializer.DeserializeAsync<Json>(stream, JsonSerializerOptions.Web, TestContext.Current.CancellationToken);
        return new SourceMap(json!.Sources.Select(Normalize).ToArray(), json.SourcesContent ?? new string?[json.Sources.Length], json.Mappings ?? "");
    }

    /// <summary>
    /// Removes leading <c>../</c> segments from <paramref name="source"/>, so it matches the paths that patches name.
    /// </summary>
    public static string Normalize(string source)
    {
        while (source.StartsWith("../", StringComparison.Ordinal))
            source = source[3..];
        return source;
    }

    /// <summary>
    /// Returns zero-based line <paramref name="line"/> of source <paramref name="source"/>, or <see langword="null"/>
    /// when the map lacks that source's text or the line.
    /// </summary>
    public string? Line(int source, int line)
    {
        if (!_lines.TryGetValue(source, out var lines))
            _lines[source] = lines = Contents[source]?.Split('\n').Select(text => text.TrimEnd('\r')).ToArray() ?? [];
        return line < lines.Length ? lines[line] : null;
    }

    /// <summary>
    /// Returns the first line of source <paramref name="source"/> that contains <paramref name="code"/>, trimmed, with
    /// its one-based number, or <see langword="null"/> when no line does.
    /// </summary>
    public (int Number, string Text)? Find(int source, string code)
    {
        for (var line = 0; Line(source, line) is { } text; line++)
            if (text.Contains(code, StringComparison.Ordinal)) return (line + 1, text.Trim());
        return null;
    }

    /// <summary>
    /// Returns the original lines, as a source index and a zero-based line, that produced the code in
    /// <paramref name="generated"/> from offset <paramref name="start"/> up to <paramref name="end"/>.
    /// </summary>
    public IEnumerable<(int Source, int Line)> OriginalLines(string generated, int start, int end)
    {
        _segments ??= Decode(_mappings);
        var (startLine, startColumn) = Position(generated, start);
        var (endLine, endColumn) = Position(generated, end);
        for (var line = startLine; line <= endLine && line < _segments.Count; line++)
        {
            var segments = _segments[line];
            for (var index = 0; index < segments.Length; index++)
            {
                // A segment covers generated code from its column up to the next segment's.
                var next = index + 1 < segments.Length ? segments[index + 1].Column : int.MaxValue;
                if ((line > startLine || next > startColumn) && (line < endLine || segments[index].Column < endColumn))
                    yield return (segments[index].Source, segments[index].OriginalLine);
            }
        }
    }

    /// <summary>Converts <paramref name="offset"/> in <paramref name="text"/> to a zero-based line and column.</summary>
    private static (int Line, int Column) Position(string text, int offset)
    {
        var line = text.AsSpan(0, offset).Count('\n');
        return (line, offset - (offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1));
    }

    /// <summary>Decodes the Base64 VLQ mappings into each generated line's segments.</summary>
    private static List<Segment[]> Decode(string mappings)
    {
        var lines = new List<Segment[]>();
        var values = new List<int>(5);
        int source = 0, originalLine = 0, originalColumn = 0;
        foreach (var text in mappings.Split(';'))
        {
            var segments = new List<Segment>();
            var column = 0;
            foreach (var field in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                values.Clear();
                int value = 0, shift = 0;
                foreach (var character in field)
                {
                    var digit = Base64.IndexOf(character);
                    value += (digit & 31) << shift;
                    if ((digit & 32) != 0)
                    {
                        shift += 5;
                        continue;
                    }
                    values.Add((value & 1) == 1 ? -(value >> 1) : value >> 1);
                    value = shift = 0;
                }

                // Each field is relative to the previous segment's. A segment without a source maps nothing, so skip it.
                column += values[0];
                if (values.Count < 4) continue;
                source += values[1];
                originalLine += values[2];
                originalColumn += values[3];
                segments.Add(new Segment(column, source, originalLine));
            }
            lines.Add(segments.ToArray());
        }
        return lines;
    }

    /// <summary>
    /// A decoded mapping: generated code from <paramref name="Column"/> came from zero-based line
    /// <paramref name="OriginalLine"/> of source <paramref name="Source"/>.
    /// </summary>
    private readonly record struct Segment(int Column, int Source, int OriginalLine);

    /// <summary>The fields of a source map file that the tests read.</summary>
    private sealed record Json(string[] Sources, string?[]? SourcesContent, string? Mappings);
}
