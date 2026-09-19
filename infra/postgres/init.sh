#!/bin/sh
set -eu
# Executed by the upstream PostgreSQL entrypoint only on a new volume.
psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --set ON_ERROR_STOP=1 <<'SQL'
CREATE DATABASE keycloak;
CREATE DATABASE accesslab_tests;
SQL
