package main

import (
	"bytes"
	"encoding/json"
	"strings"
	"testing"
)

func testSnapshot(files ...snapshotFile) repositorySnapshot {
	return repositorySnapshot{RepositoryRoot: "/repo", RepositoryIdentity: "repo", Files: files}
}

func goFile(name, content string) snapshotFile {
	return snapshotFile{Path: name, Content: []byte(content)}
}

func TestProtocolDescribeAndErrors(t *testing.T) {
	var output bytes.Buffer
	if err := run(strings.NewReader(`{"protocolVersion":"1.0","requestId":"r1","operation":"describe","payload":{}}`), &output); err != nil {
		t.Fatal(err)
	}
	var response processResponse
	if err := json.Unmarshal(output.Bytes(), &response); err != nil {
		t.Fatal(err)
	}
	if !response.Success || response.RequestID != "r1" {
		t.Fatalf("unexpected response: %+v", response)
	}
	descriptor, ok := response.Payload.(map[string]any)
	if !ok || descriptor["language"] != "golang" || descriptor["producer"] != "merkle" {
		t.Fatalf("bad descriptor: %#v", response.Payload)
	}
	output.Reset()
	if err := run(strings.NewReader(`{"protocolVersion":"9.0","requestId":"r2","operation":"describe","payload":{}}`), &output); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(output.String(), `"code":"UnsupportedProtocol"`) {
		t.Fatalf("missing structured protocol error: %s", output.String())
	}
	output.Reset()
	if err := run(strings.NewReader(`{"protocolVersion":"1.0","requestId":"r3","operation":"nope","payload":{}}`), &output); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(output.String(), `"code":"UnsupportedOperation"`) {
		t.Fatalf("missing operation error: %s", output.String())
	}
}

func TestIndexStableIDsAndGoTests(t *testing.T) {
	snapshot := testSnapshot(
		goFile("go.mod", "module example.com/app\n\ngo 1.22\n"),
		goFile("pkg/service.go", "package pkg\n\ntype Service[T any] interface { Run(T) error }\n\ntype Impl struct{}\nfunc (Impl) Run(v string) error { return nil }\nfunc Use() { Impl{}.Run(\"x\") }\n"),
		goFile("pkg/service_test.go", "package pkg_test\nimport \"testing\"\nfunc TestService(t *testing.T) { }\nfunc BenchmarkService(b *testing.B) { }\nfunc FuzzService(f *testing.F) { }\nfunc ExampleService() { }\n"),
	)
	first := indexGo(snapshot)
	second := indexGo(snapshot)
	a, _ := json.Marshal(first)
	b, _ := json.Marshal(second)
	if !bytes.Equal(a, b) {
		t.Fatal("index output is not deterministic")
	}
	ids := map[string]bool{}
	for _, unit := range first.Units {
		ids[unit.Identity] = true
	}
	for _, edge := range first.Edges {
		if edge.Kind == "containment" && (!ids[edge.SourceIdentity] || !ids[edge.TargetIdentity]) {
			t.Fatalf("containment edge points outside emitted units: %+v", edge)
		}
	}
	for _, wanted := range []string{
		"golang:project:go.mod", "golang:file:pkg/service.go", "golang:package:example.com/app/pkg",
		"golang:type:example.com/app/pkg/Service", "golang:type:example.com/app/pkg/Impl",
		"golang:member:example.com/app/pkg/Impl/Run(string)", "golang:member:example.com/app/pkg/Use()",
	} {
		if !ids[wanted] {
			t.Errorf("missing unit %s", wanted)
		}
	}
	if len(first.Tests) != 4 {
		t.Fatalf("tests=%+v", first.Tests)
	}
	for _, test := range first.Tests {
		if test.Framework != "go-testing" || !strings.HasPrefix(test.Identity, "golang:example.com/app/pkg:") {
			t.Errorf("bad test: %+v", test)
		}
	}
}

func TestIndexParseErrorPreservesFileUnit(t *testing.T) {
	result := indexGo(testSnapshot(goFile("go.mod", "module example.com/app\n"), goFile("bad.go", "package broken {")))
	found := false
	for _, unit := range result.Units {
		if unit.Identity == "golang:file:bad.go" {
			found = true
		}
	}
	if !found || len(result.Warnings) == 0 {
		t.Fatalf("file/warning missing: %+v", result)
	}
}

