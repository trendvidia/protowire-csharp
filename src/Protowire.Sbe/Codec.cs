using System.Buffers.Binary;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Sbe;

/// <summary>
/// Codec provides FIX SBE (Simple Binary Encoding) marshaling for Protobuf messages.
/// </summary>
public class Codec
{
    private readonly Dictionary<string, MessageTemplate> _byName = new();
    private readonly Dictionary<ushort, MessageTemplate> _byID = new();

    private const int HeaderSize = 8;
    private const int GroupHeaderSize = 4;

    // HARDENING.md § UTF-8: proto3 string fields are valid UTF-8.
    // Encoding.UTF8 silently substitutes U+FFFD on invalid bytes; use a
    // throwing decoder for string-typed fields.
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Initializes a new instance of the Codec class with the given proto file descriptors.
    /// </summary>
    /// <param name="files">File descriptors containing SBE-annotated messages.</param>
    public Codec(params FileDescriptor[] files)
    {
        foreach (var fd in files)
        {
            var schemaID = fd.GetOptions()?.GetExtension(AnnotationsExtensions.SchemaId) ?? 0;
            var version = fd.GetOptions()?.GetExtension(AnnotationsExtensions.Version) ?? 0;

            foreach (var md in fd.MessageTypes)
            {
                RegisterMessage(md, (ushort)schemaID, (ushort)version);
            }
        }
    }

    private void RegisterMessage(MessageDescriptor md, ushort schemaID, ushort version)
    {
        var tid = md.GetOptions()?.GetExtension(AnnotationsExtensions.TemplateId) ?? 0;
        if (tid != 0)
        {
            var tmpl = BuildTemplate(md, (ushort)tid, schemaID, version);
            _byName[md.FullName] = tmpl;
            _byID[tmpl.TemplateID] = tmpl;
        }

        foreach (var nested in md.NestedTypes)
        {
            RegisterMessage(nested, schemaID, version);
        }
    }

    private MessageTemplate BuildTemplate(MessageDescriptor md, ushort tid, ushort schemaID, ushort version)
    {
        var tmpl = new MessageTemplate
        {
            TemplateID = tid,
            SchemaID = schemaID,
            Version = version
        };

        var fields = md.Fields.InFieldNumberOrder();
        ushort offset = 0;

        foreach (var fd in fields)
        {
            if (fd.IsMap)
            {
                tmpl.Groups.Add(BuildMapTemplate(fd));
                continue;
            }

            if (fd.IsRepeated && fd.FieldType == FieldType.Message)
            {
                tmpl.Groups.Add(BuildGroupTemplate(fd));
                continue;
            }

            if (fd.IsRepeated)
            {
                tmpl.Groups.Add(new GroupTemplate
                {
                    Fd = fd,
                    IsScalarGroup = true,
                    BlockLength = GetFieldEncodingSize(fd).Item2,
                    Fields = { new FieldTemplate { Fd = fd, Offset = 0, Size = GetFieldEncodingSize(fd).Item2, Encoding = GetFieldEncodingSize(fd).Item1 } }
                });
                continue;
            }

            if (fd.FieldType == FieldType.Message)
            {
                var (size, subFields) = BuildCompositeFields(fd.MessageType);
                tmpl.Fields.Add(new FieldTemplate
                {
                    Fd = fd,
                    Offset = offset,
                    Size = size,
                    Composite = subFields
                });
                offset += size;
                continue;
            }

            var (enc, fsize) = GetFieldEncodingSize(fd);
            tmpl.Fields.Add(new FieldTemplate
            {
                Fd = fd,
                Offset = offset,
                Size = fsize,
                Encoding = enc
            });
            offset += fsize;
        }

        tmpl.BlockLength = offset;
        return tmpl;
    }

