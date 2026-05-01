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
    EQUALS,
    COLON,
    COMMA,

    AT_TYPE
}

public record Token(TokenKind Kind, string Value, Position Pos);
