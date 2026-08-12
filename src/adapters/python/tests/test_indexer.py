"""Unit tests for AST semantic indexer."""

import unittest

from merkle_adapter.indexer import index_python


class SemanticIndexerTests(unittest.TestCase):
    def test_index_classes_functions_and_tests(self):
        service_code = """
class UserService:
    def get_user(self, user_id: str):
        return {"id": user_id}
        
    def delete_user(self, user_id: str):
        pass
"""
        test_code = """
import pytest
from app.user_service import UserService

class TestUserService:
    def test_get_user(self):
        service = UserService()
        assert service.get_user("1") == {"id": "1"}
        
    def test_delete_user(self):
        service = UserService()
        service.delete_user("1")
"""
        snapshot = {
            "identity": {"value": "snap-1", "reference": "main", "provider": "git"},
            "repositoryRoot": "/repo",
            "repositoryIdentity": "repo:py-app",
            "files": [
                {"path": "pyproject.toml", "contentHash": "h0", "content": list(b"[project]\nname='app'\n"), "kind": "regularFile", "mode": "100644"},
                {"path": "app/user_service.py", "contentHash": "h1", "content": list(service_code.encode("utf-8")), "kind": "regularFile", "mode": "100644"},
                {"path": "tests/test_user_service.py", "contentHash": "h2", "content": list(test_code.encode("utf-8")), "kind": "regularFile", "mode": "100644"},
            ],
        }

        result = index_python(snapshot)
        units = result.units
        edges = result.edges
        tests = result.tests

        # Verify unit identities
        unit_ids = [u.identity for u in units]
        self.assertIn("python:project:pyproject.toml", unit_ids)
        self.assertIn("python:file:app/user_service.py", unit_ids)
        self.assertIn("python:type:app.user_service/UserService", unit_ids)
        self.assertIn("python:member:app.user_service/UserService/get_user(user_id)", unit_ids)

        # Verify tests
        test_ids = [t.identity for t in tests]
        self.assertEqual(len(tests), 2)
        self.assertIn("tests.test_user_service::TestUserService::test_get_user", test_ids)
        self.assertIn("tests.test_user_service::TestUserService::test_delete_user", test_ids)

        # Verify ordinal sorting (Protocol 1.0 requirement)
        self.assertEqual(unit_ids, sorted(unit_ids))
        edge_keys = [f"{e.source_identity}\x1f{e.target_identity}\x1f{e.kind}" for e in edges]
        self.assertEqual(edge_keys, sorted(edge_keys))
        self.assertEqual(test_ids, sorted(test_ids))


if __name__ == "__main__":
    unittest.main()
