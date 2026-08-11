# Merkle Python Language Adapter

Status: Protocol 1.0 Community Adapter  
Target Ecosystem: Python 3.10+, pytest, unittest, pip / poetry / pipenv / setuptools  

## Overview

The `merkle-adapter-python` provides Test Impact Analysis (TIA) and advisory test selection for Python applications. It implements Merkle's **Protocol 1.0** process interface over standard JSON-lines on standard input/output.

## Capabilities

| Capability | Supported | Description |
|---|:---:|---|
| `detect` | Yes | Detects `pyproject.toml`, `setup.py`, `setup.cfg`, `requirements.txt`, `Pipfile`, `poetry.lock`, and `.py` source trees. |
| `index` | Yes | Deterministic AST parsing using Python standard library `ast` to extract modules, classes, functions, calls, and test declarations (`pytest`, `unittest`). |
| `map` | Yes | Reverse dependency BFS traversal from changed method/function AST hashes to affected test suites. |

## Identity Schema

- **Source Units:** `python:member:<module>/<ClassName>/<functionName>(<params>)` or `python:member:<module>/<functionName>(<params>)` (e.g. `python:member:app.services.user/UserService/get_user(user_id)`)
- **Type Units:** `python:type:<module>/<ClassName>` (e.g. `python:type:app.services.user/UserService`)
- **Test Identities:** `<module>::<TestClass>::<testMethod>` or `<module>::<testFunction>` (e.g. `tests.test_user::TestUserService::test_get_user`)

## Configuration

In any Python repository, add a `.merkle.yml`:

```yaml
schemaVersion: 1

repository:
  stateDirectory: .merkle

languages:
  python:
    profile: minimal

baseline:
  localRef: main
  prStrategy: merge-base

policy:
  minSavingsPercent: 30
  unmapped: warn
```

## Running the Python Adapter

The adapter is self-contained with zero external dependencies and runs on Python 3.10+:

```bash
# Direct execution
python3 src/adapters/python/merkle-adapter-python.pyz

# Or via Python module
python3 -m merkle_adapter
```
