// #name RagdollLab
// #author stp3
// #desc Ragdoll physics lab for CharaStudio: turn a character into a ragdoll, grab and push it inside a box room.
// #proc_filter StudioNEOV2

// Stage 2 (active ragdoll: joint muscles + pins toward the Studio pose). Loaded by BepInEx.ScriptLoader from <game>\scripts.
// Must stay within C# 7.x as compiled by mcs: no tuples, no local functions, no pattern variables.
//
// Design notes
// - The ragdoll is a separate, unscaled proxy skeleton living in its own local physics scene,
//   so it never touches the game's colliders and we step it ourselves (substeps, time scale).
// - Pose write timing: postfix of ChaControl.LateUpdateForce with the lowest priority, i.e. after HS2Heelz
//   (which re-rotates the feet in its own postfix there). If CharaUtils.Expression already ran this frame,
//   it is run again so the *_dam twist bones follow the physics pose.
// - Each proxy maps to one FK-capable bone (cf_J_Hips, Spine01, Spine02, Neck, ArmUp00, ArmLow01,
//   Hand, LegUp00, LegLow01, Foot01). Every frame we write bone.rotation = proxy.rotation * offset
//   (and the hips position) right before CharaUtils.Expression.LateUpdate, so the *_dam twist bones
//   are computed from the ragdoll pose.
// - Joints are created while the proxies sit in the skeleton's bind (T) pose, read from the
//   p_cf_anim prefab, so joint limits are anatomical. Proxies are then teleported to the live pose.
// - World scale: 1 unit = 10 cm, so default gravity is 98.1 units/s^2.
// - Target pose: in that same hook we first let Studio produce its pose (Animator, plus FKCtrl which
//   we block and run manually), read it as the target, then overwrite it with physics. Characters
//   in IK mode use a snapshot taken at activation instead. Muscles are slerp drives toward the
//   target joint rotation; pins are PD accelerations pulling each body's COM to its target.

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
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

public static class RagdollLabScript
{
    static Harmony harmony;
    static GameObject host;

    public static void Main()
    {
        RagdollLab.Log = BepInEx.Logging.Logger.CreateLogSource("RagdollLab");
        harmony = Harmony.CreateAndPatchAll(typeof(RagdollLabPatches), "stp3.ragdolllab");
        host = new GameObject("RagdollLab");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<RagdollLab>();
        RagdollLab.Log.LogInfo("RagdollLab loaded. Open it from the Studio left toolbar.");
    }

    public static void Unload()
    {
        if (host != null) UnityEngine.Object.DestroyImmediate(host);
        host = null;
        if (harmony != null) harmony.UnpatchSelf();
        harmony = null;
        if (RagdollLab.Log != null) BepInEx.Logging.Logger.Sources.Remove(RagdollLab.Log);
    }
}

public static class RagdollLabPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharaUtils.Expression), "LateUpdate")]
    public static void ExpressionLateUpdatePrefix(CharaUtils.Expression __instance)
    {
        RagdollLab lab = RagdollLab.Instance;
        if (lab != null) lab.OnExpressionLateUpdate(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(ChaControl), "LateUpdateForce")]
    public static void LateUpdateForcePostfix(ChaControl __instance)
    {
        RagdollLab lab = RagdollLab.Instance;
        if (lab != null) lab.OnLateUpdateForce(__instance);
    }

    // Studio's own shortcuts (Esc = exit dialog, W/E/R/Q gizmo modes, Ctrl+D duplicate, digits = cameras, F = focus)
    // must not fire in FPS mode. The FPS toggle is checked here first so Studio never sees its F.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Studio.ShortcutKeyCtrl), "Update")]
    public static bool StudioShortcutPrefix()
    {
        RagdollLab lab = RagdollLab.Instance;
        if (lab == null) return true;
        if (lab.CheckFpsToggle()) return false;
        return !RagdollLab.SuppressKeys;
    }

    // Plugin hotkeys (GDC Ctrl+Shift+G, StudioCharaEditor Shift+D, ...) go through BepInEx KeyboardShortcut.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BepInEx.Configuration.KeyboardShortcut), "IsDown")]
    public static bool ShortcutIsDownPrefix(ref bool __result)
    {
        if (!RagdollLab.SuppressKeys) return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BepInEx.Configuration.KeyboardShortcut), "IsPressed")]
    public static bool ShortcutIsPressedPrefix(ref bool __result)
    {
        if (!RagdollLab.SuppressKeys) return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BepInEx.Configuration.KeyboardShortcut), "IsUp")]
    public static bool ShortcutIsUpPrefix(ref bool __result)
    {
        if (!RagdollLab.SuppressKeys) return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FKCtrl), "LateUpdate")]
    public static bool FKLateUpdatePrefix(FKCtrl __instance)
    {
        RagdollLab lab = RagdollLab.Instance;
        return lab == null || RagdollLab.RunningFK || !lab.BlocksFK(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Studio.CameraControl), "InputMouseProc")]
    public static bool CameraMousePrefix(ref bool __result)
    {
        if (!RagdollLab.BlockCamera && !RagdollLab.FpsActive) return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Studio.CameraControl), "InputKeyProc")]
    public static bool CameraKeyPrefix(ref bool __result)
    {
        if (!RagdollLab.FpsActive) return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Studio.CameraControl), "InputMouseWheelZoomProc")]
    public static bool CameraWheelPrefix(ref bool __result)
    {
        if (!RagdollLab.BlockCamera && !RagdollLab.FpsActive) return true;
        __result = false;
        return false;
    }
}

public partial class RagdollLab : MonoBehaviour
{
    public static RagdollLab Instance;
    public static ManualLogSource Log;
    public static bool BlockCamera;

    // Unused layers in HS2; the matrix is patched so they collide with each other and themselves.
    public const int PartLayer = 25;
    public const int RoomLayer = 26;

    // Settings (live unless noted)
    public float timeScale = 1f;
    public float gravity = 98.1f;          // units/s^2 (1 unit = 10 cm)
    public float totalMass = 50f;          // rebuild to apply
    public float grabStiffness = 60f;      // spring per unit mass
    public float grabMaxForce = 6f;        // multiples of body weight
    public float followStiffness = 400f;   // pins that follow an object: firmer than the mouse (the limb tracks it)
    public float followMaxForce = 20f;     // multiples of body weight
    public float pushSpeed = 20f;          // delta-v units/s if applied at the centre of mass
    public float jointDamping = 0.5f;      // angular drag on parts (damps absolute spin)
    public float jointFriction = 3f;       // joint damping in 1/s: relative-rotation damper = this * inertia about the joint
    public float muscleFreq = 0f;          // rad/s natural frequency of joint muscles toward the target (0 = limp)
    public float muscleDampingRatio = 1f;
    public float pinWeight = 0f;           // 0..1 PD pull of each body toward its target position
    public float pinFreq = 15f;            // rad/s
    public float pinFalloff = 0f;          // units; pin fades as 1/(1+(d/falloff)^2). 0 = never fades
    public bool pinHipsOnly = false;
    public bool gravityComp = true;        // pinned bodies get -g scaled by their effective pin weight
    public const float FloorMargin = 0.1f; // units below the ragdoll's lowest point at activation
    public float blendOutTime = 0.3f;      // s, physics -> Studio pose on release
    // knockdown / get-up
    public float knockDistance = 0f;       // units; pelvis this far from its target -> down (0 = off)
    public float knockDv = 0f;             // units/s; a hit giving the whole body this delta-v -> down (0 = off)
    public float downMuscle = 0.15f;       // muscle scale while down
    public float minDownTime = 1.5f;
    public float getUpTime = 2.5f;
    public bool legSelfCollision = false;  // left vs right leg contacts (off: crossing gaits tangle the legs)
    public bool relocateOnGetUp = true;    // move the Studio character to where the body lies before getting up
    // props
    public int propType = 0;               // 0 ball, 1 box, 2 heavy ball
    public float propMass = 2f;
    public float propSpeed = 80f;          // units/s
    public float propSize = 1.2f;          // units
    public float propLifetime = 20f;
    bool foldKnock = true, foldProps = true, foldSim = false;
    public int solverIterations = 20;
    public int velocityIterations = 8;
    public int substepHz = 240;
    public int maxSubsteps = 12;
    public bool showColliders = true;
    public bool showRoom = true;
    public bool flipHingeSign = false;     // rebuild to apply; safety valve if knees/elbows bend backwards
    public Vector3 roomSize = new Vector3(40f, 30f, 40f);

    internal UnityEngine.SceneManagement.Scene labScene;
    internal PhysicsScene phys;
    bool sceneReady;
    internal PhysicMaterial material;

    GameObject roomRoot;
    Vector3 builtRoomSize;          // the size the current room was built with
    Vector3 pendingRoomSize;        // a changed size waiting for the slider to settle
    float roomSizeChangedAt = -1f;
    readonly List<Ragdoll> dolls = new List<Ragdoll>();
    float accum;
    int lastSteps;
    bool pendingBuild;

    class Grab
    {
        public Ragdoll doll;
        public Rigidbody body;
        public GameObject anchorGo;
        public Rigidbody anchor;
        public ConfigurableJoint joint;
        public float depth;
        public Vector3 goal;
        public string label = "";        // "Name hand_L" (window list)
        public Transform follow;         // a pin whose anchor follows a Studio object (an IK anchor item, a folder...)
        public string followName = "";
        public Vector3 followOffset;     // the anchor in that object's frame
        public Vector2 drive;            // stiffness and force cap last applied (SetDrive)
    }

    // A Studio folder that copies the pose of a ragdoll part every frame: items parented under it (or constrained to
    // it with NodesConstraints) move with the ragdoll. It stays in the scene, still, once the ragdoll is released.
    class Follower
    {
        public Ragdoll doll;
        public string part;
        public OCIFolder folder;
    }
    readonly List<Follower> followers = new List<Follower>();
    Grab active;
    readonly List<Grab> pins = new List<Grab>();

    bool showWindow = false;   // opened from the Studio left toolbar
    Rect windowRect = new Rect(-1, 60, 480, 600);
    public float uiScale = 0f;             // 0 = auto (screen height / 1080)

    float UiScale()
    {
        if (uiScale > 0f) return uiScale;
        return Mathf.Max(1f, Mathf.Round(Screen.height / 1080f * 4f) / 4f);
    }
    Material lineMat;
    string status = "";

    class Prop
    {
        public GameObject go;
        public Rigidbody rb;
        public float born;
    }
    readonly List<Prop> props = new List<Prop>();
    string lastHit = "";

    void ThrowProp(Camera cam, Ray ray)
    {
        EnsureScene();
        while (props.Count >= 40) { DestroyProp(props[0]); props.RemoveAt(0); }
        Prop pr = new Prop();
        pr.go = NewLabObject("RL_Prop");
        pr.go.layer = PartLayer;
        pr.go.transform.position = ThrowOrigin(ray, propSize * (propType == 2 ? 2.5f : 1.5f));
        pr.go.transform.rotation = UnityEngine.Random.rotation;
        float mass = propMass;
        Color color;
        PrimitiveType shape;
        Vector3 visScale;
        if (propType == 1)
        {
            BoxCollider bc = pr.go.AddComponent<BoxCollider>();
            bc.size = Vector3.one * propSize * 1.5f;
            shape = PrimitiveType.Cube; visScale = bc.size; color = new Color(0.8f, 0.65f, 0.4f);
        }
        else
        {
            float d = propType == 2 ? propSize * 2.5f : propSize;
            if (propType == 2) mass *= 10f;
            SphereCollider sc = pr.go.AddComponent<SphereCollider>();
            sc.radius = d * 0.5f;
            shape = PrimitiveType.Sphere; visScale = Vector3.one * d;
            color = propType == 2 ? new Color(0.3f, 0.3f, 0.35f) : new Color(1f, 0.55f, 0.15f);
        }
        pr.go.GetComponent<Collider>().sharedMaterial = material;
        pr.rb = pr.go.AddComponent<Rigidbody>();
        pr.rb.mass = mass;
        pr.rb.useGravity = false;
        pr.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        pr.rb.solverIterations = solverIterations;
        pr.rb.velocity = ray.direction * propSpeed;
        pr.rb.maxDepenetrationVelocity = 50f;
        pr.go.AddComponent<RagdollLabPropProbe>();
        AddVisual(pr.go.transform, shape, visScale, color);
        pr.born = Time.time;
        props.Add(pr);
    }

    // The room walls are thick invisible colliders and the Studio camera usually sits outside the room,
    // so a prop spawned at the camera hit the outer wall. Start it just inside the room where the ray enters.
    Vector3 ThrowOrigin(Ray ray, float margin)
    {
        Vector3 fallback = ray.origin + ray.direction * 2f;
        if (roomRoot == null) return fallback;
        Vector3 c = roomRoot.transform.position + new Vector3(0, roomSize.y * 0.5f, 0);
        Vector3 h = roomSize * 0.5f - Vector3.one * margin;
        if (h.x <= 0f || h.y <= 0f || h.z <= 0f) return fallback;
        Vector3 o = ray.origin - c, d = ray.direction;
        float tEnter = float.NegativeInfinity, tExit = float.PositiveInfinity;
        for (int i = 0; i < 3; i++)
        {
            if (Mathf.Abs(d[i]) < 1e-6f)
            {
                if (o[i] < -h[i] || o[i] > h[i]) return fallback;
                continue;
            }
            float t1 = (-h[i] - o[i]) / d[i], t2 = (h[i] - o[i]) / d[i];
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            tEnter = Mathf.Max(tEnter, t1);
            tExit = Mathf.Min(tExit, t2);
        }
        if (tEnter > tExit || tExit < 0f) return fallback;     // ray misses the room
        if (tEnter < 0f) return ray.origin + d * Mathf.Min(2f, tExit * 0.5f);   // camera already inside
        return ray.origin + d * tEnter;
    }

