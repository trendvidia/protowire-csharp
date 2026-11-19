// SPDX-License-Identifier: MIT
// Copyright (c) 2026 TrendVidia, LLC.

// wire_type_oracle writes what protobuf-go and protowire-go do with a tag of
// every wire type on every field of a 25-field message (#116,
// trendvidia/protowire#614). It is protowire-go's
// TestUnmarshal_WireTypeMatchesProtobuf (encoding/pb/wire_type_test.go) with
// its oracle written down, so that WireTypeTests in tests/Protowire.Pb.Tests
// can hold Pb.Unmarshal to the same verdicts and values without Go.
//
//	go run . -out ../../tests/Protowire.Pb.Tests/testdata/wire-types.json
//
// For each input it records protobuf-go's verdict; on accept, the message
// protobuf-go read, without its unknown fields, re-encoded; and protowire-go's
// error text on reject.
package main

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"flag"
	"log"
	"os"
	"strings"

	"github.com/trendvidia/protocompile"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/reflect/protoreflect"
	"google.golang.org/protobuf/types/dynamicpb"

	"github.com/trendvidia/protowire-go/encoding/pb"
)

// The schema of protowire-go's encoding/pb/conformance_test.go.
const confProto = `
syntax = "proto3";
package conf;

message Inner {
  string name = 1;
  int64 n = 2;
}

message All {
  string s = 1;
  int64 i64 = 2;
  int32 i32 = 3;
  uint64 u64 = 4;
  uint32 u32 = 5;
  sint64 z64 = 6;
  sint32 z32 = 7;
  bool b = 8;
  double f64 = 9;
  float f32 = 10;
  bytes buf = 11;
  Inner inner = 12;
  repeated Inner list = 13;
  repeated uint32 nums = 14;
  repeated sint64 zigs = 15;
  repeated float reals = 16;
  repeated string strs = 17;
  map<string, int64> m = 18;
  map<int32, Inner> mm = 19;
  oneof choice {
    string a = 20;
    int64 c = 21;
    Inner d = 22;
  }
  repeated bool flags = 23;
  repeated int64 negs = 24;
  map<bool, bool> bm = 25;
}
`

type confInner struct {
	Name string `protowire:"1"`
	N    int64  `protowire:"2"`
}

type confAll struct {
	S     string               `protowire:"1"`
	I64   int64                `protowire:"2"`
	I32   int32                `protowire:"3"`
	U64   uint64               `protowire:"4"`
	U32   uint32               `protowire:"5"`
	Z64   int64                `protowire:"6,zigzag"`
	Z32   int32                `protowire:"7,zigzag"`
	B     bool                 `protowire:"8"`
	F64   float64              `protowire:"9"`
	F32   float32              `protowire:"10"`
	Buf   []byte               `protowire:"11"`
	Inner *confInner           `protowire:"12"`
	List  []*confInner         `protowire:"13"`
	Nums  []uint32             `protowire:"14"`
	Zigs  []int64              `protowire:"15,zigzag"`
	Reals []float32            `protowire:"16"`
	Strs  []string             `protowire:"17"`
	M     map[string]int64     `protowire:"18"`
	MM    map[int32]*confInner `protowire:"19"`
	A     string               `protowire:"20"`
	C     int64                `protowire:"21"`
	D     *confInner           `protowire:"22"`
	Flags []bool               `protowire:"23"`
	Negs  []int64              `protowire:"24"`
	BM    map[bool]bool        `protowire:"25"`
}

