// Pedal Gain Multi N — native (C++) Buzz 1503 machine, 32-bit
//
// Port of the managed (ReBuzz) machine "Pedal Gain Multi" to the classic
// Buzz native machine interface (MachineInterface.h, MI_VERSION 66).
//
// v1.3 — channel strips
//
//   Channels : one Buzz track per channel, 1..24 (new machines start with 6).
//              Add/delete tracks in the parameter window, pattern editor or
//              the GUI's −/+; the input/output plugs follow the track count.
//   Per channel (track params): Solo, Mute, Volume (fader), Pan (balance),
//              Mono (sums L+R to mono before the fader; v1.3.1)
//   Output 0 : MASTER — Σ input × Mute × Solo × Volume × Pan, then × Gain
//              (master fader) × Master Mute
//   Output k : DIRECT — input k-1 ("Direct In k-1") × its Mute × Volume × Pan
//              (post-fader, post-pan). Solo, Gain and Master Mute are
//              master-only.
//
//   All level controls (Gain, Volume, Pan) and the Mono switch glide to new
//   values (~10 ms smoothing) so automation and slider moves don't click.
//   Mutes and solos fade over the Inertia time.
//
// v1.4 — console-standard level behaviour
//   • Gain and Volume are decibel faders: 0 = -inf, 1..181 = -80..+10 dB in
//     0.5 dB steps, 161 = 0 dB (unity). (v1.3 used linear percent.)
//   • Solo now fades with Inertia, like mute (v1.3 switched instantly).
//   • Pan on a Mono channel is a constant-power panner (-3 dB per side at
//     centre); stereo channels keep the balance law.
//
// v1.5 — metering
//   • Meters report the exact peak since the GUI's last poll (no peaks lost
//     between polls); ballistics, hold and clip latching live in the GUI.
//   • Channels report both pre-fader (raw input) and post-fader (after Mono,
//     Mute, Volume, Pan) peaks; the GUI's PRE/POST switch picks one.
//   • The master reports TRUE PEAK: 4x oversampled with a 48-tap windowed-sinc
//     interpolator (BS.1770-style), catching inter-sample peaks.
//
// v1.6 — meter window support (backwards compatible)
//   • Meter data is collected into independent SLOTS, so several readers never
//     steal each other's peaks: slot 0 = the parameter panel (unchanged v3
//     request), slots 1..3 = meter windows (new v4 request).
//   • v4 replies add RMS (mean square since the last read) for every channel
//     (pre and post) and each master side. Parameters and save data unchanged.
//
// v1.7 — loudness and correlation (backwards compatible)
//   • The master is K-weighted (ITU-R BS.1770: high-shelf + RLB high-pass,
//     coefficients derived for the running sample rate) and its energy is
//     summed into 100 ms blocks, handed to meter windows for momentary,
//     short-term and gated integrated loudness (EBU R128).
//   • Running Σ L², Σ L·R, Σ R² on the master feed a stereo correlation meter.
//   • New request GUIMSG_GET_METERS_V5 returns the v4 data plus a loudness /
//     correlation trailer. The v3 (panel) and v4 requests are unchanged.
//
//   • Peak meters (inputs pre-fader, master L/R) exposed to the companion
//     "Pedal Gain Multi N.GUI.dll" via HandleGUIMessage

#include <math.h>
#include <stdio.h>
#include <atomic>
#include "MachineInterface.h"

// ── Channel configuration ─────────────────────────────────────────────────
static int const MaxChannels     = 24;
static int const DefaultChannels = 6;    // track count given to NEW machines

static float const FULL_SCALE = 32768.0f;   // Buzz ±32768 → meters 1.0 = 0 dBFS

// Level smoothing (Gain / Volume / Pan): one-pole, ~10 ms time constant.
static float const SMOOTH_SECONDS = 0.010f;

// Pan (balance) range: 0 = hard left, 64 = centre, 128 = hard right.
static int const PAN_CENTRE = 64;
static int const PAN_MAX    = 128;

// Machine data written by Save(). Its presence in Init() marks a machine that
// is being loaded (song, template, clone) rather than freshly created.
static byte const SAVE_VERSION = 2;     // 2 = v1.4 (dB fader encoding)

// ── Fader encoding (Gain and Volume) ─────────────────────────────────────
// 0 = -inf; code c in 1..LEVEL_MAX = (c - LEVEL_UNITY) * 0.5 dB.
static int   const LEVEL_UNITY = 161;       //   0.0 dB
static int   const LEVEL_MAX   = 181;       // +10.0 dB
static float const LEVEL_STEP_DB = 0.5f;

static float LevelDb(int code) { return (code - LEVEL_UNITY) * LEVEL_STEP_DB; }

static float g_levelTable[LEVEL_MAX + 1];   // code → linear gain

static void InitLevelTable()
{
    static bool done = false;
    if (done) return;
    g_levelTable[0] = 0.0f;
    for (int c = 1; c <= LEVEL_MAX; c++)
        g_levelTable[c] = powf(10.0f, LevelDb(c) / 20.0f);
    done = true;
}

static inline float LevelGain(int code)
{
    if (code <= 0)        return 0.0f;
    if (code > LEVEL_MAX) code = LEVEL_MAX;
    return g_levelTable[code];
}

