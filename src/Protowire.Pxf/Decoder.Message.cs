// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using System.Collections;
using System.Globalization;
using System.Numerics;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Protowire.Pxf;

// The descriptor-driven decoder, for generated messages. The schema says what
// every value must be, so a value of the wrong shape is refused rather than
// guessed at. Mirrors directDecoder in protowire-go/encoding/pxf/decode_fast.go.
public partial class Decoder
{
    internal const string TypeEntryMisplaced =
        "@type is allowed only as the first entry of a block bound to google.protobuf.Any (draft -01 \"Any Blocks\")";

    // Keys each map field has bound in this decode, by the message that holds
    // the map. It records what this decode bound rather than asking the map,
    // which may hold entries the caller set before decoding into it.
    private Dictionary<object, Dictionary<FieldDescriptor, HashSet<object>>> _mapKeys = null!;

    private void DecodeMessage(IMessage msg, bool inBlock)
    {
        if (inBlock) EnterDescent();
        try
        {
            DecodeMessageBody(msg, inBlock);
        }
        finally
        {
            if (inBlock) _depth--;
        }
    }

    private void DecodeMessageBody(IMessage msg, bool inBlock)
    {
        var desc = msg.Descriptor;
        // A block bound to google.protobuf.Any may open with a type entry, in
        // every place a body is written. Without one, the block is the Any's
        // own fields, type_url and value.
        if (inBlock && _current.Kind == TokenKind.AT_TYPE && WellKnown.IsAny(desc))
        {
            DecodeAnyBody(msg);
            return;
        }

        var keyedElem = _keyedElem;
        _keyedElem = null;
        Dictionary<OneofDescriptor, string>? setOneofs = null;
        var bound = new HashSet<int>();

        while (true)
        {
            if (inBlock && _current.Kind == TokenKind.RBRACE)
            {
                Advance();
                return;
            }
            if (_current.Kind == TokenKind.EOF)
            {
                if (inBlock) throw new PxfException(_current.Pos, "expected '}', got EOF");
                return;
            }

            var pos = _current.Pos;
            if (_current.Kind == TokenKind.AT_TYPE) throw new PxfException(pos, TypeEntryMisplaced);
            if (_current.Kind is not (TokenKind.IDENT or TokenKind.STRING or TokenKind.INT or TokenKind.BOOL))
            {
                throw new PxfException(pos, $"expected identifier, string, integer, or bool, got {_current.Kind} (\"{_current.Value}\")");
            }
            var keyKind = _current.Kind;
            string key = _current.Value;
            Advance();

            switch (_current.Kind)
            {
                case TokenKind.EQUALS:
                {
                    if (keyKind is TokenKind.INT or TokenKind.BOOL) throw Parser.NonNameAssignment(pos, keyKind, key);
                    Advance();
                    // A string at entry-name position is well-formed, but the
                    // schema gives it a meaning only in a keyed block.
                    if (keyKind == TokenKind.STRING) throw Keyed.QuotedNameUnkeyed(pos, key);
                    var fd = desc.FindFieldByName(key);
                    if (fd == null)
                    {
                        if (!DiscardUnknown) throw UnknownField(pos, key, desc);
                        SkipValue();
                        continue;
                    }
                    bool isNull = _current.Kind == TokenKind.NULL;
                    keyedElem?.CheckBinding(fd, isNull, pos);
                    CheckOneof(fd, ref setOneofs, pos);
                    Bind(fd, bound, pos);
                    if (isNull)
                    {
                        if (!Nullable(fd)) throw NullNotAllowed(_current.Pos, fd);
                        if (_result != null)
                        {
                            string path = _pathPrefix + fd.Name;
                            _result.MarkNull(path);
                            // A FieldMask path names fields, not list elements
                            // or map entries.
                            if (_nullMaskFd != null && _rootMsg != null && _elemDepth == 0)
                            {
                                AnnotationsRuntime.AppendNullPath(_rootMsg, _nullMaskFd, path);
                            }
                        }
                        Advance();
                        continue;
                    }
                    if (keyedElem != null && fd.Name == keyedElem.KeyName && _current.Kind == TokenKind.STRING)
                    {
                        keyedElem.CheckExplicitKey(_current.Value, _current.Pos);
                    }
                    _result?.MarkPresent(_pathPrefix + fd.Name);
                    DecodeFieldValue(msg, fd);
                    break;
                }

                case TokenKind.LBRACE:
                {
                    if (keyKind is TokenKind.INT or TokenKind.BOOL) throw Parser.NonNameBlock(pos, keyKind, key);
                    if (keyKind == TokenKind.STRING) throw Keyed.QuotedNameUnkeyed(pos, key);
                    var fd = desc.FindFieldByName(key);
                    if (fd == null)
                    {
                        if (!DiscardUnknown) throw UnknownField(pos, key, desc);
                        SkipValue();
                        continue;
                    }
                    Advance();
                    if (fd.FieldType is not (FieldType.Message or FieldType.Group) || fd.IsMap && !fd.IsRepeated)
                    {
                        throw new PxfException(pos, $"field \"{key}\" is not a message type, cannot use block syntax");
                    }
                    if (fd.IsMap)
                    {
                        throw new PxfException(pos, $"map field \"{key}\" must use assignment syntax: {key} = {{ ... }}");
                    }
                    if (fd.IsRepeated)
                    {
                        if (Keyed.KeyField(fd) is { } keyFd)
                        {
                            _result?.MarkPresent(_pathPrefix + fd.Name);
                            DecodeKeyedBlock(ListOf(msg, fd), fd, keyFd);
                            continue;
                        }
                        // The block spells the keyed form on a field with no
                        // (pxf.key): the quoted name is the more specific error.
                        if (_current.Kind == TokenKind.STRING) throw Keyed.QuotedNameUnkeyed(_current.Pos, _current.Value);
                        throw new PxfException(pos, $"repeated field \"{key}\" must use list syntax: {key} = [...]");
                    }
                    keyedElem?.CheckBinding(fd, false, pos);
                    CheckOneof(fd, ref setOneofs, pos);
                    Bind(fd, bound, pos);
                    _result?.MarkPresent(_pathPrefix + fd.Name);
                    DecodeSingularBody(msg, fd);
                    break;
                }

                case TokenKind.COLON:
                    if (!inBlock) throw Parser.TopLevelMapEntry(pos);
                    throw new PxfException(pos, "unexpected ':' in message context, use '=' for field assignments");

                default:
                    throw new PxfException(_current.Pos, $"expected '=', ':', or '{{' after \"{key}\", got {_current.Kind}");
            }
        }
    }

