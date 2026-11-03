// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Protowire.Pxf;

namespace Protowire.Conformance;

/// <summary>
/// HARDENING.md: a decoder rejects hostile input cleanly. The corpus proves that
/// for the shapes someone thought of, so this breaks every document in small
/// seeded ways and holds the parser, the decoder, the row binder and the writer
/// to one kind of failure. It needs no oracle, and a failure names the input.
/// </summary>
public class MutationTests
{
    private const int MutationsPerDocument = 40;

    private static readonly string[] Splices =
    [
        "{", "}", "[", "]", "(", ")", "=", ":", ",", "\"", "null", "true", "@type", "@type = \"x\"", "@dataset",
        "-", "+inf", "nan", "1e999999", "0p0", "1p99999", "9999999999999999999999", "1.5ms", "99999h",
        "0000-01-01T00:00:00Z", "9999-12-31T23:59:59.999999999-23:59", "b\"\"", "b\"!\"", "\\", "\n", "µ", "𝕏",
    ];

    private static string Mutate(string text, Random rng)
    {
        int at = rng.Next(text.Length + 1);
        return rng.Next(3) switch
        {
            0 => text.Insert(at, Splices[rng.Next(Splices.Length)]),
            1 => at < text.Length ? text.Remove(at, Math.Min(rng.Next(1, 8), text.Length - at)) : text,
            _ => at < text.Length ? text.Remove(at, 1).Insert(at, Splices[rng.Next(Splices.Length)]) : text,
        };
    }

    private static void Attempt(Action action, string input)
    {
        try
        {
            action();
        }
        catch (PxfException)
        {
        }
        catch (Exception e)
        {
            Assert.Fail($"[{e.GetType().Name}] {e.Message}\ninput:\n{input}");
        }
    }

    [Fact]
    public void BrokenDocuments_FailOnlyAsPxfExceptions()
    {
        var targets = Corpus.ByName.Values.Where(m => !m.IsMapEntry).ToArray();
        var rng = new Random(20261102);
        int runs = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "testdata"), "*.pxf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string text = File.ReadAllText(file);
            for (int i = 0; i < MutationsPerDocument; i++)
            {
                string input = Mutate(text, rng);
                var doc = default(Document);
                Attempt(() => doc = Parser.Parse(input), input);
                if (doc != null)
                {
                    Attempt(() => Format.FormatDocument(doc), input);
                    foreach (var ds in doc.Datasets)
                    {
                        if (!Corpus.ByName.TryGetValue(ds.Type, out var rowType)) continue;
                        foreach (var row in ds.Rows)
                        {
                            Attempt(() => new Decoder().BindRow(rowType.Parser.ParseFrom(ByteString.Empty), ds.Columns, row), input);
                        }
                    }
                }
                var target = targets[rng.Next(targets.Length)];
                var msg = target.Parser.ParseFrom(ByteString.Empty);
                Attempt(() =>
                {
                    new Decoder { Registry = Corpus.Registry }.UnmarshalFull(input, msg);
                    new Encoder { Registry = Corpus.Registry }.Marshal(msg);
                }, input);
                runs++;
            }
        }
        Assert.True(runs > 1000, $"only {runs} inputs ran");
    }
}
