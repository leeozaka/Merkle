"""Main entrypoint for Merkle Python Adapter Protocol 1.0."""

import json
import sys
from typing import Any, Dict

from merkle_adapter.detector import detect_python
from merkle_adapter.indexer import index_python
from merkle_adapter.mapper import map_impact
from merkle_adapter.protocol import AdapterDescriptor, AdapterProcessResponse


def handle_request(raw_input: bytes) -> tuple[int, Dict[str, Any]]:
    request_id = "unknown"
    operation = "unknown"

    if not raw_input.strip():
        resp = AdapterProcessResponse.fail(request_id, operation, "EmptyRequest", "No JSON input received.")
        return 2, resp.to_dict()

    try:
        req = json.loads(raw_input.decode("utf-8"))
    except json.JSONDecodeError as ex:
        resp = AdapterProcessResponse.fail(request_id, operation, "AdapterProtocolMalformed", f"Invalid JSON: {ex}")
        return 2, resp.to_dict()

    request_id = req.get("requestId") or "req"
    operation = req.get("operation") or "unknown"
    protocol_version = req.get("protocolVersion")

    if protocol_version != "1.0":
        resp = AdapterProcessResponse.fail(request_id, operation, "UnsupportedProtocol", "Expected protocol version 1.0")
        return 2, resp.to_dict()

    payload = req.get("payload", {}) or {}

    try:
        if operation == "describe":
            result_payload = AdapterDescriptor().to_dict()
        elif operation == "detect":
            files = payload.get("files", []) or []
            result_payload = detect_python(files)
        elif operation == "index":
            snapshot = payload.get("snapshot", {}) or {}
            result_payload = index_python(snapshot).to_dict()
        elif operation == "map":
            snapshot = payload.get("snapshot", {}) or {}
            index = payload.get("index", {}) or {}
            changed_units = payload.get("changedUnits", []) or []
            result_payload = map_impact(snapshot, index, changed_units).to_dict()
        else:
            resp = AdapterProcessResponse.fail(request_id, operation, "UnsupportedOperation", f"Operation '{operation}' is not supported.")
            return 2, resp.to_dict()

        resp = AdapterProcessResponse.ok(request_id, operation, result_payload)
        return 0, resp.to_dict()

    except Exception as ex:
        sys.stderr.write(f"Unhandled error in adapter: {ex}\n")
        resp = AdapterProcessResponse.fail(request_id, operation, "AdapterUnhandledError", str(ex))
        return 3, resp.to_dict()


def main() -> None:
    raw_input = sys.stdin.buffer.read()
    exit_code, response_data = handle_request(raw_input)
    sys.stdout.write(json.dumps(response_data, ensure_ascii=False) + "\n")
    sys.stdout.flush()
    sys.exit(exit_code)


if __name__ == "__main__":
    main()
