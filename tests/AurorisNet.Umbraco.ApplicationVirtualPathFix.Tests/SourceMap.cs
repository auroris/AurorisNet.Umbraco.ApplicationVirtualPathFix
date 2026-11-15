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

    private SourceMap(string[] sources, string?[] contents, string mappings)
    {
        Sources = sources;
        Contents = contents;
        _mappings = mappings;
    }

    /// <summary>The sources as the map names them, without leading parent segments, such as <c>src/packages/...</c>.</summary>
    public string[] Sources { get; }

    public string?[] Contents { get; }

    public static async Task<SourceMap?> ReadAsync(IFileInfo file)
    {
        if (!file.Exists) return null;
        await using var stream = file.CreateReadStream();
        var json = await JsonSerializer.DeserializeAsync<Json>(stream, JsonSerializerOptions.Web, TestContext.Current.CancellationToken);
        return new SourceMap(json!.Sources.Select(Normalize).ToArray(), json.SourcesContent ?? new string?[json.Sources.Length], json.Mappings ?? "");
    }

    public static string Normalize(string source)
    {
        while (source.StartsWith("../", StringComparison.Ordinal))
            source = source[3..];
        return source;
    }

    /// <summary>A line of a source's text, or null when the map does not include it.</summary>
    public string? Line(int source, int line)
    {
        if (!_lines.TryGetValue(source, out var lines))
            _lines[source] = lines = Contents[source]?.Split('\n').Select(text => text.TrimEnd('\r')).ToArray() ?? [];
        return line < lines.Length ? lines[line] : null;
    }

    /// <summary>The first line of a source that contains <paramref name="code"/>, with its one-based number.</summary>
    public (int Number, string Text)? Find(int source, string code)
    {
        for (var line = 0; Line(source, line) is { } text; line++)
            if (text.Contains(code, StringComparison.Ordinal)) return (line + 1, text.Trim());
        return null;
    }

    /// <summary>The original lines, as (source index, zero-based line), that produced generated code from start up to end.</summary>
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

                // Fields are relative to the previous segment; one without a source maps nothing.
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

    private readonly record struct Segment(int Column, int Source, int OriginalLine);

    private sealed record Json(string[] Sources, string?[]? SourcesContent, string? Mappings);
}
