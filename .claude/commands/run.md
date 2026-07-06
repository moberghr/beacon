---
description: Kill any running Beacon API / Vite processes and start a fresh API (Beacon.SampleProject) + Vite frontend (src/Beacon.UI/web).
---

# /run — start Beacon (API + frontend)

When invoked, do the following in this exact order.

## 1. Locate the repo root

Run from the repo root (the directory containing `Beacon.sln`). If invoked from a worktree under `.claude/worktrees/`, run from that worktree — do **not** cross over to the main checkout. Determine the root with:

```bash
git rev-parse --show-toplevel
```

Use that path as the working directory for every subsequent command.

## 2. Kill any existing Beacon processes

Beacon's port footprint:

- **API (Kestrel)** — 5296 (http), 7187 (https), 19921 (http profile)
- **Vite dev server** — 5173 (and incrementing: 5174, 5175 if taken)

Kill anything listening on those ports. Then also kill any `Beacon.SampleProject` binary and any `vite` process by name, in case they leaked from a previous session without holding a port at this instant.

```bash
# Kill by port (silently ignore "no process" results)
for port in 5296 7187 19921 5173 5174 5175 5176; do
  lsof -ti:"$port" 2>/dev/null | xargs kill -9 2>/dev/null || true
done

# Kill by process name (best-effort)
pkill -9 -f "Beacon.SampleProject" 2>/dev/null || true
pkill -9 -f "vite" 2>/dev/null || true

# Give the OS a moment to release the sockets
sleep 1
```

Then verify the ports are free:

```bash
lsof -nP -i:5296 -i:7187 -i:5173 2>/dev/null || echo "ports clear"
```

If anything is still listening, surface it to the user and stop — do not start new processes on top of a lingering one.

## 3. Start the API in the background

From the repo root:

```bash
dotnet run --project Beacon.SampleProject --no-launch-profile
```

Run this **with `run_in_background: true`**. Do not wait for it to finish — it's a server. Capture the bash id so you can monitor logs if startup fails.

After kicking it off, poll briefly until the API is accepting connections:

```bash
for i in 1 2 3 4 5 6 7 8 9 10; do
  if curl -fsS http://localhost:5296/beacon/api/health > /dev/null 2>&1; then
    echo "api ready on http://localhost:5296"
    break
  fi
  sleep 2
done
```

If after ~20s the API hasn't responded, read the background bash's stdout/stderr and report what failed.

## 4. Start Vite in the background

```bash
npm run dev --prefix src/Beacon.UI/web
```

Run this **with `run_in_background: true`**. Use `--prefix` (don't `cd`) so the parent working directory stays at the repo root.

Vite prints its URL to stdout. Wait a few seconds, then read the background bash output and grep for `Local:` so you can quote the actual URL back to the user — Vite picks 5173 by default but increments if taken.

## 5. Report what's running

End with a tight summary:

```
API:      http://localhost:5296/beacon/api  (bash id <id>)
Frontend: http://localhost:<vite-port>      (bash id <id>)
```

Tell the user which `bash id` to pass to `BashKill` if they want to stop either one.

## Notes

- **First-time setup**: if `npm run dev` fails with missing modules, run `npm ci --prefix src/Beacon.UI/web` first, then retry. Don't re-run `npm install` on every invocation — it's slow and unnecessary once `node_modules/` exists.
- **`Beacon:EncryptionKey` and other config**: if the API exits immediately with "EncryptionKey must be configured", that's an environment issue — report it and stop. Don't auto-generate a key.
- **Worktree awareness**: each worktree gets its own running pair. The kill step at the top reclaims ports from any other worktree's running instance, which is exactly what's wanted when switching.
- **No `dotnet build` first**: `dotnet run` rebuilds incrementally. Don't pre-build — it doubles startup time.
