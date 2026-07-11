# Drop the Target-Window Rung from the Activation Ladder

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the native-activation-of-the-target rung from `ForegroundActivator.TryActivate`, activating the root window directly, halving activation latency (~140ms to ~70ms) whenever `WindowFromPoint` returned a child HWND.

**Architecture:** The escalation ladder shrinks from four rungs to three: already-foreground check, native activation of the root (owner attach + root attach + SetForegroundWindow under a foreground-lock suppression), title bar click fallback. No changes to `TryActivateNatively`, `TryAttachToForegroundOwner`, or `TryActivateByClicking` internals.

**Tech Stack:** C# / .NET 10, Win32 P/Invoke (user32), xUnit.

## Why this is safe (rationale, verbatim from the analysis)

- **Structural:** foreground status is a top-level-window property; `GetForegroundWindow` only ever returns a root, and `IsForeground(target)` already treats "foreground == root" as success. A hypothetical success of `SetForegroundWindow(childHwnd)` could only manifest as the root becoming foreground, which is exactly what the root rung produces directly. There is no reachable end state where the target rung wins and the root rung would not have.
- **Empirical:** in all three verified traces from 2026-07-11 (16:27, 16:37, 16:39), the target rung's `SetForegroundWindow` returned false *with the owner attach active*, i.e. in the same permission context where the root rung succeeded milliseconds later. SFW rejects the child HWND because it is a child, not because of permissions.
- **Backstop:** the click fallback remains the last rung and covers anything exotic.

## Global Constraints

- Never deploy unless the user expressly asks; Task 3 (deploy + interactive verification) is gated on the user's word.
- Build with `dotnet build C:/Git/TheCloser --no-incremental`; test with `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build`. Never run an unfiltered solution-level `dotnet test`.
- Commit subjects: Conventional Commits, max 72 chars, subject-only (no body, no Co-Authored-By trailer).
- No em-dashes, en-dashes, or emoji in any generated text, code, comments, or commit messages.
- C# style: block syntax for all if statements, blank line before return statements, modern language features.
- No new unit test is possible for this change: `ForegroundActivator` hard-wires `NativeMethods` statics (tracked in `QUICK_WINS.md` under "WindowCloser hard-wires ForegroundActivator and InputSimulator"). The regression gate is the existing 43-test suite plus interactive verification. Do NOT bundle the injectability refactor into this change.

---

### Task 1: Simplify the ladder in ForegroundActivator

**Files:**
- Modify: `TheCloser/ForegroundActivator.cs` (the `TryActivate` method and the class header comment; nothing else in the file)

**Interfaces:**
- Consumes: existing private members `IsForeground(IntPtr)`, `TryActivateNatively(IntPtr)`, `TryActivateByClicking(IntPtr, TitleBarClickPosition)`, and `GetRootWindow(IntPtr)` from `NativeMethods` (via `using static`). None change.
- Produces: `TryActivate(IntPtr targetWindow, TitleBarClickPosition clickPosition)` keeps its exact public signature and return semantics (true = target or its root is foreground). Log lines retained verbatim: `"Foreground: target was already foreground."`, `"Foreground: native activation of the root window succeeded."`, `"Foreground: title bar click fallback succeeded."`. The line `"Foreground: native activation of the target window succeeded."` disappears from the codebase.

- [ ] **Step 1: Replace the ladder body in `TryActivate`**

(Both snippets below are shown dedented; the method sits at class-level indentation in the file, so preserve that indentation when matching and replacing.)

The current method reads:

```csharp
public bool TryActivate(IntPtr targetWindow, TitleBarClickPosition clickPosition)
{
    var rootWindow = GetRootWindow(targetWindow);

    if (IsForeground(targetWindow))
    {
        _logger.Log("Foreground: target was already foreground.");

        return true;
    }

    if (TryActivateNatively(targetWindow))
    {
        _logger.Log("Foreground: native activation of the target window succeeded.");

        return true;
    }

    if (rootWindow != targetWindow && TryActivateNatively(rootWindow))
    {
        _logger.Log("Foreground: native activation of the root window succeeded.");

        return true;
    }

    if (TryActivateByClicking(rootWindow, clickPosition))
    {
        _logger.Log("Foreground: title bar click fallback succeeded.");

        return true;
    }

    return false;
}
```

