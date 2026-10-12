# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Unity **6000.6.5f1** project (URP 3D template): a multiplayer block-world game in the spirit of *Eco* (server-simulated ecosystem), currently at the single-player voxel foundation stage. One scene (`Assets/Scenes/SampleScene.unity`, the only scene in the build settings). `Assets/TutorialInfo/` and `Assets/Readme.asset` are leftovers from the Unity template, not project code.

## Architecture

Code is split into assemblies so that a future dedicated server can run the world without rendering:

- `Assets/Scripts/Voxel.Core` — blocks, chunks, world, generation, collision, raycast. **Pure C#: the asmdef sets `noEngineReferences`, so `UnityEngine` is unavailable here.** Vectors are `System.Numerics.Vector3`; positions are `BlockPos` / `ChunkCoord`.
- `Assets/Scripts/Voxel.Client` — meshing, rendering, input, player. Converts vectors at the boundary with `ToNumerics()` / `ToUnity()`.
- `Assets/Tests/Voxel.Core.Tests` — EditMode tests for `Voxel.Core`.

Key rules:

- `World.SetBlock` is the only way gameplay code changes a block. It marks the chunk modified and raises `World.BlockChanged`, which remeshing listens to (networking and saving will hook in the same way). `Chunk.Set` is a raw write for generators only.
- `World.IsSolid` treats everything outside the world's sides and bottom as solid (keeps players in, culls unseen faces); `World.GetBlock` returns air there. Use `IsSolid` for collision and face culling, `GetBlock` for gameplay such as raycasts.
- Terrain comes from an `IChunkGenerator`. `ProvisionalHillsGenerator` is a placeholder until real procedural generation; do not build features that depend on its output.
- The player uses voxel collision (`VoxelCollision.Move`), not Unity physics; chunks have no `MeshCollider`. `transform.position` of the player is the centre of its feet.
- `VoxelWorld` (scene object `World`) creates the world in `Awake`; other components read `VoxelWorld.World` from `Start` onwards.
- The block texture atlas is generated at runtime by `BlockAtlas`; `Assets/Materials/Blocks.mat` only supplies the shader settings.

## Working with the Unity Editor

The Editor is driven through the **MCP for Unity** package (`com.coplaydev.unity-mcp`, tools named `mcp__unityMCP__*`). It only works while the Editor has this project open and the MCP bridge is running; load the `unity-mcp-skill` skill before using it.

- After creating or editing a script, Unity recompiles and reloads the domain. Tool calls made during that window fail or return stale results, so confirm compilation finished and check `read_console` for errors before using the new type.
- `manage_components(action="add")` has failed to resolve a freshly compiled project script by name even after compilation succeeded. Fallback: `execute_code` with `Undo.AddComponent(go, type)`.
- Scene edits made through MCP are not persisted until `manage_scene(action="save")`.
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
