# Order processing service

Test task for Entravel. .NET 8 minimal API in front of Postgres and RabbitMQ.
Posting an order persists it as `Pending` and returns 202 right away; a
MassTransit consumer in the same process picks it up off the queue, validates
it against inventory, applies a discount and writes back the final status.

## Running it

Need Docker and Compose v2.

```
docker compose up --build
```

That's Postgres 16, RabbitMQ 3 and the api. Migrations and the inventory seed
run on first start. API listens on `localhost:8080`.

```
curl -X POST http://localhost:8080/api/orders \
  -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(uuidgen)" \
  -d '{
    "customerId": "11111111-1111-1111-1111-111111111111",
    "items": [
      { "sku": "SKU-001", "quantity": 2, "unitPrice": 15.50 },
      { "sku": "SKU-002", "quantity": 1, "unitPrice": 100.00 }
    ],
    "totalAmount": 131.00
  }'

curl http://localhost:8080/api/orders/<orderId>
```

Usually settles to `Processed` in a few hundred ms.

Endpoints:

- `GET /healthz` - liveness, no deps
- `GET /readyz` - postgres + rabbit probes
- `GET /metrics` - prometheus exposition
- `GET /swagger` - dev only

There's a `scripts/smoke.sh` if you want to throw a few orders at it.

## Tests

```
dotnet test
```

Unit tests run anywhere. The integration suite uses Testcontainers, so it needs
a working Docker socket but spins up its own Postgres and RabbitMQ.

## Why these choices

Queue: RabbitMQ. Durable delivery, native ack/DLX, multiple consumers — the
things this kind of pipeline actually needs. Redis Streams works but you'd
roll DLQ semantics yourself; `System.Threading.Channels` dies with the
process. MassTransit on top because it gives typed messages, retry as
config and a clean test harness — much less boilerplate than RabbitMQ.Client
directly.

DB: Postgres + EF Core. Migrations come for free and `xmin` plays the
rowversion role for optimistic concurrency on the inventory table, so no
extra column to maintain.

The interesting part is the consumer. It claims the order (`Pending` →
`Processing`) and commits, *then* opens a second transaction for the actual
work — validate stock, reprice from the current inventory, decrement, set
the discount, mark Processed. The reason for the split is that two retried
deliveries can't both reach the second step for the same order. The downside
is that if the process dies between the two transactions the order sits in
`Processing` until something unsticks it; a periodic reaper job would do
that, but it's out of scope here.

Idempotency-Key is persisted on the order with a unique partial index, so
a repeat POST returns the original. The consumer additionally short-circuits
on `Status != Pending`, which is what really protects us against duplicate
broker deliveries.

Discount is 5% over 100, 10% over 500 (`OrderTotalsCalculator`). Submitted
unit prices are validated only for the totals match — when the worker runs
it reprices from the current inventory before applying the tier.

## What's missing / would be next

- No transactional outbox: `SaveChanges` and `Publish` are two operations,
  if the process dies between them the order sits at `Pending` forever.
  MassTransit has a built-in outbox, that's the obvious next step.
- No reaper job for stuck-in-`Processing` rows.
- `orders_received_total{customer=<uuid>}` is high-cardinality, fine for a
  demo, would need bucketing in production.
- API and consumer in the same process. Cheap to deploy, but a slow consumer
  back-pressures the API. Should be split for prod.
- No OpenTelemetry / tracing wired up. Both MassTransit and Npgsql ship
  instrumentation, it would be a config block.
- No auth. Single-tenant per the spec.

## Assumptions

- Submitted unit prices are hints. The worker reprices from inventory and
  the row's `TotalAmount` reflects the post-processing value.
- Stock can theoretically go negative if two consumers race past the
  pre-check before either commits. `xmin` makes one retry, but for a hot
  SKU you'd want pessimistic locking or a separate reservation step. With
  the current concurrency (10) it has not occured in any of the tests.
- Credentials are in `docker-compose.yml`. Obviously not how you'd ship it.

## Seeded inventory

```
SKU-001  Widget A   15.50  x100
SKU-002  Widget B  100.00  x50
SKU-003  Widget C   25.00  x200
SKU-004  Widget D   75.00  x30
SKU-005  Widget E  500.00  x10
```
