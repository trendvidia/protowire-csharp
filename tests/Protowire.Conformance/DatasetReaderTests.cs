// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Text;
using Google.Protobuf;
using Protowire.Pxf;
using Decoder = Protowire.Pxf.Decoder;

namespace Protowire.Conformance;

/// <summary>
/// The streaming <see cref="DatasetReader"/> against the materializing path,
/// <see cref="Parser.Parse(string)"/> and <see cref="Decoder.BindRow"/>: the same
/// rows, at the same positions, bound to the same messages, and the same
/// verdict, whatever sizes the stream's reads come in.
/// </summary>
public class DatasetReaderTests
{
    private static readonly string DatasetDir = Path.Combine(AppContext.BaseDirectory, "testdata", "dataset");

    // 1 proves the framing does not depend on where a read ends; 7 cuts tokens
    // at varied places; the default reads whole pulls.
    public static readonly int[] ReadSizes = [1, 7, int.MaxValue];

    public static IEnumerable<object[]> Fixtures() =>
        Directory.EnumerateFiles(DatasetDir, "*.pxf").Order(StringComparer.Ordinal)
            .SelectMany(f => ReadSizes.Select(n => new object[] { Path.GetFileName(f), n }));

    [Fact]
    public void EveryDatasetFixtureIsCovered()
    {
        // The spec's corpus has 42; a vendoring that dropped them would pass vacuously.
        Assert.Equal(42, Directory.EnumerateFiles(DatasetDir, "*.pxf").Count());
        Assert.Equal(42 * ReadSizes.Length, Fixtures().Count());
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_StreamsAsItParses(string name, int readSize)
    {
        var bytes = File.ReadAllBytes(Path.Combine(DatasetDir, name));
        var want = Materialize(Encoding.UTF8.GetString(bytes));
        var got = Stream(bytes, readSize, scan: false);
        var scanned = Stream(bytes, readSize, scan: true);
        Assert.Equal(want.Verdict, got.Verdict);
        Assert.Equal(want.Verdict, scanned.Verdict);
        if (want.Verdict != null) return;
        Assert.Equal(want.Rows, got.Rows);
        Assert.Equal(want.Messages, got.Messages);
        Assert.Equal(want.Messages, scanned.Messages);
    }

    [Fact]
    public void Fixtures_HaveBothVerdicts()
    {
        // A harness where everything rejects (or accepts) compares nothing.
        var verdicts = Directory.EnumerateFiles(DatasetDir, "*.pxf")
            .Select(f => Materialize(File.ReadAllText(f)).Verdict).ToArray();
        Assert.Equal(16, verdicts.Count(v => v == null));
        Assert.Equal(26, verdicts.Count(v => v != null));
    }

    /// <summary>
    /// Every fixture broken in small seeded ways, read whole and a byte at a
    /// time: a document either path accepts, the other accepts with the same
    /// rows. The messages may differ, because the reader reports the first
    /// error in reading order: a bind error in an early row, or a header
    /// error, comes before a parse error further on, which Parse reports first.
    /// </summary>
    [Fact]
    public void MutatedFixtures_KeepTheirVerdictAndRows()
    {
        var rng = new Random(20261102);
        int compared = 0, accepted = 0;
        foreach (var file in Directory.EnumerateFiles(DatasetDir, "*.pxf").Order(StringComparer.Ordinal))
        {
            string text = File.ReadAllText(file);
            for (int i = 0; i < 100; i++)
            {
                string input = MutationTests.Mutate(text, rng);
                Document? doc = null;
                try
                {
                    doc = Parser.Parse(input);
                }
                catch (PxfException)
                {
                }
                // A document with no @dataset is not a table to stream.
                if (doc is { Datasets.Count: 0 }) continue;
                var want = Materialize(input);
                var bytes = Encoding.UTF8.GetBytes(input);
                foreach (int readSize in new[] { 1, int.MaxValue })
                {
                    var got = Stream(bytes, readSize, scan: false);
                    Assert.True((want.Verdict == null) == (got.Verdict == null),
                        $"read size {readSize}: parse {want.Verdict ?? "accepts"}, stream {got.Verdict ?? "accepts"}\n{input}");
                    if (want.Verdict == null)
                    {
                        Assert.Equal(want.Rows, got.Rows);
                        Assert.Equal(want.Messages, got.Messages);
                    }
                }
                compared++;
                if (want.Verdict == null) accepted++;
            }
        }
        // Both verdicts are exercised in numbers.
        Assert.True(compared > 3500 && accepted > 1000, $"compared {compared}, accepted {accepted}");
    }

    private const string Header = "@dataset test.v1.AllTypes (string_field, int32_field)\n";

    [Fact]
    public void RowCutShortByTheEnd_IsTheParseError_AfterTheRowsBeforeIt()
    {
        string doc = Header + "(\"row-1\", 1)\n(\"row-2\", 2";
        var parseError = Assert.Throws<PxfException>(() => Parser.Parse(doc));
        foreach (int readSize in ReadSizes)
        {
            var reader = DatasetReader.Create(new ChunkedStream(Encoding.UTF8.GetBytes(doc), readSize));
            Assert.Equal("row-1", ((StringVal)reader.Next()!.Cells[0]!).Value);
            var e = Assert.Throws<PxfException>(() => reader.Next());
            Assert.Equal(parseError.Message, e.Message);
            Assert.Contains("expected ',' or ')' in @dataset row, got EOF", e.Message);
            // The error is sticky.
            Assert.Same(e, Assert.Throws<PxfException>(() => reader.Next()));
        }
    }

    [Theory]
    [InlineData("(\"row-2\", 2")]        // a number
    [InlineData("(\"row-2\", \"ab")]     // a string
    [InlineData("(\"row-2\", b\"AAA")]   // a bytes literal
    [InlineData("(\"row-2\", 2 /* x")]   // a block comment
    [InlineData("(\"row-2\", {")]        // a block
    [InlineData("(\"row-2\", [1,")]      // a list
    public void RowCutShortMidToken_IsTheParseError(string tail)
    {
        string doc = Header + "(\"row-1\", 1)\n" + tail;
        var parseError = Assert.Throws<PxfException>(() => Parser.Parse(doc));
        foreach (int readSize in ReadSizes)
        {
            var reader = DatasetReader.Create(new ChunkedStream(Encoding.UTF8.GetBytes(doc), readSize));
            Assert.NotNull(reader.Next());
            Assert.Equal(parseError.Message, Assert.Throws<PxfException>(() => reader.Next()).Message);
        }
    }

    [Fact]
    public void MaxMessageSize_CapsTheBytesHeldWhileSeekingARowBoundary()
    {
        var limits = PxfLimits.Default with { MaxMessageSize = 200 };
        string longRow = "(\"" + new string('x', 300) + "\", 1)\n";
        foreach (int readSize in ReadSizes)
        {
            var reader = new Decoder { Limits = limits }.NewDatasetReader(new ChunkedStream(Encoding.UTF8.GetBytes(Header + "(\"a\", 1)\n" + longRow), readSize));
            Assert.NotNull(reader.Next());
            var e = Assert.Throws<PxfException>(() => reader.Next());
            Assert.Contains("without a row boundary, MaxMessageSize=200", e.Message);
            // Never more than the cap is held, whatever the read size.
            Assert.Contains("holds 200 bytes", e.Message);
        }
    }

    [Fact]
    public void MaxMessageSize_BoundsARow_NotTheTable()
    {
        // 100 rows of about 15 bytes: a table several times the cap, each row well under it.
        var limits = PxfLimits.Default with { MaxMessageSize = 200 };
        var sb = new StringBuilder(Header);
        for (int i = 0; i < 100; i++) sb.Append($"(\"row-{i}\", {i})\n");
        Assert.True(Encoding.UTF8.GetByteCount(sb.ToString()) > 5 * 200);
        Assert.Throws<PxfException>(() => Parser.Parse(sb.ToString(), limits));
        foreach (int readSize in ReadSizes)
        {
            var reader = new Decoder { Limits = limits }.NewDatasetReader(new ChunkedStream(Encoding.UTF8.GetBytes(sb.ToString()), readSize));
            int n = 0;
            while (reader.Next() is { } row)
            {
                Assert.Equal(n, int.Parse(((IntVal)row.Cells[1]!).Raw));
                n++;
            }
            Assert.Equal(100, n);
        }
    }

    [Theory]
    [InlineData("\"", "\"")]          // a string
    [InlineData("b\"", "\"")]         // a bytes literal
    [InlineData("/*", "*/ \"s\"")]    // a block comment
    [InlineData("# ", "\n\"s\"")]     // a line comment
    [InlineData("\"\"\"", "\"\"\"")]    // a triple-quoted string
    [InlineData("", "")]               // an identifier
    public void ALongTokenReadInSmallPieces_IsLexedInLinearTime(string open, string close)
    {
        // A 256 KiB token read 64 bytes at a time: lexed again on every read,
        // it would cost about 4096 times its length.
        const int size = 256 * 1024;
        string body = open == "b\"" ? new string('A', size) : new string('x', size);
        string cell = open.Length == 0 ? "ENUM_" + body : open + body + close;
        var bytes = Encoding.UTF8.GetBytes("@dataset test.v1.AllTypes (string_field)\n(" + cell + ")\n");
        var reader = DatasetReader.Create(new ChunkedStream(bytes, 64));
        var row = reader.Next();
        Assert.NotNull(row);
        Assert.Null(reader.Next());
        Assert.InRange(reader.Scanned, size, 4L * bytes.Length);
    }

    [Fact]
    public void ACompleteRow_IsReturnedWithoutReadingPastIt()
    {
        // A sender that wrote one row and waits: reading on would block forever.
        var bytes = Encoding.UTF8.GetBytes(Header + "(\"row-1\", 1)");
        foreach (int readSize in ReadSizes)
        {
            var reader = DatasetReader.Create(new ChunkedStream(bytes, readSize, failAtEnd: true));
            Assert.Equal("row-1", ((StringVal)reader.Next()!.Cells[0]!).Value);
        }
    }

    [Fact]
    public void ACharacterSplitAcrossReads_ReadsAsTheWholeDocumentDoes()
    {
        // Two-, three- and four-byte characters, and an invalid byte, which both
        // paths read as U+FFFD.
        var bytes = Encoding.UTF8.GetBytes(Header + "(\"é€𝕏\", 1)\n(\"").Concat(new byte[] { 0xE2, 0x82 })
            .Concat(Encoding.UTF8.GetBytes("\", 2)\n")).ToArray();
        var want = Materialize(Encoding.UTF8.GetString(bytes));
        Assert.Null(want.Verdict);
        foreach (int readSize in ReadSizes)
        {
            var got = Stream(bytes, readSize, scan: false);
            Assert.Null(got.Verdict);
            Assert.Equal(want.Rows, got.Rows);
        }
    }

    [Fact]
    public void NoDataset_AndAHeaderCutShort()
    {
        Assert.Throws<NoDatasetException>(() => DatasetReader.Create(new ChunkedStream("@meta x\n"u8.ToArray(), 1)));
        string cut = "@dataset test.v1.AllTypes (string_field,";
        var parseError = Assert.Throws<PxfException>(() => Parser.Parse(cut));
        var e = Assert.Throws<PxfException>(() => DatasetReader.Create(new ChunkedStream(Encoding.UTF8.GetBytes(cut), 1)));
        Assert.Equal(parseError.Message, e.Message);
    }

    [Fact]
    public void TheHeaderAndItsDirectives()
    {
        string doc = "# lead\n@meta a.B { x = 1 }\n@proto t.Row { message Row { string s = 1; } }\n@dataset t.Row (s)\n(\"v\")\n";
        var parsed = Parser.Parse(doc);
        var reader = DatasetReader.Create(new ChunkedStream(Encoding.UTF8.GetBytes(doc), 1));
        Assert.Equal("t.Row", reader.Type);
        Assert.Equal(["s"], reader.Columns);
        var d = Assert.Single(reader.Directives);
        var want = Assert.Single(parsed.Directives);
        Assert.Equal((want.Pos, want.Name, want.Type), (d.Pos, d.Name, d.Type));
        Assert.Equal(want.Body, d.Body);
        Assert.Equal(Render(parsed.Datasets[0].Rows[0]), Render(reader.Next()!));
        Assert.Null(reader.Next());
    }

    private sealed record Outcome(string? Verdict, List<string> Rows, List<string> Messages);

    private static Outcome Materialize(string text)
    {
        var rows = new List<string>();
        var messages = new List<string>();
        try
        {
            foreach (var ds in Parser.Parse(text).Datasets)
            {
                var desc = RowType(ds.Type, ds.Pos);
                foreach (var row in ds.Rows)
                {
                    var msg = desc.Parser.ParseFrom(ByteString.Empty);
                    new Decoder().BindRow(msg, ds.Columns, row);
                    rows.Add(Render(row));
                    messages.Add(Convert.ToHexString(msg.ToByteArray()));
                }
            }
        }
        catch (PxfException e)
        {
            return new Outcome(e.Message, rows, messages);
        }
        return new Outcome(null, rows, messages);
    }

    // Every table in the document, each read on the previous reader's Tail, as
    // protowire-go's harness reads them.
    private static Outcome Stream(byte[] bytes, int readSize, bool scan)
    {
        var rows = new List<string>();
        var messages = new List<string>();
        System.IO.Stream src = new ChunkedStream(bytes, readSize);
        try
        {
            for (bool first = true; ; first = false)
            {
                DatasetReader reader;
                try
                {
                    reader = DatasetReader.Create(src);
                }
                catch (NoDatasetException) when (!first)
                {
                    break;
                }
                var desc = RowType(reader.Type, new Position(0, 0, 0));
                for (;;)
                {
                    var msg = desc.Parser.ParseFrom(ByteString.Empty);
                    if (scan)
                    {
                        if (!reader.Scan(msg)) break;
                    }
                    else
                    {
                        var row = reader.Next();
                        if (row == null) break;
                        new Decoder().BindRow(msg, reader.Columns, row);
                        rows.Add(Render(row));
                    }
                    messages.Add(Convert.ToHexString(msg.ToByteArray()));
                }
                src = reader.Tail();
            }
        }
        catch (PxfException e)
        {
            return new Outcome(e.Message, rows, messages);
        }
        return new Outcome(null, rows, messages);
    }

    // A type the corpus does not hold is the harness's refusal, the same on both paths.
    private static Google.Protobuf.Reflection.MessageDescriptor RowType(string name, Position pos) =>
        Corpus.ByName.TryGetValue(name, out var desc) ? desc : throw new PxfException(pos, $"unknown row type {name}");

    private static string Render(DatasetRow row) =>
        $"{row.Pos}: " + string.Join(" | ", row.Cells.Select(c => c == null ? "" : Format.FormatValue(c)));

    /// <summary>A stream whose reads return at most <c>chunk</c> bytes.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk, bool failAtEnd = false) : System.IO.Stream
    {
        private int _pos;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos == data.Length && failAtEnd)
            {
                throw new InvalidOperationException("read past the data a sender has written");
            }
            int n = Math.Min(Math.Min(count, chunk), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
