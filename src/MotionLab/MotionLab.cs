// #name MotionLab
// #author stp3
// #desc Plays retargeted external motion clips (.hs2m) on CharaStudio characters by writing bone rotations after the Animator.
// #proc_filter StudioNEOV2

// Loaded by BepInEx.ScriptLoader from <game>\scripts together with StudioBridge / RagdollLab / WetLab (C# 7.x, mcs:
// no tuples, local functions or pattern variables).
//
// A .hs2m clip (written by tools/hs2motion/retarget.py) holds, per frame, the local rotation of ~30 cf_J_* bones and the
// hips position in the character-object frame. We write them in a postfix of ChaControl.LateUpdateForce at Priority.Low,
// i.e. after the Animator / HS2Heelz and before RagdollLab (Priority.Last), so ragdolls can follow the motion. If
// CharaUtils.Expression already ran this frame it is run again so the *_dam twist bones follow the new pose.
// Studio's FK/IK should be off for a driven character (the bridge turns them off on motion.play).
//
// Driven through StudioBridge: any "motion.*" command is forwarded to MotionLab.Command(Dictionary).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AIChara;
using BepInEx.Logging;
using HarmonyLib;
using Studio;
using UnityEngine;

public static class MotionLabScript
{
    static GameObject host;

    public static void Main()
    {
        MotionLab.Log = BepInEx.Logging.Logger.CreateLogSource("MotionLab");
        host = new GameObject("MotionLab");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<MotionLab>();
    }

    public static void Unload()
    {
        if (host != null) UnityEngine.Object.DestroyImmediate(host);
        host = null;
        if (MotionLab.Log != null) BepInEx.Logging.Logger.Sources.Remove(MotionLab.Log);
    }
}

public static class MotionLabPatches
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(typeof(ChaControl), "LateUpdateForce")]
    public static void LateUpdateForcePostfix(ChaControl __instance)
    {
        MotionLab m = MotionLab.Instance;
        if (m != null) m.OnLateUpdateForce(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CharaUtils.Expression), "LateUpdate")]
    public static void ExpressionLateUpdatePrefix(CharaUtils.Expression __instance)
    {
        MotionLab m = MotionLab.Instance;
        if (m != null) m.OnExpressionLateUpdate(__instance);
    }
}

public class MotionClip
{
    public string Path;
    public float Fps;
    public int Frames;
    public string[] Bones;
    public Vector3[] Root;        // hips position per frame, character-object frame
    public Quaternion[] Rot;      // [frame * Bones.Length + bone] local rotation

    public float Length { get { return Frames / Fps; } }

    public Vector3 Displacement
    {
        get { return Frames > 1 ? Root[Frames - 1] - Root[0] : Vector3.zero; }
    }

    public static MotionClip Load(string path)
    {
        using (BinaryReader br = new BinaryReader(File.OpenRead(path)))
        {
            string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
            if (magic != "HS2M") throw new Exception("not an .hs2m file: " + path);
            int ver = br.ReadInt32();
            if (ver != 1) throw new Exception("unsupported .hs2m version " + ver);
            MotionClip c = new MotionClip();
            c.Path = path;
            c.Fps = br.ReadSingle();
            c.Frames = br.ReadInt32();
            int nb = br.ReadInt32();
            c.Bones = new string[nb];
            for (int i = 0; i < nb; i++) c.Bones[i] = br.ReadString();
            c.Root = new Vector3[c.Frames];
            for (int f = 0; f < c.Frames; f++) c.Root[f] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            c.Rot = new Quaternion[c.Frames * nb];
            for (int i = 0; i < c.Rot.Length; i++)
                c.Rot[i] = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            return c;
        }
    }

    // root position of an unwrapped frame index (beyond the clip the horizontal travel continues)
    public Vector3 RootAt(int f)
    {
        if (Frames == 0) return Vector3.zero;
        int loops = 0;
        if (f >= Frames) { loops = f / Frames; f -= loops * Frames; }
        Vector3 p = Root[f];
        if (loops > 0) { Vector3 d = Displacement; d.y = 0f; p += d * loops; }
        return p;
    }
}

public class MotionPlayer
{
    public int Key;
    public OCIChar Oci;
    public ChaControl Cha;
    public string Name;
    public MotionClip Clip;
    public Transform[] Bones;
    public Transform Hips;
    public Transform CharRoot;
    public CharaUtils.Expression Expression;

