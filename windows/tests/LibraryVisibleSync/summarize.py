"""Pool all raw samples and enforce the original no-op acceptance thresholds."""
import json
import math
import pathlib
import statistics

here = pathlib.Path(__file__).resolve().parent
runs = [json.loads((here / "obj" / f"comparison-{i}.json").read_text()) for i in (1, 2)]
assert runs[0]["source"] == runs[1]["source"], "Source changed between processes"
assert [run["order"] for run in runs] == ["before-first", "after-first"]
summary = []
for size in (0, 12, 1000, 10000):
    for algorithm in ("Before", "After"):
        rows = [row for run in runs for row in run["results"]
                if row["size"] == size and row["algorithm"] == algorithm]
        assert len(rows) == 2 and all(row["notifications"] == 0 for row in rows)
        samples = [sample for row in rows for sample in row["samples"]]
        assert len(samples) == 40
        times = sorted(sample["msPerOperation"] for sample in samples)
        row = {
            "size": size, "algorithm": algorithm, "samples": len(samples),
            "minMs": min(times), "medianMs": statistics.median(times),
            "p95Ms": times[math.ceil(len(times) * .95) - 1], "maxMs": max(times),
            "medianBytes": statistics.median(sample["bytesPerOperation"] for sample in samples),
            "maxBytes": max(sample["bytesPerOperation"] for sample in samples),
            "notifications": 0,
        }
        if size == 10000 and algorithm == "After":
            assert row["medianMs"] <= 2, row
            assert row["p95Ms"] <= 3, row
        summary.append(row)
        print(json.dumps(row))
(here / "obj" / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
