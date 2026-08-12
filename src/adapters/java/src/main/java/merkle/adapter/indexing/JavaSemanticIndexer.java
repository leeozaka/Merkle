package merkle.adapter.indexing;

import com.github.javaparser.JavaParser;
import com.github.javaparser.ParserConfiguration;
import com.github.javaparser.ast.CompilationUnit;
import com.github.javaparser.ast.body.*;
import com.github.javaparser.ast.expr.AnnotationExpr;
import com.github.javaparser.ast.expr.MethodCallExpr;
import com.github.javaparser.ast.expr.VariableDeclarationExpr;
import com.github.javaparser.ast.type.ClassOrInterfaceType;
import merkle.adapter.protocol.ProtocolRecords.*;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.*;

public final class JavaSemanticIndexer {

    private final JavaParser parser;

    public JavaSemanticIndexer() {
        ParserConfiguration config = new ParserConfiguration();
        config.setLanguageLevel(ParserConfiguration.LanguageLevel.JAVA_21);
        this.parser = new JavaParser(config);
    }

    public AdapterIndex index(RepositorySnapshot snapshot) {
        List<SourceUnit> units = new ArrayList<>();
        List<ImpactEdge> edges = new ArrayList<>();
        List<TestDescriptor> tests = new ArrayList<>();
        List<String> warnings = new ArrayList<>();

        List<SnapshotFile> files = snapshot.files() != null ? snapshot.files() : List.of();

        // 1. Index build files (pom.xml, build.gradle) as project units
        for (SnapshotFile file : files) {
            String path = normalizePath(file.path());
            if (path.endsWith("pom.xml") || path.endsWith("build.gradle") || path.endsWith("build.gradle.kts")) {
                String identity = "java:project:" + path;
                String hash = file.contentHash() != null ? file.contentHash() : computeSha256(file.content() != null ? file.content() : new byte[0]);
                units.add(new SourceUnit(identity, "project", path, hash, hash));
            }
        }

        // 2. Index all .java files
        for (SnapshotFile file : files) {
            String path = normalizePath(file.path());
            if (!path.endsWith(".java")) {
                continue;
            }

            byte[] content = file.content();
            if (content == null || content.length == 0) {
                continue;
            }

            String sourceCode = new String(content, StandardCharsets.UTF_8);
            String fileHash = file.contentHash() != null ? file.contentHash() : computeSha256(content);
            String fileUnitId = "java:file:" + path;

            units.add(new SourceUnit(fileUnitId, "file", path, fileHash, fileHash));

            try {
                var parseResult = parser.parse(sourceCode);
                if (!parseResult.isSuccessful() || parseResult.getResult().isEmpty()) {
                    warnings.add("Parse warning in file: " + path);
                    continue;
                }

                CompilationUnit cu = parseResult.getResult().get();
                String packageName = cu.getPackageDeclaration().map(pd -> pd.getName().asString()).orElse("");

                // Track imports for call resolution
                Map<String, String> simpleToFqcn = new HashMap<>();
                for (var importDecl : cu.getImports()) {
                    String importName = importDecl.getName().asString();
                    String simpleName = importName.substring(importName.lastIndexOf('.') + 1);
                    simpleToFqcn.put(simpleName, importName);
                }

                for (TypeDeclaration<?> type : cu.getTypes()) {
                    indexType(type, packageName, path, fileUnitId, simpleToFqcn, units, edges, tests);
                }

            } catch (Exception ex) {
                warnings.add("Error indexing " + path + ": " + ex.getMessage());
            }
        }

        // Deterministic sorting required by Protocol 1.0
        units.sort(Comparator.comparing(SourceUnit::identity));
        edges.sort(Comparator.comparing((ImpactEdge e) -> e.sourceIdentity() + "\u001f" + e.targetIdentity() + "\u001f" + e.kind()));
        tests.sort(Comparator.comparing(TestDescriptor::identity));

        // Deduplicate edges
        List<ImpactEdge> uniqueEdges = new ArrayList<>();
        ImpactEdge lastEdge = null;
        for (ImpactEdge edge : edges) {
            if (lastEdge == null || !edge.equals(lastEdge)) {
                uniqueEdges.add(edge);
                lastEdge = edge;
            }
        }

        return new AdapterIndex(units, uniqueEdges, tests, warnings);
    }

