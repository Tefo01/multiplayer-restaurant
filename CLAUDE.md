# Multiplayer Restaurant

Unity 6000.3.10f1 game jam project (1-month deadline). This file is the persistent context
bridge between chat sessions — each new session should read this whole file before doing
anything else.

**Phase 1 — Lobby + first-person movement: done.** See "Version control" through "Known
decisions / gotchas" below.

**Phase 2 — Restaurant gameplay: design locked, build in progress across separate chat
sessions.** See "Phase 2: Restaurant Gameplay" below for the game design, architecture, and
milestone checklist. Whichever session picks up a milestone should check its box and add any
new gotchas discovered to that section's gotchas list before ending.

## Version control

Git repo, remote at `https://github.com/Tefo01/multiplayer-restaurant` (private).
- **`main`** — the known-good baseline. Only merge into this once a branch's work is verified.
- **`feature/first-person-movement`** — active work branch for the FPS controller.
Commit working checkpoints often, not just at the end — this project already burned a full
session re-deriving lost work from memory after an unrecoverable experiment, before git existed
here. `.gitignore` excludes `Library/`, `Temp/`, `obj/`, etc.

## Multiplayer stack

- **Unity Gaming Services**: Authentication (anonymous sign-in) + Multiplayer Services
  (`com.unity.services.multiplayer`, Sessions API) for lobby create/join/browse/leave/reconnect.
- **Relay**: player-hosted, no dedicated server. One player is host; Relay just forwards
  packets so players on different networks/NATs can connect.
- **Netcode for GameObjects** (`com.unity.netcode.gameobjects`) for gameplay sync, running
  over the same Relay connection the Sessions SDK brings up automatically.

## Scene flow

`Bootstrap` (sign-in, no UI) → `MainMenu` (create/join/browse) → `Lobby` (player list, ready/start, chat) → `Gameplay` (first-person movement + chat).

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
- `Assets/Scripts/Network/NetworkPlayerController.cs` — first-person controller: `CharacterController`
  movement (WASD relative to look yaw, jump, gravity), mouse look (yaw rotates the root transform,
  pitch rotates `CameraPivot` locally, not networked), cursor lock (Escape toggles), owner-authoritative
  `NetworkTransform`, new Input System (`Keyboard.current`/`Mouse.current`, not legacy `Input`).
  **This is where main-game gameplay logic should attach/extend**, following its pattern for
  anything that needs to sync (RPCs, `NetworkVariable`s).
