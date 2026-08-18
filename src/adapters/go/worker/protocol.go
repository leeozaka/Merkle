package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"sort"
	"strings"
)

const (
	protocolVersion = "1.0"
	adapterVersion  = "0.1.0"
	maxProtocolSize = 16 << 20
)

type processRequest struct {
	ProtocolVersion string          `json:"protocolVersion"`
	RequestID       string          `json:"requestId"`
	Operation       string          `json:"operation"`
	Payload         json.RawMessage `json:"payload"`
}

type processError struct {
	Code    string `json:"code"`
	Message string `json:"message"`
}

type processResponse struct {
	ProtocolVersion string        `json:"protocolVersion"`
	RequestID       string        `json:"requestId"`
	Operation       string        `json:"operation"`
	Success         bool          `json:"success"`
	Payload         any           `json:"payload,omitempty"`
	Error           *processError `json:"error,omitempty"`
}

type descriptor struct {
	ProtocolVersion     string   `json:"protocolVersion"`
	Language            string   `json:"language"`
	Producer            string   `json:"producer"`
	AdapterVersion      string   `json:"adapterVersion"`
	UnitIdentityVersion string   `json:"unitIdentityVersion"`
	TestIdentityVersion string   `json:"testIdentityVersion"`
	Capabilities        []string `json:"capabilities"`
	Profiles            []string `json:"profiles"`
	SupportedTargets    []string `json:"supportedTargets"`
	SupportedPlatforms  []string `json:"supportedPlatforms"`
}

type snapshotIdentity struct {
	Value     string `json:"value"`
	Reference string `json:"reference"`
	Provider  string `json:"provider"`
}

type snapshotFile struct {
	Path        string `json:"path"`
	ContentHash string `json:"contentHash"`
	Content     []byte `json:"content"`
	Kind        string `json:"kind"`
	Mode        string `json:"mode"`
}

type repositorySnapshot struct {
	Identity           snapshotIdentity `json:"identity"`
	RepositoryRoot     string           `json:"repositoryRoot"`
	RepositoryIdentity string           `json:"repositoryIdentity"`
	Files              []snapshotFile   `json:"files"`
}

type indexRequest struct {
	Snapshot           repositorySnapshot `json:"snapshot"`
	ConfiguredSolution string             `json:"configuredSolution"`
}

type sourceUnit struct {
	Identity          string `json:"identity"`
	Kind              string `json:"kind"`
	Path              string `json:"path"`
	ContentHash       string `json:"contentHash"`
	SemanticSignature string `json:"semanticSignature"`
}

type impactEdge struct {
	SourceIdentity string `json:"sourceIdentity"`
	TargetIdentity string `json:"targetIdentity"`
	Kind           string `json:"kind"`
}

type testDescriptor struct {
	Identity    string `json:"identity"`
	DisplayName string `json:"displayName"`
	Framework   string `json:"framework"`
}

type adapterIndex struct {
	Units    []sourceUnit     `json:"units"`
	Edges    []impactEdge     `json:"edges"`
	Tests    []testDescriptor `json:"tests"`
	Warnings []string         `json:"warnings"`
}

type changedUnit struct {
	Identity   string `json:"identity"`
	Kind       string `json:"kind"`
	ChangeKind string `json:"changeKind"`
	Mapped     bool   `json:"mapped"`
}

type mapRequest struct {
	Snapshot     repositorySnapshot `json:"snapshot"`
	Index        adapterIndex       `json:"index"`
	ChangedUnits []changedUnit      `json:"changedUnits"`
}

type impactReason struct {
	Kind        string   `json:"kind"`
	ChangedUnit string   `json:"changedUnit"`
	Path        []string `json:"path"`
}

type requestedTest struct {
	Identity    string         `json:"identity"`
	DisplayName string         `json:"displayName"`
	Framework   string         `json:"framework"`
	Reasons     []impactReason `json:"reasons"`
	Mandatory   bool           `json:"mandatory"`
}

type mappingResult struct {
	RequestedTests []requestedTest `json:"requestedTests"`
	UnmappedUnits  []changedUnit   `json:"unmappedUnits"`
	Warnings       []string        `json:"warnings"`
}

type detectedLanguage struct {
	Language   string              `json:"language"`
	Confidence string              `json:"confidence"`
	Evidence   []detectionEvidence `json:"evidence"`
}

type detectionEvidence struct {
	Kind  string `json:"kind"`
	Path  string `json:"path"`
	Count int    `json:"count"`
}

func makeDescriptor() descriptor {
	return descriptor{protocolVersion, "golang", "merkle", adapterVersion, "1", "1",
		[]string{"detect", "index", "map"}, []string{"minimal", "semantic"}, []string{"go1.22+"}, []string{"linux", "macos"}}
}