    static void AddVisual(Transform parent, PrimitiveType shape, Vector3 scale, Color color)
    {
        GameObject prim = GameObject.CreatePrimitive(shape);
        Collider c = prim.GetComponent<Collider>();
        if (c != null) DestroyImmediate(c);
        prim.layer = 0;
        prim.transform.SetParent(parent, false);
        prim.transform.localScale = scale;
        MeshRenderer mr = prim.GetComponent<MeshRenderer>();
        if (mr != null && mr.sharedMaterial != null)
        {
            Material m = mr.material;
            if (m.HasProperty("_Color")) m.color = color;
        }
    }

    void DestroyProp(Prop pr)
    {
        if (pr != null && pr.go != null) Destroy(pr.go);
    }

    void ClearProps()
    {
        for (int i = 0; i < props.Count; i++) DestroyProp(props[i]);
        props.Clear();
    }

    public void ReportHit(Rigidbody prop, Collision c)
    {
        Ragdoll doll = c.rigidbody != null ? FindOwner(c.rigidbody) : null;
        if (doll == null) return;
        float dv = c.impulse.magnitude / Mathf.Max(1f, doll.Mass);
        lastHit = "Hit " + c.rigidbody.name.Replace("RL_", "") + ": rel " + (c.relativeVelocity.magnitude / 10f).ToString("0.0")
            + " m/s, impulse " + c.impulse.magnitude.ToString("0") + ", body dv " + (dv / 10f).ToString("0.00") + " m/s";
        if (knockDv > 0f && dv >= knockDv) doll.KnockDown("hit dv " + dv.ToString("0.0"));
    }

    public static bool RunningFK;
    static System.Reflection.MethodInfo fkLateUpdate;

    public static void RunFK(FKCtrl fk)
    {
        if (fkLateUpdate == null) fkLateUpdate = AccessTools.Method(typeof(FKCtrl), "LateUpdate");
        RunningFK = true;
        try { fkLateUpdate.Invoke(fk, null); }
        finally { RunningFK = false; }
    }

    public bool BlocksFK(FKCtrl fk)
    {
        for (int i = 0; i < dolls.Count; i++) if (dolls[i].BlocksFK(fk)) return true;
        return false;
    }

    void Awake()
    {
        Instance = this;
        defaultSettings = SerializeSettings();
        LoadSettings();
        lastSaved = SerializeSettings();
    }

    // ---------------------------------------------------------------- settings file
    // Every public instance field of float/int/bool/Vector3 is a setting; saved as name=value lines.

    static string SettingsPath { get { return Path.Combine(BepInEx.Paths.ConfigPath, "stp3.ragdolllab.settings.txt"); } }
    string defaultSettings = "", lastSaved = "";
    float autosaveT;

