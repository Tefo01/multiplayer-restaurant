# Multiplayer Restaurant

Unity 6000.3.10f1 game jam project (1-month deadline). This file is the persistent context
bridge between chat sessions — each new session should read this whole file before doing
anything else.

**Phase 1 — Lobby system: done.** See "Multiplayer stack" through "Known decisions / gotchas"
below.

**Phase 2 — Restaurant gameplay: design locked, build in progress across separate chat
sessions.** See "Phase 2: Restaurant Gameplay" below for the game design, architecture, and
milestone checklist. Whichever session picks up a milestone should check its box and add any
new gotchas discovered to that section's gotchas list before ending.

## Multiplayer stack

- **Unity Gaming Services**: Authentication (anonymous sign-in) + Multiplayer Services
  (`com.unity.services.multiplayer`, Sessions API) for lobby create/join/browse/leave/reconnect.
- **Relay**: player-hosted, no dedicated server. One player is host; Relay just forwards
  packets so players on different networks/NATs can connect.
- **Netcode for GameObjects** (`com.unity.netcode.gameobjects`) for gameplay sync, running
  over the same Relay connection the Sessions SDK brings up automatically.

## Scene flow

`Bootstrap` (sign-in, no UI) → `MainMenu` (create/join/browse) → `Lobby` (player list, ready/start, chat) → `Gameplay` (placeholder networked movement + chat).

All four are in Build Settings in that order. `Bootstrap` holds the persistent
`GameManager` (`LobbyManager`) and `NetworkManager` GameObjects (`DontDestroyOnLoad`).
Scene transitions Lobby→Gameplay are driven by `NetworkManager.Singleton.SceneManager.LoadScene`
(host-only call; Netcode auto-syncs connected clients to whatever scene the host is in).

## Key scripts

- `Assets/Scripts/Network/LobbyManager.cs` — the core: session create/join/leave/reconnect,
  ready-state (session player properties), host-start-game, auto-reconnect on relaunch via
  saved session ID in PlayerPrefs, host-migration UI reactivity (`SessionHostChanged`).
- `Assets/Scripts/Network/ChatManager.cs` — server-spawned `NetworkObject` with
  `DestroyWithScene = false`, so one chat channel spans Lobby and Gameplay via ServerRpc/ClientRpc.
- `Assets/Scripts/Network/NetworkPlayerController.cs` — placeholder gameplay: capsule movement,
  owner-authoritative `NetworkTransform`, new Input System (`Keyboard.current`, not legacy `Input`).
  **This is where main-game gameplay logic should attach/extend**, following its pattern for
  anything that needs to sync (RPCs, `NetworkVariable`s).
- `Assets/Scripts/UI/*` — `MainMenuUI`, `LobbySessionUI`, `ChatUI`, `GameplayUI`,
  `LobbyPlayerRow`/`LobbyListRow` (row prefabs under `Assets/Prefabs/UI/`).

## Known decisions / gotchas (don't re-discover these)

- **Relay protocol is UDP, not DTLS**: this machine's Burst toolchain couldn't compile the
  DTLS native callback. `LobbyManager.CreateLobbyAsync` explicitly requests
  `RelayProtocol.UDP` via `WithNetworkOptions`. If Burst issues are fully resolved and
  encryption matters, this could be switched back to DTLS — untested since the switch.
- **Burst/`Library` fix**: if Burst function-pointer compilation errors return, delete the
  `Library` folder and let Unity regenerate it (fixed a real corruption once already).
- **UGUI stretch-anchor trap**: any `RectTransform` using full-stretch anchors
  (`anchorMin.x=0, anchorMax.x=1`) MUST have `sizeDelta.x` explicitly zeroed. Leaving Unity's
  default `sizeDelta=(100,100)` silently adds 100px on top of the stretch, causing exactly
  this bug once (names in scroll lists clipped on the left). Applies to any Content-of-a-ScrollRect
  built via script rather than the that Editor's default 0,0,0,0 offsets/UI Inspector.
- **`NetworkManager.Shutdown()` is not synchronous** — it completes over several frames via
  Netcode's own update loop. Loading a new scene immediately after calling `Shutdown()` races
  Netcode's deferred cleanup and can silently get clobbered. Always poll
  `NetworkManager.Singleton.ShutdownInProgress` down to `false` before the next `SceneManager.LoadScene`
  (see `LobbyManager.ShutdownNetworkAndWaitAsync`).
- **Don't gate local/instant actions on backend round-trips**: `StartGame()` used to await
  `SavePropertiesAsync()` (to lock the session) before transitioning scenes — a Lobby-service
  network call that can time out and hangs the whole button. Removed; the scene transition
  is purely local and needs no backend call.
- **Host migration is UI-only, not full Netcode migration**: when the host leaves, the
  Sessions backend auto-reassigns `session.Host`, and the UI now reacts correctly (new host
  gets the Start Game button). But the actual Netcode server/Relay allocation does **not**
  migrate — that needs `.WithHostMigration()` plus real NGO host-migration plumbing, which is
  not implemented. If the real host disconnects mid-game, the "new host" doesn't automatically
  take over the live connection.
