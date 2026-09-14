# Agent guidance for Tunqio

Rules for coding agents working in this repository, including agents in `.claude/worktrees`. Keep it short:
a rule belongs here only when breaking it has already cost something, and each one names the task that found it.

## Background work

**A waiter's exit condition must cover every way the wait can end** (T-174). A poll loop or a `Monitor` filter
started in the background has to stop on success, on failure, *and* when the watched process has exited. Ask
both questions before you start one: *if this passes, does the loop exit?* and *if the process dies right now,
does it exit?* A loop that waits only for a failure string, such as
`until grep -q 'gate(s) not PASS' $f; do sleep 15; done`, never ends when the run passes. It polled a dead
process for three hours, and while it ran it looked exactly like a run still in progress.

- Wait on the process, not only its output: check it is still alive, or wait on its exit code.
- Match a string the run prints on every outcome (a final summary line), not one only failure prints.
- Give every waiter a timeout.

**Never load the machine on purpose, and share it** (T-187). This is Phil's own 8-thread desktop, and he uses
it while agents work. It froze and had to be hard-restarted twice on 2026-09-14 while a reproduction test ran
16 threads spinning at `ThreadPriority.AboveNormal` beside another agent's native build. Spinning threads never
yield, and above-normal priority starves the desktop itself.

- No load generators: no spinning or busy threads, no raised thread or process priority, no unbounded
  parallel loops. Reproduce a timing bug with a fake clock or a controlled scheduler instead.
- Cap build parallelism at half the threads: `-m:4` on MSBuild.exe and `-maxcpucount:4` on dotnet.
- Every repeat loop has a count and a timeout, and one heavy build or test run happens at a time.

**Phil reviews the build, not the branch: rebuild main's app before you ask** (T-76, T-191). Both were rejected
on 2026-09-14 as "not in the build": they were merged, but main's `artifacts\bin\Tunqio.App\release_win-x64` was
last built before either merge, because the gate run that would have rebuilt it was skipped when the merged tree
matched an agent's. An agent's worktree build is not main's build.

- After any merge that changes what the app ships, rebuild main's Release output before moving a card to Review
  or asking Phil to look, even when the tree is identical to one already gated.
- In the sign-off request, name the exact `Tunqio.exe` to run and the time it was built.

**Account for every background task before you report** (T-174). Before your final report, list what you
started in the background that is still running, and stop it or say why it should keep running. Look where the
human looks (the harness's background task list); a process list filtered by name misses a `sleep` loop.
