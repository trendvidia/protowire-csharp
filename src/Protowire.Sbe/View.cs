using System.Buffers.Binary;
using System.Text;

namespace Protowire.Sbe;

/// <summary>
/// View provides zero-allocation read access to SBE-encoded data.
/// Field values are read directly from the underlying byte buffer.
/// </summary>
public class View
{
    private readonly byte[] _data;
    private readonly ReadOnlyMemory<byte> _block;
    private readonly ViewSchema _schema;

    private const int HeaderSize = 8;
    private const int GroupHeaderSize = 4;

    // HARDENING.md § UTF-8: proto3 string fields are valid UTF-8. The default
    // Encoding.UTF8 silently substitutes U+FFFD on invalid bytes, which is
    // forbidden by the spec. This decoder throws DecoderFallbackException
    // (caught and reclassified as a clean reject by check-decode).
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal View(byte[] data, Dictionary<ushort, MessageTemplate> byID)
    {
        _data = data;
        var span = data.AsSpan();
        ushort blockLength = BinaryPrimitives.ReadUInt16LittleEndian(span[0..]);
        ushort templateID = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);

        if (!byID.TryGetValue(templateID, out var tmpl))
            throw new Exception($"SBE View: unknown template ID {templateID}");

        if (tmpl.View == null) tmpl.View = ViewSchema.Build(tmpl);

        _block = data.AsMemory(HeaderSize, blockLength);
        _schema = tmpl.View;
    }

    internal View(byte[] data, ReadOnlyMemory<byte> block, ViewSchema schema)
    {
        _data = data;
        _block = block;
        _schema = schema;
    }

    private FieldTemplate GetField(string name)
    {
        if (!_schema.Fields.TryGetValue(name, out var ft))
            throw new Exception($"SBE: unknown field {name}");
        return ft;
    }

    /// <summary>
    /// Reads a signed integer field. Works with int8, int16, int32, int64 encodings.
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <returns>The signed integer value.</returns>
    public long Int(string name)
    {
        var ft = GetField(name);
        var off = ft.Offset;
        var span = _block.Span;
        return ft.Encoding switch
        {
            "int8" => (sbyte)span[off],
            "int16" => BinaryPrimitives.ReadInt16LittleEndian(span[off..]),
            "int32" => BinaryPrimitives.ReadInt32LittleEndian(span[off..]),
            "int64" => BinaryPrimitives.ReadInt64LittleEndian(span[off..]),
            _ => throw new Exception($"SBE: field {name} is not a signed integer")
        };
    }

    /// <summary>
    /// Reads an unsigned integer field. Works with uint8, uint16, uint32, uint64 encodings.
    /// Also works for bool and enum fields (returns raw numeric value).
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <returns>The unsigned integer value.</returns>
    public ulong Uint(string name)
    {
        var ft = GetField(name);
        var off = ft.Offset;
        var span = _block.Span;
        return ft.Encoding switch
        {
            "uint8" => span[off],
            "uint16" => BinaryPrimitives.ReadUInt16LittleEndian(span[off..]),
            "uint32" => BinaryPrimitives.ReadUInt32LittleEndian(span[off..]),
            "uint64" => BinaryPrimitives.ReadUInt64LittleEndian(span[off..]),
            _ => throw new Exception($"SBE: field {name} is not an unsigned integer")
        };
    }

    /// <summary>
    /// Reads a floating-point field. Works with float and double encodings.
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <returns>The floating-point value.</returns>
    public double Float(string name)
    {
        var ft = GetField(name);
        var off = ft.Offset;
        var span = _block.Span;
        return ft.Encoding switch
        {
            "float" => BinaryPrimitives.ReadSingleLittleEndian(span[off..]),
            "double" => BinaryPrimitives.ReadDoubleLittleEndian(span[off..]),
            _ => throw new Exception($"SBE: field {name} is not a float")
        };
    }

    /// <summary>
    /// Reads a boolean field (uint8 encoding: 0 = false).
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <returns>The boolean value.</returns>
    public bool Bool(string name) => GetField(name).Encoding == "uint8" ? _block.Span[GetField(name).Offset] != 0 : throw new Exception("Not a bool");

    /// <summary>
    /// Reads an enum field as an integer.
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <returns>The enum value as an integer.</returns>
    public int Enum(string name)
    {
        var ft = GetField(name);
        var span = _block.Span;
        return ft.Encoding switch
        {
            "uint8" => span[ft.Offset],
            "uint16" => BinaryPrimitives.ReadUInt16LittleEndian(span[ft.Offset..]),
            _ => throw new Exception($"SBE: field {name} has unsupported enum encoding")
        };
    }

    /// <summary>
    /// Reads a fixed-length string field. Trailing null padding is trimmed.
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <returns>The string value.</returns>
    public string String(string name)
    {
        var ft = GetField(name);
        var raw = _block.Span[ft.Offset..(ft.Offset + ft.Size)];
        int n = raw.Length;
        while (n > 0 && raw[n - 1] == 0) n--;
        return StrictUtf8.GetString(raw[..n]);
    }

    /// <summary>
    /// Reads a fixed-length bytes field as a sub-slice of the underlying buffer.
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <returns>A ReadOnlySpan over the byte data.</returns>
    public ReadOnlySpan<byte> Bytes(string name)
    {
        var ft = GetField(name);
        return _block.Span[ft.Offset..(ft.Offset + ft.Size)];
    }

    /// <summary>
    /// Returns a sub-View over a nested message (SBE composite) field.
    /// </summary>
    /// <param name="name">The name of the composite field.</param>
    /// <returns>A new View instance for the composite block.</returns>
    public View Composite(string name)
    {
        var ft = GetField(name);
        if (ft.CompositeView == null) throw new Exception($"SBE: field {name} is not a composite");
        return new View(_data, _block.Slice(ft.Offset, ft.Size), ft.CompositeView);
    }

    /// <summary>
    /// Returns a GroupView for a repeating group field.
    /// </summary>
    /// <param name="name">The name of the group field.</param>
    /// <returns>A GroupView instance for traversing group entries.</returns>
    public GroupView Group(string name)
    {
        long pos = HeaderSize + _block.Length;
        foreach (var gi in _schema.GroupOrder)
        {
            if (pos + GroupHeaderSize > _data.Length)
                throw new Exception("SBE: group header out of bounds");
            ushort bl = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan((int)pos));
            ushort n = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan((int)pos + 2));
            // HARDENING.md § SBE step 3-4: 64-bit checked arithmetic before
            // narrowing; reject blockLength==0 with count>0 (otherwise the
            // group walk would loop or allocate without consuming input).
            if (bl == 0 && n > 0)
                throw new Exception("SBE: group blockLength=0 with count>0");
            ulong groupBytes = (ulong)n * bl;
            ulong nextPos = (ulong)pos + (ulong)GroupHeaderSize + groupBytes;
            if (nextPos > (ulong)_data.Length)
                throw new Exception("SBE: group entries out of bounds");
            if (gi.Name == name)
            {
                return new GroupView(_data, (int)pos, bl, n, gi.Schema);
            }
            pos = (long)nextPos;
        }
        throw new Exception($"SBE: unknown group {name}");
    }
}

