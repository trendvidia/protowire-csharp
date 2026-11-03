# Text fixtures

Cross-port fixtures for the PXF text a marshaller writes (#503). Each case is one message and the text protowire-go writes for it.

| File | What it is |
|---|---|
| `text.proto`, `text.binpb` | The schema, and its descriptor set with imports |
| `cases/<Message>.<name>.textpb` | The value, as protobuf text format. The file name's first word is the message in `text.v1`. |
| `cases/<Message>.<name>.binpb` | That value as protobuf bytes, which is what ports read |
| `cases/<Message>.<name>.expected.pxf` | The golden, written by protowire-go |

To add a case, add a `.textpb` and run `generate.sh`. A changed golden changes the text every port must write.

`scripts/cross_text_check.sh` runs each port's `check-decode --format pb-text` over the cases. A codegen port has no descriptors at runtime, so it holds a byte copy of `text.proto`.

The text is what the reference writes, not a rule that it's the only valid spelling. Decoders still accept every form the grammar allows.