    private GroupTemplate BuildMapTemplate(FieldDescriptor fd)
    {
        var md = fd.MessageType;
        var gt = new GroupTemplate { Fd = fd, IsMapGroup = true };
        ushort offset = 0;

        var parentLength = fd.GetOptions()?.GetExtension(AnnotationsExtensions.Length) ?? 0;

        foreach (var f in md.Fields.InFieldNumberOrder())
        {
            var (enc, fsize) = GetFieldEncodingSize(f, (ushort)parentLength);
            gt.Fields.Add(new FieldTemplate
            {
                Fd = f,
                Offset = offset,
                Size = fsize,
                Encoding = enc
            });
            offset += fsize;
        }

        gt.BlockLength = offset;
        return gt;
    }

    private GroupTemplate BuildGroupTemplate(FieldDescriptor fd)
    {
        var md = fd.MessageType;
        var gt = new GroupTemplate { Fd = fd };
        ushort offset = 0;

        foreach (var f in md.Fields.InFieldNumberOrder())
        {
            if (f.IsMap || f.IsRepeated) throw new NotSupportedException("SBE: nested repeated/map fields in groups not supported");

            if (f.FieldType == FieldType.Message)
            {
                var (size, subFields) = BuildCompositeFields(f.MessageType);
                gt.Fields.Add(new FieldTemplate
                {
                    Fd = f,
                    Offset = offset,
                    Size = size,
                    Composite = subFields
                });
                offset += size;
                continue;
            }

            var (enc, fsize) = GetFieldEncodingSize(f);
            gt.Fields.Add(new FieldTemplate
            {
                Fd = f,
                Offset = offset,
                Size = fsize,
                Encoding = enc
            });
            offset += fsize;
        }

        gt.BlockLength = offset;
        return gt;
    }

    private (ushort, List<FieldTemplate>) BuildCompositeFields(MessageDescriptor md)
    {
        var fts = new List<FieldTemplate>();
        ushort offset = 0;

        foreach (var fd in md.Fields.InFieldNumberOrder())
        {
            if (fd.IsRepeated || fd.IsMap) throw new NotSupportedException("SBE: list/map fields in composites not supported");

            if (fd.FieldType == FieldType.Message)
            {
                var (size, subFields) = BuildCompositeFields(fd.MessageType);
                fts.Add(new FieldTemplate
                {
                    Fd = fd,
                    Offset = offset,
                    Size = size,
                    Composite = subFields
                });
                offset += size;
                continue;
            }

            var (enc, fsize) = GetFieldEncodingSize(fd);
            fts.Add(new FieldTemplate
            {
                Fd = fd,
                Offset = offset,
                Size = fsize,
                Encoding = enc
            });
            offset += fsize;
        }

        return (offset, fts);
    }

    private (string, ushort) GetFieldEncodingSize(FieldDescriptor fd, ushort inheritedLength = 0)
    {
        var enc = fd.GetOptions()?.GetExtension(AnnotationsExtensions.Encoding);
        if (!string.IsNullOrEmpty(enc))
        {
            ushort size = enc switch
            {
                "int8" or "uint8" => 1,
                "int16" or "uint16" => 2,
                "int32" or "uint32" or "float" => 4,
                "int64" or "uint64" or "double" => 8,
                _ => throw new Exception($"Unknown SBE encoding {enc}")
            };
            return (enc, size);
        }

        return fd.FieldType switch
        {
            FieldType.Bool => ("uint8", 1),
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => ("int32", 4),
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => ("int64", 8),
            FieldType.UInt32 or FieldType.Fixed32 => ("uint32", 4),
            FieldType.UInt64 or FieldType.Fixed64 => ("uint64", 8),
            FieldType.Float => ("float", 4),
            FieldType.Double => ("double", 8),
            FieldType.Enum => ("uint8", 1),
            FieldType.String or FieldType.Bytes => GetStringBytesEncoding(fd, inheritedLength),
            _ => throw new NotSupportedException($"Unsupported SBE kind {fd.FieldType}")
        };
    }

    private (string, ushort) GetStringBytesEncoding(FieldDescriptor fd, ushort inheritedLength)
    {
        var length = fd.GetOptions()?.GetExtension(AnnotationsExtensions.Length) ?? 0;
        if (length == 0) length = inheritedLength;
        if (length == 0) throw new Exception($"{fd.FieldType} field {fd.FullName} requires (sbe.length) annotation");
        return ("char", (ushort)length);
    }

