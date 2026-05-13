// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Numerics;

namespace Protowire.Pxf;

public record Position(int Line, int Column, int Offset)
{
    public static readonly Position Empty = new(0, 0, 0);
}

public record Comment(Position Pos, string Text);

public sealed record Document
{
    public string TypeURL { get; init; } = string.Empty;
    public List<Directive> Directives { get; init; } = [];
    public List<DatasetDirective> Datasets { get; init; } = [];
    public List<ProtoDirective> Protos { get; init; } = [];
    public int BodyOffset { get; init; }
    public List<IEntry> Entries { get; init; } = [];
    public List<Comment> LeadingComments { get; init; } = [];
}

/// <summary>
/// Top-of-document `@&lt;name&gt; *(&lt;prefix-id&gt;) [{ ... }]` entry
/// (draft §3.4.2). Side-channel metadata that sits alongside the
/// schema-typed body — e.g. chameleon's
/// <c>@header chameleon.v1.LayerHeader { id = "x" }</c>.
/// </summary>
public sealed record Directive
{
    public required Position Pos { get; init; }
    public string Name { get; init; } = string.Empty;
    public List<string> Prefixes { get; init; } = [];
    /// <summary>
    /// Back-compat single-prefix sugar: populated when exactly one
    /// prefix identifier was supplied. Empty for zero or 2+ prefixes;
    /// new code should read <see cref="Prefixes"/> directly.
    /// </summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>Raw inner bytes of the block; empty when the directive has no `{ ... }`.</summary>
    public byte[] Body { get; init; } = [];
    public bool HasBody { get; init; }
    public List<Comment> LeadingComments { get; init; } = [];
}

/// <summary>
/// `@dataset &lt;type&gt; ( col1, col2, ... ) row*` entry at document
/// root (draft §3.4.4). Carries many instances of one message type in a
/// single document — the protowire-native CSV. <c>Type</c> MAY be empty
/// when an anonymous <c>@proto</c> precedes the <c>@dataset</c>.
/// </summary>
public sealed record DatasetDirective
{
    public required Position Pos { get; init; }
    public string Type { get; init; } = string.Empty;
    public List<string> Columns { get; init; } = [];
    public List<DatasetRow> Rows { get; init; } = [];
    public List<Comment> LeadingComments { get; init; } = [];
}

/// <summary>
/// One parenthesised cell tuple in a @dataset directive. <c>Cells</c>
/// has the same length as the containing <c>DatasetDirective.Columns</c>.
/// A null entry denotes an absent field; a <see cref="NullVal"/> denotes
/// present-but-null; any other value denotes a present field.
/// </summary>
public sealed record DatasetRow
{
    public required Position Pos { get; init; }
    public List<IValue?> Cells { get; init; } = [];
}

/// <summary>Lexical body shape of a <c>@proto</c> directive (draft §3.4.5).</summary>
public enum ProtoShape
{
    Anonymous,
    Named,
    Source,
    Descriptor,
}

public static class ProtoShapeNames
{
    public static string Name(this ProtoShape shape) => shape switch
    {
        ProtoShape.Anonymous => "anonymous",
        ProtoShape.Named => "named",
        ProtoShape.Source => "source",
        ProtoShape.Descriptor => "descriptor",
        _ => $"ProtoShape({(int)shape})",
    };
}

/// <summary>
/// <c>@proto &lt;body&gt;</c> entry at document root (draft §3.4.5).
/// <para>
/// <c>Body</c> holds raw bytes interpreted per <c>Shape</c>: for
/// anonymous/named, the bytes between <c>{</c> and matching <c>}</c>
/// (protobuf message-body source); for source, the dedented triple-
/// quoted string contents; for descriptor, the base64-decoded
/// <c>FileDescriptorSet</c>.
/// </para>
/// </summary>
public sealed record ProtoDirective
{
    public required Position Pos { get; init; }
    public ProtoShape Shape { get; init; }
    /// <summary>Dotted message type name; non-empty only when <c>Shape == Named</c>.</summary>
    public string TypeName { get; init; } = string.Empty;
    public byte[] Body { get; init; } = [];
    public List<Comment> LeadingComments { get; init; } = [];
}

public interface INode
{
    Position Pos { get; }
}

public interface IEntry : INode { }

public interface IValue : INode { }

public sealed record Assignment : IEntry
{
    public required Position Pos { get; init; }
    public string Key { get; init; } = string.Empty;
    public required IValue Value { get; init; }
    public List<Comment> LeadingComments { get; init; } = [];
    public string TrailingComment { get; init; } = string.Empty;
}

public sealed record MapEntry : IEntry
{
    public required Position Pos { get; init; }
    public string Key { get; init; } = string.Empty;
    public required IValue Value { get; init; }
    public List<Comment> LeadingComments { get; init; } = [];
    public string TrailingComment { get; init; } = string.Empty;
}

public sealed record Block : IEntry
{
    public required Position Pos { get; init; }
    public string Name { get; init; } = string.Empty;
    public List<IEntry> Entries { get; init; } = [];
    public List<Comment> LeadingComments { get; init; } = [];
}

public sealed record StringVal : IValue
{
    public required Position Pos { get; init; }
    public string Value { get; init; } = string.Empty;
}

public sealed record IntVal : IValue
{
    public required Position Pos { get; init; }
    public string Raw { get; init; } = string.Empty;
}

public sealed record FloatVal : IValue
{
    public required Position Pos { get; init; }
    public string Raw { get; init; } = string.Empty;
}

public sealed record BoolVal : IValue
{
    public required Position Pos { get; init; }
    public bool Value { get; init; }
}

public sealed record BytesVal : IValue
{
    public required Position Pos { get; init; }
    public byte[] Value { get; init; } = [];
}

public sealed record NullVal : IValue
{
    public required Position Pos { get; init; }
}

public sealed record IdentVal : IValue
{
    public required Position Pos { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed record TimestampVal : IValue
{
    public required Position Pos { get; init; }
    public DateTime Value { get; init; }
    public string Raw { get; init; } = string.Empty;
}

public sealed record DurationVal : IValue
{
    public required Position Pos { get; init; }
    public TimeSpan Value { get; init; }
    public string Raw { get; init; } = string.Empty;
}

public sealed record ListVal : IValue
{
    public required Position Pos { get; init; }
    public List<IValue> Elements { get; init; } = [];
}

public sealed record BlockVal : IValue
{
    public required Position Pos { get; init; }
    public List<IEntry> Entries { get; init; } = [];
}