// ── GUI message protocol (shared with PedalGainMultiNGUI.cs) ───────────────
// Request : int32 message id
// Reply   : GUIMSG_GET_METERS → int32 protocol version (3), int32 channel count N,
//           N × { float prePeak, float postPeak }, float truePeakL, float truePeakR
//           All values are linear (1.0 = 0 dBFS) peaks since the previous poll;
//           reading resets them.
static int const GUIMSG_GET_METERS    = 1;     // slot 0, protocol v3 (parameter panel)
static int const GUI_PROTOCOL_VERSION = 3;

// v1.6: meter-window request.
// Request : int32 GUIMSG_GET_METERS_EX, int32 slot (1..METER_SLOTS-1)
// Reply   : int32 version (4), int32 channel count N,
//           N × { float prePeak, float postPeak, float preMeanSq, float postMeanSq },
//           float truePeakL, float truePeakR, float meanSqL, float meanSqR
//           Peaks: linear, 1.0 = 0 dBFS. Mean squares: linear power, 1.0 = 0 dBFS;
//           a channel's power is the average of its two sides, (l² + r²) / 2.
//           All values cover the time since this slot was last read; reading resets.
static int const GUIMSG_GET_METERS_EX = 2;
static int const GUI_PROTOCOL_EX      = 4;
static int const METER_SLOTS          = 4;     // 0 = panel, 1..3 = meter windows

// v1.7: meter window request with loudness + correlation.
// Request : int32 GUIMSG_GET_METERS_V5, int32 slot (1..METER_SLOTS-1)
// Reply   : exactly the v4 layout but with version 5, followed by
//           int32 sampleRate, int32 blockCount B, int32 droppedBlocks,
//           float blockEnergy[B],                  // 100 ms K-weighted blocks:
//                                                  //   (Σ kL² + Σ kR²) / blockLen,
//                                                  //   full scale = 1.0 → LUFS = -0.691 + 10·log10(e)
//           float meanLL, float meanLR, float meanRR   // master, since last read
static int const GUIMSG_GET_METERS_V5 = 3;
static int const GUI_PROTOCOL_V5      = 5;
static int const LOUD_BLOCK_CAP       = 64;    // 6.4 s of blocks per slot between reads

// ── True-peak interpolator (master) ──────────────────────────────────────
// 4 phases × 12 taps. Phase f interpolates the signal at (m - 6 + f/4) from
// x[m-11..m] with a Blackman-windowed sinc, normalised to unity DC gain.
// Phase 0 reproduces x[m-6] exactly, so true peak >= sample peak.
static int const TP_PHASES = 4;
static int const TP_TAPS   = 12;
static int const TP_HIST   = TP_TAPS - 1;     // samples carried between blocks
static float g_tpCoef[TP_PHASES][TP_TAPS];

static void InitTruePeakFilter()
{
    static bool done = false;
    if (done) return;
    const double PI_D = 3.14159265358979323846;
    for (int ph = 0; ph < TP_PHASES; ph++)
    {
        double f = (double)ph / TP_PHASES, sum = 0;
        double c[TP_TAPS];
        for (int j = 0; j < TP_TAPS; j++)
        {
            double d = (TP_TAPS / 2) - j - f;          // distance from the interpolated point (j = age)
            double sinc = fabs(d) < 1e-12 ? 1.0 : sin(PI_D * d) / (PI_D * d);
            double w = fabs(d) >= TP_TAPS / 2 ? 0.0
                     : 0.42 + 0.5 * cos(PI_D * d / (TP_TAPS / 2)) + 0.08 * cos(2 * PI_D * d / (TP_TAPS / 2));
            c[j] = sinc * w;
            sum += c[j];
        }
        for (int j = 0; j < TP_TAPS; j++)
            g_tpCoef[ph][j] = (float)(c[j] / sum);
    }
    done = true;
}

// ── K-weighting coefficients (ITU-R BS.1770-4) ───────────────────────────
// Stage 1: high shelf (+4 dB above ~1.7 kHz, models the head); stage 2: RLB
// high-pass (~38 Hz). Designed analytically for any sample rate; at 48 kHz
// these reproduce the coefficients tabulated in BS.1770 (same derivation as
// the widely used libebur128).
static void KShelfCoefs(double rate, double &b0, double &b1, double &b2, double &a1, double &a2)
{
    const double PI_D = 3.14159265358979323846;
    double f0 = 1681.974450955533, G = 3.999843853973347, Q = 0.7071752369554196;
    double K  = tan(PI_D * f0 / rate);
    double Vh = pow(10.0, G / 20.0);
    double Vb = pow(Vh, 0.4996667741545416);
    double a0 = 1.0 + K / Q + K * K;
    b0 = (Vh + Vb * K / Q + K * K) / a0;
    b1 = 2.0 * (K * K - Vh) / a0;
    b2 = (Vh - Vb * K / Q + K * K) / a0;
    a1 = 2.0 * (K * K - 1.0) / a0;
    a2 = (1.0 - K / Q + K * K) / a0;
}

static void KHighPassCoefs(double rate, double &b0, double &b1, double &b2, double &a1, double &a2)
{
    const double PI_D = 3.14159265358979323846;
    double f0 = 38.13547087602444, Q = 0.5003270373238773;
    double K  = tan(PI_D * f0 / rate);
    double a0 = 1.0 + K / Q + K * K;
    b0 = 1.0; b1 = -2.0; b2 = 1.0;
    a1 = 2.0 * (K * K - 1.0) / a0;
    a2 = (1.0 - K / Q + K * K) / a0;
}

// Meter accumulator for one reader (slot). Guarded by mi::meterLock.
struct MeterAcc
{
    float  prePeak[24], postPeak[24];
    double preSq[24],   postSq[24];      // Σ (l² + r²) / 2, in raw Buzz units²
    float  tpL, tpR;                     // true peak, raw units
    double sqL, sqR;                     // Σ l², Σ r²
    double count;                        // samples accumulated

