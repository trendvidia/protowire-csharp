// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
namespace Protowire.Pxf;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class PxfRequiredAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public class PxfDefaultAttribute : Attribute
{
    public string Value { get; }
    public PxfDefaultAttribute(string value) => Value = value;
}
