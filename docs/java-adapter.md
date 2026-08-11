# Merkle Java & Spring Boot Language Adapter

Status: Protocol 1.0 Community Adapter  
Target Ecosystem: Java 17+, Maven, Gradle, Spring Boot, JUnit 5 / JUnit 4 / TestNG  

## Overview

The `merkle-adapter-java` provides Test Impact Analysis (TIA) and advisory test selection for Java and Spring Boot applications. It operates over Merkle's **Protocol 1.0** process interface via standard JSON-lines on standard input/output.

## Capabilities

| Capability | Supported | Description |
|---|:---:|---|
| `detect` | Yes | Detects `pom.xml`, `build.gradle`, `build.gradle.kts`, and `.java` source trees. |
| `index` | Yes | Deterministic AST parsing using JavaParser to extract packages, classes, methods, Spring annotations (`@Service`, `@RestController`), and test declarations. |
| `map` | Yes | Reverse dependency BFS traversal from changed method/class hashes to candidate tests. |

## Identity Schema

- **Source Units:** `java:member:<package-path>/<ClassName>/<methodSignature>` (e.g. `java:member:com/atletics/yggdrasil/service/UserService/getUser(String)`)
- **Type Units:** `java:type:<package-path>/<ClassName>` (e.g. `java:type:com/atletics/yggdrasil/service/UserService`)
- **Test Identities:** `<package>.<TestClassName>#<testMethodName>` (e.g. `com.atletics.yggdrasil.service.UserServiceTest#testGetUser`)

## Configuration

In any Java / Spring Boot repository (such as `yggdrasil`), add a `.merkle.yml`:

```yaml
schemaVersion: 1

repository:
  stateDirectory: .merkle

languages:
  java:
    profile: minimal

baseline:
  localRef: main
  prStrategy: merge-base

policy:
  minSavingsPercent: 30
  unmapped: warn
```

## Building the Java Adapter

```bash
cd src/adapters/java
mvn clean package
```

The resulting executable is `src/adapters/java/target/merkle-adapter-java.jar`.
