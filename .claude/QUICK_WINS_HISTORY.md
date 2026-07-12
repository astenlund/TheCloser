# Quick wins (history)

Implemented quick wins, archived from `QUICK_WINS.md` so the active
backlog stays scannable. **Archaeological**: loaded on demand, not at
session start. When a quick win lands, append its entry here rather
than to the active file.

Entries appear in the order they shipped. Write each with enough
context to recover the reasoning from the entry alone: investigation
findings, reverted approaches, benchmarks, the commit or scope it
landed in. Negative-knowledge findings (approaches attempted and
reverted, with the reason) are the most valuable content here for
preventing re-attempts; consider promoting those into the relevant
`.claude/patterns/<slug>.md` Cautionary tales section when touching
the pattern doc, leaving a one-line redirect here if cross-referenced.

## Cross-reference resolution

`/nightshift:ready` does **not** scan this file. When a quick win lands, every
other `**Requires:**` line in `FEATURES.md` / `BUGS.md` that referenced
it is edited at the same time to drop the now-satisfied reference. The
active `Requires:` lines therefore describe what is *currently*
blocking. This file is purely archaeological — read it when you want
to know what already shipped or to mine negative-knowledge findings,
not to resolve dependencies.

## Entries

### WindowCloser hard-wires ForegroundActivator and InputSimulator

Shipped: 2026-07-12, in the commits titled "refactor(closer): inject activator, keystroke, and sleep seams" and "refactor(activator): seam native calls for ladder tests".

`WindowCloser` now takes optional constructor seams with real-implementation defaults (the `ForegroundLockSuppression` / `TriggerButtonHealer` pattern): `IForegroundActivator? activator`, `Action<VirtualKeyCode[], VirtualKeyCode>? sendKeystroke` (the default routes to InputSimulator and owns the modified-vs-plain branch), and `Action<TimeSpan>? sleep`. `SendKeyPressIfForeground` became internal and is pinned by dispatch tests: settle-sleep-before-keystroke ordering, no-keystroke-plus-log on activation failure, `PerformedInputAttach` passthrough.

One level down, `ForegroundActivator` gained `INativeWindowApi` (default `NativeWindowApi` delegating to the `NativeMethods` statics), an injected sleep, and an injected suppression factory. The factory seam is load-bearing: the real `ForegroundLockSuppression` constructor mutates the system-wide foreground lock timeout, so activator tests must always inject it. The ladder tests pin owner-attach-before-SetForegroundWindow and detach-in-finally (the Chrome activation fix, 6fbbbc3), the same-thread and no-foreground owner-attach skips, the suppression scope around SetForegroundWindow, `PerformedInputAttach` semantics, the zero-root fallback, and the title-bar click fallback's coordinates and cursor restore. A post-landing review pass added failure toggles to `FakeNativeApi` and tests for the click fallback's cursor-save-failure skip and the SendInput fewer-events-injected log branch.
