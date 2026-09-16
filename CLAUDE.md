# Multiplayer Restaurant

Unity 6000.3.10f1 game jam project. A working multiplayer lobby system is already built;
the main game (restaurant gameplay) is the next phase, likely built in a separate chat
session. This file is the persistent context bridge between sessions.

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
