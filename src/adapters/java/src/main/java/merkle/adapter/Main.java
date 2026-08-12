package merkle.adapter;

import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.PropertyNamingStrategies;
import merkle.adapter.detection.JavaLanguageDetector;
import merkle.adapter.indexing.JavaSemanticIndexer;
import merkle.adapter.mapping.ImpactGraphMapper;
import merkle.adapter.protocol.ProtocolRecords.*;

import java.io.InputStream;
import java.io.OutputStream;
import java.util.List;

public final class Main {

    private static final String PROTOCOL_VERSION = "1.0";
    private static final String ADAPTER_VERSION = "1.0.0";
    private static final String UNIT_IDENTITY_VERSION = "1";
    private static final String TEST_IDENTITY_VERSION = "1";

    public static void main(String[] args) {
        ObjectMapper mapper = new ObjectMapper()
                .setPropertyNamingStrategy(PropertyNamingStrategies.LOWER_CAMEL_CASE)
                .configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false);

        int exitCode = run(System.in, System.out, mapper);
        System.exit(exitCode);
    }

    public static int run(InputStream input, OutputStream output, ObjectMapper mapper) {
        String requestId = "unknown";
        String operation = "unknown";

        try {
            byte[] inputBytes = input.readAllBytes();
            if (inputBytes.length == 0) {
                var response = AdapterProcessResponse.failure(requestId, operation, "EmptyRequest", "No JSON input received.");
                mapper.writeValue(output, response);
                return 2;
            }

            AdapterProcessRequest request = mapper.readValue(inputBytes, AdapterProcessRequest.class);
            requestId = request.requestId() != null ? request.requestId() : "req";
            operation = request.operation() != null ? request.operation() : "unknown";

            if (!PROTOCOL_VERSION.equals(request.protocolVersion())) {
                var response = AdapterProcessResponse.failure(requestId, operation, "UnsupportedProtocol", "Expected protocol version 1.0");
                mapper.writeValue(output, response);
                return 2;
            }

            Object resultPayload = switch (operation) {
                case "describe" -> handleDescribe();
                case "detect" -> handleDetect(request, mapper);
                case "index" -> handleIndex(request, mapper);
                case "map" -> handleMap(request, mapper);
                default -> throw new IllegalArgumentException("Unsupported operation: " + operation);
            };

            var response = AdapterProcessResponse.success(requestId, operation, resultPayload);
            mapper.writeValue(output, response);
            return 0;

        } catch (IllegalArgumentException ex) {
            try {
                var response = AdapterProcessResponse.failure(requestId, operation, "UnsupportedOperation", ex.getMessage());
                mapper.writeValue(output, response);
            } catch (Exception ignored) {}
            return 2;
        } catch (Exception ex) {
            try {
                System.err.println("Error processing request: " + ex.getMessage());
                ex.printStackTrace(System.err);
                var response = AdapterProcessResponse.failure(requestId, operation, "AdapterUnhandledError", ex.getMessage());
                mapper.writeValue(output, response);
            } catch (Exception ignored) {}
            return 3;
        }
    }

    private static AdapterDescriptor handleDescribe() {
        return new AdapterDescriptor(
                PROTOCOL_VERSION,
                "java",
                "merkle-community-java",
                ADAPTER_VERSION,
                UNIT_IDENTITY_VERSION,
                TEST_IDENTITY_VERSION,
                List.of("detect", "index", "map"),
                List.of("minimal", "semantic"),
                List.of("java-17", "java-21", "java-25"),
                List.of("linux-x64", "darwin-arm64", "darwin-x64", "windows-x64")
        );
    }

    private static DetectedLanguage handleDetect(AdapterProcessRequest request, ObjectMapper mapper) throws Exception {
        RepositorySnapshot snapshot = mapper.treeToValue(request.payload(), RepositorySnapshot.class);
        return JavaLanguageDetector.detect(snapshot != null && snapshot.files() != null ? snapshot.files() : List.of());
    }

    private static AdapterIndex handleIndex(AdapterProcessRequest request, ObjectMapper mapper) throws Exception {
        AdapterIndexRequest indexRequest = mapper.treeToValue(request.payload(), AdapterIndexRequest.class);
        JavaSemanticIndexer indexer = new JavaSemanticIndexer();
        return indexer.index(indexRequest.snapshot());
    }

    private static MappingResult handleMap(AdapterProcessRequest request, ObjectMapper mapper) throws Exception {
        AdapterMapRequest mapRequest = mapper.treeToValue(request.payload(), AdapterMapRequest.class);
        ImpactGraphMapper graphMapper = new ImpactGraphMapper();
        return graphMapper.map(mapRequest);
    }
}
