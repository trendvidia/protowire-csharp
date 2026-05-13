// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pxf;

/// <summary>
/// Per-field presence metadata returned by <see cref="Decoder.UnmarshalFull"/>.
///
/// <para>
/// Three states are distinguished, by dotted field path
/// (e.g. <c>"tls.cert_file"</c>):
/// </para>
/// <list type="bullet">
///   <item><see cref="IsSet"/> — the field carries a concrete value.</item>
///   <item><see cref="IsNull"/> — the field was explicitly written as <c>null</c>.</item>
///   <item><see cref="IsAbsent"/> — the field did not appear in the document.</item>
/// </list>
///
/// <para>
/// Mirrors the <c>Result</c> type in
/// <c>protowire-go/encoding/pxf/result.go</c>.
/// </para>
/// </summary>
public sealed class Result
{
    private readonly HashSet<string> _present = [];
    private readonly HashSet<string> _null = [];
    private readonly List<Directive> _directives = [];
    private readonly List<DatasetDirective> _datasets = [];
    private readonly List<ProtoDirective> _protos = [];

    /// <summary>True if the field has a concrete (non-null) value.</summary>
    public bool IsSet(string path) => _present.Contains(path) && !_null.Contains(path);

    /// <summary>True if the field was explicitly written as <c>null</c>.</summary>
    public bool IsNull(string path) => _null.Contains(path);

    /// <summary>True if the field did not appear in the document at all.</summary>
    public bool IsAbsent(string path) => !_present.Contains(path) && !_null.Contains(path);

    /// <summary>All paths explicitly set to null, sorted.</summary>
    public IReadOnlyList<string> NullFields()
    {
        var arr = _null.ToArray();
        Array.Sort(arr, StringComparer.Ordinal);
        return arr;
    }

    /// <summary>All paths with a concrete (non-null) value, sorted.</summary>
    public IReadOnlyList<string> SetFields()
    {
        var arr = _present.Where(p => !_null.Contains(p)).ToArray();
        Array.Sort(arr, StringComparer.Ordinal);
        return arr;
    }

    /// <summary>
    /// Generic `@&lt;name&gt; *(prefix) [{ ... }]` directives the decoder
    /// saw at document root, in source order. Excludes <c>@type</c>,
    /// <c>@dataset</c>, and <c>@proto</c>, which have their own
    /// accessors. See draft §3.4.2.
    /// </summary>
    public IReadOnlyList<Directive> Directives => _directives;

    /// <summary>
    /// <c>@dataset</c> directives in source order (draft §3.4.4). A
    /// document with any <c>@dataset</c> has no body entries, so the
    /// rows are the document's payload.
    /// </summary>
    public IReadOnlyList<DatasetDirective> Datasets => _datasets;

    /// <summary>
    /// <c>@proto</c> directives in source order (draft §3.4.5). Carry
    /// embedded protobuf schemas, making the PXF document self-describing.
    /// </summary>
    public IReadOnlyList<ProtoDirective> Protos => _protos;

    internal void MarkPresent(string path) => _present.Add(path);

    internal void MarkNull(string path)
    {
        _null.Add(path);
        _present.Add(path);
    }

    internal bool Has(string path) => _present.Contains(path);

    internal void AddDirective(Directive d) => _directives.Add(d);
    internal void AddDataset(DatasetDirective d) => _datasets.Add(d);
    internal void AddProto(ProtoDirective p) => _protos.Add(p);
}
