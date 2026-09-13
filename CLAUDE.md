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

**Account for every background task before you report** (T-174). Before your final report, list what you
started in the background that is still running, and stop it or say why it should keep running. Look where the
human looks (the harness's background task list); a process list filtered by name misses a `sleep` loop.