func TestIndexTestNameRulesAndHostKinds(t *testing.T) {
	snapshot := testSnapshot(
		goFile("go.mod", "module example.com/app\n"),
		goFile("rules_test.go", "package app\nimport \"testing\"\nfunc Test(t *testing.T) {}\nfunc Benchmark(b *testing.B) {}\nfunc Fuzz(f *testing.F) {}\nfunc Example() {}\nfunc TestX(t *testing.T) {}\nfunc TestÉ(t *testing.T) {}\nfunc Testé(t *testing.T) {}\nfunc Testhelper(t *testing.T) {}\nfunc TestWrong(t *testing.B) {}\nfunc BenchmarkWrong(b *testing.T) {}\nfunc FuzzWrong(f *testing.T) {}\nfunc ExampleWrong() int { return 1 }\n"),
		goFile("alias_test.go", "package app\nimport tst \"testing\"\nfunc TestAlias(t *tst.T) {}\n"),
	)
	result := indexGo(snapshot)
	if len(result.Tests) != 7 {
		t.Fatalf("unexpected test discovery: %+v", result.Tests)
	}
	for _, test := range result.Tests {
		if strings.HasSuffix(test.Identity, ":Testhelper") {
			t.Fatalf("lowercase test suffix was indexed: %+v", test)
		}
	}
	if !strings.HasSuffix(result.Tests[0].Identity, ":Benchmark") {
		t.Fatalf("deterministic test ordering changed unexpectedly: %+v", result.Tests)
	}
	allowed := map[string]bool{"project": true, "file": true, "namespace": true, "type": true, "member": true}
	encoded, err := json.Marshal(result)
	if err != nil {
		t.Fatal(err)
	}
	var wire struct {
		Units []struct {
			Kind string `json:"kind"`
		} `json:"units"`
	}
	if err := json.Unmarshal(encoded, &wire); err != nil {
		t.Fatal(err)
	}
	for _, unit := range wire.Units {
		if !allowed[unit.Kind] {
			t.Fatalf("worker emitted a kind outside host enum: %q", unit.Kind)
		}
	}
}

func TestMapReverseTransitiveAndUnmapped(t *testing.T) {
	index := adapterIndex{
		Tests: []testDescriptor{{"golang:example.com/app:TestA", "example.com/app.TestA", "go-testing"}},
		Edges: []impactEdge{
			{"golang:member:example.com/app:middle()", "golang:member:example.com/app:leaf()", "staticDependency"},
			{"golang:member:example.com/app:TestA(*testing.T)", "golang:member:example.com/app:middle()", "staticDependency"},
			{"golang:member:example.com/app:TestA(*testing.T)", "golang:example.com/app:TestA", "staticDependency"},
		},
	}
	result := mapGo(mapRequest{Index: index, ChangedUnits: []changedUnit{{Identity: "golang:member:example.com/app:leaf()", Kind: "member", ChangeKind: "modified"}, {Identity: "golang:unknown", Kind: "member", ChangeKind: "modified"}}})
	if len(result.RequestedTests) != 1 || result.RequestedTests[0].Identity != "golang:example.com/app:TestA" {
		t.Fatalf("bad mapping: %+v", result)
	}
	if len(result.UnmappedUnits) != 1 || result.UnmappedUnits[0].Identity != "golang:unknown" {
		t.Fatalf("bad unmapped: %+v", result)
	}
}

func TestMultiModuleIdentityAndConfigFallback(t *testing.T) {
	snapshot := testSnapshot(
		goFile("go.mod", "module example.com/root\n"),
		goFile("go.sum", "h1:root\n"),
		goFile("nested/go.mod", "module example.com/nested\n"),
		goFile("root_test.go", "package root\nimport \"testing\"\nfunc TestRoot(t *testing.T) {}\n"),
		goFile("nested/nested_test.go", "package nested\nimport \"testing\"\nfunc TestNested(t *testing.T) {}\n"),
	)
	indexed := indexGo(snapshot)
	hasRoot, hasNested := false, false
	for _, unit := range indexed.Units {
		if unit.Identity == "golang:package:example.com/root" {
			hasRoot = true
		}
		if unit.Identity == "golang:package:example.com/nested" {
			hasNested = true
		}
		if unit.Identity == "golang:package:example.com/root" && unit.Kind != "namespace" {
			t.Fatalf("package kind must match host enum: %+v", unit)
		}
	}
	if !hasRoot || !hasNested {
		t.Fatalf("module identities were not separated: %+v", indexed.Units)
	}
	mapped := mapGo(mapRequest{Snapshot: snapshot, Index: indexed, ChangedUnits: []changedUnit{{Identity: "golang:project:go.mod", Kind: "project", ChangeKind: "modified"}}})
	if len(mapped.RequestedTests) != 1 || mapped.RequestedTests[0].Identity != "golang:example.com/root:TestRoot" || len(mapped.UnmappedUnits) != 0 {
		t.Fatalf("root config fallback crossed module boundary: %+v", mapped)
	}
	sumMapped := mapGo(mapRequest{Snapshot: snapshot, Index: indexed, ChangedUnits: []changedUnit{{Identity: "golang:project:go.sum", Kind: "project", ChangeKind: "modified"}}})
	if len(sumMapped.RequestedTests) != 1 || len(sumMapped.UnmappedUnits) != 0 {
		t.Fatalf("go.sum config fallback was not mapped: %+v", sumMapped)
	}
}