    // v1.7 loudness + correlation (master, normalised to full scale = 1.0)
    float  blocks[64];                   // completed 100 ms K-weighted block energies
    int    nBlocks, dropped;
    double cLL, cLR, cRR;                // Σ l², Σ l·r, Σ r²

    void Clear()
    {
        for (int i = 0; i < 24; i++) { prePeak[i] = postPeak[i] = 0.0f; preSq[i] = postSq[i] = 0.0; }
        tpL = tpR = 0.0f;
        sqL = sqR = count = 0.0;
        nBlocks = dropped = 0;
        cLL = cLR = cRR = 0.0;
    }
};

// ── Parameter indices (Buzz numbers globals first, then track params) ──────
enum
{
    P_GAIN = 0,
    P_MASTER_MUTE,
    P_INERTIA,
    P_NUM_GLOBAL,

    P_SOLO = P_NUM_GLOBAL,      // track param 0
    P_MUTE,                     // track param 1
    P_VOLUME,                   // track param 2   (appended in v1.3)
    P_PAN,                      // track param 3   (appended in v1.3)
    P_MONO,                     // track param 4   (appended in v1.3.1)
    P_COUNT,
    P_NUM_TRACK = P_COUNT - P_NUM_GLOBAL
};

// ── Parameter declarations ─────────────────────────────────────────────────
static CMachineParameter const paraGain =
{ pt_word, "Gain", "Master fader in dB (output 0 only). 0 = -inf, 161 = 0 dB, 181 = +10 dB, 0.5 dB steps.",
  0, LEVEL_MAX, 0xFFFF, MPF_STATE, LEVEL_UNITY };

static CMachineParameter const paraMasterMute =
{ pt_switch, "Master Mute", "Mute the master output (fade time set by Inertia). Direct outs are not affected.",
  -1, -1, SWITCH_NO, MPF_STATE, SWITCH_OFF };

static CMachineParameter const paraInertia =
{ pt_word, "Inertia", "Mute fade time in milliseconds (0 = instant)",
  0, 500, 0xFFFF, MPF_STATE, 25 };

static CMachineParameter const paraSolo =
{ pt_switch, "Solo", "Solo this channel on the master mix",
  -1, -1, SWITCH_NO, MPF_STATE, SWITCH_OFF };

static CMachineParameter const paraMute =
{ pt_switch, "Mute", "Mute this channel (master mix and its direct out)",
  -1, -1, SWITCH_NO, MPF_STATE, SWITCH_OFF };

static CMachineParameter const paraVolume =
{ pt_byte, "Volume", "Channel fader in dB (master mix and direct out). 0 = -inf, 161 = 0 dB, 181 = +10 dB, 0.5 dB steps.",
  0, LEVEL_MAX, 0xFF, MPF_STATE, LEVEL_UNITY };

static CMachineParameter const paraPan =
{ pt_byte, "Pan", "Balance on stereo channels; constant-power pan (-3 dB centre) on Mono channels. 0 = left, 64 = centre, 128 = right.",
  0, PAN_MAX, 0xFF, MPF_STATE, PAN_CENTRE };

static CMachineParameter const paraMono =
{ pt_switch, "Mono", "Sum this channel to mono, (L+R)/2, before its fader and pan",
  -1, -1, SWITCH_NO, MPF_STATE, SWITCH_OFF };

static CMachineParameter const *pParameters[P_COUNT] =
{
    &paraGain, &paraMasterMute, &paraInertia,                    // global
    &paraSolo, &paraMute, &paraVolume, &paraPan, &paraMono        // track
};

// Value blocks. Layout MUST mirror pParameters (types + order).
#pragma pack(1)
struct gvals
{
    word gain;
    byte masterMute;
    word inertia;
};
struct tvals
{
    byte solo;
    byte mute;
    byte volume;
    byte pan;
    byte mono;
};
#pragma pack()

static_assert(sizeof(gvals) == 5, "gvals must be packed");
static_assert(MaxChannels == 24, "MeterAcc arrays are sized for 24 channels");
static_assert(sizeof(tvals) == 5, "tvals must be packed");

CMachineInfo const MacInfo =
{
    MT_EFFECT,
    MI_VERSION,
    MIF_MULTI_IO,               // MultiWork + channel plugs
    1, MaxChannels,             // min/max tracks = channel range
    P_NUM_GLOBAL,
    P_NUM_TRACK,
    pParameters,
    0, NULL,                    // no attributes (channel count = track count)
    "Pedal Gain Multi N",
    "PGainMulN",
    "WDE",
    NULL,
    NULL
};

// ── Channel names (stable storage for GetChannelName) ──────────────────────
static char g_inNames[MaxChannels][8];
static char g_outNames[MaxChannels + 1][16];

static void InitChannelNames()
{
    static bool done = false;
    if (done) return;
    for (int i = 0; i < MaxChannels; i++)
    {
        // 0-based, matching Buzz's plug-menu indices and track numbers.
        // Output 0 is the master, so input i's direct out is output i + 1.
        snprintf(g_inNames[i],      sizeof(g_inNames[i]),      "In %d",        i);
        snprintf(g_outNames[i + 1], sizeof(g_outNames[i + 1]), "Direct In %d", i);
    }
    snprintf(g_outNames[0], sizeof(g_outNames[0]), "Master");
    done = true;
}

class mi;

