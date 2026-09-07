// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
// Cross-port wire-compatibility dumper, driven by protowire's
// scripts/cross_envelope_check.sh. Every port carries the same program and
// the script compares their output byte for byte. Mirrors
// protowire-go/scripts/dump_envelope.
//
//   dump-envelope                        canonical Envelope → pb hex
//   dump-envelope --pb  FDS MESSAGE DOC  PXF DOC decoded against MESSAGE → pb hex
//   dump-envelope --sbe FDS MESSAGE DOC  PXF DOC decoded against MESSAGE → SBE hex
//
// The fixture modes are how the gate proves this port reads (pxf.required)
// = 1314, (pxf.default) = 1315 and the SBE numbers 1319–1323 from a schema
// (STABILITY.md promise 3, protowire#244): Decoder.UnmarshalFull applies the
// PXF annotations it finds on the message's FieldDescriptors, and Sbe.Codec
// builds its template from the file's. A port looking for the wrong number
// accepts missing-required.pxf, emits ok.pxf without its defaulted fields,
// or lays the SBE message out differently.
//
// This is a codegen port: MESSAGE names a type generated ahead of time
// (settings.v1.Settings from proto/settings.proto, a copy of
// protowire/testdata/annotations/settings.proto; bench.v1.Order from
// sbe-bench.proto), whose embedded descriptor carries the same options FDS
// does. FDS itself is therefore read only to check that it declares
// MESSAGE, the way bench-sbe accepts --testdata without reading it.
//
// Exit 0 with hex on stdout; 1 with "reject: <reason>" on stderr when the
// document cannot be decoded against the message; 2 for anything that is
// the harness's fault.

using Google.Protobuf;
using Google.Protobuf.Reflection;
using Protowire.Envelopes;
using Protowire.Pxf;
using Protowire.Sbe;
using Protowire.Sbe.Tests.Bench;

if (args.Length == 0)
{
    DumpEnvelope();
    return 0;
}
if (args.Length == 4 && (args[0] == "--pb" || args[0] == "--sbe"))
{
    return DumpFixture(args[0], args[1], args[2], args[3]);
}
Console.Error.WriteLine("usage: dump-envelope [--pb|--sbe FDS MESSAGE DOC]");
return 2;

static void DumpEnvelope()
{
    var env = Envelope.Err(402, "INSUFFICIENT_FUNDS", "balance too low",
        "$3.50", "$10.00");
    env.Data = [0xDE, 0xAD, 0xBE, 0xEF];
    env.Error!
        .WithField("amount", "MIN_VALUE", "below minimum", "10.00")
        .WithMeta("request_id", "req-123");

    byte[] bytes = Protowire.Pb.Pb.Marshal(env);
    Console.WriteLine(Convert.ToHexString(bytes).ToLowerInvariant());
}

/// <summary>Generated types the fixture modes can name, by proto full name.</summary>
static (Func<IMessage> make, FileDescriptor file)? Generated(string message) => message switch
{
    "settings.v1.Settings" => (() => new Settings.V1.Settings(), Settings.V1.SettingsReflection.Descriptor),
    "bench.v1.Order" => (() => new Order(), SbeBenchReflection.Descriptor),
    _ => null,
};

static int DumpFixture(string mode, string fdsPath, string message, string docPath)
{
    var generated = Generated(message);
    if (generated is null)
    {
        return Fatal($"{message}: no generated type in this dumper (see Generated())");
    }
    var (make, file) = generated.Value;

    FileDescriptorSet fds;
    string doc;
    try
    {
        fds = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(fdsPath));
        doc = File.ReadAllText(docPath);
    }
    catch (Exception e)
    {
        return Fatal(e.Message);
    }
    if (!fds.File.Any(f => f.MessageType.Any(m => Qualify(f, m.Name) == message)))
    {
        return Fatal($"{fdsPath}: {message} not found");
    }

    var msg = make();
    try
    {
        // The full decode is the one that validates (pxf.required) and
        // applies (pxf.default); plain Unmarshal leaves both to the caller.
        new Decoder().UnmarshalFull(doc, msg);
    }
    catch (PxfException e)
    {
        Console.Error.WriteLine($"reject: {e.Message}");
        return 1;
    }

    byte[] bytes;
    try
    {
        bytes = mode == "--pb" ? msg.ToByteArray() : new Codec(file).Marshal(msg);
    }
    catch (Exception e)
    {
        return Fatal(e.Message);
    }
    Console.WriteLine(Convert.ToHexString(bytes).ToLowerInvariant());
    return 0;
}

static string Qualify(FileDescriptorProto f, string name) =>
    string.IsNullOrEmpty(f.Package) ? name : $"{f.Package}.{name}";

static int Fatal(string msg)
{
    Console.Error.WriteLine($"dump-envelope: {msg}");
    return 2;
}