func TestProtocolIndexHonorsConfiguredGoScopes(t *testing.T) {
	snapshot := testSnapshot(
		goFile("go.mod", "module example.com/root\n"),
		goFile("root.go", "package root\n"),
		goFile("nested/go.mod", "module example.com/nested\n"),
		goFile("nested/nested.go", "package nested\n"),
		goFile("other/go.mod", "module example.com/other\n"),
		goFile("other/other.go", "package other\n"),
		goFile("go.work", "go 1.22\nuse (\n ./nested\n)\n"),
	)
	requestIndex := func(input repositorySnapshot, configured string) adapterIndex {
		payload, err := json.Marshal(indexRequest{Snapshot: input, ConfiguredSolution: configured})
		if err != nil {
			t.Fatal(err)
		}
		envelope, err := json.Marshal(processRequest{ProtocolVersion: "1.0", RequestID: "scope", Operation: "index", Payload: payload})
		if err != nil {
			t.Fatal(err)
		}
		var output bytes.Buffer
		if err := run(bytes.NewReader(envelope), &output); err != nil {
			t.Fatal(err)
		}
		var wire struct {
			Success bool            `json:"success"`
			Payload json.RawMessage `json:"payload"`
			Error   *processError   `json:"error"`
		}
		if err := json.Unmarshal(output.Bytes(), &wire); err != nil {
			t.Fatal(err)
		}
		if !wire.Success {
			t.Fatalf("scope request failed: %+v", wire.Error)
		}
		var result adapterIndex
		if err := json.Unmarshal(wire.Payload, &result); err != nil {
			t.Fatal(err)
		}
		return result
	}
	hasUnit := func(index adapterIndex, identity string) bool {
		for _, unit := range index.Units {
			if unit.Identity == identity {
				return true
			}
		}
		return false
	}
	nested := requestIndex(snapshot, "nested/go.mod")
	if !hasUnit(nested, "golang:package:example.com/nested") || hasUnit(nested, "golang:package:example.com/root") || hasUnit(nested, "golang:package:example.com/other") {
		t.Fatalf("configured nested scope leaked modules: %+v", nested.Units)
	}
	root := requestIndex(snapshot, "go.mod")
	if !hasUnit(root, "golang:package:example.com/root") || hasUnit(root, "golang:package:example.com/nested") || hasUnit(root, "golang:package:example.com/other") {
		t.Fatalf("configured root scope leaked nested/unrelated modules: %+v", root.Units)
	}
	workspace := requestIndex(snapshot, "")
	if !hasUnit(workspace, "golang:project:go.work") || !hasUnit(workspace, "golang:package:example.com/nested") || hasUnit(workspace, "golang:package:example.com/root") || hasUnit(workspace, "golang:package:example.com/other") {
		t.Fatalf("go.work scope was not honored: %+v", workspace.Units)
	}
	invalidPayload, _ := json.Marshal(indexRequest{Snapshot: snapshot, ConfiguredSolution: "missing/go.mod"})
	invalidEnvelope, _ := json.Marshal(processRequest{ProtocolVersion: "1.0", RequestID: "bad-scope", Operation: "index", Payload: invalidPayload})
	var invalidOutput bytes.Buffer
	if err := run(bytes.NewReader(invalidEnvelope), &invalidOutput); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(invalidOutput.String(), `"code":"InvalidRequest"`) {
		t.Fatalf("missing structured scope error: %s", invalidOutput.String())
	}
	dotSnapshot := testSnapshot(
		goFile("go.mod", "module example.com/root\n"),
		goFile("root.go", "package root\n"),
		goFile("nested/go.mod", "module example.com/nested\n"),
		goFile("nested/nested.go", "package nested\n"),
		goFile("go.work", "go 1.22\nuse .\n"),
		goFile("go.work.sum", "h1:workspace\n"),
	)
	dot := requestIndex(dotSnapshot, "")
	if !hasUnit(dot, "golang:package:example.com/root") || hasUnit(dot, "golang:package:example.com/nested") {
		t.Fatalf("use . included an unlisted nested module: %+v", dot.Units)
	}
	if !hasUnit(dot, "golang:project:go.work.sum") {
		t.Fatalf("go.work.sum was not indexed as a project unit: %+v", dot.Units)
	}
	for _, usePath := range []string{"", "./missing", "../outside"} {
		workContent := "go 1.22\n"
		if usePath != "" {
			workContent += "use " + usePath + "\n"
		}
		badSnapshot := testSnapshot(goFile("go.mod", "module example.com/root\n"), goFile("go.work", workContent))
		badPayload, _ := json.Marshal(indexRequest{Snapshot: badSnapshot, ConfiguredSolution: ""})
		badEnvelope, _ := json.Marshal(processRequest{ProtocolVersion: "1.0", RequestID: "bad-use", Operation: "index", Payload: badPayload})
		var badOutput bytes.Buffer
		if err := run(bytes.NewReader(badEnvelope), &badOutput); err != nil {
			t.Fatal(err)
		}
		if !strings.Contains(badOutput.String(), `"code":"InvalidRequest"`) {
			t.Fatalf("invalid use %q was accepted: %s", usePath, badOutput.String())
		}
	}
}

