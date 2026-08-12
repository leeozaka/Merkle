"""Impact graph traversal for Python projects."""

from collections import deque
from typing import Any, Dict, List, Set

from merkle_adapter.protocol import ChangedUnit, ImpactReason, MappingResult, RequestedTest


def map_impact(
    snapshot: Dict[str, Any],
    index: Dict[str, Any],
    changed_units: List[Dict[str, Any]],
) -> MappingResult:
    tests_by_id = {t["identity"]: t for t in index.get("tests", [])}

    forward_edges: Dict[str, List[Dict[str, str]]] = {}
    reverse_edges: Dict[str, List[Dict[str, str]]] = {}

    for edge in index.get("edges", []):
        src = edge["sourceIdentity"]
        tgt = edge["targetIdentity"]
        forward_edges.setdefault(src, []).append(edge)
        reverse_edges.setdefault(tgt, []).append(edge)

    selected_tests: Dict[str, RequestedTest] = {}
    unmapped_units: List[ChangedUnit] = []
    warnings: List[str] = []

    for item in changed_units:
        changed_id = item.get("identity", "")
        kind = item.get("kind", "member")
        change_kind = item.get("changeKind", "modified")

        found_test = False

        # 1. Direct test change match
        for test_id, test in tests_by_id.items():
            if test_id in changed_id or changed_id.endswith(test_id):
                found_test = True
                reason = ImpactReason(kind="staticDependency", changed_unit=changed_id, path=[changed_id, test_id])
                if test_id in selected_tests:
                    selected_tests[test_id].reasons.append(reason)
                else:
                    selected_tests[test_id] = RequestedTest(
                        identity=test_id,
                        display_name=test.get("displayName", test_id),
                        framework=test.get("framework", "pytest"),
                        reasons=[reason],
                        mandatory=True,
                    )

        # 2. BFS graph traversal
        queue = deque([(changed_id, [changed_id])])
        visited: Set[str] = {changed_id}

        while queue:
            current_id, current_path = queue.popleft()

            # Check if current node is connected to a test directly
            for edge in forward_edges.get(current_id, []):
                target = edge["targetIdentity"]
                if target.startswith("test:"):
                    test_id = target[5:]
                    if test_id in tests_by_id:
                        found_test = True
                        test = tests_by_id[test_id]
                        full_path = current_path + [test_id]
                        reason = ImpactReason(
                            kind=edge.get("kind", "staticDependency"),
                            changed_unit=changed_id,
                            path=full_path,
                        )
                        if test_id in selected_tests:
                            selected_tests[test_id].reasons.append(reason)
                        else:
                            selected_tests[test_id] = RequestedTest(
                                identity=test_id,
                                display_name=test.get("displayName", test_id),
                                framework=test.get("framework", "pytest"),
                                reasons=[reason],
                                mandatory=True,
                            )

            # Follow reverse dependencies (who calls or contains current node)
            for rev_edge in reverse_edges.get(current_id, []):
                caller = rev_edge["sourceIdentity"]
                if caller not in visited:
                    visited.add(caller)
                    queue.append((caller, current_path + [caller]))

            # Prefix and suffix matching across module paths
            prefix = current_id.split("(")[0] if "(" in current_id else current_id
            for target_key, edges_list in reverse_edges.items():
                if target_key == current_id:
                    continue
                # Match if target_key is suffix of prefix or prefix is suffix of target_key
                target_suffix = target_key.split("/")[-1]
                prefix_suffix = prefix.split("/")[-1]
                if (
                    target_key.startswith(prefix)
                    or prefix.startswith(target_key)
                    or (len(target_suffix) > 3 and target_suffix == prefix_suffix)
                ):
                    for edge in edges_list:
                        caller = edge["sourceIdentity"]
                        if caller not in visited:
                            visited.add(caller)
                            queue.append((caller, current_path + [caller]))

        if not found_test:
            unmapped_units.append(ChangedUnit(identity=changed_id, kind=kind, change_kind=change_kind, mapped=False))

    result_tests = list(selected_tests.values())
    result_tests.sort(key=lambda t: t.identity)
    unmapped_units.sort(key=lambda u: u.identity)

    return MappingResult(requested_tests=result_tests, unmapped_units=unmapped_units, warnings=warnings)
