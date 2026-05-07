// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pb;

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public class ProtowireAttribute : Attribute
{
    public int Tag { get; }
    public bool ZigZag { get; set; }
    public string? Oneof { get; set; }

    public ProtowireAttribute(int tag)
    {
        Tag = tag;
    }
}

[AttributeUsage(AttributeTargets.Class)]
public class ProtowireMessageAttribute : Attribute { }