    public float Time;
    public float Speed = 1f;
    public bool Loop = true;
    public bool Playing;
    public string RootMode = "full";      // full | inplace | y | none
    public float Weight = 1f, FadeTarget = 1f, FadeSpeed = 0f;
    public bool RemoveWhenFaded;
    public Vector3 Offset = Vector3.zero;  // added to the hips position (character frame)
    public int HookFrame = -100, ExpressionFrame = -1;
    public bool OrderLogged;
    // Timeline sync: clip time = (Timeline playbackTime - Start) * Speed; nothing is written before Start
    public bool Sync;
    public float Start;
    public bool Active = true;
    // HS2's neck-look controller rewrites the head/neck after us; it is skipped while the clip drives the character
    public bool NeckLookSkipped;
    bool neckSkipSaved;

    public void SuppressNeckLook(bool on)
    {
        // same approach as RagdollLab: disable the NeckLookControllerVer2 component (skipCalc alone keeps applying the
        // last computed rotation), restore its previous enabled state when the clip is released
        try
        {
            if (Cha == null || Cha.neckLookCtrl == null) return;
            if (on && !NeckLookSkipped) { neckSkipSaved = Cha.neckLookCtrl.enabled; Cha.neckLookCtrl.enabled = false; NeckLookSkipped = true; }
            else if (!on && NeckLookSkipped) { Cha.neckLookCtrl.enabled = neckSkipSaved; NeckLookSkipped = false; }
        }
        catch (Exception) { }
    }

    public void Bind(MotionClip clip)
    {
        Clip = clip;
        Transform root = Cha.objBodyBone != null ? Cha.objBodyBone.transform : Cha.transform;
        Dictionary<string, Transform> map = new Dictionary<string, Transform>();
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) if (!map.ContainsKey(t.name)) map[t.name] = t;
        Bones = new Transform[clip.Bones.Length];
        int missing = 0;
        for (int i = 0; i < clip.Bones.Length; i++)
        {
            Transform t;
            if (map.TryGetValue(clip.Bones[i], out t)) Bones[i] = t; else missing++;
        }
        Hips = map.ContainsKey("cf_J_Hips") ? map["cf_J_Hips"] : null;
        CharRoot = Cha.transform;
        Expression = Cha.expression;
        if (missing > 0) MotionLab.Log.LogWarning(Name + ": " + missing + " clip bones are missing on the character");
    }

    public void Advance(float dt)
    {
        if (FadeSpeed > 0f) Weight = Mathf.MoveTowards(Weight, FadeTarget, FadeSpeed * dt);
        if (Sync)
        {
            float tl;
            if (MotionLab.TimelineTime(out tl))
            {
                float t = (tl - Start) * Speed;
                Active = t >= 0f && (Loop || Clip == null || t <= Clip.Length);
                Time = Mathf.Max(0f, t);
            }
            return;
        }
        Active = true;
        if (!Playing || Clip == null) return;
        Time += dt * Speed;
        if (!Loop)
        {
            float len = Clip.Length;
            if (Time >= len) { Time = Mathf.Max(0f, len - 1e-4f); Playing = false; }
            if (Time < 0f) { Time = 0f; Playing = false; }
        }
        else if (Time < 0f) Time += Clip.Length * Mathf.Ceil(-Time / Clip.Length);
    }

    public bool Faded { get { return RemoveWhenFaded && Weight <= 0f; } }

    // write the pose at Time into the bones (called from the LateUpdateForce hook)
    public void Apply()
    {
        if (Clip == null || Bones == null || Weight <= 0f || Clip.Frames == 0 || !Active) return;
        float len = Clip.Length;
        float t = Time;
        int loops = 0;
        if (Loop && len > 0f && t >= len) { loops = Mathf.FloorToInt(t / len); t -= loops * len; }
        float fpos = Mathf.Clamp(t * Clip.Fps, 0f, Clip.Frames - 1e-3f);
        int f0 = Mathf.Clamp(Mathf.FloorToInt(fpos), 0, Clip.Frames - 1);
        float u = fpos - f0;
        int f1 = f0 + 1;
        int f1rot = f1;
        if (f1 >= Clip.Frames)
        {
            if (Loop) f1rot = 0; else { f1 = f0; f1rot = f0; u = 0f; }
        }
        int nb = Clip.Bones.Length;
        bool full = Weight >= 0.999f;
        for (int i = 0; i < nb; i++)
        {
            Transform b = Bones[i];
            if (b == null) continue;
            Quaternion q = Quaternion.Slerp(Clip.Rot[f0 * nb + i], Clip.Rot[f1rot * nb + i], u);
            b.localRotation = full ? q : Quaternion.Slerp(b.localRotation, q, Weight);
        }
        if (Hips != null && RootMode != "none")
        {
            int base0 = loops * Clip.Frames;
            Vector3 p = Vector3.Lerp(Clip.RootAt(base0 + f0), Clip.RootAt(base0 + f1), u);
            if (RootMode == "inplace" || RootMode == "y")
            {
                // remove the horizontal travel (keep the sway): subtract the linear trend of the clip
                Vector3 d = Clip.Displacement; d.y = 0f;
                float frac = Clip.Frames > 1 ? (fpos / (Clip.Frames - 1)) : 0f;
                p -= d * (loops + frac);
                p.x = Clip.Root[0].x + (RootMode == "y" ? 0f : (p.x - Clip.Root[0].x));
                p.z = Clip.Root[0].z + (RootMode == "y" ? 0f : (p.z - Clip.Root[0].z));
            }
            p += Offset;
            Vector3 wp = CharRoot.TransformPoint(p);
            Hips.position = full ? wp : Vector3.Lerp(Hips.position, wp, Weight);
        }
    }

    public Dictionary<string, object> Status()
    {
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["key"] = Key; m["name"] = Name; m["clip"] = Clip != null ? Clip.Path : null;
        m["time"] = Clip != null && Loop && Clip.Length > 0f ? Time % Clip.Length : Time;
        m["elapsed"] = Time; m["length"] = Clip != null ? Clip.Length : 0f; m["fps"] = Clip != null ? Clip.Fps : 0f;
        m["frames"] = Clip != null ? Clip.Frames : 0; m["playing"] = Playing; m["loop"] = Loop; m["speed"] = Speed;
        m["weight"] = Weight; m["root"] = RootMode; m["sync"] = Sync; m["start"] = Start; m["active"] = Active;
        return m;
    }
}

