package merkle.adapter;

import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.PropertyNamingStrategies;
import merkle.adapter.protocol.ProtocolRecords.*;
import org.junit.jupiter.api.Test;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;

class MainIntegrationTest {

    private final ObjectMapper mapper = new ObjectMapper()
            .setPropertyNamingStrategy(PropertyNamingStrategies.LOWER_CAMEL_CASE)
            .configure(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES, false);

    @Test
    void shouldHandleDescribeOperation() throws Exception {
        String requestJson = """
                {
                  "protocolVersion": "1.0",
                  "requestId": "req-describe-1",
                  "operation": "describe",
                  "payload": {}
                }
                """;

        ByteArrayOutputStream out = new ByteArrayOutputStream();
        int exitCode = Main.run(new ByteArrayInputStream(requestJson.getBytes(StandardCharsets.UTF_8)), out, mapper);

        assertThat(exitCode).isEqualTo(0);

        AdapterProcessResponse response = mapper.readValue(out.toByteArray(), AdapterProcessResponse.class);
        assertThat(response.success()).isTrue();
        assertThat(response.protocolVersion()).isEqualTo("1.0");
        assertThat(response.requestId()).isEqualTo("req-describe-1");

        AdapterDescriptor descriptor = mapper.convertValue(response.payload(), AdapterDescriptor.class);
        assertThat(descriptor.language()).isEqualTo("java");
        assertThat(descriptor.capabilities()).contains("detect", "index", "map");
    }

    @Test
    void shouldHandleIndexAndMapEndToEnd() throws Exception {
        String serviceCode = """
                package com.atletics.yggdrasil.service;
                public class UserService {
                    public String getUser(String id) {
                        return "user:" + id;
                    }
                }
                """;

        String testCode = """
                package com.atletics.yggdrasil.service;
                import org.junit.jupiter.api.Test;
                public class UserServiceTest {
                    private UserService service = new UserService();
                    @Test
                    void testGetUser() {
                        service.getUser("1");
                    }
                }
                """;

        SnapshotFile f1 = new SnapshotFile("src/main/java/com/atletics/yggdrasil/service/UserService.java", "h1", serviceCode.getBytes(StandardCharsets.UTF_8), "regularFile", "100644");
        SnapshotFile f2 = new SnapshotFile("src/test/java/com/atletics/yggdrasil/service/UserServiceTest.java", "h2", testCode.getBytes(StandardCharsets.UTF_8), "regularFile", "100644");

        RepositorySnapshot snapshot = new RepositorySnapshot(new SnapshotIdentity("s1", "main", "git"), "/repo", "repo:1", List.of(f1, f2));
        AdapterIndexRequest indexReq = new AdapterIndexRequest(snapshot, null);

        AdapterProcessRequest indexProcessReq = new AdapterProcessRequest("1.0", "req-index", "index", mapper.valueToTree(indexReq));
        ByteArrayOutputStream indexOut = new ByteArrayOutputStream();

        int indexExit = Main.run(new ByteArrayInputStream(mapper.writeValueAsBytes(indexProcessReq)), indexOut, mapper);
        assertThat(indexExit).isEqualTo(0);

        AdapterProcessResponse indexResp = mapper.readValue(indexOut.toByteArray(), AdapterProcessResponse.class);
        AdapterIndex index = mapper.convertValue(indexResp.payload(), AdapterIndex.class);

        assertThat(index.tests()).hasSize(1);
        assertThat(index.tests().get(0).identity()).isEqualTo("com.atletics.yggdrasil.service.UserServiceTest#testGetUser");

        // Now Map with a changed method unit
        ChangedUnit changedMethod = new ChangedUnit("java:member:com/atletics/yggdrasil/service/UserService/getUser(String)", "member", "modified", false);
        AdapterMapRequest mapReq = new AdapterMapRequest(snapshot, index, List.of(changedMethod));

        AdapterProcessRequest mapProcessReq = new AdapterProcessRequest("1.0", "req-map", "map", mapper.valueToTree(mapReq));
        ByteArrayOutputStream mapOut = new ByteArrayOutputStream();

        int mapExit = Main.run(new ByteArrayInputStream(mapper.writeValueAsBytes(mapProcessReq)), mapOut, mapper);
        assertThat(mapExit).isEqualTo(0);

        AdapterProcessResponse mapResp = mapper.readValue(mapOut.toByteArray(), AdapterProcessResponse.class);
        MappingResult mappingResult = mapper.convertValue(mapResp.payload(), MappingResult.class);

        assertThat(mappingResult.requestedTests()).hasSize(1);
        assertThat(mappingResult.requestedTests().get(0).identity()).isEqualTo("com.atletics.yggdrasil.service.UserServiceTest#testGetUser");
    }
}
