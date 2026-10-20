# dotnet-orders-api

A small orders REST API (C#, .NET 10, ASP.NET Core minimal API, EF Core with SQLite) built to show the parts of a
.NET service that matter under real traffic: idempotent creates, optimistic concurrency on stock, ETag / `If-Match`
on state changes, stable paging, and one error shape for everything.

It is a showcase, not a product: single instance, SQLite, no auth. See [Known limits](#known-limits).

## Endpoints

| Method and path | Purpose | Notable responses |
|---|---|---|
| `POST /items` | Create a SKU with price (minor units) and stock | 201, 400 field errors, 409 `sku-exists` |
| `GET /items/{sku}` | Read a SKU (case-insensitive) | 200, 404 `item-not-found` |
| `POST /orders` | Place an order. Requires `Idempotency-Key` | 201 + `ETag` + `Location`; 200 + `Idempotent-Replayed: true` on replay; 400, 409, 422 |
| `GET /orders/{id}` | Read an order. Supports `If-None-Match` | 200 + `ETag`, 304, 404 |
| `GET /orders` | List: `status`, `customerRef`, `createdAfter` (exclusive), `page` (0-based), `size` (1-100, default 20) | 200, 400 |
| `PATCH /orders/{id}/status` | Move an order along NEW, PAID, SHIPPED, CANCELLED. Requires `If-Match` | 200 + new `ETag`, 400, 404, 409, 412, 428 |
| `GET /healthz` | Liveness plus a database connectivity check | 200, 503 |

Status machine: `NEW -> PAID | CANCELLED`, `PAID -> SHIPPED | CANCELLED`; `SHIPPED` and `CANCELLED` are terminal.
Cancelling gives the reserved stock back.

## Run it

```bash
dotnet run --project src/OrdersApi --urls http://127.0.0.1:5101   # creates src/OrdersApi/orders.db (SQLite, WAL; found there on the real run)
bash scripts/demo.sh                                                # every example below, against a fresh database
python scripts/race_demo.py http://127.0.0.1:5101 20                # real parallel requests: last unit, same key, cancel vs ship
```

The connection string comes from `ConnectionStrings:Orders` (default `Data Source=orders.db`).

## Examples (all run against the real app on port 5101, on 2026-10-20)

```bash
# create an item: 50.05 per unit, 10 in stock
curl -si -X POST http://127.0.0.1:5101/items -H 'Content-Type: application/json' \
  -d '{"sku":"DEMO-1","name":"Demo widget","unitPriceMinor":5005,"stock":10}'
# 201 Created, Location: /items/DEMO-1
# {"sku":"DEMO-1","name":"Demo widget","unitPriceMinor":5005,"stock":10,"version":1}

# place an order: 2 x 5005 = 10010 reaches the 5% tier; discount 500.5 rounds half up to 501
curl -si -X POST http://127.0.0.1:5101/orders -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-key-1' \
  -d '{"customerRef":"alice","lines":[{"sku":"DEMO-1","quantity":2}]}'
# 201 Created, ETag: "1", Location: /orders/1
# {"id":1,"customerRef":"alice","status":"NEW","currency":"USD","subtotalMinor":10010,"discountPercent":5,
#  "discountMinor":501,"totalMinor":9509,"createdAt":"2026-10-20T12:08:52.2224507+00:00","lines":[...]}

# the same request again: a replay, 200 with Idempotent-Replayed: true, same id, same createdAt, stock taken once
curl -si -X POST http://127.0.0.1:5101/orders -H 'Content-Type: application/json' -H 'Idempotency-Key: demo-key-1' \
  -d '{"customerRef":"alice","lines":[{"sku":"DEMO-1","quantity":2}]}'

# the same key with a different body: 422
#   {"type":"urn:orders-api:problem:idempotency-key-reuse","title":"Unprocessable request","status":422,
#    "detail":"This Idempotency-Key was already used with a different request body.","instance":"/orders",
#    "code":"idempotency-key-reuse","errors":[],"traceId":"..."}

# no Idempotency-Key and a bad body: ONE 400 with every field error, in the standard shape
curl -si -X POST http://127.0.0.1:5101/orders -H 'Content-Type: application/json' \
  -d '{"customerRef":"","lines":[{"sku":"X","quantity":0}]}'
#   "code":"validation-failed","errors":[
#     {"field":"Idempotency-Key","code":"required","message":"The Idempotency-Key header is required to create an order."},
#     {"field":"customerRef","code":"required","message":"customerRef is required."},
#     {"field":"lines[0].quantity","code":"out-of-range","message":"quantity must be between 1 and 1000."}]

# state changes need If-Match
curl -si -X PATCH http://127.0.0.1:5101/orders/1/status -H 'Content-Type: application/json' -d '{"status":"PAID"}'
# 428 precondition-required
curl -si -X PATCH http://127.0.0.1:5101/orders/1/status -H 'Content-Type: application/json' -H 'If-Match: "99"' -d '{"status":"PAID"}'
# 412 precondition-failed
curl -si -X PATCH http://127.0.0.1:5101/orders/1/status -H 'Content-Type: application/json' -H 'If-Match: "1"' -d '{"status":"PAID"}'
# 200, ETag: "2"

curl -si http://127.0.0.1:5101/orders/1 -H 'If-None-Match: "2"'                  # 304 Not Modified
curl -si -X PATCH http://127.0.0.1:5101/orders/1/status -H 'Content-Type: application/json' \
  -H 'If-Match: "2"' -d '{"status":"NEW"}'                                         # 409 invalid-transition
curl -si -X PATCH http://127.0.0.1:5101/orders/1/status -H 'Content-Type: application/json' \
  -H 'If-Match: "2"' -d '{"status":"CANCELLED"}'                                   # 200, ETag: "3", stock back to 10

curl -si 'http://127.0.0.1:5101/orders?customerRef=alice&status=CANCELLED&page=0&size=5'   # 200, items + page/size/totalCount/totalPages
curl -si 'http://127.0.0.1:5101/orders?size=101'                                           # 400, field "size" out-of-range
curl -si http://127.0.0.1:5101/nope                                                        # 404, same problem shape, code not-found
curl -si -X DELETE http://127.0.0.1:5101/orders/1                                          # 405, same shape, code method-not-allowed
```

## Design notes

### Layout

One project, four folders, dependencies point downward: `Api` (minimal-API endpoints, body reading, problem
documents) -> `Application` (services, validation, fingerprinting, preconditions) -> `Domain` (entities, state machine,
pricing, no EF or HTTP types) and `Infrastructure` (DbContext, SQLite error classification, schema creation).
The services use the `DbContext` directly as the unit of work instead of hiding it behind a repository: for one
store that would be an interface with a single implementation.

### Idempotency (`POST /orders`)

The key is stored on the order row behind a unique index. The request body is normalised (customer trimmed, SKUs
trimmed and upper-cased, duplicate SKUs summed, lines sorted by SKU) and hashed with SHA-256 over a length-prefixed
canonical string.

| Situation | Result |
|---|---|
| Header missing, empty, longer than 128 characters, not printable ASCII, or sent twice | 400, field error on `Idempotency-Key` |
| New key | 201, order created, stock reserved |
| Same key, same normalised body (even reordered, different case or padding, split quantities) | 200 + `Idempotent-Replayed: true`, the stored order, stock untouched |
| Same key, different body | 422 `idempotency-key-reuse` |
| Request failed validation, unknown SKU or insufficient stock | Error, nothing stored, the key is NOT consumed (a corrected retry with the same key works) |
| Two requests with the same new key at once | One 201. The other is either a 200 replay (it lost the stock race and retried) or a 409 `idempotency-key-conflict` (it lost the unique-index race); never two orders |

A replay returns the order's current state, not a frozen copy of the first response: if the order was paid in
between, the replay shows `PAID` and the new ETag.

### Concurrency

`Item.Version` and `Order.Version` are integer EF Core concurrency tokens (SQLite has no `rowversion`), bumped in the
domain methods. EF adds `WHERE Version = @original` to each `UPDATE`; a writer with a stale copy affects zero rows and
gets `DbUpdateConcurrencyException`. The services answer that by clearing the change tracker, re-reading and trying
again, up to 8 rounds (`OrderService.MaxAttempts`), then 409 `concurrent-update`. Re-reading is what makes the outcomes
right: two buyers of the last unit retry into `insufficient-stock` (422) for the loser; five buyers of 2 units each from a stock of 20 all succeed; the same key sent twice retries into a replay.

Stock reservation and the order insert are one `SaveChanges`, so one transaction. Multi-line orders are all or nothing.

### ETag and If-Match

An order's ETag is its version in quotes (`"3"`). `GET` returns it, `If-None-Match` gets 304. Status changes
require `If-Match`: missing is 428, a non-matching value is 412, `*` matches any existing order, weak tags (`W/"3"`)
never satisfy `If-Match` (strong comparison), a malformed value is 400. If two clients hold the same ETag and one
wins, the loser re-reads inside the retry loop, sees the new version and gets 412, not a confusing 409. Check order for
a status change: 400 (body) -> 428 -> 404 -> 412 -> 409 (illegal transition).

### Errors

Every error is an RFC 9457 `application/problem+json` document with the same members: `type`
(`urn:orders-api:problem:<code>`), `title`, `status`, `detail`, `instance`, `code` (machine readable), `errors`
(field level, empty when not applicable) and `traceId`. That includes framework errors (unknown route 404, wrong
method 405) and unexpected exceptions (500 with a generic detail, details only in the log). Services throw
`AppException`; one exception handler turns it into the document, so no endpoint decides an error body.
Request bodies are read by hand (not by minimal-API binding) so a type error becomes a 400 with the JSON path
(`$.lines[0].quantity`) in the same shape; bodies over 64 KiB are 413, non-JSON content types 415.

Codes you will see: `validation-failed`, `invalid-json`, `unknown-sku`, `insufficient-stock`, `idempotency-key-reuse`,
`idempotency-key-conflict`, `concurrent-update`, `precondition-required`, `precondition-failed`, `invalid-transition`,
`sku-exists`, `item-not-found`, `order-not-found`, `payload-too-large`, `unsupported-media-type`, `not-found`,
`method-not-allowed`, `internal-error`, `database-unavailable`.

### Money

All money is `long` minor units (cents), single currency `USD`. The discount is one percentage of the whole subtotal
from the highest tier reached (threshold inclusive): 5% from 100.00, 10% from 500.00, 15% from 1000.00. It is rounded
half up to a whole minor unit: `discount = (subtotal * percent + 50) / 100` in checked integer arithmetic, so
10,010 * 5% = 500.5 becomes 501 (banker's rounding would give 500). Overflow raises `amount-too-large` instead of
wrapping.

### Time and listing

`TimeProvider` is injected; tests swap in a settable clock. `createdAt` is stored as UTC ticks in an `INTEGER`
because SQLite cannot `ORDER BY` a `DateTimeOffset` text column, and every digit (100 ns) survives the round trip.
Listing is newest first by `(createdAt, id)` both descending; the id tie-breaker keeps pages stable when orders share
a timestamp. `size` above 100 is rejected with 400 rather than silently clamped.

## How it is tested

`dotnet test` runs 277 xunit test cases (theory rows count individually), about 14 seconds:

* Unit: pricing tiers and rounding, the 16-cell status matrix, item stock rules, request fingerprinting (including a
  golden hash that pins the canonical form), validation of every field, ETag header parsing.
* End to end with `WebApplicationFactory<Program>`: every endpoint and error path. Each test class gets its own
  database: a named shared-cache in-memory SQLite database held open by one keeper connection (requests still open
  their own connections, so requests really overlap).
* Real concurrency, with a `Barrier` releasing the requests from dedicated threads, each scenario repeated: two buyers
  and eight buyers for the last unit (one 201, losers 409 or 422, stock 0), the same key two and six times at once
  (one order, stock taken once), cancel vs ship on one ETag (one 200, one 412, stock consistent with the winner),
  two cancels (restocked once), five non-competing buyers (all succeed through retries). The race tests also run
  against a WAL database file.
* Deterministic, no threads: a hook inside `SaveChanges` forces each losing path (unique-index race 409, token
  conflict retried then 201, retried into `insufficient-stock`, retried into a replay, give-up after 8 attempts, status
  change losing the token race 412, an unexpected exception 500), and a second `DbContext` updates a row behind the
  first one's back to prove the concurrency token on `Item` and `Order` even if the scheduler never overlaps requests.

## Mutation testing

**Stryker.NET 5.0.0 was tried first and did not produce a result.** It created 817 mutants, removed 82 that did not
compile and 132 it filtered, and queued 588 to test. After about 33 minutes (the time-box was about 25) it was still
in the test phase, repeatedly logging "Retrying the test session", with no per-mutant progress visible, so I stopped it.
Also note that Stryker's "safe mode" dropped every mutation inside `OrderService.PlaceAsync` (a compile error from
definite assignment in the mutated code), which is the most important method, so even a finished run would not have
covered it. No Stryker score is claimed.

The result below comes from the hand-rolled `scripts/mutation_check.py` instead: 46 single textual replacements, each
required to match its source text exactly once, the file restored in a `finally` block, a full
`dotnet test --no-restore` per mutant (about 25 seconds each, 46 mutants about 20 minutes). Run on 2026-10-20.

**Result: 45 of 46 killed on the first run; the one survivor was a real test gap, fixed; re-run of that mutant: killed.
46 of 46 killed after the fix. 0 mutants were invalid or broken.** This is a hand-picked sample of the rules that matter,
not exhaustive coverage; it says nothing about code I did not think to mutate.

Survivor M41 (body limit raised from 64 KiB to 1 MiB): not an equivalent mutant. My 413 tests posted
`RequestBody.MaxBytes + 1` bytes, i.e. they followed the constant, so raising the constant moved the test with it.
Fixed by using a literal `64 * 1024` in the test; the mutant is now killed by the two oversized-body tests.

The first column of failing tests below is the alphabetically first failing test, not the only one. Notably, removing
either concurrency token (M01, M02) and dropping the retry-loop details (M29, M30) were caught by the real racing tests
as well as by the deterministic ones, which is evidence that the barrier-released requests really do overlap.

| id | mutation | result | first failing test |
|---|---|---|---|
| M01 | Item loses its concurrency token | KILLED | ConcurrencyTests.Eight_buyers_race_for_the_last_unit_and_exactly_one_wins |
| M02 | Order loses its concurrency token | KILLED | ConcurrencyTests.Cancel_and_ship_race_on_one_etag_has_exactly_one_winner |
| M03 | reserve refuses the last unit (> becomes >=) | KILLED | ConcurrencyTests.Eight_buyers_race_for_the_last_unit_and_exactly_one_wins |
| M04 | reserve does not decrement stock | KILLED | ConcurrencyTests.Buyers_who_do_not_compete_for_the_last_unit_all_succeed_through_retries |
| M05 | restock does nothing | KILLED | ConcurrencyTests.Cancel_and_ship_race_on_one_etag_has_exactly_one_winner |
| M06 | reserve does not bump the item version | KILLED | ConcurrencyTests.Buyers_who_do_not_compete_for_the_last_unit_all_succeed_through_retries |
| M07 | status change does not bump the order version | KILLED | ConcurrencyTests.Cancel_and_ship_race_on_one_etag_has_exactly_one_winner |
| M08 | PAID can no longer be cancelled | KILLED | ConcurrencyTests.Cancel_and_ship_race_on_one_etag_has_exactly_one_winner |
| M09 | NEW may jump straight to SHIPPED | KILLED | OrderStatusApiTests.Illegal_transitions_from_new_are_409 |
| M10 | terminal states may move anywhere | KILLED | OrderStatusApiTests.A_failed_transition_does_not_restock |
| M11 | cancelling restocks on SHIPPED instead of CANCELLED | KILLED | ConcurrencyTests.Cancel_and_ship_race_on_one_etag_has_exactly_one_winner |
| M12 | cancelling never restocks | KILLED | ConcurrencyTests.Cancel_and_ship_race_on_one_etag_has_exactly_one_winner |
| M13 | discount truncates instead of rounding half up | KILLED | OrderCreateTests.Discount_tier_is_applied_with_half_up_rounding |
| M14 | tier threshold is exclusive (>= becomes >) | KILLED | OrderCreateTests.Exactly_at_a_tier_threshold_the_discount_applies |
| M15 | line total no longer detects overflow | KILLED | PricingPolicyTests.Line_total_overflow_is_an_error_not_a_wrong_number |
| M16 | total ignores the discount | KILLED | OrderCreateTests.Discount_tier_is_applied_with_half_up_rounding |
| M17 | tiers are scanned lowest first | KILLED | PricingPolicyTests.Quote_at_the_top_tier |
| M18 | same key with a different body is no longer rejected | KILLED | OrderCreateTests.Same_key_with_a_different_customer_is_422 |
| M19 | replay is reported as a fresh placement | KILLED | ConcurrencyTests.The_same_key_sent_six_times_at_once_creates_one_order |
| M20 | fingerprint ignores the customer | KILLED | OrderCreateTests.Same_key_with_a_different_customer_is_422 |
| M21 | fingerprint ignores quantities | KILLED | OrderCreateTests.Same_key_with_a_different_quantity_is_422_and_changes_nothing |
| M22 | fingerprint drops the length prefix on SKUs | KILLED | RequestFingerprintTests.Canonical_form_is_pinned_by_a_golden_hash |
| M23 | duplicate SKU lines overwrite instead of summing | KILLED | OrderCreateTests.Duplicate_sku_lines_are_merged_into_one_line |
| M24 | SKUs are no longer upper-cased | KILLED | ItemsApiTests.Create_returns_201_with_a_normalised_item |
| M25 | Idempotency-Key length limit removed | KILLED | OrderCreateTests.A_key_of_128_characters_is_accepted_and_129_is_rejected |
| M26 | two Idempotency-Key headers accepted | KILLED | OrderCreateTests.Two_idempotency_key_headers_are_rejected_instead_of_merged_into_one_key |
| M27 | replay response loses Idempotent-Replayed | KILLED | ConcurrencyTests.The_same_key_sent_twice_at_once_creates_one_order_and_takes_stock_once |
| M28 | unique-index race answers 503 instead of 409 | KILLED | ConcurrencyTests.The_same_key_sent_twice_at_once_creates_one_order_and_takes_stock_once |
| M29 | lost concurrency race does not clear the change tracker | KILLED | ConcurrencyTests.Buyers_who_do_not_compete_for_the_last_unit_all_succeed_through_retries |
| M30 | retry cap lowered to 1 attempt | KILLED | ConcurrencyTests.Buyers_who_do_not_compete_for_the_last_unit_all_succeed_through_retries |
| M31 | If-Match is never enforced | KILLED | ConcurrencyTests.Cancel_and_ship_race_on_one_etag_has_exactly_one_winner |
| M32 | weak ETags satisfy If-Match | KILLED | OrderStatusApiTests.A_weak_etag_never_satisfies_if_match |
| M33 | If-None-Match uses strong comparison | KILLED | OrderReadTests.If_none_match_with_the_current_etag_is_304_without_a_body |
| M34 | id tie-breaker removed from the sort | KILLED | OrderListingTests.Orders_created_at_the_same_instant_are_ordered_by_id_descending |
| M35 | list is oldest first | KILLED | OrderListingTests.Newest_first_by_created_at |
| M36 | createdAfter becomes inclusive | KILLED | OrderListingTests.Created_after_is_exclusive |
| M37 | status filter ignored | KILLED | OrderListingTests.Filters_by_status |
| M38 | page size cap raised from 100 to 1000 | KILLED | OrderListingTests.Size_100_is_the_cap_and_101_is_rejected |
| M39 | quantity 0 accepted | KILLED | OrderCreateTests.Missing_key_and_bad_body_report_every_field_at_once |
| M40 | merged quantity cap removed | KILLED | ValidationTests.Merged_quantity_for_one_sku_is_capped_too |
| M41 | request body limit raised from 64 KiB to 1 MiB | SURVIVED (run 1), KILLED after test fix | ProblemShapeTests.An_oversized_body_is_a_413_problem |
| M42 | UTF-8 BOM no longer stripped | KILLED | ProblemShapeTests.A_utf8_byte_order_mark_is_tolerated |
| M43 | createdAt not normalised to UTC | KILLED | TimestampTests.A_clock_that_returns_a_non_utc_offset_still_gives_identical_post_and_get_bodies |
| M44 | createdAt stored with millisecond precision (201 body vs stored value) | KILLED | TimestampTests.A_clock_that_returns_a_non_utc_offset_still_gives_identical_post_and_get_bodies |
| M45 | problem documents lose the machine-readable code | KILLED | ForcedRaceTests.A_status_change_that_loses_the_token_race_is_answered_412 |
| M46 | conflict answers 422 instead of 409 | KILLED | ConcurrencyTests.The_same_key_sent_six_times_at_once_creates_one_order |

Reproduce: `python scripts/mutation_check.py` (all), `python scripts/mutation_check.py M41` (one), `--list` (no run).


## Found by running the real app (the tests had missed these)

Everything below was found by starting the app on port 5101 and poking it with curl, not by the test suite; items 1 and 2 now
have tests, and all four were re-checked against the running app (item 3 cannot be tested in-process because TestServer is not Kestrel).

1. Two `Idempotency-Key` headers were silently merged by ASP.NET into one key `"a,b"`. Now a 400 field error `multiple-values`.
2. A UTF-8 byte order mark at the start of a JSON body (what PowerShell and Notepad write) was rejected as `invalid-json`.
   The body reader now strips it.
3. A non-ASCII header value got an empty 400 from Kestrel before the pipeline ran, so not a problem document. Kestrel now decodes
   headers as UTF-8 and the normal validator answers. A header over the size limit (431) is still an empty Kestrel response.
4. EF Core logged every SQL statement at Information level; added `appsettings.json` log levels.

Checked and fine: `createdAt` is character-for-character identical (`2026-10-20T12:08:52.2224507+00:00`, seven fractional digits) in
the 201 body, the replay, `GET`, the list, and after restarting the process. An earlier Java version of this API had a precision mismatch
between the 201 body and the stored value; here the value is stored as UTC ticks and the domain normalises the offset, so the problem does
not exist, and `TimestampTests` pins it (including a clock that returns a +05:30 offset). Cosmetic and left alone: errors thrown through the
exception handler carry `Cache-Control: no-cache,no-store`; framework 404/405 documents list `traceId` before `code`.

`scripts/race_demo.py` against the real Kestrel and a WAL file, 20 rounds each: 10 buyers for the last unit gave `{201: 20, 422: 180}`;
8 senders of one key gave `{201: 20, 200: 137, 409: 3}` (so the unique-index 409 path really happens); cancel vs ship gave
`{200: 20, 412: 20}`; every invariant held.


## Known limits

* SQLite is not SQL Server or Postgres. Write locking, isolation and error codes differ; the unique-violation
  classification (`SQLITE_CONSTRAINT_UNIQUE` 2067) and the retry loop are SQLite-shaped. Nothing here was run against
  another database.
* Single instance. Idempotency and concurrency are database-level, so several instances sharing one Postgres would
  mostly work, but that was not tried; SQLite over a network file system is not safe.
* No authentication or authorization, no rate limiting, no TLS configuration.
* Idempotency keys are global (not per client) and never expire; there is no cleanup. A real service would scope keys
  per caller and expire them.
* Schema comes from `EnsureCreated`; there are no migrations.
* Errors raised by Kestrel before the pipeline runs (for example a request header over the size limit, 431, or a
  malformed request line) are not problem documents. Not covered by automated tests (TestServer is not Kestrel);
  checked by hand for the 431 only.
* Not verified: CI, any OS other than Windows 11, any database other than SQLite, load or latency under real traffic,
  behaviour with the app behind a proxy.

## Clone, license

```bash
git clone https://github.com/Supp3r-Man/dotnet-orders-api.git
```

CI ran green on the first run on GitHub Actions (ubuntu-latest, 2026-10-20).

MIT, see [LICENSE](LICENSE).
