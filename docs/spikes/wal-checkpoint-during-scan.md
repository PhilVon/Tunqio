# WAL checkpoint stalls during a library scan

Run on 2026-09-11, following the note left in `UpsertBenchmarks` when PR #13 moved the E3-S2 upsert budget to the
benchmark gate. That benchmark saw 5 of 30 iterations take between 1.04 s and 1.15 s against a 50 ms median on
the `windows-2025-vs2026` runner, worked around it by checkpointing in `[IterationCleanup]`, and left the
question open: **does the same thing produce user-visible stalls during a real library scan?**

Short answer: **the stall never reaches the library's readers, which is the part that would have mattered. It
reaches the scan's own progress readout and anything else that wants the writer. No change is recommended; the
property worth keeping is now pinned by
[`ScanWriterStallTests`](../../tests/Tunqio.Library.Tests/Scanning/ScanWriterStallTests.cs).**

## The mechanism

`LibraryDatabase.OpenCore` sets `journal_mode = WAL` and every connection gets `synchronous = NORMAL`, so a
commit does not fsync — the write-back to `library.db` happens at a checkpoint instead. SQLite runs one
automatically once the WAL passes `wal_autocheckpoint` frames, which is 1000 pages (4 MB at this schema's page
size) and is left at its default. `SqliteTrackRepository.UpsertBatchAsync` writes about 1.1 MB of WAL per
500-track batch, so a checkpoint falls due about every seventh batch of an import, inside that batch's commit,
with `AcquireWriterAsync`'s lease still held.

## What was measured

Everything below is the development machine (Release, x64, the SQLite bundled with Microsoft.Data.Sqlite), and
the first thing to say about it is that **the stall does not reproduce here at all**:

