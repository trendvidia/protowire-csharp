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
    public List<IEntry> Entries { get; init; } = [];
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
