"""AST-based semantic indexer for Python projects."""

import ast
import hashlib
from typing import Any, Dict, List, Optional, Set, Tuple

from merkle_adapter.protocol import AdapterIndex, ImpactEdge, SourceUnit, TestDescriptor


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _normalize_path(path: str) -> str:
    return path.replace("\\", "/")


def _module_name(path: str) -> str:
    norm = _normalize_path(path)
    if norm.endswith(".py"):
        norm = norm[:-3]
    return norm.replace("/", ".")


def index_python(snapshot: Dict[str, Any]) -> AdapterIndex:
    files: List[Dict[str, Any]] = snapshot.get("files", []) or []

    units: List[SourceUnit] = []
    edges: List[ImpactEdge] = []
    tests: List[TestDescriptor] = []
    warnings: List[str] = []

    # 1. Project-level units
    for file in files:
        path = _normalize_path(file.get("path", ""))
        filename = path.split("/")[-1]
        if filename in {"pyproject.toml", "setup.py", "requirements.txt", "Pipfile", "poetry.lock"}:
            content = file.get("content", []) or []
            if isinstance(content, list):
                content_bytes = bytes(content)
            elif isinstance(content, str):
                content_bytes = content.encode("utf-8")
            else:
                content_bytes = b""
            h = file.get("contentHash") or _sha256(content_bytes)
            identity = f"python:project:{path}"
            units.append(SourceUnit(identity=identity, kind="project", path=path, content_hash=h, semantic_signature=h))

    # 2. Python source files
    for file in files:
        path = _normalize_path(file.get("path", ""))
        if not path.endswith(".py"):
            continue

        raw_content = file.get("content", []) or []
        if isinstance(raw_content, list):
            content_bytes = bytes(raw_content)
        elif isinstance(raw_content, str):
            content_bytes = raw_content.encode("utf-8")
        else:
            content_bytes = b""

        if not content_bytes:
            continue

        file_hash = file.get("contentHash") or _sha256(content_bytes)
        file_unit_id = f"python:file:{path}"
        units.append(SourceUnit(identity=file_unit_id, kind="file", path=path, content_hash=file_hash, semantic_signature=file_hash))

        module = _module_name(path)
        is_test_file = "test" in path.lower() or path.startswith("tests/")

        try:
            tree = ast.parse(content_bytes.decode("utf-8", errors="replace"), filename=path)
        except SyntaxError as ex:
            warnings.append(f"Syntax error parsing {path}: {ex}")
            continue
        except Exception as ex:
            warnings.append(f"Error parsing {path}: {ex}")
            continue

        # Extract imported names for FQCN resolution
        imports: Dict[str, str] = {}
        for node in getattr(tree, "body", []):
            if isinstance(node, ast.Import):
                for alias in node.names:
                    name = alias.asname or alias.name
                    imports[name] = alias.name
            elif isinstance(node, ast.ImportFrom) and node.module:
                for alias in node.names:
                    name = alias.asname or alias.name
                    imports[name] = f"{node.module}.{alias.name}"

        _index_ast(tree, path, module, file_unit_id, is_test_file, imports, units, edges, tests)

    # Sort deterministically (Ordinal / Code-Point sorting)
    units.sort(key=lambda u: u.identity)
    edges.sort(key=lambda e: f"{e.source_identity}\x1f{e.target_identity}\x1f{e.kind}")
    tests.sort(key=lambda t: t.identity)

    # Deduplicate edges
    unique_edges: List[ImpactEdge] = []
    seen_edges: Set[Tuple[str, str, str]] = set()
    for edge in edges:
        key = (edge.source_identity, edge.target_identity, edge.kind)
        if key not in seen_edges:
            seen_edges.add(key)
            unique_edges.append(edge)

    return AdapterIndex(units=units, edges=unique_edges, tests=tests, warnings=warnings)


def _index_ast(
    tree: ast.AST,
    path: str,
    module: str,
    file_unit_id: str,
    is_test_file: bool,
    imports: Dict[str, str],
    units: List[SourceUnit],
    edges: List[ImpactEdge],
    tests: List[TestDescriptor],
) -> None:
    # Walk top-level module items
    for node in getattr(tree, "body", []):
        if isinstance(node, ast.ClassDef):
            _index_class(node, path, module, file_unit_id, is_test_file, imports, units, edges, tests)
        elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            _index_function(node, path, module, None, file_unit_id, is_test_file, imports, units, edges, tests)


