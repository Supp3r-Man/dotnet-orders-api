#!/usr/bin/env python
"""Fires real parallel HTTP requests at a running instance and prints what happened.

Usage: python scripts/race_demo.py [base-url] [rounds]    (default http://127.0.0.1:5101, 20 rounds)
It is a demo and a smoke check against the real Kestrel + file database, not part of the test suite.
Exit code 1 if any invariant is violated.
"""
import http.client
import json
import sys
import threading
import uuid
from collections import Counter
from urllib.parse import urlparse

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5101"
ROUNDS = int(sys.argv[2]) if len(sys.argv) > 2 else 20
HOST = urlparse(BASE)


def call(method, path, body=None, headers=None):
    conn = http.client.HTTPConnection(HOST.hostname, HOST.port, timeout=60)
    try:
        h = {"Content-Type": "application/json", **(headers or {})}
        conn.request(method, path, json.dumps(body) if body is not None else None, h)
        resp = conn.getresponse()
        raw = resp.read().decode()
        return resp.status, dict(resp.getheaders()), (json.loads(raw) if raw else None)
    finally:
        conn.close()


def race(calls):
    barrier = threading.Barrier(len(calls))
    results = [None] * len(calls)

    def worker(i, fn):
        barrier.wait()
        results[i] = fn()

    threads = [threading.Thread(target=worker, args=(i, fn)) for i, fn in enumerate(calls)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    return results


def new_item(stock):
    sku = "RACE-" + uuid.uuid4().hex[:10].upper()
    status, _, _ = call("POST", "/items", {"sku": sku, "name": "race", "unitPriceMinor": 1000, "stock": stock})
    assert status == 201, status
    return sku


def place(sku, key, customer="racer", qty=1):
    return lambda: call("POST", "/orders", {"customerRef": customer, "lines": [{"sku": sku, "quantity": qty}]},
                        {"Idempotency-Key": key})


def stock(sku):
    return call("GET", f"/items/{sku}")[2]["stock"]


failures = []
last_unit, same_key, cancel_ship = Counter(), Counter(), Counter()

for _ in range(ROUNDS):
    sku = new_item(1)
    res = race([place(sku, f"k{i}-{uuid.uuid4().hex}") for i in range(10)])
    codes = Counter(r[0] for r in res)
    last_unit.update(codes)
    if codes[201] != 1 or stock(sku) != 0 or set(codes) - {201, 409, 422}:
        failures.append(("last-unit", dict(codes), stock(sku)))

for _ in range(ROUNDS):
    sku = new_item(5)
    key = "same-" + uuid.uuid4().hex
    res = race([place(sku, key) for _ in range(8)])
    codes = Counter(r[0] for r in res)
    same_key.update(codes)
    ids = {r[2]["id"] for r in res if r[0] in (200, 201)}
    if codes[201] != 1 or len(ids) != 1 or stock(sku) != 4 or set(codes) - {200, 201, 409}:
        failures.append(("same-key", dict(codes), stock(sku)))

for _ in range(ROUNDS):
    sku = new_item(10)
    status, headers, order = place(sku, "cs-" + uuid.uuid4().hex, qty=3)()
    oid = order["id"]
    status, headers, _ = call("PATCH", f"/orders/{oid}/status", {"status": "PAID"}, {"If-Match": headers["ETag"]})
    etag = headers["ETag"]
    res = race([
        lambda: call("PATCH", f"/orders/{oid}/status", {"status": "SHIPPED"}, {"If-Match": etag}),
        lambda: call("PATCH", f"/orders/{oid}/status", {"status": "CANCELLED"}, {"If-Match": etag}),
    ])
    codes = Counter(r[0] for r in res)
    cancel_ship.update(codes)
    final = call("GET", f"/orders/{oid}")[2]["status"]
    expected_stock = 10 if final == "CANCELLED" else 7
    if codes[200] != 1 or codes[412] != 1 or stock(sku) != expected_stock:
        failures.append(("cancel-vs-ship", dict(codes), final, stock(sku)))

print(f"last unit, 10 buyers x {ROUNDS} rounds : {dict(last_unit)}")
print(f"same key, 8 senders x {ROUNDS} rounds  : {dict(same_key)}")
print(f"cancel vs ship x {ROUNDS} rounds       : {dict(cancel_ship)}")
if failures:
    print("INVARIANT VIOLATIONS:", failures)
    sys.exit(1)
print("all invariants held")