- **`execute_code` (Unity MCP) runs snippets in a separate assembly-load context** for
  `Unity.Services.*` static singletons — checking e.g. `MultiplayerService.Instance` directly
  from an eval snippet can read `null`/`Uninitialized` even when the real running game has it
  correctly initialized. Verify via a real compiled method on a live component instance
  instead of trusting eval-side static reads.

## Phase 2: Restaurant Gameplay

### Core loop

Survive as many days as possible. Each day, orders come in from the house kitchen (window,
online, etc. — source undecided, see Deferred decisions) and the co-op team must cook and
complete them before time runs out. Kitchen objects are real physics props: anything can be
pushed by walking into it, making the kitchen chaotic. Items are carried with a spring-based
hold mechanic (REPO-style) — held objects spring toward a hold point rather than snapping to
it, so heavy things drag, wobble, or can't be lifted at all.

### Locked decisions

1. **Physics authority: host-authoritative.** Only the host simulates real `Rigidbody`
   physics for kitchen props, via `NetworkRigidbody` (confirmed in the installed NGO 2.13.2
   package source to auto-toggle `isKinematic` based on authority — non-host instances are
   forced kinematic and just follow the replicated transform). Clients never run their own
   conflicting simulation of a prop; they only send interaction intent to the host.
2. **Round structure: fixed-length day + summary screen.** Each day runs on a timer computed
   from `NetworkManager.ServerTime` (set once per day as a target end-time, not ticked every
   frame — avoids clock drift and per-frame `NetworkVariable` bandwidth). At day-end: summary
   screen, pass/fail check, next day scales harder via data-driven difficulty (spawn rate,
   order time limits, day length — not hardcoded constants, since balance will get tuned
   until the deadline).
3. **Reputation window: resets every 3 days.** Performance accumulates across a rolling
   3-day block; failing the block's threshold ends the run, but clearing a block wipes the
   counter. A single bad day is recoverable; the block as a whole still has teeth.
4. **No mid-day fail-fast.** Every day always plays out its full timer regardless of how
   badly it's going. Pass/fail against the current 3-day block is evaluated only at day-end.
5. **Fully co-op, shared everything.** One order queue, one shared reputation counter, one
   fail state for the whole session. No per-player competitive scoring.
6. **Order source deferred.** Window vs. online vs. other order sources is not decided yet.
   The order system must be source-agnostic (see `IOrderSource` below) so a real source can
   be plugged in later without restructuring.

### System designs

**Kitchen prop physics** — `KitchenPhysicsObject` prefab: in-scene-placed `NetworkObject`
(not runtime-spawned — fixed kitchen layout) + `Rigidbody` + `Collider` +
`NetworkTransform` left at its **default server-authoritative mode** (the opposite of the
player, which is owner-authoritative) + `NetworkRigidbody` wired to it. Non-host pushes work
today even though `PlayerPrefab` uses a `CharacterController` (no `Rigidbody`): Unity syncs
collider transforms into PhysX every frame regardless, so a dynamic prop still resolves
overlap against a `NetworkTransform`-interpolated remote player capsule and gets shoved. Ship
that cheap version first in M1 (zero player-side changes); only add a kinematic push-proxy
`Rigidbody` driven by `MovePosition()` on non-owned player instances if playtesting shows push
feel is bad. Expect a "jelly" feel for non-host pushes — a push only resolves once the host
simulates it and replicates the result back, roughly one RTT plus `NetworkTransform`'s
interpolation buffer. This is inherent to host-authoritative physics over Relay, not a bug to
chase; mitigate cosmetically (local sound/particle on contact) rather than architecturally.

**Hold/carry spring mechanic** — owner-only raycast/sphere-cast pickup from the local
player's camera (see prerequisite fix below). A client-owned
`NetworkVariable<Vector3> DesiredHoldPoint` on `NetworkPlayerController` (same
ownership-write pattern already used for `_displayName`) streams the desired hold position to
the host every tick. Host runs a PD-controller spring in `FixedUpdate` for whatever prop that
client holds: `force = stiffness*(desiredHoldPoint - rb.position) - damping*rb.velocity`,
clamped to a per-mass `maxForce` before `Rigidbody.AddForce`. Tune `maxForce` low enough
relative to mass that heavy props visibly lag, drag, or can't be lifted at all — this needs
numeric playtesting, so keep stiffness/damping/maxForce/mass as inspector-tunable data, not
hardcoded. Grab/release via `ServerRpc`s (`RequireOwnership = false`, same shape as
`ChatManager.SendChatMessageServerRpc`) that write a `CurrentHolderClientId`
`NetworkVariable` on the prop itself — single source of truth, not duplicated on the player.
Fallback if this runs short on time: Unity's built-in `SpringJoint`/`ConfigurableJoint` with
`breakForce` against a kinematic `MovePosition()`-driven anchor gives "too heavy = drops it"
almost for free, at the cost of less control over the exact feel curve.
**Prerequisite fix**: `PlayerPrefab` currently has its own per-player `Camera` +
`AudioListener` that isn't gated by `IsOwner`, so multiple clients today likely have multiple
active `AudioListener`s. Gate both by `IsOwner` at the start of M2, before building
aim-based pickup on top of it.