func TestMapWorkspaceSumUsesAdjacentWorkspace(t *testing.T) {
	snapshot := testSnapshot(
		goFile("a/go.work", "go 1.22\nuse ./mod\n"),
		goFile("a/mod/go.mod", "module example.com/a\n"),
		goFile("b/go.work", "go 1.22\nuse ./mod\n"),
		goFile("b/mod/go.mod", "module example.com/b\n"),
	)
	index := adapterIndex{Tests: []testDescriptor{{"golang:example.com/b:TestB", "example.com/b.TestB", "go-testing"}}}
	result := mapGo(mapRequest{Snapshot: snapshot, Index: index, ChangedUnits: []changedUnit{{Identity: "golang:project:b/go.work.sum", Kind: "project", ChangeKind: "modified"}}})
	if len(result.RequestedTests) != 1 || result.RequestedTests[0].Identity != "golang:example.com/b:TestB" || len(result.UnmappedUnits) != 0 {
		t.Fatalf("go.work.sum did not use its adjacent workspace scope: %+v", result)
	}
}

func TestProtocolIndexRejectsAmbiguousWorkspaces(t *testing.T) {
	snapshot := testSnapshot(
		goFile("a/go.work", "go 1.22\nuse ./mod\n"),
		goFile("a/mod/go.mod", "module example.test/a\n"),
		goFile("b/go.work", "go 1.22\nuse ./mod\n"),
		goFile("b/mod/go.mod", "module example.test/b\n"),
	)
	payload, err := json.Marshal(indexRequest{Snapshot: snapshot})
	if err != nil {
		t.Fatal(err)
	}
	envelope, err := json.Marshal(processRequest{ProtocolVersion: "1.0", RequestID: "ambiguous", Operation: "index", Payload: payload})
	if err != nil {
		t.Fatal(err)
	}
	var output bytes.Buffer
	if err := run(bytes.NewReader(envelope), &output); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(output.String(), `"code":"InvalidRequest"`) || !strings.Contains(output.String(), "a/go.work") || !strings.Contains(output.String(), "b/go.work") {
		t.Fatalf("ambiguous workspace scope was not rejected deterministically: %s", output.String())
	}
}