class miex : public CMachineInterfaceEx
{
public:
    mi *pmi = nullptr;

    virtual void MultiWork(float const * const *inputs, float **outputs, int numsamples);
    virtual char const *GetChannelName(bool input, int index);
    virtual bool HandleGUIMessage(CMachineDataOutput *pout, CMachineDataInput *pin);
};

class mi : public CMachineInterface
{
public:
    mi();

    virtual void Init(CMachineDataInput * const pi);
    virtual void Tick();
    virtual void Save(CMachineDataOutput * const po);
    virtual void SetNumTracks(int const n);
    virtual bool Work(float *psamples, int numsamples, int const mode) { return false; } // unused (MIF_MULTI_IO)
    virtual char const *DescribeValue(int const param, int const value);

    void MultiWork(float const * const *inputs, float **outputs, int n);
    char const *GetChannelName(bool input, int index);
    bool HandleGUIMessage(CMachineDataOutput *pout, CMachineDataInput *pin);

private:
    void ApplyChannelCount(int want, bool force);
    void Process(float const * const *inputs, float **outputs, int n, int channels);

    gvals gval;
    tvals tval[MaxChannels];
    miex  ex;
    bool  initialized = false;
    char  descBuf[64];

    // Current channel count. Written on the UI thread (Init/AttributesChanged),
    // read once per block on the audio thread.
    std::atomic<int> numChannels;

    // Parameter state — written in Tick(), read in MultiWork().
    int   gainCode = LEVEL_UNITY;
    bool  solo[MaxChannels]   = {};
    bool  inMute[MaxChannels] = {};
    bool  masterMute = false;
    int   volumeCode[MaxChannels];
    int   panPos[MaxChannels];
    bool  mono[MaxChannels] = {};

    // Smoothed level state (audio thread only). curL/curR = Volume × balance,
    // used by the master mix and the direct out; curGain = master fader.
    float curL[MaxChannels];
    float curR[MaxChannels];
    float curMono[MaxChannels];     // 0 = stereo, 1 = mono (crossfaded)
    float curGain          = 1.0f;
    bool  levelsInitialized = false;
    int   inertiaMs = 25;

    // Ramp state (audio thread only).
    float currentInMuteGain[MaxChannels] = {};
    float currentSoloGain[MaxChannels]   = {};   // solo gate on the master mix, fades with Inertia
    bool  inMuteInitialized = false;
    float currentMuteGain   = 1.0f;
    bool  muteInitialized   = false;
    int   cachedSr          = 0;

    // ── v1.7 loudness: K-weighting (two biquads per side) + 100 ms blocks ──
    struct Biquad
    {
        double b0, b1, b2, a1, a2;       // a0 normalised to 1
        double z1[2], z2[2];             // transposed direct form II state, per side
        void Reset() { z1[0] = z1[1] = z2[0] = z2[1] = 0.0; }
        inline double Run(int ch, double x)
        {
            double y = b0 * x + z1[ch];
            z1[ch] = b1 * x - a1 * y + z2[ch];
            z2[ch] = b2 * x - a2 * y;
            return y;
        }
    };
    Biquad kShelf, kHighPass;
    int    kRate     = 0;                // sample rate the filters were designed for
    int    blockLen  = 0;                // samples per 100 ms block
    int    blockFill = 0;
    double blockSum  = 0.0;

    void DesignKWeighting(int rate);

    // Master-bus scratch, used when nothing is connected to output 0 so the
    // master meters still work.
    float scratch[MAX_BUFFER_LENGTH * 2];

    // Meters — one accumulator per reader slot. The audio thread merges each
    // block into every slot; a GUI read copies and clears its own slot. A tiny
    // spinlock keeps each read a consistent snapshot; it is held for a few
    // hundred nanoseconds at most, once per block.
    MeterAcc         meterAcc[METER_SLOTS];
    std::atomic_flag meterLock;

    void LockMeters()   { while (meterLock.test_and_set(std::memory_order_acquire)) {} }
    void UnlockMeters() { meterLock.clear(std::memory_order_release); }

    // True-peak history + working buffer per master side (audio thread only).
    float tpBuf[2][TP_HIST + MAX_BUFFER_LENGTH];
};

mi::mi()
{
    GlobalVals = &gval;
    TrackVals  = tval;
    AttrVals   = NULL;
    numChannels.store(1, std::memory_order_relaxed);   // = minTracks until the host says otherwise

    for (int i = 0; i < MaxChannels; i++)
    {
        tval[i].solo   = SWITCH_NO;
        tval[i].mute   = SWITCH_NO;
        tval[i].volume = 0xFF;
        tval[i].pan    = 0xFF;
        tval[i].mono   = SWITCH_NO;
        volumeCode[i] = LEVEL_UNITY;
        panPos[i]    = PAN_CENTRE;
        curL[i] = curR[i] = 1.0f;
        curMono[i] = 0.0f;
    }
    meterLock.clear();
    for (int sl = 0; sl < METER_SLOTS; sl++)
        meterAcc[sl].Clear();
    for (int c = 0; c < 2; c++)
        for (int j = 0; j < TP_HIST; j++)
            tpBuf[c][j] = 0.0f;

    InitChannelNames();
    InitLevelTable();
    InitTruePeakFilter();
}