    private void indexType(
            TypeDeclaration<?> type,
            String packageName,
            String filePath,
            String fileUnitId,
            Map<String, String> imports,
            List<SourceUnit> units,
            List<ImpactEdge> edges,
            List<TestDescriptor> tests) {

        String typeName = type.getNameAsString();
        String fqcn = packageName.isEmpty() ? typeName : packageName + "." + typeName;
        String typeUnitId = "java:type:" + (packageName.isEmpty() ? "" : packageName.replace('.', '/') + "/") + typeName;

        String typeHash = computeSha256(type.toString().getBytes(StandardCharsets.UTF_8));
        units.add(new SourceUnit(typeUnitId, "type", filePath, typeHash, typeHash));

        // Containment: File -> Type
        edges.add(new ImpactEdge(fileUnitId, typeUnitId, "containment"));

        // Collect fields for variable-type resolution
        Map<String, String> varTypes = new HashMap<>();
        for (FieldDeclaration field : type.getFields()) {
            String fieldType = field.getElementType().asString();
            for (VariableDeclarator var : field.getVariables()) {
                varTypes.put(var.getNameAsString(), fieldType);
            }
            // Add static dependency from this type to field type
            String resolvedFieldTypeId = resolveTypeId(fieldType, packageName, imports);
            edges.add(new ImpactEdge(typeUnitId, resolvedFieldTypeId, "staticDependency"));
        }

        // Superclass and interface dependencies
        if (type instanceof ClassOrInterfaceDeclaration classDecl) {
            for (ClassOrInterfaceType extended : classDecl.getExtendedTypes()) {
                String targetId = resolveTypeId(extended.getNameAsString(), packageName, imports);
                edges.add(new ImpactEdge(typeUnitId, targetId, "staticDependency"));
            }
            for (ClassOrInterfaceType implemented : classDecl.getImplementedTypes()) {
                String targetId = resolveTypeId(implemented.getNameAsString(), packageName, imports);
                edges.add(new ImpactEdge(typeUnitId, targetId, "staticDependency"));
            }
        }

        // Check if this type is a Test class
        boolean isTestClass = isTestClass(type);

        // Index methods and constructors
        for (BodyDeclaration<?> member : type.getMembers()) {
            if (member instanceof MethodDeclaration method) {
                indexMethod(method, typeName, packageName, fqcn, filePath, typeUnitId, isTestClass, imports, varTypes, units, edges, tests);
            } else if (member instanceof ConstructorDeclaration constructor) {
                indexConstructor(constructor, typeName, packageName, filePath, typeUnitId, imports, units, edges);
            } else if (member instanceof TypeDeclaration<?> nestedType) {
                indexType(nestedType, packageName + "." + typeName, filePath, typeUnitId, imports, units, edges, tests);
            }
        }
    }

    private void indexMethod(
            MethodDeclaration method,
            String typeName,
            String packageName,
            String fqcn,
            String filePath,
            String typeUnitId,
            boolean isTestClass,
            Map<String, String> imports,
            Map<String, String> fieldVarTypes,
            List<SourceUnit> units,
            List<ImpactEdge> edges,
            List<TestDescriptor> tests) {

        String methodName = method.getNameAsString();
        StringBuilder sigBuilder = new StringBuilder(methodName).append("(");
        for (int i = 0; i < method.getParameters().size(); i++) {
            if (i > 0) sigBuilder.append(",");
            sigBuilder.append(method.getParameter(i).getType().asString());
        }
        sigBuilder.append(")");
        String signature = sigBuilder.toString();

        String packagePath = packageName.isEmpty() ? "" : packageName.replace('.', '/') + "/";
        String methodUnitId = "java:member:" + packagePath + typeName + "/" + signature;

        // Method semantic hash
        String methodSignature = method.getDeclarationAsString(true, true, true);
        String bodyString = method.getBody().map(Object::toString).orElse("");
        String methodHash = computeSha256((methodSignature + "\n" + bodyString).getBytes(StandardCharsets.UTF_8));

        units.add(new SourceUnit(methodUnitId, "member", filePath, methodHash, methodHash));

        // Containment: Type -> Member
        edges.add(new ImpactEdge(typeUnitId, methodUnitId, "containment"));

        // Check if method is a test
        boolean isTestMethod = isTestClass || isTestMethod(method);
        if (isTestMethod && (isTestAnnotationPresent(method) || methodName.startsWith("test"))) {
            String testIdentity = fqcn + "#" + methodName;
            String framework = detectFramework(method);
            tests.add(new TestDescriptor(testIdentity, fqcn + "." + methodName, framework));

            // Link Test Identity to Method Unit and Type Unit
            edges.add(new ImpactEdge(methodUnitId, "test:" + testIdentity, "containment"));
            edges.add(new ImpactEdge(typeUnitId, "test:" + testIdentity, "containment"));
        }

        // Local variable map for method body
        Map<String, String> localVars = new HashMap<>(fieldVarTypes);
        if (method.getBody().isPresent()) {
            List<VariableDeclarationExpr> localDecls = method.getBody().get().findAll(VariableDeclarationExpr.class);
            for (VariableDeclarationExpr decl : localDecls) {
                String typeStr = decl.getElementType().asString();
                for (VariableDeclarator var : decl.getVariables()) {
                    localVars.put(var.getNameAsString(), typeStr);
                }
            }

            List<MethodCallExpr> calls = method.getBody().get().findAll(MethodCallExpr.class);
            for (MethodCallExpr call : calls) {
                String calledMethodName = call.getNameAsString();
                if (call.getScope().isPresent()) {
                    String scope = call.getScope().get().toString();
                    String actualType = localVars.getOrDefault(scope, scope);
                    String resolvedTargetType = resolveTypeId(actualType, packageName, imports);
                    edges.add(new ImpactEdge(methodUnitId, resolvedTargetType, "staticDependency"));
                    // Also connect to method if possible
                    String targetMethodUnit = "java:member:" + resolvedTargetType.substring("java:type:".length()) + "/" + calledMethodName;
                    edges.add(new ImpactEdge(methodUnitId, targetMethodUnit, "staticDependency"));
                }
            }
        }
    }

