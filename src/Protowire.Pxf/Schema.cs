// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pxf;

/// <summary>
/// PXF directive-name reservations per draft §3.4.6.
///
/// <para>
/// The full reserved-directive-name set is 13 names: the four value
/// keywords (<c>null</c>, <c>true</c>, <c>false</c>) — rejected at the
/// lexer because they tokenise as their value form, never as a
/// directive — plus seven names with parser-layer or future
/// significance:
/// </para>
/// <list type="bullet">
///   <item><c>type</c>, <c>dataset</c>, <c>proto</c> — own production, lexed as dedicated tokens</item>
///   <item><c>entry</c> — spec-registered named directive (draft §3.4.3)</item>
///   <item><c>table</c>, <c>datasource</c>, <c>view</c>, <c>procedure</c>, <c>function</c>, <c>permissions</c>
///     — future-reserved; v1 decoders MUST reject them so applications
///     cannot squat the names before the spec allocates semantics</item>
/// </list>
/// </summary>
public static class Schema
{
    /// <summary>
    /// True when <paramref name="name"/> is reserved for future
    /// allocation by draft §3.4.6 and MUST be rejected by v1 decoders.
    /// Names with their own lexer production (<c>type</c>, <c>dataset</c>,
    /// <c>proto</c>) and the registered <c>entry</c> are not included
    /// here — they're handled either at the lexer or by the
    /// named-directive shape.
    /// </summary>
    public static bool IsFutureReservedDirective(string name) => name switch
    {
        "table" or "datasource" or "view" or "procedure" or "function" or "permissions" => true,
        _ => false,
    };
}
