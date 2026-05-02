#!/usr/bin/env bash
set -euo pipefail

API="${API:-http://localhost:8080}"

# wait until postgres + rabbit + bus are all ready, otherwise the first
# few publishes can land before the consumer queue is bound
for _ in $(seq 1 60); do
  curl -sf "$API/readyz" >/dev/null && break
  sleep 2
done
curl -sf "$API/readyz" >/dev/null || { echo "api never became ready"; docker compose logs api; exit 1; }

post() {
  curl -sS -X POST "$API/api/orders" -H 'Content-Type: application/json' "$@"
}

field() {
  sed -n "s/.*\"$2\":\"\\([^\"]*\\)\".*/\\1/p" <<<"$1"
}

# happy path
o1=$(post -d '{"customerId":"11111111-1111-1111-1111-111111111111","items":[{"sku":"SKU-001","quantity":2,"unitPrice":15.50}],"totalAmount":31.00}')
id1=$(field "$o1" orderId)

# >100 -> 5% discount tier
o2=$(post -d '{"customerId":"22222222-2222-2222-2222-222222222222","items":[{"sku":"SKU-002","quantity":1,"unitPrice":100.00},{"sku":"SKU-003","quantity":2,"unitPrice":25.00}],"totalAmount":150.00}')
id2=$(field "$o2" orderId)

# idempotency
key="smoke-$(date +%s)"
o3=$(post -H "Idempotency-Key: $key" -d '{"customerId":"33333333-3333-3333-3333-333333333333","items":[{"sku":"SKU-001","quantity":1,"unitPrice":15.50}],"totalAmount":15.50}')
o3b=$(post -H "Idempotency-Key: $key" -d '{"customerId":"33333333-3333-3333-3333-333333333333","items":[{"sku":"SKU-001","quantity":1,"unitPrice":15.50}],"totalAmount":15.50}')
id3=$(field "$o3" orderId)
id3b=$(field "$o3b" orderId)
[ "$id3" = "$id3b" ] || { echo "idempotency broken: $id3 vs $id3b"; exit 1; }

# wait for everything to land on Processed
for id in "$id1" "$id2" "$id3"; do
  for _ in $(seq 1 20); do
    status=$(field "$(curl -sf "$API/api/orders/$id")" status)
    [ "$status" = "Processed" ] && break
    [ "$status" = "Failed" ] || [ "$status" = "Rejected" ] && { echo "$id ended up $status"; exit 1; }
    sleep 1
  done
  [ "$status" = "Processed" ] || { echo "$id stuck at $status"; exit 1; }
  echo "$id $status"
done

echo
curl -sf "$API/metrics" | grep '^orders_processed_total{' || { echo "no orders_processed_total"; exit 1; }
