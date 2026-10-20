#!/usr/bin/env bash
# Walks through every example in the README against a running instance.
# Usage: scripts/demo.sh [base-url]   (default http://127.0.0.1:5101)
# Uses a random SKU/key suffix so it can be re-run against the same database file.
BASE="${1:-http://127.0.0.1:5101}"
S="$(date +%s%N | tail -c 7)"
SKU="DEMO-$S"
KEY="demo-key-$S"
JSON='Content-Type: application/json'

step() { printf '\n### %s\n' "$1"; }

step "health"
curl -si "$BASE/healthz"

step "create an item (price 5005 minor units = 50.05, stock 10)"
curl -si -X POST "$BASE/items" -H "$JSON" -d "{\"sku\":\"$SKU\",\"name\":\"Demo widget\",\"unitPriceMinor\":5005,\"stock\":10}"

step "place an order: 2 x 5005 = 10010 -> 5% tier, discount 500.5 rounds half up to 501"
curl -si -X POST "$BASE/orders" -H "$JSON" -H "Idempotency-Key: $KEY" \
  -d "{\"customerRef\":\"alice\",\"lines\":[{\"sku\":\"$SKU\",\"quantity\":2}]}"

step "same key, same body: replay (200 + Idempotent-Replayed: true)"
curl -si -X POST "$BASE/orders" -H "$JSON" -H "Idempotency-Key: $KEY" \
  -d "{\"customerRef\":\"alice\",\"lines\":[{\"sku\":\"$SKU\",\"quantity\":2}]}"

step "same key, different body: 422"
curl -si -X POST "$BASE/orders" -H "$JSON" -H "Idempotency-Key: $KEY" \
  -d "{\"customerRef\":\"alice\",\"lines\":[{\"sku\":\"$SKU\",\"quantity\":3}]}"

step "missing Idempotency-Key plus a bad body: one 400 with every field error"
curl -si -X POST "$BASE/orders" -H "$JSON" -d '{"customerRef":"","lines":[{"sku":"X","quantity":0}]}'

step "stock after one order (8 left)"
curl -si "$BASE/items/$SKU"

step "GET the order (note the ETag)"
curl -si "$BASE/orders/1"

step "status change without If-Match: 428"
curl -si -X PATCH "$BASE/orders/1/status" -H "$JSON" -d '{"status":"PAID"}'

step "status change with a wrong If-Match: 412"
curl -si -X PATCH "$BASE/orders/1/status" -H "$JSON" -H 'If-Match: "99"' -d '{"status":"PAID"}'

step "status change with the right If-Match: 200 and a new ETag"
curl -si -X PATCH "$BASE/orders/1/status" -H "$JSON" -H 'If-Match: "1"' -d '{"status":"PAID"}'

step "conditional GET: 304"
curl -si "$BASE/orders/1" -H 'If-None-Match: "2"'

step "illegal transition PAID -> NEW: 409"
curl -si -X PATCH "$BASE/orders/1/status" -H "$JSON" -H 'If-Match: "2"' -d '{"status":"NEW"}'

step "cancel (restocks 2 units)"
curl -si -X PATCH "$BASE/orders/1/status" -H "$JSON" -H 'If-Match: "2"' -d '{"status":"CANCELLED"}'
curl -s "$BASE/items/$SKU"; echo

step "list: filter, paging, size cap (size 101 -> 400)"
curl -si "$BASE/orders?customerRef=alice&status=CANCELLED&page=0&size=5"
curl -si "$BASE/orders?size=101"

step "unknown route and wrong method share the error shape"
curl -si "$BASE/nope"
curl -si -X DELETE "$BASE/orders/1"