    string SerializeSettings()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        CultureInfo ci = CultureInfo.InvariantCulture;
        foreach (FieldInfo f in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            object v = f.GetValue(this);
            string t = null;
            if (v is float) t = ((float)v).ToString("R", ci);
            else if (v is int) t = ((int)v).ToString(ci);
            else if (v is bool) t = (bool)v ? "true" : "false";
            else if (v is Vector3)
            {
                Vector3 q = (Vector3)v;
                t = q.x.ToString("R", ci) + "," + q.y.ToString("R", ci) + "," + q.z.ToString("R", ci);
            }
            if (t != null) sb.Append(f.Name).Append('=').Append(t).Append('\n');
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
            if (f == null) continue;
            string val = line.Substring(eq + 1).Trim();
            try
            {
                if (f.FieldType == typeof(float)) f.SetValue(this, float.Parse(val, ci));
                else if (f.FieldType == typeof(int)) f.SetValue(this, int.Parse(val, ci));
                else if (f.FieldType == typeof(bool)) f.SetValue(this, val == "true" || val == "True");
                else if (f.FieldType == typeof(Vector3))
                {
                    string[] c = val.Trim('(', ')').Split(',');
                    f.SetValue(this, new Vector3(float.Parse(c[0], ci), float.Parse(c[1], ci), float.Parse(c[2], ci)));
                }
            }
            catch (Exception e) { Log.LogWarning("Setting " + line + ": " + e.Message); }
        }
    }

    void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            ApplySettings(File.ReadAllText(SettingsPath));
            Log.LogInfo("Settings loaded from " + SettingsPath);
        }
        catch (Exception e) { Log.LogWarning("Could not load settings: " + e.Message); }
    }

    void SaveSettings()
    {
        try
        {
            string text = SerializeSettings();
            File.WriteAllText(SettingsPath, text);
            lastSaved = text;
        }
        catch (Exception e) { Log.LogWarning("Could not save settings: " + e.Message); }
    }

    void AutosaveSettings(float dt)
    {
        autosaveT += dt;
        if (autosaveT < 3f) return;
        autosaveT = 0f;
        string now = SerializeSettings();
        if (now != lastSaved) SaveSettings();
    }

    void OnDestroy()
    {
        RemoveToolbarButton();
        SaveSettings();
        ExitFps();
        ClearFx();
        ClearProps();
        ReleaseAll(false, true);
        DestroyRoom();
        ClearWorldMeshes();
        if (sceneReady && labScene.IsValid()) SceneManager.UnloadSceneAsync(labScene);
        sceneReady = false;
        if (lineMat != null) Destroy(lineMat);
        if (Instance == this) Instance = null;
        BlockCamera = false;
    }

    // ---------------------------------------------------------------- scene / room

    void EnsureScene()
    {
        if (sceneReady) return;
        labScene = SceneManager.CreateScene("RagdollLabScene_" + Time.frameCount,
            new CreateSceneParameters(LocalPhysicsMode.Physics3D));
        phys = labScene.GetPhysicsScene();
        sceneReady = true;
        Physics.IgnoreLayerCollision(PartLayer, PartLayer, false);
        Physics.IgnoreLayerCollision(PartLayer, RoomLayer, false);
        material = new PhysicMaterial("RagdollLab");
        material.dynamicFriction = 0.6f;
        material.staticFriction = 0.7f;
        material.bounciness = 0f;
        material.frictionCombine = PhysicMaterialCombine.Average;
        material.bounceCombine = PhysicMaterialCombine.Minimum;
        Log.LogInfo("Local physics scene created.");
    }

    internal GameObject NewLabObject(string name)
    {
        EnsureScene();
        GameObject go = new GameObject(name);
        SceneManager.MoveGameObjectToScene(go, labScene);
        return go;
    }

    void BuildRoom(Vector3 center, float floorY)
    {
        BuildRoomSized(center, floorY, roomSize);
        roomExternal = false;
    }

    // Another plugin's room (FpsLab: a huge box whose floor lies under the map, with the map meshes added as world
    // colliders): the Room W/H/D sliders leave it alone and roomSize (a saved setting) is not touched.
    bool roomExternal;

    public void SetWorldRoom(Vector3 center, Vector3 size, float floorY)
    {
        EnsureScene();
        BuildRoomSized(center, floorY, size);
        roomExternal = true;
    }

    void BuildRoomSized(Vector3 center, float floorY, Vector3 size)
    {
        DestroyRoom();
        roomRoot = NewLabObject("RagdollLab_Room");
        roomRoot.transform.position = new Vector3(center.x, floorY, center.z);
        float w = size.x, h = size.y, d = size.z, t = 10f;
        AddWall("Floor", new Vector3(0, -t / 2, 0), new Vector3(w + 2 * t, t, d + 2 * t));
        AddWall("Ceiling", new Vector3(0, h + t / 2, 0), new Vector3(w + 2 * t, t, d + 2 * t));
        AddWall("WallXp", new Vector3(w / 2 + t / 2, h / 2, 0), new Vector3(t, h, d + 2 * t));
        AddWall("WallXn", new Vector3(-w / 2 - t / 2, h / 2, 0), new Vector3(t, h, d + 2 * t));
        AddWall("WallZp", new Vector3(0, h / 2, d / 2 + t / 2), new Vector3(w, h, t));
        AddWall("WallZn", new Vector3(0, h / 2, -d / 2 - t / 2), new Vector3(w, h, t));
        builtRoomSize = size;
        roomSizeChangedAt = -1f;
        Log.LogInfo("Room built at " + roomRoot.transform.position + " size " + size);
    }

    void AddWall(string name, Vector3 localPos, Vector3 size)
    {
        GameObject go = new GameObject(name);
        go.layer = RoomLayer;
        go.transform.SetParent(roomRoot.transform, false);
        go.transform.localPosition = localPos;
        BoxCollider bc = go.AddComponent<BoxCollider>();
        bc.size = size;
        bc.sharedMaterial = material;
    }

    void DestroyRoom()
    {
        if (roomRoot != null) Destroy(roomRoot);
        roomRoot = null;
        roomExternal = false;
    }

    // The room follows Room W / H / D: it is rebuilt in place (same centre and floor) once the size has stopped
    // changing for a moment, so dragging a slider does not rebuild the walls every frame.
    void RebuildRoomIfResized()
    {
        if (roomRoot == null || roomExternal || roomSize == builtRoomSize) { roomSizeChangedAt = -1f; return; }
        if (roomSizeChangedAt < 0f || roomSize != pendingRoomSize)
        {
            pendingRoomSize = roomSize;
            roomSizeChangedAt = Time.realtimeSinceStartup;
            return;
        }
        if (Time.realtimeSinceStartup - roomSizeChangedAt < 0.3f) return;
        Vector3 p = roomRoot.transform.position;
        BuildRoom(p, p.y);
        status = "Room resized to " + roomSize.x.ToString("0") + " x " + roomSize.y.ToString("0") + " x " + roomSize.z.ToString("0") + ".";
    }

    // ---------------------------------------------------------------- frame loop

    void Update()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        CheckFpsToggle();

        if (!StudioAPI.StudioLoaded) return;
        RebuildRoomIfResized();
        EnsureToolbarButton();
        SyncToolbarButton();
        AutosaveSettings(Time.unscaledDeltaTime);

        for (int i = dolls.Count - 1; i >= 0; i--)
        {
            if (!dolls[i].Alive)
            {
                Log.LogInfo("Character gone; dropping ragdoll " + dolls[i].Name);
                DropGrabsOf(dolls[i]);
                dolls[i].Release(false);
                dolls.RemoveAt(i);
            }
        }

        for (int i = 0; i < dolls.Count; i++) dolls[i].UpdateBehaviour(Time.deltaTime * timeScale, this);
        for (int i = props.Count - 1; i >= 0; i--)
        {
            if (props[i].go == null || Time.time - props[i].born > propLifetime) { DestroyProp(props[i]); props.RemoveAt(i); }
        }

        for (int i = dolls.Count - 1; i >= 0; i--)
        {
            if (dolls[i].AdvanceRelease(Time.deltaTime, blendOutTime))
            {
                dolls[i].Release(false);
                dolls.RemoveAt(i);
            }
        }

        if (pendingBuild)
        {
            pendingBuild = false;
            BuildForSelection();
        }

        if (FpsActive) { UpdateFps(Time.unscaledDeltaTime); BlockCamera = true; }
        else HandleMouse(ctrl, shift);
        UpdateFx(RealDt * timeScale);
        Step();
        UpdateFollowers();
    }

    void LateUpdate()
    {
        // Fallback only for characters that LateUpdateForce has not been seen for (hook is once per frame).
        for (int i = 0; i < dolls.Count; i++)
        {
            if (Time.frameCount - dolls[i].LufFrame > 2) dolls[i].RunHookAndExpression();
        }
    }

    public void OnExpressionLateUpdate(CharaUtils.Expression expr)
    {
        for (int i = 0; i < dolls.Count; i++)
        {
            if (dolls[i].ExpressionIs(expr)) dolls[i].ExpressionFrame = Time.frameCount;
        }
    }

    public void OnLateUpdateForce(ChaControl cha)
    {
        for (int i = 0; i < dolls.Count; i++)
        {
            if (!dolls[i].Owns(cha)) continue;
            dolls[i].LufFrame = Time.frameCount;
            dolls[i].RunHookAndExpression();
        }
    }

    static System.Reflection.MethodInfo exprLateUpdate;

    public static void RunExpression(CharaUtils.Expression e)
    {
        if (e == null || !e.isActiveAndEnabled) return;
        if (exprLateUpdate == null) exprLateUpdate = AccessTools.Method(typeof(CharaUtils.Expression), "LateUpdate");
        exprLateUpdate.Invoke(e, null);
    }

    // real seconds per frame; while a video is recorded with Time.captureFramerate, the game advances a fixed step
    // per frame however slowly it renders, and the physics must follow that game time
    static float RealDt { get { return Time.captureFramerate > 0 ? 1f / Time.captureFramerate : Time.unscaledDeltaTime; } }

    void Step()
    {
        if (!sceneReady || (dolls.Count == 0 && props.Count == 0)) { accum = 0; lastSteps = 0; return; }
        UpdatePushers();
        float dt = 1f / Mathf.Max(30, substepHz);
        for (int i = 0; i < dolls.Count; i++) dolls[i].UpdateDrives(this);
        UpdateFollowPins();
        accum += RealDt * timeScale;
        int n = 0;
        while (accum >= dt && n < maxSubsteps)
        {
            accum -= dt;
            n++;
            Vector3 g = new Vector3(0, -gravity, 0);
            for (int i = 0; i < dolls.Count; i++) dolls[i].PreStep(g, this);
            for (int i = 0; i < props.Count; i++)
            {
                Rigidbody prb = props[i].rb;
                if (prb != null && !prb.IsSleeping()) prb.AddForce(g, ForceMode.Acceleration);
            }
            if (active != null) active.anchor.MovePosition(active.goal);
            foreach (Grab pg in pins) if (pg.follow != null && pg.anchor != null) pg.anchor.MovePosition(pg.goal);
            MovePushers();
            phys.Simulate(dt);
        }
        if (n == maxSubsteps) accum = 0;
        lastSteps = n;
    }

    // ---------------------------------------------------------------- selection / build

    void BuildForSelection()
    {
        int built = 0;
        foreach (OCIChar oci in StudioAPI.GetSelectedCharacters())
        {
            ChaControl cha = oci.charInfo;
            if (cha == null) continue;
            bool exists = false;
            for (int i = 0; i < dolls.Count; i++) if (dolls[i].Owns(cha)) exists = true;
            if (exists) { Log.LogInfo(cha.name + " is already a ragdoll."); continue; }

            Ragdoll doll = new Ragdoll(cha, oci);
            try
            {
                if (doll.Build(this))
                {
                    dolls.Add(doll); built++;
                    if (roomRoot == null) BuildRoom(cha.transform.position, doll.LowestPoint() - FloorMargin);
                }
                else doll.Release(false);
            }
            catch (Exception e)
            {
                Log.LogError("Build failed: " + e);
                doll.Release(false);
            }
        }
        status = built > 0 ? ("Ragdolled " + built + " character(s).") : "No selected character was ragdolled (select one in the work tree).";
    }

    void ReleaseAll(bool bake, bool immediate)
    {
        DropGrab(active); active = null;
        for (int i = 0; i < pins.Count; i++) DropGrab(pins[i]);
        pins.Clear();
        if (bake || immediate || blendOutTime <= 0f)
        {
            for (int i = 0; i < dolls.Count; i++) dolls[i].Release(bake);
            dolls.Clear();
        }
        else
        {
            for (int i = 0; i < dolls.Count; i++) dolls[i].BeginRelease();
        }
    }

    // ---------------------------------------------------------------- mouse

    static bool PointerOverUI()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
    }

    internal static Camera StudioCamera()
    {
        Camera cam = Camera.main;
        if (cam != null) return cam;
        Studio.Studio st = Studio.Studio.Instance;
        return st != null && st.cameraCtrl != null ? st.cameraCtrl.mainCmaera : null;
    }

    void HandleMouse(bool ctrl, bool shift)
    {
        Camera cam = StudioCamera();
        Vector2 gm = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        bool overWindow = showWindow && windowRect.Contains(gm / UiScale());

        if (active != null)
        {
            if (Input.GetMouseButton(0) && cam != null && active.body != null)
            {
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (wheel != 0f) active.depth = Mathf.Max(1f, active.depth * (1f - wheel));
                active.goal = cam.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, active.depth));
                if (Input.GetKeyDown(KeyCode.Space))
                {
                    pins.Add(active);
                    active = null;
                    status = "Pinned (" + pins.Count + " pin(s)).";
                }
                else if (Input.GetKeyDown(KeyCode.T))
                {
                    ObjectCtrlInfo sel = SelectedObject();
                    if (sel == null) status = "T pins to the selected Studio object: select one first (an item, a folder...).";
                    else
                    {
                        FollowWith(active, sel, false);
                        pins.Add(active);
                        active = null;
                        status = "Pinned to " + pins[pins.Count - 1].followName + ": the part follows it.";
                    }
                }
                else if (Input.GetKeyDown(KeyCode.N) && active.doll != null)
                {
                    string part = active.doll.KeyOf(active.body);
                    OCIFolder f = part != null ? MakeFollower(active.doll, part) : null;
                    if (f != null) status = "Folder \"" + f.name + "\" follows " + part + ".";
                }
            }
            else
            {
                DropGrab(active);
                active = null;
            }
        }
        else if (shift && !ctrl && Input.GetMouseButtonDown(0) && cam != null && !overWindow && !PointerOverUI() && StudioAPI.StudioLoaded)
        {
            if (roomRoot == null) status = "Make a room first (Ragdoll selected or Room at selection).";
            else
            {
                ThrowProp(cam, cam.ScreenPointToRay(Input.mousePosition));
                status = "Thrown (" + props.Count + " props).";
            }
        }
        else if (Input.GetMouseButtonDown(0) && cam != null && sceneReady && (dolls.Count > 0 || props.Count > 0) && !overWindow && !PointerOverUI())
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;
            if (phys.Raycast(ray.origin, ray.direction, out hit, 10000f, 1 << PartLayer, QueryTriggerInteraction.Ignore)
                && hit.rigidbody != null)
            {
                Ragdoll doll = FindOwner(hit.rigidbody);
                if (doll == null && !ctrl)
                {
                    active = MakeGrab(null, hit.rigidbody, hit.point, cam);
                    status = "Grabbing prop (wheel: depth, Space: pin)";
                }
                else if (doll != null)
                {
                    if (ctrl)
                    {
                        hit.rigidbody.AddForceAtPosition(ray.direction * pushSpeed * doll.Mass, hit.point, ForceMode.Impulse);
                        status = "Pushed " + hit.rigidbody.name;
                    }
                    else
                    {
                        active = MakeGrab(doll, hit.rigidbody, hit.point, cam);
                        status = "Grabbing " + hit.rigidbody.name.Replace("RL_", "") + " (wheel: depth, Space: pin, T: pin to selected object, N: follower folder)";
                    }
                }
            }
        }

        BlockCamera = active != null || overWindow;
    }

    Ragdoll FindOwner(Rigidbody rb)
    {
        for (int i = 0; i < dolls.Count; i++) if (dolls[i].HasBody(rb)) return dolls[i];
        return null;
    }

    Grab MakeGrab(Ragdoll doll, Rigidbody rb, Vector3 point, Camera cam)
    {
        if (doll != null) doll.WakeUp(); else rb.WakeUp();
        Grab g = new Grab();
        g.doll = doll;
        g.body = rb;
        g.anchorGo = NewLabObject("RagdollLab_GrabAnchor");
        g.anchorGo.transform.position = point;
        g.anchor = g.anchorGo.AddComponent<Rigidbody>();
        g.anchor.isKinematic = true;

        ConfigurableJoint j = rb.gameObject.AddComponent<ConfigurableJoint>();
        j.autoConfigureConnectedAnchor = false;
        j.connectedBody = g.anchor;
        j.anchor = rb.transform.InverseTransformPoint(point);
        j.connectedAnchor = Vector3.zero;
        g.joint = j;
        SetDrive(g, grabStiffness, grabMaxForce);

        g.depth = cam != null ? Vector3.Dot(point - cam.transform.position, cam.transform.forward) : 10f;
        g.goal = point;
        g.label = (doll != null ? doll.Name + " " : "prop ") + rb.name.Replace("RL_", "");
        return g;
    }

    // ---------------------------------------------------------------- pins that follow objects, follower folders

    static ObjectCtrlInfo SelectedObject()
    {
        Studio.Studio st = Studio.Studio.Instance;
        if (st == null || st.treeNodeCtrl == null) return null;
        TreeNodeObject node = st.treeNodeCtrl.selectNode;
        ObjectCtrlInfo oci;
        return node != null && st.dicInfo.TryGetValue(node, out oci) ? oci : null;
    }

    static string NameOf(ObjectCtrlInfo oci)
    {
        return oci != null && oci.treeNodeObject != null ? oci.treeNodeObject.textName : "?";
    }

    // the pin's anchor follows `target` from now on, keeping where it is relative to it (snap: at its origin)
    void FollowWith(Grab g, ObjectCtrlInfo target, bool snap)
    {
        Transform t = target.guideObject.transformTarget;
        g.follow = t;
        g.followName = NameOf(target);
        g.followOffset = snap ? Vector3.zero : t.InverseTransformPoint(g.goal);
        g.goal = t.TransformPoint(g.followOffset);
        SetDrive(g, followStiffness, followMaxForce);
    }

    // anchors of pins that follow objects go where those objects are now (before the substeps)
    void UpdateFollowPins()
    {
        foreach (Grab g in pins)
        {
            if (g.follow == null || g.anchor == null) continue;
            if (g.drive.x != followStiffness || g.drive.y != followMaxForce) SetDrive(g, followStiffness, followMaxForce);
            Vector3 goal = g.follow.TransformPoint(g.followOffset);
            if ((goal - g.goal).sqrMagnitude > 1e-8f)
            {
                if (g.doll != null) g.doll.WakeUp(); else if (g.body != null) g.body.WakeUp();
            }
            g.goal = goal;
        }
    }

    OCIFolder MakeFollower(Ragdoll doll, string part)
    {
        Vector3 pos; Quaternion rot;
        if (!doll.BonePose(part, out pos, out rot)) return null;
        OCIFolder f = AddObjectFolder.Add();
        f.name = "RL " + doll.Name + " " + part;
        followers.Add(new Follower { doll = doll, part = part, folder = f });
        UpdateFollowers();
        return f;
    }

    // after the physics step: each follower folder takes its part's pose (in its parent's frame)
    void UpdateFollowers()
    {
        for (int i = followers.Count - 1; i >= 0; i--)
        {
            Follower f = followers[i];
            if (f.folder == null || f.folder.objectItem == null) { followers.RemoveAt(i); continue; }
            if (!dolls.Contains(f.doll)) continue;   // released: the folder stays where it was
            Vector3 pos; Quaternion rot;
            if (!f.doll.BonePose(f.part, out pos, out rot)) continue;
            Transform par = f.folder.objectItem.transform.parent;
            f.folder.objectInfo.changeAmount.pos = par != null ? par.InverseTransformPoint(pos) : pos;
            f.folder.objectInfo.changeAmount.rot = (par != null ? Quaternion.Inverse(par.rotation) * rot : rot).eulerAngles;
        }
    }

    // ---- for scripts and Studio Bridge

    // pins a part where it is now; with `follow`, the pin follows that object (snap: pulled to its origin)
    public bool PinPart(OCIChar oci, string part, ObjectCtrlInfo follow, bool snap)
    {
        Ragdoll d = DollOf(oci);
        Rigidbody rb = d != null ? d.BodyOf(part) : null;
        if (rb == null) return false;
        Grab g = MakeGrab(d, rb, rb.worldCenterOfMass, StudioCamera());
        if (follow != null) FollowWith(g, follow, snap);
        pins.Add(g);
        return true;
    }

    // drops the pins of one character (null: all); returns how many
    public int Unpin(OCIChar oci)
    {
        Ragdoll d = oci != null ? DollOf(oci) : null;
        if (oci != null && d == null) return 0;
        int n = 0;
        for (int i = pins.Count - 1; i >= 0; i--)
        {
            if (d != null && pins[i].doll != d) continue;
            DropGrab(pins[i]); pins.RemoveAt(i); n++;
        }
        return n;
    }

    // one line per pin: what it holds, what it follows, where its anchor should be and is
    public string PinReport()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (Grab g in pins)
            sb.Append(g.label).Append(" | follow ").Append(g.follow != null ? g.followName + " at " + g.follow.position.ToString("F2") : "-")
              .Append(" | goal ").Append(g.goal.ToString("F2")).Append(" | anchor ").Append(g.anchorGo != null ? g.anchorGo.transform.position.ToString("F2") : "-")
              .Append(" | body ").Append(g.body != null ? g.body.worldCenterOfMass.ToString("F2") : "-")
              .Append(" | joint ").Append(g.joint != null ? g.body.transform.TransformPoint(g.joint.anchor).ToString("F2") + " to "
                  + (g.joint.connectedBody != null ? g.joint.connectedBody.position.ToString("F2") : "none") + " sleeping " + g.body.IsSleeping() + " spring " + g.drive.x.ToString("0") : "gone")
              .Append('\n');
        return sb.ToString();
    }

    // a follower folder for a part; returns its Studio key (-1: no such ragdoll or part)
    public int AddFollower(OCIChar oci, string part)
    {
        Ragdoll d = DollOf(oci);
        OCIFolder f = d != null ? MakeFollower(d, part) : null;
        return f != null ? f.objectInfo.dicKey : -1;
    }

    // the pull toward the anchor: spring per unit mass of the whole doll (or prop), force cap in body weights
    void SetDrive(Grab g, float stiffness, float maxW)
    {
        if (g.joint == null) return;
        float m = g.doll != null ? g.doll.Mass : g.body.mass;
        float k = stiffness * m;
        JointDrive drv = new JointDrive();
        drv.positionSpring = k;
        drv.positionDamper = 2f * Mathf.Sqrt(k * m) * 0.6f;
        drv.maximumForce = maxW * m * gravity;
        g.joint.xDrive = drv; g.joint.yDrive = drv; g.joint.zDrive = drv;
        g.drive = new Vector2(stiffness, maxW);
    }

    void DropGrab(Grab g)
    {
        if (g == null) return;
        if (g.joint != null) Destroy(g.joint);
        if (g.anchorGo != null) Destroy(g.anchorGo);
    }

    void DropGrabsOf(Ragdoll doll)
    {
        if (active != null && active.doll == doll) { DropGrab(active); active = null; }
        for (int i = pins.Count - 1; i >= 0; i--)
        {
            if (pins[i].doll == doll) { DropGrab(pins[i]); pins.RemoveAt(i); }
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
            toolbarToggle = new KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle("stp3.ragdolllab", "Ragdoll Lab", () => toolbarIcon, showWindow,
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

    // a stick figure (y = 0 is the bottom row)
    static Texture2D MakeToolbarIcon()
    {
        Color32[] px = new Color32[32 * 32];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);
        IconDot(px, 16, 25, 4);
        IconLine(px, 16, 20, 16, 12, 1);
        IconLine(px, 7, 17, 25, 19, 1);
        IconLine(px, 16, 12, 10, 3, 1);
        IconLine(px, 16, 12, 23, 4, 1);
        Texture2D t = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        t.SetPixels32(px); t.Apply(false);
        t.name = "RagdollLabIcon";
        return t;
    }

    // ---------------------------------------------------------------- GUI

    void OnGUI()
    {
        if (FpsActive && StudioAPI.StudioLoaded) { DrawHud(); return; }
        if (!showWindow || !StudioAPI.StudioLoaded) return;
        float sc = UiScale();
        Matrix4x4 old = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(sc, sc, 1f));
        float sw = Screen.width / sc, sh = Screen.height / sc;
        // Fixed-size window whose body scrolls. (Re-fitting the height every OnGUI call made the window
        // shake while dragged, because OnGUI runs several times per frame.)
        // wide enough for the widest row (three controls): narrower, the scroll view grew a horizontal bar and
        // every button row was stretched past the window's right edge ("Release all" cut off)
        windowRect.width = 480f;
        windowRect.height = Mathf.Max(200f, Mathf.Min(sh - 40f, 1000f));
        if (windowRect.x < 0f) windowRect.x = sw - windowRect.width - 20f;
        windowRect = GUI.Window(0x52474C42, windowRect, DrawWindow, "Ragdoll Lab");
        windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, sw - 60f));
        windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, sh - windowRect.height));
        GUI.matrix = old;
    }

    float Slider(string label, float v, float min, float max, string fmt)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(110));
        v = GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(150));
        GUILayout.Label(v.ToString(fmt), GUILayout.Width(50));
        GUILayout.EndHorizontal();
        return v;
    }

    Vector2 windowScroll;

    // one row per pin: what it holds, what it follows; make it follow the selected Studio object, or drop it
    void DrawPins()
    {
        if (pins.Count == 0) return;
        for (int i = 0; i < pins.Count; i++)
        {
            Grab g = pins[i];
            GUILayout.BeginHorizontal();
            GUILayout.Label(g.label + (g.follow != null ? " -> " + g.followName : ""), GUILayout.Width(250));
            if (g.follow == null)
            {
                if (GUILayout.Button("Follow selected"))
                {
                    ObjectCtrlInfo sel = SelectedObject();
                    if (sel == null) status = "Select a Studio object (an item, a folder...) for the pin to follow.";
                    else { FollowWith(g, sel, false); status = g.label + " follows " + g.followName + "."; }
                }
            }
            else if (GUILayout.Button("Stay")) { g.follow = null; g.followName = ""; SetDrive(g, grabStiffness, grabMaxForce); }
            if (GUILayout.Button("Unpin", GUILayout.Width(55))) { DropGrab(g); pins.RemoveAt(i); i--; }
            GUILayout.EndHorizontal();
        }
    }

    void DrawWindow(int id)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("UI " + UiScale().ToString("0.00") + (uiScale <= 0f ? " auto" : ""), GUILayout.Width(80));
        if (GUILayout.Button("-")) uiScale = Mathf.Max(0.75f, UiScale() - 0.25f);
        if (GUILayout.Button("+")) uiScale = Mathf.Min(4f, UiScale() + 0.25f);
        if (GUILayout.Button("auto")) uiScale = 0f;
        if (GUILayout.Button("Save")) { SaveSettings(); status = "Settings saved to " + SettingsPath; }
        if (GUILayout.Button("Defaults")) { ApplySettings(defaultSettings); status = "Defaults restored."; }
        GUILayout.EndHorizontal();

        // the body scrolls so the window never runs past the bottom of the screen
        windowScroll = GUILayout.BeginScrollView(windowScroll);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Ragdoll selected")) pendingBuild = true;
        if (GUILayout.Button("Release all")) { ReleaseAll(false, false); status = "Releasing (blend " + blendOutTime.ToString("0.00") + " s)."; }
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Bake pose to FK")) { ReleaseAll(true, true); status = "Baked to FK and released."; }
        if (GUILayout.Button("Unpin all"))
        {
            for (int i = 0; i < pins.Count; i++) DropGrab(pins[i]);
            pins.Clear();
        }
        GUILayout.EndHorizontal();
        DrawPins();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Room at selection"))
        {
            foreach (OCIChar oci in StudioAPI.GetSelectedCharacters())
            {
                if (oci.charInfo == null) continue;
                float floor = oci.charInfo.transform.position.y;
                for (int i = 0; i < dolls.Count; i++) if (dolls[i].Owns(oci.charInfo)) floor = dolls[i].LowestPoint() - FloorMargin;
                BuildRoom(oci.charInfo.transform.position, floor);
                break;
            }
        }
        showRoom = GUILayout.Toggle(showRoom, "Room");
        showColliders = GUILayout.Toggle(showColliders, "Colliders");
        GUILayout.EndHorizontal();

        GUILayout.Label("-- muscles / pins (live) --");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Limp")) { muscleFreq = 0f; pinWeight = 0f; }
        if (GUILayout.Button("Stiff")) { muscleFreq = 20f; muscleDampingRatio = 1f; pinWeight = 0f; }
        if (GUILayout.Button("Puppet")) { muscleFreq = 20f; muscleDampingRatio = 1f; pinWeight = 1f; pinFreq = 12f; pinFalloff = 3f; pinHipsOnly = false; knockDistance = 3.5f; knockDv = 8f; }
        if (GUILayout.Button("Hips pin")) { muscleFreq = 20f; muscleDampingRatio = 1f; pinWeight = 1f; pinFreq = 15f; pinFalloff = 0f; pinHipsOnly = true; knockDistance = 0f; knockDv = 0f; }
        GUILayout.EndHorizontal();
        muscleFreq = Slider("Muscle (rad/s)", muscleFreq, 0f, 60f, "0.0");
        muscleDampingRatio = Slider("Muscle damping", muscleDampingRatio, 0f, 2f, "0.00");
        pinWeight = Slider("Pin weight", pinWeight, 0f, 1f, "0.00");
        pinFreq = Slider("Pin (rad/s)", pinFreq, 2f, 60f, "0.0");
        pinFalloff = Slider("Pin falloff", pinFalloff, 0f, 20f, "0.0");
        GUILayout.BeginHorizontal();
        pinHipsOnly = GUILayout.Toggle(pinHipsOnly, "Pin hips only");
        gravityComp = GUILayout.Toggle(gravityComp, "Gravity comp");
        if (GUILayout.Button("Snap to target")) { for (int i = 0; i < dolls.Count; i++) dolls[i].SnapToTarget(); }
        GUILayout.EndHorizontal();
        blendOutTime = Slider("Blend out (s)", blendOutTime, 0f, 2f, "0.00");

        foldKnock = GUILayout.Toggle(foldKnock, "-- knockdown / get-up --");
        if (foldKnock)
        {
            knockDistance = Slider("Knock dist", knockDistance, 0f, 20f, "0.0");
            knockDv = Slider("Knock hit dv", knockDv, 0f, 100f, "0");
            downMuscle = Slider("Down muscle", downMuscle, 0f, 1f, "0.00");
            minDownTime = Slider("Min down (s)", minDownTime, 0f, 10f, "0.0");
            getUpTime = Slider("Get-up (s)", getUpTime, 0.2f, 5f, "0.0");
            GUILayout.BeginHorizontal();
            relocateOnGetUp = GUILayout.Toggle(relocateOnGetUp, "Relocate on get-up");
            legSelfCollision = GUILayout.Toggle(legSelfCollision, "Leg-leg collision");
            if (GUILayout.Button("Knock down")) { for (int i = 0; i < dolls.Count; i++) dolls[i].KnockDown("button"); }
            if (GUILayout.Button("Reseat twisted")) { for (int i = 0; i < dolls.Count; i++) dolls[i].ReseatTwisted(45f, "button"); }
            GUILayout.EndHorizontal();
        }

        foldProps = GUILayout.Toggle(foldProps, "-- props (Shift+click throws) --");
        if (foldProps)
        {
            propType = GUILayout.Toolbar(propType, new string[] { "Ball", "Box", "Heavy" });
            propMass = Slider("Prop mass", propMass, 0.1f, 30f, "0.0");
            propSpeed = Slider("Prop speed", propSpeed, 5f, 300f, "0");
            propSize = Slider("Prop size", propSize, 0.3f, 5f, "0.0");
            GUILayout.BeginHorizontal();
            GUILayout.Label("props " + props.Count);
            if (GUILayout.Button("Clear props")) ClearProps();
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(lastHit)) GUILayout.Label(lastHit);
        }

        DrawFpsSettings();

        foldSim = GUILayout.Toggle(foldSim, "-- simulation --");
        if (foldSim)
        {
        timeScale = Slider("Time scale", timeScale, 0f, 1f, "0.00");
        gravity = Slider("Gravity", gravity, 0f, 200f, "0.0");
        pushSpeed = Slider("Push speed", pushSpeed, 0f, 100f, "0");
        grabStiffness = Slider("Grab stiffness", grabStiffness, 5f, 300f, "0");
        grabMaxForce = Slider("Grab max (xW)", grabMaxForce, 0.5f, 20f, "0.0");
        followStiffness = Slider("Follow pin stiff", followStiffness, 5f, 2000f, "0");
        followMaxForce = Slider("Follow pin max", followMaxForce, 0.5f, 60f, "0.0");
        jointDamping = Slider("Angular drag", jointDamping, 0f, 5f, "0.00");
        jointFriction = Slider("Joint friction", jointFriction, 0f, 30f, "0.0");
        solverIterations = (int)Slider("Solver iter", solverIterations, 4, 60, "0");
        velocityIterations = (int)Slider("Velocity iter", velocityIterations, 1, 30, "0");
        substepHz = (int)Slider("Substep Hz", substepHz, 60, 1000, "0");
        GUILayout.Label("-- applied on next Ragdoll --");
        totalMass = Slider("Total mass", totalMass, 5f, 150f, "0");
        flipHingeSign = GUILayout.Toggle(flipHingeSign, "Flip hinge sign (if knees/elbows bend backwards)");
        GUILayout.Label("-- room size (the room is rebuilt at once) --");
        roomSize.x = Slider("Room W", roomSize.x, 10f, 200f, "0");
        roomSize.y = Slider("Room H", roomSize.y, 10f, 100f, "0");
        roomSize.z = Slider("Room D", roomSize.z, 10f, 200f, "0");
        }

        GUILayout.EndScrollView();
        GUILayout.Label("Drag: grab  Ctrl+click: push  Shift+click: throw  Space: pin");
        GUILayout.Label("Dolls " + dolls.Count + "  pins " + pins.Count + "  substeps/frame " + lastSteps);
        for (int i = 0; i < dolls.Count; i++) GUILayout.Label(dolls[i].Name + ": " + dolls[i].MotionSummary());
        if (!string.IsNullOrEmpty(status)) GUILayout.Label(status);
        GUI.DragWindow();
    }

    // ---------------------------------------------------------------- debug drawing

    void OnRenderObject()
    {
        if (Camera.current == null || Camera.current != StudioCamera()) return;
        if (lineMat == null)
        {
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh == null) return;
            lineMat = new Material(sh);
            lineMat.hideFlags = HideFlags.HideAndDontSave;
            lineMat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            lineMat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            lineMat.SetInt("_Cull", (int)CullMode.Off);
            lineMat.SetInt("_ZWrite", 0);
            lineMat.SetInt("_ZTest", (int)CompareFunction.Always);
        }
        lineMat.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.LINES);
        if (showRoom && roomRoot != null)
        {
            GL.Color(new Color(0.6f, 0.8f, 1f, 0.5f));
            Vector3 o = roomRoot.transform.position;
            Vector3 c = o + new Vector3(0, roomSize.y / 2, 0);
            DrawBox(c, Quaternion.identity, roomSize);
        }
        if (showColliders)
        {
            for (int i = 0; i < dolls.Count; i++) dolls[i].DrawColliders(active != null ? active.body : null);
        }
        GL.Color(new Color(1f, 0.6f, 0.2f, 0.6f));
        for (int i = 0; i < props.Count; i++)
        {
            if (props[i].go == null) continue;
            Transform t = props[i].go.transform;
            BoxCollider pb = props[i].go.GetComponent<BoxCollider>();
            SphereCollider ps = props[i].go.GetComponent<SphereCollider>();
            if (pb != null) DrawBox(t.position, t.rotation, pb.size);
            else if (ps != null) { DrawCircle(t.position, t.up, ps.radius); DrawCircle(t.position, t.right, ps.radius); }
        }
        GL.Color(new Color(1f, 0.3f, 0.2f, 0.9f));
        if (active != null && active.joint != null) GLLine(active.body.transform.TransformPoint(active.joint.anchor), active.goal);
        for (int i = 0; i < pins.Count; i++)
        {
            if (pins[i].joint != null) GLLine(pins[i].body.transform.TransformPoint(pins[i].joint.anchor), pins[i].anchorGo.transform.position);
        }
        GL.End();
        GL.PopMatrix();
        DrawFx(Camera.current);
    }

    internal static void GLLine(Vector3 a, Vector3 b)
    {
        GL.Vertex(a); GL.Vertex(b);
    }

    internal static void DrawBox(Vector3 c, Quaternion r, Vector3 size)
    {
        Vector3 h = size * 0.5f;
        Vector3[] p = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            p[i] = c + r * new Vector3((i & 1) == 0 ? -h.x : h.x, (i & 2) == 0 ? -h.y : h.y, (i & 4) == 0 ? -h.z : h.z);
        }
        int[] e = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7 };
        for (int i = 0; i < e.Length; i += 2) GLLine(p[e[i]], p[e[i + 1]]);
    }

    internal static void DrawCircle(Vector3 c, Vector3 axis, float r)
    {
        Vector3 u = Vector3.Cross(axis, Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right).normalized * r;
        Vector3 v = Vector3.Cross(axis.normalized, u);
        Vector3 prev = c + u;
        for (int i = 1; i <= 16; i++)
        {
            float a = i * Mathf.PI * 2f / 16f;
            Vector3 cur = c + u * Mathf.Cos(a) + v * Mathf.Sin(a);
            GLLine(prev, cur);
            prev = cur;
        }
    }
}

