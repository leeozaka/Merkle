"""Unit tests for impact mapping."""

import unittest

from merkle_adapter.indexer import index_python
from merkle_adapter.mapper import map_impact


class ImpactMapperTests(unittest.TestCase):
    def test_map_changed_method_to_test(self):
        service_code = """
class ProductService:
    def get_product(self, pid: str):
        return {"id": pid}
        
    def delete_product(self, pid: str):
        pass
"""
        test_code = """
import pytest
from app.product_service import ProductService

class TestProductService:
    def test_get_product(self):
        service = ProductService()
        assert service.get_product("p1") == {"id": "p1"}
        
    def test_delete_product(self):
        service = ProductService()
        service.delete_product("p1")
"""
        snapshot = {
            "identity": {"value": "snap-1", "reference": "main", "provider": "git"},
            "repositoryRoot": "/repo",
            "repositoryIdentity": "repo:py-app",
            "files": [
                {"path": "pyproject.toml", "contentHash": "h0", "content": list(b"[project]\nname='app'\n"), "kind": "regularFile", "mode": "100644"},
                {"path": "app/product_service.py", "contentHash": "h1", "content": list(service_code.encode("utf-8")), "kind": "regularFile", "mode": "100644"},
                {"path": "tests/test_product_service.py", "contentHash": "h2", "content": list(test_code.encode("utf-8")), "kind": "regularFile", "mode": "100644"},
            ],
        }

        index = index_python(snapshot).to_dict()

        changed_units = [
            {
                "identity": "python:member:app.product_service/ProductService/get_product(pid)",
                "kind": "member",
                "changeKind": "modified",
                "mapped": False,
            }
        ]

        result = map_impact(snapshot, index, changed_units)
        self.assertEqual(len(result.unmapped_units), 0)
        self.assertGreaterEqual(len(result.requested_tests), 1)

        test_ids = [t.identity for t in result.requested_tests]
        self.assertIn("tests.test_product_service::TestProductService::test_get_product", test_ids)
        self.assertEqual(test_ids, sorted(test_ids))


if __name__ == "__main__":
    unittest.main()
