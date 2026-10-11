// #name WetLab
// #author stp3
// #desc Localised clothes wetting (darker, glossier fabric around the crotch) with drips and a floor puddle, for CharaStudio.
// #proc_filter StudioNEOV2

// Stage 2. Loaded by BepInEx.ScriptLoader together with the other scripts in <game>\scripts (C# 7.x, mcs).
//
// The game's siru/weathering layer only paints white droplets, so the fabric itself is edited instead:
// each bottom garment's _MainTex is copied (Blit + ReadPixels, works for non-readable textures) and, where
// the stain has reached, blended toward a darker, slightly more saturated version; if the material has a
// _MetallicGlossMap its alpha (gloss) is raised in the same area. Where the stain is comes from a per-texel
// distance map: every vertex gets an anisotropic distance from the crotch (spreads easily downward, little
// upward, noisy edge), the triangles are rasterised into the texture's UV space, and the wet front grows by
// thresholding it. Texels are bucketed by distance so a growth step only touches the moving edge.
// Drips fall from the lowest wet points (tracked on bones) and pool on the floor.
// Gloss: a copy of each wet garment part is drawn over it with the shader's own alpha-blend variant
// (_ALPHABLEND_ON, _Mode 2, Src/Dst blend 5/10, no ZWrite), the game's wet gloss (_ExGloss) and a high
// smoothness; its main-texture alpha is the stain mask, so only the wet area shines.
// The stain origin defaults to just below the fly of the trousers and can be picked by clicking the body.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using AIChara;
using BepInEx.Logging;
using HarmonyLib;
using KKAPI.Studio;
using Studio;
using UnityEngine;
using UnityEngine.Rendering;

public static class WetLabScript
{
    static GameObject host;
    static Harmony harmony;

    public static void Main()
    {
        WetLab.Log = BepInEx.Logging.Logger.CreateLogSource("WetLab");
        harmony = Harmony.CreateAndPatchAll(typeof(WetLabPatches), "stp3.wetlab");
        host = new GameObject("WetLab");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<WetLab>();
        WetLab.Log.LogInfo("WetLab loaded. Open it from the Studio left toolbar.");
    }

    public static void Unload()
    {
        if (host != null) UnityEngine.Object.DestroyImmediate(host);
        host = null;
        if (harmony != null) harmony.UnpatchSelf();
        harmony = null;
        if (WetLab.Log != null) BepInEx.Logging.Logger.Sources.Remove(WetLab.Log);
    }
}

public static class WetLabPatches
{
    // (WetLab.BlockCamera: hovering the window, or waiting for an origin-pick click)
    // Clicking or scrolling on the window must not also drive the Studio camera.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Studio.CameraControl), "InputMouseProc")]
    public static bool CameraMousePrefix(ref bool __result)
    {
        if (!WetLab.BlockCamera) return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Studio.CameraControl), "InputMouseWheelZoomProc")]
    public static bool CameraWheelPrefix(ref bool __result)
    {
        if (!WetLab.MouseOverWindow) return true;
        __result = false;
        return false;
    }
}

public class WetLab : MonoBehaviour
{
    public static ManualLogSource Log;
    public static bool MouseOverWindow;

    // ---------------------------------------------------------------- settings (persisted)
    public int settingsVersion = 4;         // settings files from older versions are ignored (defaults changed)
    public float maxRadius = 2.2f;          // units (1 unit = 10 cm) the stain reaches at full spread
    public float spreadTime = 6.04f;          // s from start to full spread (radius grows like sqrt(t))
    public float upFactor = 3.0f;           // >1: spreads less upward
    public float downFactor = 0.75f;        // <1: spreads further downward
    public float frontFactor = 1.0f;
    public float backFactor = 1.3f;
    public float sideFactor = 3.0f;
    public float edgeNoise = 0.35f;         // units of distance jitter along the stain edge
    public float feather = 0.195f;            // units of soft edge
    // Default origin: where a level line from in front meets the clothes (or skin) in front of the crotch bone
    // (cf_J_Kokan). originDown lowers that line, originOut moves the origin off the surface (+) or into it (-), in
    // units (1 = 10 cm). It replaced "Crotch down / fwd", multiples of the thigh length from the hip joints, which put
    // the origin in mid-air on characters with other proportions.
    public float originDown = 0f;
    public float originOut = 0f;
    public bool usePickedOrigin = false;
    float darken = 1f;                      // (former setting) darker wet looks are made with a black stain instead
    public float saturate = 1.15f;
    public float wetGloss = 0.845f;          // gloss (MetallicGlossMap alpha) in the wet area, if the map exists
    public float tintR = 1f, tintG = 1f, tintB = 1f;   // wet colour multiplier (red for blood)
    public bool glossShell = true;
    public float shellGloss = 0.9f;         // _ExGloss of the gloss overlay
    public float shellSmooth = 0.8f;        // _Glossiness of the gloss overlay
    public float shellOpacity = 0.85f;
    public bool includeSkin = true;
    public bool includeHair = true;
    public bool showMarker = true;         // the pink origin cross (hide it for screenshots)
    public float outerResist = 1.5f;        // extra distance (x body scale) for going round a thigh to its outer side
    public float stainR = 0f, stainG = 0f, stainB = 0f;
    public float stainAmount = 0.3f;          // 0: water (darken only); ~0.85: coloured stain such as blood
    public float skinWater = 0.15f;           // how much a water stain shows on skin (0: none; skin is attached only for stains)
    public int maxSkinTexSize = 4096;
    public int maxTexSize = 2048;
    // drips & puddle
    public bool drips = true;
    public float dripRate = 18f;            // drops/s at the peak (while spreading)
    public float dripTail = 8f;             // s the dripping continues (tapering) after full spread
    public float dropSize = 0.031f;          // units
    public float puddleGrowth = 1.0f;       // puddle area per drop (relative)
    public float floorOffset = 0f;          // units; moves the floor (and the puddles on it) up or down at once
    public int floorMode = 0;               // 0 map surface under the crotch (feet estimate where the map has none), 1 feet estimate, 2 fixed
    public float floorHeight = 0f;          // floorMode 2: the height kept (taken from the floor when switching to it)
    public bool skinDrips = false;          // drips also from skin and face (e.g. a head wound); off: only from clothes and hair
    // Set by StudioBridge scripts for the attach they make (they probe the map floor themselves). Not saved, and
    // cleared by "Attach to selected": a saved script floor once put every later puddle at ankle height on another map.
    public bool useFixedFloor = false;
    public float fixedFloorY = 0f;          // world height of that floor
    static readonly HashSet<string> NotSaved = new HashSet<string> { "useFixedFloor", "fixedFloorY" };
    public float dropR = 0.85f, dropG = 0.88f, dropB = 0.75f, dropA = 0.2f, dropShine = 0.6f;
    public bool puddlePremultiply = true;
    public float puddleScale = 1.5f;
    public float loopHold = 2f;            // s the fully wet state is held before a loop restarts       // drawn puddle radius relative to the collected volume
    public float puddleEdge = 0.04f;       // fraction of the radius over which the puddle rim fades (small = sharp)
    public bool puddleReflection = true;  // real mirror image of the scene in the puddles
    public float reflectStrength = 0.85f;
    public float reflectResolution = 0.5f; // of the screen
    public float puddleR = 0.1f, puddleG = 0.097f, puddleB = 0.097f, puddleA = 0.8f, puddleShine = 1.0f;
    public float uiScale = 0f;

    // ---------------------------------------------------------------- state
    class Target
    {
        public Material mat;
        public string label;
        public int w, h;
        public float[] dist;            // per texel; +inf where the garment does not cover
        public List<int>[] bins;        // texel indices bucketed by distance
        public Color32[] orig, wet, cur;
        public Texture2D tex;           // the edited _MainTex
        public Texture origTex;
        public Color32[] gOrig, gWet, gCur;
        public Texture2D gTex;          // the edited _MetallicGlossMap (null if the material has none)
        public Texture origGloss;
        // gloss overlay
        public GameObject shell;
        public SkinnedMeshRenderer shellSmr, srcSmr;
        public Renderer shellRend, srcRend;
        public Material shellMat;
        public Texture2D shellTex;
        public Color32[] shellCur;
        public Color[] shellBaseColors;
        public Color32[] shellWet;      // overlay colour of a fully wet texel (undarkened wet colour)
        public byte[] shellBaked;       // overlay alpha kept from earlier stain layers
        public Mesh shellMesh;
        // geometry, kept so the stain shape can be rebuilt without re-reading textures
        public RendData rdata;
        public int[] tris;
        public bool isSkin;
    }

    class RendData
    {
        public Renderer rd;
        public SkinnedMeshRenderer smr;
        public Vector3[] world;
        public Vector2[] uv;
        public float[] dv;
    }
    List<RendData> rends = new List<RendData>();
    Vector3 fC, fU, fF, fR; float fS;
    Vector3 legUpL, legUpR, legLowL, legLowR;
    string shapeSig = "", lookSig = "";
    bool pendShape, pendLook;
    float rebuildAt = -1f;

    struct Emitter { public Transform bone; public Vector3 local; public float d; }
    struct Drop { public Vector3 pos, vel; public float size; }
    struct Splash { public Vector3 pos, vel; public float life; }
    class Puddle { public Vector3 center; public float area; public float seed; public GameObject go; public Mesh mesh; public float builtArea = -1f; public bool frozen; public Color col; }

    float PuddleRadius(Puddle pd) { return Mathf.Sqrt(pd.area / Mathf.PI) * puddleScale; }

    Color PuddleColor(Puddle pd) { return pd.frozen ? pd.col : new Color(puddleR, puddleG, puddleB, puddleA); }
    Material puddleMat;
    Texture2D puddleTex;
    string puddleSig = "";

    const float BinWidth = 0.05f;
    Transform pickedBone, originBone;
    Vector3 pickedLocal, originLocal;
    bool pickArmed;
    public static bool BlockCamera;
    List<Target> targets = new List<Target>();
    List<Emitter> emitters = new List<Emitter>();
    List<Drop> drops = new List<Drop>();
    List<Splash> splashes = new List<Splash>();
    List<Puddle> puddles = new List<Puddle>();
    ChaControl cha;
    float t;
    bool running;
    float appliedRadius = 0f;
    float floorY;
    string floorKey = "";
    Transform toeL, toeR;                   // for the feet estimate, found by BodyFrame
    float dropAccum;
    string status = "";
    bool showWindow = false;   // opened from the Studio left toolbar
    Rect windowRect = new Rect(-1, 60, 380, 600);
    Vector2 scroll;
    string defaults, lastSaved;
    readonly Dictionary<string, float> defaultValues = new Dictionary<string, float>();   // the plugin's own float defaults
    float autosaveT;
    Material fxMat;

    // ---------------------------------------------------------------- several characters
    // The fields above hold the character being edited. Attaching to another character parks them in a Rig (its
    // stains, layers, puddles and settings stay as they are, untouched) and starts afresh; attaching to a parked
    // character again brings its rig back. Before this, attaching elsewhere restored the first character's clothes.
    static readonly string[] RigFields =
    {
        "rends", "targets", "emitters", "drops", "splashes", "puddles", "stains",
        "fC", "fU", "fF", "fR", "fS", "legUpL", "legUpR", "legLowL", "legLowR",
        "shapeSig", "lookSig", "pendShape", "pendLook", "rebuildAt",
        "pickedBone", "originBone", "pickedLocal", "originLocal",
        "cha", "t", "running", "appliedRadius", "floorY", "floorKey", "toeL", "toeR", "dropAccum",
        "layers", "looping", "loopHoldT", "loopCount", "useFixedFloor", "fixedFloorY",
    };

    class Rig
    {
        public string name;
        public string settings;
        public Dictionary<string, object> v = new Dictionary<string, object>();
        public List<Puddle> Puddles { get { return (List<Puddle>)v["puddles"]; } }
        public List<Target> Targets { get { return (List<Target>)v["targets"]; } }
    }

    readonly Dictionary<ChaControl, Rig> parked = new Dictionary<ChaControl, Rig>();
    Dictionary<string, object> blank;

    static FieldInfo RigField(string n) { return typeof(WetLab).GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }

    Rig Capture()
    {
        Rig r = new Rig { name = cha != null ? cha.fileParam.fullname : "?", settings = SerializeSettings() };
        foreach (string n in RigFields) r.v[n] = RigField(n).GetValue(this);
        return r;
    }

    void Restore(Rig r)
    {
        foreach (string n in RigFields) RigField(n).SetValue(this, r.v[n]);
        ApplySettings(r.settings);
        shapeSig = ShapeSignature(); lookSig = LookSignature();   // its stain was built with these: no rebuild
    }

    // nothing attached (fresh collections, the fields' initial values); the parked rigs keep theirs
    void ResetState()
    {
        foreach (string n in RigFields)
        {
            FieldInfo f = RigField(n);
            if (typeof(System.Collections.ICollection).IsAssignableFrom(f.FieldType)) f.SetValue(this, Activator.CreateInstance(f.FieldType));
            else f.SetValue(this, blank[n]);
        }
    }

    // Parks the current character (if it has a stain) and switches to sel: its parked rig, or nothing yet.
    // Returns true when sel's rig was brought back.
    bool SwitchTo(ChaControl sel)
    {
        if (cha != null && targets.Count > 0) { parked[cha] = Capture(); ResetState(); }
        else if (cha != null) Detach();
        Rig r;
        if (!parked.TryGetValue(sel, out r)) return false;
        parked.Remove(sel);
        Restore(r);
        return true;
    }

    // A parked character that was deleted: free what was made for it (its materials went with it).
    void DisposeRig(Rig r)
    {
        foreach (Target tg in r.Targets)
        {
            if (tg.tex != null) Destroy(tg.tex);
            if (tg.gTex != null) Destroy(tg.gTex);
            if (tg.shell != null) Destroy(tg.shell);
            if (tg.shellMat != null) Destroy(tg.shellMat);
            if (tg.shellTex != null) Destroy(tg.shellTex);
            if (tg.shellMesh != null) Destroy(tg.shellMesh);
        }
        foreach (Puddle pd in r.Puddles)
        {
            if (pd.go != null) Destroy(pd.go);
            if (pd.mesh != null) Destroy(pd.mesh);
        }
    }

    // Restores the clothes of every character (the one being edited and the parked ones) and clears all puddles.
    void RemoveAll()
    {
        string keep = SerializeSettings();
        Detach(); ClearPuddles();
        foreach (Rig r in new List<Rig>(parked.Values))
        {
            Restore(r);
            Detach(); ClearPuddles();
            ResetState();
        }
        parked.Clear();
        ApplySettings(keep);
    }

    void PruneParked()
    {
        List<ChaControl> gone = null;
        foreach (KeyValuePair<ChaControl, Rig> kv in parked)
            if (kv.Key == null) { if (gone == null) gone = new List<ChaControl>(); gone.Add(kv.Key); }
        if (gone == null) return;
        foreach (ChaControl c in gone) { DisposeRig(parked[c]); parked.Remove(c); }
    }

    IEnumerable<Puddle> AllPuddles()
    {
        foreach (Puddle pd in puddles) yield return pd;
        foreach (Rig r in parked.Values) foreach (Puddle pd in r.Puddles) yield return pd;
    }

    bool AnyPuddles()
    {
        if (puddles.Count > 0) return true;
        foreach (Rig r in parked.Values) if (r.Puddles.Count > 0) return true;
        return false;
    }

    string ParkedNames()
    {
        List<string> l = new List<string>();
        foreach (Rig r in parked.Values) l.Add(r.name);
        return string.Join(", ", l.ToArray());
    }