- `Assets/Prefabs/PlayerPrefab.prefab` — root holds `NetworkObject`/`NetworkTransform`/
  `NetworkPlayerController`/`CharacterController` (feet-at-pivot: `center=(0,1,0)`, `height=2`,
  `radius=0.5`, so the root transform sits at the character's feet). Visual mesh lives on a
  separate `Model` child (currently a placeholder capsule) so a real rigged character model can
  be swapped in later without touching collision/movement/networking. `CameraPivot` → `PlayerCamera`
  (Camera + AudioListener, both disabled by default) is the first-person camera rig.
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
- **The player's `NetworkObject` spawns on connect, not on scene load** — `OnNetworkSpawn` fires
  as soon as the client attaches to the session, which happens *before* `LobbyManager` even loads
  the `Lobby` scene (let alone `Gameplay`). Any owner-only first-person logic (camera enable,
  cursor lock, movement/gravity) must **not** live in `OnNetworkSpawn`/unconditionally in `Update`.
  Putting it there caused two real bugs: the cursor locked out while still looking at lobby UI,
  and `CharacterController.Move()` free-fell the player through `MainMenu`/`Lobby` (scenes with no
  floor) for however long they sat there, so by the time `Gameplay` loaded the player was already
  far below and moving too fast to ever register a ground collision — this is what looked like an
  unfixable `isGrounded` bug across a whole prior session. Fix: gate everything on the object
  actually sitting in the `Gameplay` scene, checked every frame via
  `gameObject.scene.name == LobbyManager.GameplaySceneName` (Netcode migrates this persistent
  object between scenes rather than respawning it, so this is reliable). See
  `NetworkPlayerController.EnterGameplayPresentation`/`ExitGameplayPresentation`.
- **`Gameplay.unity`'s `Floor` sits at world `y = -3`, not `y = 0`** — intentional (set up to test
  something), not a bug. Don't "fix" it back to 0.

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
player, which is owner-authoritative) + `NetworkRigidbody` wired to it.
**Correction to the original assumption below (found via M1's host-only spike test — see
gotcha below): pushing is NOT free/passive.** `CharacterController.Move()` collides with and
stops against a dynamic `Rigidbody` but never imparts force onto it — this needed an explicit
`OnControllerColliderHit` handler on `NetworkPlayerController`, which now exists. On the host
it sets the hit prop's `Rigidbody.linearVelocity` directly (not `AddForce`/impulse — see
gotcha); on a client it forwards push intent via a `ServerRpc(RequireOwnership = false)`
(`RequestPropPushServerRpc`, same shape as `ChatManager.SendChatMessageServerRpc`) since the
client's local copy of the prop is a kinematic `NetworkRigidbody` follower with no real
authority to push. Expect a "jelly" feel for non-host pushes — a push only resolves once the
host simulates it and replicates the result back, roughly one RTT plus `NetworkTransform`'s
interpolation buffer. This is inherent to host-authoritative physics over Relay, not a bug to
chase; mitigate cosmetically (local sound/particle on contact) rather than architecturally.
Remember `Gameplay.unity`'s `Floor` is at world `y = -3`, not `0`, when placing/spawn-positioning
props.

**Hold/carry spring mechanic** — owner-only raycast/sphere-cast pickup from the player's own
`playerCamera` (`NetworkPlayerController`'s existing first-person camera — already gated
correctly by ownership and by the `EnterGameplayPresentation`/`ExitGameplayPresentation`
scene-entry pattern, so no prerequisite camera fix is needed here). A client-owned
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
**Implemented as `KitchenPhysicsObject.cs`** (new script, on the prop) +
`NetworkPlayerController` additions (`DesiredHoldPoint`, `HandleHold`/`TryGrab`/`ReleaseHeld`,
left-mouse-button hold-to-carry/release-to-drop input). Host-only tested: grab → spring-follow
→ release all confirmed working. **Critical tuning constraint found by testing (see gotcha
below): `maxHoldForce` must clear `mass * 9.81` with real headroom**, or the prop can never
lift off the ground at all (spring force alone has to beat gravity before any is left over to
actually move it towards the hold point). `KitchenPhysicsObject.prefab` currently ships
`maxHoldForce = 90` against its `mass = 5` crate (weight ≈ 49N), which lifts cleanly and settles
with a small, physically-plausible gravity sag below the exact hold point (heavier prop → more
sag — a nice side effect of the plain P/D controller, not something explicitly designed for).
**Important**: gate any new per-frame gameplay logic (grab input, hold-point updates) the
same way `HandleMove`/`HandleLook` already are — on `IsOwner` *and* on
`gameObject.scene.name == LobbyManager.GameplaySceneName` inside `Update`, per the documented
gotcha above about the player object spawning before scene load. Don't add new logic to
`OnNetworkSpawn`.