def _index_class(
    cls_node: ast.ClassDef,
    path: str,
    module: str,
    file_unit_id: str,
    is_test_file: bool,
    imports: Dict[str, str],
    units: List[SourceUnit],
    edges: List[ImpactEdge],
    tests: List[TestDescriptor],
) -> None:
    cls_name = cls_node.name
    type_id = f"python:type:{module}/{cls_name}"
    cls_hash = _sha256(ast.dump(cls_node).encode("utf-8"))

    units.append(SourceUnit(identity=type_id, kind="type", path=path, content_hash=cls_hash, semantic_signature=cls_hash))
    edges.append(ImpactEdge(source_identity=file_unit_id, target_identity=type_id, kind="containment"))

    # Base classes
    is_unittest = False
    for base in cls_node.bases:
        if isinstance(base, ast.Name):
            if base.id == "TestCase":
                is_unittest = True
            resolved = imports.get(base.id, f"{module}.{base.id}")
            edges.append(ImpactEdge(source_identity=type_id, target_identity=f"python:type:{resolved}", kind="staticDependency"))
        elif isinstance(base, ast.Attribute):
            if base.attr == "TestCase":
                is_unittest = True
            edges.append(ImpactEdge(source_identity=type_id, target_identity=f"python:type:{base.attr}", kind="staticDependency"))

    is_test_class = is_test_file or cls_name.startswith("Test") or cls_name.endswith("Test") or cls_name.endswith("Tests")

    for item in cls_node.body:
        if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)):
            _index_function(item, path, module, cls_name, type_id, is_test_class, imports, units, edges, tests, is_unittest)


def _index_function(
    fn_node: Any,
    path: str,
    module: str,
    parent_cls_name: Optional[str],
    parent_unit_id: str,
    is_test_context: bool,
    imports: Dict[str, str],
    units: List[SourceUnit],
    edges: List[ImpactEdge],
    tests: List[TestDescriptor],
    is_unittest_class: bool = False,
) -> None:
    fn_name = fn_node.name
    params = [arg.arg for arg in fn_node.args.args if arg.arg not in {"self", "cls"}]
    param_str = ",".join(params)

    if parent_cls_name:
        member_id = f"python:member:{module}/{parent_cls_name}/{fn_name}({param_str})"
    else:
        member_id = f"python:member:{module}/{fn_name}({param_str})"

    fn_hash = _sha256(ast.dump(fn_node).encode("utf-8"))
    units.append(SourceUnit(identity=member_id, kind="member", path=path, content_hash=fn_hash, semantic_signature=fn_hash))
    edges.append(ImpactEdge(source_identity=parent_unit_id, target_identity=member_id, kind="containment"))

    # Test Discovery
    is_test = is_test_context and (fn_name.startswith("test_") or fn_name.endswith("_test") or fn_name == "test")
    if is_test:
        if parent_cls_name:
            test_id = f"{module}::{parent_cls_name}::{fn_name}"
            disp_name = f"{module}.{parent_cls_name}.{fn_name}"
        else:
            test_id = f"{module}::{fn_name}"
            disp_name = f"{module}.{fn_name}"

        framework = "unittest" if is_unittest_class else "pytest"
        tests.append(TestDescriptor(identity=test_id, display_name=disp_name, framework=framework))
        edges.append(ImpactEdge(source_identity=member_id, target_identity=f"test:{test_id}", kind="containment"))
        edges.append(ImpactEdge(source_identity=parent_unit_id, target_identity=f"test:{test_id}", kind="containment"))

    # Scan calls in function body
    for node in ast.walk(fn_node):
        if isinstance(node, ast.Call):
            target_name = _extract_call_name(node)
            if target_name:
                resolved = imports.get(target_name, target_name)
                # Normalize python:type: and python:member: edges
                edges.append(ImpactEdge(source_identity=member_id, target_identity=f"python:type:{resolved.replace('.', '/')}", kind="staticDependency"))
                edges.append(ImpactEdge(source_identity=member_id, target_identity=f"python:member:{resolved.replace('.', '/')}", kind="staticDependency"))


def _extract_call_name(node: ast.Call) -> Optional[str]:
    if isinstance(node.func, ast.Name):
        return node.func.id
    elif isinstance(node.func, ast.Attribute):
        if isinstance(node.func.value, ast.Name):
            return f"{node.func.value.id}.{node.func.attr}"
        return node.func.attr
    return None
