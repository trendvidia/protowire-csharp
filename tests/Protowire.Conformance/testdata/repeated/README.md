# Repeated-field spellings

Cross-port fixtures for how a repeated field is written (draft `-01`
§ Entries and Keys; issue
[#337](https://github.com/trendvidia/protowire/issues/337)). A repeated
field is written with a list value. It may be bound more than once, and
the lists concatenate. A scalar or a block bound to a repeated field is
an error. The keyed surface form of a keyed repeated field is the one
exception; [`../keyed/`](../keyed/) covers it.

All fixtures bind against `test.v1.AllTypes` in
[`../test.proto`](../test.proto). `cmd/pxf`'s `repeated_test.go` drives
them through `pxf validate` against the reference implementation.

| File | Expected |
|---|---|
| `concat-lists.pxf` | accept; `repeated_string` is `a, b, c` and `repeated_nested` is `n1, n2` |
| `err-scalar-entries.pxf` | reject |
| `err-block-entries.pxf` | reject |
| `err-assigned-block.pxf` | reject |

Measured before the text changed (issue #337): protowire-go v1.8.1 and
protowire-dart both concatenate repeated list bindings. Go rejects all
three error fixtures' spellings. Dart rejects the scalar and `= { }`
forms but accepts repeated blocks, and is tracked to reject them in
[protowire-dart#26](https://github.com/trendvidia/protowire-dart/issues/26).