    private static PxfException UnknownField(Position pos, string key, MessageDescriptor desc) =>
        new(pos, $"unknown field \"{key}\" in {desc.FullName}");

    // A singular field is bound at most once in a body, null included, so a
    // second scalar does not replace the first and a second block does not
    // merge into it. Repeated fields concatenate, and each body has its own set.
    private static void Bind(FieldDescriptor fd, HashSet<int> bound, Position pos)
    {
        if (fd.IsRepeated || fd.IsMap) return;
        if (!bound.Add(fd.FieldNumber)) throw new PxfException(pos, $"field \"{fd.Name}\" is already set");
    }

    private static void CheckOneof(FieldDescriptor fd, ref Dictionary<OneofDescriptor, string>? set, Position pos)
    {
        var oo = fd.RealContainingOneof;
        if (oo == null) return;
        set ??= [];
        if (set.TryGetValue(oo, out var prev))
        {
            throw new PxfException(pos, $"oneof \"{oo.Name}\": field \"{fd.Name}\" conflicts with already-set field \"{prev}\"");
        }
        set[oo] = fd.Name;
    }

    // Explicit presence, so null has an unset state to mean: a message field,
    // a proto3 optional scalar, a oneof member or a proto2 optional field. A
    // required field would make the message invalid in binary.
    private static bool Nullable(FieldDescriptor fd) =>
        fd.HasPresence && !fd.IsRepeated && !fd.IsMap && !fd.IsRequired;

    private static PxfException NullNotAllowed(Position pos, FieldDescriptor fd)
    {
        bool isMsg = fd.FieldType is FieldType.Message or FieldType.Group;
        if (fd.IsMap) return new(pos, $"null is not allowed on map field \"{fd.Name}\"");
        if (fd.IsRepeated) return new(pos, $"null is not allowed on repeated field \"{fd.Name}\"");
        if (fd.IsRequired && isMsg) return new(pos, $"null is not allowed on required field \"{fd.Name}\": it must be set");
        if (fd.IsRequired) return new(pos, $"null is not allowed on scalar field \"{fd.Name}\": the field is required, so it must be set");
        return new(pos, $"null is not allowed on scalar field \"{fd.Name}\": declare it optional, or as a wrapper type, to make it nullable");
    }

    private void DecodeFieldValue(IMessage msg, FieldDescriptor fd)
    {
        if (fd.IsMap)
        {
            DecodeMapField(msg, fd);
            return;
        }
        if (fd.IsRepeated)
        {
            // A block tail abbreviates `= { ... }`, so the keyed block form
            // has an assignment spelling.
            if (_current.Kind == TokenKind.LBRACE && Keyed.KeyField(fd) is { } keyFd)
            {
                Advance();
                DecodeKeyedBlock(ListOf(msg, fd), fd, keyFd);
                return;
            }
            DecodeListField(msg, fd);
            return;
        }
        if (fd.FieldType is FieldType.Message or FieldType.Group)
        {
            DecodeSingularMessage(msg, fd);
            return;
        }
        fd.Accessor.SetValue(msg, ConsumeScalar(fd, fd));
    }