public class RagdollLabPropProbe : MonoBehaviour
{
    Rigidbody rb;
    void Awake() { rb = GetComponent<Rigidbody>(); }
    void OnCollisionEnter(Collision c)
    {
        RagdollLab lab = RagdollLab.Instance;
        if (lab != null) lab.ReportHit(rb, c);
    }
}

public partial class Ragdoll
{
    class Part
    {
        public string key;
        public Transform bone;
        public Transform tr;
        public Rigidbody rb;
        public Collider col;
        public Part parent;
        public float massFrac;
        public Quaternion offset;   // bone.rotation = proxy.rotation * offset
        public bool isRoot;
        public ConfigurableJoint joint;
        public float pivotInertia;  // scalar inertia about the joint anchor, for the friction damper
        public Vector3 flexAxisP;   // flexion axis in the parent's frame (bind): +angle = flexion
        public Quaternion crouchLocal; // synthetic crouch target (Inverse(parent.rot) * rot)
        public float subInertia;    // inertia of this part and everything below it about the joint anchor
        public float weak;          // 0..1 hit-reaction weakening of this body's pin and muscle (decays)
        public bool hold;           // keeps its pose relative to the parent at full strength, even when dead (tied limbs)
        public Quaternion holdLocal;
        public float appliedW = -1f; // muscle frequency last written to the drive
        public Quaternion startLocal; // Inverse(parent.rot) * rot when the joint was created (bind pose)
        // target pose (written by OnLateUpdateHook)
        public bool hasTarget;
        public Vector3 tgtPos;
        public Quaternion tgtBoneRot;
        public Quaternion tgtProxyRot;
        public Vector3 tgtCom;
        public Vector3 tgtVel;
    }

