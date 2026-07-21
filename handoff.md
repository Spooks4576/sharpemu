# SharpEmu — Unreal Engine 5 bring-up handoff

## What this is

SharpEmu is a PlayStation 5 (Gen5) emulator: it loads a decrypted PS5 `eboot`,
runs the guest x86-64 directly (no recompiler — a direct-execution backend with a
POSIX signal bridge for faults/traps), and HLE-implements the PS5 libraries. Guest
AGC/GNM graphics command buffers are translated to Vulkan; guest GCN/RDNA shaders
are translated to SPIR-V.

- **Branch:** `gr2-work` (fork: `Spooks4576/sharpemu`, upstream: `sharpemu/sharpemu`).
- **Build:** `dotnet build src/SharpEmu.CLI/SharpEmu.CLI.csproj -c Debug`
- **Run:** `artifacts/bin/Debug/net10.0/linux-x64/SharpEmu <path>/eboot.bin`

## Current goal

Get two **Unreal Engine 5** PS5 titles rendering their menus correctly and then
past them:

| Title | ID | ROM path |
|---|---|---|
| Ghostrunner 2 | PPSA09911 | `~/Downloads/PS5/ROMS/PPSA09911-app/` |
| Squirrel Gun | PPSA22102 | `~/Downloads/PS5/ROMS/PPSA22102-app/` |

Decrypted ELFs for static analysis are the `eboot.bin.esbak` files next to each
`eboot.bin` (the `.bin` itself is an encrypted SELF). UE 5.2 source reference is at
`~/Desktop/UE5/UnrealEngine-5.2/`.

## Where the games are now

Both titles **boot to their menu** and then the render/RHI thread parks forever on
UE's `FPThreadEvent::Wait` — the event's `Trigger()` is never called.

- GR2 reaches `GM_MainMenu_C`; Squirrel reaches `TitleController_C_1`.
- The waiter matches UE5.2 `Engine/Source/Runtime/Core/Public/HAL/PThreadEvent.h`
  field-for-field (`Triggered`@+0x14, `WaitingThreads`@+0x18, mutex@+0x20, cond@+0x28).
- The event is meant to be triggered on **GPU-job completion**, delivered through the
  PS5 **AGC event queue** (`sceAgcDriverAddEqEvent`) — *not* via flip args (both
  titles flip with `arg=0`). The async-compute queue shows `producer=none-observed`
  waits: the emulator isn't observing/delivering the GPU completion that should fire
  the event.

**This GPU-completion → event path is the open problem.** A shareable, evidence-backed
brief for a UE expert is published at:
`https://claude.ai/code/artifact/38f7ec8b-90bb-4512-9dc0-ea97275fda57`

## What was fixed this session (these got both games *to* the menu)

All in the two commits on top of `fcb66a3`:

- **`KernelPthreadCompatExports.cs`** — floor timed `cond_timedwait` to ≥1 guest ms.
  Guest retry loops measure elapsed time in whole ms; a sub-ms host resume let them
  read "0 ms elapsed" forever and busy-spin until the import guard killed the run.
- **`AgcExports.cs`** — implemented `sceAgcCbBranch` (+`sceAgcDcbDrawIndirect`,
  `sceAgcSetCxRegIndirectPatchSetNumRegisters`) and made the submit parser **follow
  `IT_INDIRECT_BUFFER`** with correct suspend propagation (a branch is the
  submission's continuation, not a call — earlier mishandling desynced the queue and
  tripped a `HasActiveSubmission` assert).
- **Shader translator** (`Gen5ShaderTranslator.cs`, `Gen5SpirvTranslator*.cs`) — added
  DS scatter opcodes (`0xB0`/`0xB1`, `0x4E`), VOP3 `0x36A` (`V_CVT_PK_U16_U32`), and
  the `R8_UINT` render-target format. These were making fullscreen composite/lighting
  pixel shaders fail translation → white-triangle output.
- **`VideoOutExports.cs`** — `sceVideoOutGetFlipStatus` now returns the last completed
  flip's `flipArg` at `status+0x18` (was hard-coded 0). Real bug (matches the Unity
  "Neva" report); helps flipArg-based titles but is a no-op for GR2/Squirrel (arg=0).
- **`NpWebApi2Exports.cs`** — stubbed `sceNpWebApi2CheckTimeout`.

## Tools added (env-gated, off by default)

- **`SHARPEMU_UE_LOG=1`** — title-agnostic UE log tap (`UeLogHook.cs`). Byte-pattern
  scans the `UE_LOG` variadic formatter, detours it through an `int3` trampoline, and
  in the SIGTRAP handler (`DirectExecutionBackend.PosixSignals.cs`) reads the pristine
  args, renders the line with a compact wide-`printf`, prose-filters, and prints
  `[UELOG] …`. `=verbose` keeps non-prose lines. Works on GR2 and Squirrel unchanged.
- **`SHARPEMU_TRACE_COND_BT=1`** — RBP-walk backtrace of a repeatedly-waited condvar
  (in `KernelPthreadCompatExports.cs`). Used to trace the stuck main thread to
  `UEngine::TickWorldTravel`.
- **`SHARPEMU_LOG_AGC=1`** — AGC packet / label / `wait_suspended` trace (pre-existing).

## Immediate blocker: NID collision (must fix before the branch builds)

After rebasing onto the fork's latest `gr2-work` (`fcb66a3`), the build fails with
`SHEM001`: NID `znaWI0gpuo8` is exported twice —

- `PadExports.PadGetTriggerEffectState` (from commit `a3dac18`, wrong NID)
- `UserServiceExports.UserServiceGetUserNameAlt` (from the `sharpemu:main` merge, correct — `znaWI0gpuo8` is `sceUserServiceGetUserName`)

This collision **pre-exists on `origin/gr2-work`** (the merge combined both). Resolve
by fixing the Pad export (drop/relabel its wrong NID) so `sceUserServiceGetUserName`
wins. Do this as a new commit on top.

## Next steps

1. Resolve the `znaWI0gpuo8` collision so the branch builds.
2. Pursue the GPU-completion → `FPThreadEvent` trigger path (the open problem): trace
   `sceAgcDriverAddEqEvent` → GPU-job/label completion → event-queue trigger, and find
   where the async-compute completion notification is dropped (`producer=none-observed`).
   The UE expert brief above frames the exact questions.
3. Separately, finish shader-translation gaps so the menu renders cleanly rather than
   as corrupted geometry.

## Repo/git state

- `gr2-work` is rebased onto `origin/gr2-work` (`fcb66a3`); my 2 commits sit on top;
  nothing pushed. Working tree clean apart from this `handoff.md`.