void mi::Init(CMachineDataInput * const pi)
{
    ex.pmi = this;
    pCB->SetMachineInterfaceEx(&ex);

    CMachine *self = pCB->GetThisMachine();     // only valid in Init()

    bool loading = false;
    if (pi != NULL)
    {
        byte version = 0;
        pi->Read(version);
        loading = version >= 1;
    }

    // Plugs for the track count the machine has right now.
    initialized = true;
    ApplyChannelCount(pCB->GetNumTracks(self), true);

    // A brand-new machine gets the default channel count. A loaded one keeps
    // whatever track count the song/template/clone carries (the host applies
    // it through SetNumTracks).
    if (!loading)
        pCB->SetNumTracks(self, DefaultChannels);
}

void mi::Save(CMachineDataOutput * const po)
{
    po->Write(SAVE_VERSION);
}

// Called by the host whenever tracks are added or deleted (and on load).
void mi::SetNumTracks(int const n)
{
    // Before Init() the plugs can't be set yet; Init() reads the track count.
    if (!initialized) return;

    int cur = numChannels.load(std::memory_order_acquire);

    // Tracks being removed lose their host-side state, so reset ours too;
    // otherwise re-adding a track could resurrect stale settings.
    if (n < cur)
    {
        LockMeters();
        for (int sl = 0; sl < METER_SLOTS; sl++)
            for (int i = (n < 0 ? 0 : n); i < cur && i < MaxChannels; i++)
            {
                MeterAcc &a = meterAcc[sl];
                a.prePeak[i] = a.postPeak[i] = 0.0f;
                a.preSq[i]   = a.postSq[i]   = 0.0;
            }
        UnlockMeters();
    }
    for (int i = (n < 0 ? 0 : n); i < cur && i < MaxChannels; i++)
    {
        solo[i]      = false;
        inMute[i]    = false;
        volumeCode[i] = paraVolume.DefValue;
        panPos[i]    = PAN_CENTRE;
        mono[i]      = false;
    }

    ApplyChannelCount(n, false);
}

// Tell the host the new plug counts. Ordering matters: the host's buffer
// arrays must always be at least as large as the count the audio thread
// uses, so shrink our count BEFORE the host's and grow it AFTER.
void mi::ApplyChannelCount(int want, bool force)
{
    if (want < 1)           want = 1;
    if (want > MaxChannels) want = MaxChannels;

    int cur = numChannels.load(std::memory_order_acquire);
    if (!force && want == cur) return;

    if (want < cur)
        numChannels.store(want, std::memory_order_release);

    pCB->SetInputChannelCount(want);
    pCB->SetOutputChannelCount(want + 1);   // + master

    if (want >= cur)
        numChannels.store(want, std::memory_order_release);
}

void mi::Tick()
{
    if (gval.gain != paraGain.NoValue)       gainCode   = gval.gain;
    if (gval.masterMute != SWITCH_NO)        masterMute = gval.masterMute != SWITCH_OFF;
    if (gval.inertia != paraInertia.NoValue) inertiaMs  = gval.inertia;

    int channels = numChannels.load(std::memory_order_relaxed);
    for (int i = 0; i < channels; i++)
    {
        if (tval[i].solo   != SWITCH_NO)          solo[i]      = tval[i].solo != SWITCH_OFF;
        if (tval[i].mute   != SWITCH_NO)          inMute[i]    = tval[i].mute != SWITCH_OFF;
        if (tval[i].volume != paraVolume.NoValue) volumeCode[i] = tval[i].volume;
        if (tval[i].pan    != paraPan.NoValue)    panPos[i]    = tval[i].pan;
        if (tval[i].mono   != SWITCH_NO)          mono[i]      = tval[i].mono != SWITCH_OFF;
    }
}

char const *mi::DescribeValue(int const param, int const value)
{
    switch (param)
    {
    case P_GAIN:
    case P_VOLUME:
        if (value <= 0)
            return "-inf dB";
        snprintf(descBuf, sizeof(descBuf), "%+.1f dB", LevelDb(value > LEVEL_MAX ? LEVEL_MAX : value));
        return descBuf;

    case P_INERTIA:
        if (value <= 0)
            return "0 ms (instant)";
        snprintf(descBuf, sizeof(descBuf), "%d ms", value);
        return descBuf;

    case P_PAN:
        if (value == PAN_CENTRE)
            return "C";
        if (value < PAN_CENTRE)
            snprintf(descBuf, sizeof(descBuf), "L %d%%", (PAN_CENTRE - value) * 100 / PAN_CENTRE);
        else
            snprintf(descBuf, sizeof(descBuf), "R %d%%", (value - PAN_CENTRE) * 100 / (PAN_MAX - PAN_CENTRE));
        return descBuf;
    }

    return NULL;   // host default (switches show 0/1)
}

static inline float fmaxf2(float a, float b) { return a > b ? a : b; }
static inline float fminf2(float a, float b) { return a < b ? a : b; }

void mi::MultiWork(float const * const *inputs, float **outputs, int n)
{
    int channels = numChannels.load(std::memory_order_acquire);

    // Buzz guarantees n <= MAX_BUFFER_LENGTH; chunk defensively anyway so the
    // scratch buffer can never overflow.
    if (n <= MAX_BUFFER_LENGTH)
    {
        Process(inputs, outputs, n, channels);
        return;
    }

    float const *inOff[MaxChannels];
    float       *outOff[MaxChannels + 1];
    for (int done = 0; done < n; done += MAX_BUFFER_LENGTH)
    {
        int m = n - done < MAX_BUFFER_LENGTH ? n - done : MAX_BUFFER_LENGTH;
        for (int i = 0; i < channels; i++)
            inOff[i] = (inputs && inputs[i]) ? inputs[i] + 2 * done : nullptr;
        for (int o = 0; o <= channels; o++)
            outOff[o] = (outputs && outputs[o]) ? outputs[o] + 2 * done : nullptr;
        Process(inOff, outOff, m, channels);
    }
}