Replace it with:

```csharp
public bool TryActivate(IntPtr targetWindow, TitleBarClickPosition clickPosition)
{
    var rootWindow = GetRootWindow(targetWindow);

    if (rootWindow == IntPtr.Zero)
    {
        rootWindow = targetWindow;
    }

    if (IsForeground(targetWindow))
    {
        _logger.Log("Foreground: target was already foreground.");

        return true;
    }

    if (TryActivateNatively(rootWindow))
    {
        _logger.Log("Foreground: native activation of the root window succeeded.");

        return true;
    }

    if (TryActivateByClicking(rootWindow, clickPosition))
    {
        _logger.Log("Foreground: title bar click fallback succeeded.");

        return true;
    }

    return false;
}
```

The `IntPtr.Zero` guard is new behavior-preservation, not new function: `GetAncestor(hWnd, GA_ROOT)` returns NULL for the desktop window and invalid handles. Today such a target still gets a native attempt via the old target rung; after the removal, the root rung must receive the target itself in that case or the native path silently vanishes for those windows. `TryActivateByClicking(rootWindow, ...)` also benefits from the non-zero handle (its `GetWindowRect` would fail on NULL and skip the fallback).

- [ ] **Step 2: Update the class header comment**

Replace:

```csharp
// Brings the target window to the foreground via an escalation ladder: already-foreground check,
// native activation of the target and then its root (each under a foreground-lock suppression,
// with the input queues of the foreground owner and the target attached), and finally a
// synthesized title bar click.
```

with:

```csharp
// Brings the target window to the foreground via an escalation ladder: already-foreground check,
// native activation of the root window (under a foreground-lock suppression, with the input
// queues of the foreground owner and the root attached), and finally a synthesized title bar
// click. The root is activated directly because SetForegroundWindow rejects child HWNDs even
// with foreground permission, and a child can only become "foreground" via its root anyway.
```

- [ ] **Step 3: Build**

