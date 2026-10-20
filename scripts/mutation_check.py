#!/usr/bin/env python
"""Hand-rolled mutation check: apply ONE textual mutation at a time, run the test suite, record whether
at least one test failed, then restore the file.

Usage:  python scripts/mutation_check.py              (all mutations)
        python scripts/mutation_check.py M05 M10      (selected ids)
        python scripts/mutation_check.py --list       (print the mutations, run nothing)
Set DOTNET to the dotnet executable if `dotnet` is not on PATH.

Each mutation replaces the ONLY occurrence of `old` in the named file with `new`. A mutation whose `old`
text is missing or appears more than once is reported as BROKEN (the script is out of date), never as a
survivor. A mutant that does not compile is reported as INVALID, not killed. Files are restored from the
original bytes in a finally block (also on Ctrl-C). If a test run exceeds the timeout, the process tree
this script started is killed (taskkill on Windows) and the mutant counts as killed (timeout).
Every run builds and runs the whole suite, roughly 20-30 seconds per mutant.

History: Stryker.NET 5.0.0 was tried first and did not finish (see the README), hence this script.
"""
import os
import re
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SRC = os.path.join(ROOT, "src", "OrdersApi")
DOTNET = os.environ.get("DOTNET", "dotnet")
TIMEOUT_SECONDS = 420