    private void DecodeSingularMessage(IMessage msg, FieldDescriptor fd)
    {
        var mdesc = fd.MessageType;
        if (OnSecretField != null && WellKnown.IsSecret(mdesc) && _current.Kind == TokenKind.STRING)
        {
            string path = _pathPrefix + fd.Name;
            CallSecretHook(path);
            Advance();
            // The Secret is set, so presence and its metadata read as usual,
            // but its value is left to the hook.
            fd.Accessor.SetValue(msg, fd.Accessor.GetValue(msg) as IMessage ?? NewMessage(mdesc));
            _result?.MarkPresent(path + ".value");
            return;
        }
        if (TryShorthand(mdesc, fd, out var value, out var inner))
        {
            fd.Accessor.SetValue(msg, value!);
            foreach (string name in inner)
            {
                _result?.MarkPresent($"{_pathPrefix}{fd.Name}.{name}");
            }
            return;
        }
        if (_current.Kind != TokenKind.LBRACE)
        {
            throw new PxfException(_current.Pos, $"expected {MessageValueForms(mdesc)} for message field \"{fd.Name}\", got {_current.Kind} (\"{_current.Value}\")");
        }
        Advance();
        DecodeSingularBody(msg, fd);
    }

    // The body of a singular message field, its '{' consumed, for both
    // spellings of it. Presence inside is recorded under the field, never
    // under its parent, where it would satisfy the parent's (pxf.required).
    private void DecodeSingularBody(IMessage msg, FieldDescriptor fd)
    {
        if (OnSecretField != null && WellKnown.IsSecret(fd.MessageType))
        {
            var secret = fd.Accessor.GetValue(msg) as IMessage ?? NewMessage(fd.MessageType);
            fd.Accessor.SetValue(msg, secret);
            DecodeSecretBlock(secret, _pathPrefix + fd.Name);
            return;
        }
        bool wrapper = WellKnown.IsWrapper(fd.MessageType);
        var sub = wrapper ? NewMessage(fd.MessageType) : (fd.Accessor.GetValue(msg) as IMessage ?? NewMessage(fd.MessageType));
        if (!wrapper) fd.Accessor.SetValue(msg, sub);
        string saved = _pathPrefix;
        _pathPrefix = $"{saved}{fd.Name}.";
        try
        {
            DecodeMessage(sub, true);
        }
        finally
        {
            _pathPrefix = saved;
        }
        if (wrapper) fd.Accessor.SetValue(msg, WellKnown.Unwrap(sub));
    }

    private static IMessage NewMessage(MessageDescriptor desc) => desc.Parser.ParseFrom(ByteString.Empty);

    private static string MessageValueForms(MessageDescriptor mdesc)
    {
        if (WellKnown.IsTimestamp(mdesc)) return "a timestamp or '{'";
        if (WellKnown.IsDuration(mdesc)) return "a duration or '{'";
        if (WellKnown.IsWrapper(mdesc)) return "a scalar or '{'";
        if (WellKnown.IsBigInt(mdesc)) return "an integer or '{'";
        if (WellKnown.IsDecimal(mdesc) || WellKnown.IsBigFloat(mdesc)) return "a number or '{'";
        if (WellKnown.IsSecret(mdesc)) return "a string or '{'";
        return "'{'";
    }

    /// <summary>
    /// The scalar shorthand of a well-known message type, if the current token
    /// is one. <paramref name="value"/> is what the field stores: a message, or
    /// the bare scalar for a wrapper, which generated code holds unboxed.
    /// <paramref name="inner"/> names the fields the shorthand sets.
    /// </summary>
    private bool TryShorthand(MessageDescriptor mdesc, FieldDescriptor named, out object? value, out string[] inner)
    {
        value = null;
        inner = [];
        var pos = _current.Pos;
        var kind = _current.Kind;
        if (WellKnown.IsTimestamp(mdesc) && kind == TokenKind.TIMESTAMP)
        {
            if (!Rfc3339.TryParse(_current.Value, out long seconds, out int nanos))
            {
                throw new PxfException(pos, $"invalid timestamp \"{_current.Value}\"");
            }
            value = WellKnown.NewSecondsNanos(mdesc, seconds, nanos);
            inner = ["seconds", "nanos"];
        }
        else if (WellKnown.IsDuration(mdesc) && kind == TokenKind.DURATION)
        {
            if (!DurationParser.TryParseNanos(_current.Value, out long total))
            {
                throw new PxfException(pos, $"invalid duration \"{_current.Value}\"");
            }
            value = WellKnown.NewSecondsNanos(mdesc, total / 1_000_000_000, (int)(total % 1_000_000_000));
            inner = ["seconds", "nanos"];
        }
        else if (kind != TokenKind.LBRACE && WellKnown.IsWrapper(mdesc))
        {
            value = ConsumeScalar(mdesc.FindFieldByName("value"), named);
            inner = ["value"];
            return true;
        }
        else if (WellKnown.IsBigInt(mdesc) && kind == TokenKind.INT)
        {
            RefuseSuffix(named);
            CheckLiteralDigits(_current.Value, pos);
            if (!BigInteger.TryParse(_current.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var bi))
            {
                throw new PxfException(pos, $"invalid big integer: {_current.Value}");
            }
            var msg = NewMessage(mdesc);
            WellKnown.SetBigInt(msg, bi);
            value = msg;
            inner = ["abs", "negative"];
        }
        else if (WellKnown.IsDecimal(mdesc) && kind is TokenKind.INT or TokenKind.FLOAT)
        {
            RefuseSuffix(named);
            var msg = NewMessage(mdesc);
            WellKnown.SetDecimal(msg, ParseDecimal(_current.Value, pos));
            value = msg;
            inner = ["unscaled", "scale", "negative"];
        }
        else if (WellKnown.IsBigFloat(mdesc) && kind is TokenKind.INT or TokenKind.FLOAT)
        {
            var msg = NewMessage(mdesc);
            WellKnown.SetBigFloat(msg, ParseBigFloatToken(pos));
            value = msg;
            inner = ["mantissa", "exponent", "prec", "negative"];
        }
        else if (WellKnown.IsSecret(mdesc) && kind == TokenKind.STRING)
        {
            var msg = NewMessage(mdesc);
            mdesc.FindFieldByName("value").Accessor.SetValue(msg, _current.Value);
            value = msg;
            inner = ["value"];
        }
        else
        {
            return false;
        }
        Advance();
        return true;
    }

