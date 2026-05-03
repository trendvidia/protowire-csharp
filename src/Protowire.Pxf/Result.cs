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

    internal void MarkPresent(string path) => _present.Add(path);

    internal void MarkNull(string path)
    {
        _null.Add(path);
        _present.Add(path);
    }

    internal bool Has(string path) => _present.Contains(path);
}
