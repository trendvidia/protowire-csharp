// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Protowire.Pxf;
using Xunit.Abstractions;

namespace Protowire.Conformance;

/// <summary>
/// Holds this port to protowire-go on the vendored spec corpora and on this
/// project's own documents: the same verdict, the same PB, the same PXF written
/// back, and the same formatted text. The Go side is <c>tool/go_oracle</c>,
/// whose output <c>GO_ORACLE</c> names. Without it these tests pass vacuously,
/// so the CI job that runs them sets <c>REQUIRE_GO_ORACLE</c> too.
///
/// <para>
/// A difference fails unless <c>known-divergences.txt</c> lists it, and a listed
/// difference that has gone fails too.
/// </para>
/// </summary>
public class GoConformanceTests(ITestOutputHelper output)
{
    private static readonly string Root = AppContext.BaseDirectory;
    private static readonly string? OraclePath = Environment.GetEnvironmentVariable("GO_ORACLE");
    private static readonly Lazy<JsonElement> Oracle = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(OraclePath!)).RootElement);
    private static readonly Lazy<Dictionary<string, JsonElement>> Results = new(() =>
        Oracle.Value.GetProperty("results").EnumerateArray()
            .ToDictionary(r => r.GetProperty("doc").GetString()!));
    private static readonly Lazy<Dictionary<string, HashSet<string>>> Ledger = new(ReadLedger);
    private static readonly object ReportLock = new();

    public static IEnumerable<object[]> Documents()
    {
        if (OraclePath == null)
        {
            yield return ["(no oracle)"];
            yield break;
        }
        foreach (var doc in Results.Value.Keys.Order(StringComparer.Ordinal))
        {
            yield return [doc];
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Document(string doc)
    {
        if (OraclePath == null)
        {
            Assert.Null(Environment.GetEnvironmentVariable("REQUIRE_GO_ORACLE"));
            return;
        }
        var differences = Compare(doc, Results.Value[doc]);
        Report(doc, differences);

        Ledger.Value.TryGetValue(doc, out var listed);
        listed ??= [];
        var unlisted = differences.Keys.Where(a => !listed.Contains(a)).Select(a => $"{a}: {differences[a]}").ToArray();
        var gone = listed.Where(a => !differences.ContainsKey(a)).ToArray();
        foreach (var kv in differences)
        {
            output.WriteLine($"{kv.Key}: {kv.Value}");
        }
        Assert.True(unlisted.Length == 0, "differs from protowire-go: " + string.Join("\n", unlisted));
        Assert.True(gone.Length == 0, "known-divergences.txt lists what no longer differs: " + string.Join(", ", gone));
    }

    /// <summary>A document nothing compares is a gap that looks like a pass.</summary>
    [Fact]
    public void TheOracleCoversEveryDocument()
    {
        if (OraclePath == null) return;
        foreach (var dir in new[] { "testdata", "docs", "annotated" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, dir), "*.pxf", SearchOption.AllDirectories))
            {
                var doc = Path.GetRelativePath(Root, file).Replace('\\', '/');
                // Goldens for the .binpb beside them, which are what runs.
                if (doc.StartsWith("testdata/text/", StringComparison.Ordinal)) continue;
                Assert.True(Results.Value.ContainsKey(doc), $"no oracle result for {doc}");
            }
        }
    }

    [Fact]
    public void TheLedgerNamesOnlyDocumentsTheOracleHolds()
    {
        if (OraclePath == null) return;
        Assert.DoesNotContain(Ledger.Value.Keys, d => !Results.Value.ContainsKey(d));
    }

    private static Dictionary<string, string> Compare(string doc, JsonElement r)
    {
        var d = new Dictionary<string, string>();
        var path = Path.Combine(Root, doc);
        if (Get(r, "binary", false))
        {
            CompareBinary(path, r, d);
            return d;
        }
        var text = File.ReadAllText(path);
        CompareParse(text, r, d);
        if (r.TryGetProperty("message", out var message))
        {
            CompareDecode(text, message.GetString()!, r, d);
        }
        else
        {
            CompareRows(text, r, d);
        }
        return d;
    }

    private static void CompareParse(string text, JsonElement r, Dictionary<string, string> d)
    {
        Document? parsed = null;
        Exception? rejection = null;
        try
        {
            parsed = Parser.Parse(text);
        }
        catch (Exception e)
        {
            rejection = e;
        }
        var want = Get(r, "parsed", false);
        if (want != (parsed != null))
        {
            d["parse"] = want ? $"Go parses, C# refuses: {Line(rejection)}" : $"C# parses, Go: {Get(r, "parseError", "")}";
            return;
        }
        if (rejection != null && rejection is not PxfException)
        {
            d["crash"] = Line(rejection);
        }
        if (parsed != null && Format.FormatDocument(parsed) != Get(r, "formatted", ""))
        {
            d["format"] = "formatted text differs";
        }
    }

    private static void CompareDecode(string text, string name, JsonElement r, Dictionary<string, string> d)
    {
        if (!Corpus.ByName.TryGetValue(name, out var desc))
        {
            d["binding"] = $"no generated class for {name}";
            return;
        }
        var msg = desc.Parser.ParseFrom(ByteString.Empty);
        Exception? rejection = null;
        try
        {
            new Decoder { Registry = Corpus.Registry }.UnmarshalFull(text, msg);
        }
        catch (Exception e)
        {
            rejection = e;
        }
        var accepted = Get(r, "accepted", false);
        if (accepted != (rejection == null))
        {
            d["verdict"] = accepted ? $"Go accepts, C# refuses: {Line(rejection)}" : $"C# accepts, Go: {Get(r, "error", "")}";
            return;
        }
        if (rejection != null)
        {
            if (rejection is not PxfException) d["crash"] = Line(rejection);
            return;
        }
        ComparePb(msg, Bytes(r, "pb"), d);
        CompareText(msg, Get(r, "text", ""), d);
    }

    private static void CompareRows(string text, JsonElement r, Dictionary<string, string> d)
    {
        var want = r.TryGetProperty("rows", out var rows) ? rows.EnumerateArray().ToArray() : [];
        var got = new List<IMessage>();
        Exception? rejection = null;
        try
        {
            foreach (var ds in Parser.Parse(text).Datasets)
            {
                if (!Corpus.ByName.TryGetValue(ds.Type, out var desc))
                {
                    throw new PxfException(ds.Pos, $"unknown row type {ds.Type}");
                }
                foreach (var row in ds.Rows)
                {
                    var msg = desc.Parser.ParseFrom(ByteString.Empty);
                    new Decoder().BindRow(msg, ds.Columns, row);
                    got.Add(msg);
                }
            }
        }
        catch (Exception e)
        {
            rejection = e;
        }
        var accepted = Get(r, "accepted", false);
        if (accepted != (rejection == null))
        {
            d["verdict"] = accepted ? $"Go accepts, C# refuses: {Line(rejection)}" : $"C# accepts, Go: {Get(r, "error", "")}";
            return;
        }
        if (rejection != null)
        {
            if (rejection is not PxfException) d["crash"] = Line(rejection);
            return;
        }
        if (got.Count != want.Length)
        {
            d["rows"] = $"C# bound {got.Count} rows, Go {want.Length}";
            return;
        }
        for (int i = 0; i < got.Count; i++)
        {
            ComparePb(got[i], Bytes(want[i], "pb"), d);
            // The oracle writes rows with no resolver, so an Any stays as its fields.
            CompareText(got[i], Get(want[i], "text", ""), d, TypeRegistry.Empty);
        }
    }

    private static void CompareBinary(string path, JsonElement r, Dictionary<string, string> d)
    {
        var name = r.GetProperty("message").GetString()!;
        if (!Corpus.ByName.TryGetValue(name, out var desc))
        {
            d["binding"] = $"no generated class for {name}";
            return;
        }
        var msg = desc.Parser.ParseFrom(File.ReadAllBytes(path));
        if (Get(r, "accepted", false))
        {
            CompareText(msg, Get(r, "text", ""), d);
            return;
        }
        try
        {
            new Encoder { Registry = Corpus.Registry }.Marshal(msg);
            d["verdict"] = $"C# writes it, Go: {Get(r, "error", "")}";
        }
        catch (PxfException)
        {
        }
    }

    // Protobuf fixes neither map order nor where a oneof goes, so both sides
    // are read back and written deterministically before they are compared.
    private static void ComparePb(IMessage msg, byte[] want, Dictionary<string, string> d)
    {
        var theirs = msg.Descriptor.Parser.ParseFrom(want);
        if (!Deterministic(msg).SequenceEqual(Deterministic(theirs)))
        {
            d["pb"] = $"C# {Convert.ToHexString(Deterministic(msg))} Go {Convert.ToHexString(Deterministic(theirs))}";
        }
    }

    private static void CompareText(IMessage msg, string want, Dictionary<string, string> d, TypeRegistry? registry = null)
    {
        string got;
        try
        {
            got = new Encoder { Registry = registry ?? Corpus.Registry }.Marshal(msg);
        }
        catch (Exception e)
        {
            d["text"] = $"marshal threw: {Line(e)}";
            return;
        }
        if (got != want)
        {
            d["text"] = $"C#:\n{got}\nGo:\n{want}";
        }
    }

    private static byte[] Deterministic(IMessage msg)
    {
        using var ms = new MemoryStream();
        using (var output = new CodedOutputStream(ms, leaveOpen: true) { Deterministic = true })
        {
            msg.WriteTo(output);
        }
        return ms.ToArray();
    }

    private static Dictionary<string, HashSet<string>> ReadLedger()
    {
        var ledger = new Dictionary<string, HashSet<string>>();
        foreach (var line in File.ReadAllLines(Path.Combine(Root, "known-divergences.txt")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!ledger.TryGetValue(f[0], out var set)) ledger[f[0]] = set = [];
            set.Add(f[1]);
        }
        return ledger;
    }

    // CONFORMANCE_REPORT collects every difference in one file, in the form the
    // ledger takes, because a run stops being readable well before the ledger
    // does.
    private static void Report(string doc, Dictionary<string, string> differences)
    {
        var path = Environment.GetEnvironmentVariable("CONFORMANCE_REPORT");
        if (path == null) return;
        lock (ReportLock)
        {
            foreach (var kv in differences)
            {
                var detail = kv.Value.Replace('\n', ' ');
                File.AppendAllText(path, $"{doc} {kv.Key} {detail[..Math.Min(detail.Length, 300)]}\n");
            }
        }
    }

    private static string Line(Exception? e) => e == null ? "" : $"[{e.GetType().Name}] {e.Message}";

    private static bool Get(JsonElement r, string name, bool fallback) =>
        r.TryGetProperty(name, out var v) ? v.GetBoolean() : fallback;

    private static string Get(JsonElement r, string name, string fallback) =>
        r.TryGetProperty(name, out var v) ? v.GetString()! : fallback;

    private static byte[] Bytes(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) ? v.GetBytesFromBase64() : [];
}

