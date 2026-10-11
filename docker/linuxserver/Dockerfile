ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.100@sha256:c7445f141c04f1a6b454181bd098dcfa606c61ba0bd213d0a702489e5bd4cd71
ARG JELLYFIN_BASE_IMAGE=lscr.io/linuxserver/jellyfin:12.2ubu2604-ls53@sha256:1bb4f88d822a0510bb3604b68aacb80d77c1d2a30181e500090c65111b98723b

FROM ${DOTNET_SDK_IMAGE} AS build

ARG PLUGIN_VERSION=0.1.2.0
ARG INSTALLATION_DIRECTORY="Npgsql Database_0.1.2.0"

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

FROM ${JELLYFIN_BASE_IMAGE}

LABEL org.opencontainers.image.source="https://github.com/ramielrowe/jellyfin-plugin-database-npgsql" \
      org.opencontainers.image.description="Unofficial LinuxServer.io Jellyfin derivative with the Npgsql database provider pre-installed" \
      org.opencontainers.image.licenses="GPL-3.0-only"

COPY --from=build /out/seed/ /usr/local/share/jellyfin-npgsql/
COPY --chmod=0555 docker/common/install-plugin.sh /usr/local/libexec/install-jellyfin-npgsql
COPY docker/linuxserver/root/ /
COPY --chmod=0755 docker/linuxserver/root/etc/s6-overlay/s6-rc.d/init-npgsql-plugin/run /etc/s6-overlay/s6-rc.d/init-npgsql-plugin/run
