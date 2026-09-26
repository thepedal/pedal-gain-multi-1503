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
//   Mutes keep their own Inertia fade.
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
static byte const SAVE_VERSION = 1;

// ── GUI message protocol (shared with PedalGainMultiNGUI.cs) ───────────────
// Request : int32 message id
// Reply   : GUIMSG_GET_METERS → int32 protocol version (2), int32 channel count N,
//           float MeterIn[N], float MeterL, float MeterR     (master out)
static int const GUIMSG_GET_METERS    = 1;
static int const GUI_PROTOCOL_VERSION = 2;

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
{ pt_word, "Gain", "Master fader in percent (output 0 only). 100 = unity, 200 = +6 dB.",
  0, 200, 0xFFFF, MPF_STATE, 100 };

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
{ pt_byte, "Volume", "Channel fader in percent (master mix and direct out). 100 = unity, 200 = +6 dB.",
  0, 200, 0xFF, MPF_STATE, 100 };

static CMachineParameter const paraPan =
{ pt_byte, "Pan", "Channel balance (master mix and direct out). 0 = left, 64 = centre, 128 = right.",
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
    int   gainPct = 100;
    bool  solo[MaxChannels]   = {};
    bool  inMute[MaxChannels] = {};
    bool  masterMute = false;
    int   volumePct[MaxChannels];
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
    bool  inMuteInitialized = false;
    float currentMuteGain   = 1.0f;
    bool  muteInitialized   = false;
    int   cachedSr          = 0;

    // Master-bus scratch, used when nothing is connected to output 0 so the
    // master meters still work.
    float scratch[MAX_BUFFER_LENGTH * 2];

    // Meters — audio thread writes, UI thread reads.
    std::atomic<float> meterIn[MaxChannels];
    std::atomic<float> meterL;
    std::atomic<float> meterR;
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
        volumePct[i] = 100;
        panPos[i]    = PAN_CENTRE;
        curL[i] = curR[i] = 1.0f;
        curMono[i] = 0.0f;
        meterIn[i].store(0.0f, std::memory_order_relaxed);
    }
    meterL.store(0.0f, std::memory_order_relaxed);
    meterR.store(0.0f, std::memory_order_relaxed);

    InitChannelNames();
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
    for (int i = (n < 0 ? 0 : n); i < cur && i < MaxChannels; i++)
    {
        solo[i]      = false;
        inMute[i]    = false;
        volumePct[i] = paraVolume.DefValue;
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
    if (gval.gain != paraGain.NoValue)       gainPct    = gval.gain;
    if (gval.masterMute != SWITCH_NO)        masterMute = gval.masterMute != SWITCH_OFF;
    if (gval.inertia != paraInertia.NoValue) inertiaMs  = gval.inertia;

    int channels = numChannels.load(std::memory_order_relaxed);
    for (int i = 0; i < channels; i++)
    {
        if (tval[i].solo   != SWITCH_NO)          solo[i]      = tval[i].solo != SWITCH_OFF;
        if (tval[i].mute   != SWITCH_NO)          inMute[i]    = tval[i].mute != SWITCH_OFF;
        if (tval[i].volume != paraVolume.NoValue) volumePct[i] = tval[i].volume;
        if (tval[i].pan    != paraPan.NoValue)    panPos[i]    = tval[i].pan;
        if (tval[i].mono   != SWITCH_NO)          mono[i]      = tval[i].mono != SWITCH_OFF;
    }
}

