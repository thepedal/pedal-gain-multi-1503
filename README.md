# Pedal Gain Multi N

Native C++ port of **Pedal Gain Multi** for **Jeskola Buzz build 1503 (32-bit)**,
with a companion WPF GUI DLL. Buzz 1503 only: 32-bit, installed at
`C:\Program Files (x86)\Jeskola\Buzz`. It is not intended for ReBuzz.

It exists because Buzz 1503's managed-machine API has no multi-input `Work`
signature; the native interface does (`MIF_MULTI_IO` + `MultiWork`).

## Features

- **Channels are tracks: 1 … 24.** New machines start with 6. To change the count:
  - use the **−** / **+** buttons in the GUI's IN header, or
  - add or delete tracks in the parameter window or pattern editor, as with any Buzz machine.

  The input and output plugs follow the track count. Only existing channels appear in
  the parameter window.
- **Multi-out.** Output plug **0 is the master mix**. Output plug **k is the direct out
  of input k** (plug 1 carries input 1, plug 2 carries input 2, and so on).
  - **Master (out 0):** every input with its Mute and Solo applied, then × Gain, then × Master Mute.
  - **Direct k (out k):** input k × its Mute (same Inertia fade) × Gain. Solo and Master Mute
    act on the master mix only.
- **Gain** 0 … 200 % (100 = unity, 200 = +6 dB). Applies to the master and every direct out.
- **Inertia** 0 … 500 ms (default 25). The fade time shared by every mute.
- **Metering:** input meters (before mute/solo), plus master L/R, with held-peak lines and dB readouts.

### Parameters

| Group | Parameter | Notes |
|---|---|---|
| Global | Gain | 0–200 % |
| Global | Master Mute | master mix only |
| Global | Inertia | 0–500 ms |
| Track *k* | Solo | channel *k*, master mix |
| Track *k* | Mute | channel *k*, master mix and direct out *k* |

## Layout

```
pedalgainmultin/
├── native/
│   ├── PedalGainMultiN.cpp        machine + DSP + GUI message handler
│   ├── PedalGainMultiN.vcxproj
│   └── sdk/MachineInterface.h     (fetched on first build — not in the repo)
└── gui/
    ├── PedalGainMultiNGUI.cs      WPF meter/button panel (code-only, no XAML)
    └── PedalGainMultiN.GUI.csproj net48 (Buzz 1503's .NET Framework 4 CLR)
```

## Requirements

- Visual Studio 2022 or later with **Desktop development with C++** and **.NET desktop development**.
- Buzz 1503 installed at `C:\Program Files (x86)\Jeskola\Buzz`. Its `BuzzGUI.Interfaces.dll` is referenced by the GUI build.
- Internet access on the first native build, to download `MachineInterface.h`
  (MI_VERSION 66, taken from a pinned commit of the ReBuzz repo, which carries a
  copy; nothing depends on ReBuzz at runtime). Alternatively, place your own copy
  from Buzz's dev files in `native\sdk\`.

## Building

Run these from a Developer PowerShell for VS, elevated if the install directories are under Program Files:

```powershell
# Native machine (32-bit only; other platforms are rejected with a build error)
msbuild native\PedalGainMultiN.vcxproj /p:Configuration=Release /p:Platform=Win32

# GUI (net48)
dotnet build gui\PedalGainMultiN.GUI.csproj -c Release
```

`BuzzDir` defaults to `C:\Program Files (x86)\Jeskola\Buzz`. Override it with
`/p:BuzzDir=…` if Buzz moves.

## What gets deployed

Only `.dll` files are deployed. `.pdb` and `.deps.json` files are never copied.

| Build | File | Destination |
|---|---|---|
| native Win32 | `Pedal Gain Multi N.dll` | `C:\Program Files (x86)\Jeskola\Buzz\Gear\Effects` |
| GUI net48 | `Pedal Gain Multi N.GUI.dll` | `C:\Program Files (x86)\Jeskola\Buzz\Gear\Effects` |

Buzz skips `*GUI.dll` when scanning for machines and loads it as the parameter-window
GUI of the matching machine DLL.

## How it works

**Channels = tracks.** The machine declares Solo and Mute as track parameters, with 1–24
tracks. Buzz calls `SetNumTracks(n)` whenever tracks are added or deleted, including on
load. The machine then calls `SetInputChannelCount(n)` and `SetOutputChannelCount(n + 1)`.
When the count shrinks, the machine lowers its own count before telling Buzz, and when it
grows it raises it after, so the audio thread never reads past the arrays Buzz passes in.
Removed tracks have their Solo/Mute cleared, so a re-added track always starts fresh.

**New vs loaded.** `Save()` writes one version byte. In `Init()`, a machine with no saved
data is treated as new and given 6 tracks. A machine loaded from a song, template or clone
keeps its saved track count.

**Multi-I/O.** `MultiWork` gets one interleaved stereo buffer per plug, or `NULL` when a plug
is unconnected. Direct outs are only computed for connected plugs. If output 0 is
unconnected, the master mix goes to an internal scratch buffer so the master meters keep
working.

**GUI link.** The GUI polls every 33 ms with `IMachine.SendGUIMessage`, which
reaches `HandleGUIMessage` on the C++ side.

- Request: `int32 id` (1 = get meters).
- Reply (protocol v2): `int32 version (2)`, `int32 channel count N`, then N input meter floats, then master L and R floats.
- The GUI rebuilds its rows whenever N changes.
- Meter values are normalised so that 1.0 = 0 dBFS.

Button states are read from the parameters themselves, and clicks go through
`IParameter.SetValue`. Undo, the pattern editor and save/load therefore stay in sync.

**.NET version.** The GUI targets net48, a deliberate exception to the
".NET 10 or higher" rule: Buzz 1503 runs on the .NET Framework 4 CLR and cannot
load a .NET 10 assembly. The source avoids `Math.Clamp` and `MathF`, which
.NET Framework lacks.

## Verified in Buzz 1503 (v1.2)

Confirmed on a live Buzz 1503 (32-bit) install:

- **New machine:** starts with 6 tracks, 6 input plugs and 7 output plugs (master + 6 direct).
  The parameter window shows exactly 6 Solo/Mute pairs.
- **Adding and deleting tracks:** works from the parameter window, the pattern editor and
  the GUI's −/+. The plugs, parameter window and meter rows all follow.
- **Deleting a track with a cable attached:** Buzz removes the cable along with the channel.
- **Save and reload:** a song reloads with its saved track count and Solo/Mute states.
- **Direct outs:** each follows its input's Mute, with the Inertia fade.
- **GUI:** the meters and the M/S buttons work.

**Songs saved with v1.0 or v1.1 are not compatible.** The parameter layout changed, so
rebuild any test songs rather than loading them.

## Differences from the managed version

- Parameter changes arrive once per tick in `Tick()` rather than through immediate setters. The Inertia ramp absorbs this.
- Multi-out (master + direct outs) and a variable channel count (tracks) are new.
- When nothing is connected to the master output, the meters keep working.
- This is a separate machine ("Pedal Gain Multi N"). Songs using the managed "Pedal Gain Multi" are not converted automatically.

## License

MIT, as for Pedal Gain Multi. `MachineInterface.h` (© Oskari Tammelin) is not
included. It is fetched at build time, and its own terms allow its use for
writing freeware Buzz machines.