/// <summary>The generated classes for the corpus schemas, by full name.</summary>
internal static class Corpus
{
    private static readonly FileDescriptor[] Files =
    [
        Protowire.Conformance.Schemas.Any.AnyReflection.Descriptor,
        Protowire.Conformance.Schemas.Bigfloat.BigfloatReflection.Descriptor,
        Protowire.Conformance.Schemas.Blocks.BlocksReflection.Descriptor,
        Protowire.Conformance.Schemas.Paths.PathsReflection.Descriptor,
        Protowire.Conformance.Schemas.Duplicates.DuplicatesReflection.Descriptor,
        Protowire.Conformance.Schemas.Duration.DurationReflection.Descriptor,
        Protowire.Conformance.Schemas.BoolKeys.BoolKeysReflection.Descriptor,
        Protowire.Conformance.Schemas.Nulls.NullsReflection.Descriptor,
        Protowire.Conformance.Schemas.NullsProto2.NullsProto2Reflection.Descriptor,
        Protowire.Conformance.Schemas.NullsEditions.NullsEditionsReflection.Descriptor,
        Protowire.Conformance.Schemas.Strings.StringsReflection.Descriptor,
        Protowire.Conformance.Schemas.Text.TextReflection.Descriptor,
        Protowire.Conformance.Schemas.Testv1.TestReflection.Descriptor,
        Protowire.Conformance.Schemas.Values.ConformanceReflection.Descriptor,
        Protowire.Conformance.Schemas.SettingsAnnotated.SettingsAnnotatedReflection.Descriptor,
        Protowire.Pxf.Tests.KeyedFixtures.KeyedReflection.Descriptor,
    ];

    public static readonly Dictionary<string, MessageDescriptor> ByName = Build();

    public static readonly TypeRegistry Registry = TypeRegistry.FromFiles(Files);

    private static Dictionary<string, MessageDescriptor> Build()
    {
        var map = new Dictionary<string, MessageDescriptor>();
        foreach (var file in Files)
        {
            Add(map, file.MessageTypes);
        }
        return map;
    }

    private static void Add(Dictionary<string, MessageDescriptor> map, IEnumerable<MessageDescriptor> messages)
    {
        foreach (var m in messages)
        {
            map[m.FullName] = m;
            Add(map, m.NestedTypes);
        }
    }
}