public class MotionLab : MonoBehaviour
{
    public static ManualLogSource Log;
    public static MotionLab Instance;
    static Harmony harmony;
    static MethodInfo exprLateUpdate;

    readonly List<MotionPlayer> players = new List<MotionPlayer>();
    readonly Dictionary<string, MotionClip> cache = new Dictionary<string, MotionClip>();

    static Type tlType;
    static System.Reflection.PropertyInfo tlTime;
    static bool tlMissing;

    // current Timeline playback time (Timeline plugin, by reflection); false when the plugin is not present
    public static bool TimelineTime(out float t)
    {
        t = 0f;
        if (tlMissing) return false;
        if (tlType == null)
        {
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type ty = a.GetType("Timeline.Timeline");
                if (ty != null && ty.GetMethod("Play") != null) { tlType = ty; break; }
            }
            if (tlType == null) { tlMissing = true; return false; }
            tlTime = tlType.GetProperty("playbackTime");
            if (tlTime == null) { tlMissing = true; return false; }
        }
        object v = tlTime.GetValue(null, null);
        t = Convert.ToSingle(v);
        return true;
    }

    public static string ClipDir
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.dataPath), Path.Combine("UserData", "MotionLab")); }
    }

    void Awake()
    {
        Instance = this;
        try { harmony = Harmony.CreateAndPatchAll(typeof(MotionLabPatches), "stp3.motionlab"); }
        catch (Exception e) { Log.LogError("MotionLab patches failed: " + e); }
        Log.LogInfo("MotionLab ready; clip dir " + ClipDir);
    }

    void OnDestroy()
    {
        for (int i = 0; i < players.Count; i++) players[i].SuppressNeckLook(false);
        try { if (harmony != null) harmony.UnpatchSelf(); } catch (Exception) { }
        harmony = null;
        players.Clear();
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        float dt = UnityEngine.Time.deltaTime;
        for (int i = players.Count - 1; i >= 0; i--)
        {
            MotionPlayer p = players[i];
            if (p.Cha == null || p.Oci == null || p.Faded) { p.SuppressNeckLook(false); players.RemoveAt(i); continue; }
            p.Advance(dt);
        }
    }

    // fallback for characters whose LateUpdateForce hook was not seen (should not happen in Studio)
    void LateUpdate()
    {
        for (int i = 0; i < players.Count; i++)
        {
            MotionPlayer p = players[i];
            if (p.Cha != null && UnityEngine.Time.frameCount - p.HookFrame > 2) { p.Apply(); }
        }
    }

    public void OnLateUpdateForce(ChaControl cha)
    {
        for (int i = 0; i < players.Count; i++)
        {
            MotionPlayer p = players[i];
            if (p.Cha != cha || p.HookFrame == UnityEngine.Time.frameCount) continue;
            p.HookFrame = UnityEngine.Time.frameCount;
            bool exprAlready = p.ExpressionFrame == UnityEngine.Time.frameCount;
            if (!p.OrderLogged)
            {
                p.OrderLogged = true;
                Log.LogInfo(p.Name + ": hook order: Expression " + (exprAlready ? "before" : "after") + " LateUpdateForce");
            }
            p.Apply();
            if (exprAlready && p.Expression != null) RunExpression(p.Expression);
        }
    }

    public void OnExpressionLateUpdate(CharaUtils.Expression e)
    {
        for (int i = 0; i < players.Count; i++) if (players[i].Expression == e) players[i].ExpressionFrame = UnityEngine.Time.frameCount;
    }

    static void RunExpression(CharaUtils.Expression e)
    {
        if (exprLateUpdate == null) exprLateUpdate = AccessTools.Method(typeof(CharaUtils.Expression), "LateUpdate");
        try { exprLateUpdate.Invoke(e, null); } catch (Exception) { }
    }

    // ---------------------------------------------------------------- API (called by StudioBridge through reflection)

    MotionPlayer Find(int key)
    {
        for (int i = 0; i < players.Count; i++) if (players[i].Key == key) return players[i];
        return null;
    }

    static OCIChar CharaByKey(int key)
    {
        ObjectCtrlInfo oci;
        if (!Studio.Studio.Instance.dicObjectCtrl.TryGetValue(key, out oci)) throw new Exception("no object with key " + key);
        OCIChar oc = oci as OCIChar;
        if (oc == null || oc.charInfo == null) throw new Exception("key " + key + " is not a character");
        return oc;
    }

    MotionClip GetClip(string path, bool reload)
    {
        if (string.IsNullOrEmpty(path)) throw new Exception("path required");
        if (!Path.IsPathRooted(path)) path = Path.Combine(ClipDir, path);
        if (!path.EndsWith(".hs2m") && !File.Exists(path)) path += ".hs2m";
        path = Path.GetFullPath(path);
        MotionClip c;
        if (!reload && cache.TryGetValue(path, out c)) return c;
        if (!File.Exists(path)) throw new Exception("clip not found: " + path);
        c = MotionClip.Load(path);
        cache[path] = c;
        return c;
    }

    static double Num(Dictionary<string, object> q, string k, double def)
    {
        object v;
        if (!q.TryGetValue(k, out v) || v == null) return def;
        if (v is double) return (double)v;
        if (v is long) return (long)v;
        if (v is int) return (int)v;
        double d;
        return double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : def;
    }

    static bool Bool(Dictionary<string, object> q, string k, bool def)
    {
        object v;
        if (!q.TryGetValue(k, out v) || v == null) return def;
        if (v is bool) return (bool)v;
        string s = v.ToString().ToLowerInvariant();
        return s == "true" || s == "1";
    }

    static string Str(Dictionary<string, object> q, string k, string def)
    {
        object v;
        return q.TryGetValue(k, out v) && v != null ? v.ToString() : def;
    }

    static Vector3 Vec(object v, Vector3 def)
    {
        List<object> l = v as List<object>;
        if (l == null || l.Count < 3) return def;
        return new Vector3((float)Num1(l[0]), (float)Num1(l[1]), (float)Num1(l[2]));
    }

    static double Num1(object v)
    {
        if (v is double) return (double)v;
        if (v is long) return (long)v;
        double d;
        return v != null && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0;
    }

    public Dictionary<string, object> Command(Dictionary<string, object> q)
    {
        string cmd = Str(q, "cmd", "");
        Dictionary<string, object> r = new Dictionary<string, object>();
        switch (cmd)
        {
            case "motion.load":
            {
                // {path, reload} -> clip info (also warms the cache)
                MotionClip c = GetClip(Str(q, "path", null), Bool(q, "reload", false));
                r["path"] = c.Path; r["fps"] = c.Fps; r["frames"] = c.Frames; r["length"] = c.Length;
                r["bones"] = new List<object>(c.Bones);
                r["displacement"] = new List<object> { c.Displacement.x, c.Displacement.y, c.Displacement.z };
                return r;
            }
            case "motion.play":
            {
                // {key, path, speed, loop, t, root(full|inplace|y|none), fade, weight, offset[3], reload,
                //  sync (follow the Timeline clock), start (Timeline time at which the clip begins), keepNeckLook (leave HS2's
                //  neck-look controller on; by default it is skipped so the clip's head rotation shows)}
                int key = (int)Num(q, "key", -1);
                OCIChar oc = CharaByKey(key);
                MotionPlayer p = Find(key);
                string path = Str(q, "path", null);
                if (p == null)
                {
                    if (path == null) throw new Exception("path required for a new player");
                    p = new MotionPlayer();
                    p.Key = key; p.Oci = oc; p.Cha = oc.charInfo; p.Name = oc.treeNodeObject != null ? oc.treeNodeObject.textName : ("chara " + key);
                    players.Add(p);
                }
                if (path != null) p.Bind(GetClip(path, Bool(q, "reload", false)));
                else if (p.Clip == null) throw new Exception("no clip loaded on this character");
                p.Speed = (float)Num(q, "speed", p.Speed);
                p.Loop = Bool(q, "loop", p.Loop);
                p.RootMode = Str(q, "root", p.RootMode);
                if (q.ContainsKey("offset")) p.Offset = Vec(q["offset"], p.Offset);
                p.SuppressNeckLook(!Bool(q, "keepNeckLook", false));
                if (q.ContainsKey("sync")) p.Sync = Bool(q, "sync", false);
                if (q.ContainsKey("start")) p.Start = (float)Num(q, "start", 0);
                if (q.ContainsKey("t")) p.Time = (float)Num(q, "t", 0);
                else if (path != null) p.Time = 0f;
                float fade = (float)Num(q, "fade", 0);
                float w = (float)Num(q, "weight", 1);
                p.RemoveWhenFaded = false;
                if (fade > 0f) { if (path != null && !q.ContainsKey("weight")) p.Weight = 0f; p.FadeTarget = w; p.FadeSpeed = Mathf.Abs(w - p.Weight) / fade; }
                else { p.Weight = w; p.FadeTarget = w; p.FadeSpeed = 0f; }
                p.Playing = Bool(q, "playing", true);
                r["status"] = p.Status();
                return r;
            }
            case "motion.pause":
            {
                MotionPlayer p = Find((int)Num(q, "key", -1));
                if (p == null) throw new Exception("no motion on this character");
                p.Playing = Bool(q, "playing", false);
                r["status"] = p.Status();
                return r;
            }
            case "motion.seek":
            {
                MotionPlayer p = Find((int)Num(q, "key", -1));
                if (p == null) throw new Exception("no motion on this character");
                p.Time = (float)Num(q, "t", 0);
                if (q.ContainsKey("playing")) p.Playing = Bool(q, "playing", p.Playing);
                r["status"] = p.Status();
                return r;
            }
            case "motion.speed":
            {
                MotionPlayer p = Find((int)Num(q, "key", -1));
                if (p == null) throw new Exception("no motion on this character");
                p.Speed = (float)Num(q, "speed", p.Speed);
                if (q.ContainsKey("loop")) p.Loop = Bool(q, "loop", p.Loop);
                r["status"] = p.Status();
                return r;
            }
            case "motion.stop":
            {
                // {key, fade}: release the character back to the Animator (all when key is omitted)
                float fade = (float)Num(q, "fade", 0);
                int key = (int)Num(q, "key", int.MinValue);
                int n = 0;
                for (int i = players.Count - 1; i >= 0; i--)
                {
                    MotionPlayer p = players[i];
                    if (key != int.MinValue && p.Key != key) continue;
                    n++;
                    if (fade > 0f) { p.RemoveWhenFaded = true; p.FadeTarget = 0f; p.FadeSpeed = p.Weight / fade; }
                    else { p.SuppressNeckLook(false); players.RemoveAt(i); }
                }
                r["stopped"] = n;
                return r;
            }
            case "motion.status":
            {
                List<object> l = new List<object>();
                for (int i = 0; i < players.Count; i++) l.Add(players[i].Status());
                r["players"] = l;
                r["clipDir"] = ClipDir;
                List<object> files = new List<object>();
                if (Directory.Exists(ClipDir)) foreach (string f in Directory.GetFiles(ClipDir, "*.hs2m")) files.Add(Path.GetFileNameWithoutExtension(f));
                r["clips"] = files;
                return r;
            }
            default:
                throw new Exception("unknown motion command " + cmd);
        }
    }
}
