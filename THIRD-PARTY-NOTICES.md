# Third-party notices

The release package is designed to distribute the following runtime libraries:

- `Npgsql.dll`, copyright the Npgsql Development Team, licensed under the
  [PostgreSQL License](https://github.com/npgsql/npgsql/blob/main/LICENSE).
- `Npgsql.EntityFrameworkCore.PostgreSQL.dll`, copyright the Npgsql Development
  Team, licensed under the
  [PostgreSQL License](https://github.com/npgsql/efcore.pg/blob/main/LICENSE).

Jellyfin, Entity Framework Core, and Microsoft.Extensions assemblies are supplied
by the host and are intentionally not redistributed in this plugin package. Build,
analyzer, and test packages are development-only dependencies and are likewise not
included in the release package.
