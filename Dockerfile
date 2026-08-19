# syntax=docker/dockerfile:1.7

FROM golang:1.22.12-bookworm AS go-toolchain
ARG MERKLE_ADAPTERS=all
RUN mkdir -p /selected && \
    case ",${MERKLE_ADAPTERS}," in \
      *,golang,*|*,all,*) cp -a /usr/local/go /selected/go ;; \
    esac

FROM python:3.12-slim-bookworm AS python-toolchain
ARG MERKLE_ADAPTERS=all
RUN mkdir -p /selected && \
    case ",${MERKLE_ADAPTERS}," in \
      *,python,*|*,all,*) cp -a /usr/local /selected/python ;; \
    esac

FROM maven:3.9.11-eclipse-temurin-17 AS java-toolchain
ARG MERKLE_ADAPTERS=all
RUN mkdir -p /selected && \
    case ",${MERKLE_ADAPTERS}," in \
      *,java,*|*,all,*) \
        cp -a /opt/java/openjdk /selected/java && \
        cp -a /usr/share/maven /selected/maven \
        ;; \
    esac

FROM mcr.microsoft.com/dotnet/runtime:6.0 AS dotnet-runtime-6
FROM mcr.microsoft.com/dotnet/runtime:7.0 AS dotnet-runtime-7
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS dotnet-runtime-8
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS dotnet-runtime-9

FROM mcr.microsoft.com/dotnet/sdk:10.0.301-noble-aot AS build
ARG MERKLE_ADAPTERS=all
ARG MERKLE_RUNTIME
ARG MERKLE_INSTALLATION_ID=development

COPY --from=go-toolchain /selected /opt/selected
COPY --from=python-toolchain /selected /opt/selected
COPY --from=java-toolchain /selected /opt/selected
COPY --from=dotnet-runtime-6 /usr/share/dotnet/shared/Microsoft.NETCore.App /opt/dotnet-runtime-6
COPY --from=dotnet-runtime-7 /usr/share/dotnet/shared/Microsoft.NETCore.App /opt/dotnet-runtime-7
COPY --from=dotnet-runtime-8 /usr/share/dotnet/shared/Microsoft.NETCore.App /opt/dotnet-runtime-8
COPY --from=dotnet-runtime-9 /usr/share/dotnet/shared/Microsoft.NETCore.App /opt/dotnet-runtime-9
RUN if [ -d /opt/selected/go ]; then mv /opt/selected/go /usr/local/go; fi && \
    if [ -d /opt/selected/python ]; then cp -a /opt/selected/python/. /usr/local/; fi && \
    if [ -d /opt/selected/java ]; then mv /opt/selected/java /opt/java; fi && \
    if [ -d /opt/selected/maven ]; then mv /opt/selected/maven /usr/share/maven; fi && \
    case ",${MERKLE_ADAPTERS}," in \
      *,dotnet,*|*,all,*) \
        cp -a /opt/dotnet-runtime-6/. /usr/share/dotnet/shared/Microsoft.NETCore.App/ && \
        cp -a /opt/dotnet-runtime-7/. /usr/share/dotnet/shared/Microsoft.NETCore.App/ && \
        cp -a /opt/dotnet-runtime-8/. /usr/share/dotnet/shared/Microsoft.NETCore.App/ && \
        cp -a /opt/dotnet-runtime-9/. /usr/share/dotnet/shared/Microsoft.NETCore.App/ \
        ;; \
    esac && \
    mkdir -p \
      /var/cache/merkle/dotnet \
      /var/cache/merkle/nuget \
      /var/cache/merkle/maven \
      /var/cache/merkle/gradle \
      /var/cache/merkle/go \
      /var/cache/merkle/go-mod \
      /var/cache/merkle/go-build \
      /var/cache/merkle/pip && \
    chmod -R 0777 /var/cache/merkle && \
    rm -rf /opt/selected /opt/dotnet-runtime-6 /opt/dotnet-runtime-7 /opt/dotnet-runtime-8 /opt/dotnet-runtime-9

ENV PATH="/usr/local/go/bin:/opt/java/bin:/usr/share/maven/bin:${PATH}" \
    JAVA_HOME=/opt/java \
    DOTNET_CLI_HOME=/var/cache/merkle/dotnet \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    NUGET_PACKAGES=/var/cache/merkle/nuget \
    MAVEN_CONFIG=/var/cache/merkle/maven \
    GRADLE_USER_HOME=/var/cache/merkle/gradle \
    GOPATH=/var/cache/merkle/go \
    GOMODCACHE=/var/cache/merkle/go-mod \
    GOCACHE=/var/cache/merkle/go-build \
    PIP_CACHE_DIR=/var/cache/merkle/pip

WORKDIR /src/merkle
COPY global.json Directory.Build.props Merkle.slnx build ./
COPY src ./src
RUN --mount=type=cache,target=/var/cache/merkle/nuget \
    --mount=type=cache,target=/var/cache/merkle/maven \
    --mount=type=cache,target=/var/cache/merkle/go-mod \
    --mount=type=cache,target=/var/cache/merkle/go-build \
    ./build publish \
      --adapters "${MERKLE_ADAPTERS}" \
      --adapter-policy strict \
      --configuration Release \
      --runtime "${MERKLE_RUNTIME}" \
      --output /opt/merkle
RUN chmod -R 0777 /var/cache/merkle && \
    rm -rf /src/merkle && \
    mkdir -p /workspace
WORKDIR /workspace

COPY docker/entrypoint.sh /usr/local/bin/merkle-entrypoint
RUN chmod 0755 /usr/local/bin/merkle-entrypoint && cd /tmp && /opt/merkle/Merkle.Cli --help >/dev/null

LABEL org.opencontainers.image.source="https://github.com/leeozaka/merkle" \
      org.merkle.managed="true" \
      org.merkle.installation-id="${MERKLE_INSTALLATION_ID}" \
      org.merkle.adapters="${MERKLE_ADAPTERS}"

ENTRYPOINT ["/usr/local/bin/merkle-entrypoint"]
