#!/bin/bash
set -euo pipefail
export KC_DB_PASSWORD="$(</run/secrets/postgres_password)"
exec /opt/keycloak/bin/kc.sh start-dev --import-realm
