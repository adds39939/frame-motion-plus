# Phase 2 join: CUDA/Vulkan interop and `fmp pattern`

Gate (`docs/implementation-plan.md`, Phase 2): **the interop round trip passes** (CUDA writes a pattern into an
exported buffer and Vulkan reads it back bit-exact, both formats; semaphore handshake in both directions; the
validation layer reports nothing) and **`fmp pattern` runs clean under the validation layer**. The jitter part of the
gate is in [`phase2-jitter.md`](phase2-jitter.md).

Machine: RTX 5090, driver 620.70.06 (CUDA driver API 13.5, Vulkan 1.4), Plasma 6.8 Wayland, DP-3 4K 240 Hz and
HDMI-A-1 4K 120 Hz, both HDR. .NET 10.0.114 SDK; `fmp` is the NativeAOT binary from
`dotnet publish src/FrameMotionPlus.App -c Release -r linux-x64`. All numbers below were measured with the presenter
after the Phase 2 review fixes (retained dropped writes, stride releases, shader-access synchronization validation,
the 500-release bounds) and the bridge after the join review fixes (back-pressure on finished work, numbering that
never restarts on a ring, the import reaper). Reviewers were running GPU tests on the same machine at earlier times.

## Verdict

**Both automated gate items are met.**

- CUDA imports the presenter's ring (one opaque-FD memory object per slot, both timeline semaphores) and draws into
  the slots; Vulkan presents them 1:1 to an offscreen target and the read-back is **bit-exact** against an independent
  CPU reference, in **RGBA8 and RGB10A2**, at nine shapes from 333x97 to 3840x2160, including a width whose pitch is
  only 4-byte aligned (1277 px, tight pitch 5108 B) and one with a pitch alignment of 12 B.
- The **semaphore handshake works in both directions through real work, and each direction has a control** that removes
  the synchronisation and shows the test then sees the failure.
- **`fmp pattern` runs clean under `VK_LAYER_KHRONOS_validation` with synchronization (incl. shader-access) checks**,
  offscreen and windowed, 8- and 10-bit, with and without the 2x blend generator in the loop: 0 messages, every
  offscreen frame verified bit-exact, 0 dropped, 0 bytes allocated on the present thread.
- The produce → signal → present path costs **about 0.05 to 0.15 ms of CPU per frame at p50 and 0.17 to 0.29 ms at
  p99** on this (busy) desktop, offscreen and windowed (tables below; the windowed maximum per 3 s run measured 310 to
  592 µs over all the runs recorded, depending on what else the desktop was running: see "Windowed"), and **about
  35 µs (1440p) to 65 µs (4K) of GPU time**. The rare
  1.6 to 2.3 ms CPU outliers of the offscreen mode are the NVIDIA driver's own (every 1,024th `vkQueueSubmit2` is
  slow), not ours, and do not occur when frames are actually presented to a window (see "Per-frame cost").
- **Back-pressure is on finished work and cannot wedge** (join review): the bridge's lead is the writes handed out
  that the GPU has not finished (handed out − *written*), held under a limit (2N by default, at most 250) chosen so the
  producer's CUDA stream never fills and the presenter's bounds are never reached. After a producer or presenter stall
  of any length the loop resumes by itself with correct frames; a slow producer is held back instead of queueing;
  within the limit enqueueing never blocks on a full stream, and the bridge never makes a context-synchronising free
  that could block it while a bridge is live (see "Frees"); write numbers never restart on a ring; and no timeline is
  ever signalled out of turn (see "Back-pressure").

## What the bridge does