# (id, description, relative path under src/OrdersApi, old text, new text)
MUTATIONS = [
    # --- optimistic concurrency and stock arithmetic
    ("M01", "Item loses its concurrency token", "Infrastructure/OrdersDbContext.cs",
     "item.Property(i => i.Version).IsConcurrencyToken();", "item.Property(i => i.Version);"),
    ("M02", "Order loses its concurrency token", "Infrastructure/OrdersDbContext.cs",
     "order.Property(o => o.Version).IsConcurrencyToken();", "order.Property(o => o.Version);"),
    ("M03", "reserve refuses the last unit (> becomes >=)", "Domain/Item.cs",
     "if (quantity > Stock)", "if (quantity >= Stock)"),
    ("M04", "reserve does not decrement stock", "Domain/Item.cs",
     "Stock -= quantity;", "Stock -= 0;"),
    ("M05", "restock does nothing", "Domain/Item.cs",
     "Stock += quantity;", "Stock += 0;"),
    ("M06", "reserve does not bump the item version", "Domain/Item.cs",
     "Stock -= quantity;\n        Version++;", "Stock -= quantity;"),
    ("M07", "status change does not bump the order version", "Domain/Order.cs",
     "Status = target;\n        Version++;", "Status = target;"),
    # --- status machine
    ("M08", "PAID can no longer be cancelled", "Domain/OrderStatus.cs",
     "OrderStatus.Paid => to is OrderStatus.Shipped or OrderStatus.Cancelled,",
     "OrderStatus.Paid => to is OrderStatus.Shipped,"),
    ("M09", "NEW may jump straight to SHIPPED", "Domain/OrderStatus.cs",
     "OrderStatus.New => to is OrderStatus.Paid or OrderStatus.Cancelled,",
     "OrderStatus.New => to is OrderStatus.Paid or OrderStatus.Cancelled or OrderStatus.Shipped,"),
    ("M10", "terminal states may move anywhere", "Domain/OrderStatus.cs",
     "_ => false,", "_ => true,"),
    ("M11", "cancelling restocks on SHIPPED instead of CANCELLED", "Application/OrderService.cs",
     "if (target == OrderStatus.Cancelled)", "if (target == OrderStatus.Shipped)"),
    ("M12", "cancelling never restocks", "Application/OrderService.cs",
     "if (target == OrderStatus.Cancelled)", "if (target == OrderStatus.Cancelled && ifMatch.Any && !ifMatch.Any)"),
    # --- pricing
    ("M13", "discount truncates instead of rounding half up", "Domain/PricingPolicy.cs",
     "checked(subtotalMinor * percent + 50) / 100", "checked(subtotalMinor * percent) / 100"),
    ("M14", "tier threshold is exclusive (>= becomes >)", "Domain/PricingPolicy.cs",
     "if (subtotalMinor >= tier.ThresholdMinor)", "if (subtotalMinor > tier.ThresholdMinor)"),
    ("M15", "line total no longer detects overflow", "Domain/PricingPolicy.cs",
     "return checked(unitPriceMinor * quantity);", "return unchecked(unitPriceMinor * quantity);"),
    ("M16", "total ignores the discount", "Domain/PricingPolicy.cs",
     "new Quote(subtotal, percent, discount, subtotal - discount)", "new Quote(subtotal, percent, discount, subtotal)"),
    ("M17", "tiers are scanned lowest first", "Domain/PricingPolicy.cs",
     "tiers.OrderByDescending(t => t.ThresholdMinor)", "tiers.OrderBy(t => t.ThresholdMinor)"),
    # --- idempotency and fingerprinting
    ("M18", "same key with a different body is no longer rejected", "Application/OrderService.cs",
     "if (existing.RequestHash != hash)", "if (existing.RequestHash.Length < 0)"),
    ("M19", "replay is reported as a fresh placement", "Application/OrderService.cs",
     "return new Placement(existing, Replayed: true);", "return new Placement(existing, Replayed: false);"),
    ("M20", "fingerprint ignores the customer", "Application/RequestFingerprint.cs",
     ".Append(order.CustomerRef.Length).Append(':').Append(order.CustomerRef);", ";"),
    ("M21", "fingerprint ignores quantities", "Application/RequestFingerprint.cs",
     ".Append(\"|qty=\").Append(line.Quantity);", ".Append(\"|qty=\");"),
    ("M22", "fingerprint drops the length prefix on SKUs", "Application/RequestFingerprint.cs",
     ".Append(\"|sku=\").Append(line.Sku.Length).Append(':').Append(line.Sku)", ".Append(\"|sku=\").Append(line.Sku)"),
    ("M23", "duplicate SKU lines overwrite instead of summing", "Application/Validation.cs",
     "merged[sku] = merged.GetValueOrDefault(sku) + line.Quantity.Value;", "merged[sku] = line.Quantity.Value;"),
    ("M24", "SKUs are no longer upper-cased", "Application/Validation.cs",
     "sku = NormalizeSku(trimmed);", "sku = trimmed;"),
    ("M25", "Idempotency-Key length limit removed", "Application/Validation.cs",
     "if (key.Length > MaxKeyLength)", "if (key.Length > MaxKeyLength * 100)"),
    ("M26", "two Idempotency-Key headers accepted", "Api/Endpoints.cs",
     "if (keyValues.Count > 1)", "if (keyValues.Count > 100)"),
    ("M27", "replay response loses Idempotent-Replayed", "Api/Endpoints.cs",
     "ctx.Response.Headers[ReplayedHeader] = \"true\";", "_ = ReplayedHeader;"),
    ("M28", "unique-index race answers 503 instead of 409", "Application/OrderService.cs",
     "throw new AppException(ErrorKind.Conflict, \"idempotency-key-conflict\",",
     "throw new AppException(ErrorKind.Unavailable, \"idempotency-key-conflict\","),
    ("M29", "lost concurrency race does not clear the change tracker", "Application/OrderService.cs",
     "// Forget everything we loaded and start over: the next round sees the new stock or the new order.\n"
     "                db.ChangeTracker.Clear();",
     "// (mutated: tracker not cleared)"),
    ("M30", "retry cap lowered to 1 attempt", "Application/OrderService.cs",
     "public const int MaxAttempts = 8;", "public const int MaxAttempts = 1;"),
    # --- ETag / If-Match
    ("M31", "If-Match is never enforced", "Application/OrderService.cs",
     "if (!ifMatch.MatchesStrong(ETags.ForVersion(order.Version)))",
     "if (!ifMatch.MatchesStrong(ETags.ForVersion(order.Version)) && ifMatch.Any && !ifMatch.Any)"),
    ("M32", "weak ETags satisfy If-Match", "Application/Preconditions.cs",
     "_tags.Exists(t => !t.Weak && t.Value == etag)", "_tags.Exists(t => t.Value == etag)"),
    ("M33", "If-None-Match uses strong comparison", "Api/Endpoints.cs",
     "ifNoneMatch.MatchesWeak(etag)", "ifNoneMatch.MatchesStrong(etag)"),
    # --- listing and limits
    ("M34", "id tie-breaker removed from the sort", "Application/OrderService.cs",
     ".OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)", ".OrderByDescending(o => o.CreatedAt)"),
    ("M35", "list is oldest first", "Application/OrderService.cs",
     ".OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)", ".OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)"),
    ("M36", "createdAfter becomes inclusive", "Application/OrderService.cs",
     "orders.Where(o => o.CreatedAt > after)", "orders.Where(o => o.CreatedAt >= after)"),
    ("M37", "status filter ignored", "Application/OrderService.cs",
     "orders = orders.Where(o => o.Status == status);", "orders = orders.Where(o => o.Status == status || true);"),
    ("M38", "page size cap raised from 100 to 1000", "Application/Validation.cs",
     "public const int MaxPageSize = 100;", "public const int MaxPageSize = 1000;"),
    ("M39", "quantity 0 accepted", "Application/Validation.cs",
     "if (line.Quantity < 1 || line.Quantity > MaxQuantity)", "if (line.Quantity < 0 || line.Quantity > MaxQuantity)"),
    ("M40", "merged quantity cap removed", "Application/Validation.cs",
     "if (total > MaxQuantity)", "if (total > MaxQuantity * 100)"),
    ("M41", "request body limit raised from 64 KiB to 1 MiB", "Api/RequestBody.cs",
     "public const int MaxBytes = 64 * 1024;", "public const int MaxBytes = 1024 * 1024;"),
    ("M42", "UTF-8 BOM no longer stripped", "Api/RequestBody.cs",
     "if (bytes.StartsWith(Utf8Bom))", "if (bytes.StartsWith(Utf8Bom) && bytes.Length < 0)"),
    # --- time and precision
    ("M43", "createdAt not normalised to UTC", "Domain/Order.cs",
     "CreatedAt = createdAt.ToUniversalTime(),", "CreatedAt = createdAt,"),
    ("M44", "createdAt stored with millisecond precision (201 body vs stored value)", "Infrastructure/OrdersDbContext.cs",
     "v => v.UtcTicks,", "v => v.UtcTicks / 10_000 * 10_000,"),
    # --- problem documents
    ("M45", "problem documents lose the machine-readable code", "Api/Problems.cs",
     "problem.Extensions[\"code\"] = code;", "_ = code;"),
    ("M46", "conflict answers 422 instead of 409", "Api/Problems.cs",
     "ErrorKind.Conflict => StatusCodes.Status409Conflict,", "ErrorKind.Conflict => StatusCodes.Status422UnprocessableEntity,"),
]


