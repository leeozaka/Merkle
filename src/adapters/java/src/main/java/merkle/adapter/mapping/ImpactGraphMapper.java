package merkle.adapter.mapping;

import merkle.adapter.protocol.ProtocolRecords.*;

import java.util.*;

public final class ImpactGraphMapper {

    public MappingResult map(AdapterMapRequest request) {
        AdapterIndex index = request.index();
        List<ChangedUnit> changedUnits = request.changedUnits() != null ? request.changedUnits() : List.of();

        Map<String, TestDescriptor> testsByIdentity = new HashMap<>();
        if (index.tests() != null) {
            for (TestDescriptor test : index.tests()) {
                testsByIdentity.put(test.identity(), test);
            }
        }

        // Build forward and reverse graphs
        Map<String, List<ImpactEdge>> reverseEdges = new HashMap<>();
        Map<String, List<ImpactEdge>> forwardEdges = new HashMap<>();

        if (index.edges() != null) {
            for (ImpactEdge edge : index.edges()) {
                forwardEdges.computeIfAbsent(edge.sourceIdentity(), k -> new ArrayList<>()).add(edge);
                reverseEdges.computeIfAbsent(edge.targetIdentity(), k -> new ArrayList<>()).add(edge);
            }
        }

        Map<String, RequestedTest> selectedTests = new HashMap<>();
        List<ChangedUnit> unmappedUnits = new ArrayList<>();
        List<String> warnings = new ArrayList<>();

        for (ChangedUnit changedUnit : changedUnits) {
            String changedId = changedUnit.identity();
            boolean foundAnyTest = false;

            // 1. Direct test change check
            for (TestDescriptor test : testsByIdentity.values()) {
                if (changedId.contains(test.identity()) || changedId.endsWith(test.identity())) {
                    foundAnyTest = true;
                    ImpactReason reason = new ImpactReason(
                            "staticDependency",
                            changedId,
                            List.of(changedId, test.identity())
                    );
                    selectedTests.merge(test.identity(),
                            new RequestedTest(test.identity(), test.displayName(), test.framework(), List.of(reason), true),
                            (existing, replacement) -> mergeRequestedTests(existing, reason));
                }
            }

            // 2. BFS graph traversal to find affected tests
            Queue<PathNode> queue = new ArrayDeque<>();
            Set<String> visited = new HashSet<>();

            queue.add(new PathNode(changedId, List.of(changedId)));
            visited.add(changedId);

            while (!queue.isEmpty()) {
                PathNode current = queue.poll();

                // Check if current node connects to any test
                for (ImpactEdge edge : forwardEdges.getOrDefault(current.nodeId, List.of())) {
                    if (edge.targetIdentity().startsWith("test:")) {
                        String testIdentity = edge.targetIdentity().substring(5);
                        TestDescriptor test = testsByIdentity.get(testIdentity);
                        if (test != null) {
                            foundAnyTest = true;
                            List<String> fullPath = new ArrayList<>(current.path);
                            fullPath.add(test.identity());
                            ImpactReason reason = new ImpactReason(
                                    edge.kind() != null ? edge.kind() : "staticDependency",
                                    changedId,
                                    fullPath
                            );
                            selectedTests.merge(test.identity(),
                                    new RequestedTest(test.identity(), test.displayName(), test.framework(), List.of(reason), true),
                                    (existing, replacement) -> mergeRequestedTests(existing, reason));
                        }
                    }
                }

                // Check exact reverse edges (who calls or contains the current node)
                for (ImpactEdge revEdge : reverseEdges.getOrDefault(current.nodeId, List.of())) {
                    String callerId = revEdge.sourceIdentity();
                    if (visited.add(callerId)) {
                        List<String> nextPath = new ArrayList<>(current.path);
                        nextPath.add(callerId);
                        queue.add(new PathNode(callerId, nextPath));
                    }
                }

                // Also check prefix / sub-signature matches in reverse edges
                String prefix = current.nodeId.contains("(") ? current.nodeId.substring(0, current.nodeId.indexOf('(')) : current.nodeId;
                for (Map.Entry<String, List<ImpactEdge>> entry : reverseEdges.entrySet()) {
                    String targetKey = entry.getKey();
                    if (targetKey.startsWith(prefix) || prefix.startsWith(targetKey)) {
                        for (ImpactEdge edge : entry.getValue()) {
                            String callerId = edge.sourceIdentity();
                            if (visited.add(callerId)) {
                                List<String> nextPath = new ArrayList<>(current.path);
                                nextPath.add(callerId);
                                queue.add(new PathNode(callerId, nextPath));
                            }
                        }
                    }
                }
            }

            if (!foundAnyTest) {
                unmappedUnits.add(new ChangedUnit(changedId, changedUnit.kind(), changedUnit.changeKind(), false));
            }
        }

        List<RequestedTest> resultTests = new ArrayList<>(selectedTests.values());
        resultTests.sort(Comparator.comparing(RequestedTest::identity));
        unmappedUnits.sort(Comparator.comparing(ChangedUnit::identity));

        return new MappingResult(resultTests, unmappedUnits, warnings);
    }

    private static RequestedTest mergeRequestedTests(RequestedTest existing, ImpactReason newReason) {
        List<ImpactReason> reasons = new ArrayList<>(existing.reasons());
        reasons.add(newReason);
        return new RequestedTest(existing.identity(), existing.displayName(), existing.framework(), reasons, existing.mandatory());
    }

    private record PathNode(String nodeId, List<String> path) {}
}