func TestCalculatorLocalCallMapsChangedMemberToTest(t *testing.T) {
	snapshot := testSnapshot(
		goFile("go.mod", "module example.test/calculator\n"),
		goFile("calc.go", "package calculator\nfunc Add(left, right int) int { return left + right }\n"),
		goFile("calc_test.go", "package calculator\nimport \"testing\"\nfunc TestAdd(t *testing.T) { if Add(2, 3) != 5 { t.Fatalf(\"wrong result\") } }\n"),
	)
	indexed := indexGo(snapshot)
	for i := range indexed.Edges {
		if indexed.Edges[i].Kind == "staticDependency" {
			indexed.Edges[i].Kind = "StaticDependency"
		}
	}
	for _, warning := range indexed.Warnings {
		if strings.Contains(warning, "Fatalf") {
			t.Fatalf("standard-library typed receiver was reported unresolved: %q", warning)
		}
	}
	result := mapGo(mapRequest{Snapshot: snapshot, Index: indexed, ChangedUnits: []changedUnit{{Identity: "golang:member:example.test/calculator/Add(int,int)", Kind: "member", ChangeKind: "modified"}}})
	if len(result.RequestedTests) != 1 || result.RequestedTests[0].Identity != "golang:example.test/calculator:TestAdd" {
		t.Fatalf("changed Add did not map to TestAdd: index=%+v result=%+v", indexed, result)
	}
}

func TestCalculatorExternalPackageCallMapsChangedMemberToTest(t *testing.T) {
	snapshot := testSnapshot(
		goFile("go.mod", "module example.test/calculator\n"),
		goFile("calc.go", "package calculator\nfunc Add(left, right int) int { return left + right }\n"),
		goFile("calc_test.go", "package calculator_test\nimport (\"testing\"; calculator \"example.test/calculator\")\nfunc TestAdd(t *testing.T) { if calculator.Add(2, 3) != 5 { t.Fatalf(\"wrong result\") } }\n"),
	)
	indexed := indexGo(snapshot)
	result := mapGo(mapRequest{Snapshot: snapshot, Index: indexed, ChangedUnits: []changedUnit{{Identity: "golang:member:example.test/calculator/Add(int,int)", Kind: "member", ChangeKind: "modified"}}})
	if len(result.RequestedTests) != 1 || result.RequestedTests[0].Identity != "golang:example.test/calculator:TestAdd" {
		t.Fatalf("changed Add did not map from external package test: index=%+v result=%+v", indexed, result)
	}
}

func TestImportedCallsDistinguishExternalAndIndexedPackages(t *testing.T) {
	snapshot := testSnapshot(
		goFile("go.mod", "module example.test/app\n"),
		goFile("dep/dep.go", "package dep\nfunc Known() string { return \"ok\" }\n"),
		goFile("use.go", "package app\nimport (\"fmt\"; \"example.test/app/dep\")\nfunc Use() string { _ = fmt.Sprintf(\"%d\", 1); _ = dep.Missing(); return dep.Known() }\n"),
	)
	indexed := indexGo(snapshot)
	hasLocalEdge, warnedFmt, warnedMissing := false, false, false
	for _, edge := range indexed.Edges {
		if edge.SourceIdentity == "golang:member:example.test/app/Use()" && edge.TargetIdentity == "golang:member:example.test/app/dep/Known()" {
			hasLocalEdge = true
		}
	}
	for _, warning := range indexed.Warnings {
		if strings.Contains(warning, "fmt.Sprintf") {
			warnedFmt = true
		}
		if strings.Contains(warning, "dep.Missing") {
			warnedMissing = true
		}
	}
	if !hasLocalEdge || warnedFmt || !warnedMissing {
		t.Fatalf("imported call classification was incorrect: edges=%+v warnings=%+v", indexed.Edges, indexed.Warnings)
	}
}

func TestMethodIdentityAndMalformedTrailingEnvelope(t *testing.T) {
	snapshot := testSnapshot(goFile("go.mod", "module example.com/app\n"), goFile("a.go", "package app\ntype S struct{}\nfunc (S) Target() {}\nfunc (s S) Caller() { s.Target() }\n"))
	indexed := indexGo(snapshot)
	found := false
	for _, edge := range indexed.Edges {
		if edge.SourceIdentity == "golang:member:example.com/app/S/Caller()" && edge.TargetIdentity == "golang:member:example.com/app/S/Target()" {
			found = true
		}
	}
	if !found {
		t.Fatalf("method edge used a non-method identity: %+v", indexed.Edges)
	}
	var output bytes.Buffer
	if err := run(strings.NewReader(`{"protocolVersion":"1.0","requestId":"r","operation":"describe","payload":{}} {}`), &output); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(output.String(), `"code":"AdapterProtocolMalformed"`) {
		t.Fatalf("trailing envelope was accepted: %s", output.String())
	}
}