| Run | Batch median | p95 | Max | Over 1 s |
|-----|--------------|-----|-----|----------|
| 30 × 500 new tracks on the 100k database, checkpointing on (the benchmark's loop without its cleanup) | 59.8 ms | 90.8 ms | 108.9 ms | 0 of 30 |
| The same with `wal_autocheckpoint = 0` | 53.9 ms | 67.7 ms | 85.5 ms | 0 of 30 |
| Scanner, 20 000 files into an empty library | 60.2 ms | 86.0 ms | 101.1 ms | 0 of 40 |
| Scanner, 20 000 files into the 100k library | 62.7 ms | 84.6 ms | 89.5 ms | 0 of 40 |

So on this storage a checkpoint costs about 3–6 ms per batch amortised, and the CI observation is a property of
the runner's disk, not of the code. Timed on its own, a checkpoint here is mostly a fixed cost:

| Pages in the log | Checkpoint | Per page |
|------------------|-----------|----------|
| 193 | 10.8 ms | 56.2 µs |
| 427 | 16.1 ms | 37.6 µs |
| 730 | 19.9 ms | 27.2 µs |
| 1 267 | 25.0 ms | 19.8 µs |
| 2 528 | 33.1 ms | 13.1 µs |
| 4 978 | 58.1 ms | 11.7 µs |
| 8 222 | 68.7 ms | 8.4 µs |

That is roughly **9.5 ms fixed plus 7.2 µs per page** — the default 1000-page checkpoint costs about 17 ms here,
two thirds of it fixed. The number matters for the remedies below.

### Transplanting the runner's cost

Since the disk will not produce it, the cost was injected instead: every seventh batch holds the writer lease
for a further 1050 ms, which is where a real checkpoint's cost lands. Same scan, 20 000 files into the 100k
library, with the shell simulated beside it — a Tracks page and a search every 25 ms, and a rating written
every 200 ms.

| | Without the injected stall | With it |
|---|---|---|
| Whole scan | 3.49 s (5 737 files/s) | 8.28 s (2 414 files/s) |
| Upsert batch, median / p95 / max | 62.7 / 84.6 / 89.5 ms | 53.5 / 1 105 / 1 139 ms |
| **Tracks page read, p95 / max** | **67.9 / 73.5 ms** | **63.7 / 74.4 ms** |
| **Search, p95 / max** | **10.8 / 12.6 ms** | **9.5 / 42.8 ms** |
| Gap between progress reports, max | 306 ms | 1 313 ms (5 gaps over 1 s) |
| UI tag edit, median / max | 19.5 / 123 ms | 23.9 / 1 057 ms (4 of 13 over 1 s) |

## What a user would see

**Reading the library is untouched.** This is the headline: a checkpoint is a write, and in WAL a write never
holds up a reader. Browsing, scrolling, sorting and searching go on answering in tens of milliseconds for the
whole second the writer is stuck — the read numbers above are the same to within noise with and without the
stall. Nothing hitches.

**The scan's progress readout freezes and then jumps.** The pipeline's channels are bounded (64 directories,
16 read batches — under two batches of slack), so the Enumerate and ReadTags stages fill them and stop within
about 150 ms of the writer stalling, and `ScanRun.Report` has nothing to report until it resumes. About one
second of frozen counters per 3 500 tracks imported, on storage where a checkpoint costs that much. It is
honest — the scan really is stalled — and it is the mildest of the three symptoms.

**Anything else that wants the writer waits behind the batch.** Rating a track, editing tags, a play-count
write: `AcquireWriterAsync` serialises them all, so a UI write issued during a stalled batch takes the rest of
that batch plus its own work — 1 057 ms at worst above, against 123 ms during an ordinary scan. This is the
one a user could actually feel, and note that the 123 ms is there without any checkpoint: a UI write during a
scan always waits for a batch. That is a property of the writer lease, not of checkpointing.

**Throughput.** A 100 000-track import pays about 29 checkpoints; on storage where each costs a second that is
half a minute added to a scan that would already be taking several. The work is not wasted — it is the same
write-back, just bunched.

## The three remedies, and why none is taken

**Checkpoint deliberately at a point where a pause does not matter.** To get the checkpoints out of the batches
you have to turn autocheckpoint off for the scan, and then the WAL grows unchecked: measured at 33 MB over 31
batches, so a 100 000-track import would carry a ~210 MB WAL and one checkpoint of all of it at the end. On the
storage that makes a 1000-page checkpoint cost a second, a 52 000-page one is not a pause that does not matter,
and it lands exactly when the user has been told the scan finished. It also leaves every dirty page for a
single fsync, so a crash mid-import has the lot to replay. Worse on every axis.

**Tune `wal_autocheckpoint` down** so the stalls are shorter. The cost is not proportional to the work: about
9.5 ms of the 10.8 ms a 193-page checkpoint takes here is fixed. Halving the threshold halves the pages per
checkpoint but doubles the number of them, so the fixed part is paid twice as often — more total checkpoint
time for a stall that barely shortens. On a runner where the fixed part is fsync latency rather than 9.5 ms of
software, the split is likely more fixed, not less, which only strengthens this.

**Tune it up, or batch differently.** Fewer, longer freezes, a larger WAL, and the same total write-back.
Trading five one-second pauses for one five-second pause is not an improvement.

**Do nothing.** The symptom that would have justified a change — a UI that hitches while the library is being
scanned — does not exist: reads are isolated by WAL and measured to be so. What remains is a progress counter
that pauses on slow storage, and a background write that occasionally waits a second. Neither is worth
restructuring the scanner for, and every available lever makes something else worse.

## What is now covered

`ScanWriterStallTests.A_stalled_upsert_batch_does_not_slow_the_librarys_readers_Async` runs a 5 000-file scan,
holds the writer for a second on the fourth batch, and asserts that the library's reads go on completing inside
that window and none of them waits on it. The stall is injected rather than provoked, so the test asserts the
isolation property on any machine instead of measuring the runner's disk. Its output line also records the
longest gap between progress reports and the count of reads that got through, which is where the numbers in
this document would show a regression.

The benchmark side is unchanged: `UpsertBenchmarks` still checkpoints in `[IterationCleanup]`, for the reason
PR #13 gave — the claim it gates is one upsert of 500 tracks, not sustained import throughput.