**Day/round cycle** — `DayCycleManager`, an in-scene `NetworkBehaviour` in `Gameplay.unity`.
`NetworkVariable<double> DayEndServerTime` set once at day start from
`NetworkManager.ServerTime`; every client computes its own countdown locally each frame
(`DayEndServerTime - ServerTime.TimeAsFloat`) — no ongoing replication cost. Data-driven
difficulty scaling per day. `NetworkVariable<DayPhase>` state machine, server-driven:
`InProgress → DayEndSummary → (InProgress next day | GameOver)`. Reputation follows the
3-day rolling window and day-end-only evaluation from Locked decisions #3–4. Returning
Gameplay→Lobby (or restarting) does **not** need `NetworkManager.Shutdown()` / the
`ShutdownInProgress` poll — that gotcha only applies to fully leaving the session. It's just
a host-only `NetworkManager.Singleton.SceneManager.LoadScene(...)`, identical in shape to
`LobbyManager.StartGame()`. Prefer restarting a run by reloading the `Gameplay` scene
outright (NGO destroys/respawns in-scene `NetworkObject`s for free) over hand-writing
per-system reset code; only build incremental reset later if a full reload proves disruptive.

**Order queue** — server-authoritative `OrderManager`, in-scene alongside
`DayCycleManager`. `OrderRecord` struct (id, dish-id as `FixedString32Bytes`, created/deadline
`ServerTime` doubles, state, source-tag) replicated via `NetworkList<OrderRecord>` so UI can
bind directly to `OnListChanged` instead of hand-rolled add/remove RPCs. `IOrderSource`
interface (`event Action<OrderRequest> OnOrderRequested`) is the deferred-source plug point —
`OrderManager` doesn't know or care where orders come from. Ship exactly one placeholder,
`TimedRandomOrderSource` (day-configured spawn interval, capped concurrent orders), purely so
the pipeline is testable end-to-end now; it is not the real order-source decision.
Fulfillment via `ServerRpc TryFulfillOrder(int orderId, ...)` (`RequireOwnership = false`)
delegating the actual dish-match check to a pluggable matcher (recipe/dish content is a
separate, later decision). Missed orders are caught by a server-side per-order deadline check
against `ServerTime` on a coarse cadence — no per-frame work needed.

### Milestones

Pick up one at a time in a fresh chat session; check the box and note any new gotchas below
when a milestone is done.

- [ ] **M1 — Kitchen prop physics foundation.** `KitchenPhysicsObject` prefab pattern,
  `NetworkRigidbody`/`NetworkTransform` wiring, verify push feel with 2+ real clients over
  Relay. No holding, no orders. Start with a small throwaway spike (one prop, two clients)
  before building further — the `CharacterController`-pushes-`Rigidbody` interaction is
  untested in this project.
- [ ] **M2 — Hold/carry spring mechanic.** Depends on M1 (needs a stable prop to grab). Fix
  the camera/`AudioListener` ownership gating first.
- [ ] **M3 — Day/round cycle system.** No hard dependency on M1/M2 — a day can run and end
  with zero props/holding, so this can be built in parallel with or even before M1/M2 if the
  meta-loop (timer → summary → game-over → back to Lobby) is wanted working first.
- [ ] **M4 — Order queue + one placeholder order source + basic serve/fulfill interaction.**
  Depends on M3 for per-day config (spawn rate, time limits, thresholds).
- [ ] **M5 — Real cooking/plating content.** Integrates M1 (props) + M2 (holding) into M4's
  fulfillment inside M3's loop: stations, recipes, plating. Largest and least-specified
  milestone — re-scope once M1–M4 are actually built and remaining jam time is known. Also
  covers the full balance/tuning pass (spring constants, difficulty curve, thresholds).

### Deferred decisions

- Order source(s): window, online, or other — not decided.
- Dish/recipe content and what "matching" an order actually requires.
- Where a failed run sends players (MainMenu vs. a restart flow) — left open intentionally.

### Gotchas to carry forward

- **Stay on classic PhysX** (`Rigidbody`/`Collider`, already in `com.unity.modules.physics`)
  for all of this. Do **not** pull in `com.unity.physics`/DOTS (Burst/Jobs-heavy) — this
  project's Burst toolchain is already known-fragile (see the DTLS gotcha above).
- **Host-authoritative physics raises the stakes of the existing host-migration gap**: a host
  disconnect mid-game now loses prop positions and current-holder state too, not just
  movement. Same known limitation as before, just a bigger blast radius — no fix planned.
- **Verify `NetworkTransform`/`NetworkRigidbody` idle-prop bandwidth** stays low under load
  once the kitchen has many interactive props — don't assume the default change-threshold
  behavior is enough, check it with a real scene.