**Drawer/cupboard mechanic** — reuses the hold system as-is, with zero new C# code. A drawer
or cupboard door is just another `KitchenPhysicsObject`-style prop (`Rigidbody` + `Collider` +
`NetworkObject` + `NetworkTransform` + `NetworkRigidbody` + `KitchenPhysicsObject`), the only
difference being a Unity `Joint` component constraining its motion to the correct single DOF:
a `ConfigurableJoint` (`X`/`Y` `Motion = Locked`, `Z Motion = Limited` with a `linearLimit`) for
a drawer's linear slide, or a `HingeJoint` (`useLimits = true`, `min`/`max` in degrees) for a
cupboard door's rotation. Since the joint restricts every other axis, the exact same hold
spring (`AddForce` toward `DesiredHoldPoint`) and push logic (`OnControllerColliderHit`
setting velocity) that already work on freely-carryable props just naturally express
themselves as "pull open" / "shove closed" along whatever axis the joint allows — grabbing a
drawer handle or door and pulling it, or walking into it to close it, needs no drawer-specific
code at all. `connectedBody` is left `null` on both joint types so they anchor directly to
world space (no separate kinematic anchor body needed), with `connectedAnchor` set explicitly
(`autoConfigureConnectedAnchor = false`) for deterministic placement. See the gotcha below
about initial collider overlap, which caused a real bug during this build.
`KitchenDrawer.prefab` and `KitchenCupboardDoor.prefab` are the two current instances
(in-scene-placed in `Gameplay.unity`, next to a static, non-networked back-stop wall /
cabinet frame — the drawer's back-stop is load-bearing (its `ConfigurableJoint`'s
`linearLimit` is symmetric around the closed position, so nothing else stops it sliding
backward through the cabinet), the door's frame is closer to just visual context
(`HingeJoint`'s `min`/`max` limits are already asymmetric and precise on their own).

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
Note the player object is persistent across scenes (not respawned — see the `NetworkObject`
spawn-timing gotcha above), so a scene reload must still work correctly with
`EnterGameplayPresentation`/`ExitGameplayPresentation` re-triggering on the next `Gameplay`
entry.

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

- [x] **M1 — Kitchen prop physics foundation.** `KitchenPhysicsObject` prefab pattern,
  `NetworkRigidbody`/`NetworkTransform` wiring, verify push feel with 2+ real clients over
  Relay. No holding, no orders. Start with a small throwaway spike (one prop, two clients)
  before building further — the `CharacterController`-pushes-`Rigidbody` interaction is
  untested in this project.
  **Done:** spike prop (`KitchenTestCrate`/`KitchenPhysicsObject.prefab`) built, the
  push-unpushable bug found + fixed (see gotcha below — see the "Kitchen prop physics" system
  design above for the corrected behavior), and push feel confirmed working by the user with
  real multi-client testing over Relay.
- [x] **M2 — Hold/carry spring mechanic.** Depends on M1 (needs a stable prop to grab). The
  camera/`AudioListener` ownership gating this used to need as a prerequisite is already
  handled by the existing first-person controller — nothing to fix there.
  **Done:** implemented (`KitchenPhysicsObject.cs` + `NetworkPlayerController` grab/hold input),
  including finding and fixing the `maxHoldForce`-vs-gravity tuning bug (see gotcha below).
  Grab → spring-follow → release confirmed working by the user with real multi-client testing
  over Relay (client-side grab through the host-authoritative `RequestGrabServerRpc`/
  `RequestReleaseServerRpc` path).
- [ ] **M2b — Drawer/cupboard mechanic (not in the original M1-M5 plan; added after M2).**
  Reuses the hold/push systems as-is (see "Drawer/cupboard mechanic" system design above) —
  no new C# code, just two new prop archetypes (`KitchenDrawer.prefab` with a
  `ConfigurableJoint`, `KitchenCupboardDoor.prefab` with a `HingeJoint`) placed in
  `Gameplay.unity`. **Progress:** host-only tested — grab-and-pull-open, release-and-stays-open,
  and push-to-close all confirmed working for both. Found and fixed two real bugs along the way:
  initial collider overlap with static stop/frame geometry causing an explosive phantom-open on
  scene start, and the cupboard door's hinge anchor being miscalculated (off by the scale
  factor, leaving a sliver of door on the wrong side of the pivot) so the door collided with its
  own frame mid-swing and wouldn't stay open — both documented as gotchas below. Still needs the
  same real multi-client Relay verification as M1/M2 before this box is checked.