    bool snapshotMode;          // false: live Studio pose each frame; true: frozen activation pose
    FKCtrl fk;
    bool liveFK;
    Transform hipsT;
    Transform[] chain;          // hips..part bones and the bones between them
    Quaternion[] snapRot;
    Vector3 snapHipsPos;
    int hookFrame = -1;
    public int LufFrame = -100, ExpressionFrame = -1;
    bool orderLogged;

    // Read the target, write physics; if Expression already ran this frame, run it again on the new pose.
    public void RunHookAndExpression()
    {
        if (!built || hookFrame == Time.frameCount) return;
        bool exprAlready = ExpressionFrame == Time.frameCount;
        if (!orderLogged)
        {
            orderLogged = true;
            RagdollLab.Log.LogInfo(Name + ": hook order: Expression " + (exprAlready ? "before" : "after")
                + " LateUpdateForce; hover(cf_N_height)=" + (hipsT != null && hipsT.parent != null ? hipsT.parent.localPosition.ToString("F2") : "?"));
        }
        OnLateUpdateHook();
        if (exprAlready) RagdollLab.RunExpression(expression);
    }
    bool releasing;
    float releaseT;
    float appliedMuscle = -1f, appliedZeta = -1f;
    public int State;           // 0 standing (normal), 1 down, 2 getting up
    float stateT, settleT;
    float pinScale = 1f, muscleScale = 1f;
    float poseBlend = 1f;       // 0 = synthetic crouch target, 1 = Studio pose target
    RagdollLab labRef;
    Part pPelvis, pChest, pHead, pFootL, pFootR;
    readonly List<Collider[]> legPairs = new List<Collider[]>();
    bool appliedLegCollision;
    // get-up "rise" phase: feet held where they lie, pelvis lifted over them, chest/head kept above
    bool riseStarted;
    float riseW, riseHeight, crouchH, standH, chestOff, headOff, riseBaseY;
    Vector3 riseXZ, footAnchorL, footAnchorR;

    static float CrouchAngle(string key)
    {
        if (key.StartsWith("abdomen")) return 25f;
        if (key.StartsWith("chest")) return 15f;
        if (key.StartsWith("head")) return 10f;
        if (key.StartsWith("upperarm")) return 70f;
        if (key.StartsWith("forearm")) return 70f;
        if (key.StartsWith("hand")) return 10f;
        if (key.StartsWith("thigh")) return 100f;
        if (key.StartsWith("shin")) return 120f;
        if (key.StartsWith("foot")) return 15f;
        return 0f;
    }

    static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

    public string StateName { get { return State == 3 ? "DEAD" : (State == 1 ? "DOWN" : (State == 2 ? "getting up" : "up")); } }

    public void KnockDown(string why)
    {
        if (!built || releasing || State == 1 || State == 3) return;
        State = 1; stateT = 0f; settleT = 0f;
        pinScale = 0f; poseBlend = 1f; riseStarted = false; riseW = 0f;
        muscleScale = labRef != null ? labRef.downMuscle : 0.15f;
        RagdollLab.Log.LogInfo(Name + " knocked down (" + why + ")");
    }

    public void UpdateBehaviour(float dt, RagdollLab lab)
    {
        labRef = lab;
        if (!built || parts.Count == 0) return;
        if (settleUntil > Time.time) Settle(dt);
        if (appliedLegCollision != lab.legSelfCollision)
        {
            appliedLegCollision = lab.legSelfCollision;
            foreach (Collider[] pr in legPairs) if (pr[0] != null && pr[1] != null) Physics.IgnoreCollision(pr[0], pr[1], !appliedLegCollision);
        }
        if (releasing) return;
        if (UpdateCombat(dt, lab)) return;
        Part pelvis = parts[0];
        if (State == 0)
        {
            pinScale = 1f; muscleScale = 1f;
            if (lab.knockDistance > 0f && lab.pinWeight > 0f && pelvis.hasTarget
                && (pelvis.tgtCom - pelvis.rb.worldCenterOfMass).magnitude > lab.knockDistance)
                KnockDown("pelvis off target");
        }
        else if (State == 1)
        {
            stateT += dt;
            muscleScale = lab.downMuscle;
            settleT = pelvis.rb.velocity.magnitude < 3f ? settleT + dt : 0f;
            if (stateT >= lab.minDownTime && settleT >= 0.4f && lab.pinWeight > 0f) BeginGetUp(lab);
        }
        else
        {
            // 0-0.3 gather into a crouch | 0.3-0.75 rise over the feet | 0.5-0.8 crouch -> Studio pose | 0.8-1 normal pins
            stateT += dt;
            float u = stateT / Mathf.Max(0.2f, lab.getUpTime);
            muscleScale = Mathf.Lerp(lab.downMuscle, 1f, Smooth(u / 0.25f));
            poseBlend = Smooth((u - 0.5f) / 0.3f);
            if (!riseStarted && u >= 0.3f) StartRise(lab);
            if (riseStarted)
            {
                riseHeight = Mathf.Lerp(crouchH, standH, Smooth((u - 0.3f) / 0.45f));
                riseW = 1f - Smooth((u - 0.8f) / 0.2f);
            }
            pinScale = Smooth((u - 0.8f) / 0.2f);
            if (u >= 1f)
            {
                State = 0; pinScale = 1f; muscleScale = 1f; poseBlend = 1f; riseStarted = false; riseW = 0f;
                LogErrors("get-up done", 0f);
                ReseatTwisted(45f, "after get-up");
            }
        }
        if (State == 0)
        {
            diagT += dt;
            if (diagT >= 2f) { diagT = 0f; LogErrors("standing", 35f); }
        }
    }

    float diagT;

    float AngleError(Part p)
    {
        return p.hasTarget ? Quaternion.Angle(p.tr.rotation, p.tgtProxyRot) : 0f;
    }

