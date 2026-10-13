# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Unity **6000.6.5f1** project (URP 3D template): a multiplayer block-world game in the spirit of *Eco*: the server simulates an ecosystem (plants that grow, spread and die) that players affect. Current state: voxel world, dedicated-server multiplayer, and a first plant simulation. One scene (`Assets/Scenes/SampleScene.unity`, the only scene in the build settings). `Assets/TutorialInfo/` and `Assets/Readme.asset` are leftovers from the Unity template, not project code.

## Architecture

Code is split into assemblies so that a future dedicated server can run the world without rendering:

- `Assets/Scripts/Voxel.Core` — blocks, chunks, world, generation, collision, raycast. **Pure C#: the asmdef sets `noEngineReferences`, so `UnityEngine` is unavailable here.** Vectors are `System.Numerics.Vector3`; positions are `BlockPos` / `ChunkCoord`.
- `Assets/Scripts/Eco.Sim` — the ecosystem simulation: soil fertility, plants, species rules, simulation clock, save format. **Pure C# as well (`noEngineReferences`), depends only on `Voxel.Core`.**
- `Assets/Scripts/Voxel.Client` — meshing, rendering, input, player, and for now the session/network code. Converts vectors at the boundary with `ToNumerics()` / `ToUnity()`.
- `Assets/Tests/Voxel.Core.Tests`, `Assets/Tests/Eco.Sim.Tests` — EditMode tests for the two pure assemblies.

Key rules:

- `World.SetBlock` is the only way gameplay code changes a block. It marks the chunk modified and raises `World.BlockChanged`, which remeshing listens to (networking and saving will hook in the same way). `Chunk.Set` is a raw write for generators only.
- `World.IsSolid` treats everything outside the world's sides and bottom as solid (keeps players in, culls unseen faces); `World.GetBlock` returns air there. Use `IsSolid` for collision and face culling, `GetBlock` for gameplay such as raycasts.
- A block is a `ushort`: low 12 bits are the type, high 4 bits the orientation. Compare types through `Blocks.TypeOf(block)`, never the raw value.
- `WorldSave` stores a header (seed, generator version, dimensions) plus **only the modified chunks**; untouched chunks are regenerated on load. Any change to what a generator produces must bump `IChunkGenerator.Version`, which makes older saves fail to load instead of loading corrupted. `ChunkSerializer` (run-length encoded) is meant to be reused as the network chunk format.
- A persistent world is loaded from `world.dat` in the server's data folder and written back by `VoxelWorld.Save` and `Unload`. A save whose seed or dimensions differ from the inspector settings, or that fails to load, is ignored with a warning and overwritten on exit. Delete the file to reset the world.
- Terrain comes from `TerrainGenerator` (plains, hills, bare-rock massifs; relief stays below 64 blocks). **Server and clients each generate the same world from the seed, so generation must be bit-identical on every machine**: it uses only integer math (`IntNoise`, fixed point with `IntNoise.One` = 1), never floats, `System.Random` or anything order-dependent. `TerrainGeneratorTests` pins reference heights and a world fingerprint; if they change, the generator's output changed and `TerrainGenerator.Version` must be bumped (old saves are then refused, and clients on another version are turned away).
- The player uses voxel collision (`VoxelCollision.Move`), not Unity physics; chunks have no `MeshCollider`. `transform.position` of the player is the centre of its feet.
- `VoxelWorld.World` only exists during a session; it is `null` in the menu. The host gets it from `VoxelWorld.LoadPersistent` (called by `GameSession` right before starting the network); a client gets an empty world from `VoxelWorld.CreateRemote` when the server's world info arrives. `GameSession` unloads it when the session stops.
- **Gameplay code reads the world through `WorldSync.World`**, which stays `null` until the world is fully available on this machine (on a client, until every chunk has arrived). The local player does not simulate before that.
- **Players never call `World.SetBlock` directly: they call `WorldSync.RequestSetBlock`.** The server validates every request (`BlockEditRules` in `Voxel.Core`, plus "not inside a player") and broadcasts accepted changes; a client applies its own request immediately and the server sends back the real block if it refuses.

