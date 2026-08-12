"""Unit tests for Protocol 1.0 message handling."""

import json
import unittest

from merkle_adapter.__main__ import handle_request


class ProtocolHandlerTests(unittest.TestCase):
    def test_describe_operation(self):
        req = {
            "protocolVersion": "1.0",
            "requestId": "desc-1",
            "operation": "describe",
            "payload": {},
        }
        code, res = handle_request(json.dumps(req).encode("utf-8"))
        self.assertEqual(code, 0)
        self.assertEqual(res["protocolVersion"], "1.0")
        self.assertEqual(res["requestId"], "desc-1")
        self.assertEqual(res["operation"], "describe")
        self.assertTrue(res["success"])
        self.assertEqual(res["payload"]["language"], "python")
        self.assertEqual(res["payload"]["unitIdentityVersion"], "1")
        self.assertEqual(res["payload"]["testIdentityVersion"], "1")
        self.assertIn("index", res["payload"]["capabilities"])
        self.assertIn("map", res["payload"]["capabilities"])

    def test_unsupported_protocol_fails(self):
        req = {
            "protocolVersion": "9.9",
            "requestId": "err-1",
            "operation": "describe",
            "payload": {},
        }
        code, res = handle_request(json.dumps(req).encode("utf-8"))
        self.assertEqual(code, 2)
        self.assertFalse(res["success"])
        self.assertEqual(res["error"]["code"], "UnsupportedProtocol")

    def test_unsupported_operation_fails(self):
        req = {
            "protocolVersion": "1.0",
            "requestId": "err-2",
            "operation": "invalidOp",
            "payload": {},
        }
        code, res = handle_request(json.dumps(req).encode("utf-8"))
        self.assertEqual(code, 2)
        self.assertFalse(res["success"])
        self.assertEqual(res["error"]["code"], "UnsupportedOperation")

    def test_malformed_json_fails(self):
        code, res = handle_request(b"{not-valid-json")
        self.assertEqual(code, 2)
        self.assertFalse(res["success"])
        self.assertEqual(res["error"]["code"], "AdapterProtocolMalformed")


if __name__ == "__main__":
    unittest.main()