`FrameMotionPlus.CudaVulkan.CudaVulkanBridge` (Linux) is the `IGpuBridge`. `Configure` has the presenter create its
ring, then exports it (fresh FDs) and imports into the CUDA context: per slot `cuImportExternalMemory` (opaque FD,
size = the slot's allocation size, dedicated flag) and `cuExternalMemoryGetMappedBuffer` at offset 0; both timeline
semaphores with `cuImportExternalSemaphore` (timeline FD). Before importing anything it checks that the ring's Vulkan
device UUID is the CUDA device's (`GpuDeviceMismatchException` otherwise). `TryAcquireSlot` (or `AcquireSlot`)
hands out write n = 1, 2, … (on a fresh ring) in slot (n − 1) mod N, allocation-free, never blocking, and only while
the writes handed out that the GPU has not finished stay within the limit (see "Back-pressure"), stamped with the
write number and the bridge's configuration generation:

| `BridgeSlot` member | Domain | Value |
| --- | --- | --- |
| `ProducerWait` (= `ProducerView.Ready`) | CUDA timeline | *released* ≥ n − N (none while n ≤ N) |
| `ProducerView` | CUDA | the mapped device pointer, pitch, format, colour |
| `ProducerSignal` | CUDA timeline | *written* := n |
| `ConsumerView`, `ConsumerWait` | Vulkan | the slot's `VkBuffer`, *written* ≥ n (what `TryPresent` takes) |
| `ConsumerSignal` | Vulkan timeline | *released* := n (the presenter signals it) |

So a CUDA generator handed `ProducerView` as `output` and `ProducerSignal` as `done` waits for the slot's release and
signals the write by itself. Writes must be signalled in order (one stream, or several where each waits for the
previous write's *written* point before signalling; the Core contract, "Order"; a violation is noticed on a
best-effort basis only, never prevented): `fmp pattern --blend` puts real-frame copies and generated frames on one stream for that reason, with a
CUDA event per history buffer as the source frames' `Ready` and the source stream waiting on the *written* point of a
buffer's last reader before redrawing it.

A size or format change goes through `Configure` too. It validates the new shape first (a refused `Configure`
changes nothing), releases every write handed out through the presenter (in calls it accepts), polls *written* from
the host until all of them have finished (bounded, `DrainTimeoutNs`, 2 s by default), frees the CUDA memory imports
(before the Vulkan objects they refer to), has the presenter replace its ring and imports the new one; the
generation grows. If the drain does not finish in time it throws `TimeoutException` and leaves the current ring
imported and usable. Write numbers never restart on a ring: a fresh ring starts at 1, and a ring imported again (a
new bridge on the same presenter, or a `Configure` that keeps the ring) continues after the highest write it has
seen (from the ring's own counters, the presenter's and a per-ring record of what bridges handed out). The old ring's
semaphores stay alive until the bridge is disposed, so the driver cannot give their handles to the new ring while a
stale slot still names them. That is a bound that grows with use: two CUDA semaphore imports per ring replacement,
kept until the bridge is disposed (300 replacements on one bridge showed a flat `Configure` cost and unchanged GPU
memory), so a bridge reconfigured without end should be replaced now and then. A slot from an earlier generation is refused by `CreatePresentRequest` and reported by
`IsCurrent`; a presenter reconfigured behind the bridge's back is caught by the next acquire.

Dispose does the same drain and never throws or hangs. It releases from the CUDA side only once the presenter no
longer owns the ring (then nothing else can signal *released*, so the timeline cannot go backwards), and it never
forces *written*. If the work has not finished in time (a slow producer, or writes waiting for releases that only
come once the presenter lets go), it hands the import, and the retired semaphores, to a background reaper instead of
freeing memory that queued work still uses (`cuMemFree` would wait for that work, possibly for ever).

**The reaper frees nothing while any bridge is live on the import's CUDA context** (final review). On this driver,
while a free waits for queued work, kernel launches, event records and semaphore enqueues on every other thread
block as well. The final reviewer found the consequence: after an abandoning Dispose, a new bridge on the same ring
ran a single-threaded loop that enqueued ahead of its presents; its first write carried *written* past the abandoned
one, the reaper freed within 5 ms, the loop's next enqueue blocked behind that free, and the present that would have
released the queued write never came. With the producer N + 1 writes ahead it deadlocked every run, N ahead about one
run in nine (in our port of the test: 17 of 30 cycles), one write at a time (as `fmp pattern` runs) never. Now the
reaper counts live bridges per native context handle (from construction to the end of Dispose) and holds an import,
even one whose work is done, until none is live; checking the count and freeing happen under one lock, so a bridge
created meanwhile waits for that free, which by then has nothing left to wait for. Holding costs one ring's imports
per abandoned bridge, on an error path.

It also never queues a GPU-side wait. While the presenter owns the ring it polls *written* from the host (through a
read of the ring's counter that is safe against the presenter destroying the ring); once *written* has reached the
last write handed out, the work is done. A GPU-side wait that cannot complete would make every free in the context
block: in the re-review, the reaper's old wait for a write that was never signalled made an unrelated 1 MiB
`cuMemFree` block, and stream creation, kernel enqueues and allocations on other threads with it, until the presenter
was disposed, so a teardown in the natural order (bridge, CUDA buffers, presenter) hung for good. Once the presenter
has let go of the ring, nothing else will signal *released*, so the reaper releases from CUDA if the presenter had
not (never to a lower value: not when a later bridge handed out writes on that ring, which the final review caught
signalling 2 where the presenter had released 8), and then frees: every write's queued work can finish by then (a
write that was never signalled has none), and the free's own wait covers what is still running. The earlier version
waited GPU-side for *written* there, which after a failed release through the presenter and a write that never came
left a 1 MiB free spinning for good. The reaper's stream for that release is created at its first look, so a CUDA
context object disposed right after the presenter does not stop it. An entry whose CUDA context is gone (destroyed,
deinitialised, a sticky error, or the context object disposed before the reaper's first look when it then needs that
stream) is dropped and counted (`DroppedReaps`), not polled for ever. `AbandonedImports` and
`CudaVulkanBridge.PendingReaps` count the rest, and `fmp pattern` fails if one is still pending 3 s after its
presenter has been disposed, when its bridge is long gone and nothing can hold a finished import any more.

**FD ownership.** Every FD from `Export()` is owned by the export until an import succeeds; the bridge marks it
transferred right after each successful import, and the export's disposal closes exactly the ones never transferred
(all of them on a device mismatch, the rest after a failure part-way). Joining the two halves showed that the
driver does not keep the documented rule itself: **a failed `cuImportExternalMemory` closes the descriptor it was
given** (seen with descriptors that are not GPU memory; a failed semaphore import leaves it open). A caller following
the documented rule would then close a recycled descriptor number. `CudaExternalMemory.ImportOpaqueFd` and the
semaphore imports now hand the driver a private duplicate (high in the descriptor table, where ordinary opens do not
land), close it after a failure if the driver did not, and close the caller's descriptor after a success, so the rule
holds whatever the driver does. Also seen: the driver accepts an opaque FD import with the wrong size (even 1 TB) or a
semaphore FD imported as memory without complaint, so sizes and kinds must come from the exporter, as they do here.

## Back-pressure (join review fix)

The join review found three defects in the first back-pressure: the bridge measured its lead against the presenter's
*submitted* releases rather than finished work, so a producer stall let it hand out more writes than the presenter's
waiting bound allows; the presenter refused those (`RejectedRequests`) without retaining or releasing them, and from
then on the bridge refused every acquire (Outstanding stuck at 64 of 64, the pipeline wedged). A second bridge on a
ring with history restarted numbering at 1. And the CUDA stream could fill before the limit, so "never blocks" was not
true. The bridge now works like this:

| Number | Value | What it is |
| --- | --- | --- |
| Presenter bounds, `RingProtocol` | at most 500 release submissions waiting for unfinished writes (`MaxWaitingReleases`); a request at most 500 × N writes past the last release (`MaxWriteLead`, 2,000 at N = 4), so one call submits at most 499 catch-up releases | Protect the device: a present behind 511 waiting releases lost it on a queue that had never run a command buffer, about 6,550 on one that had (Vulkan review). A request past them is refused (`RejectedRequests`), and a refused write is neither shown, retained nor released. |
| Bridge limit, `CudaVulkanBridgeOptions.MaxLead` | 2 × N by default (8 at N = 4); at most 250 (`MaxLeadCeiling`, half of 500) and at most 960 / `OperationsPerWrite` (16 by default) | A hard limit on `Outstanding` = writes handed out − the ring's *written* value, i.e. writes whose GPU work has not finished. `TryAcquireSlot` says no beyond it; `AcquireSlot` throws. `Configure` refuses options that break either cap. |
| Guidance (Core, `IGpuBridge`) | about N | A pipeline following the design doc ("no queued frames anywhere") keeps its lead within about N and skips frames at the source. `fmp pattern` runs at a lead of 1. |

**Why it cannot wedge.** A producer stall, a slow GPU and a stalled presenter (whose missing releases stop the
producer's writes) all stop *written*, so all of them hold the producer back at the limit; when the stall ends, the
queued work runs, *written* catches up and acquiring resumes, with nothing to reset. And the presenter can never refuse
a request from the bridge: every write handed out is at most `MaxLead` ≤ 250 past *written*, and *written* is at most
N past *released*, so a request is at most N + 250 ≤ 500 × N past the last release, and the release submissions
waiting for unfinished writes (distinct values between *written* and the newest write handed out) are at most 250,
half the waiting bound. Stale slots never become requests (`CreatePresentRequest` refuses them). So in `fmp pattern`
a non-zero `RejectedRequests` is a bug in the bridge or the loop, and fails the run.

**Why enqueueing does not block on a full stream.** A CUDA stream on this driver holds about 1,024 queued
operations; past that the enqueue call itself blocks the CPU until the GPU drains the queue (writes of wait + kernel +
signal blocked at the 341st write; the reviewer's wait + two kernels + signal at the 259th). The limit times the
operations a producer declares per write (`OperationsPerWrite`) stays within 960. Measured (I3 below): 60 writes of 16
operations each behind 20 ms of GPU work per write, worst acquire + enqueue 0.1 ms; the old bridge blocked for 20.0 ms.

**Frees.** The limit is not the only thing an enqueue can wait for: while a call that synchronises the context
(`cuMemFree`, `cuMemFreeHost`, destroying an external semaphore) waits on another thread for queued work, every
enqueue blocks too (measured by the final reviewer: kernel launches, event records and semaphore wait and signal
enqueues each blocked for the full 2 s bound). The bridge makes such calls only inside its own `Configure` (replacing
the ring) and Dispose, on the caller's thread after its own writes have drained, and through the reaper only while no
bridge is live on the context. So within the limit a producer's enqueues never block, provided nothing else frees in
that context during playback: not the application (whose frees stay off the playback period, as off the present and
pacing threads) and not a second pipeline on the same context, whose bridge's `Configure` or Dispose would free while
this one runs (Phase 2 has one pipeline).

**Reading *written*.** `Outstanding` and the refusal path read the ring's *written* value with
`vkGetSemaphoreCounterValue` (a host query, allocation-free, thread-safe); `TryAcquireSlot` skips it while a value it
has already seen proves there is room. If a read is lower than an earlier one, writes were signalled out of order: the
bridge counts it (`OutOfOrderSignals`, which `fmp pattern` fails on) but cannot prevent it. Detection is best effort:
the counter is only compared when the bridge reads it, so a backwards step undone before the next read goes unseen; 0
does not prove the order was kept (the Core contract and the bridge's documentation say so).

**Measured, against the invariants the review set** (all in `ReviewRegressionTests`, N = 4, limit 8, a loop that
acquires, enqueues (wait + pattern + signal) and presents each tick with no readiness gate and no retry of a refusal;
"old" is the bridge the review rejected, run against the same tests):

| Invariant | Scenario | New bridge | Old bridge |
| --- | --- | --- | --- |
| I1 resumes after a stall | the review's: GPU stalled 2.5 s while the loop runs 800 frames at full speed, then 240 frames | 8 acquired during the stall (792 held back), then 240 of 240 acquired and shown, correct | 564 acquired, 63 rejected, then 0 of 240 acquired: wedged |
| I1 | producer stall 0.5 / 2.5 / 10 s at 240 Hz | in the last second 240 acquired, 240 shown; lead at most 8, 0 at the end; 113 / 593 / 2,392 held back | passes (its enqueue blocked inside CUDA during the stall instead, see I3) |
| I1 | presenter stall 0.5 / 2.5 / 10 s at 240 Hz (producer goes on, newest write in a mailbox) | the same: 240 / 240 in the last second, lead at most 8, 0 at the end; 109 / 589 / 2,389 held back | – |
| I2 slow producer held back | 6 ms of GPU work per write at 240 Hz, 600 ticks | 421 acquired, 179 held back, backlog at most 8 of 8 | backlog 185 writes past a limit of 64 |
| I3 no blocking in CUDA | limit 60, 16 operations per write, 20 ms GPU per write, 2 s | worst acquire + enqueue 0.097 ms (159 acquired, 1,885 held back) | 20.0 ms: blocked |
| I4 numbering monotonic | 25 writes, then a new bridge, or a `Configure` refused for width 0, then 40 more | writes continue at 26, *written* never goes down, all 40 frames bit-exact | restarted at 1 (and the refused `Configure` had destroyed the import) |
| I5 no rejected request | every test above, plus `fmp pattern --unpaced` | 0 rejected | 63 rejected in the stall scenario |

**Catch-up cost.** Presenting the newest write after a lead at the limit catches up one release per N writes: 0.8 ms
for 12 writes (2 steps), 1.7 ms for 33 (8 steps) and 1.6 ms for 254 (63 steps, the 250 ceiling) under validation. The
presenter's own worst case, 499 steps for a request 2,000 writes ahead, cannot be reached through the bridge.

**The hang, exactly** (first review). Under gdb with the reviewer's scenario on the code before that fix, the test
thread was spinning in `cuMemFree_v2`, called from the bridge's Dispose freeing the slot mappings while the producer's
kernels for writes 5 to 33 were blocked on *released* values that could no longer be signalled. Destroying an external
semaphore synchronises the context in the same way (found during this fix: disposing a retired semaphore hung behind a
GPU-side wait that could not complete), so the bridge now never frees an import or a semaphore while work against them
may be stuck, and waits for its drains from the host, so a drain that times out leaves no GPU-side wait behind; what
cannot be freed in time goes to the reaper.

## What was proven, and how

All in `tests/FrameMotionPlus.Gpu.Tests` (categories `Gpu` and `Display`; they skip, never fail, without the hardware),
every test under the validation layer with synchronization checks, failing on any message, including objects the layer
finds leaked when the device and instance are destroyed. Compared with an independent CPU reference written from the
kernel specifications (the one the CUDA tests use).

- **Round trip, bit-exact** (`RoundTripTests`): CUDA draws the test pattern into ring slots, two laps so every slot is
  reused behind a real release wait; Vulkan presents 1:1 offscreen; read-back equals the reference for RGBA8 and
  RGB10A2 at 640x360, 1277x719 (default pitch 5120 B; tight pitch 5108 B), 333x97 (pitch alignment 12), 2560x1440 and
  3840x2160.
- **written, CUDA to Vulkan** (`HandshakeTests`): CUDA writes a slot behind a 250 ms GPU delay and the CPU presents at
  once (*written* is still behind when the request goes in); the read-back is right and completes only after the
  delay. **Control:** signalling *written* before the write: the presenter reads the slot unwritten.
- **released, Vulkan to CUDA:** CUDA laps the ring before the presenter has even been handed the slot's previous write;
  the lap stays blocked GPU-side (*written* does not advance) and the presenter shows the old write intact, then the
  lap runs. **Control:** without the release wait CUDA overwrites the slot the consumer still owns and the presenter
  shows the lap instead.
- **Generators straight into the ring** (`GeneratorTests`): `BlendGenerator` and `RepeatGenerator` through
  `IFrameGenerator.Enqueue` with `output` = the slot's producer view and `done` = its written signal, sources with
  their own pitch and CUDA-event `Ready`, ten timesteps and a discontinuity, both formats, bit-exact; and real frames
  alternating with blends in one ring (the `--blend` arrangement).
- **Hundreds of frames with reuse** (`StressTests`): 600 frames per format with the producer a full ring ahead (write
  i + N is enqueued before write i is presented, so it waits in the very slot about to be read); every frame compared
  whole, and *written* checked to be below i + N before each present. 2000 frames with forced refusals: a full-speed
  producer, held back by the bridge while the GPU is behind and retrying, never stalls (each acquire succeeds within
  2 s; in one run 447 shown, 1,553 dropped, about 146,000 refused polls), and nothing is rejected.
- **Ring recreation** (`RecreationTests`): 48 format and size changes with two writes in flight each time (one behind a
  GPU delay); after a warm-up lap of shapes the device/anon-inode descriptor count, the bridge's live CUDA objects and
  the process's GPU memory (nvidia-smi) are unchanged, and the validation layer reports no leaked Vulkan object.
  Reconfiguring with three laps of writes queued (most blocked on *released*) drains without deadlock.
- **Producer lead** (`LeadTests`): the presenter stalled while the producer hands out 3N writes at the default limit,
  33 (the first review's count) and 254 (the 250 ceiling, 3 operations per write), exactly up to the limit; the
  newest is then presented, bit-exact, and the lead returns to 0; `TryAcquireSlot` refuses at the limit (`AcquireSlot`
  throws, naming `TryAcquireSlot`), hands out nothing while refusing and again after a present or after a `Release`;
  `Configure` and Dispose drain the maximum lead; `Configure` refuses a limit that could fill the stream or reach the
  presenter's bounds (61 × 16 operations, 251, 1 × 961); and a presenter whose releases fail does not hang anything:
  `Configure` times out (bounded) with *released* untouched behind the presenter, Dispose returns at once and hands
  the import to the reaper, which frees it once the presenter is disposed. Every blocking call runs under a bounded
  wait that fails the test, and so does the rig's teardown.
- **Join review regressions** (`ReviewRegressionTests`), one per finding, each reproducing the reviewer's scenario
  (the I1 to I5 table above, and): a `Configure` that times out during a 3 s producer stall never signals *released*
  from CUDA while the presenter owns the ring (the old bridge signalled 564 over the presenter's 500, the review's
  `VUID-VkSubmitInfo2-semaphore-03882`), *released* never goes down and the ring keeps working; a stale slot is refused
  by `CreatePresentRequest` and `IsCurrent` (fails with the check removed); over 60 recreations a stale
  `ProducerSignal` never reaches the new ring (the old bridge: 11 handle collisions, the review's 2 in 24); a slow
  producer's import goes to the reaper (Dispose returns in its 2 s timeout, nothing forced) and is freed once its 4.8 s
  of work is done; writes signalled out of order from two streams are counted (`OutOfOrderSignals`). The old bridge
  fails every one of these except the stale-slot check, which it had (its removal is what the test catches).
- **Re-review regressions** (same class). After a Dispose that abandons its import (a slot acquired and never
  signalled) with the presenter alive, a 1 MiB free, a stream creation, a kernel enqueue (and its completion) and an
  allocation on other threads complete at once (each bounded at 3 s, so a block fails the test instead of hanging the
  run; per-call times under "Per-call times" below); a new bridge on the same ring then shows 20 of 20 frames
  bit-exact, from write 5, while the old import stays held, and the import is freed, the presenter still alive, once
  that bridge is disposed; and the natural teardown (bridge, CUDA buffers, presenter, CUDA context object) completes,
  the presenter's Dispose taking its 2 s drain timeout before it forces the write, and the import is freed, not
  dropped. With the reaper's GPU-side wait restored, all four calls block for the full 3 s in both variants and the
  test fails in 14 s. An entry whose context is gone (injected: the driver reports it destroyed) and one whose context
  object was disposed before the reaper's first look are each dropped and counted; with the drop removed, both tests
  fail (the reaper retries for ever).
- **Final-review regressions** (same class). A new bridge on the ring of an abandoned import runs a single-threaded
  loop 150 iterations long, N + 1 writes ahead of its presents (3 cycles), N ahead (10 cycles) and one write at a time
  (3 cycles), each cycle bounded (a loop stuck for 10 s is unstuck from the test thread and fails the test) and ending
  with a frame checked bit-exact and the import freed once the bridge is disposed: no cycle stuck. With the reaper
  freeing as it did, N + 1 ahead stuck in 3 of 3 cycles (after 22 to 45 iterations) and N ahead in 17 of 30. After a
  failed release through the presenter, a write that never came and the presenter's Dispose, a 1 MiB free and a stream
  creation take well under a millisecond and the import is freed; with the old GPU-side wait both blocked the full
  3 s and the rig's teardown hit its 60 s bound. And an abandoned import whose ring a later bridge took to write 8
  before the presenter replaced it is not released from CUDA at all; the old reaper signalled *released* := 2.
- **Per-call times** with an import abandoned (the free, stream creation, kernel and allocation above) depend on
  what else the machine is doing; they are not a bound. On this desktop with no other GPU tests or builds running
  (a chat client and a game launcher idle in the background): 0.07 to 0.61 ms per call over 44 executions in the
  re-review and final rounds; with 32 busy loops on the CPU (32 hardware threads), 3.2 to 4.8 ms; with the whole test
  run pinned to two cores (`taskset -c 0-1`), 0.09 to 0.43 ms. The final reviewer measured 0.06 to 0.88 ms on a quiet
  desktop and 2.9 to 5.4 ms with 32 busy loops. What the test asserts is that none blocks (3 s bound).
- **Zero allocation** (`AllocationTests`): 720 measured frames per format of pattern writes, generator writes, presents,
  feedback and GPU-timer reads at 1280x720 into a 1920x1080 target, acquiring through `TryAcquireSlot` and reading the
  lead, plus, in every measured window, a stall phase that runs the back-pressure path (the presenter stalled, the
  bridge reading *written* and refusing at its limit, one catch-up present): **0 bytes, 0 collections**, with every
  path counted inside the measured windows (720 acquisitions, 720 presents, 360 + 360 writes, 720 release waits, 720
  GPU spans, 360 semaphore waits and 360 signals inside `generator.Enqueue` itself, 36 stall-phase acquisitions and 6
  refusals) so the loop cannot pass by doing nothing. An allocation injected into either semaphore branch of
  `CudaStream`, or into the bridge's refusal path (144 B: 6 refusals), fails it.
- **Failures** (`FailureTests`): a ring on another GPU (CUDA UUID replaced through a test seam; this machine has one
  GPU) is refused at construction or before the first import, with both UUIDs in the message and no descriptor left; an
  injected failure at each import step (slot memory, mapping, either semaphore) leaks nothing and closes nothing twice
  (canary descriptors opened at the moment of failure, which take the numbers CUDA freed, stay open); a descriptor the
  driver rejects stays with its owner; misuse (no Configure, bad format, slot count, presenter reconfigured directly,
  use after Dispose) is refused clearly; a never-signalled write makes `Configure` time out with a clear message, and
  Dispose return within its timeout and hand the import to the reaper, which frees it once the presenter's Dispose has
  forced its own waits; Dispose waits for writes still running; a bridge disposed after its presenter drains and does
  not hang.
- **`fmp pattern`** (`PatternCommandTests`): offscreen runs of both formats with and without blend under validation;
  `--unpaced` runs (no pacing, no readiness gate; with and without blend and readback) that keep going on the
  bridge's back-pressure alone with nothing rejected; a windowed run; the verifier against the reference (and that one
  flipped bit fails it); out of process, SIGINT and SIGTERM in the middle of a run: it stops within about 0.1 s, tears
  down, prints its verdict (PASS) and exits 0; and, out of process with CUDA, the Vulkan driver or Wayland hidden,
  exit 69 and a one-line message. Fresh processes (the JIT host) with no warm-up frames at all, offscreen with and
  without blend and windowed, allocate 0 B from the first measured slot (see "First use after the warm-up" below). A
  producer failure injected between acquiring a write and signalling it (`FMP_PATTERN_FAIL_AT=<slot>`, a fault-injection
  switch read from the environment by `fmp pattern` only and listed in `fmp --help`) fails the run with its cause, the summary reports the slots, time and rate that ran, and
  the teardown completes in its natural order with the import reaped (with the reaper's old GPU-side wait this
  in-process run never returned). The published binary with the same injection exits in 4.8 to 5.2 s (the bridge's and
  the presenter's 2 s drain timeouts), offscreen and windowed. Since the final review, `--validate` also writes each
  validation message to stderr as it arrives, with its id (the Gpu test rig does the same), so a message can no longer
  be counted but lost (one windowed run in the re-review counted one whose text was lost with the output file; 171 of
  the final reviewer's windowed `--validate` runs and all since showed none); and the summary's "max presents in
  flight" is the maximum over the measured slots, no longer the presenter's lifetime maximum, which the pre-roll
  always takes to its cap.

## Per-frame cost of produce → signal → present

`fmp pattern --offscreen --no-verify --frames 1200` (published binary, 120 Hz on `CLOCK_MONOTONIC`, 1:1 offscreen
target the pattern's size, no readback, first 30 frames excluded). GPU times come from CUDA event spans around the CUDA
work and from the presenter's timestamp queries; CPU time is the present thread's wall time from the free-frame-slot
check to the end of `TryPresent` and feedback draining (acquire slot, read the lead, CUDA wait/launch/signal,
present). Every run: 1200 presented, 0 dropped, 0 bytes allocated on the present thread, 0 collections. Re-measured
after the join review fixes on a busier desktop than the first measurement (a chat client using most of a core, a
browser's GPU process; the GPU idling at P3, 787 MHz, between frames): the first measurement had CPU p50 34 to 47 µs and
p99 104 to 134 µs, GPU times about a third lower. The bridge is not the difference: the bridge the review rejected,
built into the same binary and run back to back with the new one, measured the same within noise (p50 93 to 105 µs
against 48 to 108 µs, p99 172 to 209 µs against 176 to 215 µs, three runs each).

| Ring | GPU: pattern into slot | GPU: present slot copy | GPU: present pass | CPU p50 / p99 / max |
| --- | --- | --- | --- | --- |
| 2560x1440 8-bit | 15.4 / 19.4 µs | 8.7 / 9.7 µs | 11.2 / 16.0 µs | 97 / 199 / 1819 µs |
| 2560x1440 10-bit | 15.3 / 19.5 µs | 9.1 / 10.8 µs | 12.1 / 19.3 µs | 113 / 246 / 1809 µs |
| 3840x2160 8-bit | 23.6 / 27.4 µs | 18.2 / 26.5 µs | 21.9 / 28.6 µs | 45 / 173 / 1793 µs |
| 3840x2160 10-bit | 24.3 / 30.2 µs | 19.5 / 30.4 µs | 22.9 / 28.4 µs | 90 / 198 / 1864 µs |

GPU columns are p50 / p99. With the 2x blend generator in the loop (`--blend`: real frames copied into the ring
alternate with `BlendGenerator` output at t = 0.5 written straight into it; per output frame, so a source frame is
drawn every second frame):

| Ring | source draw | real copy to slot | blend into slot | present copy | present pass | CPU p50 / p99 / max |
| --- | --- | --- | --- | --- | --- | --- |
| 2560x1440 8-bit | 14.7 / 19.6 | 21.6 / 25.8 | 14.8 / 18.4 | 8.9 / 10.8 | 11.5 / 14.5 | 114 / 221 / 1841 µs |
| 2560x1440 10-bit | 15.2 / 20.2 | 21.6 / 26.6 | 17.4 / 17.8 | 9.4 / 12.0 | 12.1 / 17.1 | 103 / 233 / 1700 µs |
| 3840x2160 8-bit | 23.8 / 28.3 | 38.8 / 43.3 | 37.2 / 40.1 | 20.6 / 35.4 | 21.9 / 28.7 | 121 / 227 / 1647 µs |
| 3840x2160 10-bit | 24.4 / 29.1 | 38.4 / 44.0 | 37.2 / 40.0 | 21.8 / 37.2 | 23.0 / 31.4 | 123 / 219 / 2139 µs |

GPU columns in µs, p50 / p99. Reading them:

- The whole GPU side of a frame is **about 35 µs at 1440p and 65 µs at 4K** at the GPU's idle clocks, 0.8 to 1.6 % of
  a 4.17 ms 240 Hz period. The interop adds no GPU work of its own (its semaphore waits and signals are queue-level
  operations), and the presenter's copy out of the slot is the design doc's "about 0.1 ms at 4K" at a fifth of that.
- A device-to-device copy of a 4K frame into a slot costs 38 µs (the design doc's "tens of microseconds"), the same as
  the blend; real frames that come out of VFG straight into the ring avoid it.
- The CPU cost is tens of µs to about 0.1 ms per frame and moves with the desktop's load (above); it stays far inside
  a period.
- **The maximum is 1.6 to 2.3 ms in every run**, once or twice per 1200 frames, and it is the driver's. A per-call
  breakdown (JIT build, no validation, 1440p, 1470 frames) put all of it in `VulkanPresenter.TryPresent`, with every
  CUDA and bridge call under 70 µs worst case. The Vulkan reviewer then traced it to the NVIDIA 620.70.06 driver:
  `vkQueueSubmit2` takes 1.56 to 1.68 ms on every 1,024th queue submission (a bare submit loop stalls at submissions
  1017, 2041, 3065, 4089 whatever the command buffers hold, their spacing or the frame size; on-CPU, no context
  switch, no GC). It appears offscreen and when a swapchain exists but is not presented to; in windowed presentation
  the maximum per run was 310 to 592 µs (below). So it distorts this offscreen benchmark mode, not real presentation; to re-measure
  on a direct display later.

Under `--validate` (synchronization checks on) the CPU cost per frame is 250 to 500 µs, all of it the layer's.

## Windowed, on this desktop

`fmp pattern --duration-s 3` (published binary), 2560x1440 ring in a 960x540 window (1392x783 px at the desktop's
scale), FIFO, at most two presents in flight, paced at the display's rate on `CLOCK_MONOTONIC`. The compositor put
the window on the 240 Hz DP-3 panel except for the two plain 8-bit runs, which landed on the 120 Hz HDMI-A-1 output.
10-bit gets an `A2B10G10R10 / HDR10_ST2084` swapchain (passthrough), 8-bit `B8G8R8A8 / sRGB`. Final runs, after the
join review fixes, on the busier desktop described above:

| Run | Slots | Presented | Dropped | Skipped (no free image) | CPU p50 / p99 / max | Allocated |
| --- | --- | --- | --- | --- | --- | --- |
| 8-bit (120 Hz output) | 361 | 361 (120.4/s) | 0 | 0 | 77 / 290 / 311 µs | 0 B |
| 8-bit, again (120 Hz output) | 361 | 361 (120.3/s) | 0 | 0 | 146 / 247 / 422 µs | 0 B |
| 10-bit | 721 | 721 (240.3/s) | 0 | 0 | 113 / 219 / 422 µs | 0 B |
| 10-bit, again | 721 | 720 (240.0/s) | 0 | 1 | 60 / 209 / 378 µs | 0 B |
| 10-bit, `--blend` | 721 | 719 (239.7/s) | 0 | 2 | 97 / 253 / 313 µs | 0 B |
| 10-bit, `--blend`, again | 721 | 721 (240.3/s) | 0 | 0 | 112 / 246 / 330 µs | 0 B |
| 8-bit, `--validate` | 721 | 699 (233.0/s) | 0 | 22 | (validation) | 0 B, 0 messages |
| 10-bit, `--validate` | 721 | 697 (232.2/s) | 0 | 24 | (validation) | 0 B, 0 messages |

Feedback is `PresentWait2`, capping presents in flight at 2; no request was rejected; the bridge's lead never went
above 1 (of 8). Re-run after the re-review fixes (same command, published binary, all four on the 240 Hz output, a chat client
and a game launcher running, no browser): 8-bit 79 / 199 / 318 µs, 10-bit 77 / 199 / 469 µs (3 slots skipped),
10-bit `--blend` 84 / 211 / 525 µs, 8-bit `--blend` 77 / 208 / 427 µs (CPU p50 / p99 / max; 721 slots each, 0 dropped,
0 B allocated).

**The windowed CPU maximum is a measured range, not a bound:** 310 to 422 µs in the final runs above, 318 to 525 µs in
the re-run, and 538 µs and 592 µs in the join reviewer's runs, the 592 µs with a browser active. It moves with what
else the desktop is running (the p50 and p99 move far less); no run recorded so far reached 0.6 ms, which says nothing
about a desktop busier than these. The first measurement (quieter desktop) had p50 48 to 82 µs and p99 172 to 208 µs.
Earlier runs of the same command skipped 0 to 20 slots of 721 in a quiet desktop and up to 82 under concurrent GPU load; under validation (the layer costs about
0.4 ms per frame) about 3 % of slots are skipped.

Nothing is ever dropped. When a refresh slot finds no free image even after waiting half a period, no frame is
produced for it: at those moments the compositor completed this window's presents more slowly than the panel's
240 Hz, and a slot grid on our clock cannot see vblanks. That is the fixed-refresh fallback path the design doc describes; locking
to the display through present feedback and `present_wait2` is Phase 6. GPU times in the window match the offscreen
ones (the pass is cheaper: it scales down to the window).

## Reproducing

```sh
fmp pattern --offscreen --validate --format 10 --blend --frames 600          # the gate, any size/format
fmp pattern --offscreen --no-verify --size 3840x2160 --format 10 --frames 1200   # costs alone
fmp pattern --validate --format 10 --duration-s 3                            # windowed
fmp pattern --offscreen --unpaced --no-verify --blend --duration-s 3         # back-pressure alone, no pacing
dotnet test tests/FrameMotionPlus.Gpu.Tests -c Release                       # the integration tests
```

## Not done, or not clean

- **Windowed pacing** is clock-paced FIFO; in the final runs the six without validation presented every slot or all
  but 2, the two under validation skipped about 3 %, and earlier runs skipped up to 3 % of slots in a quiet desktop
  and up to 11 % under concurrent GPU load (above). Locking to the display is Phase 6.
- **The ~1.6 ms submit stalls** offscreen are the driver's (above; up to 2.3 ms with the desktop's load); not seen in
  windowed presentation.
- **Caller errors the bridge can only document.** A slot whose `ProducerSignal` is never enqueued: nothing can tell
  it from a slow producer, so drains time out on it and its import goes to the reaper, which frees it once *written*
  has passed that write (a later bridge's first write does) or the presenter, disposing, has forced its own waits (the
  bridge's drain released the write through the presenter, so the presenter waits for it and forces it when its own
  drain times out), and no bridge is live. Until then the import's memory stays allocated and the presenter's release
  of that write waits GPU-side, on its Vulkan queue; nothing waits for it in the CUDA context, and if releasing through
  the presenter failed, the reaper releases from CUDA once the presenter lets go and frees without waiting for that
  write. N or more consecutive writes on one ring that are never signalled wedge any later bridge on that ring until
  the presenter is disposed (its first writes wait for releases of those). A process that returns from `Main` without
  disposing the presenter while one of its releases waits for a write that is never signalled hangs in the driver's
  exit path: dispose the presenter (it forces its own waits). A bridge that is never disposed keeps the reaper from
  freeing anything on its context. A multi-stream producer whose queued work waits for one of its own writes that it
  never signals keeps the reaper's eventual free waiting with it. A `ProducerView` pointer kept and written after a `Configure`: the old memory import is freed, so it dangles
  (`IsCurrent` tells a producer before it enqueues). Writes signalled out of order: noticed on a best-effort basis
  (`OutOfOrderSignals`, only when the bridge reads the counter), never prevented. A slot handed out and then neither
  presented, released nor followed by a newer one that reaches the presenter: its writes stay unfinished and hold the
  producer back (the Core contract, "Completion", requires every slot to reach the consumer). The
  stale-`ProducerSignal` hole the review found (the driver handing the new ring's import the old semaphore's handle)
  is closed: retired semaphores stay alive until the bridge is disposed, two per ring replacement.
- **A catch-up at the bridge's ceiling** (250 writes) costs 63 release submissions, 1.6 ms on the present thread under
  validation; at the default limit (2N) it is 2 submissions. The presenter's own worst case (499) is unreachable
  through the bridge.
- **The pacing loop's readiness gate** (wait up to half a period for a free image) is demo pacing, replaced by the
  Phase 6 scheduler. Nothing depends on it: `--unpaced` and the Gpu tests' stall loops run without it.
- **The windowed show log** (`tests/FrameMotionPlus.Vulkan.Tests/Support/ShowLog.cs`) now counts a present as handed
  to the compositor once its GPU work (copy and output pass) is seen finished, polled through the presenter's
  frame-done timeline (a test-only accessor), instead of when it was submitted; completion is seen at most one loop
  turn late, which can only count an interval as starved that was fed, not the other way round.
- **First use after the warm-up (the 32-byte allocation, explained and handled).** The windowed `fmp pattern` test
  (JIT test host, 1.5 s, 30 warm-up frames) used to fail about once in five full Gpu-suite runs on 32 bytes allocated
  on the present thread. The join reviewer traced them: one `System.String`, "libc", the library name the runtime hands
  its P/Invoke resolver when the JIT host binds a P/Invoke stub, allocated at the first execution in the process of
  `Monotonic.NowNs` inside the loop's poll for a free image. That happens once per process, at the first wait for a
  free image: frame 2 or 3 in passing runs, inside the warm-up, and after it in the failing ones. It is the JIT
  runtime's first-use work, not per-frame allocation, and the NativeAOT binary has no such stub. Handling: before
  anything is timed or counted, `fmp pattern` now runs a pre-roll (about 16 ms, bounded at 2 s, on the present thread)
  through every path of its per-frame loop that could otherwise first run after the warm-up: producing (pattern or
  blend, with the GPU timers), the poll for a free image including its sleep, the bridge refusing at its limit (the
  held-back path), a present that catches several releases up, a present the presenter drops for want of a free frame,
  feedback and timer reads, and the read-back and verification. Its frames are not counted or verified (the
  presenter's own statistics include its few presents). The zero-allocation checks stay exact: fresh processes with no
  warm-up frames at all now allocate 0 B from the first measured slot (offscreen with and without blend, windowed);
  without the pre-roll the same runs allocate 280 B (608 B with blend) in their first slots; and a 32-byte allocation
  injected per frame, in the bridge's acquire or in the loop itself, fails every `fmp pattern` allocation check and
  `AllocationTests`. `AllocationTests` already ran every path of its measured windows in a warm-up window first; the
  one branch that window need not reach (the rig's not-ready spin) is now called once before it.
- **Only one GPU here**, so the device-mismatch path is tested by replacing the CUDA side's UUID through a test seam;
  the import-failure paths by injected exceptions at each step and by descriptors the driver itself rejects (a real
  exported descriptor cannot be made to fail import: the driver accepts wrong sizes and kinds).
- The bridge is Linux-only (opaque FDs). On other systems it throws `PlatformNotSupportedException` and `fmp pattern`
  exits 69 saying so; Windows (NT handles) is Phase 10.
