"""Merkle Protocol 1.0 JSON Records and Message Types for Python."""

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional


@dataclass
class AdapterProcessError:
    code: str
    message: str

    def to_dict(self) -> Dict[str, Any]:
        return {"code": self.code, "message": self.message}


@dataclass
class AdapterProcessResponse:
    protocol_version: str
    request_id: str
    operation: str
    success: bool
    payload: Optional[Any] = None
    error: Optional[AdapterProcessError] = None

    def to_dict(self) -> Dict[str, Any]:
        data: Dict[str, Any] = {
            "protocolVersion": self.protocol_version,
            "requestId": self.request_id,
            "operation": self.operation,
            "success": self.success,
        }
        if self.payload is not None:
            data["payload"] = self.payload
        if self.error is not None:
            data["error"] = self.error.to_dict()
        return data

    @classmethod
    def ok(cls, request_id: str, operation: str, payload: Any) -> "AdapterProcessResponse":
        return cls("1.0", request_id, operation, True, payload=payload)

    @classmethod
    def fail(cls, request_id: str, operation: str, code: str, message: str) -> "AdapterProcessResponse":
        return cls("1.0", request_id, operation, False, error=AdapterProcessError(code, message))


@dataclass
class AdapterDescriptor:
    protocol_version: str = "1.0"
    language: str = "python"
    producer: str = "merkle-community-python"
    adapter_version: str = "1.0.0"
    unit_identity_version: str = "1"
    test_identity_version: str = "1"
    capabilities: List[str] = field(default_factory=lambda: ["detect", "index", "map"])
    profiles: List[str] = field(default_factory=lambda: ["minimal", "semantic"])
    supported_targets: List[str] = field(default_factory=lambda: ["python-3.10", "python-3.11", "python-3.12", "python-3.13", "python-3.14"])
    supported_platforms: List[str] = field(default_factory=lambda: ["linux-x64", "darwin-arm64", "darwin-x64", "windows-x64"])

    def to_dict(self) -> Dict[str, Any]:
        return {
            "protocolVersion": self.protocol_version,
            "language": self.language,
            "producer": self.producer,
            "adapterVersion": self.adapter_version,
            "unitIdentityVersion": self.unit_identity_version,
            "testIdentityVersion": self.test_identity_version,
            "capabilities": self.capabilities,
            "profiles": self.profiles,
            "supportedTargets": self.supported_targets,
            "supportedPlatforms": self.supported_platforms,
        }


@dataclass
class SourceUnit:
    identity: str
    kind: str
    path: str
    content_hash: str
    semantic_signature: str

    def to_dict(self) -> Dict[str, Any]:
        return {
            "identity": self.identity,
            "kind": self.kind,
            "path": self.path,
            "contentHash": self.content_hash,
            "semanticSignature": self.semantic_signature,
        }


@dataclass
class ImpactEdge:
    source_identity: str
    target_identity: str
    kind: str

    def to_dict(self) -> Dict[str, Any]:
        return {
            "sourceIdentity": self.source_identity,
            "targetIdentity": self.target_identity,
            "kind": self.kind,
        }


@dataclass
class TestDescriptor:
    identity: str
    display_name: str
    framework: str

    def to_dict(self) -> Dict[str, Any]:
        return {
            "identity": self.identity,
            "displayName": self.display_name,
            "framework": self.framework,
        }


@dataclass
class ChangedUnit:
    identity: str
    kind: str
    change_kind: str
    mapped: bool

    def to_dict(self) -> Dict[str, Any]:
        return {
            "identity": self.identity,
            "kind": self.kind,
            "changeKind": self.change_kind,
            "mapped": self.mapped,
        }


@dataclass
class ImpactReason:
    kind: str
    changed_unit: str
    path: List[str]

    def to_dict(self) -> Dict[str, Any]:
        return {
            "kind": self.kind,
            "changedUnit": self.changed_unit,
            "path": self.path,
        }


@dataclass
class RequestedTest:
    identity: str
    display_name: str
    framework: str
    reasons: List[ImpactReason]
    mandatory: bool = True

    def to_dict(self) -> Dict[str, Any]:
        return {
            "identity": self.identity,
            "displayName": self.display_name,
            "framework": self.framework,
            "reasons": [r.to_dict() for r in self.reasons],
            "mandatory": self.mandatory,
        }


@dataclass
class AdapterIndex:
    units: List[SourceUnit]
    edges: List[ImpactEdge]
    tests: List[TestDescriptor]
    warnings: List[str] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "units": [u.to_dict() for u in self.units],
            "edges": [e.to_dict() for e in self.edges],
            "tests": [t.to_dict() for t in self.tests],
            "warnings": self.warnings,
        }


@dataclass
class MappingResult:
    requested_tests: List[RequestedTest]
    unmapped_units: List[ChangedUnit]
    warnings: List[str] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "requestedTests": [t.to_dict() for t in self.requested_tests],
            "unmappedUnits": [u.to_dict() for u in self.unmapped_units],
            "warnings": self.warnings,
        }
