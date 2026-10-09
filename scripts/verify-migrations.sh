#!/usr/bin/env bash
set -euo pipefail

plugin_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
export DOTNET_CLI_HOME="$plugin_root/.dotnet"
export NUGET_PACKAGES="$plugin_root/.packages"

provider_project="$plugin_root/src/Jellyfin.Plugin.Database.Npgsql/Jellyfin.Plugin.Database.Npgsql.csproj"
design_project="$plugin_root/tools/Jellyfin.Plugin.Database.Npgsql.Migrations/Jellyfin.Plugin.Database.Npgsql.Migrations.csproj"
script_root="$plugin_root/.artifacts/migrations"
script_path="$script_root/idempotent.sql"

mkdir -p "$script_root"
dotnet tool restore
dotnet restore "$plugin_root/Jellyfin.Plugin.Database.Npgsql.slnx" --locked-mode
dotnet build "$design_project" --configuration Release --no-restore
dotnet ef migrations has-pending-model-changes \
    --context JellyfinDbContext \
    --project "$provider_project" \
    --startup-project "$design_project" \
    --configuration Release \
    --no-build
dotnet ef migrations script \
    --context JellyfinDbContext \
    --project "$provider_project" \
    --startup-project "$design_project" \
    --configuration Release \
    --no-build \
    --idempotent \
    --output "$script_path"

echo "Verified the checked-in snapshot and wrote $script_path"
