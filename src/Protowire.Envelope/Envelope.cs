using Protowire.Pb;

namespace Protowire.Envelopes;

/// <summary>
/// Envelope wraps an API response with transport metadata, an optional
/// success payload, and an optional application error.
/// </summary>
public class Envelope
{
    [Protowire(1)]
    public int Status { get; set; }
    [Protowire(2)]
    public string? TransportError { get; set; }
    [Protowire(3)]
    public byte[]? Data { get; set; }
    [Protowire(4)]
    public AppError? Error { get; set; }

    /// <summary>
    /// Reports whether the envelope represents a successful response.
    /// </summary>
    public bool IsOK => string.IsNullOrEmpty(TransportError) && Error == null;

    /// <summary>
    /// Reports whether the envelope has a transport-level error.
    /// </summary>
    public bool IsTransportError => !string.IsNullOrEmpty(TransportError);

    /// <summary>
    /// Reports whether the envelope has an application-level error.
    /// </summary>
    public bool IsAppError => Error != null;

    /// <summary>
    /// Returns the application error code, or empty string if no error.
    /// </summary>
    public string ErrorCode => Error?.Code ?? string.Empty;

    /// <summary>
    /// Returns field errors indexed by field name for easy lookup.
    /// </summary>
    public Dictionary<string, FieldError>? FieldErrors =>
        Error?.Details?.ToDictionary(fe => fe.Field, fe => fe);

    // Builders

    /// <summary>
    /// OK creates a success envelope with the given status and raw data payload.
    /// </summary>
    public static Envelope OK(int status, byte[]? data) => new() { Status = status, Data = data };

    /// <summary>
    /// Err creates an error envelope.
    /// </summary>
    public static Envelope Err(int status, string code, string message, params string[] args) => new()
    {
        Status = status,
        Error = new AppError { Code = code, Message = message, Args = args.ToList() }
    };

    /// <summary>
    /// TransportErr creates a transport-level error envelope.
    /// </summary>
    public static Envelope TransportErr(string err) => new() { TransportError = err };
}

/// <summary>
/// AppError represents an application-level error.
/// </summary>
public class AppError
{
    [Protowire(1)]
    public string Code { get; set; } = string.Empty;
    [Protowire(2)]
    public string? Message { get; set; }
    [Protowire(3)]
    public List<string>? Args { get; set; }
    [Protowire(4)]
    public List<FieldError>? Details { get; set; }
    [Protowire(5)]
    public Dictionary<string, string>? Metadata { get; set; }

    // Builders

    public AppError WithField(string field, string code, string message, params string[] args)
    {
        Details ??= new List<FieldError>();
        Details.Add(new FieldError
        {
            Field = field,
            Code = code,
            Message = message,
            Args = args.ToList()
        });
        return this;
    }

    public AppError WithMeta(string key, string value)
    {
        Metadata ??= new Dictionary<string, string>();
        Metadata[key] = value;
        return this;
    }
}

/// <summary>
/// FieldError represents a validation error on a specific field.
/// </summary>
public class FieldError
{
    [Protowire(1)]
    public string Field { get; set; } = string.Empty;
    [Protowire(2)]
    public string Code { get; set; } = string.Empty;
    [Protowire(3)]
    public string? Message { get; set; }
    [Protowire(4)]
    public List<string>? Args { get; set; }
}