func failResponse(requestID, operation, code, message string) processResponse {
	return processResponse{ProtocolVersion: protocolVersion, RequestID: requestID, Operation: operation, Success: false, Error: &processError{Code: code, Message: message}}
}

func okResponse(requestID, operation string, payload any) processResponse {
	return processResponse{ProtocolVersion: protocolVersion, RequestID: requestID, Operation: operation, Success: true, Payload: payload}
}

func run(in io.Reader, out io.Writer) error {
	requestID, operation := "unknown", "unknown"
	limited := io.LimitReader(in, maxProtocolSize+1)
	data, err := io.ReadAll(limited)
	if err != nil {
		return writeResponse(out, failResponse(requestID, operation, "AdapterProtocolMalformed", "Could not read request."))
	}
	if len(data) == 0 {
		return writeResponse(out, failResponse(requestID, operation, "AdapterProtocolMalformed", "No JSON input received."))
	}
	if len(data) > maxProtocolSize {
		return writeResponse(out, failResponse(requestID, operation, "AdapterProtocolMalformed", "The request exceeds the 16 MiB protocol limit."))
	}
	var req processRequest
	dec := json.NewDecoder(bytes.NewReader(data))
	if err := dec.Decode(&req); err != nil {
		return writeResponse(out, failResponse(requestID, operation, "AdapterProtocolMalformed", "The request is not valid JSON."))
	}
	var extra any
	if err := dec.Decode(&extra); err != io.EOF {
		return writeResponse(out, failResponse(requestID, operation, "AdapterProtocolMalformed", "The request must contain one JSON envelope."))
	}
	requestID, operation = req.RequestID, req.Operation
	if requestID == "" {
		requestID = "unknown"
	}
	if operation == "" {
		operation = "unknown"
	}
	if req.ProtocolVersion != protocolVersion {
		return writeResponse(out, failResponse(requestID, operation, "UnsupportedProtocol", "Expected protocol version 1.0."))
	}
	var payload any
	switch operation {
	case "describe":
		payload = makeDescriptor()
	case "detect":
		var snap repositorySnapshot
		if err := decodePayload(req.Payload, &snap); err != nil {
			return writeResponse(out, failResponse(requestID, operation, "InvalidRequest", err.Error()))
		}
		payload = detectGo(snap)
	case "index":
		var input indexRequest
		if err := decodePayload(req.Payload, &input); err != nil {
			return writeResponse(out, failResponse(requestID, operation, "InvalidRequest", err.Error()))
		}
		indexed, err := indexGoScoped(input.Snapshot, input.ConfiguredSolution)
		if err != nil {
			return writeResponse(out, failResponse(requestID, operation, "InvalidRequest", err.Error()))
		}
		payload = indexed
	case "map":
		var input mapRequest
		if err := decodePayload(req.Payload, &input); err != nil {
			return writeResponse(out, failResponse(requestID, operation, "InvalidRequest", err.Error()))
		}
		payload = mapGo(input)
	default:
		return writeResponse(out, failResponse(requestID, operation, "UnsupportedOperation", fmt.Sprintf("Unsupported operation %q.", operation)))
	}
	return writeResponse(out, okResponse(requestID, operation, payload))
}

func decodePayload(raw json.RawMessage, out any) error {
	if len(raw) == 0 || string(raw) == "null" {
		return fmt.Errorf("request payload is required")
	}
	dec := json.NewDecoder(bytes.NewReader(raw))
	if err := dec.Decode(out); err != nil {
		return fmt.Errorf("request payload has an invalid shape")
	}
	var extra any
	if err := dec.Decode(&extra); err != io.EOF {
		return fmt.Errorf("request payload has trailing data")
	}
	return nil
}

func writeResponse(out io.Writer, response processResponse) error {
	data, err := json.Marshal(response)
	if err != nil {
		return err
	}
	_, err = out.Write(append(data, '\n'))
	return err
}

func normalizePath(path string) string {
	path = strings.ReplaceAll(path, "\\", "/")
	path = strings.TrimPrefix(path, "./")
	for strings.Contains(path, "//") {
		path = strings.ReplaceAll(path, "//", "/")
	}
	return path
}

func contentHash(file snapshotFile) string {
	if file.ContentHash != "" {
		return file.ContentHash
	}
	h := sha256.Sum256(file.Content)
	return hex.EncodeToString(h[:])
}

func sortStringsUnique(values []string) []string {
	seen := make(map[string]bool, len(values))
	result := make([]string, 0, len(values))
	for _, value := range values {
		if !seen[value] {
			seen[value] = true
			result = append(result, value)
		}
	}
	sort.Strings(result)
	return result
}
