// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Protowire.Pxf.Tests.SecretHook;

namespace Protowire.Pxf.Tests;

/// <summary>
/// <see cref="Decoder.OnSecretField"/>: a pxf.Secret's value goes to the hook
/// instead of onto the message, in the scalar and block forms and in singular,
/// list and map positions, under the reference's path spelling. Ports
/// secret_hook_test.go and the hook cases of eq_block_presence_test.go in
/// protowire-go/encoding/pxf.
/// </summary>
public class SecretHookTests
{
    private static readonly TypeRegistry Registry = TypeRegistry.FromFiles(PxfSecretHookTestReflection.Descriptor);

    private static (Decoder Decoder, List<(string Path, string Value)> Seen) Recording()
    {
        var seen = new List<(string, string)>();
        return (new Decoder { Registry = Registry, OnSecretField = (path, value) => seen.Add((path, value)) }, seen);
    }

    private static List<(string Path, string Value)> Sorted(List<(string Path, string Value)> seen) =>
        seen.OrderBy(s => s.Path, StringComparer.Ordinal).ToList();

    // Every position, in both forms: the hook sees each value once, under
    // its path, and no value reaches the message.
    [Theory]
    [InlineData("db_password = \"x\"", "db_password")]
    [InlineData("db_password { value = \"x\" }", "db_password")]
    [InlineData("db_password = { value = \"x\" }", "db_password")]
    [InlineData("backup_keys = [\"x\"]", "backup_keys[0]")]
    [InlineData("backup_keys = [{ value = \"x\" }]", "backup_keys[0]")]
    [InlineData("tenant_keys = { \"acme\": \"x\" }", "tenant_keys[\"acme\"]")]
    [InlineData("tenant_keys = { \"acme\": { value = \"x\" } }", "tenant_keys[\"acme\"]")]
    [InlineData("tenant_keys = { acme: \"x\" }", "tenant_keys[\"acme\"]")]
    [InlineData("by_id = { 7: \"x\" }", "by_id[7]")]
    [InlineData("by_id = { -7: { value = \"x\" } }", "by_id[-7]")]
    public void FiresInEveryPosition_AndLeavesValueUnset(string doc, string path)
    {
        foreach (bool full in new[] { false, true })
        {
            var (decoder, seen) = Recording();
            var msg = new SecretDemo();
            if (full) decoder.UnmarshalFull(doc, msg);
            else decoder.Unmarshal(doc, msg);

            Assert.Equal([(path, "x")], seen);
            var secrets = new List<Secret?> { msg.DbPassword };
            secrets.AddRange(msg.BackupKeys);
            secrets.AddRange(msg.TenantKeys.Values);
            secrets.AddRange(msg.ById.Values);
            var secret = Assert.Single(secrets, s => s != null);
            Assert.Equal("", secret!.Value);
        }
    }

    // Every row of the path table in OnSecretField's documentation: a Secret
    // is reported under the body it sits in, however the body is written.
    // TestOnSecretField_PathNamesTheBody in the reference.
    [Theory]
    [InlineData("pw = \"x\"", "pw")]
    [InlineData("db { password = \"x\" }", "db.password")]
    [InlineData("db = { password = \"x\" }", "db.password")]
    [InlineData("pw { value = \"x\" hint = \"h\" }", "pw")]
    [InlineData("pw = { value = \"x\" }", "pw")]
    [InlineData("servers = [{ password = \"x\" }]", "servers[0].password")]
    [InlineData("servers = [{ password = { value = \"x\" } }]", "servers[0].password")]
    [InlineData("by_host = { \"a\": { password = \"x\" } }", "by_host[\"a\"].password")]
    [InlineData("payload { @type = \"secrethook.v1.Holder\" pw = \"x\" }", "payload.pw")]
    [InlineData("payload { @type = \"secrethook.v1.Holder\" pw { value = \"x\" } }", "payload.pw")]
    [InlineData("tenant_keys = { \"acme\": \"x\" }", "tenant_keys[\"acme\"]")]
    public void PathNamesTheBody(string doc, string path)
    {
        foreach (bool full in new[] { false, true })
        {
            var (decoder, seen) = Recording();
            if (full) decoder.UnmarshalFull(doc, new Root());
            else decoder.Unmarshal(doc, new Root());
            Assert.Equal([(path, "x")], seen);
        }
    }

