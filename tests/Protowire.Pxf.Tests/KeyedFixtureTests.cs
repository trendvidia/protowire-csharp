// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Protowire.Pxf.Tests.KeyedFixtures;

namespace Protowire.Pxf.Tests;

/// <summary>
/// The cross-port keyed-repeated-field fixtures, vendored verbatim from the
/// spec repo's <c>testdata/keyed</c>; its README states each verdict. Every
/// <c>.pxf</c> file in the directory becomes one case of <see cref="Fixture"/>,
/// and a file nothing classifies fails, so a new fixture cannot be skipped.
/// </summary>
public class KeyedFixtureTests
{
    private const int ExpectedFixtures = 15;

    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "testdata", "keyed");

    private static readonly Dictionary<string, (KeyedErrorKind Kind, string Message)> Rejects = new()
    {
        ["err-duplicate-key"] = (KeyedErrorKind.DuplicateKey, "duplicate key \"greeting\" in keyed field \"children\""),
        ["err-duplicate-key-spelling"] = (KeyedErrorKind.DuplicateKey, "duplicate key \"greeting\" in keyed field \"children\""),
        ["err-key-conflict"] = (KeyedErrorKind.KeyConflict, "key field \"id\" = \"farewell\" conflicts with entry name \"greeting\" in keyed field \"children\""),
        ["err-empty-key"] = (KeyedErrorKind.EmptyKey, "empty entry name in keyed field \"children\": the empty string is not a valid key"),
        ["err-empty-key-anonymous"] = (KeyedErrorKind.EmptyKey, "explicit empty-string assignment to key field \"id\" of keyed field \"children\": the empty string is not a valid key"),
        ["err-quoted-name-unkeyed"] = (KeyedErrorKind.QuotedNameUnkeyed, "quoted entry name \"a\" is only valid inside a keyed repeated field's block (draft -01 §3.13)"),
    };

    private static readonly string[] FmtPairs = ["fmt-unquote", "fmt-anonymous-to-keyed"];

    public static IEnumerable<object[]> Fixtures() =>
        Directory.GetFiles(Dir, "*.pxf")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new object[] { n });

    [Fact]
    public void AllFixturesAreDiscovered() => Assert.Equal(ExpectedFixtures, Fixtures().Count());

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture(string name)
    {
        if (Rejects.TryGetValue(name, out var reject))
        {
            AssertRejected(name, reject.Kind, reject.Message);
        }
        else if (FmtPairs.Contains(name))
        {
            AssertFormatsTo(Read(name), Read(name + ".expected"));
        }
        else if (FmtPairs.Any(p => name == p + ".expected"))
        {
            AssertFormatsTo(Read(name), Read(name));
        }
        else
        {
            switch (name)
            {
                case "roundtrip-keyed":
                case "roundtrip-quoted":
                    AssertRoundTrips(name);
                    break;
                case "anonymous-equivalence":
                    AssertAnonymousEquivalent();
                    break;
                case "redundant-key-ok":
                    AssertRedundantKeyLegal();
                    break;
                case "anonymous-duplicate-ok":
                    AssertDuplicatesStayAnonymous();
                    break;
                default:
                    Assert.Fail($"fixture {name} has no verdict in this harness");
                    break;
            }
        }
    }

    private static void AssertRoundTrips(string name)
    {
        var data = Read(name);
        var (msg, _) = Decode(data);
        Assert.Equal(Body(data), new Encoder().Marshal(msg));
    }

    private static void AssertAnonymousEquivalent()
    {
        var anon = Read("anonymous-equivalence");
        var keyed = Read("roundtrip-keyed");
        var (anonMsg, desc) = Decode(anon);
        var (keyedMsg, _) = Decode(keyed);

        Assert.Equal(keyedMsg, anonMsg);
        Assert.Equal(keyedMsg.ToByteArray(), anonMsg.ToByteArray());
        Assert.Equal(Body(keyed), new Encoder().Marshal(anonMsg));

        var doc = Parser.Parse(anon);
        Keyed.Canonicalize(doc, desc);
        Assert.Equal(Clean(keyed), Clean(Format.FormatDocument(doc)));
    }

    private static void AssertRedundantKeyLegal()
    {
        var data = Read("redundant-key-ok");
        var (msg, desc) = Decode(data);
        var node = Assert.IsType<Node>(msg);
        Assert.Equal("greeting", Assert.Single(node.Children).Id);

        // Once for the root; the child's key is the entry name.
        Assert.Single(Lines(new Encoder().Marshal(msg)), l => l.Contains("id = "));
        var doc = Parser.Parse(data);
        Keyed.Canonicalize(doc, desc);
        Assert.Single(Lines(Format.FormatDocument(doc)), l => l.Contains("id = "));
    }

    private static void AssertDuplicatesStayAnonymous()
    {
        var data = Read("anonymous-duplicate-ok");
        var (msg, desc) = Decode(data);
        var node = Assert.IsType<Node>(msg);
        Assert.Equal(["dup", "dup"], node.Children.Select(c => c.Id));

        var encoded = new Encoder().Marshal(msg);
        Assert.Contains("children = [", encoded);
        var again = new Node();
        new Decoder().Unmarshal(encoded, again);
        Assert.Equal(node, again);

        var doc = Parser.Parse(data);
        Keyed.Canonicalize(doc, desc);
        Assert.Contains("children = [", Format.FormatDocument(doc));
    }

    private static void AssertRejected(string name, KeyedErrorKind kind, string message)
    {
        var data = Read(name);
        var doc = Parser.Parse(data); // the grammar accepts every one of these

        var plain = Assert.Throws<KeyedException>(() => new Decoder().Unmarshal(data, New(doc.TypeURL)));
        Assert.Equal(kind, plain.Kind);
        Assert.EndsWith(": " + message, plain.Message);

        var full = Assert.Throws<KeyedException>(() => new Decoder().UnmarshalFull(data, New(doc.TypeURL)));
        Assert.Equal(kind, full.Kind);
    }

    private static void AssertFormatsTo(string input, string expected)
    {
        var doc = Parser.Parse(input);
        Keyed.Canonicalize(doc, New(doc.TypeURL).Descriptor);
        Assert.Equal(expected, Format.FormatDocument(doc));
    }

    private static (IMessage Msg, MessageDescriptor Desc) Decode(string data)
    {
        var typeUrl = Parser.Parse(data).TypeURL;
        var msg = New(typeUrl);
        new Decoder().Unmarshal(data, msg);
        var full = New(typeUrl);
        new Decoder().UnmarshalFull(data, full);
        Assert.Equal(msg, full);
        return (msg, msg.Descriptor);
    }

    private static IMessage New(string typeUrl) => typeUrl switch
    {
        "keyed.v1.Node" => new Node(),
        "keyed.v1.Deployment" => new Deployment(),
        "keyed.v1.Doc" => new Doc(),
        _ => throw new ArgumentException($"no message for @type {typeUrl}"),
    };

    // Git may check the fixtures out with CRLF on Windows; the expectation
    // is on the text, so compare with LF.
    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(Dir, name + ".pxf")).Replace("\r\n", "\n");

    private static IEnumerable<string> Lines(string s) => s.Split('\n');

    /// <summary>The document without comment lines and the blank runs they leave.</summary>
    private static string Clean(string data)
    {
        var s = string.Join('\n', Lines(data).Where(l => !l.TrimStart().StartsWith('#')));
        while (s.Contains("\n\n\n")) s = s.Replace("\n\n\n", "\n\n");
        return s.TrimEnd('\n') + "\n";
    }

    /// <summary>What the encoder should reproduce: it writes no <c>@type</c> line.</summary>
    private static string Body(string data)
    {
        var clean = Clean(data);
        return clean[(clean.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];
    }
}