// wireRecords is wire_type_test.go's: single records for field num, every
// wire type, each with a well-formed value and a value cut short.
func wireRecords(num protowire.Number) [][]byte {
	tag := func(typ protowire.Type) []byte { return protowire.AppendTag(nil, num, typ) }
	cat := func(parts ...[]byte) []byte {
		var b []byte
		for _, p := range parts {
			b = append(b, p...)
		}
		return b
	}
	return [][]byte{
		cat(tag(protowire.VarintType), []byte{0x01}),
		cat(tag(protowire.VarintType), []byte{0x80}), // cut short
		cat(tag(protowire.Fixed64Type), []byte{1, 0, 0, 0, 0, 0, 0, 0}),
		cat(tag(protowire.Fixed64Type), []byte{1, 0, 0}), // cut short
		cat(tag(protowire.BytesType), []byte{0x00}),
		cat(tag(protowire.BytesType), []byte{0x01, 0x01}),
		cat(tag(protowire.BytesType), []byte{0x01}), // cut short
		cat(tag(protowire.StartGroupType), tag(protowire.EndGroupType)),
		tag(protowire.StartGroupType), // never ended
		tag(protowire.EndGroupType),   // ends nothing
		cat(tag(protowire.Fixed32Type), []byte{1, 0, 0, 0}),
		cat(tag(protowire.Fixed32Type), []byte{1, 0}), // cut short
		cat(protowire.AppendVarint(nil, uint64(num)<<3|6), []byte{0x01}),
		cat(protowire.AppendVarint(nil, uint64(num)<<3|7), []byte{0x01}),
	}
}

// wireTypeInputs is wire_type_test.go's: every record for every field, alone
// and inside each message field (12, 13, 22) and map field (18, 19, 25).
func wireTypeInputs() [][]byte {
	var out [][]byte
	for num := protowire.Number(1); num <= 25; num++ {
		out = append(out, wireRecords(num)...)
	}
	for _, outer := range []protowire.Number{12, 13, 18, 19, 22, 25} {
		for inner := protowire.Number(1); inner <= 3; inner++ {
			for _, rec := range wireRecords(inner) {
				b := protowire.AppendTag(nil, outer, protowire.BytesType)
				out = append(out, protowire.AppendBytes(b, rec))
			}
		}
	}
	for _, h := range []string{
		"0a0161" + "0801",     // s: "a", then s as a varint
		"1007" + "1501000000", // i64: 7, then i64 as fixed32
		"720101" + "7001",     // nums packed [1], then nums as a varint
		"7001" + "7501000000", // nums [1], then nums as fixed32
	} {
		b, err := hex.DecodeString(h)
		if err != nil {
			log.Fatal(err)
		}
		out = append(out, b)
	}
	return out
}

type row struct {
	Input  string `json:"input"`
	Accept bool   `json:"accept"`
	// Known is protobuf-go's message without its unknown fields, re-encoded
	// deterministically: what the input means. Absent on a rejection.
	Known string `json:"known,omitempty"`
	// Error is protowire-go's, on a rejection.
	Error string `json:"error,omitempty"`
}

func main() {
	out := flag.String("out", "", "where to write the table")
	flag.Parse()
	comp := protocompile.Compiler{
		Resolver: protocompile.WithStandardImports(&protocompile.SourceResolver{
			Accessor: protocompile.SourceAccessorFromMap(map[string]string{"conf.proto": confProto}),
		}),
	}
	files, err := comp.Compile(context.Background(), "conf.proto")
	if err != nil {
		log.Fatal(err)
	}
	md := files[0].Messages().ByName(protoreflect.Name("All"))

	var rows []row
	for _, in := range wireTypeInputs() {
		r := row{Input: hex.EncodeToString(in)}
		oracle := dynamicpb.NewMessage(md)
		oracleErr := proto.UnmarshalOptions{DiscardUnknown: true}.Unmarshal(in, oracle)
		var ours confAll
		goErr := pb.Unmarshal(in, &ours)
		if oracleErr == nil {
			r.Accept = true
			known, err := proto.MarshalOptions{Deterministic: true}.Marshal(oracle)
			if err != nil {
				log.Fatal(err)
			}
			r.Known = hex.EncodeToString(known)
			if goErr != nil {
				log.Fatalf("%x: protobuf-go accepts it, protowire-go rejects it: %v", in, goErr)
			}
		} else {
			if goErr == nil {
				log.Fatalf("%x: protobuf-go rejects it (%v), protowire-go accepts it", in, oracleErr)
			}
			// protobuf-go writes "proto: " with a no-break space in some
			// binaries, chosen by a hash of the binary; the text is the same.
			r.Error = strings.ReplaceAll(goErr.Error(), " ", " ")
		}
		rows = append(rows, r)
	}
	b, err := json.MarshalIndent(rows, "", "  ")
	if err != nil {
		log.Fatal(err)
	}
	if err := os.WriteFile(*out, append(b, '\n'), 0o644); err != nil {
		log.Fatal(err)
	}
	log.Printf("%d inputs", len(rows))
}