// Balance law for a stereo channel: centre = both sides at unity; turning
// towards one side attenuates the other side linearly to zero.
static inline void BalanceGains(int pan, float &l, float &r)
{
    if (pan <= PAN_CENTRE) { l = 1.0f; r = (float)pan / PAN_CENTRE; }
    else                   { r = 1.0f; l = (float)(PAN_MAX - pan) / (PAN_MAX - PAN_CENTRE); }
}

// Constant-power pan law for a mono channel: l² + r² = 1 at every position,
// so loudness stays even across the pan; -3 dB per side at centre.
static inline void PanGains(int pan, float &l, float &r)
{
    float theta = (float)pan / PAN_MAX * 1.5707963f;   // 0 .. π/2
    l = cosf(theta);
    r = sinf(theta);
    if (pan == PAN_CENTRE) l = r = 0.70710678f;        // exact -3 dB
    if (pan <= 0)       { l = 1.0f; r = 0.0f; }
    if (pan >= PAN_MAX) { l = 0.0f; r = 1.0f; }
}

// One-pole glide. Snaps onto the target once within 1e-4 (-80 dB): closer
// than that, the per-sample step can fall below float precision near
// gains of 1-2 and the value would stall a hair short of the target.
static inline float Glide(float cur, float target, float k)
{
    cur += (target - cur) * k;
    return fabsf(target - cur) < 1e-4f ? target : cur;
}

void mi::DesignKWeighting(int rate)
{
    KShelfCoefs   (rate, kShelf.b0,    kShelf.b1,    kShelf.b2,    kShelf.a1,    kShelf.a2);
    KHighPassCoefs(rate, kHighPass.b0, kHighPass.b1, kHighPass.b2, kHighPass.a1, kHighPass.a2);
    kShelf.Reset();
    kHighPass.Reset();
    kRate     = rate;
    blockLen  = (rate + 5) / 10;          // 100 ms
    blockFill = 0;
    blockSum  = 0.0;
}