    private void indexConstructor(
            ConstructorDeclaration constructor,
            String typeName,
            String packageName,
            String filePath,
            String typeUnitId,
            Map<String, String> imports,
            List<SourceUnit> units,
            List<ImpactEdge> edges) {

        StringBuilder sigBuilder = new StringBuilder(typeName).append("(");
        for (int i = 0; i < constructor.getParameters().size(); i++) {
            if (i > 0) sigBuilder.append(",");
            sigBuilder.append(constructor.getParameter(i).getType().asString());
        }
        sigBuilder.append(")");
        String signature = sigBuilder.toString();

        String packagePath = packageName.isEmpty() ? "" : packageName.replace('.', '/') + "/";
        String constructorUnitId = "java:member:" + packagePath + typeName + "/" + signature;

        String constructorHash = computeSha256(constructor.toString().getBytes(StandardCharsets.UTF_8));
        units.add(new SourceUnit(constructorUnitId, "member", filePath, constructorHash, constructorHash));

        // Containment: Type -> Constructor
        edges.add(new ImpactEdge(typeUnitId, constructorUnitId, "containment"));
    }

    private boolean isTestClass(TypeDeclaration<?> type) {
        String name = type.getNameAsString();
        if (name.endsWith("Test") || name.endsWith("Tests") || name.endsWith("TestCase") || name.startsWith("Test")) {
            return true;
        }
        for (AnnotationExpr annotation : type.getAnnotations()) {
            String aName = annotation.getNameAsString();
            if (aName.contains("SpringBootTest") || aName.contains("WebMvcTest") ||
                aName.contains("DataJpaTest") || aName.contains("ExtendWith")) {
                return true;
            }
        }
        return false;
    }

    private boolean isTestMethod(MethodDeclaration method) {
        return isTestAnnotationPresent(method);
    }

    private boolean isTestAnnotationPresent(MethodDeclaration method) {
        for (AnnotationExpr annotation : method.getAnnotations()) {
            String name = annotation.getNameAsString();
            if (name.equals("Test") || name.equals("ParameterizedTest") ||
                name.equals("RepeatedTest") || name.equals("TestFactory") ||
                name.endsWith(".Test")) {
                return true;
            }
        }
        return false;
    }

    private String detectFramework(MethodDeclaration method) {
        for (AnnotationExpr annotation : method.getAnnotations()) {
            String name = annotation.getNameAsString();
            if (name.equals("ParameterizedTest") || name.equals("RepeatedTest") || name.equals("TestFactory")) {
                return "junit5";
            }
        }
        return "junit5";
    }

    private String resolveTypeId(String typeName, String currentPackage, Map<String, String> imports) {
        if (imports.containsKey(typeName)) {
            String fqcn = imports.get(typeName);
            return "java:type:" + fqcn.replace('.', '/');
        }
        return "java:type:" + (currentPackage.isEmpty() ? "" : currentPackage.replace('.', '/') + "/") + typeName;
    }

    private static String normalizePath(String path) {
        return path.replace('\\', '/');
    }

    private static String computeSha256(byte[] data) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            byte[] hash = digest.digest(data);
            StringBuilder hexString = new StringBuilder();
            for (byte b : hash) {
                String hex = Integer.toHexString(0xff & b);
                if (hex.length() == 1) hexString.append('0');
                hexString.append(hex);
            }
            return hexString.toString();
        } catch (NoSuchAlgorithmException e) {
            throw new RuntimeException(e);
        }
    }
}
