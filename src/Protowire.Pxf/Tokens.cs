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