Ecosystem simulation (`Eco.Sim`, run by the server only):

- **`Ecosystem.Step()` is the only thing that advances the simulation, and it must stay deterministic**: no real time, no `UnityEngine`, no `System.Random` (use the ecosystem's `SimRandom`, whose state is saved), and plants are processed in list order. Tests rely on "same seed + same calls = same forest", including across a save and reload.
- Time acceleration never changes what a step does: `SimClock` only decides *how many* steps to run (one step = 10 game minutes; at speed 1 that is 10 real seconds). Do not scale step effects by a delta time, and never use `Time.timeScale`. `EcosystemSync.Update` runs the due steps within an 8 ms budget per frame; steps queued by "advance N days" always run, backlog caused by the multiplier is dropped.
- Soil fertility lives on a coarse grid (`Ecosystem.CellSize` = 4×4 block columns per cell). Each cell regenerates toward its own capacity, laid out in smooth rich and poor patches. Plants draw it down; below a species' `MinFertility` seeds do not germinate and plants starve.
- A block column holds at most one plant. `Species.Spacing` only applies between plants of the same species. A plant dies, at the next step, if the block under it is no longer dirt or grass or if a block is placed where it stands. This is not re-read every step: the ecosystem listens to `World.BlockChanged` and only rechecks plants in columns that changed, so **a block changed without `World.SetBlock` would never uproot a plant**.
- `Step` cost is what limits time acceleration (about 5 ms for 80,000 plants in the Editor). Plants live in an array sorted by id, looked up by binary search; keep the per-plant work in `Live` free of dictionary lookups and block reads.
- Plants are records, not GameObjects and not `NetworkObject`s. They have no collision: players walk through them.
- Species are data: `SpeciesAsset` (ScriptableObject in `Assets/Species`, durations in game days, plus the meshes and materials to draw) converted to `Eco.Sim.Species`. **Plants reference their species by index in `EcosystemSync.speciesAssets`**, so reordering or renaming species makes `ecosystem.dat` fail to load (a fresh ecosystem then replaces it). The save also records the world's seed and generator version and is refused on any other terrain, since plant positions only make sense on the terrain they grew on. Balance lives in these assets: with the current values trees need rich soil (forests and clearings) and bushes tolerate poor soil.
- `EcosystemSync` mirrors `WorldSync`: named messages, server-authoritative. A joining player receives the existing plants in batches of 500 per network tick (resuming by plant id, which stays correct while plants are born and die), and every player gets, once per network tick, only the final state of plants that changed (a plant's growth is sent as one of `Plant.StageCount` stages, not continuously). Felling is requested with `EcosystemSync.RequestFell` and validated for reach by the server; clients predict it and get the plant back if refused.
- Simulation speed commands (F5/F6/F7, `EcosystemSync.RequestMultiplier` / `RequestAdvanceDays`) are only honoured for admins: the host of a hosted game, or players listed in the server's `-admin <id,id>` argument. `-timescale <x>` sets the starting speed.
- Plant materials must have **GPU instancing enabled in the asset** (`PlantRenderer` draws with `Graphics.RenderMeshInstanced`); enabling it only at runtime would let the build strip the shader variant.
- `PlantRenderer` groups plants into 32-block regions and only prepares and draws regions within `EcosystemSync.plantViewDistance` (128 blocks) of the main camera. Drawing every plant in the world each frame dropped a final-size world to 19 fps.

Scale (measured in the Editor at the planned final size, 64 × 8 × 64 chunks = 1024 × 128 × 1024 blocks, `-worldsize 64,8,64`): the host starts in about 4.5 s and uses about 1.5 GB; a dedicated server starts in under 1 s and uses about 600 MB; sending the whole world to a joining player is 2.6 MB in under 6 s, and that player is in game after about 8 s; a mature forest is about 85,000 plants. Those figures predate client-side generation: a joining player now receives only modified chunks. Still not done at that size: per-player chunk subscriptions (every client holds the whole world), skipping GameObjects for empty chunks, and off-thread meshing.

Networking (Netcode for GameObjects 2.x, Unity Transport, direct IP; the UDP port is set on `GameSession` in the scene, currently 50000):

- `GameSession` owns starting and stopping, in three roles: host (F1 or `-host`), client (Enter or `-connect <address>`), and dedicated server (`-server`, implied by `-batchmode`). F10 leaves. Other arguments: `-port`, `-password`, `-data <dir>` (server save folder, default `Application.persistentDataPath`), `-autosave <seconds>`, `-playerid <id>`, `-worldsize <x,y,z>` (server world size in chunks, overriding the scene).
- **Dedicated server**: the Dedicated Server build module is not installed, so the server is the normal player run headless. `VoxelWorld` skips all meshing and rendering when `Application.isBatchMode` is true, the frame rate is capped to the network tick rate, and the process quits if the server stops.

```powershell
& "A:\dev\ECO 2\Builds\Test\ECO2.exe" -batchmode -nographics -server -password secret -data "D:\eco-server" -logFile "D:\eco-server\server.log"
```

- **Server role teardown differs from client teardown**: a dedicated server never raises `OnClientStopped`, so `GameSession` ends a session on both `OnServerStopped` and `OnClientStopped` (a host gets both; `sessionActive` makes it idempotent).
- `PlayerRegistry` (server only, plain class created by `GameSession`) is the connection approval callback. It checks the password (empty = open server), refuses a player id that is already connected, and spawns the player at the position saved in `players.dat`, raised out of any block built there since. A client's id comes from `PlayerIdentity` (a GUID file in `persistentDataPath`; **the Editor uses a separate file from builds** so both can join the same server from one machine). The id and password travel in the NGO connection payload (`ConnectionRequest`), unencrypted.
- **Saving happens on the server only** (`world.dat`, `players.dat`, `ecosystem.dat` in the data folder): every `autosaveInterval` (60 s), when a player disconnects (players only), on F10 and on quit. **A player's object is already unreadable when NGO reports the disconnection or the end of the session**, so `PlayerRegistry.TrackPlayers` records every connected player's position each frame. `GameSession.EndSession` saves everything once more before tearing the session down, because on quit the network can stop before `OnApplicationQuit` reaches `GameSession`. A killed server loses at most one autosave interval.
- The client shows the server's refusal reason only when it is one of the texts in `ConnectionRefusal` (`Voxel.Core`); any other `NetworkManager.DisconnectReason` is NGO's internal wording and is replaced by a generic message.
- The server's world is persistent (loaded from and saved to `world.dat`). **A client never reads or writes a world file**: it generates the terrain itself from the server's seed, and `WorldSync` then receives only the chunks modified since generation.
- `WorldSync` uses NGO named messages, not RPCs or `NetworkVariable`s, so it needs no `NetworkObject`: on connection, world info (size, seed, generator version, `World.TerrainFingerprint`, number of modified chunks), then the modified chunks (`ChunkBatch`, one batch of at most 16 KB per client per network tick, nearest first), then one message per block change. The client generates the world inside the world-info handler, so later messages always apply on top of generated terrain. If its generator version or terrain fingerprint differs from the server's, it leaves with `WorldSync.Failure` set (shown in the menu) rather than play on mismatched terrain. Chunks are serialized when sent, and all these messages share one reliable ordered channel, so a block changed during the transfer is never lost. Every client currently receives the whole world and every change; per-player chunk subscriptions are left for the load test.
- `PlayerRules` (`Voxel.Core`) holds the player size, eye height and reach used by both the local simulation and the server checks.
- The server chooses each player's spawn position in `GameSession.ApproveConnection` (connection approval is enabled for that reason). Scene management is disabled in the `NetworkManager`: there is a single scene and a reload would destroy the loaded world.
- `Assets/Prefabs/Player.prefab` is the player prefab. Its `NetworkTransform` is **owner-authoritative**; `PlayerController` is a `NetworkBehaviour` that stays disabled except on the owner, which simulates movement locally. The scene's Main Camera is not parented to the player; the owner repositions it every frame.
- The on-screen text is drawn with `OnGUI` for display only. IMGUI gets no input with the new Input System, so all input (including typing the address) is read from `Keyboard.current`.
- The block texture atlas is generated at runtime by `BlockAtlas`; `Assets/Materials/Blocks.mat` only supplies the shader settings.

## Working with the Unity Editor

The Editor is driven through the **MCP for Unity** package (`com.coplaydev.unity-mcp`, tools named `mcp__unityMCP__*`). It only works while the Editor has this project open and the MCP bridge is running; load the `unity-mcp-skill` skill before using it.

- After creating or editing a script, Unity recompiles and reloads the domain. Tool calls made during that window fail or return stale results, so confirm compilation finished and check `read_console` for errors before using the new type.
- `manage_components(action="add")` has failed to resolve a freshly compiled project script by name even after compilation succeeded. Fallback: `execute_code` with `Undo.AddComponent(go, type)`.
- Scene edits made through MCP are not persisted until `manage_scene(action="save")`.
- In the Editor, private fields of scene objects survive script reloads and entering Play mode, and **a `null` string comes back as an empty string**. Test unset strings with `string.IsNullOrEmpty`, not `== null` (this once made the host send an empty player id and start without a player).
- The console regularly shows a `NoSubscription` error from `generators.ai.unity.com` (Unity AI generators without a subscription). It is unrelated to project code.

## Commands

The Unity executable is at `A:\dev\Unity Editor\6000.6.5f1\Editor\Unity.exe`. Batch mode cannot open a project that is already open in the Editor; when the Editor is running, use the MCP tools instead (`run_tests` / `get_test_job`, `refresh_unity`, `manage_build`).

Run tests headless (use `PlayMode` for play mode tests; add `-testFilter "Namespace.Class.Method"` for a single test):

```powershell
& "A:\dev\Unity Editor\6000.6.5f1\Editor\Unity.exe" -batchmode -projectPath "A:\dev\ECO 2" -runTests -testPlatform EditMode -testResults "Logs\test-results.xml" -logFile "Logs\test.log"
```

Check that scripts compile headless (exit code is non-zero on compile errors):

```powershell
& "A:\dev\Unity Editor\6000.6.5f1\Editor\Unity.exe" -batchmode -quit -projectPath "A:\dev\ECO 2" -logFile "Logs\compile.log"
```

## Conventions that are easy to get wrong

- **Input**: active input handling is the new Input System only (`activeInputHandler: 1`). `UnityEngine.Input` (`Input.GetKey`, `Input.GetAxis`) throws at runtime; use `UnityEngine.InputSystem`.
- **Keyboard layout**: the target layout is AZERTY (ZQSD movement). Input System `Key` values are physical positions named after the QWERTY layout, so `Keyboard.current.wKey/aKey/sKey/dKey` is ZQSD on AZERTY. Do not use `zKey`/`qKey` for movement.
- `Assets/InputSystem_Actions.inputactions` is registered as the project-wide actions asset, but `PlayerController` currently polls `Keyboard.current` / `Mouse.current` directly and does not use it.
- **Rendering**: URP with two quality levels, `Mobile` and `PC`, each bound to its own pipeline asset and renderer in `Assets/Settings/`. Rendering changes usually need to be applied to both.
- **Assets**: every asset needs its `.meta` file committed alongside it; move or rename assets through Unity (or move the `.meta` with them) so GUID references survive. Binary assets are tracked with Git LFS and Unity YAML uses the `unityyamlmerge` driver (see `.gitattributes`).
