package merkle.adapter.detection;

import merkle.adapter.protocol.ProtocolRecords.DetectedLanguage;
import merkle.adapter.protocol.ProtocolRecords.DetectionEvidence;
import merkle.adapter.protocol.ProtocolRecords.SnapshotFile;

import java.util.ArrayList;
import java.util.List;

public final class JavaLanguageDetector {

    public static DetectedLanguage detect(List<SnapshotFile> files) {
        List<DetectionEvidence> evidence = new ArrayList<>();
        int javaSourceCount = 0;

        for (SnapshotFile file : files) {
            String path = file.path().replace('\\', '/');
            if (path.endsWith("pom.xml")) {
                evidence.add(new DetectionEvidence("manifest", path, null));
            } else if (path.endsWith("build.gradle") || path.endsWith("build.gradle.kts")) {
                evidence.add(new DetectionEvidence("manifest", path, null));
            } else if (path.endsWith(".java")) {
                javaSourceCount++;
            }
        }

        if (javaSourceCount > 0) {
            evidence.add(new DetectionEvidence("source", null, javaSourceCount));
        }

        if (evidence.isEmpty()) {
            return new DetectedLanguage("java", "none", List.of());
        }

        String confidence = (!evidence.isEmpty() && javaSourceCount > 0) ? "high" : "medium";
        return new DetectedLanguage("java", confidence, evidence);
    }
}