- [ ] **M2c — Real cupboard/drawer model (`Assets/cupboard.fbx`), wired to the same hold/push
  system as M2b's placeholders.** `cabinet_door` + `cabinet_handle.005` (child) and
  `drawer_drawpart.001` + `drawer_handle.004` (child) got the identical component stack as
  `KitchenPhysicsObject` props (Rigidbody/BoxCollider/NetworkObject/NetworkTransform/
  NetworkRigidbody/KitchenPhysicsObject + Joint), saved as `CabinetDoor.prefab` /
  `DrawerPart.prefab`, in-scene-placed under a `cupboard` parent alongside the static
  `cabinet_outerbox`/`drawer_outerbox` housing (MeshCollider, non-networked, non-convex is
  fine since static). **Progress: confirmed working by the user via real playtesting** —
  grab/open/close/reopen all working for both door and drawer. Three real bugs found and
  fixed along the way (all now gotchas below): (1) this FBX's Blender export didn't have
  transforms applied, baking an extreme ~96-100x uniform `localScale` onto every object with
  correspondingly tiny local mesh data — fixed by baking scale into the mesh vertices and
  resetting `localScale` to 1, not by re-exporting; (2) the usual initial-collider-overlap
  issue against the static housing (~0.004 units) - same fix as M2b, explicit clearance gap;
  (3) the drawer's `ConfigurableJoint` used a naive symmetric limit around its closed
  position, which let it slide **past** closed into the cabinet interior when pushed shut far
  enough that the outerbox started occupying the space the drawer's own collider needed for
  the grab raycast to reach it - "closed, can't reopen" was actually "closed too far, grab
  raycast hits the housing instead." Fixed by offsetting `connectedAnchor` so the placed
  (closed) position sits at one **edge** of the joint's range instead of the middle - see the
  gotcha below for the general form of this fix. Also added a low-friction `PhysicsMaterial`
  (`Assets/Physics/LowFrictionSlide.physicMaterial`, `dynamicFriction`/`staticFriction` =
  0.05) to the drawer's collider, since it sits with much more contact area against the real
  mesh housing than the old placeholder ever touched against a flat floor, and default
  friction was making it feel sticky. `NetworkPlayerController.TryGrab` and
  `KitchenPhysicsObject.FixedUpdate` both carry `[HoldDebug]` `Debug.Log` calls (the
  `FixedUpdate` one throttled to `Time.frameCount % 30`) - deliberately left in per the user's
  request, in case this class of bug resurfaces. Still needs the same real multi-client Relay
  verification as M1/M2/M2b before this box is checked.
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
- **New per-frame gameplay code goes through the same scene/ownership gate as movement/look**
  (`IsOwner` + `gameObject.scene.name == LobbyManager.GameplaySceneName`), not
  `OnNetworkSpawn` — see the existing gotcha above about player spawn timing. Any Phase 2
  system reading/writing camera, cursor, or physics-affecting state must follow this.
- **`CharacterController` does NOT push `Rigidbody`s for free.** The original M1 design
  assumed passive PhysX overlap resolution alone would shove kitchen props — wrong.
  `CharacterController` has infinite mass as far as PhysX is concerned: `Move()` collides with
  and stops against a dynamic `Rigidbody` but never imparts force. Confirmed empirically with a
  host-only isolated test (200 `Move()` steps straight into a test prop produced zero
  displacement). Fix is an explicit `OnControllerColliderHit` handler on
  `NetworkPlayerController` — see "Kitchen prop physics" above for the actual implementation.
