using System.Numerics;

namespace Protowire.Pxf;

public record Position(int Line, int Column, int Offset)
{
    public static readonly Position Empty = new(0, 0, 0);
}

public record Comment(Position Pos, string Text);

public class Document
{
    public string TypeURL { get; set; } = string.Empty;
    public List<IEntry> Entries { get; set; } = new();
    public List<Comment> LeadingComments { get; set; } = new();
}

public interface INode
{
    Position Pos { get; }
}

public interface IEntry : INode { }

public interface IValue : INode { }

public class Assignment : IEntry
{
    public Position Pos { get; init; }
    public string Key { get; set; } = string.Empty;
    public IValue Value { get; set; } = null!;
    public List<Comment> LeadingComments { get; set; } = new();
    public string TrailingComment { get; set; } = string.Empty;
}

public class MapEntry : IEntry
{
    public Position Pos { get; init; }
    public string Key { get; set; } = string.Empty;
    public IValue Value { get; set; } = null!;
    public List<Comment> LeadingComments { get; set; } = new();
    public string TrailingComment { get; set; } = string.Empty;
}

public class Block : IEntry
{
    public Position Pos { get; init; }
    public string Name { get; set; } = string.Empty;
    public List<IEntry> Entries { get; set; } = new();
    public List<Comment> LeadingComments { get; set; } = new();
}

public class StringVal : IValue
{
    public Position Pos { get; init; }
    public string Value { get; set; } = string.Empty;
}

public class IntVal : IValue
{
    public Position Pos { get; init; }
    public string Raw { get; set; } = string.Empty;
}

public class FloatVal : IValue
{
    public Position Pos { get; init; }
    public string Raw { get; set; } = string.Empty;
}

public class BoolVal : IValue
{
    public Position Pos { get; init; }
    public bool Value { get; set; }
}

public class BytesVal : IValue
{
    public Position Pos { get; init; }
    public byte[] Value { get; set; } = Array.Empty<byte>();
}

public class NullVal : IValue
{
    public Position Pos { get; init; }
}

public class IdentVal : IValue
{
    public Position Pos { get; init; }
    public string Name { get; set; } = string.Empty;
}

public class TimestampVal : IValue
{
    public Position Pos { get; init; }
    public DateTime Value { get; set; }
    public string Raw { get; set; } = string.Empty;
}

public class DurationVal : IValue
{
    public Position Pos { get; init; }
    public TimeSpan Value { get; set; }
    public string Raw { get; set; } = string.Empty;
}

public class ListVal : IValue
{
    public Position Pos { get; init; }
    public List<IValue> Elements { get; set; } = new();
}

public class BlockVal : IValue
{
    public Position Pos { get; init; }
    public List<IEntry> Entries { get; set; } = new();
}