def kill_tree(proc):
    if os.name == "nt":
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)], capture_output=True)
    else:
        proc.kill()


def run_tests():
    """Returns (status, failing test names). Only the process tree started here is ever killed."""
    proc = subprocess.Popen([DOTNET, "test", "--no-restore", "-nologo"], cwd=ROOT, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True)
    try:
        out, _ = proc.communicate(timeout=TIMEOUT_SECONDS)
    except subprocess.TimeoutExpired:
        kill_tree(proc)
        proc.communicate()
        return "timeout", []
    if re.search(r"error CS\d+", out):
        return "compile-error", []
    failing = sorted({re.sub(r"\(.*$", "", m) for m in re.findall(r"^\s+Failed (\S+)", out, re.M)})
    return ("killed" if proc.returncode != 0 else "survived"), failing


def main():
    args = sys.argv[1:]
    if "--list" in args:
        for mid, desc, fname, _old, _new in MUTATIONS:
            print(f"{mid} | {desc} | {fname}")
        return
    wanted = set(args)
    originals = {}
    results = []
    try:
        for mid, desc, fname, old, new in MUTATIONS:
            if wanted and mid not in wanted:
                continue
            path = os.path.join(SRC, *fname.split("/"))
            if fname not in originals:
                with open(path, "rb") as f:
                    originals[fname] = f.read()
            text = originals[fname].decode("utf-8").replace("\r\n", "\n")
            if text.count(old) != 1:
                label = "BROKEN (pattern not found)" if old not in text else "BROKEN (pattern not unique)"
                results.append((mid, desc, label, []))
                print(f"{mid} | {desc} | {label}", flush=True)
                continue
            with open(path, "w", encoding="utf-8", newline="\n") as f:
                f.write(text.replace(old, new, 1))
            try:
                status, failing = run_tests()
            finally:
                with open(path, "wb") as f:
                    f.write(originals[fname])
            label = {"killed": "KILLED", "survived": "SURVIVED", "compile-error": "INVALID (does not compile)",
                     "timeout": "KILLED (timeout)"}[status]
            results.append((mid, desc, label, failing))
            print(f"{mid} | {desc} | {label} | {len(failing)} failing: {', '.join(failing[:3])}", flush=True)
    finally:
        for fname, data in originals.items():
            with open(os.path.join(SRC, *fname.split("/")), "wb") as f:
                f.write(data)
    valid = [r for r in results if not r[2].startswith(("BROKEN", "INVALID"))]
    killed = sum(1 for r in valid if r[2].startswith("KILLED"))
    print("\n| id | mutation | result | failing tests (first 3) |")
    print("|---|---|---|---|")
    for mid, desc, label, failing in results:
        print(f"| {mid} | {desc} | {label} | {', '.join(failing[:3])} |")
    print(f"\n{killed}/{len(valid)} valid mutants killed; "
          f"{len(results) - len(valid)} broken or invalid")


if __name__ == "__main__":
    main()