void mi::Process(float const * const *inputs, float **outputs, int n, int channels)
{
    int sr = pMasterInfo->SamplesPerSec;
    if (sr > 0) cachedSr = sr;

    // Smoothing coefficient for Gain / Volume / Pan.
    float k = cachedSr > 0 ? 1.0f - expf(-1.0f / (SMOOTH_SECONDS * cachedSr)) : 1.0f;

    float fadeSeconds = inertiaMs * 0.001f;
    float muteStep = (fadeSeconds > 0.0f && cachedSr > 0)
        ? 1.0f / (cachedSr * fadeSeconds)
        : 1.0f;

    // Per-channel level targets.
    float tgtL[MaxChannels], tgtR[MaxChannels];
    for (int i = 0; i < MaxChannels; i++)
    {
        float v = LevelGain(volumeCode[i]), bl, br;
        if (mono[i]) PanGains(panPos[i], bl, br);
        else         BalanceGains(panPos[i], bl, br);
        tgtL[i] = v * bl;
        tgtR[i] = v * br;
    }
    float tgtGain = LevelGain(gainCode);

    // First block after creation/load: start AT the targets (no fade-in).
    if (!inMuteInitialized)
    {
        bool anySoloInit = false;
        for (int i = 0; i < channels; i++)
            if (solo[i]) anySoloInit = true;
        for (int i = 0; i < MaxChannels; i++)
        {
            currentInMuteGain[i] = inMute[i] ? 0.0f : 1.0f;
            currentSoloGain[i]   = (anySoloInit && !solo[i]) ? 0.0f : 1.0f;
        }
        inMuteInitialized = true;
    }
    if (!levelsInitialized)
    {
        for (int i = 0; i < MaxChannels; i++)
        {
            curL[i] = tgtL[i];
            curR[i] = tgtR[i];
            curMono[i] = mono[i] ? 1.0f : 0.0f;
        }
        curGain = tgtGain;
        levelsInitialized = true;
    }

    // Master bus: output 0, or scratch if nothing is connected there.
    float *master = (outputs != nullptr && outputs[0] != nullptr) ? outputs[0] : scratch;
    for (int s = 0; s < n * 2; s++)
        master[s] = 0.0f;

    bool anySolo = false;
    for (int i = 0; i < channels; i++)
        if (solo[i]) { anySolo = true; break; }

    // This block's meter statistics (merged into every slot at the end).
    float  bPre[MaxChannels] = {}, bPost[MaxChannels] = {};
    double bPreSq[MaxChannels] = {}, bPostSq[MaxChannels] = {};

    for (int i = 0; i < channels; i++)
    {
        float const *in  = (inputs  != nullptr) ? inputs[i]      : nullptr;
        float       *dir = (outputs != nullptr) ? outputs[i + 1] : nullptr;

        if (in == nullptr)
        {
            // Nothing to process: park every ramp at its target.
            currentInMuteGain[i] = inMute[i] ? 0.0f : 1.0f;
            currentSoloGain[i]   = (anySolo && !solo[i]) ? 0.0f : 1.0f;
            curL[i] = tgtL[i];
            curR[i] = tgtR[i];
            curMono[i] = mono[i] ? 1.0f : 0.0f;
            if (dir != nullptr)
                for (int s = 0; s < n * 2; s++) dir[s] = 0.0f;
            continue;
        }

        float p = 0.0f, pp = 0.0f;       // pre- and post-fader peaks
        float psq = 0.0f, ppsq = 0.0f;   // pre- and post-fader Σ (l² + r²)
        float soloTarget = (anySolo && !solo[i]) ? 0.0f : 1.0f;
        float soloGain   = currentSoloGain[i];
        float inTarget    = inMute[i] ? 0.0f : 1.0f;
        float inGain      = currentInMuteGain[i];
        float cl = curL[i], cr = curR[i];
        float tl = tgtL[i], tr = tgtR[i];
        float cm = curMono[i], tm = mono[i] ? 1.0f : 0.0f;

        for (int s = 0; s < n; s++)
        {
            float l = in[2 * s];
            float r = in[2 * s + 1];

            if (inGain < inTarget)      inGain = fminf2(inGain + muteStep, inTarget);
            else if (inGain > inTarget) inGain = fmaxf2(inGain - muteStep, inTarget);

            if (soloGain < soloTarget)      soloGain = fminf2(soloGain + muteStep, soloTarget);
            else if (soloGain > soloTarget) soloGain = fmaxf2(soloGain - muteStep, soloTarget);

            cl = Glide(cl, tl, k);
            cr = Glide(cr, tr, k);
            cm = Glide(cm, tm, k);

            // Channel strip: mono (crossfaded) → mute × fader × balance.
            float avg = (l + r) * 0.5f;
            float ml  = l + (avg - l) * cm;
            float mr  = r + (avg - r) * cm;
            float sl  = ml * inGain * cl;
            float sr  = mr * inGain * cr;

            // Master mix adds the solo gate (Gain applied below).
            master[2 * s]     += sl * soloGain;
            master[2 * s + 1] += sr * soloGain;

            // Direct out: the channel strip as-is (post-fader, post-pan).
            if (dir != nullptr)
            {
                dir[2 * s]     = sl;
                dir[2 * s + 1] = sr;
            }

            float a = fmaxf2(fabsf(l), fabsf(r));
            if (a > p) p = a;
            float b = fmaxf2(fabsf(sl), fabsf(sr));
            if (b > pp) pp = b;
            psq  += l * l + r * r;
            ppsq += sl * sl + sr * sr;
        }

        currentInMuteGain[i] = inGain;
        currentSoloGain[i]   = soloGain;
        curL[i] = cl; curR[i] = cr; curMono[i] = cm;
        bPre[i]    = p;
        bPost[i]   = pp;
        bPreSq[i]  = 0.5 * psq;
        bPostSq[i] = 0.5 * ppsq;
    }

    // Channels above the current count: park ramps at target and clear meters,
    // so re-enabling them later is glitch-free.
    for (int i = channels; i < MaxChannels; i++)
    {
        currentInMuteGain[i] = inMute[i] ? 0.0f : 1.0f;
        currentSoloGain[i]   = (anySolo && !solo[i]) ? 0.0f : 1.0f;
        curL[i] = tgtL[i];
        curR[i] = tgtR[i];
        curMono[i] = mono[i] ? 1.0f : 0.0f;
    }

    // Master: Gain (smoothed) × Master Mute ramp, collect peaks.
    float targetMuteGain = masterMute ? 0.0f : 1.0f;
    if (!muteInitialized)
    {
        currentMuteGain = targetMuteGain;
        muteInitialized = true;
    }

    float g = curGain;
    float *tl = tpBuf[0] + TP_HIST;     // this block's master samples, after the history
    float *tr = tpBuf[1] + TP_HIST;
    float  msqL = 0.0f, msqR = 0.0f;

    // Loudness blocks completed during this call (at most 1 per 100 ms, so
    // 2 is ample for any block size up to MAX_BUFFER_LENGTH), and correlation.
    if (cachedSr > 0 && cachedSr != kRate) DesignKWeighting(cachedSr);
    float  doneBlocks[4];
    int    nDone = 0;
    double cLL = 0.0, cLR = 0.0, cRR = 0.0;
    double const invFs = 1.0 / FULL_SCALE;
    for (int s = 0; s < n; s++)
    {
        if (currentMuteGain < targetMuteGain)
            currentMuteGain = fminf2(currentMuteGain + muteStep, targetMuteGain);
        else if (currentMuteGain > targetMuteGain)
            currentMuteGain = fmaxf2(currentMuteGain - muteStep, targetMuteGain);

        g = Glide(g, tgtGain, k);

        float effG = g * currentMuteGain;
        float l = master[2 * s]     * effG;
        float r = master[2 * s + 1] * effG;
        master[2 * s]     = l;
        master[2 * s + 1] = r;
        tl[s] = l;
        tr[s] = r;
        msqL += l * l;
        msqR += r * r;

        // Correlation sums (unweighted, full scale = 1.0).
        double nl = l * invFs, nr = r * invFs;
        cLL += nl * nl;
        cLR += nl * nr;
        cRR += nr * nr;

        // K-weighted energy into 100 ms blocks.
        if (blockLen > 0)
        {
            double kl = kHighPass.Run(0, kShelf.Run(0, nl));
            double kr = kHighPass.Run(1, kShelf.Run(1, nr));
            blockSum += kl * kl + kr * kr;
            if (++blockFill >= blockLen)
            {
                if (nDone < 4) doneBlocks[nDone++] = (float)(blockSum / blockLen);
                blockSum  = 0.0;
                blockFill = 0;
            }
        }
    }
    curGain = g;
    float bTp[2] = { 0.0f, 0.0f };

    // True peak: 4x oversampled peak of each master side.
    for (int c = 0; c < 2; c++)
    {
        float *x = tpBuf[c];
        float peak = 0.0f;
        for (int m = TP_HIST; m < TP_HIST + n; m++)
        {
            float const *win = x + m - TP_HIST;           // x[m-11 .. m]
            for (int ph = 0; ph < TP_PHASES; ph++)
            {
                float const *h = g_tpCoef[ph];
                float y = 0.0f;
                for (int j = 0; j < TP_TAPS; j++)
                    y += h[j] * win[TP_HIST - j];         // j = age of the sample
                float a = fabsf(y);
                if (a > peak) peak = a;
            }
        }
        // Carry the last TP_HIST samples into the next block.
        for (int j = 0; j < TP_HIST; j++)
            x[j] = x[n + j];
        bTp[c] = peak;
    }

    // Merge this block into every reader slot.
    LockMeters();
    for (int sl = 0; sl < METER_SLOTS; sl++)
    {
        MeterAcc &a = meterAcc[sl];
        for (int i = 0; i < channels; i++)
        {
            if (bPre[i]  > a.prePeak[i])  a.prePeak[i]  = bPre[i];
            if (bPost[i] > a.postPeak[i]) a.postPeak[i] = bPost[i];
            a.preSq[i]  += bPreSq[i];
            a.postSq[i] += bPostSq[i];
        }
        if (bTp[0] > a.tpL) a.tpL = bTp[0];
        if (bTp[1] > a.tpR) a.tpR = bTp[1];
        a.sqL   += msqL;
        a.sqR   += msqR;
        a.count += n;

        if (sl > 0)   // loudness/correlation only for meter-window slots
        {
            for (int b = 0; b < nDone; b++)
            {
                if (a.nBlocks < LOUD_BLOCK_CAP) a.blocks[a.nBlocks++] = doneBlocks[b];
                else                            a.dropped++;
            }
            a.cLL += cLL;
            a.cLR += cLR;
            a.cRR += cRR;
        }
    }
    UnlockMeters();
}