/// <summary>
/// GroupView provides access to entries in an SBE repeating group.
/// </summary>
public class GroupView
{
    private readonly byte[] _data;
    private readonly int _start;
    private readonly int _blockLength;
    private readonly int _count;
    private readonly ViewSchema _schema;

    private const int GroupHeaderSize = 4;

    internal GroupView(byte[] data, int start, int blockLength, int count, ViewSchema schema)
    {
        _data = data;
        _start = start;
        _blockLength = blockLength;
        _count = count;
        _schema = schema;
    }

    /// <summary>
    /// Gets the number of entries in the group.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// Returns a View over the i-th group entry.
    /// </summary>
    /// <param name="i">The 0-based index of the entry.</param>
    /// <returns>A View instance for the entry block.</returns>
    public View Entry(int i)
    {
        if (i < 0 || i >= _count) throw new IndexOutOfRangeException();
        int offset = _start + GroupHeaderSize + i * _blockLength;
        return new View(_data, _data.AsMemory(offset, _blockLength), _schema);
    }
}

internal class ViewSchema
{
    public Dictionary<string, FieldTemplate> Fields { get; } = new();
    public List<ViewGroupInfo> GroupOrder { get; } = new();

    public static ViewSchema Build(MessageTemplate tmpl)
    {
        var vs = new ViewSchema();
        foreach (var ft in tmpl.Fields)
        {
            vs.Fields[ft.Fd.Name] = ft;
            if (ft.Composite != null) ft.CompositeView = BuildFields(ft.Composite);
        }
        foreach (var gt in tmpl.Groups)
        {
            vs.GroupOrder.Add(new ViewGroupInfo
            {
                Name = gt.Fd.Name,
                Schema = BuildFields(gt.Fields)
            });
        }
        return vs;
    }

    private static ViewSchema BuildFields(List<FieldTemplate> fields)
    {
        var vs = new ViewSchema();
        foreach (var ft in fields)
        {
            vs.Fields[ft.Fd.Name] = ft;
            if (ft.Composite != null) ft.CompositeView = BuildFields(ft.Composite);
        }
        return vs;
    }
}

internal class ViewGroupInfo
{
    public string Name { get; set; } = "";
    public ViewSchema Schema { get; set; } = null!;
}

internal partial class MessageTemplate
{
    public ViewSchema? View { get; set; }
}

internal partial class FieldTemplate
{
    public ViewSchema? CompositeView { get; set; }
}
