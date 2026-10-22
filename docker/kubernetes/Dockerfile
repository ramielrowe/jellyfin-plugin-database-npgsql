ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.100@sha256:c7445f141c04f1a6b454181bd098dcfa606c61ba0bd213d0a702489e5bd4cd71
ARG RUNTIME_IMAGE=public.ecr.aws/docker/library/debian:bookworm-slim@sha256:a4672c0cb26fbdde88e38fa2dfb6c681942306680e41e4378b28770b6e79ee91

FROM ${DOTNET_SDK_IMAGE} AS build

ARG PLUGIN_VERSION=0.1.4.0
ARG INSTALLATION_DIRECTORY="Npgsql Database_0.1.4.0"

WORKDIR /src

COPY .editorconfig global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY LICENSE THIRD-PARTY-NOTICES.md meta.json ./
COPY build/Package.proj build/release-policy.json build/
COPY src/Jellyfin.Plugin.Database.Npgsql/ src/Jellyfin.Plugin.Database.Npgsql/
COPY tools/Jellyfin.Plugin.Database.Npgsql.Packaging/ tools/Jellyfin.Plugin.Database.Npgsql.Packaging/
COPY --chmod=0755 docker/common/build-seed.sh docker/common/
COPY docker/common/package-files.txt docker/common/

RUN dotnet restore src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj --locked-mode \
    && dotnet restore tools/Jellyfin.Plugin.Database.Npgsql.Packaging/Jellyfin.Plugin.Database.Npgsql.Packaging.csproj --locked-mode \
    && dotnet msbuild build/Package.proj -target:Package -property:Configuration=Release \
    && docker/common/build-seed.sh "${PLUGIN_VERSION}" "${INSTALLATION_DIRECTORY}"

FROM ${RUNTIME_IMAGE}

LABEL org.opencontainers.image.source="https://github.com/ramielrowe/jellyfin-plugin-database-npgsql" \
      org.opencontainers.image.description="Non-root Kubernetes init image that atomically seeds the Jellyfin Npgsql database provider" \
      org.opencontainers.image.licenses="GPL-3.0-only"

# Debian slim already contains the complete GNU runtime used by the installer.
# Assert that contract at build time; the runtime never installs or downloads.
RUN set -eu; \
    for command in bash chmod chown cmp find grep id install mkdir mktemp mv rm sed sha256sum sort stat wc; do \
        command -v "$command" >/dev/null; \
    done

COPY --from=build /out/seed/ /usr/local/share/jellyfin-npgsql/
COPY --chmod=0555 docker/common/install-plugin.sh /usr/local/bin/install-jellyfin-npgsql

USER 1000:1000
ENTRYPOINT ["/usr/local/bin/install-jellyfin-npgsql", "/usr/local/share/jellyfin-npgsql", "/config/data/plugins"]