- **Don't push a `Rigidbody` with `AddForce(ForceMode.Impulse)` from `OnControllerColliderHit`.**
  The hit fires on every frame of sustained contact (once per `Move()` call), and impulses
  accumulate rather than replace — chaining ~25-30 hits in a row this way launched a test prop
  to ~47 m/s and off into the void within a couple hundred ms. Set `Rigidbody.linearVelocity`
  directly instead (Unity's own documented `CharacterController`-push pattern) — it's idempotent
  per frame regardless of contact duration, so sustained pushing stays bounded at the configured
  push speed instead of runaway-accelerating.
- **A held prop's `maxHoldForce` must clear `Rigidbody.mass * Physics.gravity.magnitude`
  (≈ `mass * 9.81`) with real headroom, or it can never lift off the ground at all.** The
  spring's force is clamped to `maxHoldForce` before `AddForce`, and if that cap is at or below
  the prop's own weight, the vertical component alone consumes the whole force budget just to
  (barely) cancel gravity — none left over to actually move it, so it just sits on the floor
  (at best creeping sideways, fighting the floor's friction, if the target is mostly horizontal).
  Found by host-only testing: `maxHoldForce = 40` against a `mass = 5` crate (weight 49.05N)
  never lifted at all; raising to `maxHoldForce = 90` fixed it immediately and gave a believable
  gravity sag at rest (spring force balances gravity below the exact target point: sag ≈
  `mass * 9.81 / holdStiffness`). When tuning a new/heavier prop, check this ratio first before
  assuming stiffness/damping need adjusting.
- **A joint-constrained prop (drawer/cupboard door) that overlaps its own static "stop"
  geometry at rest will explosively depenetrate the instant physics starts.** Found building
  the drawer/cupboard mechanic: the drawer's back-stop wall and the closed drawer's collider
  overlapped by just 0.025 units in the source scene data, and on entering Play mode PhysX's
  depenetration push shoved the drawer straight to its `ConfigurableJoint`'s full open limit
  before any player ever touched it - looked exactly like a phantom "grab" had happened, and
  wasted real debugging time on the joint's anchor config before the actual cause (simple
  collider overlap) was found. Same thing happened independently with the cupboard door
  overlapping its frame. Fix is just geometry: always leave a small explicit gap (~0.02-0.05
  units) between a prop's closed/rest position and any static collider it sits against - check
  this first, before suspecting the joint itself, if a joint-constrained prop is doing anything
  unexplained at rest.
- **Don't stress-test a joint-constrained prop's hold spring with a target far past its
  travel/rotation limit.** Slamming a `ConfigurableJoint`/`HingeJoint` hard into its limit with
  a large, poorly-aligned force differential produced a real instability once (the prop's
  `CurrentHolderClientId` silently reset to "not held" with no error logged, for reasons not
  fully root-caused). Not expected to be reachable in real play, since `DesiredHoldPoint` is
  always bounded by the fixed `holdDistance` offset from the camera - only came up because a
  test deliberately set a hold target several meters past the door's hinge limit. Worth knowing
  if a future NetworkVariable-driven target (not gated by holdDistance) is ever added.
- **`Joint.anchor` is a completely ordinary local-space point — `Transform.TransformPoint`/
  `InverseTransformPoint` convert it to/from world space exactly like any other local point,
  scale included.** (An earlier version of this gotcha misdiagnosed the bug below as some
  special "unscaled mesh space" - it isn't; that was just a hand-arithmetic mistake, not a
  Joint quirk.) `KitchenCupboardDoor`'s hinge was originally set to `anchor = (-0.3, 0, 0)`,
  reasoning "half the door's 0.6 *world*-space width" - but a local value gets *multiplied* by
  `localScale` on its way to world space, so using a world-space distance directly as the
  local value is wrong by a factor of `localScale`; for this door (`localScale.x = 0.6`) the
  correct local value was `-0.3 / 0.6 = -0.5`. The fix that "worked" (`anchor = -0.5`) wasn't
  landing on some special unscaled-mesh convention, it was just `InverseTransformPoint`'s
  ordinary scale-aware math finally applied correctly. **The robust way to get a joint anchor
  right, always: compute the desired point in world space (e.g. from `Renderer.bounds`), then
  call `transform.InverseTransformPoint(worldPoint)` and use that result verbatim** - don't
  hand-derive a fraction of `localScale`/mesh width and guess which "space" it needs to be in.
- **An FBX with an extreme baked-in import scale (localScale in the tens-to-hundreds) usually
  means Blender transforms weren't applied before export, and it's worth baking the scale into
  the mesh on the Unity side rather than re-exporting.** `Assets/cupboard.fbx`'s parts all
  shared the exact same non-identity transform (`localRot ≈ (270°, 0, 0)`, `localScale ≈
  93-100`) - the giveaway that every object in the source Blender file needs Object → Apply →
  All Transforms before its next export. Re-exporting isn't required to keep working, though:
  for each affected part, read `MeshFilter.sharedMesh`, write a **new** `Mesh` (don't mutate
  the shared imported asset) with every vertex pre-multiplied by the object's current
  `localScale`, assign it back, then set `localScale = Vector3.one`. Any child (e.g. a handle)
  needs the same treatment - detach it first (`SetParent(null, true)` to preserve world
  transform), bake it independently, then re-parent (`SetParent(parent, true)`) so its local
  transform re-resolves against the parent's new scale-1 frame.
- **A symmetric `ConfigurableJoint` linear limit around a slideable prop's "closed" position
  lets it travel PAST closed, not just open - offset `connectedAnchor` so closed sits at one
  EDGE of the range instead of the middle.** The drawer's joint originally used
  `connectedAnchor = closedPosition`, `limit = 0.5`, giving an achievable range of
  `[closed - 0.5, closed + 0.5]` - the extra "closed - 0.5" half let a hard push shove it
  further into the cabinet than its actual closed position. Symptom looked exactly like "grab
  stopped working once fully closed," because the over-pushed drawer's collider was now behind
  the static housing's opening, so the grab raycast hit the housing instead. Not a hold-logic
  or raycast bug - the joint was the one letting it go somewhere it visually shouldn't ever be
  able to reach. Fix: to get an achievable range of `[closed, closed + travel]` (same total
  open reach, no over-closing), set `limit = travel / 2` and
  `connectedAnchor = closedPosition + openDirectionWorld * limit` (i.e. the anchor sits at the
  *midpoint* of the desired range, not at closed itself).
- **CRITICAL: manually driving physics via `execute_code` (`Rigidbody.AddForce`/`AddTorque`
  called from an out-of-band MCP eval snippet) is NOT reliable for diagnosing joint/physics
  bugs - trust real playtesting over it.** A large chunk of this session was spent chasing what
  looked like a total, reproducible breakdown of Netcode + `Joint` physics: `AddTorque`/
  `AddForce` calls (even via the `HingeJoint`'s own `motor`) on `NetworkObject`s produced
  exactly zero response, including on the OLD, already-shipped, previously-verified-working
  `KitchenCupboardDoor` and on a completely unrelated free-floating crate - across a fresh
  Play session, a full domain reload, and even a full Unity Editor restart. The apparent
  conclusion (`HingeJoint`/`ConfigurableJoint` don't work at all on `NetworkObject`s in this
  project) was wrong: the user actually played the exact same objects normally (mouse-driven
  grab, real gameplay input) and they worked fine the whole time. The `execute_code` bridge
  runs in a separate assembly-load context (see the existing gotcha above re:
  `Unity.Services.*` static singletons reading stale/null there) - this apparently extends to
  physics manipulation too: reads of `.position`/`.rotation`/`.IsSleeping()` came back
  self-consistent and plausible-looking, but the actual force/torque calls silently failed to
  affect the real simulation the live game was running. **When a physics/joint mechanic looks
  broken only under `execute_code`-driven force injection, don't trust that signal - ask for
  (or do) a real Play-button playtest with actual input before concluding anything is broken.**