    /// <summary>
    /// Encodes a Protobuf message into SBE binary format.
    /// </summary>
    /// <param name="msg">The message to encode.</param>
    /// <returns>The SBE binary data.</returns>
    public byte[] Marshal(IMessage msg)
    {
        if (!_byName.TryGetValue(msg.Descriptor.FullName, out var tmpl))
            throw new Exception($"No SBE template for {msg.Descriptor.FullName}");

        int totalSize = HeaderSize + tmpl.BlockLength;
        foreach (var gt in tmpl.Groups)
        {
            var val = gt.Fd.Accessor.GetValue(msg);
            int count = (val is System.Collections.ICollection c) ? c.Count : (val is System.Collections.IEnumerable e) ? e.Cast<object>().Count() : 0;
            totalSize += GroupHeaderSize + count * gt.BlockLength;
        }

        var buf = new byte[totalSize];
        var span = buf.AsSpan();

        BinaryPrimitives.WriteUInt16LittleEndian(span[0..], tmpl.BlockLength);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], tmpl.TemplateID);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], tmpl.SchemaID);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], tmpl.Version);

        var rootBlock = span[HeaderSize..(HeaderSize + tmpl.BlockLength)];
        foreach (var ft in tmpl.Fields)
        {
            WriteField(rootBlock, ft, msg);
        }

        int pos = HeaderSize + tmpl.BlockLength;
        foreach (var gt in tmpl.Groups)
        {
            pos += MarshalGroup(span[pos..], msg, gt);
        }

        return buf;
    }

    private void WriteField(Span<byte> block, FieldTemplate ft, IMessage msg)
    {
        if (ft.Composite != null)
        {
            var subMsg = (IMessage)ft.Fd.Accessor.GetValue(msg);
            var sub = block[ft.Offset..(ft.Offset + ft.Size)];
            foreach (var sf in ft.Composite)
            {
                WriteField(sub, sf, subMsg);
            }
            return;
        }

        var val = ft.Fd.Accessor.GetValue(msg);
        var off = ft.Offset;

        switch (ft.Encoding)
        {
            case "int8": block[off] = (byte)(sbyte)Convert.ToInt64(val); break;
            case "int16": BinaryPrimitives.WriteInt16LittleEndian(block[off..], (short)Convert.ToInt64(val)); break;
            case "int32": BinaryPrimitives.WriteInt32LittleEndian(block[off..], (int)Convert.ToInt64(val)); break;
            case "int64": BinaryPrimitives.WriteInt64LittleEndian(block[off..], Convert.ToInt64(val)); break;
            case "uint8": block[off] = (byte)Convert.ToUInt64(val); break;
            case "uint16": BinaryPrimitives.WriteUInt16LittleEndian(block[off..], (ushort)Convert.ToUInt64(val)); break;
            case "uint32": BinaryPrimitives.WriteUInt32LittleEndian(block[off..], (uint)Convert.ToUInt64(val)); break;
            case "uint64": BinaryPrimitives.WriteUInt64LittleEndian(block[off..], Convert.ToUInt64(val)); break;
            case "float": BinaryPrimitives.WriteSingleLittleEndian(block[off..], Convert.ToSingle(val)); break;
            case "double": BinaryPrimitives.WriteDoubleLittleEndian(block[off..], Convert.ToDouble(val)); break;
            case "char":
                var target = block[off..(off + ft.Size)];
                target.Clear();
                if (val is string s) System.Text.Encoding.UTF8.GetBytes(s, target);
                else if (val is ByteString bs) bs.Span.CopyTo(target);
                break;
        }
    }

    private int MarshalGroup(Span<byte> buf, IMessage msg, GroupTemplate gt)
    {
        var val = gt.Fd.Accessor.GetValue(msg);
        var entries = new List<object>();
        if (val is System.Collections.IDictionary dict)
        {
            foreach (System.Collections.DictionaryEntry entry in dict) entries.Add(entry);
        }
        else if (val is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable) entries.Add(item!);
        }
        
        int n = entries.Count;

        BinaryPrimitives.WriteUInt16LittleEndian(buf[0..], gt.BlockLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buf[2..], (ushort)n);

        for (int i = 0; i < n; i++)
        {
            var start = GroupHeaderSize + i * gt.BlockLength;
            var entry = buf[start..(start + gt.BlockLength)];
            
            if (gt.IsScalarGroup)
            {
                WriteScalarValue(entry, gt.Fields[0], entries[i]);
            }
            else if (gt.IsMapGroup)
            {
                var entryDict = (System.Collections.DictionaryEntry)entries[i];
                WriteScalarValue(entry, gt.Fields[0], entryDict.Key);
                WriteScalarValue(entry, gt.Fields[1], entryDict.Value!);
            }
            else
            {
                var entryMsg = (IMessage)entries[i];
                foreach (var ft in gt.Fields)
                {
                    WriteField(entry, ft, entryMsg);
                }
            }
        }

        return GroupHeaderSize + n * gt.BlockLength;
    }

    private void WriteScalarValue(Span<byte> block, FieldTemplate ft, object val)
    {
        var off = ft.Offset;

        switch (ft.Encoding)
        {
            case "int8": block[off] = (byte)(sbyte)Convert.ToInt64(val); break;
            case "int16": BinaryPrimitives.WriteInt16LittleEndian(block[off..], (short)Convert.ToInt64(val)); break;
            case "int32": BinaryPrimitives.WriteInt32LittleEndian(block[off..], (int)Convert.ToInt64(val)); break;
            case "int64": BinaryPrimitives.WriteInt64LittleEndian(block[off..], Convert.ToInt64(val)); break;
            case "uint8": block[off] = (byte)Convert.ToUInt64(val); break;
            case "uint16": BinaryPrimitives.WriteUInt16LittleEndian(block[off..], (ushort)Convert.ToUInt64(val)); break;
            case "uint32": BinaryPrimitives.WriteUInt32LittleEndian(block[off..], (uint)Convert.ToUInt64(val)); break;
            case "uint64": BinaryPrimitives.WriteUInt64LittleEndian(block[off..], Convert.ToUInt64(val)); break;
            case "float": BinaryPrimitives.WriteSingleLittleEndian(block[off..], Convert.ToSingle(val)); break;
            case "double": BinaryPrimitives.WriteDoubleLittleEndian(block[off..], Convert.ToDouble(val)); break;
            case "char":
                var target = block[off..(off + ft.Size)];
                target.Clear();
                if (val is string s) System.Text.Encoding.UTF8.GetBytes(s, target);
                else if (val is ByteString bs) bs.Span.CopyTo(target);
                break;
        }
    }

    /// <summary>
    /// Decodes SBE binary data into a Protobuf message.
    /// </summary>
    /// <param name="data">The SBE binary data.</param>
    /// <param name="msg">The message to populate.</param>
    public void Unmarshal(byte[] data, IMessage msg)
    {
        if (data.Length < HeaderSize) throw new Exception("SBE: data too short for header");

        var span = data.AsSpan();
        ushort blockLength = BinaryPrimitives.ReadUInt16LittleEndian(span[0..]);
        ushort templateID = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);

        if (!_byID.TryGetValue(templateID, out var tmpl))
            throw new Exception($"SBE: unknown template ID {templateID}");

        int end = HeaderSize + blockLength;
        if (data.Length < end) throw new Exception("SBE: data too short for root block");

        var rootBlock = span[HeaderSize..end];
        foreach (var ft in tmpl.Fields)
        {
            ReadField(rootBlock, ft, msg);
        }

        int pos = end;
        foreach (var gt in tmpl.Groups)
        {
            pos += UnmarshalGroup(span[pos..], msg, gt);
        }
    }

    private void ReadField(ReadOnlySpan<byte> block, FieldTemplate ft, IMessage msg)
    {
        if (ft.Composite != null)
        {
            var subMsg = (IMessage)ft.Fd.Accessor.GetValue(msg);
            if (subMsg == null)
            {
                subMsg = ft.Fd.MessageType.Parser.ParseFrom(Array.Empty<byte>());
                ft.Fd.Accessor.SetValue(msg, subMsg);
            }
            var sub = block[ft.Offset..(ft.Offset + ft.Size)];
            foreach (var sf in ft.Composite)
            {
                ReadField(sub, sf, subMsg);
            }
            return;
        }

        var off = ft.Offset;
        object? val = ft.Encoding switch
        {
            "int8" => (sbyte)block[off],
            "int16" => BinaryPrimitives.ReadInt16LittleEndian(block[off..]),
            "int32" => BinaryPrimitives.ReadInt32LittleEndian(block[off..]),
            "int64" => BinaryPrimitives.ReadInt64LittleEndian(block[off..]),
            "uint8" => block[off],
            "uint16" => BinaryPrimitives.ReadUInt16LittleEndian(block[off..]),
            "uint32" => BinaryPrimitives.ReadUInt32LittleEndian(block[off..]),
            "uint64" => BinaryPrimitives.ReadUInt64LittleEndian(block[off..]),
            "float" => BinaryPrimitives.ReadSingleLittleEndian(block[off..]),
            "double" => BinaryPrimitives.ReadDoubleLittleEndian(block[off..]),
            "char" => ReadStringBytes(block[off..(off + ft.Size)], ft.Fd),
            _ => throw new Exception($"Unknown SBE encoding {ft.Encoding}")
        };

        if (ft.Fd.FieldType == FieldType.Bool) val = Convert.ToByte(val) != 0;
        else if (ft.Fd.FieldType == FieldType.Enum) val = (int)Convert.ToUInt64(val);
        else if (ft.Fd.FieldType == FieldType.Int32) val = (int)Convert.ToInt64(val);
        else if (ft.Fd.FieldType == FieldType.UInt32) val = (uint)Convert.ToUInt64(val);

        if (ft.Fd.ContainingOneof != null && IsZero(val)) return;

        ft.Fd.Accessor.SetValue(msg, val);
    }

    private static bool IsZero(object? val)
    {
        if (val == null) return true;
        if (val is bool b) return !b;
        if (val is int i) return i == 0;
        if (val is long l) return l == 0;
        if (val is uint ui) return ui == 0;
        if (val is ulong ul) return ul == 0;
        if (val is float f) return f == 0;
        if (val is double d) return d == 0;
        if (val is string s) return string.IsNullOrEmpty(s);
        if (val is ByteString bs) return bs.Length == 0;
        if (val is byte[] bytes) return bytes.Length == 0;
        return false;
    }

    private object ReadStringBytes(ReadOnlySpan<byte> data, FieldDescriptor fd)
    {
        if (fd.FieldType == FieldType.Bytes)
        {
            return ByteString.CopyFrom(data);
        }
        // Trim trailing nulls for strings
        int n = data.Length;
        while (n > 0 && data[n - 1] == 0) n--;
        return StrictUtf8.GetString(data[..n]);
    }

    private int UnmarshalGroup(ReadOnlySpan<byte> buf, IMessage msg, GroupTemplate gt)
    {
        if (buf.Length < GroupHeaderSize) throw new Exception("SBE: data too short for group header");

        ushort blockLength = BinaryPrimitives.ReadUInt16LittleEndian(buf[0..]);
        ushort numInGroup = BinaryPrimitives.ReadUInt16LittleEndian(buf[2..]);

        // HARDENING.md § SBE step 3-4: count * blockLength must be checked in
        // 64-bit before allocating; reject blockLength==0 with count>0 to
        // foreclose unbounded writer-state allocation that consumes no input.
        if (blockLength == 0 && numInGroup > 0)
            throw new Exception("SBE: group blockLength=0 with count>0");
        ulong groupBytes = (ulong)numInGroup * blockLength;
        ulong totalSizeChecked = (ulong)GroupHeaderSize + groupBytes;
        if (totalSizeChecked > (ulong)buf.Length)
            throw new Exception("SBE: data too short for group entries");
        int totalSize = (int)totalSizeChecked;

        var val = gt.Fd.Accessor.GetValue(msg);
        for (int i = 0; i < numInGroup; i++)
        {
            var start = GroupHeaderSize + i * blockLength;
            var entry = buf[start..(start + blockLength)];
            
            if (gt.IsScalarGroup)
            {
                var sval = ReadScalarValue(entry, gt.Fields[0]);
                ((System.Collections.IList)val).Add(sval);
            }
            else if (gt.IsMapGroup)
            {
                var key = ReadScalarValue(entry, gt.Fields[0]);
                var vval = ReadScalarValue(entry, gt.Fields[1]);
                ((System.Collections.IDictionary)val)[key] = vval;
            }
            else
            {
                var entryMsg = gt.Fd.MessageType.Parser.ParseFrom(Array.Empty<byte>());
                foreach (var ft in gt.Fields)
                {
                    ReadField(entry, ft, entryMsg);
                }
                ((System.Collections.IList)val).Add(entryMsg);
            }
        }

        return totalSize;
    }

    private object ReadScalarValue(ReadOnlySpan<byte> block, FieldTemplate ft)
    {
        var off = ft.Offset;
        object? val = ft.Encoding switch
        {
            "int8" => (sbyte)block[off],
            "int16" => BinaryPrimitives.ReadInt16LittleEndian(block[off..]),
            "int32" => BinaryPrimitives.ReadInt32LittleEndian(block[off..]),
            "int64" => BinaryPrimitives.ReadInt64LittleEndian(block[off..]),
            "uint8" => block[off],
            "uint16" => BinaryPrimitives.ReadUInt16LittleEndian(block[off..]),
            "uint32" => BinaryPrimitives.ReadUInt32LittleEndian(block[off..]),
            "uint64" => BinaryPrimitives.ReadUInt64LittleEndian(block[off..]),
            "float" => BinaryPrimitives.ReadSingleLittleEndian(block[off..]),
            "double" => BinaryPrimitives.ReadDoubleLittleEndian(block[off..]),
            "char" => ReadStringBytes(block[off..(off + ft.Size)], ft.Fd),
            _ => throw new Exception($"Unknown SBE encoding {ft.Encoding}")
        };

        if (ft.Fd.FieldType == FieldType.Bool) val = Convert.ToByte(val) != 0;
        else if (ft.Fd.FieldType == FieldType.Enum) val = (int)Convert.ToUInt64(val);
        else if (ft.Fd.FieldType == FieldType.Int32) val = (int)Convert.ToInt64(val);
        else if (ft.Fd.FieldType == FieldType.UInt32) val = (uint)Convert.ToUInt64(val);

        return val!;
    }

    /// <summary>
    /// Creates a zero-allocation View over SBE-encoded data.
    /// </summary>
    /// <param name="data">The SBE binary data.</param>
    /// <returns>A View instance for reading fields directly from the buffer.</returns>
    public View CreateView(byte[] data) => new(data, _byID);
}

internal partial class MessageTemplate
{
    public ushort TemplateID { get; set; }
    public ushort SchemaID { get; set; }
    public ushort Version { get; set; }
    public ushort BlockLength { get; set; }
    public List<FieldTemplate> Fields { get; set; } = new();
    public List<GroupTemplate> Groups { get; set; } = new();
}

internal partial class FieldTemplate
{
    public FieldDescriptor Fd { get; set; } = null!;
    public ushort Offset { get; set; }
    public ushort Size { get; set; }
    public string? Encoding { get; set; }
    public List<FieldTemplate>? Composite { get; set; }
}

internal class GroupTemplate
{
    public FieldDescriptor Fd { get; set; } = null!;
    public ushort BlockLength { get; set; }
    public List<FieldTemplate> Fields { get; set; } = new();
    public bool IsScalarGroup { get; set; }
    public bool IsMapGroup { get; set; }
}