    void Awake()
    {
        blank = new Dictionary<string, object>();
        foreach (string n in RigFields) blank[n] = RigField(n).GetValue(this);
        Camera.onPreCull += OnCameraPreCull;
        defaults = SerializeSettings();
        foreach (FieldInfo f in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (f.FieldType == typeof(float)) defaultValues[f.Name] = (float)f.GetValue(this);
        LoadSettings();
        lastSaved = SerializeSettings();
    }

    void OnDestroy()
    {
        RemoveToolbarButton();
        SaveSettings();
        RemoveAll();
        MouseOverWindow = false;
        if (fxMat != null) Destroy(fxMat);
        if (glintMat != null) Destroy(glintMat);
        Camera.onPreCull -= OnCameraPreCull;
        if (reflCam != null) Destroy(reflCam.gameObject);
        if (reflRT != null) { reflRT.Release(); Destroy(reflRT); }
        if (reflMat != null) Destroy(reflMat);
        ClearPuddles();
        if (puddleMat != null) Destroy(puddleMat);
        if (puddleTex != null) Destroy(puddleTex);
    }

    // ---------------------------------------------------------------- frame loop

    float Radius()
    {
        return maxRadius * Mathf.Sqrt(Mathf.Clamp01(spreadTime > 0f ? t / spreadTime : 1f));
    }

    void Update()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        if (!StudioAPI.StudioLoaded) { MouseOverWindow = false; return; }
        EnsureToolbarButton();
        SyncToolbarButton();

        float sc = UiScale();
        Vector2 gm = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / sc;
        MouseOverWindow = showWindow && windowRect.Contains(gm);
        BlockCamera = MouseOverWindow || pickArmed;
        if (pickArmed && !MouseOverWindow && Input.GetMouseButtonDown(0)) { pickArmed = false; PickOrigin(); }

        autosaveT += Time.unscaledDeltaTime;
        if (autosaveT > 3f)
        {
            autosaveT = 0f;
            string now = SerializeSettings();
            if (now != lastSaved) SaveSettings();
        }

        if (cha == null && targets.Count > 0) { Detach(); status = "Character gone."; }
        if (parked.Count > 0) PruneParked();
        if (targets.Count > 0)
        {
            string sig = ShapeSignature(), lsig = LookSignature();
            if (sig != shapeSig) { shapeSig = sig; pendShape = true; rebuildAt = Time.unscaledTime + 0.4f; }
            if (lsig != lookSig) { lookSig = lsig; pendLook = true; rebuildAt = Time.unscaledTime + 0.4f; }
            if (rebuildAt > 0f && Time.unscaledTime >= rebuildAt && !Input.GetMouseButton(0))
            {
                rebuildAt = -1f;
                float t0 = Time.realtimeSinceStartup;
                if (includeSkin && skinWater > 0f && !HasSkinTarget()) AttachIncremental();
                else BuildAll(false, pendShape, pendLook);
                pendShape = pendLook = false;
                status = "Rebuilt (" + (Time.realtimeSinceStartup - t0).ToString("0.00") + " s).";
            }
        }

        float dt = Time.deltaTime;
        if (targets.Count > 0)
        {
            if (running) t += dt;
            float r = Radius();
            if (Mathf.Abs(r - appliedRadius) > 0.04f || (r == 0f && appliedRadius != 0f) || (r == maxRadius && appliedRadius != maxRadius))
            {
                ApplyRadius(appliedRadius, r);
                appliedRadius = r;
            }
            if (running && t >= spreadTime + dripTail)
            {
                if (!looping) running = false;
                else
                {
                    // wait for the last drops to land, hold the wet state, then dry and start over
                    if (drops.Count == 0) loopHoldT += dt; else loopHoldT = 0f;
                    if (loopHoldT >= loopHold) RestartLoop();
                }
            }
            if (drips) EmitDrops(dt);
        }
        UpdateFloor();
        UpdateDrops(dt);
    }

    // ---------------------------------------------------------------- attach / detach

    ChaControl forcedSel;   // AttachKey (diagnostics) instead of the work-tree selection

    public string AttachKey(int key)
    {
        ObjectCtrlInfo o;
        if (!Singleton<Studio.Studio>.Instance.dicObjectCtrl.TryGetValue(key, out o) || !(o is OCIChar)) return "no character " + key;
        forcedSel = ((OCIChar)o).charInfo;
        try { useFixedFloor = false; AttachSelected(); } finally { forcedSel = null; }
        return status;
    }

