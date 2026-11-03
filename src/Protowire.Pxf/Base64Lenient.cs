// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pxf;

/// <summary>
/// Decodes a bytes literal in either alphabet of RFC 4648, with or without
/// padding (draft -01 § Bytes). One literal uses one alphabet.
/// </summary>
internal static class Base64Lenient
{
    public static bool TryDecode(string raw, out byte[] decoded)
    {
        decoded = [];
        bool std = false, url = false;
        int pad = 0;
        foreach (char c in raw)
        {
            if (c == '=') { pad++; continue; }
            if (pad > 0) return false;
            if (c is '+' or '/') std = true;
            else if (c is '-' or '_') url = true;
            else if (!char.IsAsciiLetterOrDigit(c)) return false;
        }
        if (std && url) return false;
        if (pad > 0 && raw.Length % 4 != 0) return false;
        string body = url ? raw.Replace('-', '+').Replace('_', '/') : raw;
        int rem = body.Length % 4;
        if (rem == 1) return false;
        if (rem != 0) body = body.PadRight(body.Length + (4 - rem), '=');
        try
        {
            decoded = Convert.FromBase64String(body);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