Run: `dotnet build C:/Git/TheCloser --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 4: Run the existing test suite (regression gate)**

Run: `dotnet test C:/Git/TheCloser/TheCloser.Tests --no-build`
Expected: 43 passed, 0 failed. (No test reaches `TryActivate`; this guards against collateral damage only.)

- [ ] **Step 5: Commit**

```bash
git -C C:/Git/TheCloser add TheCloser/ForegroundActivator.cs
git -C C:/Git/TheCloser commit -m "refactor(app): drop target rung, activate root directly"
```

### Task 2: Cross-reference sweep in .claude docs

Retiring the target-then-root rung pair invalidates prose that describes the ladder as having it. Grep is the source of truth; the two known sites are listed, but run the search anyway.

**Files:**
- Modify: `.claude/BUGS_HISTORY.md` (the "Durable learnings" bullet about the HWND hierarchy)
- Verify-only: `.claude/QUICK_WINS.md`, `CLAUDE.md`, `README.md` (expected to need no edits; confirm)

**Interfaces:**
- Consumes: nothing from Task 1's code; textual references only.
- Produces: docs consistent with the three-rung ladder.

- [ ] **Step 1: Find all ladder-shape references**

Preferred: the harness Grep tool with pattern `rung|ladder`, case-insensitive, glob `*.md`, over the repo root. Shell fallback: `grep -rn -i -E "rung|ladder" C:/Git/TheCloser/README.md C:/Git/TheCloser/CLAUDE.md C:/Git/TheCloser/.claude --include="*.md"` (single `-E` pattern; this machine's Git Bash grep aborts with SIGABRT on `-i` combined with multiple `-e` flags, and in a pipeline that abort masquerades as zero hits).

Expected hits and their dispositions:
- `.claude/BUGS_HISTORY.md`: the learnings bullet saying the "target-then-root rung pair covers both" hierarchy shapes: EDIT (Step 2).
- `.claude/BUGS_HISTORY.md`: other mentions of rungs in the archived entry describe the past accurately: LEAVE.
- `.claude/QUICK_WINS.md` injectability entry: mentions "rung order and the attach discipline" generically, still true of the three-rung ladder: LEAVE.
- `.claude/reviews/2026-07-11-full-solution-review.md`: archival review snapshot: LEAVE.
- `CLAUDE.md` "escalation ladder in ForegroundActivator": still accurate, names no rung count: LEAVE.
- Anything else found: judge by the same rule (historical narration stays, present-tense design description must match the three-rung ladder).

- [ ] **Step 2: Update the stale learnings bullet in BUGS_HISTORY.md**

Replace:

```markdown
- Chrome's HWND hierarchy under the cursor varies by invocation: WindowFromPoint sometimes returns the Chrome_WidgetWin_1 root, sometimes a child; the ladder's target-then-root rung pair covers both.
```

with:

```markdown
- Chrome's HWND hierarchy under the cursor varies by invocation: WindowFromPoint sometimes returns the Chrome_WidgetWin_1 root, sometimes a child. The ladder originally covered both with a target-then-root rung pair; the target rung was later dropped because SetForegroundWindow rejects child HWNDs even with foreground permission (verified in the 2026-07-11 traces: the child rung failed in the same permission context where the root rung succeeded), so the ladder activates the root directly.
```

- [ ] **Step 3: Commit**

```bash
git -C C:/Git/TheCloser add .claude/BUGS_HISTORY.md
git -C C:/Git/TheCloser commit -m "docs(bugs): note target-rung removal in ladder learnings"
```

(If Step 1 surfaced additional present-tense references, include those files in the same commit; they are one logical change.)

### Task 3: Deploy and interactive verification (USER-GATED)

**Do not start this task without the user expressly asking for a deploy.**

**Files:**
- None modified; runs `deploy.ps1` and reads `%TEMP%/TheCloser.log`.

**Interfaces:**
- Consumes: the deployed binary from Tasks 1-2.
- Produces: verified production behavior; evidence for closing the plan.

- [ ] **Step 1: Deploy (after the user says so)**

Run (PowerShell): `$env:PATH = "C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"; pwsh -NoProfile -File C:\Git\TheCloser\deploy.ps1`
Expected: daemon stopped, Release Native AOT publish, both executables copied to `C:\Sync\Personal\3. Resources\Bin\TheCloser\`. (The PATH prepend works around the machine-local vswhere gap for Native AOT publishes.)

- [ ] **Step 2: Interactive verification (user at the machine)**

Procedure: click an explorer window (so it holds the input lock), hover a background Chrome window's viewport, invoke TheCloser.

Read the trace: `Get-Content "$env:TEMP\TheCloser.log" -Tail 10`

Expected log shape per invocation, two lines, ~70ms apart:

```
<timestamp> chrome -> CTRL-W
<timestamp> Foreground: native activation of the root window succeeded.
```

Success criteria: NO `SetForegroundWindow returned false.` line (the child rung that produced it is gone), no click-fallback line, and the hovered window's active tab closed. If a `SetForegroundWindow returned false.` line appears followed by the click fallback, the root rung itself failed; that is a new data point, not a regression of this change (the removed rung could not have saved it), but capture the trace in `.claude/BUGS.md` as a new entry.

- [ ] **Step 3: Delete this plan file**

Plans are ephemeral; the code, tests, and commits are the durable record.

```bash
git -C C:/Git/TheCloser rm .claude/plans/2026-07-11-drop-target-rung.md
git -C C:/Git/TheCloser commit -m "docs(plans): drop landed target-rung plan"
```
