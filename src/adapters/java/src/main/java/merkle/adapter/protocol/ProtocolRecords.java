package merkle.adapter.protocol;

import com.fasterxml.jackson.annotation.JsonInclude;
import com.fasterxml.jackson.databind.JsonNode;
import java.util.List;

public final class ProtocolRecords {

    public record AdapterProcessRequest(
            String protocolVersion,
            String requestId,
            String operation,
            JsonNode payload
    ) {}

    @JsonInclude(JsonInclude.Include.NON_NULL)
    public record AdapterProcessResponse(
            String protocolVersion,
            String requestId,
            String operation,
            boolean success,
            Object payload,
            AdapterProcessError error
    ) {
        public static AdapterProcessResponse success(String requestId, String operation, Object payload) {
            return new AdapterProcessResponse("1.0", requestId, operation, true, payload, null);
        }

        public static AdapterProcessResponse failure(String requestId, String operation, String code, String message) {
            return new AdapterProcessResponse("1.0", requestId, operation, false, null, new AdapterProcessError(code, message));
        }
    }

    public record AdapterProcessError(String code, String message) {}

    @JsonInclude(JsonInclude.Include.NON_NULL)
    public record AdapterDescriptor(
            String protocolVersion,
            String language,
            String producer,
            String adapterVersion,
            String unitIdentityVersion,
            String testIdentityVersion,
            List<String> capabilities,
            List<String> profiles,
            List<String> supportedTargets,
            List<String> supportedPlatforms
    ) {}

    public record SnapshotIdentity(String value, String reference, String provider) {}

    public record SnapshotFile(
            String path,
            String contentHash,
            byte[] content,
            String kind,
            String mode
    ) {}

    public record RepositorySnapshot(
            SnapshotIdentity identity,
            String repositoryRoot,
            String repositoryIdentity,
            List<SnapshotFile> files
    ) {}

    public record AdapterIndexRequest(
            RepositorySnapshot snapshot,
            String configuredSolution
    ) {}

    public record SourceUnit(
            String identity,
            String kind,
            String path,
            String contentHash,
            String semanticSignature
    ) {}

    public record ImpactEdge(
            String sourceIdentity,
            String targetIdentity,
            String kind
    ) {}

    public record TestDescriptor(
            String identity,
            String displayName,
            String framework
    ) {}

    public record AdapterIndex(
            List<SourceUnit> units,
            List<ImpactEdge> edges,
            List<TestDescriptor> tests,
            List<String> warnings
    ) {}

    public record ChangedUnit(
            String identity,
            String kind,
            String changeKind,
            boolean mapped
    ) {}

    public record AdapterMapRequest(
            RepositorySnapshot snapshot,
            AdapterIndex index,
            List<ChangedUnit> changedUnits
    ) {}

    public record ImpactReason(
            String kind,
            String changedUnit,
            List<String> path
    ) {}

    public record RequestedTest(
            String identity,
            String displayName,
            String framework,
            List<ImpactReason> reasons,
            boolean mandatory
    ) {}

    public record MappingResult(
            List<RequestedTest> requestedTests,
            List<ChangedUnit> unmappedUnits,
            List<String> warnings
    ) {}

    public record DetectionEvidence(String kind, String path, Integer count) {}

    public record DetectedLanguage(
            String language,
            String confidence,
            List<DetectionEvidence> evidence
    ) {}
}
