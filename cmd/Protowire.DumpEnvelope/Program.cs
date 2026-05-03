// Cross-port envelope wire-compatibility dumper.
//
// Constructs a canonical envelope, encodes it via Protowire.Pb, and prints
// the bytes as a hex string. The same canonical value is constructed in
// every other port; the spec repo's `scripts/cross_envelope_check.sh`
// asserts that all ports' hex output is byte-identical.
//
// Mirrors `protowire-go/scripts/dump_envelope/main.go`.

using Protowire.Envelopes;
using Protowire.Pb;

var env = Envelope.Err(402, "INSUFFICIENT_FUNDS", "balance too low",
    "$3.50", "$10.00");
env.Data = [0xDE, 0xAD, 0xBE, 0xEF];
env.Error!
    .WithField("amount", "MIN_VALUE", "below minimum", "10.00")
    .WithMeta("request_id", "req-123");

byte[] bytes = Pb.Marshal(env);
Console.WriteLine(Convert.ToHexString(bytes).ToLowerInvariant());
