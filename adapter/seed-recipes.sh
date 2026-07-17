#!/usr/bin/env bash
# Seeds forge with Parts + BOM lines mirroring data/recipes-v0.toml's craft chain
# (iron-ore -> iron-plate -> iron-gear), via forge-api HTTP only (D10 — no direct
# Postgres). Idempotent: re-running finds existing parts/BOM lines by name and
# skips creation instead of duplicating.
#
# Scope: Parts + BOM only. No stock/MRP writes — B19 (cycle-validator untested
# against real MRP data) is a non-issue here since this seed is a strict DAG
# (ore -> plate -> gear, same as the TOML), and nothing here touches MRP planning.
#
# Not a 1:1 port of recipes-v0.toml — forge's Part/BOM model has no fields for
# duration/speed_base/machine category (see inventory.md B28, WorkCenter mapping
# skipped). This seeds what forge's model *can* represent: items + input/output
# quantities. The TOML remains the sim's source of truth (D14); this is the
# forge-linkage proof, not a competing content source.
#
# Usage: BASE_URL=http://127.0.0.1:5000 ./seed-recipes.sh

set -euo pipefail

BASE_URL="${BASE_URL:-http://127.0.0.1:5000}"
BARCODE="${BARCODE:-GAME-ADAPTER-01}"
PIN="${PIN:-4242}"

log() { echo "[seed-recipes] $*" >&2; }

TOKEN=$(curl -s -X POST "$BASE_URL/api/v1/auth/kiosk-login" \
  -H "Content-Type: application/json" \
  -d "{\"barcode\":\"$BARCODE\",\"pin\":\"$PIN\"}" | jq -r '.token')

if [[ -z "$TOKEN" || "$TOKEN" == "null" ]]; then
  log "kiosk-login failed — is forge-api up? (see inventory.md B21: base docker-compose.yml only, never the dev overlay)"
  exit 1
fi

auth() { curl -s -H "Authorization: Bearer $TOKEN" "$@"; }

# find_or_create_part <name> <procurement_source> <inventory_class> -> prints part id
find_or_create_part() {
  local name="$1" proc="$2" inv="$3"
  local existing
  existing=$(auth "$BASE_URL/api/v1/parts?search=$(python3 -c "import urllib.parse,sys; print(urllib.parse.quote(sys.argv[1]))" "$name")&pageSize=10" \
    | jq -r --arg n "$name" '.items[] | select(.name == $n) | .id' | head -1)
  if [[ -n "$existing" ]]; then
    log "part '$name' exists -> id=$existing"
    echo "$existing"
    return
  fi
  local id
  id=$(auth -X POST "$BASE_URL/api/v1/parts" -H "Content-Type: application/json" \
    -d "{\"name\":\"$name\",\"description\":\"factory v0 seed — mirrors data/recipes-v0.toml\",\"revision\":\"A\",\"procurementSource\":\"$proc\",\"inventoryClass\":\"$inv\",\"materialSpecId\":null}" \
    | jq -r '.id')
  log "part '$name' created -> id=$id"
  echo "$id"
}

# ensure_bom_line <parent_id> <child_id> <qty> <source_type>
ensure_bom_line() {
  local parent="$1" child="$2" qty="$3" source="$4"
  local has_line
  has_line=$(auth "$BASE_URL/api/v1/parts/$parent" | jq -r --argjson c "$child" '.bomLines[] | select(.childPartId == $c) | .id' | head -1)
  if [[ -n "$has_line" ]]; then
    log "BOM line parent=$parent child=$child already exists -> id=$has_line"
    return
  fi
  local id
  id=$(auth -X POST "$BASE_URL/api/v1/parts/$parent/bom" -H "Content-Type: application/json" \
    -d "{\"childPartId\":$child,\"quantity\":$qty,\"referenceDesignator\":null,\"sourceType\":\"$source\",\"leadTimeDays\":null,\"notes\":\"factory v0 seed\"}" \
    | jq -r '.bomLines[-1].id')
  log "BOM line parent=$parent child=$child qty=$qty created -> id=$id"
}

ORE_ID=$(find_or_create_part "factory-v0-iron-ore" "Buy" "Raw")
PLATE_ID=$(find_or_create_part "factory-v0-iron-plate" "Make" "Component")
GEAR_ID=$(find_or_create_part "factory-v0-iron-gear" "Make" "FinishedGood")

# smelt-iron-plate: 1x ore -> 1x plate
ensure_bom_line "$PLATE_ID" "$ORE_ID" 1 "Buy"
# craft-iron-gear: 2x plate -> 1x gear
ensure_bom_line "$GEAR_ID" "$PLATE_ID" 2 "Make"

log "done. part ids: ore=$ORE_ID plate=$PLATE_ID gear=$GEAR_ID"
echo "{\"ore\":$ORE_ID,\"plate\":$PLATE_ID,\"gear\":$GEAR_ID}"