char const *mi::DescribeValue(int const param, int const value)
{
    switch (param)
    {
    case P_GAIN:
        if (value <= 0)
            return "0% (-inf dB)";
        snprintf(descBuf, sizeof(descBuf), "%d%% (%+.1f dB)", value, 20.0 * log10(value * 0.01));
        return descBuf;

    case P_VOLUME:
        if (value <= 0)
            return "0% (-inf dB)";
        snprintf(descBuf, sizeof(descBuf), "%d%% (%+.1f dB)", value, 20.0 * log10(value * 0.01));
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

// One-pole glide. Snaps onto the target once within 1e-4 (-80 dB): closer
// than that, the per-sample step can fall below float precision near
// gains of 1-2 and the value would stall a hair short of the target.
static inline float Glide(float cur, float target, float k)
{
    cur += (target - cur) * k;
    return fabsf(target - cur) < 1e-4f ? target : cur;
}

void mi::Process(float const * const *inputs, float **outputs, int n, int channels)
{
    int sr = pMasterInfo->SamplesPerSec;
    if (sr > 0) cachedSr = sr;
    float decay = cachedSr > 0 ? expf(-2.302585f * n / cachedSr) : 0.95f;

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
        float v = volumePct[i] * 0.01f, bl, br;
        BalanceGains(panPos[i], bl, br);
        tgtL[i] = v * bl;
        tgtR[i] = v * br;
    }
    float tgtGain = gainPct * 0.01f;

    // First block after creation/load: start AT the targets (no fade-in).
    if (!inMuteInitialized)
    {
        for (int i = 0; i < MaxChannels; i++)
            currentInMuteGain[i] = inMute[i] ? 0.0f : 1.0f;
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

    for (int i = 0; i < channels; i++)
    {
        float const *in  = (inputs  != nullptr) ? inputs[i]      : nullptr;
        float       *dir = (outputs != nullptr) ? outputs[i + 1] : nullptr;

        if (in == nullptr)
        {
            // Nothing to process: park every ramp at its target.
            meterIn[i].store(meterIn[i].load(std::memory_order_relaxed) * decay, std::memory_order_relaxed);
            currentInMuteGain[i] = inMute[i] ? 0.0f : 1.0f;
            curL[i] = tgtL[i];
            curR[i] = tgtR[i];
            curMono[i] = mono[i] ? 1.0f : 0.0f;
            if (dir != nullptr)
                for (int s = 0; s < n * 2; s++) dir[s] = 0.0f;
            continue;
        }

        float p = 0.0f;
        float effSoloGain = (anySolo && !solo[i]) ? 0.0f : 1.0f;
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
            master[2 * s]     += sl * effSoloGain;
            master[2 * s + 1] += sr * effSoloGain;

            // Direct out: the channel strip as-is (post-fader, post-pan).
            if (dir != nullptr)
            {
                dir[2 * s]     = sl;
                dir[2 * s + 1] = sr;
            }

            float a = fmaxf2(fabsf(l), fabsf(r));
            if (a > p) p = a;
        }

        currentInMuteGain[i] = inGain;
        curL[i] = cl; curR[i] = cr; curMono[i] = cm;
        meterIn[i].store(fmaxf2(p / FULL_SCALE, meterIn[i].load(std::memory_order_relaxed) * decay),
                         std::memory_order_relaxed);
    }

    // Channels above the current count: park ramps at target and clear meters,
    // so re-enabling them later is glitch-free.
    for (int i = channels; i < MaxChannels; i++)
    {
        currentInMuteGain[i] = inMute[i] ? 0.0f : 1.0f;
        curL[i] = tgtL[i];
        curR[i] = tgtR[i];
        curMono[i] = mono[i] ? 1.0f : 0.0f;
        meterIn[i].store(0.0f, std::memory_order_relaxed);
    }

    // Master: Gain (smoothed) × Master Mute ramp, collect peaks.
    float targetMuteGain = masterMute ? 0.0f : 1.0f;
    if (!muteInitialized)
    {
        currentMuteGain = targetMuteGain;
        muteInitialized = true;
    }

    float g = curGain;
    float peakL = 0.0f, peakR = 0.0f;
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

        float al = fabsf(l), ar = fabsf(r);
        if (al > peakL) peakL = al;
        if (ar > peakR) peakR = ar;
    }
    curGain = g;

    meterL.store(fmaxf2(peakL / FULL_SCALE, meterL.load(std::memory_order_relaxed) * decay), std::memory_order_relaxed);
    meterR.store(fmaxf2(peakR / FULL_SCALE, meterR.load(std::memory_order_relaxed) * decay), std::memory_order_relaxed);
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

    if (id == GUIMSG_GET_METERS)
    {
        int channels = numChannels.load(std::memory_order_relaxed);
        pout->Write(GUI_PROTOCOL_VERSION);
        pout->Write(channels);
        for (int i = 0; i < channels; i++)
            pout->Write(meterIn[i].load(std::memory_order_relaxed));
        pout->Write(meterL.load(std::memory_order_relaxed));
        pout->Write(meterR.load(std::memory_order_relaxed));
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