    void AttachSelected()
    {
        ChaControl sel = forcedSel;
        if (sel == null)
            foreach (OCIChar oci in StudioAPI.GetSelectedCharacters())
                if (oci.charInfo != null) { sel = oci.charInfo; break; }
        if (sel != null && sel != cha && SwitchTo(sel))
        {
            status = "Editing " + sel.fileParam.fullname + " again (" + layers + " kept layer(s)). Others keep their stains.";
            return;
        }
        ChaControl previous = cha;
        Detach();
        cha = sel;
        if (cha == null) cha = previous;   // re-attach after moving the origin even if nothing is selected
        if (cha == null) { status = "Select a character in the work tree."; return; }
        if (!BodyFrame()) { status = "Bones not found."; cha = null; return; }

        float t0 = Time.realtimeSinceStartup;
        AddAllRenderers();
        BuildAll(true);
        t = 0f; running = false; appliedRadius = 0f; dropAccum = 0f;
        shapeSig = ShapeSignature(); lookSig = LookSignature();
        status = "Attached " + targets.Count + " material(s), " + emitters.Count + " drip points ("
            + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s). Press Start.";
        Log.LogInfo(status + " floorY=" + floorY.ToString("F2"));
    }

    // Clothes, skin, face and hair of the current character. Renderers already handled are skipped,
    // so this also serves the incremental attach.
    void AddAllRenderers()
    {
        // clothes kinds: 0 top, 1 bot, 2 inner_t, 3 inner_b, 4 gloves, 5 panst, 6 socks, 7 shoes
        int[] kinds = { 0, 1, 2, 3, 5, 6 };
        GameObject[] objs = cha.objClothes;
        foreach (int k in kinds)
        {
            if (objs == null || k >= objs.Length || objs[k] == null || !objs[k].activeInHierarchy) continue;
            foreach (Renderer rd in objs[k].GetComponentsInChildren<Renderer>(false))
            {
                if (!rd.enabled || rd.name.StartsWith("WetLabShell")) continue;
                try { AddRenderer(rd, "c" + k, false); }
                catch (Exception e) { Log.LogWarning("skip " + rd.name + ": " + e.Message); }
            }
        }
        if (includeSkin && skinWater > 0f
            && cha.cmpBody != null && cha.cmpBody.targetCustom != null && cha.cmpBody.targetCustom.rendBody != null)
        {
            try { AddRenderer(cha.cmpBody.targetCustom.rendBody, "skin", true); }
            catch (Exception e) { Log.LogWarning("skip skin: " + e.Message); }
        }
        // head: the face is a separate renderer (treated as skin), hair as fabric-like without a gloss overlay
        Renderer face = FaceRenderer(cha);
        if (includeSkin && skinWater > 0f && face != null)
        {
            try { AddRenderer(face, "face", true); }
            catch (Exception e) { Log.LogWarning("skip face: " + e.Message); }
        }
        if (includeHair && cha.objHair != null)
        {
            foreach (GameObject h in cha.objHair)
            {
                if (h == null || !h.activeInHierarchy) continue;
                foreach (Renderer rd in h.GetComponentsInChildren<Renderer>(false))
                {
                    if (!rd.enabled || rd.name.StartsWith("WetLabShell")) continue;
                    try { AddRenderer(rd, "hair", false, true); }
                    catch (Exception e) { Log.LogWarning("skip hair " + rd.name + ": " + e.Message); }
                }
            }
        }
    }

    // After moving the origin (or turning the skin on), add the parts that are now near enough without
    // re-reading anything already attached, so kept stain layers survive.
    void AttachIncremental()
    {
        if (cha == null || targets.Count == 0) { AttachSelected(); return; }
        if (!BodyFrame()) return;
        float t0 = Time.realtimeSinceStartup;
        int before = targets.Count;
        AddAllRenderers();
        BuildAll(false, true, true);
        t = 0f; running = false; looping = false; dropAccum = 0f;
        shapeSig = ShapeSignature(); lookSig = LookSignature();
        status = "Updated: +" + (targets.Count - before) + " material(s) (" + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s). Press Start.";
    }

    bool HasRend(Renderer rd)
    {
        foreach (RendData r in rends) if (r.rd == rd) return true;
        return false;
    }

    static Renderer FaceRenderer(ChaControl c)
    {
        return c != null && c.cmpFace != null && c.cmpFace.targetCustom != null ? c.cmpFace.targetCustom.rendHead : null;
    }

    void AddRenderer(Renderer rd, string label, bool skin) { AddRenderer(rd, label, skin, false); }

    void AddRenderer(Renderer rd, string label, bool skin, bool noShell)
    {
        if (HasRend(rd)) return;
        RendData r = new RendData();
        r.rd = rd;
        r.smr = rd as SkinnedMeshRenderer;
        if (!Bake(r)) return;
        ComputeDv(r);
        Mesh shared = r.smr != null ? r.smr.sharedMesh : (rd.GetComponent<MeshFilter>() != null ? rd.GetComponent<MeshFilter>().sharedMesh : null);
        if (shared == null) return;
        // body: the game's own per-character material (rd.materials would clone it and cut the game's updates off)
        Material[] mats = skin ? rd.sharedMaterials : rd.materials;
        int matCount = skin ? Mathf.Min(1, mats.Length) : mats.Length;   // skin: the body material only, not the siru overlay
        bool any = false;
        for (int mi = 0; mi < matCount; mi++)
        {
            Material m = mats[mi];
            if (m == null || !m.HasProperty("_MainTex")) continue;
            Vector2 sc = m.GetTextureScale("_MainTex"), of = m.GetTextureOffset("_MainTex");
            if (Mathf.Abs(sc.x - 1f) > 0.01f || Mathf.Abs(sc.y - 1f) > 0.01f || of.sqrMagnitude > 1e-4f)
            {
                Log.LogInfo("skip tiled main texture on " + rd.name + "#" + mi);
                continue;
            }
            int sub = Mathf.Min(mi, shared.subMeshCount - 1);
            int[] tris = shared.GetTriangles(sub);
            // generous cull, so later shape tweaks still find the part: never closer than radius + 3 units
            if (MinOver(tris, r.dv) > maxRadius + feather + 3f) continue;
            AddTarget(m, label + "/" + rd.name + "#" + mi, r, tris, sub, skin, noShell);
            any = true;
        }
        if (any) rends.Add(r);
    }

    bool Bake(RendData r)
    {
        Mesh mesh; Matrix4x4 toWorld;
        if (!GetPosedMesh(r.rd, out mesh, out toWorld)) return false;
        Vector3[] v = mesh.vertices;
        r.uv = mesh.uv;
        if (r.uv == null || r.uv.Length != v.Length) return false;
        r.world = new Vector3[v.Length];
        for (int i = 0; i < v.Length; i++) r.world[i] = toWorld.MultiplyPoint3x4(v[i]);
        if (r.smr != null) Destroy(mesh);
        return true;
    }

    void ComputeDv(RendData r)
    {
        if (r.dv == null || r.dv.Length != r.world.Length) r.dv = new float[r.world.Length];
        for (int i = 0; i < r.world.Length; i++)
            r.dv[i] = Distance(r.world[i]) - Noise(r.world[i] / Mathf.Max(0.1f, fS)) * edgeNoise;
    }

    static float MinOver(int[] tris, float[] dv)
    {
        float m = float.PositiveInfinity;
        for (int i = 0; i < tris.Length; i++) m = Mathf.Min(m, dv[tris[i]]);
        return m;
    }

    void AddTarget(Material m, string label, RendData r, int[] tris, int sub, bool skin, bool noShell)
    {
        Texture src = m.GetTexture("_MainTex");
        int cap = skin ? maxSkinTexSize : maxTexSize;
        int w = src != null ? src.width : 512, h = src != null ? src.height : 512;
        while (w > cap || h > cap) { w /= 2; h /= 2; }

        Target tg = new Target();
        tg.mat = m; tg.label = label; tg.w = w; tg.h = h;
        tg.rdata = r; tg.tris = tris; tg.isSkin = skin;
        tg.origTex = src;
        tg.orig = ReadTexture(src, w, h, false, new Color32(255, 255, 255, 255));
        tg.wet = new Color32[tg.orig.Length];
        tg.cur = (Color32[])tg.orig.Clone();
        tg.tex = new Texture2D(w, h, TextureFormat.RGBA32, true, false);
        tg.tex.name = "WetLab_" + (src != null ? src.name : "white");
        tg.tex.wrapMode = src != null ? src.wrapMode : TextureWrapMode.Repeat;
        tg.tex.anisoLevel = src != null ? src.anisoLevel : 1;

        if (!skin && m.HasProperty("_MetallicGlossMap") && m.GetTexture("_MetallicGlossMap") != null)
        {
            Texture g = m.GetTexture("_MetallicGlossMap");
            tg.origGloss = g;
            tg.gOrig = ReadTexture(g, w, h, true, new Color32(255, 255, 255, 255));
            tg.gWet = new Color32[tg.gOrig.Length];
            tg.gCur = (Color32[])tg.gOrig.Clone();
            tg.gTex = new Texture2D(w, h, TextureFormat.RGBA32, true, true);
            tg.gTex.name = "WetLabGloss_" + g.name;
            tg.gTex.wrapMode = g.wrapMode;
        }
        m.SetTexture("_MainTex", tg.tex);
        if (tg.gTex != null) m.SetTexture("_MetallicGlossMap", tg.gTex);
        if (glossShell && !skin && !noShell)
        {
            try { CreateShell(tg, r.rd, sub); }
            catch (Exception e) { Log.LogWarning("gloss overlay failed on " + label + ": " + e.Message); }
        }
        targets.Add(tg);
        Log.LogInfo("target " + label + " " + w + "x" + h + " gloss=" + (tg.gTex != null) + (skin ? " (skin)" : ""));
    }

    // (Re)compute everything that depends on the stain shape and look, then repaint the current radius.
    void BuildAll(bool firstTime) { BuildAll(firstTime, true, true); }

    void BuildAll(bool firstTime, bool shape, bool look)
    {
        if (!firstTime && shape)
        {
            if (!BodyFrame()) return;
            foreach (RendData r in rends) { if (r.rd != null && Bake(r)) ComputeDv(r); }
        }
        foreach (Target tg in targets)
        {
            if (look || firstTime)
            {
                for (int i = 0; i < tg.orig.Length; i++) tg.wet[i] = WetColor(tg.orig[i], tg.isSkin);
                if (tg.gWet != null)
                {
                    byte gt = (byte)(Mathf.Clamp01(wetGloss) * 255f);
                    for (int i = 0; i < tg.gOrig.Length; i++) { Color32 c = tg.gOrig[i]; if (c.a < gt) c.a = gt; tg.gWet[i] = c; }
                }
            }
            if (shape || firstTime)
            {
                tg.dist = Rasterise(tg.tris, tg.rdata.uv, tg.rdata.dv, tg.w, tg.h);
                int nb = Mathf.CeilToInt((maxRadius + 2f) / BinWidth) + 1;
                tg.bins = new List<int>[nb];
                for (int i = 0; i < tg.dist.Length; i++)
                {
                    float d = tg.dist[i];
                    if (float.IsInfinity(d) || d > maxRadius + feather) continue;
                    int b = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(0f, d) / BinWidth), 0, nb - 1);
                    if (tg.bins[b] == null) tg.bins[b] = new List<int>();
                    tg.bins[b].Add(i);
                }
            }
            Array.Copy(tg.orig, tg.cur, tg.orig.Length);
            if (tg.gCur != null) Array.Copy(tg.gOrig, tg.gCur, tg.gOrig.Length);
            if (tg.shellCur != null)
            {
                if (tg.shellWet == null) tg.shellWet = new Color32[tg.orig.Length];
                for (int i = 0; i < tg.wet.Length; i++)
                {
                    tg.shellWet[i] = WetColor(tg.orig[i], false, 1f);
                    Color32 c = tg.orig[i];
                    c.a = tg.shellBaked != null ? tg.shellBaked[i] : (byte)0;
                    tg.shellCur[i] = c;
                }
            }
        }
        if (shape || firstTime)
        {
            emitters.Clear();
            foreach (RendData r in rends)
                if (r.smr != null && (skinDrips || !IsSkinRend(r))) AddEmitters(r.smr, r.world, r.dv);
        }
        ApplyRadius(0f, Radius(), true);
        appliedRadius = Radius();
    }

    int layers;

    // Freeze the current stain into the textures (and the overlay's gloss), so another stain with other
    // settings can be put on top: e.g. blood first, then water. Puddles keep the colour they have now.
    void KeepLayer()
    {
        foreach (Target tg in targets)
        {
            Array.Copy(tg.cur, tg.orig, tg.cur.Length);
            if (tg.gCur != null) Array.Copy(tg.gCur, tg.gOrig, tg.gCur.Length);
            if (tg.shellCur != null)
            {
                if (tg.shellBaked == null) tg.shellBaked = new byte[tg.shellCur.Length];
                for (int i = 0; i < tg.shellCur.Length; i++)
                    if (tg.shellCur[i].a > tg.shellBaked[i]) tg.shellBaked[i] = tg.shellCur[i].a;
            }
        }
        foreach (Puddle pd in puddles)
        {
            if (pd.frozen) continue;
            pd.frozen = true;
            pd.col = new Color(puddleR, puddleG, puddleB, puddleA);
        }
        layers++;
        looping = false;
        t = 0f; running = false; dropAccum = 0f;
        BuildAll(false, false, true);
        status = "Kept " + layers + " layer(s). Set up the next stain and press Start.";
    }

    // Put every edited texture back to the original pixels (no stain at all).
    void DryAll()
    {
        foreach (Target tg in targets)
        {
            Array.Copy(tg.orig, tg.cur, tg.orig.Length);
            tg.tex.SetPixels32(tg.cur); tg.tex.Apply(true);
            if (tg.gCur != null) { Array.Copy(tg.gOrig, tg.gCur, tg.gOrig.Length); tg.gTex.SetPixels32(tg.gCur); tg.gTex.Apply(true); }
            if (tg.shellCur != null)
            {
                for (int i = 0; i < tg.shellCur.Length; i++)
                {
                    Color32 c = tg.orig[i];
                    c.a = tg.shellBaked != null ? tg.shellBaked[i] : (byte)0;
                    tg.shellCur[i] = c;
                }
                tg.shellTex.SetPixels32(tg.shellCur); tg.shellTex.Apply(true);
            }
        }
        appliedRadius = 0f;
    }

    bool HasSkinTarget()
    {
        foreach (Target tg in targets) if (tg.isSkin) return true;
        return false;
    }

    bool IsSkinRend(RendData r)
    {
        foreach (Target tg in targets) if (tg.rdata == r) return tg.isSkin;
        return false;
    }

    string ShapeSignature()
    {
        return Sig(new float[] { upFactor, downFactor, frontFactor, backFactor, sideFactor, outerResist, edgeNoise, feather,
            originDown, originOut, maxRadius, usePickedOrigin ? 1f : 0f });
    }

    string LookSignature()
    {
        return Sig(new float[] { darken, saturate, wetGloss, tintR, tintG, tintB, stainR, stainG, stainB, stainAmount, skinWater });
    }

    static string Sig(float[] v)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (float f in v) sb.Append(f.ToString("R", ci)).Append(';');
        return sb.ToString();
    }

    void Detach()
    {
        layers = 0;
        looping = false;
        foreach (Target tg in targets)
        {
            if (tg.mat != null)
            {
                tg.mat.SetTexture("_MainTex", tg.origTex);
                if (tg.gTex != null) tg.mat.SetTexture("_MetallicGlossMap", tg.origGloss);
            }
            if (tg.tex != null) Destroy(tg.tex);
            if (tg.gTex != null) Destroy(tg.gTex);
            // immediate, so a re-attach in the same frame does not find the overlay among the clothes
            if (tg.shell != null) DestroyImmediate(tg.shell);
            if (tg.shellMat != null) Destroy(tg.shellMat);
            if (tg.shellTex != null) Destroy(tg.shellTex);
            if (tg.shellMesh != null) Destroy(tg.shellMesh);
        }
        targets.Clear();
        rends.Clear();
        emitters.Clear();
        running = false;
        cha = null;
    }

    Color32 WetColor(Color32 c, bool skin) { return WetColor(c, skin, darken); }

    Color32 WetColor(Color32 c, bool skin, float dk)
    {
        float r0 = c.r / 255f, g0 = c.g / 255f, b0 = c.b / 255f;
        float l = 0.3f * r0 + 0.59f * g0 + 0.11f * b0;
        float r = Mathf.Clamp01((l + (r0 - l) * saturate) * dk * tintR);
        float g = Mathf.Clamp01((l + (g0 - l) * saturate) * dk * tintG);
        float b = Mathf.Clamp01((l + (b0 - l) * saturate) * dk * tintB);
        if (stainAmount > 0f)
        {
            // a coloured stain (blood) replaces the hue whatever the cloth colour is, keeping some of its shading
            float shade = 0.55f + 0.45f * l;
            r = Mathf.Lerp(r, stainR * shade, stainAmount);
            g = Mathf.Lerp(g, stainG * shade, stainAmount);
            b = Mathf.Lerp(b, stainB * shade, stainAmount);
        }
        if (skin)
        {
            float k = skinWater;   // how strongly the skin takes the stain, on its own slider
            r = Mathf.Lerp(r0, r, k); g = Mathf.Lerp(g0, g, k); b = Mathf.Lerp(b0, b, k);
        }
        return new Color32((byte)(r * 255f), (byte)(g * 255f), (byte)(b * 255f), c.a);
    }

    // Copy any texture (readable or not) into a Color32 array of the requested size.
    static Color32[] ReadTexture(Texture src, int w, int h, bool linear, Color32 fill)
    {
        if (src == null)
        {
            Color32[] f = new Color32[w * h];
            for (int i = 0; i < f.Length; i++) f[i] = fill;
            return f;
        }
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
            linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
        RenderTexture prev = RenderTexture.active;
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;
        Texture2D tmp = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
        tmp.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tmp.Apply(false);
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);
        Color32[] px = tmp.GetPixels32();
        Destroy(tmp);
        return px;
    }

    // ---------------------------------------------------------------- spread

    void ApplyRadius(float r0, float r1) { ApplyRadius(r0, r1, false); }

    void ApplyRadius(float r0, float r1, bool force)
    {
        float lo = Mathf.Min(r0, r1) - feather, hi = Mathf.Max(r0, r1);
        float inv = 1f / Mathf.Max(0.01f, feather);
        foreach (Target tg in targets)
        {
            if (tg.mat == null) continue;
            int b0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Max(0f, lo) / BinWidth) - 1);
            int b1 = Mathf.Min(tg.bins.Length - 1, Mathf.FloorToInt(hi / BinWidth) + 1);
            bool changed = force;
            for (int b = b0; b <= b1; b++)
            {
                List<int> list = tg.bins[b];
                if (list == null) continue;
                for (int j = 0; j < list.Count; j++)
                {
                    int i = list[j];
                    float f = r1 <= 0f ? 0f : Mathf.Clamp01((r1 - Mathf.Max(0f, tg.dist[i])) * inv);
                    tg.cur[i] = Color32.Lerp(tg.orig[i], tg.wet[i], f);
                    if (tg.gTex != null) tg.gCur[i] = Color32.Lerp(tg.gOrig[i], tg.gWet[i], f);
                    if (tg.shellCur != null)
                    {
                        Color32 sc = Color32.Lerp(tg.orig[i], tg.shellWet[i], f);
                        byte a = (byte)(f * shellOpacity * tg.orig[i].a);
                        if (tg.shellBaked != null && tg.shellBaked[i] > a) a = tg.shellBaked[i];
                        sc.a = a;
                        tg.shellCur[i] = sc;
                    }
                    changed = true;
                }
            }
            if (!changed) continue;
            tg.tex.SetPixels32(tg.cur); tg.tex.Apply(true);
            if (tg.gTex != null) { tg.gTex.SetPixels32(tg.gCur); tg.gTex.Apply(true); }
            if (tg.shellTex != null) { tg.shellTex.SetPixels32(tg.shellCur); tg.shellTex.Apply(true); }
        }
    }

    // ---------------------------------------------------------------- gloss overlay

    void CreateShell(Target tg, Renderer rd, int sub)
    {
        SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
        if (smr == null || smr.sharedMesh == null) return;
        Mesh src = smr.sharedMesh;
        // a mesh holding only this material's triangles, same skinning and blend shapes
        Mesh mc = new Mesh();
        mc.name = "WetLabShell_" + src.name;
        mc.vertices = src.vertices;
        mc.normals = src.normals;
        mc.tangents = src.tangents;
        mc.uv = src.uv;
        if (src.uv2 != null && src.uv2.Length == src.vertexCount) mc.uv2 = src.uv2;
        mc.boneWeights = src.boneWeights;
        mc.bindposes = src.bindposes;
        mc.subMeshCount = 1;
        mc.SetTriangles(src.GetTriangles(sub), 0);
        int vc = src.vertexCount;
        for (int bsi = 0; bsi < src.blendShapeCount; bsi++)
        {
            string bn = src.GetBlendShapeName(bsi);
            for (int fi = 0; fi < src.GetBlendShapeFrameCount(bsi); fi++)
            {
                Vector3[] dv = new Vector3[vc], dn = new Vector3[vc], dt = new Vector3[vc];
                src.GetBlendShapeFrameVertices(bsi, fi, dv, dn, dt);
                mc.AddBlendShapeFrame(bn, src.GetBlendShapeFrameWeight(bsi, fi), dv, dn, dt);
            }
        }
        mc.bounds = src.bounds;

        GameObject go = new GameObject("WetLabShell");
        go.layer = rd.gameObject.layer;
        go.transform.SetParent(rd.transform.parent, false);
        go.transform.localPosition = rd.transform.localPosition;
        go.transform.localRotation = rd.transform.localRotation;
        go.transform.localScale = rd.transform.localScale;
        SkinnedMeshRenderer s2 = go.AddComponent<SkinnedMeshRenderer>();
        s2.sharedMesh = mc;
        s2.bones = smr.bones;
        s2.rootBone = smr.rootBone;
        s2.localBounds = smr.localBounds;
        s2.updateWhenOffscreen = smr.updateWhenOffscreen;
        s2.quality = smr.quality;
        s2.shadowCastingMode = ShadowCastingMode.Off;
        s2.receiveShadows = smr.receiveShadows;

        Material sm = new Material(tg.mat);
        sm.name = "WetLabGloss";
        sm.EnableKeyword("_ALPHABLEND_ON");
        sm.DisableKeyword("_ALPHATEST_ON");
        sm.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        SetF(sm, "_Mode", 2f); SetF(sm, "_SrcBlend", 5f); SetF(sm, "_DstBlend", 10f); SetF(sm, "_ZWrite", 0f);
        SetF(sm, "_ExGloss", shellGloss);
        SetF(sm, "_Glossiness", shellSmooth); SetF(sm, "_Glossiness2", shellSmooth);
        SetF(sm, "_Glossiness3", shellSmooth); SetF(sm, "_Glossiness4", shellSmooth);
        SetF(sm, "_AlphaEx", 1f);
        // keep the garment's _Cutoff: this shader only *clips* with main-texture alpha, and that clip is
        // what hides the overlay outside the stain (with _Cutoff 0 it covered the whole garment)
        if (sm.HasProperty("_Cutoff") && sm.GetFloat("_Cutoff") < 0.3f) sm.SetFloat("_Cutoff", 0.5f);
        sm.SetOverrideTag("RenderType", "Transparent");
        sm.renderQueue = Mathf.Max(tg.mat.renderQueue + 1, 2650);

        tg.shellCur = new Color32[tg.wet.Length];
        for (int i = 0; i < tg.wet.Length; i++) { Color32 c = WetColor(tg.orig[i], false, 1f); c.a = 0; tg.shellCur[i] = c; }
        tg.shellBaseColors = new Color[ShellColorProps.Length];
        for (int i = 0; i < ShellColorProps.Length; i++)
            tg.shellBaseColors[i] = sm.HasProperty(ShellColorProps[i]) ? sm.GetColor(ShellColorProps[i]) : Color.white;
        tg.shellTex = new Texture2D(tg.w, tg.h, TextureFormat.RGBA32, true, false);
        tg.shellTex.name = "WetLabGlossTex";
        tg.shellTex.wrapMode = tg.tex.wrapMode;
        tg.shellTex.SetPixels32(tg.shellCur); tg.shellTex.Apply(true);
        sm.SetTexture("_MainTex", tg.shellTex);
        s2.sharedMaterial = sm;

        tg.shell = go; tg.shellSmr = s2; tg.srcSmr = smr; tg.shellMat = sm; tg.shellMesh = mc;
    }

    static readonly string[] ShellColorProps = { "_Color", "_Color2", "_Color3", "_Color4" };
    static readonly string[] AlphaTexProps = { "_AlphaMask", "_AlphaMask2" };
    static readonly string[] AlphaFloatProps = { "_alpha_a", "_alpha_b", "_alpha_c", "_alpha_d", "_AlphaMaskUVset", "_AlphaEx" };

    static void SetF(Material m, string name, float v)
    {
        if (m.HasProperty(name)) m.SetFloat(name, v);
    }

    void LateUpdate()
    {
        // keep overlays in step with their garments (shown/hidden, blend shapes, live gloss settings)
        foreach (Target tg in targets)
        {
            if (tg.shellSmr == null) continue;
            if (tg.srcSmr == null) { tg.shellSmr.enabled = false; continue; }
            bool vis = tg.srcSmr.enabled && tg.srcSmr.gameObject.activeInHierarchy;
            if (tg.shellSmr.enabled != vis) tg.shellSmr.enabled = vis;
            int n = tg.shellSmr.sharedMesh != null ? tg.shellSmr.sharedMesh.blendShapeCount : 0;
            for (int i = 0; i < n; i++) tg.shellSmr.SetBlendShapeWeight(i, tg.srcSmr.GetBlendShapeWeight(i));
            SetF(tg.shellMat, "_ExGloss", shellGloss);
            SetF(tg.shellMat, "_ExGlossiness", shellSmooth);
            // Darken on the overlay: the game's wet shading in the overlay otherwise decides how dark the wet
            // area looks, which is why the Darken slider seemed to do nothing
            if (tg.shellBaseColors != null)
                for (int ci = 0; ci < ShellColorProps.Length; ci++)
                    if (tg.shellMat.HasProperty(ShellColorProps[ci]))
                    {
                        Color bc = tg.shellBaseColors[ci];
                        tg.shellMat.SetColor(ShellColorProps[ci], new Color(bc.r * darken, bc.g * darken, bc.b * darken, bc.a));
                    }
            SetF(tg.shellMat, "_Glossiness", shellSmooth);
            // follow the garment's own hiding (clothes states, parts made invisible with alpha masks)
            if (tg.mat != null)
            {
                foreach (string tn in AlphaTexProps)
                    if (tg.mat.HasProperty(tn) && tg.shellMat.HasProperty(tn)) tg.shellMat.SetTexture(tn, tg.mat.GetTexture(tn));
                foreach (string fn in AlphaFloatProps)
                    if (tg.mat.HasProperty(fn) && tg.shellMat.HasProperty(fn)) tg.shellMat.SetFloat(fn, tg.mat.GetFloat(fn));
            }
        }
    }

    // Click on the body or clothes: the nearest vertex to the mouse ray (closest to the camera among those
    // within reach of the ray) becomes the stain origin, attached to that vertex's main bone.
    void PickOrigin()
    {
        ChaControl c = cha;
        if (c == null) foreach (OCIChar oci in StudioAPI.GetSelectedCharacters()) { if (oci.charInfo != null) { c = oci.charInfo; break; } }
        Camera cam = Camera.main;
        if (c == null || cam == null) { status = "Select a character first."; return; }
        PickOrigin(c, cam.ScreenPointToRay(Input.mousePosition));
    }

    // origin = first point of the character's body, face, hair or clothes along the ray
    bool PickOrigin(ChaControl c, Ray ray)
    {
        List<Renderer> rends = BodyAndClothes(c);
        if (FaceRenderer(c) != null) rends.Add(FaceRenderer(c));
        if (c.objHair != null)
            foreach (GameObject h in c.objHair)
                if (h != null && h.activeInHierarchy) rends.AddRange(h.GetComponentsInChildren<SkinnedMeshRenderer>(false));
        Transform bestBone; Vector3 bestP;
        if (!FirstAlongRay(rends, ray, out bestP, out bestBone)) { status = "Nothing under the cursor. Click on the body."; return false; }
        pickedBone = bestBone;
        pickedLocal = bestBone.InverseTransformPoint(bestP);
        usePickedOrigin = true;
        status = "Origin set on " + bestBone.name + ".";
        if (targets.Count > 0) AttachIncremental();   // garments near the new origin may differ
        return true;
    }

    static List<Renderer> BodyAndClothes(ChaControl c)
    {
        List<Renderer> rends = new List<Renderer>();
        if (c.cmpBody != null && c.cmpBody.targetCustom != null && c.cmpBody.targetCustom.rendBody != null) rends.Add(c.cmpBody.targetCustom.rendBody);
        if (c.objClothes != null)
            foreach (GameObject o in c.objClothes)
                if (o != null && o.activeInHierarchy) rends.AddRange(o.GetComponentsInChildren<SkinnedMeshRenderer>(false));
        return rends;
    }

    // The first posed vertex along a ray (within 1.5 cm of it), and the bone that mostly moves it.
    static bool FirstAlongRay(List<Renderer> rends, Ray ray, out Vector3 bestP, out Transform bestBone)
    {
        float best = float.PositiveInfinity; bestBone = null; bestP = Vector3.zero;
        foreach (Renderer rd in rends)
        {
            SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
            if (smr == null || !smr.enabled || smr.sharedMesh == null) continue;
            Mesh mesh; Matrix4x4 toWorld;
            if (!GetPosedMesh(smr, out mesh, out toWorld)) continue;
            Vector3[] v = mesh.vertices;
            BoneWeight[] bw = smr.sharedMesh.boneWeights;
            Transform[] bones = smr.bones;
            for (int i = 0; i < v.Length; i++)
            {
                Vector3 p = toWorld.MultiplyPoint3x4(v[i]);
                Vector3 q = p - ray.origin;
                float along = Vector3.Dot(q, ray.direction);
                if (along <= 0f) continue;
                float perp = (q - ray.direction * along).magnitude;
                if (perp > 0.15f + along * 0.004f) continue;
                if (along < best && bw != null && i < bw.Length && bw[i].boneIndex0 < bones.Length && bones[bw[i].boneIndex0] != null)
                {
                    best = along; bestBone = bones[bw[i].boneIndex0]; bestP = p;
                }
            }
            Destroy(mesh);
        }
        return bestBone != null;
    }

    // The default origin, at the fly: see originDown / originOut. Falls back to the old fixed offsets from the hip
    // joints when the line meets nothing.
    Vector3 DefaultOrigin(Transform root, Vector3 legMid)
    {
        Transform kokan = Find(root, "cf_J_Kokan");
        Vector3 k = kokan != null ? kokan.position : legMid - fU * 0.2f * fS + fF * 0.3f * fS;
        Ray ray = new Ray(k - fU * originDown + fF * 1.5f, -fF);   // from 15 cm in front, back toward the body
        Vector3 p; Transform b;
        if (cha != null && FirstAlongRay(BodyAndClothes(cha), ray, out p, out b)) return p + fF * originOut;
        return legMid - fU * 0.3f * fS + fF * 1.1f * fS;
    }

    // ---------------------------------------------------------------- scripting (StudioBridge, Timeline events)

    // Attach to a character without the Studio selection. If from != to, the origin is where the ray from -> to
    // first meets the character (e.g. a bullet's path), otherwise the default origin is used.
    public string ScriptAttach(OCIChar oci, Vector3 from, Vector3 to)
    {
        Detach();
        stains.Clear();
        ClearPuddles();
        cha = oci != null ? oci.charInfo : null;
        if (cha == null) return status = "No character.";
        if (!BodyFrame()) { cha = null; return status = "Bones not found."; }
        if ((to - from).sqrMagnitude > 1e-6f)
        {
            if (!PickOrigin(cha, new Ray(from, (to - from).normalized))) { usePickedOrigin = false; pickedBone = null; }
            if (!BodyFrame()) { cha = null; return status = "Bones not found."; }
        }
        float t0 = Time.realtimeSinceStartup;
        AddAllRenderers();
        BuildAll(true);
        t = 0f; running = false; looping = false; appliedRadius = 0f; dropAccum = 0f;
        shapeSig = ShapeSignature(); lookSig = LookSignature();
        status = "Attached " + targets.Count + " material(s) by script ("
            + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s), origin " + (usePickedOrigin && pickedBone != null ? pickedBone.name : "default") + ".";
        Log.LogInfo(status);
        return status;
    }

    // ---- several stains on one character, prepared once and switched without rebuilding (e.g. blood from the
    // head, later urine from the crotch, in one looping scene)

    class StainCache
    {
        public string preset;
        public float maxRadius, spreadTime, dripTail, dripRate;
        public readonly Dictionary<Target, float[]> dist = new Dictionary<Target, float[]>();
        public readonly Dictionary<Target, List<int>[]> bins = new Dictionary<Target, List<int>[]>();
        public readonly Dictionary<Target, Color32[]> wet = new Dictionary<Target, Color32[]>();
        public readonly Dictionary<Target, Color32[]> gWet = new Dictionary<Target, Color32[]>();
        public readonly Dictionary<Target, Color32[]> shellWet = new Dictionary<Target, Color32[]>();
        public readonly List<Emitter> em = new List<Emitter>();
    }
    Dictionary<string, StainCache> stains = new Dictionary<string, StainCache>();

    // Build the stain shape and look for (preset, origin) on the attached character and remember it as `name`.
    // origin: from == to -> the default (crotch) origin, else where the ray from -> to meets the character.
    public string ScriptDefineStain(string name, string preset, Vector3 from, Vector3 to, float radius, float spread, float tail)
    {
        if (cha == null || targets.Count == 0) return status = "Attach first.";
        float t0 = Time.realtimeSinceStartup;
        maxRadius = radius; spreadTime = spread; dripTail = tail;
        ScriptPreset(preset);
        bool ray = (to - from).sqrMagnitude > 1e-6f;
        if (ray) { if (!PickOrigin(cha, new Ray(from, (to - from).normalized))) return status; }   // rebuilds
        else { usePickedOrigin = false; pickedBone = null; AttachIncremental(); }
        StainCache c = new StainCache();
        c.preset = preset; c.maxRadius = maxRadius; c.spreadTime = spreadTime; c.dripTail = dripTail; c.dripRate = dripRate;
        foreach (Target tg in targets)
        {
            c.dist[tg] = tg.dist; c.bins[tg] = tg.bins;
            c.wet[tg] = (Color32[])tg.wet.Clone();
            if (tg.gWet != null) c.gWet[tg] = (Color32[])tg.gWet.Clone();
            if (tg.shellWet != null) c.shellWet[tg] = (Color32[])tg.shellWet.Clone();
        }
        c.em.AddRange(emitters);
        stains[name] = c;
        shapeSig = ShapeSignature(); lookSig = LookSignature();
        DryAll(); t = 0f; running = false;
        return status = "Stain '" + name + "' ready (" + (Time.realtimeSinceStartup - t0).ToString("0.0") + " s).";
    }

    // Switch to a prepared stain. The stain already on the clothes stays; puddles keep their colour.
    public string ScriptUseStain(string name, bool start)
    {
        StainCache c;
        if (!stains.TryGetValue(name, out c)) return status = "No stain '" + name + "'.";
        foreach (Target tg in targets)
        {
            float[] d; List<int>[] b; Color32[] w;
            if (c.dist.TryGetValue(tg, out d) && c.bins.TryGetValue(tg, out b) && c.wet.TryGetValue(tg, out w))
            {
                tg.dist = d; tg.bins = b; Array.Copy(w, tg.wet, w.Length);
                Color32[] g;
                if (tg.gWet != null && c.gWet.TryGetValue(tg, out g)) Array.Copy(g, tg.gWet, g.Length);
                if (tg.shellWet != null && c.shellWet.TryGetValue(tg, out g)) Array.Copy(g, tg.shellWet, g.Length);
            }
            else tg.bins = new List<int>[1];   // target added later: no part in this stain
        }
        emitters.Clear(); emitters.AddRange(c.em);
        foreach (Puddle pd in puddles)
        {
            if (pd.frozen) continue;
            pd.frozen = true;
            pd.col = new Color(puddleR, puddleG, puddleB, puddleA);
        }
        ScriptPreset(c.preset);
        maxRadius = c.maxRadius; spreadTime = c.spreadTime; dripTail = c.dripTail; dripRate = c.dripRate;
        shapeSig = ShapeSignature(); lookSig = LookSignature();   // no automatic rebuild for these changes
        t = 0f; appliedRadius = 0f; dropAccum = 0f; looping = false; running = start;
        return status = "Using stain '" + name + "'.";
    }

    // diagnostics: the map surface below a point, and how many floor triangles the index holds
    public string FloorProbe(float x, float y, float z)
    {
        Ground g = GroundIndex();
        Vector3 n;
        float h = g.Height(x, z, y, y - 60f, out n);
        return "map floor " + (float.IsNaN(h) ? "none" : h.ToString("0.000")) + " (index: " + (g.v != null ? g.v.Length / 3 : 0) + " triangles, map " + g.mapNo + ")";
    }

    // diagnostics: the default origin against the body, in the pelvis frame (x right, y up, z forward; world units)
    public string OriginInfo()
    {
        if (cha == null || !BodyFrame()) return "not attached";
        Transform root = cha.objBodyBone.transform;
        Vector3 mid = (legUpL + legUpR) * 0.5f;
        Func<Vector3, string> F = w => { Vector3 q = w - mid; return "(" + Vector3.Dot(q, fR).ToString("0.00") + ", " + Vector3.Dot(q, fU).ToString("0.00") + ", " + Vector3.Dot(q, fF).ToString("0.00") + ")"; };
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("fS=" + fS.ToString("0.000") + " originDown=" + originDown + " originOut=" + originOut + " picked=" + usePickedOrigin + "\n");
        sb.Append("origin " + F(fC) + "\n");
        foreach (string bn in new[] { "cf_J_Hips", "cf_J_Kosi01", "cf_J_Kosi02", "cf_J_Kokan", "cf_J_Ana", "cf_J_LegUp00_L", "cf_J_LegLow01_L" })
        {
            Transform t = Find(root, bn);
            if (t != null) sb.Append(bn + " " + F(t.position) + " lossy " + t.lossyScale.x.ToString("0.00") + "\n");
        }
        // the crotch skin: the lowest body vertex near the midline, in front of / behind the hip line
        SkinnedMeshRenderer body = null;
        if (cha.objBody != null)
            foreach (SkinnedMeshRenderer smr in cha.objBody.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (smr.sharedMesh != null && (body == null || smr.sharedMesh.vertexCount > body.sharedMesh.vertexCount)) body = smr;
        if (body != null)
        {
            Mesh m = new Mesh();
            body.BakeMesh(m);
            Matrix4x4 l2w = Matrix4x4.TRS(body.transform.position, body.transform.rotation, Vector3.one);
            float best = float.MaxValue; Vector3 bp = Vector3.zero;
            foreach (Vector3 v in m.vertices)
            {
                Vector3 w = l2w.MultiplyPoint3x4(v), q = w - mid;
                float x = Vector3.Dot(q, fR), y = Vector3.Dot(q, fU), z = Vector3.Dot(q, fF);
                if (Mathf.Abs(x) > 0.06f || Mathf.Abs(z) > 1.5f || y > 0f) continue;
                if (y < best) { best = y; bp = w; }
            }
            Destroy(m);
            sb.Append("lowest midline skin " + F(bp) + " (" + body.name + ")\n");
        }
        return sb.ToString();
    }

    // diagnostics: each wet character, how many of its materials still show WetLab's texture, layers, puddles
    public string RigsInfo()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("editing " + (cha != null ? cha.fileParam.fullname : "nobody") + ": " + TargetsInfo(targets) + ", layers " + layers + ", puddles " + puddles.Count + "\n");
        foreach (Rig r in parked.Values)
            sb.Append("parked " + r.name + ": " + TargetsInfo(r.Targets) + ", layers " + r.v["layers"] + ", puddles " + r.Puddles.Count + "\n");
        return sb.ToString();
    }

    static string TargetsInfo(List<Target> ts)
    {
        int shown = 0;
        foreach (Target tg in ts) if (tg.mat != null && tg.tex != null && tg.mat.GetTexture("_MainTex") == tg.tex) shown++;
        return shown + "/" + ts.Count + " materials stained";
    }

    public string ScriptInfo()
    {
        return "t=" + t.ToString("0.00") + " running=" + running + " radius=" + Radius().ToString("0.00") + " applied=" + appliedRadius.ToString("0.00")
            + " targets=" + targets.Count + " emitters=" + emitters.Count + " drops=" + drops.Count + " puddles=" + puddles.Count
            + " floorY=" + floorY.ToString("0.00") + " skinDrips=" + skinDrips + " drips=" + drips + " cha=" + (cha != null ? cha.name : "none");
    }

    // names of the prepared stains on the attached character ("" when nothing is attached)
    public string ScriptStains()
    {
        if (cha == null || targets.Count == 0) return "";
        return string.Join(",", new List<string>(stains.Keys).ToArray());
    }

    public void ScriptPreset(string name)
    {
        if (name.ToLowerInvariant() == "blood") BloodPreset(); else WaterPreset();
    }

    public void ScriptStart() { t = 0f; running = true; looping = false; }

    // stain gone, drops and puddles cleared (e.g. when a Timeline loop restarts)
    public void ScriptDry()
    {
        t = 0f; running = false; looping = false;
        DryAll();
        drops.Clear(); splashes.Clear();
        ClearPuddles();
    }

    // ---- for other plugins: characters prepared in advance and switched without rebuilding. ScriptAttach costs
    // seconds; a parked rig comes back at once. ScriptPark puts the attached character aside (its stain as it is);
    // ScriptResume makes a parked character the attached one again (the current one is parked); ScriptForget
    // restores one character's clothes and removes its drops and puddles, attached or parked.
    public string ScriptPark()
    {
        if (cha == null || targets.Count == 0) return status = "Nothing attached.";
        string n = cha.fileParam.fullname;
        parked[cha] = Capture();
        ResetState();
        return status = "Parked " + n + ".";
    }

    public bool ScriptIsPrepared(OCIChar oci)
    {
        ChaControl c = oci != null ? oci.charInfo : null;
        return c != null && ((c == cha && targets.Count > 0) || parked.ContainsKey(c));
    }

    public string ScriptResume(OCIChar oci)
    {
        ChaControl c = oci != null ? oci.charInfo : null;
        if (c == null) return status = "No character.";
        if (c == cha && targets.Count > 0) return status = "Resumed (already attached).";
        if (!parked.ContainsKey(c)) return status = "Not prepared.";
        SwitchTo(c);
        return status = "Resumed " + c.fileParam.fullname + ".";
    }

    public string ScriptForget(OCIChar oci)
    {
        ChaControl c = oci != null ? oci.charInfo : null;
        if (c == null) return status = "No character.";
        if (c == cha && targets.Count > 0) { ScriptDry(); Detach(); return status = "Forgot the attached character."; }
        Rig r;
        if (!parked.TryGetValue(c, out r)) return status = "Not prepared.";
        ChaControl keep = cha != null && targets.Count > 0 ? cha : null;
        if (keep != null) { parked[keep] = Capture(); ResetState(); }
        else if (cha != null) Detach();
        string settings = SerializeSettings();
        parked.Remove(c);
        Restore(r);
        ScriptDry(); Detach(); ResetState();
        ApplySettings(settings);
        if (keep != null) { Rig k = parked[keep]; parked.Remove(keep); Restore(k); }
        return status = "Forgot a parked character.";
    }

    void WaterPreset()
    {
        // a tuned water look
        tintR = 1f; tintG = 1f; tintB = 1f; saturate = 1.15f; wetGloss = 0.845f;
        stainR = 0f; stainG = 0f; stainB = 0f; stainAmount = 0.3f; skinWater = 0.15f;
        shellGloss = 0.9f; shellSmooth = 0.8f; shellOpacity = 0.85f;
        dropR = 0.85f; dropG = 0.88f; dropB = 0.75f; dropA = 0.2f; dropShine = 0.6f; dropSize = 0.031f;
        puddleR = 0.1f; puddleG = 0.097f; puddleB = 0.097f; puddleA = 0.8f; puddleShine = 1f;
    }

    void BloodPreset()
    {
        tintR = 1f; tintG = 1f; tintB = 1f; saturate = 1f;
        stainR = 0.42f; stainG = 0.03f; stainB = 0.04f; stainAmount = 0.85f; skinWater = 0.85f;
        dropR = 0.2f; dropG = 0.02f; dropB = 0.03f; dropA = 0.9f; dropShine = 0.35f;
        puddleR = 0.1f; puddleG = 0.01f; puddleB = 0.02f; puddleA = 0.9f; puddleShine = 0.3f; dropSize = 0.031f;
    }

    // ---------------------------------------------------------------- geometry

    static Transform Find(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++) { Transform r = Find(t.GetChild(i), name); if (r != null) return r; }
        return null;
    }

    bool BodyFrame()
    {
        Transform root = cha.objBodyBone != null ? cha.objBodyBone.transform : null;
        if (root == null) return false;
        Transform hips = Find(root, "cf_J_Hips"), neck = Find(root, "cf_J_Neck");
        Transform lu = Find(root, "cf_J_LegUp00_L"), ru = Find(root, "cf_J_LegUp00_R");
        Transform ll = Find(root, "cf_J_LegLow01_L"), rl = Find(root, "cf_J_LegLow01_R");
        Transform fl = Find(root, "cf_J_Foot01_L"), fr = Find(root, "cf_J_Foot01_R");
        Transform tl = Find(root, "cf_J_Toes01_L"), tr = Find(root, "cf_J_Toes01_R");
        if (hips == null || neck == null || lu == null || ru == null || ll == null || rl == null || fl == null || fr == null || tl == null || tr == null) return false;
        // Frame from the pelvis itself (hip-joint line and hip-joint midpoint -> cf_J_Hips). The toes
        // (used before) point elsewhere when crouching or lying, which pushed the origin toward the buttocks.
        Vector3 mid = (lu.position + ru.position) * 0.5f;
        fR = (ru.position - lu.position).normalized;
        Vector3 up = hips.position - mid;
        up -= fR * Vector3.Dot(up, fR);
        if (up.sqrMagnitude < 1e-6f) up = neck.position - hips.position;
        fU = up.normalized;
        fF = Vector3.Cross(fR, fU);
        fS = Vector3.Distance(lu.position, ll.position) / 4.2f;
        legUpL = lu.position; legUpR = ru.position; legLowL = ll.position; legLowR = rl.position;
        Vector3 legMid = (lu.position + ru.position) * 0.5f;
        fC = usePickedOrigin && pickedBone != null ? fC : DefaultOrigin(root, legMid);
        if (usePickedOrigin && pickedBone != null) fC = pickedBone.TransformPoint(pickedLocal);
        originBone = usePickedOrigin && pickedBone != null ? pickedBone : hips;
        originLocal = originBone.InverseTransformPoint(fC);
        toeL = tl; toeR = tr;
        floorY = FloorBase() + floorOffset;
        floorKey = FloorKey();
        return true;
    }

    // ---------------------------------------------------------------- floor

    // feet estimate: the toe joints sit ~0.31 (prefab units, x scale) above the sole; fails when kneeling or lying
    float FeetFloor()
    {
        if (toeL == null || toeR == null) return floorY - floorOffset;
        return Mathf.Min(toeL.position.y, toeR.position.y) - 0.31f * fS;
    }

    // highest map surface below the crotch (NaN: the map has no floor mesh there)
    float MapFloor()
    {
        if (cha == null) return float.NaN;
        Vector3 c = originBone != null ? originBone.TransformPoint(originLocal) : fC;   // where the crotch is now
        Vector3 n;
        return GroundIndex().Height(c.x, c.z, c.y, c.y - 60f, out n);
    }

    float FloorBase()
    {
        if (useFixedFloor) return fixedFloorY;
        if (floorMode == 2) return floorHeight;
        if (floorMode == 0)
        {
            float m = MapFloor();
            if (!float.IsNaN(m)) return m;
        }
        return FeetFloor();
    }

    string FloorKey()
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        return floorMode + "|" + floorOffset.ToString("R", ci) + "|" + floorHeight.ToString("R", ci) + "|" + useFixedFloor + "|" + fixedFloorY.ToString("R", ci);
    }

    // Every frame: drops land on the floor as it is now (the character may move). When the floor settings change,
    // the puddles already lying there move with the floor; when only the character moves, they stay.
    void UpdateFloor()
    {
        if (cha == null || targets.Count == 0) return;
        float nf = FloorBase() + floorOffset;
        string key = FloorKey();
        if (key != floorKey && Mathf.Abs(nf - floorY) > 1e-5f)
        {
            float d = nf - floorY;
            foreach (Puddle pd in puddles)
            {
                pd.center.y += d;
                if (pd.go != null) pd.go.transform.position += Vector3.up * d;
            }
        }
        floorKey = key;
        floorY = nf;
    }

    // Studio maps often have no colliders: the map's meshes are baked once per map into a grid of upward-facing
    // triangles, and a vertical ray is intersected with them (same method as StudioBridge's ground.probe).
    class Ground
    {
        public int mapNo = int.MinValue;
        public Vector3[] v;
        public Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        public float cell = 4f;

        static long Key(int ix, int iz) { return ((long)ix << 32) ^ (uint)iz; }

        public void Add(Vector3 a, Vector3 b, Vector3 c, List<Vector3> store)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-10f) return;
            n.Normalize();
            if (Mathf.Abs(n.y) < 0.3f) return;          // walls are never floor
            int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / cell), x1 = Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / cell);
            int z0 = Mathf.FloorToInt(Mathf.Min(a.z, Mathf.Min(b.z, c.z)) / cell), z1 = Mathf.FloorToInt(Mathf.Max(a.z, Mathf.Max(b.z, c.z)) / cell);
            if ((x1 - x0 + 1) * (z1 - z0 + 1) > 4096) return;
            int t = store.Count / 3;
            store.Add(a); store.Add(b); store.Add(c);
            for (int ix = x0; ix <= x1; ix++)
                for (int iz = z0; iz <= z1; iz++)
                {
                    List<int> l; long k = Key(ix, iz);
                    if (!cells.TryGetValue(k, out l)) { l = new List<int>(); cells[k] = l; }
                    l.Add(t);
                }
        }

        // highest surface between yTop and yBottom under (x, z); NaN if none
        public float Height(float x, float z, float yTop, float yBottom, out Vector3 normal)
        {
            normal = Vector3.up;
            List<int> l;
            if (v == null || !cells.TryGetValue(Key(Mathf.FloorToInt(x / cell), Mathf.FloorToInt(z / cell)), out l)) return float.NaN;
            float best = float.NaN;
            foreach (int t in l)
            {
                Vector3 a = v[t * 3], b = v[t * 3 + 1], c = v[t * 3 + 2];
                float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(d) < 1e-9f) continue;
                float w0 = ((b.z - c.z) * (x - c.x) + (c.x - b.x) * (z - c.z)) / d;
                float w1 = ((c.z - a.z) * (x - c.x) + (a.x - c.x) * (z - c.z)) / d;
                float w2 = 1f - w0 - w1;
                if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                float y = w0 * a.y + w1 * b.y + w2 * c.y;
                if (y > yTop || y < yBottom) continue;
                if (float.IsNaN(best) || y > best) { best = y; normal = Vector3.Cross(b - a, c - a).normalized; if (normal.y < 0f) normal = -normal; }
            }
            return best;
        }
    }

    // A map loaded as its own scene has several root objects and Map.MapRoot is only one of them (on some maps an
    // object without renderers, e.g. a reflection probe): take every root of that scene.
    static List<Renderer> MapRenderers(GameObject root)
    {
        List<Renderer> l = new List<Renderer>();
        UnityEngine.SceneManagement.Scene sc = root.scene;
        GameObject[] roots = sc.IsValid() && sc.name != "Studio" ? sc.GetRootGameObjects() : new[] { root };
        foreach (GameObject go in roots) l.AddRange(go.GetComponentsInChildren<Renderer>(false));
        return l;
    }

    static Ground ground;
    static float groundBuiltAt;

    static Ground GroundIndex()
    {
        Studio.Studio st = Singleton<Studio.Studio>.Instance;
        int mapNo = st != null && st.sceneInfo != null ? st.sceneInfo.mapInfo.no : -1;
        // an empty index (map not loaded yet, or no floor meshes) is rebuilt at most every 5 s
        if (ground != null && ground.mapNo == mapNo && (ground.v.Length > 0 || Time.realtimeSinceStartup - groundBuiltAt < 5f)) return ground;
        groundBuiltAt = Time.realtimeSinceStartup;
        Ground g = new Ground();
        g.mapNo = mapNo;
        List<Vector3> store = new List<Vector3>();
        Studio.Map map = Studio.Map.Instance;
        GameObject root = map != null ? map.MapRoot : null;
        if (root != null)
        {
            Mesh tmp = new Mesh();
            foreach (Renderer rd in MapRenderers(root))
            {
                if (!rd.enabled) continue;
                Mesh mesh; Matrix4x4 m;
                SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
                if (smr != null)
                {
                    if (smr.sharedMesh == null) continue;
                    try { smr.BakeMesh(tmp); } catch (Exception) { continue; }
                    mesh = tmp;
                    m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);   // baked meshes include scale
                }
                else
                {
                    MeshFilter mf = rd.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    mesh = mf.sharedMesh; m = rd.transform.localToWorldMatrix;
                }
                Vector3[] verts = mesh.vertices;
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                {
                    if (mesh.GetTopology(sm) != MeshTopology.Triangles) continue;
                    int[] tris = mesh.GetTriangles(sm);
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                        g.Add(m.MultiplyPoint3x4(verts[tris[i]]), m.MultiplyPoint3x4(verts[tris[i + 1]]), m.MultiplyPoint3x4(verts[tris[i + 2]]), store);
                }
            }
            Destroy(tmp);
        }
        g.v = store.ToArray();
        ground = g;
        Log.LogInfo("floor index for map " + mapNo + ": " + (g.v.Length / 3) + " floor-facing triangles");
        return g;
    }

    // Anisotropic distance from the origin, plus a penalty for going round a thigh to its outer side
    // (a straight line from the crotch reaches the outer thigh about as soon as the inner one).
    float Distance(Vector3 p)
    {
        Vector3 q = p - fC;
        float y = Vector3.Dot(q, fU), z = Vector3.Dot(q, fF), x = Vector3.Dot(q, fR);
        y *= y > 0f ? upFactor : downFactor;
        z *= z > 0f ? frontFactor : backFactor;
        x *= sideFactor;
        float d = Mathf.Sqrt(x * x + y * y + z * z);
        if (outerResist > 0f) d += OuterPenalty(p);
        return d;
    }

    float OuterPenalty(Vector3 p)
    {
        float best = float.PositiveInfinity, pen = 0f;
        for (int side = 0; side < 2; side++)
        {
            Vector3 a = side == 0 ? legUpL : legUpR, b = side == 0 ? legLowL : legLowR, other = side == 0 ? legUpR : legUpL;
            Vector3 ax = b - a;
            float len2 = ax.sqrMagnitude;
            if (len2 < 1e-6f) continue;
            float tt = Vector3.Dot(p - a, ax) / len2;
            if (tt < -0.15f) continue;                       // above the hip joint: not on this leg
            Vector3 dir = ax / Mathf.Sqrt(len2);
            Vector3 radial = p - (a + ax * Mathf.Clamp(tt, 0f, 1.6f));
            radial -= dir * Vector3.Dot(radial, dir);
            float dist = radial.magnitude;
            if (dist >= best) continue;
            best = dist;
            Vector3 inner = other - a;
            inner -= dir * Vector3.Dot(inner, dir);
            if (dist < 1e-4f || inner.sqrMagnitude < 1e-6f) { pen = 0f; continue; }
            float cos = Vector3.Dot(radial / dist, inner.normalized);
            pen = outerResist * (1f - cos) * fS * Mathf.Clamp01((tt + 0.15f) / 0.3f);
        }
        return pen;
    }

    static bool GetPosedMesh(Renderer rd, out Mesh mesh, out Matrix4x4 toWorld)
    {
        SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
        if (smr != null)
        {
            if (smr.sharedMesh == null) { mesh = null; toWorld = Matrix4x4.identity; return false; }
            mesh = new Mesh();
            smr.BakeMesh(mesh);   // baked vertices already include the renderer's scale
            Transform tr = smr.transform;
            toWorld = Matrix4x4.TRS(tr.position, tr.rotation, Vector3.one);
            mesh.uv = smr.sharedMesh.uv;
            return true;
        }
        MeshFilter mf = rd.GetComponent<MeshFilter>();
        mesh = mf != null ? mf.sharedMesh : null;
        toWorld = rd.transform.localToWorldMatrix;
        return mesh != null;
    }

    static float Hash(int x, int y, int z)
    {
        int h = x * 374761393 + y * 668265263 + z * 1274126177;
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h ^ (h >> 16)) & 0xffff) / 32767.5f - 1f;
    }

    static float ValueNoise(Vector3 p)
    {
        int x0 = Mathf.FloorToInt(p.x), y0 = Mathf.FloorToInt(p.y), z0 = Mathf.FloorToInt(p.z);
        float fx = p.x - x0, fy = p.y - y0, fz = p.z - z0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        float a = Mathf.Lerp(Hash(x0, y0, z0), Hash(x0 + 1, y0, z0), fx);
        float b = Mathf.Lerp(Hash(x0, y0 + 1, z0), Hash(x0 + 1, y0 + 1, z0), fx);
        float c = Mathf.Lerp(Hash(x0, y0, z0 + 1), Hash(x0 + 1, y0, z0 + 1), fx);
        float d = Mathf.Lerp(Hash(x0, y0 + 1, z0 + 1), Hash(x0 + 1, y0 + 1, z0 + 1), fx);
        return Mathf.Lerp(Mathf.Lerp(a, b, fy), Mathf.Lerp(c, d, fy), fz);
    }

    static float Noise(Vector3 p)
    {
        return ValueNoise(p * 1.3f) * 0.65f + ValueNoise(p * 3.1f + new Vector3(17.3f, 5.1f, 9.7f)) * 0.35f;
    }

    // Rasterise triangles into a UV-space distance map (minimum where triangles overlap), then dilate
    // two texels so bilinear/mip sampling at UV seams does not pull in uncovered texels.
    static float[] Rasterise(int[] tris, Vector2[] uv, float[] dv, int w, int h)
    {
        float[] d = new float[w * h];
        for (int i = 0; i < d.Length; i++) d[i] = float.PositiveInfinity;
        for (int k = 0; k + 2 < tris.Length; k += 3)
        {
            int i0 = tris[k], i1 = tris[k + 1], i2 = tris[k + 2];
            float ox = Mathf.Floor(uv[i0].x), oy = Mathf.Floor(uv[i0].y);
            Vector2 a = new Vector2((uv[i0].x - ox) * w, (uv[i0].y - oy) * h);
            Vector2 b = new Vector2((uv[i1].x - ox) * w, (uv[i1].y - oy) * h);
            Vector2 c = new Vector2((uv[i2].x - ox) * w, (uv[i2].y - oy) * h);
            float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            if (Mathf.Abs(area) < 1e-8f) continue;
            int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
            int maxX = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
            int maxY = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((b.x - px) * (c.y - py) - (c.x - px) * (b.y - py)) / area;
                    float w1 = ((c.x - px) * (a.y - py) - (a.x - px) * (c.y - py)) / area;
                    float w2 = 1f - w0 - w1;
                    if (w0 < -0.01f || w1 < -0.01f || w2 < -0.01f) continue;
                    float val = w0 * dv[i0] + w1 * dv[i1] + w2 * dv[i2];
                    int idx = y * w + x;
                    if (val < d[idx]) d[idx] = val;
                }
            }
        }
        for (int pass = 0; pass < 2; pass++)
        {
            float[] e = (float[])d.Clone();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (!float.IsInfinity(d[idx])) continue;
                    float m = float.PositiveInfinity;
                    if (x > 0) m = Mathf.Min(m, d[idx - 1]);
                    if (x < w - 1) m = Mathf.Min(m, d[idx + 1]);
                    if (y > 0) m = Mathf.Min(m, d[idx - w]);
                    if (y < h - 1) m = Mathf.Min(m, d[idx + w]);
                    e[idx] = m;
                }
            }
            d = e;
        }
        return d;
    }

    // ---------------------------------------------------------------- drips

    // Drip points: wettable vertices, each followed through its most-weighted bone. Lower ones drip more.
    void AddEmitters(SkinnedMeshRenderer smr, Vector3[] world, float[] dv)
    {
        if (smr == null || smr.sharedMesh == null) return;
        BoneWeight[] bw = smr.sharedMesh.boneWeights;
        Transform[] bones = smr.bones;
        if (bw == null || bw.Length != world.Length || bones == null) return;
        int step = Mathf.Max(1, world.Length / 3000);
        for (int i = 0; i < world.Length; i += step)
        {
            if (dv[i] > maxRadius) continue;
            int bi = bw[i].boneIndex0;
            if (bi < 0 || bi >= bones.Length || bones[bi] == null) continue;
            Emitter e = new Emitter();
            e.bone = bones[bi];
            e.local = bones[bi].InverseTransformPoint(world[i]);
            e.d = dv[i];
            emitters.Add(e);
        }
        while (emitters.Count > 800) emitters.RemoveAt(UnityEngine.Random.Range(0, emitters.Count));
    }

    float DripIntensity()
    {
        if (!drips || spreadTime + dripTail <= 0f) return 0f;
        if (t <= 0f) return 0f;
        if (t < spreadTime) return Mathf.Clamp01(t / Mathf.Max(0.5f, spreadTime * 0.3f));
        float k = (t - spreadTime) / Mathf.Max(0.01f, dripTail);
        return k >= 1f ? 0f : (1f - k) * (1f - k);
    }

    void EmitDrops(float dt)
    {
        float rate = dripRate * DripIntensity();
        if (rate <= 0f || emitters.Count == 0) return;
        float r = Radius();
        // the lowest currently-wet points
        float lowest = float.PositiveInfinity;
        for (int i = 0; i < emitters.Count; i++)
        {
            if (emitters[i].d > r || emitters[i].bone == null) continue;
            lowest = Mathf.Min(lowest, emitters[i].bone.TransformPoint(emitters[i].local).y);
        }
        if (float.IsInfinity(lowest)) return;
        dropAccum += rate * dt;
        int guard = 0;
        while (dropAccum >= 1f && guard++ < 200)
        {
            Emitter e = emitters[UnityEngine.Random.Range(0, emitters.Count)];
            if (e.d > r || e.bone == null) continue;
            Vector3 p = e.bone.TransformPoint(e.local);
            // strongly prefer points near the lowest wet height (crotch bottom, hem, inner thighs)
            if (UnityEngine.Random.value > Mathf.Exp(-(p.y - lowest) / 0.6f)) continue;
            dropAccum -= 1f;
            if (drops.Count > 1500) continue;
            Drop d = new Drop();
            d.pos = p + UnityEngine.Random.insideUnitSphere * 0.05f;
            d.vel = new Vector3(UnityEngine.Random.Range(-0.5f, 0.5f), -UnityEngine.Random.Range(0f, 2f), UnityEngine.Random.Range(-0.5f, 0.5f));
            d.size = dropSize * UnityEngine.Random.Range(0.7f, 1.3f);
            drops.Add(d);
        }
    }

    void UpdateDrops(float dt)
    {
        Vector3 g = new Vector3(0f, -98.1f, 0f);
        for (int i = drops.Count - 1; i >= 0; i--)
        {
            Drop d = drops[i];
            d.vel += g * dt;
            d.pos += d.vel * dt;
            if (d.pos.y <= floorY)
            {
                Vector3 hit = new Vector3(d.pos.x, floorY, d.pos.z);
                AddToPuddle(hit, d.size);
                for (int k = 0; k < 3 && splashes.Count < 600; k++)
                {
                    Splash sp = new Splash();
                    sp.pos = hit + Vector3.up * 0.02f;
                    sp.vel = new Vector3(UnityEngine.Random.Range(-6f, 6f), UnityEngine.Random.Range(4f, 10f), UnityEngine.Random.Range(-6f, 6f));
                    sp.life = UnityEngine.Random.Range(0.15f, 0.3f);
                    splashes.Add(sp);
                }
                drops.RemoveAt(i);
                continue;
            }
            drops[i] = d;
        }
        for (int i = splashes.Count - 1; i >= 0; i--)
        {
            Splash sp = splashes[i];
            sp.life -= dt;
            sp.vel += g * dt;
            sp.pos += sp.vel * dt;
            if (sp.life <= 0f || sp.pos.y < floorY) { splashes.RemoveAt(i); continue; }
            splashes[i] = sp;
        }
    }

    void AddToPuddle(Vector3 p, float size)
    {
        Puddle best = null; float bd = float.PositiveInfinity;
        foreach (Puddle pd in puddles)
        {
            if (pd.frozen) continue;   // a kept layer's puddle: new drops start their own
            float rr = PuddleRadius(pd);
            float dd = new Vector2(p.x - pd.center.x, p.z - pd.center.z).magnitude;
            if (dd < rr + 0.6f && dd < bd) { bd = dd; best = pd; }
        }
        float add = 0.04f * puddleGrowth * (size / 0.07f) * (size / 0.07f);
        if (best == null)
        {
            if (puddles.Count >= 30) return;
            best = new Puddle(); best.center = p; best.area = 0f; best.seed = UnityEngine.Random.value * 100f;
            puddles.Add(best);
        }
        // the centre drifts slightly toward new drops
        float wNew = add / Mathf.Max(0.001f, best.area + add);
        best.center = Vector3.Lerp(best.center, p, wNew * 0.5f);
        best.area += add;
    }

    bool looping;
    float loopHoldT;

    void RestartLoop()
    {
        DryAll();
        ClearCurrentPuddles();
        t = 0f; running = true; loopHoldT = 0f; dropAccum = 0f;
        loopCount++;
    }
    int loopCount;

    // Puddles of the current stain only (kept layers' puddles stay), plus drops in flight.
    void ClearCurrentPuddles()
    {
        for (int i = puddles.Count - 1; i >= 0; i--)
        {
            Puddle pd = puddles[i];
            if (pd.frozen) continue;
            if (pd.go != null) Destroy(pd.go);
            if (pd.mesh != null) Destroy(pd.mesh);
            puddles.RemoveAt(i);
        }
        drops.Clear(); splashes.Clear();
    }

    void ClearPuddles()
    {
        foreach (Puddle pd in puddles)
        {
            if (pd.go != null) Destroy(pd.go);
            if (pd.mesh != null) Destroy(pd.mesh);
        }
        puddles.Clear(); drops.Clear(); splashes.Clear();
    }

    // ---------------------------------------------------------------- lit puddles

    // A puddle is a flat mesh drawn with the game's clothes shader (taken from a wet garment) in its
    // alpha-blend variant with high smoothness and wet gloss, so scene lights and reflections show on it
    // the same way they do on the clothes. Its texture is a radial alpha ramp that feathers the rim.
    Material PuddleMaterial()
    {
        string sig = puddleR + "," + puddleG + "," + puddleB + "," + puddleA + "," + puddleShine + "," + puddlePremultiply;
        if (puddleMat != null && sig != puddleSig && puddleSig.EndsWith(puddlePremultiply ? "False" : "True"))
        {
            // blend mode changed: rebuild the material
            Destroy(puddleMat); puddleMat = null;
        }
        if (puddleMat != null && sig == puddleSig) return puddleMat;
        if (puddleMat == null)
        {
            Material template = null;
            foreach (Target tg in targets) if (!tg.isSkin && tg.mat != null && tg.mat.shader != null) { template = tg.mat; break; }
            if (template == null) return null;
            puddleMat = new Material(template);
            puddleMat.name = "WetLabPuddle";
            foreach (string tn in new string[] { "_BumpMap", "_DetailGlossMap", "_DetailGlossMap2", "_DetailMainTex", "_DetailMask",
                "_MetallicGlossMap", "_OcclusionMap", "_WeatheringMap", "_WeatheringMask", "_AlphaMask", "_AlphaMask2" })
                if (puddleMat.HasProperty(tn)) puddleMat.SetTexture(tn, null);
            foreach (string fn in new string[] { "_alpha_a", "_alpha_b", "_alpha_c", "_alpha_d", "_Metallic", "_WeatheringAll",
                "_WeatheringRange1", "_WeatheringRange2", "_WeatheringRange3", "_WeatheringRange4", "_WeatheringRange5", "_WeatheringRange6",
                "_EmissionStrength", "_DetailGlossScale", "_DetailGlossScale2", "_DetailNormalMapScale", "_DetailNormalMapScale2" })
                SetF(puddleMat, fn, 0f);
            foreach (string cn in new string[] { "_Color", "_Color2", "_Color3", "_Color4" })
                if (puddleMat.HasProperty(cn)) puddleMat.SetColor(cn, Color.white);
            puddleMat.DisableKeyword("_ALPHATEST_ON");
            puddleMat.DisableKeyword("_NORMALMAP");
            // "Transparent" (premultiplied) mode: reflections and highlights stay at full strength while the
            // body of the water is see-through. Fade mode scaled the shine down with the low alpha.
            if (puddlePremultiply)
            {
                puddleMat.DisableKeyword("_ALPHABLEND_ON");
                puddleMat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                SetF(puddleMat, "_Mode", 3f); SetF(puddleMat, "_SrcBlend", 1f);
            }
            else
            {
                puddleMat.EnableKeyword("_ALPHABLEND_ON");
                puddleMat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                SetF(puddleMat, "_Mode", 2f); SetF(puddleMat, "_SrcBlend", 5f);
            }
            SetF(puddleMat, "_DstBlend", 10f); SetF(puddleMat, "_ZWrite", 0f);
            SetF(puddleMat, "_Cutoff", 0f);           // the clothes shader also clips below _Cutoff (0.5)
            SetF(puddleMat, "_SpecularHighlights", 1f); SetF(puddleMat, "_GlossyReflections", 1f);
            SetF(puddleMat, "_AlphaEx", 1f);
            puddleMat.SetOverrideTag("RenderType", "Transparent");
            puddleMat.renderQueue = 3000;
        }
        if (puddleTex == null)
        {
            puddleTex = new Texture2D(64, 64, TextureFormat.RGBA32, true, false);
            puddleTex.wrapMode = TextureWrapMode.Clamp;
            puddleTex.name = "WetLabPuddleTex";
        }
        Color32[] px = new Color32[64 * 64];
        byte r = (byte)(puddleR * 255f), g = (byte)(puddleG * 255f), b = (byte)(puddleB * 255f);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                float dx = (x + 0.5f) / 32f - 1f, dy = (y + 0.5f) / 32f - 1f;
                float rr = Mathf.Sqrt(dx * dx + dy * dy);
                float a = rr < 0.7f ? 1f : Mathf.Clamp01(1f - (rr - 0.7f) / 0.3f);
                a = a * a * (3f - 2f * a);
                px[y * 64 + x] = new Color32(r, g, b, (byte)(a * puddleA * 255f));
            }
        puddleTex.SetPixels32(px); puddleTex.Apply(true);
        puddleMat.SetTexture("_MainTex", puddleTex);
        float smooth = Mathf.Lerp(0.6f, 0.98f, Mathf.Clamp01(puddleShine));
        foreach (string gn in new string[] { "_Glossiness", "_Glossiness2", "_Glossiness3", "_Glossiness4" }) SetF(puddleMat, gn, smooth);
        SetF(puddleMat, "_ExGloss", Mathf.Clamp01(puddleShine * 1.5f));
        SetF(puddleMat, "_ExGlossiness", smooth);
        puddleSig = sig;
        return puddleMat;
    }

    void UpdatePuddleMeshes()
    {
        if (puddles.Count == 0) return;
        Material m = PuddleMaterial();
        if (m == null) return;   // no garment to borrow the shader from: GL fallback draws them
        int layer = cha != null ? cha.gameObject.layer : 0;
        foreach (Puddle pd in puddles)
        {
            if (pd.go == null)
            {
                pd.go = new GameObject("WetLabPuddle");
                pd.go.layer = layer;
                pd.mesh = new Mesh();
                pd.mesh.name = "WetLabPuddleMesh";
                pd.go.AddComponent<MeshFilter>().sharedMesh = pd.mesh;
                MeshRenderer mr = pd.go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = m;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = true;
            }
            pd.go.GetComponent<MeshRenderer>().sharedMaterial = m;
            if (Mathf.Abs(pd.area - pd.builtArea) < pd.area * 0.02f) continue;
            pd.builtArea = pd.area;
            BuildPuddleMesh(pd);
        }
    }

    void BuildPuddleMesh(Puddle pd)
    {
        float rad = PuddleRadius(pd);
        const int seg = 40, rings = 4;
        int n = 1 + seg * rings;
        Vector3[] v = new Vector3[n];
        Vector2[] uv = new Vector2[n];
        Vector3[] nr = new Vector3[n];
        Vector4[] tg = new Vector4[n];
        v[0] = Vector3.zero; uv[0] = new Vector2(0.5f, 0.5f);
        for (int k = 0; k < rings; k++)
        {
            float f = (k + 1f) / rings;
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                float noise = 1f + 0.16f * Mathf.Sin(a * 3f + pd.seed) + 0.09f * Mathf.Sin(a * 7f + pd.seed * 1.7f);
                int idx = 1 + k * seg + i;
                v[idx] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * rad * noise * f;
                uv[idx] = new Vector2(0.5f + 0.5f * Mathf.Cos(a) * f, 0.5f + 0.5f * Mathf.Sin(a) * f);
            }
        }
        for (int i = 0; i < n; i++) { nr[i] = Vector3.up; tg[i] = new Vector4(1f, 0f, 0f, 1f); }
        List<int> tris = new List<int>();
        for (int i = 0; i < seg; i++) { int j = (i + 1) % seg; tris.Add(0); tris.Add(1 + j); tris.Add(1 + i); }
        for (int k = 1; k < rings; k++)
            for (int i = 0; i < seg; i++)
            {
                int j = (i + 1) % seg;
                int a0 = 1 + (k - 1) * seg + i, a1 = 1 + (k - 1) * seg + j, b0 = 1 + k * seg + i, b1 = 1 + k * seg + j;
                tris.Add(a0); tris.Add(a1); tris.Add(b1);
                tris.Add(a0); tris.Add(b1); tris.Add(b0);
            }
        pd.mesh.Clear();
        pd.mesh.vertices = v; pd.mesh.uv = uv; pd.mesh.normals = nr; pd.mesh.tangents = tg;
        pd.mesh.SetTriangles(tris, 0);
        pd.mesh.RecalculateBounds();
        pd.go.transform.position = pd.center + Vector3.up * 0.015f;
    }

    // ---------------------------------------------------------------- drawing

    void OnRenderObject()
    {
        bool marker = showMarker && showWindow && originBone != null && (targets.Count > 0 || usePickedOrigin);
        if (drops.Count == 0 && splashes.Count == 0 && !AnyPuddles() && !marker) return;
        Camera cam = Camera.current;
        if (cam == null || cam != Camera.main) return;
        if (fxMat == null)
        {
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            fxMat = new Material(sh);
            fxMat.hideFlags = HideFlags.HideAndDontSave;
            fxMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            fxMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            fxMat.SetInt("_Cull", (int)CullMode.Off);
            fxMat.SetInt("_ZWrite", 0);
            fxMat.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        }
        fxMat.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.TRIANGLES);
        // puddles: noisy-edged discs whose alpha fades out toward the rim (no outline), with a soft
        // sheen band on the side facing the camera  (fallback: only puddles without a lit mesh)
        foreach (Puddle pd in AllPuddles())
        {
            if (pd.go != null) continue;
            float rad = PuddleRadius(pd);
            if (rad < 0.02f) continue;
            Vector3 c = pd.center + Vector3.up * 0.015f;
            const int seg = 32;
            Color core = PuddleColor(pd);
            Color edge = new Color(core.r, core.g, core.b, 0f);
            Vector3 toCam = cam.transform.position - c; toCam.y = 0f;
            Vector3 camDir = toCam.sqrMagnitude > 1e-4f ? toCam.normalized : Vector3.forward;
            Vector3 prevIn = Vector3.zero, prevOut = Vector3.zero;
            float prevSheen = 0f;
            for (int i = 0; i <= seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                float n = 1f + 0.16f * Mathf.Sin(a * 3f + pd.seed) + 0.09f * Mathf.Sin(a * 7f + pd.seed * 1.7f);
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Vector3 outer = c + dir * rad * n;
                Vector3 inner = c + dir * rad * n * (1f - puddleEdge);
                float sheen = Mathf.Max(0f, Vector3.Dot(dir, camDir) - 0.2f) * puddleShine;
                if (i > 0)
                {
                    GL.Color(core);
                    GL.Vertex(c); GL.Vertex(prevIn); GL.Vertex(inner);
                    // feathered rim: full colour at 70 % of the radius, transparent at the edge
                    GL.Color(core); GL.Vertex(prevIn);
                    GL.Color(edge); GL.Vertex(prevOut);
                    GL.Color(edge); GL.Vertex(outer);
                    GL.Color(core); GL.Vertex(prevIn);
                    GL.Color(edge); GL.Vertex(outer);
                    GL.Color(core); GL.Vertex(inner);
                }
                prevIn = inner; prevOut = outer; prevSheen = sheen;
            }
        }
        // drops: a rounded head fading into a transparent tail, with a small soft highlight
        Vector3 fwd = cam.transform.forward;
        Color dropCol = new Color(dropR, dropG, dropB, dropA);
        Color dropClear = new Color(dropR, dropG, dropB, 0f);
        foreach (Drop d in drops)
        {
            Vector3 vd = d.vel.sqrMagnitude > 1e-4f ? d.vel.normalized : Vector3.down;
            Vector3 side = Vector3.Cross(vd, fwd);
            if (side.sqrMagnitude < 1e-6f) side = cam.transform.right;
            side = side.normalized * d.size;
            Vector3 tail = d.pos - d.vel * 0.012f - vd * d.size * 1.5f;
            Vector3 head = d.pos + vd * d.size;
            GL.Color(dropCol); GL.Vertex(d.pos - side); GL.Vertex(d.pos + side); GL.Vertex(head);
            GL.Color(dropCol); GL.Vertex(d.pos - side);
            GL.Color(dropCol); GL.Vertex(d.pos + side);
            GL.Color(dropClear); GL.Vertex(tail);
            Vector3 hl = d.pos - side * 0.3f + vd * d.size * 0.3f;
            float hs = d.size * 0.35f;
            GL.Color(new Color(1f, 1f, 1f, dropShine * 0.6f));
            GL.Vertex(hl - side.normalized * hs); GL.Vertex(hl + side.normalized * hs); GL.Vertex(hl + vd * hs * 1.5f);
        }
        foreach (Splash sp in splashes)
        {
            Vector3 side = cam.transform.right * 0.02f, up = cam.transform.up * 0.02f;
            GL.Color(new Color(dropR, dropG, dropB, dropA * Mathf.Clamp01(sp.life / 0.15f)));
            GL.Vertex(sp.pos - side); GL.Vertex(sp.pos + side); GL.Vertex(sp.pos + up);
        }
        if (marker)
        {
            Vector3 o = originBone.TransformPoint(originLocal);
            float m = 0.25f;
            GL.Color(new Color(1f, 0.2f, 0.9f, 0.9f));
            Vector3 rr = cam.transform.right * m * 0.15f, uu = cam.transform.up * m * 0.15f;
            GL.Vertex(o - cam.transform.right * m - uu); GL.Vertex(o - cam.transform.right * m + uu); GL.Vertex(o + cam.transform.right * m);
            GL.Vertex(o - cam.transform.up * m - rr); GL.Vertex(o - cam.transform.up * m + rr); GL.Vertex(o + cam.transform.up * m);
        }
        GL.End();
        GL.PopMatrix();
        DrawPuddleReflections(cam);
    }

    Material glintMat;

    // Specular glints on the puddles, drawn additively: an elongated soft ellipse on the far side of
    // each puddle (where an overhead light would reflect toward the camera), stronger at grazing views.
    // ---------------------------------------------------------------- planar reflection

    Camera reflCam;
    RenderTexture reflRT;
    Material reflMat;
    bool renderingReflection;
    float reflPlaneY;
    int reflFrame = -1;

    // Before the main camera renders, render the scene mirrored in the floor plane into a texture
    // (oblique near plane at the floor, culling inverted); puddles then show it at their screen position.
    void OnCameraPreCull(Camera cam)
    {
        if (renderingReflection || !puddleReflection || !AnyPuddles() || cam == null || cam != Camera.main) return;
        if (reflFrame == Time.frameCount) return;
        reflFrame = Time.frameCount;
        try { RenderReflection(cam); }
        catch (Exception e) { Log.LogWarning("reflection disabled: " + e.Message); puddleReflection = false; }
    }

    void RenderReflection(Camera cam)
    {
        int w = Mathf.Max(64, (int)(cam.pixelWidth * Mathf.Clamp(reflectResolution, 0.1f, 1f)));
        int h = Mathf.Max(64, (int)(cam.pixelHeight * Mathf.Clamp(reflectResolution, 0.1f, 1f)));
        if (reflRT == null || reflRT.width != w || reflRT.height != h)
        {
            if (reflRT != null) { reflRT.Release(); Destroy(reflRT); }
            reflRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            reflRT.name = "WetLabReflection";
            reflRT.hideFlags = HideFlags.HideAndDontSave;
        }
        if (reflCam == null)
        {
            GameObject go = new GameObject("WetLabReflectionCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            reflCam = go.AddComponent<Camera>();
            reflCam.enabled = false;
        }
        reflCam.CopyFrom(cam);
        reflCam.targetTexture = reflRT;
        reflCam.cullingMask = cam.cullingMask & ~(1 << 5);   // no UI
        reflPlaneY = floorY + 0.02f;
        Vector4 plane = new Vector4(0f, 1f, 0f, -reflPlaneY);
        Matrix4x4 refl = ReflectionMatrix(plane);
        Vector3 camPos = cam.transform.position;
        reflCam.transform.position = refl.MultiplyPoint(camPos);
        reflCam.worldToCameraMatrix = cam.worldToCameraMatrix * refl;
        Vector4 clip = CameraSpacePlane(reflCam, new Vector3(0f, reflPlaneY, 0f), Vector3.up, 0.02f);
        reflCam.projectionMatrix = cam.CalculateObliqueMatrix(clip);
        bool oldInvert = GL.invertCulling;
        GL.invertCulling = !oldInvert;
        renderingReflection = true;
        try { reflCam.Render(); }
        finally { renderingReflection = false; GL.invertCulling = oldInvert; }
    }

    static Matrix4x4 ReflectionMatrix(Vector4 p)
    {
        Matrix4x4 m = Matrix4x4.identity;
        m.m00 = 1f - 2f * p.x * p.x; m.m01 = -2f * p.x * p.y; m.m02 = -2f * p.x * p.z; m.m03 = -2f * p.w * p.x;
        m.m10 = -2f * p.y * p.x; m.m11 = 1f - 2f * p.y * p.y; m.m12 = -2f * p.y * p.z; m.m13 = -2f * p.w * p.y;
        m.m20 = -2f * p.z * p.x; m.m21 = -2f * p.z * p.y; m.m22 = 1f - 2f * p.z * p.z; m.m23 = -2f * p.w * p.z;
        m.m30 = 0f; m.m31 = 0f; m.m32 = 0f; m.m33 = 1f;
        return m;
    }

    static Vector4 CameraSpacePlane(Camera c, Vector3 pos, Vector3 normal, float offset)
    {
        Matrix4x4 m = c.worldToCameraMatrix;
        Vector3 cpos = m.MultiplyPoint(pos + normal * offset);
        Vector3 cn = m.MultiplyVector(normal).normalized;
        return new Vector4(cn.x, cn.y, cn.z, -Vector3.Dot(cpos, cn));
    }

    // Draw the mirror image onto each puddle: screen-position texture lookup, Fresnel-weighted, feathered rim.
    void DrawPuddleReflections(Camera cam)
    {
        if (!puddleReflection || reflRT == null || !AnyPuddles()) return;
        if (reflMat == null)
        {
            Shader sh = Shader.Find("UI/Default");
            if (sh == null) { puddleReflection = false; Log.LogWarning("UI/Default shader not found; puddle reflection off"); return; }
            reflMat = new Material(sh);
            reflMat.hideFlags = HideFlags.HideAndDontSave;
            reflMat.SetInt("unity_GUIZTestMode", (int)CompareFunction.LessEqual);
        }
        reflMat.mainTexture = reflRT;
        reflMat.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.TRIANGLES);
        Vector3 camPos = cam.transform.position;
        foreach (Puddle pd in AllPuddles())
        {
            Color pc = PuddleColor(pd);
            float mx = Mathf.Max(0.001f, Mathf.Max(pc.r, Mathf.Max(pc.g, pc.b)));
            Color tint = Color.Lerp(Color.white, new Color(pc.r / mx, pc.g / mx, pc.b / mx), 0.5f);
            float rad = PuddleRadius(pd);
            if (rad < 0.05f) continue;
            Vector3 c = new Vector3(pd.center.x, reflPlaneY + 0.003f, pd.center.z);
            Vector3 toCam = camPos - c;
            if (toCam.y <= 0.01f) continue;
            float cosV = toCam.y / toCam.magnitude;
            float a = reflectStrength * Mathf.Lerp(0.3f, 1f, Mathf.Pow(1f - cosV, 2f));
            Color core = new Color(tint.r, tint.g, tint.b, a), edge = new Color(tint.r, tint.g, tint.b, 0f);
            const int seg = 32;
            Vector3 prevIn = Vector3.zero, prevOut = Vector3.zero;
            for (int i = 0; i <= seg; i++)
            {
                float ang = i * Mathf.PI * 2f / seg;
                float n = 1f + 0.16f * Mathf.Sin(ang * 3f + pd.seed) + 0.09f * Mathf.Sin(ang * 7f + pd.seed * 1.7f);
                Vector3 dir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));
                Vector3 outer = c + dir * rad * n, inner = c + dir * rad * n * (1f - puddleEdge);
                if (i > 0)
                {
                    RV(cam, c, core); RV(cam, prevIn, core); RV(cam, inner, core);
                    RV(cam, prevIn, core); RV(cam, prevOut, edge); RV(cam, outer, edge);
                    RV(cam, prevIn, core); RV(cam, outer, edge); RV(cam, inner, core);
                }
                prevIn = inner; prevOut = outer;
            }
        }
        GL.End();
        GL.PopMatrix();
    }

    static void RV(Camera cam, Vector3 p, Color col)
    {
        Vector3 vp = cam.WorldToViewportPoint(p);
        GL.Color(col);
        GL.TexCoord2(vp.x, vp.y);
        GL.Vertex(p);
    }


    static void Ellipse(Vector3 c, Vector3 a, Vector3 b, Color col)
    {
        Color rim = new Color(col.r, col.g, col.b, 0f);
        const int seg = 20;
        Vector3 prev = c + a;
        for (int i = 1; i <= seg; i++)
        {
            float t = i * Mathf.PI * 2f / seg;
            Vector3 q = c + a * Mathf.Cos(t) + b * Mathf.Sin(t);
            GL.Color(col); GL.Vertex(c);
            GL.Color(rim); GL.Vertex(prev);
            GL.Color(rim); GL.Vertex(q);
            prev = q;
        }
    }

    static void FeatheredDisc(Vector3 c, float rad, Color col, float seed)
    {
        Color rim = new Color(col.r, col.g, col.b, 0f);
        const int seg = 32;
        Vector3 prev = Vector3.zero;
        for (int i = 0; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            float n = 1f + 0.16f * Mathf.Sin(a * 3f + seed) + 0.09f * Mathf.Sin(a * 7f + seed * 1.7f);
            Vector3 q = c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * rad * n;
            if (i > 0)
            {
                GL.Color(col); GL.Vertex(c);
                GL.Color(rim); GL.Vertex(prev);
                GL.Color(rim); GL.Vertex(q);
            }
            prev = q;
        }
    }


    // ---------------------------------------------------------------- Studio left toolbar button

    KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle toolbarToggle;
    bool toolbarFailed;
    Texture2D toolbarIcon;

    void EnsureToolbarButton()
    {
        if (toolbarToggle != null || toolbarFailed || !StudioAPI.StudioLoaded) return;
        try
        {
            toolbarIcon = MakeToolbarIcon();
            toolbarToggle = new KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle("stp3.wetlab", "Wet Lab", () => toolbarIcon, showWindow,
                ApiOwner(), v => { showWindow = v; });
            KKAPI.Studio.UI.Toolbars.ToolbarManager.AddLeftToolbarControl(toolbarToggle);
        }
        catch (Exception e)
        {
            toolbarFailed = true;
            toolbarToggle = null;
            Log.LogWarning("toolbar button failed: " + e.Message);
        }
    }

    // KoikatuAPI.Instance is internal; the toolbar only uses the owner to label stored button positions
    static BepInEx.BaseUnityPlugin ApiOwner()
    {
        System.Reflection.PropertyInfo pi = typeof(KKAPI.KoikatuAPI).GetProperty("Instance",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        return pi != null ? pi.GetValue(null, null) as BepInEx.BaseUnityPlugin : null;
    }

    void SyncToolbarButton()
    {
        if (toolbarToggle != null && !toolbarToggle.IsDisposed && toolbarToggle.Toggled.Value != showWindow)
            toolbarToggle.Toggled.OnNext(showWindow);
    }

    void RemoveToolbarButton()
    {
        if (toolbarToggle != null)
        {
            try { KKAPI.Studio.UI.Toolbars.ToolbarManager.RemoveControl(toolbarToggle); }
            catch (Exception) { }
            toolbarToggle = null;
        }
        if (toolbarIcon != null) Destroy(toolbarIcon);
    }

    static void IconDot(Color32[] px, int x, int y, int r)
    {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= 32 || yy >= 32 || dx * dx + dy * dy > r * r + r) continue;
                px[yy * 32 + xx] = new Color32(255, 255, 255, 255);
            }
    }

    static void IconLine(Color32[] px, int x0, int y0, int x1, int y1, int r)
    {
        int n = Mathf.Max(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0)) * 2 + 1;
        for (int i = 0; i <= n; i++)
            IconDot(px, Mathf.RoundToInt(Mathf.Lerp(x0, x1, i / (float)n)), Mathf.RoundToInt(Mathf.Lerp(y0, y1, i / (float)n)), r);
    }

    // a water drop (y = 0 is the bottom row)
    static Texture2D MakeToolbarIcon()
    {
        Color32[] px = new Color32[32 * 32];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);
        IconDot(px, 16, 11, 8);
        for (int y = 11; y <= 28; y++)
        {
            int half = Mathf.RoundToInt(8f * (28 - y) / 17f);
            for (int x = 16 - half; x <= 16 + half; x++) px[y * 32 + x] = new Color32(255, 255, 255, 255);
        }
        IconDot(px, 13, 9, 2);   // highlight: cut a small hole
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (dx * dx + dy * dy <= 4) px[(9 + dy) * 32 + 12 + dx] = new Color32(0, 0, 0, 0);
        Texture2D t = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        t.SetPixels32(px); t.Apply(false);
        t.name = "WetLabIcon";
        return t;
    }

    // ---------------------------------------------------------------- GUI

    float UiScale()
    {
        if (uiScale > 0f) return uiScale;
        return Mathf.Max(1f, Mathf.Round(Screen.height / 1080f * 4f) / 4f);
    }

    // field: the setting's field name; its "def" button puts back the plugin's own default (greyed out at it)
    float Slider(string label, float v, float min, float max, string fmt, string field = null)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(110));
        v = GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(120));
        GUILayout.Label(v.ToString(fmt), GUILayout.Width(50));
        float d;
        if (field != null && defaultValues.TryGetValue(field, out d))
        {
            bool was = GUI.enabled;
            GUI.enabled = was && Mathf.Abs(v - d) > 1e-6f;
            if (GUILayout.Button("def", GUILayout.Width(32))) v = d;
            GUI.enabled = was;
        }
        GUILayout.EndHorizontal();
        return v;
    }

    void OnGUI()
    {
        if (!showWindow || !StudioAPI.StudioLoaded) return;
        GUI.depth = -1000;   // draw (and take clicks) above other plugins' IMGUI
        float sc = UiScale();
        Matrix4x4 old = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(sc, sc, 1f));
        float sw = Screen.width / sc, sh = Screen.height / sc;
        windowRect.width = 380f;
        windowRect.height = Mathf.Max(200f, Mathf.Min(sh - 40f, 900f));
        if (windowRect.x < 0f) windowRect.x = Mathf.Max(0f, sw - windowRect.width - 20f - (480f + 20f));   // left of Ragdoll Lab (480 wide)
        windowRect = GUI.Window(0x5745544C, windowRect, DrawWindow, "Wet Lab");
        windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, sw - 60f));
        windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, sh - windowRect.height));
        GUI.matrix = old;
    }

    void DrawWindow(int id)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("UI " + UiScale().ToString("0.00"), GUILayout.Width(60));
        if (GUILayout.Button("-")) uiScale = Mathf.Max(0.75f, UiScale() - 0.25f);
        if (GUILayout.Button("+")) uiScale = Mathf.Min(4f, UiScale() + 0.25f);
        if (GUILayout.Button("auto")) uiScale = 0f;
        if (GUILayout.Button("Defaults")) ApplySettings(defaults);
        GUILayout.EndHorizontal();

        scroll = GUILayout.BeginScrollView(scroll);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Attach to selected")) { useFixedFloor = false; AttachSelected(); }
        if (GUILayout.Button("Remove")) { Detach(); ClearPuddles(); status = "Removed (clothes restored)."; }
        GUILayout.EndHorizontal();
        if (parked.Count > 0)
        {
            GUILayout.Label("Editing: " + (cha != null ? cha.fileParam.fullname : "nobody") + ". Also wet: " + ParkedNames()
                + " (select one and Attach to edit it again).");
            if (GUILayout.Button("Remove all (every character)")) { RemoveAll(); status = "Removed from every character."; }
        }
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Start")) { t = 0f; running = true; looping = false; }
        if (GUILayout.Button("Full")) { t = spreadTime + dripTail; running = false; }
        if (GUILayout.Button("Dry")) { t = 0f; running = false; looping = false; DryAll(); }
        if (GUILayout.Button("Clear puddle")) ClearPuddles();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Keep stain as layer" + (layers > 0 ? " (" + layers + ")" : ""))) KeepLayer();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(looping ? "Looping (" + (loopCount + 1) + ")" : "Loop"))
        {
            if (targets.Count > 0) { looping = true; loopCount = 0; RestartLoop(); loopCount = 0; }
            else status = "Attach first.";
        }
        if (GUILayout.Button("Stop")) { looping = false; running = false; }
        GUILayout.EndHorizontal();
        loopHold = Slider("Loop hold (s)", loopHold, 0f, 20f, "0.0", "loopHold");
        GUILayout.BeginHorizontal();
        includeSkin = GUILayout.Toggle(includeSkin, "Include skin/face");
        includeHair = GUILayout.Toggle(includeHair, "Include hair  [re-attach]");
        GUILayout.EndHorizontal();
        showMarker = GUILayout.Toggle(showMarker, "Show origin marker");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(pickArmed ? "Click on the body..." : "Pick origin")) pickArmed = !pickArmed;
        if (GUILayout.Button("Default origin")) { usePickedOrigin = false; pickedBone = null; if (targets.Count > 0) AttachIncremental(); }
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Water preset")) WaterPreset();
        if (GUILayout.Button("Blood preset")) BloodPreset();
        GUILayout.EndHorizontal();

        GUILayout.Label("-- spread --");
        maxRadius = Slider("Max radius", maxRadius, 0.1f, 10f, "0.00", "maxRadius");
        spreadTime = Slider("Spread time (s)", spreadTime, 0f, 120f, "0", "spreadTime");
        GUILayout.Label("-- wet look --");
        saturate = Slider("Saturation", saturate, 0.5f, 2f, "0.00", "saturate");
        wetGloss = Slider("Gloss map", wetGloss, 0f, 1f, "0.00", "wetGloss");
        tintR = Slider("Tint R", tintR, 0f, 2f, "0.00", "tintR");
        tintG = Slider("Tint G", tintG, 0f, 2f, "0.00", "tintG");
        tintB = Slider("Tint B", tintB, 0f, 2f, "0.00", "tintB");
        stainAmount = Slider("Stain amount", stainAmount, 0f, 1f, "0.00", "stainAmount");
        stainR = Slider("Stain R", stainR, 0f, 1f, "0.00", "stainR");
        stainG = Slider("Stain G", stainG, 0f, 1f, "0.00", "stainG");
        stainB = Slider("Stain B", stainB, 0f, 1f, "0.00", "stainB");
        skinWater = Slider("Skin", skinWater, 0f, 1f, "0.00", "skinWater");
        glossShell = GUILayout.Toggle(glossShell, "Gloss overlay");
        shellGloss = Slider("Overlay wet", shellGloss, 0f, 1f, "0.00", "shellGloss");
        shellSmooth = Slider("Overlay smooth", shellSmooth, 0f, 1f, "0.00", "shellSmooth");
        shellOpacity = Slider("Overlay opacity", shellOpacity, 0f, 1f, "0.00", "shellOpacity");
        GUILayout.Label("-- shape --");
        upFactor = Slider("Up resist", upFactor, 0.5f, 6f, "0.00", "upFactor");
        downFactor = Slider("Down ease", downFactor, 0.1f, 2f, "0.00", "downFactor");
        frontFactor = Slider("Front resist", frontFactor, 0.3f, 3f, "0.00", "frontFactor");
        backFactor = Slider("Back resist", backFactor, 0.3f, 3f, "0.00", "backFactor");
        sideFactor = Slider("Side resist", sideFactor, 0.3f, 3f, "0.00", "sideFactor");
        outerResist = Slider("Outer thigh resist", outerResist, 0f, 5f, "0.00", "outerResist");
        edgeNoise = Slider("Edge noise", edgeNoise, 0f, 2f, "0.00", "edgeNoise");
        feather = Slider("Edge soft", feather, 0.02f, 1.5f, "0.00", "feather");
        originDown = Slider("Origin down", originDown, -2f, 3f, "0.00", "originDown");
        originOut = Slider("Origin out", originOut, -0.5f, 1f, "0.00", "originOut");
        GUILayout.Label("-- drips --");
        drips = GUILayout.Toggle(drips, "Drips and puddle");
        dripRate = Slider("Drops/s", dripRate, 0f, 80f, "0", "dripRate");
        dripTail = Slider("Drip tail (s)", dripTail, 0f, 30f, "0.0", "dripTail");
        dropSize = Slider("Drop size", dropSize, 0.02f, 0.2f, "0.00", "dropSize");
        puddleGrowth = Slider("Puddle growth", puddleGrowth, 0f, 5f, "0.00", "puddleGrowth");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Floor", GUILayout.Width(50));
        int fm = GUILayout.Toolbar(floorMode, new[] { "Map surface", "Feet", "Fixed" });
        GUILayout.EndHorizontal();
        if (fm != floorMode)
        {
            if (fm == 2) floorHeight = floorY - floorOffset;   // keep the floor where it is now
            floorMode = fm;
            if (useFixedFloor) useFixedFloor = false;           // the user takes over from a script's floor
        }
        floorOffset = Slider("Floor offset", floorOffset, -2f, 2f, "0.00", "floorOffset");
        if (cha != null)
        {
            float mf = MapFloor(), ff = FeetFloor();
            GUILayout.Label("  floor y " + floorY.ToString("0.00") + (useFixedFloor ? " (set by a script)" : "")
                + "   map " + (float.IsNaN(mf) ? "none" : mf.ToString("0.00")) + "   feet " + ff.ToString("0.00"));
        }
        dropR = Slider("Drop R", dropR, 0f, 1f, "0.00", "dropR");
        dropG = Slider("Drop G", dropG, 0f, 1f, "0.00", "dropG");
        dropB = Slider("Drop B", dropB, 0f, 1f, "0.00", "dropB");
        dropA = Slider("Drop opacity", dropA, 0f, 1f, "0.00", "dropA");
        dropShine = Slider("Drop shine", dropShine, 0f, 1f, "0.00", "dropShine");
        puddleR = Slider("Puddle R", puddleR, 0f, 1f, "0.00", "puddleR");
        puddleG = Slider("Puddle G", puddleG, 0f, 1f, "0.00", "puddleG");
        puddleB = Slider("Puddle B", puddleB, 0f, 1f, "0.00", "puddleB");
        puddleA = Slider("Puddle opacity", puddleA, 0f, 1f, "0.00", "puddleA");
        puddleShine = Slider("Puddle shine", puddleShine, 0f, 1f, "0.00", "puddleShine");
        puddleEdge = Slider("Puddle edge", puddleEdge, 0.005f, 0.5f, "0.000", "puddleEdge");
        puddleScale = Slider("Puddle size", puddleScale, 0.3f, 4f, "0.00", "puddleScale");
        puddleReflection = GUILayout.Toggle(puddleReflection, "Mirror reflection in puddles");
        reflectStrength = Slider("Reflection", reflectStrength, 0f, 1f, "0.00", "reflectStrength");
        reflectResolution = Slider("Refl. resolution", reflectResolution, 0.1f, 1f, "0.00", "reflectResolution");
        GUILayout.EndScrollView();

        GUILayout.Label("Targets " + targets.Count + (running ? "  t=" + t.ToString("0.0") + " s" : "") + "  drops " + drops.Count);
        if (!string.IsNullOrEmpty(status)) GUILayout.Label(status);
        GUI.DragWindow();
    }

    // ---------------------------------------------------------------- settings file

    static string SettingsPath { get { return Path.Combine(BepInEx.Paths.ConfigPath, "stp3.wetlab.settings.txt"); } }

    string SerializeSettings()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        CultureInfo ci = CultureInfo.InvariantCulture;
        foreach (FieldInfo f in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (NotSaved.Contains(f.Name)) continue;
            object v = f.GetValue(this);
            string s = null;
            if (v is float) s = ((float)v).ToString("R", ci);
            else if (v is int) s = ((int)v).ToString(ci);
            else if (v is bool) s = (bool)v ? "true" : "false";
            if (s != null) sb.Append(f.Name).Append('=').Append(s).Append('\n');
        }
        return sb.ToString();
    }

    void ApplySettings(string text)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            FieldInfo f = GetType().GetField(line.Substring(0, eq).Trim(), BindingFlags.Public | BindingFlags.Instance);
            if (f == null || NotSaved.Contains(f.Name)) continue;
            string val = line.Substring(eq + 1).Trim();
            try
            {
                if (f.FieldType == typeof(float)) f.SetValue(this, float.Parse(val, ci));
                else if (f.FieldType == typeof(int)) f.SetValue(this, int.Parse(val, ci));
                else if (f.FieldType == typeof(bool)) f.SetValue(this, val == "true" || val == "True");
            }
            catch (Exception e) { Log.LogWarning("Setting " + line + ": " + e.Message); }
        }
    }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            string text = File.ReadAllText(SettingsPath);
            if (text.Contains("settingsVersion=" + settingsVersion + "\n")) ApplySettings(text);
            else
            {
                // older file: keep only the UI scale, everything else takes the new defaults
                foreach (string line in text.Split('\n')) if (line.StartsWith("uiScale=")) ApplySettings(line);
                Log.LogInfo("Old WetLab settings ignored (defaults changed).");
            }
        }
        catch (Exception e) { Log.LogWarning("Could not load settings: " + e.Message); }
    }

    void SaveSettings()
    {
        try { string s = SerializeSettings(); File.WriteAllText(SettingsPath, s); lastSaved = s; }
        catch (Exception e) { Log.LogWarning("Could not save settings: " + e.Message); }
    }
}