    // Diagnostic: how far each body is from its target orientation (and how far the ankle is from its target joint rotation).
    void LogErrors(string when, float minDeg)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        bool any = false;
        foreach (Part p in parts)
        {
            float e = AngleError(p);
            if (e < minDeg) continue;
            any = true;
            string rel = p.parent != null ? "/joint " + JointError(p).ToString("0") : "";
            sb.Append(p.key).Append(' ').Append(e.ToString("0")).Append(rel).Append("  ");
        }
        if (minDeg <= 0f || any) RagdollLab.Log.LogInfo(Name + " [" + when + "] deg off target: " + sb);
    }

    // Put bodies that are far from their target orientation (e.g. a joint stuck on the wrong side of a limit)
    // back onto the target, keeping each one attached at its parent's joint anchor. Descendants follow.
    float JointError(Part p)
    {
        if (p.parent == null || !p.hasTarget || !p.parent.hasTarget) return 0f;
        Quaternion cur = Quaternion.Inverse(p.parent.tr.rotation) * p.tr.rotation;
        Quaternion tgt = Quaternion.Inverse(p.parent.tgtProxyRot) * p.tgtProxyRot;
        return Quaternion.Angle(cur, tgt);
    }

    // Re-seat joints that are far from their target joint rotation (e.g. a knee that flipped to the wrong
    // side of its limit): the body is put at its target rotation *relative to its parent*, at the parent's
    // joint anchor, and everything below it follows. The pelvis is re-oriented if it is itself far off.
    public void ReseatTwisted(float thresholdDeg, string why)
    {
        if (!built) return;
        HashSet<Part> moved = new HashSet<Part>();
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        foreach (Part p in parts)
        {
            if (!p.hasTarget) continue;
            if (p.joint == null)
            {
                float re = AngleError(p);
                if (re < thresholdDeg) continue;
                p.tr.rotation = p.tgtProxyRot;
                p.rb.rotation = p.tgtProxyRot;
                p.rb.angularVelocity = Vector3.zero;
                moved.Add(p);
                sb.Append(p.key).Append('(').Append(re.ToString("0")).Append(") ");
                continue;
            }
            bool parentMoved = moved.Contains(p.parent);
            float e = JointError(p);
            if (!parentMoved && e < thresholdDeg) continue;
            Quaternion rot = p.parent.tr.rotation * (Quaternion.Inverse(p.parent.tgtProxyRot) * p.tgtProxyRot);
            Vector3 pos = p.parent.tr.TransformPoint(p.joint.connectedAnchor);
            p.tr.SetPositionAndRotation(pos, rot);
            p.rb.position = pos;
            p.rb.rotation = rot;
            p.rb.angularVelocity = Vector3.zero;
            p.rb.velocity = p.parent.rb.velocity;
            moved.Add(p);
            if (!parentMoved) sb.Append(p.key).Append('(').Append(e.ToString("0")).Append(") ");
        }
        if (moved.Count > 0) RagdollLab.Log.LogInfo(Name + " reseated [" + why + "]: " + sb + "+" + moved.Count + " bodies total");
    }

    // Yaw-only PD toward the target heading, so the body turns to face the (relocated) Studio pose while
    // the feet are held on the floor, instead of twisting the legs at the end of the get-up.
    void YawAlign(Part p, float w)
    {
        Vector3 cf = p.tr.forward; cf.y = 0f;
        Vector3 tf = p.tgtProxyRot * Vector3.forward; tf.y = 0f;
        if (cf.sqrMagnitude < 0.04f || tf.sqrMagnitude < 0.04f) return;
        float ang = Vector3.SignedAngle(cf, tf, Vector3.up) * Mathf.Deg2Rad;
        p.rb.AddTorque(Vector3.up * (w * w * ang - 2f * w * p.rb.angularVelocity.y), ForceMode.Acceleration);
    }

    void StartRise(RagdollLab lab)
    {
        riseStarted = true;
        riseW = 1f;
        footAnchorL = pFootL.rb.worldCenterOfMass;
        footAnchorR = pFootR.rb.worldCenterOfMass;
        Vector3 feetMid = (footAnchorL + footAnchorR) * 0.5f;
        riseXZ = feetMid;
        riseBaseY = feetMid.y;
        Part pelvis = pPelvis;
        if (pelvis.hasTarget && pFootL.hasTarget && pFootR.hasTarget)
        {
            standH = pelvis.tgtCom.y - (pFootL.tgtCom.y + pFootR.tgtCom.y) * 0.5f;
            chestOff = (pChest.tgtCom - pelvis.tgtCom).magnitude;
            headOff = (pHead.tgtCom - pelvis.tgtCom).magnitude;
        }
        else { standH = 8f; chestOff = 3f; headOff = 5f; }
        crouchH = standH * 0.45f;

        if (lab.relocateOnGetUp && oci != null && oci.guideObject != null && oci.objectInfo != null
            && oci.guideObject.transformTarget != null && pelvis.hasTarget)
        {
            // face the way the body lies, then put the Studio pose's pelvis over the feet
            Vector3 bf = pelvis.tr.forward; bf.y = 0f;
            if (bf.sqrMagnitude < 0.25f)
            {
                Vector3 spine = pelvis.tr.up; spine.y = 0f;
                bf = pelvis.tr.forward.y > 0f ? -spine : spine;   // supine: sit up toward the feet
            }
            Camera viewer = RagdollLab.StudioCamera();
            if (lab.faceCameraOnGetUp && viewer != null)
            {
                Vector3 toCam = viewer.transform.position - feetMid; toCam.y = 0f;
                if (toCam.sqrMagnitude > 1f) bf = toCam;            // zombies get up facing the player
            }
            Vector3 tf = pelvis.tgtProxyRot * Vector3.forward; tf.y = 0f;
            float yaw = (tf.sqrMagnitude > 1e-4f && bf.sqrMagnitude > 1e-4f) ? Vector3.SignedAngle(tf, bf, Vector3.up) : 0f;
            Transform tt = oci.guideObject.transformTarget;
            Vector3 root = tt.position;
            Vector3 newTgt = root + Quaternion.AngleAxis(yaw, Vector3.up) * (pelvis.tgtCom - root);
            Vector3 delta = feetMid - newTgt;
            delta.y = 0f;
            Vector3 local = tt.parent != null ? tt.parent.InverseTransformVector(delta) : delta;
            Vector3 rot = oci.objectInfo.changeAmount.rot;
            rot.y += yaw;
            oci.objectInfo.changeAmount.rot = rot;
            oci.objectInfo.changeAmount.pos = oci.objectInfo.changeAmount.pos + local;
            foreach (Part p in parts) p.hasTarget = false;   // target jumps: no velocity spike
            RagdollLab.Log.LogInfo(Name + " relocated by " + delta.ToString("F1") + ", yaw " + yaw.ToString("F0"));
        }
    }

    void RisePin(Part p, Vector3 target, float w, float wp)
    {
        Vector3 d = target - p.rb.worldCenterOfMass;
        p.rb.AddForce(w * (wp * wp * d - 2f * wp * p.rb.velocity), ForceMode.Acceleration);
    }

    void BeginGetUp(RagdollLab lab)
    {
        State = 2; stateT = 0f;
        poseBlend = 0f;
        riseStarted = false; riseW = 0f;
        foreach (Part p in parts) if (p.rb != null) p.rb.WakeUp();
        RagdollLab.Log.LogInfo(Name + " getting up");
    }

    readonly ChaControl cha;
    readonly OCIChar oci;
    readonly List<Part> parts = new List<Part>();
    readonly Dictionary<Rigidbody, Part> byBody = new Dictionary<Rigidbody, Part>();
    readonly HashSet<int> ignoredPairs = new HashSet<int>();
    GameObject container;
    bool built;
    bool savedAnim, savedFK, savedIK, savedNeck, tookControl;
    CharaUtils.Expression expression;
    public float Mass;
    public string Name;
    float appliedFriction = -1f;

    public Ragdoll(ChaControl cha, OCIChar oci)
    {
        this.cha = cha;
        this.oci = oci;
        Name = cha != null ? cha.name : "?";
    }

    public bool Alive { get { return cha != null && cha.objBodyBone != null; } }
    public bool Owns(ChaControl c) { return c == cha; }
    public bool HasBody(Rigidbody rb) { return byBody.ContainsKey(rb); }
    public string KeyOf(Rigidbody rb) { Part p; return byBody.TryGetValue(rb, out p) ? p.key : null; }

    public Rigidbody BodyOf(string key)
    {
        foreach (Part p in parts) if (p.key == key && p.rb != null) return p.rb;
        return null;
    }

    // the world pose the part's bone gets from its body (as ApplyPose writes it)
    public bool BonePose(string key, out Vector3 pos, out Quaternion rot)
    {
        foreach (Part p in parts)
            if (p.key == key && p.tr != null) { pos = p.tr.position; rot = p.tr.rotation * p.offset; return true; }
        pos = Vector3.zero; rot = Quaternion.identity;
        return false;
    }
    public bool ExpressionIs(CharaUtils.Expression e) { return expression != null && e == expression; }

    static void Index(Transform t, Dictionary<string, Transform> map)
    {
        if (!map.ContainsKey(t.name)) map[t.name] = t;
        for (int i = 0; i < t.childCount; i++) Index(t.GetChild(i), map);
    }

    static Vector3 Ortho(Vector3 v, Vector3 n)
    {
        return (v - Vector3.Dot(v, n) * n).normalized;
    }

    public bool Build(RagdollLab lab)
    {
        ManualLogSource log = RagdollLab.Log;
        Transform root = cha.objBodyBone.transform;
        Dictionary<string, Transform> map = new Dictionary<string, Transform>();
        Index(root, map);

        string[] needed = {
            "cf_J_Root", "cf_J_Hips", "cf_J_Spine01", "cf_J_Spine02", "cf_J_Spine03", "cf_J_Neck", "cf_J_Head",
            "cf_J_ArmUp00_L", "cf_J_ArmLow01_L", "cf_J_Hand_L", "cf_J_ArmUp00_R", "cf_J_ArmLow01_R", "cf_J_Hand_R",
            "cf_J_LegUp00_L", "cf_J_LegLow01_L", "cf_J_Foot01_L", "cf_J_LegUp00_R", "cf_J_LegLow01_R", "cf_J_Foot01_R" };
        foreach (string n in needed)
        {
            if (!map.ContainsKey(n)) { log.LogError("Missing bone " + n + " on " + Name); return false; }
        }
        Transform toesL = map.ContainsKey("cf_J_Toes01_L") ? map["cf_J_Toes01_L"] : (map.ContainsKey("cf_J_Foot02_L") ? map["cf_J_Foot02_L"] : null);
        Transform toesR = map.ContainsKey("cf_J_Toes01_R") ? map["cf_J_Toes01_R"] : (map.ContainsKey("cf_J_Foot02_R") ? map["cf_J_Foot02_R"] : null);
        if (toesL == null || toesR == null) { log.LogError("Missing toe bones on " + Name); return false; }

        // ---- put the live skeleton into its bind pose (from the p_cf_anim prefab), remember the live pose
        Transform hips = map["cf_J_Hips"];
        hipsT = hips;
        Transform[] all = hips.GetComponentsInChildren<Transform>(true);
        Quaternion[] savedRot = new Quaternion[all.Length];
        for (int i = 0; i < all.Length; i++) savedRot[i] = all[i].localRotation;
        Vector3 savedHipsPos = hips.localPosition;

        GameObject prefab = null;
        try { prefab = CommonLib.LoadAsset<GameObject>("studio/base/00.unity3d", "p_cf_anim", false, "abdata"); }
        catch (Exception e) { log.LogWarning("Prefab load threw: " + e.Message); }
        int matched = 0;
        if (prefab != null)
        {
            Dictionary<string, Transform> pmap = new Dictionary<string, Transform>();
            Index(prefab.transform, pmap);
            for (int i = 0; i < all.Length; i++)
            {
                Transform p;
                if (pmap.TryGetValue(all[i].name, out p)) { all[i].localRotation = p.localRotation; matched++; }
            }
            Transform ph;
            if (pmap.TryGetValue("cf_J_Hips", out ph)) hips.localPosition = ph.localPosition;
        }
        else log.LogWarning("p_cf_anim prefab not found; using the current pose as joint reference.");
        log.LogInfo("Bind pose: matched " + matched + "/" + all.Length + " transforms under hips.");

        // ---- character axes in bind pose
        Vector3 U = (map["cf_J_Neck"].position - hips.position).normalized;
        Vector3 toeMid = (toesL.position + toesR.position) * 0.5f;
        Vector3 ankMid = (map["cf_J_Foot01_L"].position + map["cf_J_Foot01_R"].position) * 0.5f;
        Vector3 F = Ortho(toeMid - ankMid, U);
        Vector3 R = Vector3.Cross(U, F);
        float thighLen = Vector3.Distance(map["cf_J_LegUp00_L"].position, map["cf_J_LegLow01_L"].position);
        float s = thighLen / 4.2f;
        log.LogInfo("Axes U=" + U.ToString("F3") + " F=" + F.ToString("F3") + " R=" + R.ToString("F3") + " scale s=" + s.ToString("F3")
            + " ArmUp00_L side=" + Vector3.Dot(map["cf_J_ArmUp00_L"].position - hips.position, R).ToString("F2"));

        container = lab.NewLabObject("RagdollLab_" + Name);

        // ---- parts (bone, segment direction, reference for local Z, mass fraction)
        Func<string, Vector3> P = n => map[n].position;
        Part pelvis = MakePart("pelvis", hips, U, F, 0.14f, null);
        pelvis.isRoot = true;
        Part abdomen = MakePart("abdomen", map["cf_J_Spine01"], (P("cf_J_Spine02") - P("cf_J_Spine01")).normalized, F, 0.12f, pelvis);
        Part chest = MakePart("chest", map["cf_J_Spine02"], (P("cf_J_Neck") - P("cf_J_Spine02")).normalized, F, 0.20f, abdomen);
        Part head = MakePart("head", map["cf_J_Neck"], (P("cf_J_Head") - P("cf_J_Neck")).normalized, F, 0.08f, chest);

        // pelvis box: from below the hip joints to just above cf_J_Hips
        Vector3 legMid = (P("cf_J_LegUp00_L") + P("cf_J_LegUp00_R")) * 0.5f;
        float hipW = Vector3.Distance(P("cf_J_LegUp00_L"), P("cf_J_LegUp00_R"));
        float bottom = Vector3.Dot(legMid - hips.position, U) - 0.9f * s;
        float top = 0.5f * s;
        AddBox(pelvis, new Vector3(hipW + 1.4f * s, top - bottom, 2.3f * s), new Vector3(0, (top + bottom) * 0.5f, -0.1f * s), lab);

        float abLen = Vector3.Distance(P("cf_J_Spine01"), P("cf_J_Spine02"));
        AddBox(abdomen, new Vector3(2.4f * s, abLen, 1.9f * s), new Vector3(0, abLen * 0.5f, 0), lab);

        float chLen = Vector3.Distance(P("cf_J_Spine02"), P("cf_J_Neck"));
        float shoulderW = Vector3.Distance(P("cf_J_ArmUp00_L"), P("cf_J_ArmUp00_R"));
        AddBox(chest, new Vector3(Mathf.Max(shoulderW * 0.95f, 2.6f * s), chLen, 2.2f * s), new Vector3(0, chLen * 0.5f, 0.1f * s), lab);

        Vector3 headC = P("cf_J_Head") + U * 0.8f * s;
        AddSphere(head, head.tr.InverseTransformPoint(headC), 1.0f * s, lab);

        Part[] upper = new Part[2], fore = new Part[2], hand = new Part[2], thigh = new Part[2], shin = new Part[2], foot = new Part[2];
        string[] side = { "L", "R" };
        Transform[] toes = { toesL, toesR };
        for (int k = 0; k < 2; k++)
        {
            string sd = side[k];
            Vector3 a0 = P("cf_J_ArmUp00_" + sd), a1 = P("cf_J_ArmLow01_" + sd), a2 = P("cf_J_Hand_" + sd);
            upper[k] = MakePart("upperarm_" + sd, map["cf_J_ArmUp00_" + sd], (a1 - a0).normalized, F, 0.03f, chest);
            AddCapsule(upper[k], Vector3.Distance(a0, a1), 0.42f * s, lab);
            fore[k] = MakePart("forearm_" + sd, map["cf_J_ArmLow01_" + sd], (a2 - a1).normalized, F, 0.018f, upper[k]);
            AddCapsule(fore[k], Vector3.Distance(a1, a2), 0.33f * s, lab);
            hand[k] = MakePart("hand_" + sd, map["cf_J_Hand_" + sd], (a2 - a1).normalized, F, 0.007f, fore[k]);
            AddBox(hand[k], new Vector3(0.32f * s, 1.0f * s, 0.85f * s), new Vector3(0, 0.5f * s, 0), lab);

            Vector3 l0 = P("cf_J_LegUp00_" + sd), l1 = P("cf_J_LegLow01_" + sd), l2 = P("cf_J_Foot01_" + sd);
            thigh[k] = MakePart("thigh_" + sd, map["cf_J_LegUp00_" + sd], (l1 - l0).normalized, F, 0.10f, pelvis);
            AddCapsule(thigh[k], Vector3.Distance(l0, l1), 0.68f * s, lab);
            shin[k] = MakePart("shin_" + sd, map["cf_J_LegLow01_" + sd], (l2 - l1).normalized, F, 0.045f, thigh[k]);
            AddCapsule(shin[k], Vector3.Distance(l1, l2), 0.45f * s, lab);

            Vector3 toeVec = toes[k].position - l2;
            Vector3 footDir = Ortho(toeVec, U);
            float toeLen = Vector3.Dot(toeVec, footDir);
            // Not "ankle minus cf_J_Root": the bind pose uses the prefab's hips height, which is too high for
            // shorter legs, and the box then reached ~1.4 units below the sole and dug into the floor.
            float ankleH = Vector3.Dot(l2 - toes[k].position, U) * (0.96f / 0.65f);
            foot[k] = MakePart("foot_" + sd, map["cf_J_Foot01_" + sd], footDir, U, 0.015f, shin[k]);
            AddBox(foot[k], new Vector3(0.8f * s, toeLen + 0.7f * s, ankleH + 0.2f * s),
                new Vector3(0, toeLen * 0.5f, (-ankleH + 0.2f * s) * 0.5f), lab);
        }

        // ---- rigidbody settings
        Mass = 0f;
        foreach (Part p in parts)
        {
            p.rb.mass = Mathf.Max(0.05f, p.massFrac * lab.totalMass);
            Mass += p.rb.mass;
            p.rb.useGravity = false;
            p.rb.drag = 0.05f;
            p.rb.angularDrag = lab.jointDamping;
            p.rb.maxAngularVelocity = 50f;
            p.rb.maxDepenetrationVelocity = 50f;
            p.rb.sleepThreshold = 0.1f;   // mass-normalised energy; 1 unit = 10 cm
            p.rb.solverIterations = lab.solverIterations;
            p.rb.solverVelocityIterations = lab.velocityIterations;
            p.rb.interpolation = RigidbodyInterpolation.None;
            p.rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            byBody[p.rb] = p;
        }

        // ---- joints, created in bind pose
        Joint(abdomen, R, F, U, F, 30, 25, 20, 20, lab);
        Joint(chest, R, F, U, F, 25, 20, 15, 20, lab);
        Joint(head, R, F, U, F, 40, 40, 30, 50, lab);
        for (int k = 0; k < 2; k++)
        {
            Vector3 armDir = (fore[k].tr.position - upper[k].tr.position).normalized;
            Vector3 legDir = (shin[k].tr.position - thigh[k].tr.position).normalized;
            Vector3 footDir = foot[k].tr.up;
            Joint(upper[k], F, U, armDir, -U, 100, 75, 90, 60, lab);
            Joint(fore[k], U, F, armDir, F, 140, 5, 5, 30, lab);
            Joint(hand[k], F, U, armDir, -U, 70, 60, 25, 10, lab);
            Joint(thigh[k], R, F, legDir, F, 100, 20, 40, 30, lab);
            Joint(shin[k], R, F, legDir, -F, 140, 2, 3, 5, lab);
            Joint(foot[k], R, U, footDir, U, 30, 50, 25, 25, lab);
        }

        // ---- inertia of each joint's subtree about its anchor (bind pose), used to scale muscles
        foreach (Part p in parts)
        {
            if (p.joint == null) continue;
            float I = 0f;
            foreach (Part q in parts)
            {
                Part a = q;
                while (a != null && a != p) a = a.parent;
                if (a == null) continue;
                Vector3 d = q.rb.worldCenterOfMass - p.tr.position;
                I += q.rb.inertiaTensor.magnitude + q.rb.mass * d.sqrMagnitude;
            }
            p.subInertia = I;
        }

        // ---- self-collision filtering: ignore pairs overlapping in bind pose
        int ignoredBind = IgnoreOverlaps();

        // ---- restore the live pose and teleport proxies onto it
        for (int i = 0; i < all.Length; i++) all[i].localRotation = savedRot[i];
        hips.localPosition = savedHipsPos;
        foreach (Part p in parts)
        {
            p.tr.SetPositionAndRotation(p.bone.position, p.bone.rotation * Quaternion.Inverse(p.offset));
            p.rb.position = p.tr.position;
            p.rb.rotation = p.tr.rotation;
            p.rb.velocity = Vector3.zero;
            p.rb.angularVelocity = Vector3.zero;
        }
        int ignoredLive = IgnoreOverlaps();

        pPelvis = pelvis; pChest = chest; pHead = head; pFootL = foot[0]; pFootR = foot[1];
        foreach (Part p in parts)
        {
            if (p.joint == null) continue;
            p.crouchLocal = Quaternion.AngleAxis(CrouchAngle(p.key), p.flexAxisP) * p.startLocal;
        }
        Part[] legL = { thigh[0], shin[0], foot[0] }, legR = { thigh[1], shin[1], foot[1] };
        foreach (Part a in legL) foreach (Part b in legR) legPairs.Add(new Collider[] { a.col, b.col });
        appliedLegCollision = !lab.legSelfCollision;   // force first application

        // chain of transforms that define the part bones' world pose (for the snapshot target)
        HashSet<Transform> chainSet = new HashSet<Transform>();
        foreach (Part p in parts)
        {
            Transform t = p.bone;
            while (t != null) { chainSet.Add(t); if (t == hips) break; t = t.parent; }
        }
        chain = new Transform[chainSet.Count];
        chainSet.CopyTo(chain);
        snapRot = new Quaternion[chain.Length];
        for (int i = 0; i < chain.Length; i++) snapRot[i] = chain[i].localRotation;
        snapHipsPos = hips.localPosition;
        log.LogInfo("Ragdoll " + Name + ": " + parts.Count + " bodies, mass " + Mass.ToString("F1")
            + ", ignored overlaps bind=" + ignoredBind + " live=" + ignoredLive);

        TakeControl();
        expression = cha.expression;
        built = true;
        InitCombat(lab);
        return true;
    }

    Part MakePart(string key, Transform bone, Vector3 segDir, Vector3 zRef, float massFrac, Part parent)
    {
        Part p = new Part();
        p.key = key;
        p.bone = bone;
        p.parent = parent;
        p.massFrac = massFrac;
        GameObject go = new GameObject("RL_" + key);
        go.layer = RagdollLab.PartLayer;
        go.transform.SetParent(container.transform, false);
        Vector3 z = zRef - Vector3.Dot(zRef, segDir) * segDir;
        if (z.sqrMagnitude < 1e-4f) z = Vector3.Cross(segDir, Vector3.right);
        go.transform.SetPositionAndRotation(bone.position, Quaternion.LookRotation(z.normalized, segDir));
        p.tr = go.transform;
        p.offset = Quaternion.Inverse(go.transform.rotation) * bone.rotation;
        p.rb = go.AddComponent<Rigidbody>();
        parts.Add(p);
        return p;
    }

    void AddCapsule(Part p, float len, float r, RagdollLab lab)
    {
        CapsuleCollider c = p.tr.gameObject.AddComponent<CapsuleCollider>();
        c.direction = 1;
        c.radius = r;
        c.height = Mathf.Max(len, 2f * r);
        c.center = new Vector3(0, len * 0.5f, 0);
        c.sharedMaterial = lab.material;
        p.col = c;
    }

    void AddBox(Part p, Vector3 size, Vector3 center, RagdollLab lab)
    {
        BoxCollider c = p.tr.gameObject.AddComponent<BoxCollider>();
        c.size = size;
        c.center = center;
        c.sharedMaterial = lab.material;
        p.col = c;
    }

    void AddSphere(Part p, Vector3 center, float r, RagdollLab lab)
    {
        SphereCollider c = p.tr.gameObject.AddComponent<SphereCollider>();
        c.radius = r;
        c.center = center;
        c.sharedMaterial = lab.material;
        p.col = c;
    }

    // primary: hinge-like axis candidate; childDir/flexDir decide its sign so that flexion is a
    // negative joint angle (PhysX/Unity joint convention, cf. Unity RagdollBuilder knee -80..0).
    void Joint(Part child, Vector3 primary, Vector3 secondary, Vector3 childDir, Vector3 flexDir,
        float maxFlex, float maxExt, float yLim, float zLim, RagdollLab lab)
    {
        Vector3 moved = Quaternion.AngleAxis(10f, primary) * childDir - childDir;
        Vector3 axis = Vector3.Dot(moved, flexDir) >= 0f ? primary : -primary;
        Vector3 sec = Ortho(secondary, axis);

        ConfigurableJoint j = child.tr.gameObject.AddComponent<ConfigurableJoint>();
        j.autoConfigureConnectedAnchor = false;
        j.connectedBody = child.parent.rb;
        j.anchor = Vector3.zero;
        j.connectedAnchor = child.parent.tr.InverseTransformPoint(child.tr.position);
        j.axis = child.tr.InverseTransformDirection(axis);
        j.secondaryAxis = child.tr.InverseTransformDirection(sec);
        j.xMotion = ConfigurableJointMotion.Locked;
        j.yMotion = ConfigurableJointMotion.Locked;
        j.zMotion = ConfigurableJointMotion.Locked;
        j.angularXMotion = ConfigurableJointMotion.Limited;
        j.angularYMotion = ConfigurableJointMotion.Limited;
        j.angularZMotion = ConfigurableJointMotion.Limited;
        float lo = -maxFlex, hi = maxExt;
        if (lab.flipHingeSign) { lo = -maxExt; hi = maxFlex; }
        SoftJointLimit l = new SoftJointLimit();
        l.limit = lo; j.lowAngularXLimit = l;
        l.limit = hi; j.highAngularXLimit = l;
        l.limit = yLim; j.angularYLimit = l;
        l.limit = zLim; j.angularZLimit = l;
        j.projectionMode = JointProjectionMode.PositionAndRotation;
        j.projectionDistance = 0.05f;
        j.projectionAngle = 5f;
        j.enablePreprocessing = false;
        j.enableCollision = false;
        j.rotationDriveMode = RotationDriveMode.Slerp;
        child.joint = j;
        child.startLocal = Quaternion.Inverse(child.parent.tr.rotation) * child.tr.rotation;
        child.flexAxisP = Quaternion.Inverse(child.parent.tr.rotation) * axis;
        Vector3 arm = child.rb.worldCenterOfMass - child.tr.position;
        child.pivotInertia = child.rb.inertiaTensor.magnitude + child.rb.mass * arm.sqrMagnitude;
    }

    int IgnoreOverlaps()
    {
        int n = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            for (int k = i + 1; k < parts.Count; k++)
            {
                Part a = parts[i], b = parts[k];
                if (a.parent == b || b.parent == a) continue;
                Vector3 dir; float dist;
                Transform ta = a.col.transform, tb = b.col.transform;
                if (Physics.ComputePenetration(a.col, ta.position, ta.rotation, b.col, tb.position, tb.rotation, out dir, out dist))
                {
                    if (ignoredPairs.Add(i * 64 + k))
                    {
                        Physics.IgnoreCollision(a.col, b.col, true);
                        RagdollLab.Log.LogDebug("ignore " + a.key + " <-> " + b.key);
                        n++;
                    }
                }
            }
        }
        return n;
    }

    void TakeControl()
    {
        savedAnim = cha.animBody != null && cha.animBody.enabled;
        if (oci != null && oci.oiCharInfo != null)
        {
            savedFK = oci.oiCharInfo.enableFK;
            savedIK = oci.oiCharInfo.enableIK;
            fk = oci.fkCtrl;
        }
        // Live: Studio keeps producing the pose every frame (Animator, FK run by us) and we read it.
        // Snapshot: IK cannot be re-run safely in our hook, so freeze the activation pose instead.
        snapshotMode = savedIK || !savedAnim;
        if (snapshotMode)
        {
            if (cha.animBody != null) cha.animBody.enabled = false;
            if (oci != null && oci.oiCharInfo != null)
            {
                oci.ActiveKinematicMode(OICharInfo.KinematicMode.FK, false, true);
                oci.ActiveKinematicMode(OICharInfo.KinematicMode.IK, false, true);
            }
        }
        liveFK = !snapshotMode && savedFK && fk != null;
        if (cha.neckLookCtrl != null) { savedNeck = cha.neckLookCtrl.enabled; cha.neckLookCtrl.enabled = false; }
        tookControl = true;
        RagdollLab.Log.LogInfo(Name + ": target mode " + ModeName.Replace("+FK", "") + (liveFK ? " (+FK)" : "") + ", anim=" + savedAnim + " FK=" + savedFK + " IK=" + savedIK);
    }

    public string ModeName { get { return (snapshotMode ? "Snapshot" : "Live") + (liveFK ? "+FK" : ""); } }

    public bool BlocksFK(FKCtrl f) { return built && liveFK && f == fk; }

    // Once per frame, inside LateUpdate before Expression: produce Studio's pose, read it, write physics.
    public void OnLateUpdateHook()
    {
        if (!built || hookFrame == Time.frameCount) return;
        hookFrame = Time.frameCount;
        if (snapshotMode)
        {
            for (int i = 0; i < chain.Length; i++) if (chain[i] != null) chain[i].localRotation = snapRot[i];
            if (hipsT != null) hipsT.localPosition = snapHipsPos;
        }
        else if (liveFK && fk != null && fk.enabled)
        {
            RagdollLab.RunFK(fk);
        }
        float dt = Time.deltaTime > 1e-5f ? Time.deltaTime : 1f / 60f;
        foreach (Part p in parts)
        {
            if (p.bone == null || p.rb == null) continue;
            p.tgtPos = p.bone.position;
            p.tgtBoneRot = p.bone.rotation;
            p.tgtProxyRot = p.tgtBoneRot * Quaternion.Inverse(p.offset);
            Vector3 com = p.tgtPos + p.tgtProxyRot * p.rb.centerOfMass;
            p.tgtVel = p.hasTarget ? Vector3.ClampMagnitude((com - p.tgtCom) / dt, 300f) : Vector3.zero;
            p.tgtCom = com;
            p.hasTarget = true;
        }
        ApplyPose();
    }

    static void SetTargetLocal(ConfigurableJoint j, Quaternion targetLocal, Quaternion startLocal)
    {
        // joint-space conversion as in the well-known ConfigurableJoint extension (mstevenson gist)
        Vector3 right = j.axis;
        Vector3 forward = Vector3.Cross(j.axis, j.secondaryAxis).normalized;
        Vector3 up = Vector3.Cross(forward, right).normalized;
        Quaternion w2j = Quaternion.LookRotation(forward, up);
        Quaternion r = Quaternion.Inverse(w2j);
        r *= Quaternion.Inverse(targetLocal) * startLocal;
        r *= w2j;
        j.targetRotation = r;
    }

    public void UpdateDrives(RagdollLab lab)
    {
        if (!built) return;
        bool driveChanged = appliedFriction != lab.jointFriction || appliedMuscle != lab.muscleFreq
            || appliedZeta != lab.muscleDampingRatio || (releasing && appliedMuscle != 0f);
        float w = releasing ? 0f : lab.muscleFreq * muscleScale;
        driveChanged |= appliedMuscle != w;
        appliedFriction = lab.jointFriction; appliedMuscle = w; appliedZeta = lab.muscleDampingRatio;
        foreach (Part p in parts)
        {
            if (p.joint == null) continue;
            if (p.hold)
            {
                float wh = releasing ? 0f : lab.muscleFreq * 2f;
                if (Mathf.Abs(wh - p.appliedW) > 0.05f)
                {
                    p.appliedW = wh;
                    JointDrive dh = new JointDrive();
                    dh.positionSpring = wh * wh * p.subInertia;
                    dh.positionDamper = lab.jointFriction * p.pivotInertia + 2f * lab.muscleDampingRatio * wh * p.subInertia;
                    dh.maximumForce = float.MaxValue;
                    p.joint.slerpDrive = dh;
                }
                SetTargetLocal(p.joint, p.holdLocal, p.startLocal);
                continue;
            }
            float wj = w * (1f - lab.weakenMuscle * p.weak);
            if (driveChanged || Mathf.Abs(wj - p.appliedW) > 0.05f)
            {
                p.appliedW = wj;
                JointDrive d = new JointDrive();
                d.positionSpring = wj * wj * p.subInertia;
                d.positionDamper = lab.jointFriction * p.pivotInertia + 2f * lab.muscleDampingRatio * wj * p.subInertia;
                d.maximumForce = float.MaxValue;
                p.joint.slerpDrive = d;
            }
            if (w <= 0f) continue;
            bool hasAnim = p.hasTarget && p.parent.hasTarget;
            Quaternion local;
            if (poseBlend >= 1f) { if (!hasAnim) continue; local = Quaternion.Inverse(p.parent.tgtProxyRot) * p.tgtProxyRot; }
            else if (poseBlend <= 0f || !hasAnim) local = p.crouchLocal;
            else local = Quaternion.Slerp(p.crouchLocal, Quaternion.Inverse(p.parent.tgtProxyRot) * p.tgtProxyRot, poseBlend);
            SetTargetLocal(p.joint, local, p.startLocal);
        }
    }

    public void SnapToTarget()
    {
        if (!built) return;
        State = 0; pinScale = 1f; muscleScale = 1f; poseBlend = 1f; riseStarted = false; riseW = 0f;
        if (HP <= 0f) HP = MaxHP;
        Stagger = 0f;
        foreach (Part p in parts)
        {
            if (!p.hasTarget) continue;
            p.tr.SetPositionAndRotation(p.tgtPos, p.tgtProxyRot);
            p.rb.position = p.tgtPos;
            p.rb.rotation = p.tgtProxyRot;
            p.rb.velocity = Vector3.zero;
            p.rb.angularVelocity = Vector3.zero;
            p.rb.WakeUp();
        }
    }

    public void BeginRelease()
    {
        if (!built || releasing) return;
        releasing = true;
        releaseT = 0f;
    }

    // true when the blend-out has finished and the doll should be released
    public bool AdvanceRelease(float dt, float blendTime)
    {
        if (!releasing) return false;
        releaseT += dt;
        return releaseT >= blendTime;
    }

    public void PreStep(Vector3 g, RagdollLab lab)
    {
        if (!built) return;
        float pw = releasing ? 0f : lab.pinWeight * pinScale;
        float wp = lab.pinFreq;
        foreach (Part p in parts)
        {
            if (p.rb == null) continue;
            p.rb.angularDrag = lab.jointDamping;
            if (p.rb.solverIterations != lab.solverIterations) p.rb.solverIterations = lab.solverIterations;
            if (p.rb.solverVelocityIterations != lab.velocityIterations) p.rb.solverVelocityIterations = lab.velocityIterations;
            bool pinned = pw > 0f && p.hasTarget && (!lab.pinHipsOnly || p.isRoot);
            if (pinned)
            {
                Vector3 d = p.tgtCom - p.rb.worldCenterOfMass;
                float k = pw * (1f - p.weak);
                if (lab.pinFalloff > 0f && State == 0) { float r = d.magnitude / lab.pinFalloff; k /= 1f + r * r; }
                Vector3 acc = k * (wp * wp * d + 2f * wp * (p.tgtVel - p.rb.velocity));
                if (lab.gravityComp) acc -= g * k;   // a pure spring sags by g/w^2; with feet on the floor that bends the knees
                p.rb.AddForce(acc, ForceMode.Acceleration);
            }
            if (pinned || !p.rb.IsSleeping()) p.rb.AddForce(g, ForceMode.Acceleration);
        }
        if (riseStarted && riseW > 0f && !releasing)
        {
            Vector3 pelvisT = new Vector3(riseXZ.x, riseBaseY + riseHeight, riseXZ.z);
            RisePin(pPelvis, pelvisT, riseW, wp);
            RisePin(pChest, pelvisT + Vector3.up * chestOff, riseW, wp);
            RisePin(pHead, pelvisT + Vector3.up * headOff, riseW * 0.5f, wp);
            RisePin(pFootL, footAnchorL, riseW, wp);
            RisePin(pFootR, footAnchorR, riseW, wp);
        }
        if (State == 2 && riseStarted && !releasing && pPelvis.hasTarget)
        {
            YawAlign(pPelvis, wp);
            YawAlign(pChest, wp);
        }
    }

    public string MotionSummary()
    {
        if (!built) return "-";
        Part fastest = null; float w = 0f; int awake = 0;
        foreach (Part p in parts)
        {
            if (p.rb == null) continue;
            if (!p.rb.IsSleeping()) awake++;
            float a = p.rb.angularVelocity.magnitude;
            if (a > w) { w = a; fastest = p; }
        }
        return ModeName + " " + StateName + (releasing ? " releasing" : "") + ", awake " + awake + "/" + parts.Count + (fastest != null ? ", fastest " + fastest.key + " " + w.ToString("0.0") + " rad/s" : "");
    }

    public float LowestPoint()
    {
        float y = float.PositiveInfinity;
        foreach (Part p in parts) if (p.col != null) y = Mathf.Min(y, p.col.bounds.min.y);
        return float.IsInfinity(y) ? cha.transform.position.y : y;
    }

    public void WakeUp()
    {
        foreach (Part p in parts) if (p.rb != null) p.rb.WakeUp();
    }

    public void ApplyPose()
    {
        if (!built) return;
        float w = 0f;
        if (releasing)
        {
            float bt = RagdollLab.Instance != null ? RagdollLab.Instance.blendOutTime : 0.3f;
            w = Mathf.Clamp01(releaseT / Mathf.Max(0.01f, bt));
            w = w * w * (3f - 2f * w);
        }
        for (int i = 0; i < parts.Count; i++)
        {
            Part p = parts[i];
            if (p.bone == null || p.tr == null) continue;
            Quaternion rot = p.tr.rotation * p.offset;
            Vector3 pos = p.tr.position;
            if (w > 0f && p.hasTarget) { rot = Quaternion.Slerp(rot, p.tgtBoneRot, w); pos = Vector3.Lerp(pos, p.tgtPos, w); }
            if (p.isRoot) p.bone.position = pos;
            p.bone.rotation = rot;
        }
    }

    public void Release(bool bakeToFK)
    {
        if (built && tookControl && cha != null)
        {
            ApplyPose();
            if (bakeToFK && oci != null && oci.fkCtrl != null)
            {
                oci.fkCtrl.CopyBone(OIBoneInfo.BoneGroup.Body | OIBoneInfo.BoneGroup.Neck
                    | OIBoneInfo.BoneGroup.RightLeg | OIBoneInfo.BoneGroup.LeftLeg
                    | OIBoneInfo.BoneGroup.RightArm | OIBoneInfo.BoneGroup.LeftArm);
                OIBoneInfo.BoneGroup[] fkParts = FKCtrl.parts;
                for (int i = 0; i < fkParts.Length; i++)
                {
                    if (fkParts[i] == OIBoneInfo.BoneGroup.Body || fkParts[i] == OIBoneInfo.BoneGroup.Neck)
                        oci.oiCharInfo.activeFK[i] = true;
                }
                oci.ActiveKinematicMode(OICharInfo.KinematicMode.FK, true, true);
            }
            else if (snapshotMode && oci != null && oci.oiCharInfo != null)
            {
                if (savedFK) oci.ActiveKinematicMode(OICharInfo.KinematicMode.FK, true, true);
                if (savedIK) oci.ActiveKinematicMode(OICharInfo.KinematicMode.IK, true, true);
            }
            if (cha.animBody != null) cha.animBody.enabled = savedAnim;
            if (cha.neckLookCtrl != null) cha.neckLookCtrl.enabled = savedNeck;
        }
        built = false;
        tookControl = false;
        releasing = false;
        if (container != null) UnityEngine.Object.Destroy(container);
        container = null;
        parts.Clear();
        byBody.Clear();
    }

    public void DrawColliders(Rigidbody highlight)
    {
        if (!built) return;
        foreach (Part p in parts)
        {
            if (p.col == null) continue;
            bool sleeping = p.rb.IsSleeping();
            GL.Color(p.rb == highlight ? new Color(1f, 0.9f, 0.2f, 0.9f)
                : (sleeping ? new Color(0.4f, 0.4f, 1f, 0.6f) : new Color(0.3f, 1f, 0.4f, 0.7f)));
            Transform t = p.col.transform;
            BoxCollider bc = p.col as BoxCollider;
            CapsuleCollider cc = p.col as CapsuleCollider;
            SphereCollider sc = p.col as SphereCollider;
            if (bc != null) RagdollLab.DrawBox(t.TransformPoint(bc.center), t.rotation, bc.size);
            else if (cc != null)
            {
                Vector3 ax = t.up;
                float half = Mathf.Max(0f, cc.height * 0.5f - cc.radius);
                Vector3 c = t.TransformPoint(cc.center);
                Vector3 a = c - ax * half, b = c + ax * half;
                RagdollLab.DrawCircle(a, ax, cc.radius);
                RagdollLab.DrawCircle(b, ax, cc.radius);
                Vector3 u = t.right * cc.radius, v = t.forward * cc.radius;
                RagdollLab.GLLine(a + u, b + u); RagdollLab.GLLine(a - u, b - u);
                RagdollLab.GLLine(a + v, b + v); RagdollLab.GLLine(a - v, b - v);
            }
            else if (sc != null)
            {
                Vector3 c = t.TransformPoint(sc.center);
                RagdollLab.DrawCircle(c, t.up, sc.radius);
                RagdollLab.DrawCircle(c, t.right, sc.radius);
                RagdollLab.DrawCircle(c, t.forward, sc.radius);
            }
        }
    }
}
