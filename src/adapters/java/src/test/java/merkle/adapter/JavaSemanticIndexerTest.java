package merkle.adapter;

import merkle.adapter.indexing.JavaSemanticIndexer;
import merkle.adapter.protocol.ProtocolRecords.*;
import org.junit.jupiter.api.Test;

import java.nio.charset.StandardCharsets;
import java.util.List;

import static org.assertj.core.api.Assertions.assertThat;

class JavaSemanticIndexerTest {

    @Test
    void shouldIndexSpringServiceAndJUnit5Test() {
        String serviceCode = """
                package com.example.demo.service;
                
                import org.springframework.stereotype.Service;
                
                @Service
                public class UserService {
                    public String findUserById(String id) {
                        return "user-" + id;
                    }
                }
                """;

        String testCode = """
                package com.example.demo.service;
                
                import org.junit.jupiter.api.Test;
                import static org.junit.jupiter.api.Assertions.assertEquals;
                
                class UserServiceTest {
                    private UserService userService = new UserService();
                
                    @Test
                    void testFindUserById() {
                        String user = userService.findUserById("123");
                        assertEquals("user-123", user);
                    }
                }
                """;

        SnapshotFile serviceFile = new SnapshotFile(
                "src/main/java/com/example/demo/service/UserService.java",
                "hash-service",
                serviceCode.getBytes(StandardCharsets.UTF_8),
                "regularFile",
                "100644"
        );

        SnapshotFile testFile = new SnapshotFile(
                "src/test/java/com/example/demo/service/UserServiceTest.java",
                "hash-test",
                testCode.getBytes(StandardCharsets.UTF_8),
                "regularFile",
                "100644"
        );

        RepositorySnapshot snapshot = new RepositorySnapshot(
                new SnapshotIdentity("test-id", "refs/heads/main", "git"),
                "/repo",
                "repo:demo",
                List.of(serviceFile, testFile)
        );

        JavaSemanticIndexer indexer = new JavaSemanticIndexer();
        AdapterIndex index = indexer.index(snapshot);

        assertThat(index.units()).isNotEmpty();
        assertThat(index.tests()).hasSize(1);
        assertThat(index.tests().get(0).identity()).isEqualTo("com.example.demo.service.UserServiceTest#testFindUserById");
        assertThat(index.tests().get(0).framework()).isEqualTo("junit5");

        // Verify units contain UserService and method
        assertThat(index.units())
                .extracting(SourceUnit::identity)
                .contains(
                        "java:type:com/example/demo/service/UserService",
                        "java:member:com/example/demo/service/UserService/findUserById(String)"
                );
    }
}