char const *mi::GetChannelName(bool input, int index)
{
    int channels = numChannels.load(std::memory_order_relaxed);
    if (input  && index >= 0 && index < channels)  return g_inNames[index];
    if (!input && index >= 0 && index <= channels) return g_outNames[index];
    return NULL;
}

bool mi::HandleGUIMessage(CMachineDataOutput *pout, CMachineDataInput *pin)
{
    if (pout == nullptr || pin == nullptr) return false;

    int id = 0;
    pin->Read(id);

    int channels = numChannels.load(std::memory_order_relaxed);
    float const inv  = 1.0f / FULL_SCALE;
    double const inv2 = 1.0 / ((double)FULL_SCALE * FULL_SCALE);

    if (id == GUIMSG_GET_METERS)
    {
        // Parameter panel: slot 0, v3 layout (unchanged since v1.5).
        MeterAcc a;
        LockMeters();
        a = meterAcc[0];
        meterAcc[0].Clear();
        UnlockMeters();

        pout->Write(GUI_PROTOCOL_VERSION);
        pout->Write(channels);
        for (int i = 0; i < channels; i++)
        {
            pout->Write(a.prePeak[i]  * inv);
            pout->Write(a.postPeak[i] * inv);
        }
        pout->Write(a.tpL * inv);
        pout->Write(a.tpR * inv);
        return true;
    }

    if (id == GUIMSG_GET_METERS_EX || id == GUIMSG_GET_METERS_V5)
    {
        bool v5 = id == GUIMSG_GET_METERS_V5;
        int slot = 0;
        pin->Read(slot);
        if (slot < 1 || slot >= METER_SLOTS) return false;

        MeterAcc a;
        LockMeters();
        a = meterAcc[slot];
        meterAcc[slot].Clear();
        UnlockMeters();

        double norm = a.count > 0 ? inv2 / a.count : 0.0;
        pout->Write(v5 ? GUI_PROTOCOL_V5 : GUI_PROTOCOL_EX);
        pout->Write(channels);
        for (int i = 0; i < channels; i++)
        {
            pout->Write(a.prePeak[i]  * inv);
            pout->Write(a.postPeak[i] * inv);
            pout->Write((float)(a.preSq[i]  * norm));
            pout->Write((float)(a.postSq[i] * norm));
        }
        pout->Write(a.tpL * inv);
        pout->Write(a.tpR * inv);
        pout->Write((float)(a.sqL * norm));
        pout->Write((float)(a.sqR * norm));

        if (v5)
        {
            double cn = a.count > 0 ? 1.0 / a.count : 0.0;
            pout->Write(kRate);
            pout->Write(a.nBlocks);
            pout->Write(a.dropped);
            for (int b = 0; b < a.nBlocks; b++)
                pout->Write(a.blocks[b]);
            pout->Write((float)(a.cLL * cn));
            pout->Write((float)(a.cLR * cn));
            pout->Write((float)(a.cRR * cn));
        }
        return true;
    }

    return false;
}

// ── miex forwarding ────────────────────────────────────────────────────────
void miex::MultiWork(float const * const *inputs, float **outputs, int numsamples)
{
    if (pmi) pmi->MultiWork(inputs, outputs, numsamples);
}

char const *miex::GetChannelName(bool input, int index)
{
    return pmi ? pmi->GetChannelName(input, index) : NULL;
}

bool miex::HandleGUIMessage(CMachineDataOutput *pout, CMachineDataInput *pin)
{
    return pmi ? pmi->HandleGUIMessage(pout, pin) : false;
}

DLL_EXPORTS
