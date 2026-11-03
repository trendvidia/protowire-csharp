# Annotation extension-number fixtures

Cross-port conformance fixtures for the **PXF annotation extension
numbers** — `(pxf.required) = 1314` and `(pxf.default) = 1315` on
`google.protobuf.FieldOptions`, allocated in
[`proto/pxf/annotations.proto`](../../proto/pxf/annotations.proto) inside
the registered block [`STABILITY.md`](../../STABILITY.md) promise 3
describes (issue #244).

Unlike the other fixture directories, these are not consumed by each
port's own test suite. They are driven by
[`scripts/cross_envelope_check.sh`](../../scripts/cross_envelope_check.sh),
which hands every descriptor-driven port the **same compiled descriptor
set** and document through its `dump-envelope --pb` mode and compares the
bytes with the golden here. That is the difference from a port's unit
tests: those compile the port's *own* vendored `annotations.proto`, so a
port whose reader and copy both still say `50001` passes them and fails
here.

All fixtures bind against [`settings.proto`](settings.proto) (package
`settings.v1`, message `Settings`), compiled with its imports into
[`settings.binpb`](settings.binpb) by

```
protoc -I testdata/annotations -I proto --include_imports \
  --descriptor_set_out=testdata/annotations/settings.binpb settings.proto
```

`cmd/pxf/fixture_numbers_test.go` pins that the compiled set carries
`1314` and `1315` and no retired number.

| File | Asserts |
|---|---|
| `ok.pxf` | Only `name` is written; `retries`, `region` and `verbose` take their `(pxf.default)`. The pb bytes equal `ok.expected.hex`, which carries all four fields — a port that misses `1315` emits `name` alone. |
| `missing-required.pxf` | `name` is absent and carries `(pxf.required) = true`. A port must reject the document (`dump-envelope` exits 1); a port that misses `1314` accepts it. |

[`settings_annotated.proto`](settings_annotated.proto) is the same schema in
the annotation form: `@required` and `@default(v)` lower only to the `1327`
carrier, so it needs a v1.2 compiler, and
[`settings_annotated.binpb`](settings_annotated.binpb) is that compiler's
image. `ok.pxf` and `missing-required.pxf` get the same verdicts against it.
The gate does not run it yet. It covers `protoc-gen-pxf-java-meta`, whose
tables must not depend on the spelling: the plugin's test compares the two
schemas' tables, and protowire-java's `lite-fixtures` runs both documents
through the generated code.

`ok.expected.hex` was produced by the reference implementation
(protowire-go) and checked by decoding it back with
`protoc --decode=settings.v1.Settings`.

The SBE numbers `1319`–`1323` are proved the same way by
[`../sbe-bench.pxf`](../sbe-bench.pxf) against
[`../sbe-bench.binpb`](../sbe-bench.binpb) through `dump-envelope --sbe`,
with the golden in [`../sbe-bench.expected.hex`](../sbe-bench.expected.hex).
