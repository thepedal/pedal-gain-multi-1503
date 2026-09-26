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
- **Channel strips.** Each channel has Mute, Solo, a **Volume** fader, a **Pan**
  (balance) control and a **Mono** switch, in the GUI and as track parameters you can
  sequence.
- **Mono** sums a channel to (L+R)/2 before its fader and pan, so a source with the same
  signal on both sides keeps its level. Switching crossfades over about 10 ms, so it
  doesn't click.
- **Decibel faders.** Volume and Gain run from −∞ and −80 dB up to +10 dB in 0.5 dB steps,
  with 0 dB as the default. The GUI faders use a console-style taper: 0 dB sits at 75 %
  of travel, −20 dB at 42 % and −40 dB at 20 %.
- **Pan laws.** Stereo channels use a balance law (centre = both sides at unity). Mono
  channels use a constant-power pan law (−3 dB per side at centre), so a mono source
  keeps the same loudness wherever it's panned. Turning MO on for a centred channel
  therefore lowers each side by 3 dB, which is the standard pan-law trade-off.
- **Numbering is 0-based**, matching Buzz's plug menus and track numbers. Track *k*,
  GUI row *k* and input plug **In *k*** are the same channel.
- **Multi-out.** Output plug **0 is the master mix**. The direct outs follow it, so the
  direct out of In *k* is output plug *k*+1, labelled **Direct In *k***. For example,
  plug 1 carries In 0 and plug 2 carries In 1. The master stays at plug 0 so its cable
  never moves when channels are added or deleted.
  - **Master (out 0):** Σ input → Mono → × Mute × Solo × Volume × Pan, then × **Gain**
    (the master fader), then × Master Mute.
  - **Direct In k (out k+1):** In k → Mono → × its Mute × Volume × Pan, i.e. the channel
    strip as-is. Solo, Gain and Master Mute act on the master mix only.
- **Smooth level changes.** Gain, Volume and Pan glide to new values (about 10 ms), so
  automation and fader moves don't click or zipper. Mutes keep their Inertia fade.
- **Inertia** 0 … 500 ms (default 25). The fade time shared by every mute and solo.
- **Metering:**
  - **Channel meters** switch between **PRE** (raw input) and **POST** (after Mono, Mute,
    Volume and Pan) with the PRE/POST button in the IN header.
  - **Master meters** show **true peak**: 4× oversampled, so peaks between samples are
    caught. On a test tone whose samples all land at −3 dB of the real peak, a plain
    sample meter reads 3 dB low; this reads within 0.01 dB.
  - **Exact peaks:** the machine reports the highest peak since the GUI's last check, so
    no short peak is missed.
  - **Ballistics:** instant rise, falls about 12 dB/s (20 dB in 1.7 s, IEC 60268-18
    style), 3 s peak hold with a dB readout.
  - **Clip lights** latch red at 0 dBFS or above, on the bar end and the readout. Click
    any meter to clear all clip lights and peak holds.

### GUI controls

- **M / S:** mute and solo per channel. **M** on the OUT row is the Master Mute.
- **MO:** Mono toggle per channel (teal when on).
- **VOL / PAN faders:** drag to set, double-click to reset (0 dB or centre). The mouse
  wheel moves VOL by 1 dB (0.5 dB with Ctrl) and PAN by 5 steps (1 with Ctrl). The
  tooltip shows the value in dB or L/C/R.
- **Master fader:** on the OUT row, controls Gain.
- **− / +:** remove or add a channel (track).

### Parameters

| Group | Parameter | Notes |
|---|---|---|
| Global | Gain | master fader in dB (see below), output 0 only |
| Global | Master Mute | master mix only |
| Global | Inertia | 0–500 ms |
| Track *k* | Solo | channel *k*, master mix |
| Track *k* | Mute | channel *k*, master mix and Direct In *k* |
| Track *k* | Volume | channel fader in dB (see below), master mix and Direct In *k* |
| Track *k* | Pan | 0 = left, 64 = centre, 128 = right; balance (stereo) or constant-power (Mono) |
| Track *k* | Mono | sum to (L+R)/2 before fader and pan, master mix and Direct In *k* |

**Fader values** (Gain and Volume): 0 = −∞, 1 = −80 dB, then 0.5 dB per step, so 149 = −6 dB,
161 = 0 dB (the default) and 181 = +10 dB. The parameter window and GUI tooltips show dB.

New track parameters are always added at the end (Volume and Pan in v1.3, Mono in
v1.3.1), so existing ones keep their positions and older songs load with the new
controls at their defaults.

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
- Reply (protocol v3): `int32 version (3)`, `int32 channel count N`, then N pairs of
  pre-fader and post-fader peak floats, then master true-peak L and R floats.
- Every value is the highest peak since the previous request, and reading resets it.
  The GUI drops the first reply after the window opens, since it may hold old peaks.
- The GUI rebuilds its rows whenever N changes.
- Meter values are normalised so that 1.0 = 0 dBFS.

Button states are read from the parameters themselves, and clicks go through
`IParameter.SetValue`. Undo, the pattern editor and save/load therefore stay in sync.

**.NET version.** The GUI targets net48, a deliberate exception to the
".NET 10 or higher" rule: Buzz 1503 runs on the .NET Framework 4 CLR and cannot
load a .NET 10 assembly. The source avoids `Math.Clamp` and `MathF`, which
.NET Framework lacks.

## Verify in Buzz 1503 (v1.5.0)

Not yet checked on a live install:

1. **dB faders.** VOL and the master fader read in dB. Double-click gives 0 dB at about
   three-quarters of the fader. The mouse wheel moves 1 dB (0.5 dB with Ctrl).
2. **Solo fades.** Soloing or unsoloing on sustained material fades over the Inertia time
   instead of clicking.
3. **Mono pan.** With MO on, panning from left through centre to right keeps an even
   loudness, and centre is 3 dB down per side.
4. **Meters.**
   - PRE/POST switches the channel meters: POST follows the faders and mutes.
   - The master shows true peak.
   - Meters fall smoothly, the hold line stays about 3 s, and clip lights latch at
     0 dBFS until you click a meter.
5. **Labels.** Input plugs read `0. In 0`… and outputs `0. Master`, `1. Direct In 0`…

## Verified in Buzz 1503 (v1.2)

Confirmed on a live Buzz 1503 (32-bit) install:

- **New machine:** starts with 6 tracks, 6 input plugs and 7 output plugs (master + 6 direct).
  The parameter window shows exactly 6 Solo/Mute pairs.
- **Adding and deleting tracks:** works from the parameter window, the pattern editor and
  the GUI's −/+. The plugs, parameter window and meter rows all follow.
- **Deleting a track with a cable attached:** Buzz removes the cable along with the channel.
- **Save and reload:** a song reloads with its saved track count and Solo/Mute states.
- **Direct outs:** each follows its input's Mute, with the Inertia fade.
  (In v1.3 they also follow the channel's Volume fader, and no longer follow Gain.)
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