    [Fact]
    public void AnyBody_PacksTheSecretWithoutItsValue()
    {
        var (decoder, _) = Recording();
        var root = new Root();
        decoder.Unmarshal("payload { @type = \"secrethook.v1.Holder\" pw { value = \"x\" hint = \"h\" } }", root);
        var holder = Holder.Parser.ParseFrom(root.Payload.Value);
        Assert.Equal("", holder.Pw.Value);
        Assert.Equal("h", holder.Pw.Hint);
    }

    [Fact]
    public void ListAndMap_FireOncePerElement()
    {
        var (decoder, seen) = Recording();
        var msg = new SecretDemo();
        decoder.Unmarshal("""
            backup_keys = ["a", { value = "b" hint = "second" }, "c"]
            tenant_keys = {
              "acme": "k1"
              "evil_corp": { value = "k2" }
            }
            """, msg);
        Assert.Equal(
            [("backup_keys[0]", "a"), ("backup_keys[1]", "b"), ("backup_keys[2]", "c"),
             ("tenant_keys[\"acme\"]", "k1"), ("tenant_keys[\"evil_corp\"]", "k2")],
            Sorted(seen));
        Assert.Equal(3, msg.BackupKeys.Count);
        Assert.All(msg.BackupKeys, s => Assert.Equal("", s.Value));
        Assert.Equal("second", msg.BackupKeys[1].Hint);
        Assert.All(msg.TenantKeys.Values, s => Assert.Equal("", s.Value));
    }

    [Fact]
    public void BlockForm_AssignsHintAndFingerprint()
    {
        var (decoder, seen) = Recording();
        var msg = new SecretDemo();
        decoder.Unmarshal("""
            db_password = "p1"
            api_token {
              value = "t1"
              hint = "external API"
              fingerprint = "sha256:abc"
            }
            """, msg);
        Assert.Equal([("api_token", "t1"), ("db_password", "p1")], Sorted(seen));
        Assert.Equal("", msg.DbPassword.Value);
        Assert.Equal("", msg.ApiToken.Value);
        Assert.Equal("external API", msg.ApiToken.Hint);
        Assert.Equal("sha256:abc", msg.ApiToken.Fingerprint);
    }

    // A block without a value, empty or with metadata only, calls nothing
    // and still sets the Secret.
    [Theory]
    [InlineData("db_password {}", "", "")]
    [InlineData("db_password { hint = \"rotated 2026\" fingerprint = \"sha256:abc\" }", "rotated 2026", "sha256:abc")]
    public void BlockWithoutValue_DoesNotFire(string doc, string hint, string fingerprint)
    {
        var (decoder, seen) = Recording();
        var msg = new SecretDemo();
        decoder.Unmarshal(doc, msg);
        Assert.Empty(seen);
        Assert.NotNull(msg.DbPassword);
        Assert.Equal("", msg.DbPassword.Value);
        Assert.Equal(hint, msg.DbPassword.Hint);
        Assert.Equal(fingerprint, msg.DbPassword.Fingerprint);
    }

    [Theory]
    [InlineData("db_password = \"x\"", new[] { "db_password", "db_password.value" }, new string[0])]
    [InlineData("db_password { value = \"x\" hint = \"h\" }", new[] { "db_password", "db_password.value", "db_password.hint" }, new[] { "db_password.fingerprint" })]
    public void Presence_RecordsTheHookedValue(string doc, string[] set, string[] unset)
    {
        var (decoder, _) = Recording();
        var result = decoder.UnmarshalFull(doc, new SecretDemo());
        foreach (string path in set) Assert.True(result.IsSet(path), path);
        foreach (string path in unset) Assert.False(result.IsSet(path), path);
    }

    [Theory]
    [InlineData("db_password = \"x\"", "db_password")]
    [InlineData("db_password { value = \"x\" hint = \"h\" }", "db_password")]
    [InlineData("backup_keys = [\"a\", \"x\", \"c\"]", "backup_keys[1]")]
    [InlineData("backup_keys = [{ value = \"x\" }]", "backup_keys[0]")]
    [InlineData("tenant_keys = { \"acme\": \"x\" }", "tenant_keys[\\\"acme\\\"]")]
    [InlineData("tenant_keys = { \"acme\": { value = \"x\" } }", "tenant_keys[\\\"acme\\\"]")]
    public void HookException_AbortsTheDecode(string doc, string quotedPath)
    {
        var cause = new InvalidOperationException("vault unreachable");
        var decoder = new Decoder
        {
            OnSecretField = (_, value) =>
            {
                if (value == "x") throw cause;
            },
        };
        var e = Assert.Throws<PxfException>(() => decoder.Unmarshal(doc, new SecretDemo()));
        Assert.Contains($"pxf.Secret hook for \"{quotedPath}\": vault unreachable", e.Message);
        Assert.Same(cause, e.InnerException);
    }

