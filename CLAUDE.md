# Multiplayer Restaurant

Unity 6000.3.10f1 game jam project. A working multiplayer lobby system is already built;
first-person player movement is in progress. This file is the persistent context bridge
between sessions.

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