    // A precision suffix lexes on any number, and only a BigFloat reads it.
    private void RefuseSuffix(FieldDescriptor named)
    {
        if (_lex.Prec.Length > 0 && _current.Kind is TokenKind.INT or TokenKind.FLOAT)
        {
            throw new PxfException(_current.Pos,
                $"precision suffix p{_lex.Prec} is allowed only on a pxf.BigFloat, not on field \"{named.Name}\"");
        }
    }

    private Protowire.Pb.Decimal ParseDecimal(string literal, Position pos)
    {
        CheckLiteralDigits(literal, pos);
        string s = literal;
        bool negative = s.StartsWith('-');
        if (negative) s = s[1..];
        int dot = s.IndexOf('.');
        int scale = 0;
        if (dot >= 0)
        {
            scale = s.Length - dot - 1;
            s = s.Remove(dot, 1);
        }
        CheckDecimalScale(scale, pos);
        if (!BigInteger.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var unscaled))
        {
            throw new PxfException(pos, $"invalid decimal: {literal}");
        }
        return new Protowire.Pb.Decimal(unscaled, scale, negative);
    }

    private Protowire.Pb.BigFloat ParseBigFloatToken(Position pos)
    {
        try
        {
            return BigFloatText.Parse(_current.Value, _lex.Prec, _limits.MaxNumericLiteralDigits, _limits.MaxBigFloatExponent);
        }
        catch (FormatException e)
        {
            throw new PxfException(pos, e.Message);
        }
    }

    // Whether the current token starts a list element or map value that
    // OnSecretField takes: either form of a pxf.Secret.
    private bool HooksSecret(MessageDescriptor mdesc) =>
        OnSecretField != null && WellKnown.IsSecret(mdesc) && _current.Kind is TokenKind.STRING or TokenKind.LBRACE;

    // A list element or map value HooksSecret accepted, reported at path: the
    // Secret it stands for, its value given to the hook rather than stored.
    private IMessage HookedSecret(MessageDescriptor mdesc, string path)
    {
        var secret = NewMessage(mdesc);
        if (_current.Kind == TokenKind.STRING)
        {
            CallSecretHook(path);
            Advance();
            return secret;
        }
        Advance();
        DecodeSecretBlock(secret, path);
        return secret;
    }

    // Hands the current string token to OnSecretField. An exception from the
    // hook aborts the decode at the value's position, naming the path.
    private void CallSecretHook(string path)
    {
        var pos = _current.Pos;
        try
        {
            OnSecretField!(path, _current.Value);
        }
        catch (Exception e)
        {
            throw new PxfException(pos, $"pxf.Secret hook for {GoQuote(path)}: {e.Message}", e);
        }
    }

    /// <summary>
    /// The block form of a pxf.Secret under <see cref="OnSecretField"/>, its
    /// <c>{</c> consumed: <c>value</c> goes to the hook, <c>hint</c> and
    /// <c>fingerprint</c> onto <paramref name="secret"/>. The Secret is a
    /// closed shape of string fields, each bound at most once, so anything
    /// else is an error. Mirrors <c>decodeSecretBlockInto</c> in
    /// <c>protowire-go/encoding/pxf/decode_fast.go</c>.
    /// </summary>
    private void DecodeSecretBlock(IMessage secret, string path)
    {
        EnterDescent();
        try
        {
            var desc = secret.Descriptor;
            var valueFd = desc.FindFieldByName("value");
            var hintFd = desc.FindFieldByName("hint");
            var fingerprintFd = desc.FindFieldByName("fingerprint");
            var bound = new HashSet<int>();
            while (true)
            {
                switch (_current.Kind)
                {
                    case TokenKind.RBRACE:
                        Advance();
                        return;
                    case TokenKind.EOF:
                        throw new PxfException(_current.Pos, "expected '}' to close pxf.Secret block, got EOF");
                    case TokenKind.IDENT:
                        break;
                    default:
                        throw new PxfException(_current.Pos,
                            $"expected pxf.Secret field name (value/hint/fingerprint), got {_current.Kind} (\"{_current.Value}\")");
                }
                string name = _current.Value;
                var namePos = _current.Pos;
                Advance();
                if (_current.Kind != TokenKind.EQUALS)
                {
                    throw new PxfException(_current.Pos, $"expected '=' after pxf.Secret field \"{name}\", got {_current.Kind}");
                }
                Advance();
                if (_current.Kind != TokenKind.STRING)
                {
                    throw new PxfException(_current.Pos, $"expected string value for pxf.Secret {name}, got {_current.Kind}");
                }
                FieldDescriptor? fd = name switch
                {
                    "value" => valueFd,
                    "hint" => hintFd,
                    "fingerprint" => fingerprintFd,
                    _ => throw new PxfException(namePos, $"unknown pxf.Secret field \"{name}\" (expected value/hint/fingerprint)"),
                };
                if (fd == null)
                {
                    throw new PxfException(namePos, $"unknown pxf.Secret field \"{name}\": this schema does not declare it");
                }
                Bind(fd, bound, namePos);
                if (name == "value")
                {
                    CallSecretHook(path);
                }
                else
                {
                    fd.Accessor.SetValue(secret, _current.Value);
                }
                _result?.MarkPresent($"{path}.{name}");
                Advance();
            }
        }
        finally
        {
            _depth--;
        }
    }

    /// <summary>
    /// Whether <paramref name="body"/>, the entries of an Any block at
    /// <paramref name="depth"/>, decodes to exactly <paramref name="want"/>.
    /// A reader's own check is the only complete one: a body loses what the
    /// text cannot say at any level, and what else the reader does to it, a
    /// default or a required rule among them, is the decoder's to know.
    /// </summary>
    internal static bool AnyBodyReadsBack(string body, MessageDescriptor desc, TypeRegistry registry, int depth, int maxDepth, ByteString want)
    {
        var d = new Decoder { Registry = registry, _limits = PxfLimits.Default with { MaxNestingDepth = maxDepth } };
        d.ResetState();
        d._result = new Result();
        d._depth = depth;
        d._lex = new Lexer(body + "}", d._limits.MaxBytesLiteralLength);
        var inner = NewMessage(desc);
        try
        {
            d.Advance();
            d.DecodeMessage(inner, true);
            d.PostDecode(inner, "", null);
        }
        catch (PxfException)
        {
            return false;
        }
        return want.Span.SequenceEqual(WellKnown.Deterministic(inner));
    }

    /// <summary>
    /// One value typed by <paramref name="fd"/>, reported under
    /// <paramref name="named"/>'s name: a wrapper's scalar is typed by the
    /// wrapper's <c>value</c> field and a map's by its entry's, and a document
    /// writes neither.
    /// </summary>
    private object ConsumeScalar(FieldDescriptor fd, FieldDescriptor named)
    {
        var pos = _current.Pos;
        RefuseSuffix(named);
        string text = _current.Value;
        object value;
        switch (fd.FieldType)
        {
            case FieldType.String:
                if (_current.Kind != TokenKind.STRING) throw Expected("string", named, pos);
                value = text;
                break;
            case FieldType.Bool:
                if (_current.Kind != TokenKind.BOOL) throw Expected("bool", named, pos);
                value = text == "true";
                break;
            case FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32:
                if (_current.Kind != TokenKind.INT) throw Expected("integer", named, pos);
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i32))
                {
                    throw new PxfException(pos, $"invalid int32: {text}");
                }
                value = i32;
                break;
            case FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64:
                if (_current.Kind != TokenKind.INT) throw Expected("integer", named, pos);
                if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long i64))
                {
                    throw new PxfException(pos, $"invalid int64: {text}");
                }
                value = i64;
                break;
            case FieldType.UInt32 or FieldType.Fixed32:
                if (_current.Kind != TokenKind.INT) throw Expected("integer", named, pos);
                if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint u32))
                {
                    throw new PxfException(pos, $"invalid uint32: {text}");
                }
                value = u32;
                break;
            case FieldType.UInt64 or FieldType.Fixed64:
                if (_current.Kind != TokenKind.INT) throw Expected("integer", named, pos);
                if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u64))
                {
                    throw new PxfException(pos, $"invalid uint64: {text}");
                }
                value = u64;
                break;
            case FieldType.Float:
                value = (float)ParseFloat(text, named, pos, single: true);
                break;
            case FieldType.Double:
                value = ParseFloat(text, named, pos, single: false);
                break;
            case FieldType.Bytes:
                if (_current.Kind != TokenKind.BYTES) throw Expected("bytes", named, pos);
                if (!Base64Lenient.TryDecode(text, out var bytes))
                {
                    throw new PxfException(pos, $"invalid base64 for field \"{named.Name}\"");
                }
                value = ByteString.CopyFrom(bytes);
                break;
            case FieldType.Enum:
                return ConsumeEnum(fd, named);
            default:
                throw new PxfException(pos, $"unsupported kind {fd.FieldType} for field \"{named.Name}\"");
        }
        Advance();
        return value;
    }

    private static PxfException Expected(string what, FieldDescriptor named, Position pos) =>
        new(pos, $"expected {what} for field \"{named.Name}\"");

    // The NaN Go's strconv.ParseFloat gives for "nan": positive, quiet, no
    // payload (0x7FF8000000000000). .NET's double.NaN has the sign bit set
    // (0xFFF8000000000000), and the bits reach the wire, where the write gate
    // compares them with protowire-go's (#82). As a float it narrows to
    // 0x7FC00000, Go's float32 NaN.
    private static readonly double QuietNaN = BitConverter.Int64BitsToDouble(0x7FF8000000000000);

    private double ParseFloat(string text, FieldDescriptor named, Position pos, bool single)
    {
        if (_current.Kind == TokenKind.IDENT && text is "inf" or "nan")
        {
            return text == "inf" ? double.PositiveInfinity : QuietNaN;
        }
        if (_current.Kind is not (TokenKind.FLOAT or TokenKind.INT)) throw Expected("number", named, pos);
        if (text is "+inf" or "-inf") return text[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        const NumberStyles Style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        if (single)
        {
            if (float.TryParse(text, Style, CultureInfo.InvariantCulture, out float f) && !float.IsInfinity(f)) return f;
            throw new PxfException(pos, $"invalid float: {text}");
        }
        if (double.TryParse(text, Style, CultureInfo.InvariantCulture, out double d) && !double.IsInfinity(d)) return d;
        throw new PxfException(pos, $"invalid double: {text}");
    }

    private object ConsumeEnum(FieldDescriptor fd, FieldDescriptor named)
    {
        var pos = _current.Pos;
        RefuseSuffix(named);
        int number;
        switch (_current.Kind)
        {
            case TokenKind.IDENT:
                var ev = fd.EnumType.FindValueByName(_current.Value)
                    ?? throw new PxfException(pos, $"unknown enum value \"{_current.Value}\" for {fd.EnumType.FullName}");
                number = ev.Number;
                break;
            case TokenKind.INT:
                if (!int.TryParse(_current.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number))
                {
                    throw new PxfException(pos, $"invalid enum number: {_current.Value}");
                }
                break;
            default:
                throw new PxfException(pos, $"expected enum name or number for field \"{named.Name}\"");
        }
        Advance();
        return Enum.ToObject(fd.EnumType.ClrType, number);
    }

    private static IList ListOf(IMessage msg, FieldDescriptor fd) => (IList)fd.Accessor.GetValue(msg);

    private void DecodeListField(IMessage msg, FieldDescriptor fd)
    {
        if (_current.Kind != TokenKind.LBRACKET)
        {
            throw new PxfException(_current.Pos, $"expected '[' for repeated field \"{fd.Name}\"");
        }
        EnterDescent();
        try
        {
            Advance();
            var list = ListOf(msg, fd);
            var keyFd = Keyed.KeyField(fd);
            bool isMessage = fd.FieldType is FieldType.Message or FieldType.Group;
            while (_current.Kind is not (TokenKind.RBRACKET or TokenKind.EOF))
            {
                if (list.Count >= _limits.MaxRepeatedCount)
                {
                    throw new PxfException(_current.Pos, $"repeated field \"{fd.Name}\" exceeds MaxRepeatedCount={_limits.MaxRepeatedCount}");
                }
                if (_current.Kind == TokenKind.NULL)
                {
                    throw new PxfException(_current.Pos, $"null is not allowed in repeated field \"{fd.Name}\"");
                }
                list.Add(isMessage ? ConsumeListMessage(fd, list, keyFd) : ConsumeScalar(fd, fd));
                if (_current.Kind == TokenKind.COMMA) Advance();
            }
            if (_current.Kind != TokenKind.RBRACKET) throw new PxfException(_current.Pos, $"expected ']', got {_current.Kind}");
            Advance();
        }
        finally
        {
            _depth--;
        }
    }

    private object ConsumeListMessage(FieldDescriptor fd, IList list, FieldDescriptor? keyFd)
    {
        var mdesc = fd.MessageType;
        if (HooksSecret(mdesc)) return HookedSecret(mdesc, $"{_pathPrefix}{fd.Name}[{list.Count}]");
        if (TryShorthand(mdesc, fd, out var value, out _)) return value!;
        if (_current.Kind != TokenKind.LBRACE)
        {
            throw new PxfException(_current.Pos, $"expected {MessageValueForms(mdesc)} for repeated message element, got {_current.Kind} (\"{_current.Value}\")");
        }
        Advance();
        var sub = NewMessage(mdesc);
        if (keyFd != null)
        {
            _keyedElem = new KeyedElemState(fd.Name, keyFd, EntryName: "", Named: false);
        }
        var saved = EnterElement(fd, list.Count);
        try
        {
            DecodeMessage(sub, true);
        }
        finally
        {
            LeaveElement(saved);
        }
        return WellKnown.IsWrapper(mdesc) ? WellKnown.Unwrap(sub) : sub;
    }

    /// <summary>
    /// The block form of a keyed repeated field: named entries, one per
    /// element in list order, each name supplying the element's key field. The
    /// opening <c>{</c> is consumed on entry, the closing <c>}</c> on return.
    /// </summary>
    private void DecodeKeyedBlock(IList list, FieldDescriptor fd, FieldDescriptor keyFd)
    {
        EnterDescent();
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (_current.Kind != TokenKind.RBRACE)
            {
                if (_current.Kind == TokenKind.EOF)
                {
                    throw new PxfException(_current.Pos, $"expected '}}' to close keyed field \"{fd.Name}\", got EOF");
                }
                if (_current.Kind is not (TokenKind.IDENT or TokenKind.STRING))
                {
                    throw new PxfException(_current.Pos,
                        $"expected entry name (identifier or string) in keyed field \"{fd.Name}\", got {_current.Kind}");
                }
                var namePos = _current.Pos;
                string name = _current.Value;
                if (name.Length == 0) throw Keyed.EmptyName(namePos, fd.Name);
                if (!seen.Add(name)) throw Keyed.DuplicateKey(namePos, fd.Name, name);
                Advance();

                if (_current.Kind == TokenKind.EQUALS)
                {
                    Advance();
                    if (_current.Kind != TokenKind.LBRACE)
                    {
                        throw new PxfException(_current.Pos,
                            $"keyed entry \"{name}\" of field \"{fd.Name}\" must have a block value ('{{ ... }}'): the element type is a message");
                    }
                }
                else if (_current.Kind != TokenKind.LBRACE)
                {
                    throw new PxfException(_current.Pos,
                        $"expected '{{' or '=' after entry name \"{name}\" in keyed field \"{fd.Name}\", got {_current.Kind}");
                }
                Advance();
                if (list.Count >= _limits.MaxRepeatedCount)
                {
                    throw new PxfException(_current.Pos, $"repeated field \"{fd.Name}\" exceeds MaxRepeatedCount={_limits.MaxRepeatedCount}");
                }

                var element = NewMessage(fd.MessageType);
                keyFd.Accessor.SetValue(element, name);
                _keyedElem = new KeyedElemState(fd.Name, keyFd, name, Named: true);
                var saved = EnterElement(fd, list.Count);
                try
                {
                    // The entry name supplies the key field, and PostDecode
                    // reads presence only: a default would replace the name.
                    _result?.MarkPresent(_pathPrefix + keyFd.Name);
                    DecodeMessage(element, true);
                }
                finally
                {
                    LeaveElement(saved);
                }
                list.Add(element);
            }
            Advance();
        }
        finally
        {
            _depth--;
        }
    }

    private void DecodeMapField(IMessage msg, FieldDescriptor fd)
    {
        if (_current.Kind != TokenKind.LBRACE)
        {
            throw new PxfException(_current.Pos, $"expected '{{' for map field \"{fd.Name}\"");
        }
        EnterDescent();
        try
        {
            Advance();
            var map = (IDictionary)fd.Accessor.GetValue(msg);
            var keyFd = fd.MessageType.FindFieldByNumber(1);
            var valueFd = fd.MessageType.FindFieldByNumber(2);
            bool isMessage = valueFd.FieldType is FieldType.Message or FieldType.Group;
            while (_current.Kind is not (TokenKind.RBRACE or TokenKind.EOF))
            {
                var pos = _current.Pos;
                if (map.Count >= _limits.MaxRepeatedCount)
                {
                    throw new PxfException(pos, $"map field \"{fd.Name}\" exceeds MaxRepeatedCount={_limits.MaxRepeatedCount}");
                }
                var keyKind = _current.Kind;
                if (keyKind is not (TokenKind.IDENT or TokenKind.STRING or TokenKind.INT or TokenKind.BOOL))
                {
                    throw new PxfException(pos, $"expected map key, got {keyKind} (\"{_current.Value}\")");
                }
                if (keyKind == TokenKind.INT && _lex.Prec.Length > 0)
                {
                    throw new PxfException(pos, $"map field \"{fd.Name}\": a key cannot carry a precision suffix (p{_lex.Prec})");
                }
                string keyText = _current.Value;
                Advance();
                if (_current.Kind == TokenKind.EQUALS)
                {
                    throw new PxfException(_current.Pos, "unexpected '=' in map, use ':' for map entries");
                }
                if (_current.Kind != TokenKind.COLON)
                {
                    throw new PxfException(_current.Pos, $"expected ':' after map key, got {_current.Kind}");
                }
                Advance();

                var key = DecodeMapKey(fd, keyFd, keyText, keyKind, pos);
                BindMapKey(msg, fd, key, pos);
                if (_current.Kind == TokenKind.NULL)
                {
                    throw new PxfException(_current.Pos, $"null is not allowed as map value in field \"{fd.Name}\"");
                }
                map[key] = isMessage ? ConsumeMapMessage(fd, valueFd.MessageType, key) : ConsumeScalar(valueFd, fd);
            }
            if (_current.Kind != TokenKind.RBRACE) throw new PxfException(_current.Pos, $"expected '}}', got {_current.Kind}");
            Advance();
        }
        finally
        {
            _depth--;
        }
    }

    private object ConsumeMapMessage(FieldDescriptor fd, MessageDescriptor mdesc, object key)
    {
        if (HooksSecret(mdesc)) return HookedSecret(mdesc, $"{_pathPrefix}{fd.Name}{KeySuffix(key)}");
        if (TryShorthand(mdesc, fd, out var value, out _)) return value!;
        if (_current.Kind != TokenKind.LBRACE)
        {
            throw new PxfException(_current.Pos, $"expected {MessageValueForms(mdesc)} for map message value, got {_current.Kind} (\"{_current.Value}\")");
        }
        Advance();
        var sub = NewMessage(mdesc);
        string saved = _pathPrefix;
        _pathPrefix = $"{saved}{fd.Name}{KeySuffix(key)}.";
        _elemDepth++;
        try
        {
            DecodeMessage(sub, true);
        }
        finally
        {
            LeaveElement(saved);
        }
        return WellKnown.IsWrapper(mdesc) ? WellKnown.Unwrap(sub) : sub;
    }

    // Keys compare as values of the key type, so 1 and "1" are one key of an
    // integer-keyed map, inside one literal or across two bindings of the
    // field. Each message is its own scope.
    private void BindMapKey(IMessage msg, FieldDescriptor fd, object key, Position pos)
    {
        if (!_mapKeys.TryGetValue(msg, out var fields)) _mapKeys[msg] = fields = [];
        if (!fields.TryGetValue(fd, out var keys)) fields[fd] = keys = [];
        if (!keys.Add(key))
        {
            string shown = key is string s ? $"\"{s}\"" : Convert.ToString(key, CultureInfo.InvariantCulture)!;
            throw new PxfException(pos, $"map field \"{fd.Name}\": key {shown} is already set");
        }
    }

    private static object DecodeMapKey(FieldDescriptor field, FieldDescriptor keyFd, string key, TokenKind kind, Position pos)
    {
        switch (keyFd.FieldType)
        {
            case FieldType.String:
                if (kind == TokenKind.BOOL)
                {
                    throw new PxfException(pos, $"invalid string map key {key} for field \"{field.Name}\": the keyword {key} is a bool key; write \"{key}\" for the string");
                }
                return key;
            case FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32:
                return int.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i32) ? i32
                    : throw new PxfException(pos, $"invalid int32 map key: {key}");
            case FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64:
                return long.TryParse(key, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long i64) ? i64
                    : throw new PxfException(pos, $"invalid int64 map key: {key}");
            case FieldType.UInt32 or FieldType.Fixed32:
                return uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out uint u32) ? u32
                    : throw new PxfException(pos, $"invalid uint32 map key: {key}");
            case FieldType.UInt64 or FieldType.Fixed64:
                return ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u64) ? u64
                    : throw new PxfException(pos, $"invalid uint64 map key: {key}");
            case FieldType.Bool:
                // true, false, 0, 1, "true" or "false": a quoted integer is
                // not a bool literal, and an identifier names no map key.
                switch (kind, key)
                {
                    case (TokenKind.BOOL, _): return key == "true";
                    case (TokenKind.STRING, "true"): return true;
                    case (TokenKind.STRING, "false"): return false;
                    case (not TokenKind.STRING, "1"): return true;
                    case (not TokenKind.STRING, "0"): return false;
                }
                throw new PxfException(pos, $"invalid bool map key {key} for field \"{field.Name}\": a bool key is true, false, 0, 1, \"true\" or \"false\"");
            default:
                throw new PxfException(pos, $"unsupported map key kind: {keyFd.FieldType}");
        }
    }

    private static string KeySuffix(object key) => key switch
    {
        string s => $"[{GoQuote(s)}]",
        bool b => b ? "[true]" : "[false]",
        _ => $"[{Convert.ToString(key, CultureInfo.InvariantCulture)}]",
    };

    private static string GoQuote(string s)
    {
        var sb = new System.Text.StringBuilder();
        Encoder.WriteQuotedString(sb, s);
        return sb.ToString();
    }

    /// <summary>
    /// A <c>google.protobuf.Any</c> block that opens with a type entry: the
    /// rest of the block is a body of the type the URL names, packed into the
    /// Any's value, and the URL is stored verbatim. Its fields are recorded
    /// under the Any's own path.
    /// </summary>
    private void DecodeAnyBody(IMessage any)
    {
        Advance();
        if (_current.Kind != TokenKind.EQUALS)
        {
            throw new PxfException(_current.Pos, $"expected '=' after @type, got {_current.Kind}");
        }
        Advance();
        if (_current.Kind != TokenKind.STRING)
        {
            throw new PxfException(_current.Pos, $"expected a string type URL after @type =, got {_current.Kind}");
        }
        string url = _current.Value;
        var urlPos = _current.Pos;
        Advance();

        string typeName = url[(url.LastIndexOf('/') + 1)..];
        var inner = Registry.Find(typeName)
            ?? throw new PxfException(urlPos, $"cannot resolve Any type \"{url}\"");
        var body = NewMessage(inner);
        // The body shares the Any's braces, which the caller already counted.
        _depth--;
        try
        {
            DecodeMessageBody(body, true);
        }
        finally
        {
            _depth++;
        }
        // Packed to bytes below, out of reach of the pass over the root.
        if (_result != null) PostDecode(body, _pathPrefix, null);

        any.Descriptor.FindFieldByName("type_url").Accessor.SetValue(any, url);
        any.Descriptor.FindFieldByName("value").Accessor.SetValue(any, ByteString.CopyFrom(WellKnown.Deterministic(body)));
    }
}
