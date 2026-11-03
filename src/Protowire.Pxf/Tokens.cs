// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pxf;

public enum TokenKind
{
    EOF,
    ILLEGAL,
    NEWLINE,
    COMMENT,

    IDENT,
    STRING,
    INT,
    FLOAT,
    BOOL,
    NULL,
    BYTES,
    TIMESTAMP,
    DURATION,

    LBRACE,
    RBRACE,
    LBRACKET,
    RBRACKET,
    LPAREN,
    RPAREN,
    EQUALS,
    COLON,
    COMMA,

    AT_TYPE,
    AT_DATASET,
    AT_PROTO,
    AT_DIRECTIVE,
}

public record Token(TokenKind Kind, string Value, Position Pos);

/// <summary>
/// How an error names the token it got, in protowire-go's words, so the
/// messages match the reference's (#99).
/// </summary>
internal static class TokenText
{
    /// <summary>A kind's name, as protowire-go's <c>TokenKind.String</c> spells it.</summary>
    public static string Name(TokenKind kind) => kind switch
    {
        TokenKind.EOF => "EOF",
        TokenKind.ILLEGAL => "ILLEGAL",
        TokenKind.NEWLINE => "newline",
        TokenKind.COMMENT => "comment",
        TokenKind.IDENT => "identifier",
        TokenKind.STRING => "string",
        TokenKind.INT => "integer",
        TokenKind.FLOAT => "float",
        TokenKind.BOOL => "bool",
        TokenKind.NULL => "null",
        TokenKind.BYTES => "bytes",
        TokenKind.TIMESTAMP => "timestamp",
        TokenKind.DURATION => "duration",
        TokenKind.LBRACE => "{",
        TokenKind.RBRACE => "}",
        TokenKind.LBRACKET => "[",
        TokenKind.RBRACKET => "]",
        TokenKind.LPAREN => "(",
        TokenKind.RPAREN => ")",
        TokenKind.EQUALS => "=",
        TokenKind.COLON => ":",
        TokenKind.COMMA => ",",
        TokenKind.AT_TYPE => "@type",
        TokenKind.AT_DATASET => "@dataset",
        TokenKind.AT_PROTO => "@proto",
        TokenKind.AT_DIRECTIVE => "@directive",
        _ => $"TokenKind({(int)kind})",
    };

    /// <summary>
    /// The token with its text, as protowire-go's <c>tokenErrMsg</c> renders
    /// it. A token the lexer rejected is its diagnostic, which says more than
    /// the kind's name.
    /// </summary>
    public static string Describe(Token t) =>
        t.Kind == TokenKind.ILLEGAL ? t.Value : $"{Name(t.Kind)} ({Quote(t.Value)})";

    public static string Quote(string s)
    {
        var sb = new System.Text.StringBuilder();
        Encoder.WriteQuotedString(sb, s);
        return sb.ToString();
    }
}