    // Invalid UTF-8 never reaches the hook: the lexer refuses the string.
    [Theory]
    [InlineData("db_password = \"\\xff\\xfe\"")]
    [InlineData("backup_keys = [\"\\xff\\xfe\"]")]
    [InlineData("tenant_keys = { \"acme\": \"\\xff\\xfe\" }")]
    [InlineData("db_password { value = \"\\xff\\xfe\" }")]
    [InlineData("db_password { hint = \"\\xff\\xfe\" }")]
    [InlineData("db_password { fingerprint = \"\\xff\\xfe\" }")]
    public void InvalidUtf8_IsRejectedBeforeTheHook(string doc)
    {
        bool called = false;
        var decoder = new Decoder { OnSecretField = (_, _) => called = true };
        var e = Assert.Throws<PxfException>(() => decoder.Unmarshal(doc, new SecretDemo()));
        Assert.Contains("UTF-8", e.Message);
        Assert.False(called);
    }

    // decodeSecretBlockInto's error branches, and a subfield bound twice,
    // which would otherwise hand the hook a second value.
    [Theory]
    [InlineData("db_password { value \"x\" }", "expected '=' after pxf.Secret field \"value\"")]
    [InlineData("db_password { value = 42 }", "expected string value for pxf.Secret value")]
    [InlineData("db_password { 42 = \"x\" }", "expected pxf.Secret field name (value/hint/fingerprint)")]
    [InlineData("db_password { value = \"x\"", "expected '}' to close pxf.Secret block, got EOF")]
    [InlineData("db_password { value = \"x\" rotation = \"P30D\" }", "unknown pxf.Secret field \"rotation\"")]
    [InlineData("db_password { value = \"x\" value = \"y\" }", "field \"value\" is already set")]
    [InlineData("db_password { hint = \"a\" hint = \"b\" }", "field \"hint\" is already set")]
    [InlineData("db_password { value = null }", "expected string value for pxf.Secret value")]
    public void BlockForm_Errors(string doc, string want)
    {
        var (decoder, seen) = Recording();
        var e = Assert.Throws<PxfException>(() => decoder.Unmarshal(doc, new SecretDemo()));
        Assert.Contains(want, e.Message);
        Assert.True(seen.Count <= 1);
    }

    // Without the hook, both forms store the value as before.
    [Theory]
    [InlineData("db_password = \"x\"")]
    [InlineData("db_password { value = \"x\" hint = \"h\" }")]
    public void NoHook_StoresTheValue(string doc)
    {
        var msg = new SecretDemo();
        new Decoder().Unmarshal(doc, msg);
        Assert.Equal("x", msg.DbPassword.Value);
    }

    // BindRow decodes a row as a body, so the hook covers a row's cells.
    [Fact]
    public void BindRow_FiresForACell()
    {
        var ds = Parser.Parse("@dataset secrethook.v1.Root ( pw, db.password )\n( \"a\", { value = \"b\" } )\n").Datasets[0];
        var (decoder, seen) = Recording();
        var msg = new Root();
        decoder.BindRow(msg, ds.Columns, ds.Rows[0]);
        Assert.Equal([("db.password", "b"), ("pw", "a")], Sorted(seen));
        Assert.Equal("", msg.Pw.Value);
        Assert.Equal("", msg.Db.Password.Value);
    }

    // The hook keeps the plaintext off the wire form too.
    [Fact]
    public void Serialized_HoldsNoPlaintext()
    {
        var (decoder, _) = Recording();
        var msg = new SecretDemo();
        decoder.Unmarshal("db_password { value = \"supersecret\" hint = \"h\" } backup_keys = [\"supersecret\"]", msg);
        Assert.DoesNotContain("supersecret", msg.ToByteString().ToStringUtf8());
    }
}
