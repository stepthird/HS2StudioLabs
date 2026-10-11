// #name StudioBridge
// #author stp3
// #desc Local HTTP command bridge so an external agent can inspect and drive CharaStudio.
// #proc_filter StudioNEOV2

// Stage 1. Loaded by BepInEx.ScriptLoader together with the other scripts in <game>\scripts (C# 7.x, mcs).
//
// Listens on http://127.0.0.1:17321/ only (this PC). POST a JSON object {"cmd": "...", ...}; the command runs on
// Unity's main thread and the reply is JSON {"ok": true, ...} or {"ok": false, "error": "..."}.
// Requests from web browsers (Origin or Sec-Fetch-Site header) are refused with 403.
// Commands: ping, scene, maps, map.load, obj.transform, select, anime.search, chara.anime, chara.speed,
//           camera.get, camera.set, camera.frame, capture.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using AIChara;
using BepInEx.Logging;
using KKAPI.Studio;
using Studio;
using UnityEngine;

public static class StudioBridgeScript
{
    static GameObject host;

    public static void Main()
    {
        StudioBridge.Log = BepInEx.Logging.Logger.CreateLogSource("StudioBridge");
        host = new GameObject("StudioBridge");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<StudioBridge>();
    }

    public static void Unload()
    {
        if (host != null) UnityEngine.Object.DestroyImmediate(host);
        host = null;
        if (StudioBridge.Log != null) BepInEx.Logging.Logger.Sources.Remove(StudioBridge.Log);
    }
}

public partial class StudioBridge : MonoBehaviour
{
    public static ManualLogSource Log;
    public const int Port = 17321;
    public static readonly string ShotDir = Path.Combine(BepInEx.Paths.GameRootPath, @"UserData\StudioBridge\shots");

    class Job
    {
        public Dictionary<string, object> req;
        public string reply;
        public ManualResetEvent done = new ManualResetEvent(false);
        public int runAtFrame;
    }
    readonly List<Job> deferred = new List<Job>();

    HttpListener listener;
    Thread thread;
    volatile bool stopping;
    readonly Queue<Job> jobs = new Queue<Job>();

    // ---------------------------------------------------------------- server

    void Start()
    {
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            listener.Start();
            thread = new Thread(Serve);
            thread.IsBackground = true;
            thread.Start();
            Log.LogInfo("StudioBridge listening on http://127.0.0.1:" + Port + "/");
        }
        catch (Exception e) { Log.LogError("StudioBridge could not listen: " + e); }
    }

    void OnEnable() { Camera.onPreCull += OnPreCullAttach; }

    void OnDisable() { Camera.onPreCull -= OnPreCullAttach; }

    void OnDestroy()
    {
        attachments.Clear();
        DofShutdown();
        stopping = true;
        try { if (listener != null) { listener.Stop(); listener.Close(); } } catch (Exception) { }
        lock (jobs)
        {
            while (jobs.Count > 0) { Job j = jobs.Dequeue(); j.reply = Err("shutting down"); j.done.Set(); }
        }
        foreach (Job j in deferred) { j.reply = Err("shutting down"); j.done.Set(); }
        deferred.Clear();
    }

    void Serve()
    {
        while (!stopping)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch (Exception) { if (stopping) return; continue; }
            // Any web page can POST to 127.0.0.1 (it cannot read the reply, but the command would still run).
            // Browsers send Origin / Sec-Fetch-Site with such requests; local clients (curl, Python) send neither.
            if (ctx.Request.Headers["Origin"] != null || ctx.Request.Headers["Sec-Fetch-Site"] != null)
            {
                try { ctx.Response.StatusCode = 403; ctx.Response.Close(); } catch (Exception) { }
                continue;
            }
            string reply;
            try
            {
                string body;
                using (StreamReader r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                if (string.IsNullOrEmpty(body)) body = "{\"cmd\":\"ping\"}";
                Dictionary<string, object> req = MiniJson.Parse(body) as Dictionary<string, object>;
                if (req == null) reply = Err("body must be a JSON object");
                else
                {
                    Job job = new Job();
                    job.req = req;
                    lock (jobs) jobs.Enqueue(job);
                    reply = job.done.WaitOne(60000) ? job.reply : Err("timed out waiting for the main thread");
                }
            }
            catch (Exception e) { reply = Err("bad request: " + e.Message); }
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception) { }
        }
    }

    void Update()
    {
        int n = 0;
        while (n++ < 20)
        {
            Job job = null;
            lock (jobs) { if (jobs.Count > 0) job = jobs.Dequeue(); }
            if (job == null) break;
            // captures wait a few frames so camera/pose changes from earlier commands have been applied
            if (Str(job.req, "cmd", "") == "capture")
            {
                job.runAtFrame = Time.frameCount + Math.Max(1, Int(job.req, "waitFrames", 3));
                deferred.Add(job);
                continue;
            }
            Execute(job);
        }
        RunTimelineEvents();
        RunMovers();
        DofRegister();
        for (int i = deferred.Count - 1; i >= 0; i--)
        {
            if (Time.frameCount < deferred[i].runAtFrame) continue;
            Job job = deferred[i];
            deferred.RemoveAt(i);
            Execute(job);
        }
    }

    void Execute(Job job)
    {
        try { job.reply = MiniJson.Serialize(Run(job.req)); }
        catch (Exception e) { job.reply = Err(e.GetType().Name + ": " + e.Message); Log.LogWarning("command failed: " + e); }
        job.done.Set();
    }

    static string Err(string msg)
    {
        Dictionary<string, object> d = new Dictionary<string, object>();
        d["ok"] = false; d["error"] = msg;
        return MiniJson.Serialize(d);
    }

    // ---------------------------------------------------------------- commands

    Dictionary<string, object> Run(Dictionary<string, object> q)
    {
        string cmd = Str(q, "cmd", "");
        Dictionary<string, object> r = new Dictionary<string, object>();
        r["ok"] = true;
        if (cmd == "ping") { r["studio"] = StudioAPI.StudioLoaded; r["version"] = 1; r["frame"] = Time.frameCount; r["realtime"] = Time.realtimeSinceStartup; r["screen"] = Screen.width + "x" + Screen.height; return r; }
        if (!StudioAPI.StudioLoaded) throw new Exception("Studio is not loaded yet");
        Studio.Studio st = Studio.Studio.Instance;
        Info info = Info.Instance;

        switch (cmd)
        {
            case "scene":
            {
                int mapNo = st.sceneInfo != null ? st.sceneInfo.mapInfo.no : -1;
                Dictionary<string, object> map = new Dictionary<string, object>();
                map["no"] = mapNo;
                Info.MapLoadInfo mi;
                map["name"] = info.dicMapLoadInfo.TryGetValue(mapNo, out mi) ? mi.name : "";
                r["map"] = map;
                r["camera"] = CameraState();
                List<object> objs = new List<object>();
                foreach (KeyValuePair<int, ObjectCtrlInfo> kv in st.dicObjectCtrl) objs.Add(Describe(kv.Key, kv.Value));
                r["objects"] = objs;
                return r;
            }
            case "maps":
            {
                string qs = Str(q, "q", "").ToLowerInvariant();
                List<object> list = new List<object>();
                foreach (KeyValuePair<int, Info.MapLoadInfo> kv in info.dicMapLoadInfo)
                {
                    if (qs.Length > 0 && (kv.Value.name ?? "").ToLowerInvariant().IndexOf(qs, StringComparison.Ordinal) < 0) continue;
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["no"] = kv.Key; m["name"] = kv.Value.name;
                    list.Add(m);
                    if (list.Count >= Int(q, "limit", 50)) break;
                }
                r["maps"] = list;
                return r;
            }
            case "map.load":
            {
                int no = Int(q, "no", int.MinValue);
                if (no == int.MinValue)
                {
                    string name = Str(q, "name", "");
                    foreach (KeyValuePair<int, Info.MapLoadInfo> kv in info.dicMapLoadInfo)
                        if (string.Equals(kv.Value.name, name, StringComparison.OrdinalIgnoreCase)) { no = kv.Key; break; }
                    if (no == int.MinValue)
                        foreach (KeyValuePair<int, Info.MapLoadInfo> kv in info.dicMapLoadInfo)
                            if ((kv.Value.name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { no = kv.Key; break; }
                }
                if (no == int.MinValue || !info.dicMapLoadInfo.ContainsKey(no)) throw new Exception("map not found");
                st.AddMap(no, true, true, true);
                r["no"] = no; r["name"] = info.dicMapLoadInfo[no].name; r["note"] = "loading asynchronously; poll scene.map.no";
                return r;
            }
            case "select":
            {
                ObjectCtrlInfo oci = Obj(st, q);
                st.treeNodeCtrl.SelectSingle(oci.treeNodeObject);
                return r;
            }
            case "obj.transform":
            {
                ObjectCtrlInfo oci = Obj(st, q);
                ChangeAmount ca = oci.objectInfo.changeAmount;
                if (q.ContainsKey("pos")) ca.pos = Vec(q["pos"]);
                if (q.ContainsKey("rot")) ca.rot = Vec(q["rot"]);
                if (q.ContainsKey("scale")) ca.scale = Vec(q["scale"]);
                r["object"] = Describe(Int(q, "key", -1), oci);
                return r;
            }
            case "anime.search":
            {
                string qs = Str(q, "q", "").ToLowerInvariant();
                int limit = Int(q, "limit", 60);
                int gFilter = Int(q, "group", int.MinValue), cFilter = Int(q, "category", int.MinValue);
                List<object> list = new List<object>();
                foreach (KeyValuePair<int, Dictionary<int, Dictionary<int, Info.AnimeLoadInfo>>> g in info.dicAnimeLoadInfo)
                {
                    if (gFilter != int.MinValue && g.Key != gFilter) continue;
                    Info.GroupInfo gi; info.dicAGroupCategory.TryGetValue(g.Key, out gi);
                    foreach (KeyValuePair<int, Dictionary<int, Info.AnimeLoadInfo>> c in g.Value)
                    {
                        if (cFilter != int.MinValue && c.Key != cFilter) continue;
                        Info.CategoryInfo ci = null;
                        if (gi != null) gi.dicCategory.TryGetValue(c.Key, out ci);
                        string gname = gi != null ? gi.name : "", cname = ci != null ? ci.name : "";
                        foreach (KeyValuePair<int, Info.AnimeLoadInfo> a in c.Value)
                        {
                            string hay = (gname + " / " + cname + " / " + a.Value.name).ToLowerInvariant();
                            if (qs.Length > 0 && hay.IndexOf(qs, StringComparison.Ordinal) < 0) continue;
                            Dictionary<string, object> m = new Dictionary<string, object>();
                            m["group"] = g.Key; m["category"] = c.Key; m["no"] = a.Key;
                            m["groupName"] = gname; m["categoryName"] = cname; m["name"] = a.Value.name;
                            list.Add(m);
                            if (list.Count >= limit) { r["anime"] = list; r["truncated"] = true; return r; }
                        }
                    }
                }
                r["anime"] = list;
                return r;
            }
            case "anime.groups":
            {
                List<object> list = new List<object>();
                foreach (KeyValuePair<int, Info.GroupInfo> g in info.dicAGroupCategory)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["group"] = g.Key; m["name"] = g.Value.name;
                    List<object> cats = new List<object>();
                    foreach (KeyValuePair<int, Info.CategoryInfo> c in g.Value.dicCategory)
                    {
                        Dictionary<string, object> cm = new Dictionary<string, object>();
                        cm["category"] = c.Key; cm["name"] = c.Value.name;
                        cats.Add(cm);
                    }
                    m["categories"] = cats;
                    list.Add(m);
                }
                r["groups"] = list;
                return r;
            }
            case "chara.anime":
            {
                OCIChar oc = Obj(st, q) as OCIChar;
                if (oc == null) throw new Exception("not a character");
                oc.LoadAnime(Int(q, "group", 0), Int(q, "category", 0), Int(q, "no", 0), Flt(q, "time", 0f));
                if (q.ContainsKey("speed")) oc.animeSpeed = Flt(q, "speed", 1f);
                if (q.ContainsKey("loop")) oc.charAnimeCtrl.isForceLoop = Bool(q, "loop", false);   // the anime panel's loop toggle
                r["object"] = Describe(Int(q, "key", -1), oc);
                return r;
            }
            case "chara.speed":
            {
                OCIChar oc = Obj(st, q) as OCIChar;
                if (oc == null) throw new Exception("not a character");
                if (q.ContainsKey("speed")) oc.animeSpeed = Flt(q, "speed", 1f);
                if (q.ContainsKey("loop")) oc.charAnimeCtrl.isForceLoop = Bool(q, "loop", false);
                r["speed"] = oc.animeSpeed; r["loop"] = oc.charAnimeCtrl.isForceLoop;
                return r;
            }
            case "camera.get":
                r["camera"] = CameraState();
                return r;
            case "camera.set":
            {
                Studio.CameraControl cc = st.cameraCtrl;
                Studio.CameraControl.CameraData d = cc.Export();
                if (q.ContainsKey("target")) d.pos = Vec(q["target"]);
                if (q.ContainsKey("rot")) d.rotate = Vec(q["rot"]);
                if (q.ContainsKey("dist")) d.distance = new Vector3(0f, 0f, -Mathf.Abs(Flt(q, "dist", 10f)));
                if (q.ContainsKey("fov")) d.parse = Flt(q, "fov", 23f);
                cc.Import(d);
                r["camera"] = CameraState();
                return r;
            }
            case "camera.frame":
            {
                // orbit framing around an object (or a point): yaw/pitch in degrees, distance in units
                Vector3 target;
                if (q.ContainsKey("point")) target = Vec(q["point"]);
                else
                {
                    ObjectCtrlInfo oci = Obj(st, q);
                    target = oci.guideObject != null && oci.guideObject.transformTarget != null
                        ? oci.guideObject.transformTarget.position : Vector3.zero;
                }
                target += Vector3.up * Flt(q, "height", 0f);
                Studio.CameraControl cc = st.cameraCtrl;
                Studio.CameraControl.CameraData d = cc.Export();
                d.pos = cc.transBase != null ? cc.transBase.InverseTransformPoint(target) : target;
                d.rotate = new Vector3(Flt(q, "pitch", 10f), Flt(q, "yaw", 180f), 0f);
                d.distance = new Vector3(0f, 0f, -Mathf.Abs(Flt(q, "dist", 25f)));
                if (q.ContainsKey("fov")) d.parse = Flt(q, "fov", 23f);
                cc.Import(d);
                r["camera"] = CameraState();
                return r;
            }
            case "chara.face":
            {
                OCIChar oc = Chara(st, q);
                ChaControl c = oc.charInfo;
                // eyebrow/eyes: the numbers Studio's UI shows (positions in the character's pattern list); raw:true = internal ids
                bool raw = Bool(q, "raw", false);
                if (q.ContainsKey("eyebrow")) c.ChangeEyebrowPtn(raw ? Int(q, "eyebrow", 0) : UiToId(PatternKeys(oc, true), Int(q, "eyebrow", 0)), false);
                if (q.ContainsKey("eyes")) c.ChangeEyesPtn(raw ? Int(q, "eyes", 0) : UiToId(PatternKeys(oc, false), Int(q, "eyes", 0)), false);
                if (q.ContainsKey("mouth")) oc.ChangeMouthPtn(Int(q, "mouth", 0));
                if (q.ContainsKey("eyesOpen")) oc.ChangeEyesOpen(Flt(q, "eyesOpen", 1f));
                if (q.ContainsKey("mouthOpen")) oc.ChangeMouthOpen(Flt(q, "mouthOpen", 0f));
                if (q.ContainsKey("blink")) oc.ChangeBlink(Bool(q, "blink", true));
                if (q.ContainsKey("eyesLook")) oc.ChangeLookEyesPtn(Int(q, "eyesLook", 0), true);
                if (q.ContainsKey("neckLook")) oc.ChangeLookNeckPtn(Int(q, "neckLook", 0));
                if (q.ContainsKey("tears")) oc.SetTears(Flt(q, "tears", 0f));
                if (q.ContainsKey("blush")) c.ChangeHohoAkaRate(Flt(q, "blush", 0f));
                r["face"] = FaceState(oc);
                return r;
            }
            case "chara.clothes":
            {
                OCIChar oc = Chara(st, q);
                if (q.ContainsKey("all")) oc.SetClothesStateAll(Int(q, "all", 0));
                Dictionary<string, object> states = q.ContainsKey("states") ? q["states"] as Dictionary<string, object> : null;
                if (states != null)
                    foreach (KeyValuePair<string, object> kv in states)
                    {
                        int kind = Array.IndexOf(ClothesKinds, kv.Key);
                        if (kind < 0) int.TryParse(kv.Key, out kind);
                        if (kind < 0) throw new Exception("unknown clothes kind " + kv.Key);
                        oc.SetClothesState(kind, (byte)Math.Round(Num(kv.Value, 0)));
                    }
                Dictionary<string, object> cs = new Dictionary<string, object>();
                byte[] st2 = oc.charInfo.fileStatus.clothesState;
                for (int i = 0; i < st2.Length && i < ClothesKinds.Length; i++) cs[ClothesKinds[i]] = (int)st2[i];
                r["clothes"] = cs;
                r["note"] = "0 on, 1 half, 2 off";
                return r;
            }
            case "chara.hands":
            {
                OCIChar oc = Chara(st, q);
                if (q.ContainsKey("left")) oc.ChangeHandAnime(0, Int(q, "left", 0));
                if (q.ContainsKey("right")) oc.ChangeHandAnime(1, Int(q, "right", 0));
                r["hands"] = new List<object> { oc.oiCharInfo.handPtn[0], oc.oiCharInfo.handPtn[1] };
                return r;
            }
            case "fk.list":
            {
                OCIChar oc = Chara(st, q);
                string grp = Str(q, "group", "");
                List<object> list = new List<object>();
                foreach (OCIChar.BoneInfo b in oc.listBones)
                {
                    if (grp.Length > 0 && b.boneGroup.ToString().IndexOf(grp, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["bone"] = BoneName(b);
                    m["group"] = b.boneGroup.ToString();
                    m["rot"] = V(b.boneInfo.changeAmount.rot);
                    list.Add(m);
                }
                r["fkEnabled"] = oc.oiCharInfo.enableFK;
                r["bones"] = list;
                return r;
            }
            case "fk.enable":
            {
                OCIChar oc = Chara(st, q);
                bool on = Bool(q, "on", true);
                List<object> groups = q.ContainsKey("groups") ? q["groups"] as List<object> : null;
                if (groups != null)
                    foreach (object g in groups)
                        oc.ActiveFK((OIBoneInfo.BoneGroup)Enum.Parse(typeof(OIBoneInfo.BoneGroup), g.ToString(), true), on, false);
                else oc.ActiveKinematicMode(OICharInfo.KinematicMode.FK, on, true);
                r["fkEnabled"] = oc.oiCharInfo.enableFK;
                r["ikEnabled"] = oc.oiCharInfo.enableIK;
                return r;
            }
            case "ik.enable":
            {
                OCIChar oc = Chara(st, q);
                oc.ActiveKinematicMode(OICharInfo.KinematicMode.IK, Bool(q, "on", true), true);
                r["ikEnabled"] = oc.oiCharInfo.enableIK;
                return r;
            }
            case "fk.capture":
            {
                // copy the current (animated) pose into FK and switch FK on for body and neck, as a base to adjust
                OCIChar oc = Chara(st, q);
                oc.fkCtrl.CopyBone();
                OIBoneInfo.BoneGroup[] parts = FKCtrl.parts;
                for (int i = 0; i < parts.Length; i++)
                    if (parts[i] == OIBoneInfo.BoneGroup.Body || parts[i] == OIBoneInfo.BoneGroup.Neck) oc.oiCharInfo.activeFK[i] = true;
                oc.ActiveKinematicMode(OICharInfo.KinematicMode.FK, true, true);
                if (Bool(q, "stopAnime", true)) oc.animeSpeed = 0f;
                r["fkEnabled"] = oc.oiCharInfo.enableFK;
                return r;
            }
            case "fk.set":
            {
                // bones: {"cf_J_Spine01": [x, y, z], ...} local Euler angles, as shown by fk.list
                OCIChar oc = Chara(st, q);
                Dictionary<string, object> bones = q.ContainsKey("bones") ? q["bones"] as Dictionary<string, object> : null;
                if (bones == null) throw new Exception("bones: {name: [x,y,z]} required");
                int set = 0;
                List<object> missing = new List<object>();
                foreach (KeyValuePair<string, object> kv in bones)
                {
                    OCIChar.BoneInfo hit = null;
                    foreach (OCIChar.BoneInfo b in oc.listBones) if (BoneName(b) == kv.Key) { hit = b; break; }
                    if (hit == null) { missing.Add(kv.Key); continue; }
                    hit.boneInfo.changeAmount.rot = Vec(kv.Value);
                    set++;
                }
                r["set"] = set;
                if (missing.Count > 0) r["missing"] = missing;
                return r;
            }
            case "item.search":
            {
                string qs = Str(q, "q", "").ToLowerInvariant();
                int limit = Int(q, "limit", 60);
                List<object> list = new List<object>();
                foreach (KeyValuePair<int, Dictionary<int, Dictionary<int, Info.ItemLoadInfo>>> g in info.dicItemLoadInfo)
                {
                    Info.GroupInfo gi; info.dicItemGroupCategory.TryGetValue(g.Key, out gi);
                    foreach (KeyValuePair<int, Dictionary<int, Info.ItemLoadInfo>> c in g.Value)
                    {
                        Info.CategoryInfo ci = null;
                        if (gi != null) gi.dicCategory.TryGetValue(c.Key, out ci);
                        string gname = gi != null ? gi.name : "", cname = ci != null ? ci.name : "";
                        foreach (KeyValuePair<int, Info.ItemLoadInfo> a in c.Value)
                        {
                            string hay = (gname + " / " + cname + " / " + a.Value.name).ToLowerInvariant();
                            if (qs.Length > 0 && hay.IndexOf(qs, StringComparison.Ordinal) < 0) continue;
                            Dictionary<string, object> m = new Dictionary<string, object>();
                            m["group"] = g.Key; m["category"] = c.Key; m["no"] = a.Key;
                            m["groupName"] = gname; m["categoryName"] = cname; m["name"] = a.Value.name;
                            list.Add(m);
                            if (list.Count >= limit) { r["items"] = list; r["truncated"] = true; return r; }
                        }
                    }
                }
                r["items"] = list;
                return r;
            }
            case "chara.accessories":
            {
                // list accessory slots (name from the list, parent, shown); show: {slot: bool} switches them like the
                // Studio accessory panel. Slots past 20 (MoreAccessories) are listed from the scene objects only.
                OCIChar oc = Chara(st, q);
                ChaControl c = oc.charInfo;
                Dictionary<string, object> show = q.ContainsKey("show") ? q["show"] as Dictionary<string, object> : null;
                if (show != null)
                    foreach (KeyValuePair<string, object> kv in show)
                        c.SetAccessoryState(int.Parse(kv.Key), kv.Value is bool ? (bool)kv.Value : Convert.ToString(kv.Value) == "true");
                List<object> list = new List<object>();
                ChaFileAccessory.PartsInfo[] parts = c.nowCoordinate.accessory.parts;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].type == 350) continue;   // ao_none
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["slot"] = i; m["type"] = parts[i].type; m["id"] = parts[i].id; m["parent"] = parts[i].parentKey;
                    ListInfoBase li = null;
                    try { li = Manager.Character.Instance.chaListCtrl.GetListInfo((ChaListDefine.CategoryNo)parts[i].type, parts[i].id); } catch (Exception) { }
                    m["name"] = li != null ? li.Name : "";
                    m["shown"] = i < c.fileStatus.showAccessory.Length && c.fileStatus.showAccessory[i];
                    m["object"] = c.objAccessory != null && i < c.objAccessory.Length && c.objAccessory[i] != null ? c.objAccessory[i].name : "";
                    list.Add(m);
                }
                r["accessories"] = list;
                List<object> extra = new List<object>();
                foreach (Transform t in c.GetComponentsInChildren<Transform>(true))
                    if (t.name.StartsWith("ca_slot") && t.childCount > 0)
                        extra.Add(t.name + " -> " + t.GetChild(0).name + (t.gameObject.activeInHierarchy ? "" : " (hidden)"));
                r["slotObjects"] = extra;
                return r;
            }
            case "chara.accessoryAxis":
            {
                {
                    Vector3 ax0, mz0, c0, an0;
                    AccessoryAxis(Chara(st, q), Int(q, "slot", 0), out ax0, out mz0, out c0, out an0);
                    r["axis"] = V(ax0); r["center"] = V(c0); r["muzzle"] = V(mz0); r["anchor"] = V(an0);
                    return r;
                }
            }
                // long axis of an accessory's mesh (e.g. a gun barrel) in world space, pointing away from the bone it
                // hangs on, and the far end ("muzzle"): lets scenes aim a held weapon

            case "chara.load":
            {
                // add a character from a card file (relative paths: UserData/chara/<female|male>/...)
                string path = Str(q, "path", "");
                string sx = q.ContainsKey("sex") ? Convert.ToString(q["sex"], CultureInfo.InvariantCulture) : "female";
                bool male = sx == "male" || sx == "0";
                if (!Path.IsPathRooted(path))
                    path = Path.GetFullPath(Path.Combine(Application.dataPath, "../UserData/chara/" + (male ? "male/" : "female/") + path));
                if (!File.Exists(path)) throw new Exception("card not found: " + path);
                HashSet<int> before = new HashSet<int>(st.dicObjectCtrl.Keys);
                if (male) st.AddMale(path); else st.AddFemale(path);
                List<object> added = new List<object>();
                foreach (KeyValuePair<int, ObjectCtrlInfo> kv in st.dicObjectCtrl)
                    if (!before.Contains(kv.Key)) added.Add(Describe(kv.Key, kv.Value));
                r["added"] = added;
                return r;
            }
            case "item.add":
            {
                HashSet<int> before = new HashSet<int>(st.dicObjectCtrl.Keys);
                st.AddItem(Int(q, "group", 0), Int(q, "category", 0), Int(q, "no", 0));
                List<object> added = new List<object>();
                foreach (KeyValuePair<int, ObjectCtrlInfo> kv in st.dicObjectCtrl)
                    if (!before.Contains(kv.Key)) added.Add(Describe(kv.Key, kv.Value));
                r["added"] = added;
                return r;
            }
            case "delete":
            {
                // Studio's own delete clears the selection in the same frame. Without that, a deleted node (or a
                // deleted child of it) stays selected, and the next selection change (select, chara.load, ...)
                // fails once on the destroyed node.
                ObjectCtrlInfo oci = Obj(st, q);
                TreeNodeObject node = oci.treeNodeObject;
                bool selected = false;
                foreach (TreeNodeObject s in st.treeNodeCtrl.selectNodes)
                    for (TreeNodeObject t = s; t != null && !selected; t = t.parent) if (t == node) selected = true;
                st.treeNodeCtrl.DeleteNode(node);
                if (selected) st.treeNodeCtrl.SelectSingle(null);
                return r;
            }
            case "toolbar.list":
            {
                // the Studio left toolbar's buttons (KKAPI): id, tooltip, owner plugin, toggle state, and where the
                // button is on the screen ([x, y, w, h] in pixels from the top left)
                List<object> list = new List<object>();
                foreach (KKAPI.Studio.UI.Toolbars.ToolbarControlBase b in KKAPI.Studio.UI.Toolbars.ToolbarManager.GetAllButtons(false))
                {
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["id"] = b.ButtonID;
                    e["tip"] = b.HoverText;
                    e["owner"] = b.Owner != null ? b.Owner.Info.Metadata.GUID : "";
                    KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle t = b as KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle;
                    if (t != null) e["on"] = t.Toggled.Value;
                    if (b.ButtonObject != null)
                    {
                        e["shown"] = b.ButtonObject.gameObject.activeInHierarchy;
                        // Canvas is in UnityEngine.UIModule, which the ScriptLoader compiler does not see: by reflection.
                        // An overlay canvas has its corners in screen pixels already; otherwise its camera projects them.
                        Type canvasType = Type.GetType("UnityEngine.Canvas, UnityEngine.UIModule");
                        Component canvas = canvasType != null ? b.ButtonObject.GetComponentInParent(canvasType) : null;
                        Camera cam = null;
                        if (canvas != null && Convert.ToInt32(canvasType.GetProperty("renderMode").GetValue(canvas, null)) != 0)
                            cam = canvasType.GetProperty("worldCamera").GetValue(canvas, null) as Camera;
                        Vector3[] c = new Vector3[4];
                        ((RectTransform)b.ButtonObject.transform).GetWorldCorners(c);
                        Vector3 lo = cam != null ? cam.WorldToScreenPoint(c[0]) : c[0], hi = cam != null ? cam.WorldToScreenPoint(c[2]) : c[2];
                        e["rect"] = new List<object> { lo.x, Screen.height - hi.y, hi.x - lo.x, hi.y - lo.y };
                    }
                    list.Add(e);
                }
                r["buttons"] = list;
                return r;
            }
            case "toolbar.click":
            {
                // press a toolbar button as a click would (e.g. open a plugin's window); on: true / false sets a toggle
                string id = Str(q, "id", "");
                foreach (KKAPI.Studio.UI.Toolbars.ToolbarControlBase b in KKAPI.Studio.UI.Toolbars.ToolbarManager.GetAllButtons(true))
                {
                    if (b.ButtonID != id) continue;
                    KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle t = b as KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle;
                    if (t != null)
                    {
                        t.Toggled.OnNext(q.ContainsKey("on") ? Bool(q, "on", true) : !t.Toggled.Value);
                        r["on"] = t.Toggled.Value;
                    }
                    else if (b.ButtonObject != null) b.ButtonObject.onClick.Invoke();
                    else throw new Exception("toolbar button " + id + " has no control yet");
                    return r;
                }
                throw new Exception("no toolbar button " + id + " (toolbar.list shows the ids)");
            }
            case "plugin.get":
            {
                MonoBehaviour p = Plugin(Str(q, "plugin", ""));
                Dictionary<string, object> vals = new Dictionary<string, object>();
                List<object> fields = q.ContainsKey("fields") ? q["fields"] as List<object> : null;
                foreach (System.Reflection.FieldInfo f in p.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (fields != null && !fields.Contains(f.Name)) continue;
                    object v = f.GetValue(p);
                    if (v is float || v is int || v is bool || v is string) vals[f.Name] = v;
                    else if (v is Vector3) vals[f.Name] = V((Vector3)v);
                }
                r["values"] = vals;
                return r;
            }
            case "plugin.set":
            {
                MonoBehaviour p = Plugin(Str(q, "plugin", ""));
                Dictionary<string, object> values = q.ContainsKey("values") ? q["values"] as Dictionary<string, object> : null;
                if (values == null) throw new Exception("values: {field: value} required");
                List<object> done = new List<object>();
                foreach (KeyValuePair<string, object> kv in values)
                {
                    System.Reflection.FieldInfo f = p.GetType().GetField(kv.Key, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (f == null) throw new Exception("no public field " + kv.Key);
                    f.SetValue(p, ConvertArg(kv.Value, f.FieldType));
                    done.Add(kv.Key);
                }
                r["set"] = done;
                return r;
            }
            case "plugin.call":
            {
                // any method (public or not) by name, with simple arguments; e.g. WetLab AttachSelected, RagdollLab KnockDown
                MonoBehaviour p = Plugin(Str(q, "plugin", ""));
                string mname = Str(q, "method", "");
                List<object> args = q.ContainsKey("args") ? q["args"] as List<object> : new List<object>();
                System.Reflection.MethodInfo hit = null;
                foreach (System.Reflection.MethodInfo m in p.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    if (m.Name == mname && m.GetParameters().Length == args.Count) { hit = m; break; }
                if (hit == null) throw new Exception("no method " + mname + " with " + args.Count + " argument(s)");
                System.Reflection.ParameterInfo[] ps = hit.GetParameters();
                object[] call = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++) call[i] = ConvertArg(args[i], ps[i].ParameterType);
                object ret = hit.Invoke(p, call);
                if (ret is float || ret is int || ret is bool || ret is string) r["result"] = ret;
                return r;
            }
            case "chara.animeOption":
            {
                // Items an animation brings along (e.g. the battle rifle animations' AK-74), parented to bones.
                // visible: show/hide them (same as the anime panel's option toggle).
                OCIChar oc = Chara(st, q);
                if (oc.optionItemCtrl == null) throw new Exception("character has no option item controller");
                if (q.ContainsKey("visible")) oc.optionItemCtrl.visible = Bool(q, "visible", true);
                List<object> items = new List<object>();
                foreach (OptionItemCtrl.ItemInfo ii in oc.optionItemCtrl.HashItem)
                {
                    if (ii.gameObject == null) continue;
                    List<Transform> parts = new List<Transform>();
                    if (ii.child.Count > 0) foreach (OptionItemCtrl.ChildInfo ci in ii.child) { if (ci.obj != null) parts.Add(ci.obj.transform); }
                    else parts.Add(ii.gameObject.transform);
                    foreach (Transform t in parts)
                    {
                        Dictionary<string, object> m = new Dictionary<string, object>();
                        m["name"] = t.name;
                        m["bone"] = t.parent != null ? t.parent.name : "";
                        m["localPos"] = V(t.localPosition); m["localRot"] = V(t.localEulerAngles);
                        m["pos"] = V(t.position); m["rot"] = V(t.eulerAngles); m["lossyScale"] = V(t.lossyScale);
                        items.Add(m);
                    }
                }
                r["visible"] = oc.optionItemCtrl.visible;
                r["items"] = items;
                return r;
            }
            case "item.attach":
            {
                // Make an item follow a character bone every frame (after animation, before rendering).
                // pos/rot: offset in the bone's frame (Euler degrees). off:true detaches.
                ObjectCtrlInfo io = Obj(st, q);
                OCIItem item = io as OCIItem;
                if (item == null || item.objectItem == null) throw new Exception("key is not an item");
                attachments.RemoveAll(a => a.item == item);
                if (Bool(q, "off", false)) { r["attached"] = attachments.Count; return r; }
                Dictionary<string, object> cq = new Dictionary<string, object>();
                cq["key"] = q.ContainsKey("chara") ? q["chara"] : (object)0.0;
                OCIChar oc = Chara(st, cq);
                string bone = Str(q, "bone", "");
                Transform bt = FindBone(oc.charInfo.transform, bone);
                if (bt == null) throw new Exception("bone not found: " + bone);
                Attachment at = new Attachment();
                at.item = item; at.chara = oc; at.bone = bt;
                at.pos = q.ContainsKey("pos") ? Vec(q["pos"]) : Vector3.zero;
                at.rot = Quaternion.Euler(q.ContainsKey("rot") ? Vec(q["rot"]) : Vector3.zero);
                attachments.Add(at);
                ApplyAttachments();
                r["attached"] = attachments.Count;
                r["worldPos"] = V(item.objectItem.transform.position);
                return r;
            }
            case "item.parent":
            {
                // Studio's own parenting: put an object under a character's accessory point node (e.g. "右手"),
                // like dragging it there in the workspace. Saved with the scene, follows the motion, and the
                // user can still move it with the guide (local to the point). point omitted / "" = unparent.
                ObjectCtrlInfo io = Obj(st, q);
                attachments.RemoveAll(a => a.item == io);
                string point = Str(q, "point", "");
                TreeNodeObject target = null;
                if (point != "")
                {
                    Dictionary<string, object> cq = new Dictionary<string, object>();
                    cq["key"] = q.ContainsKey("chara") ? q["chara"] : (object)0.0;
                    OCIChar oc = Chara(st, cq);
                    foreach (KeyValuePair<TreeNodeObject, int> kv in oc.dicAccessoryPoint)
                    {
                        string nm = AIChara.ChaAccessoryDefine.AccessoryParentName[kv.Value];
                        if (nm == point || kv.Value.ToString() == point) { target = kv.Key; break; }
                    }
                    if (target == null) throw new Exception("accessory point not found: " + point);
                }
                st.treeNodeCtrl.SetParent(io.treeNodeObject, target);
                r["parent"] = io.parentInfo != null && io.parentInfo.treeNodeObject != null ? io.parentInfo.treeNodeObject.textName : "";
                r["local"] = V(io.objectInfo.changeAmount.pos);
                r["localRot"] = V(io.objectInfo.changeAmount.rot);
                if (io.guideObject != null && io.guideObject.transformTarget != null)
                    r["bone"] = io.guideObject.transformTarget.parent != null ? io.guideObject.transformTarget.parent.name : "";
                return r;
            }
            case "pose.load":
            {
                // Kinema pose file (UserData/studio/pose/.../*.dat, relative or absolute): animation + IK/FK state, as the pose panel loads it.
                OCIChar oc = Chara(st, q);
                string path = Str(q, "path", "");
                if (!Path.IsPathRooted(path)) path = Path.GetFullPath(Path.Combine(Application.dataPath, "../UserData/studio/pose/" + path));
                if (!File.Exists(path)) throw new Exception("pose file not found: " + path);
                r["sexMatches"] = PauseCtrl.CheckIdentifyingCode(path, oc.charInfo.sex);
                r["loaded"] = PauseCtrl.Load(oc, path);
                r["name"] = PauseCtrl.LoadName(path);
                r["object"] = Describe(Int(q, "key", -1), oc);
                return r;
            }
            case "ragdoll.build":
            {
                // RagdollLab: ragdoll this character (active ragdoll that keeps following its animation until hit)
                MonoBehaviour lab = Plugin("RagdollLab");
                OCIChar oc = Chara(st, q);
                r["built"] = lab.GetType().GetMethod("BuildFor").Invoke(lab, new object[] { oc });
                // the room floor goes on the map surface under the character (RagdollLab guesses it from the pose)
                if (Bool(q, "mapFloor", true))
                {
                    Vector3 c = oc.charInfo.transform.position;
                    float best = MapFloorUnder(c);
                    if (!float.IsNaN(best))
                    {
                        // optional room bounds in world x/z (e.g. the floor strip between a walkway step and the rails)
                        if (q.ContainsKey("room"))
                        {
                            List<object> b = q["room"] as List<object>;   // [xmin, xmax, zmin, zmax]
                            float x0 = (float)Num(b[0], 0), x1 = (float)Num(b[1], 0), z0 = (float)Num(b[2], 0), z1 = (float)Num(b[3], 0);
                            lab.GetType().GetField("roomSize").SetValue(lab, new Vector3(x1 - x0, 40f, z1 - z0));
                            c = new Vector3((x0 + x1) * 0.5f, c.y, (z0 + z1) * 0.5f);
                        }
                        lab.GetType().GetMethod("SetRoomFloor").Invoke(lab, new object[] { new Vector3(c.x, 0f, c.z), best });
                        r["floor"] = best;
                    }
                }
                return r;
            }
            case "ragdoll.lock":
            {
                // lock ragdoll joints of parts starting with these prefixes ("upperarm,forearm,hand")
                MonoBehaviour lab = Plugin("RagdollLab");
                r["locked"] = lab.GetType().GetMethod("LockParts").Invoke(lab, new object[] { Chara(st, q), Str(q, "parts", "upperarm,forearm,hand") });
                return r;
            }
            case "ragdoll.tie":
            {
                MonoBehaviour lab = Plugin("RagdollLab");
                r["tied"] = lab.GetType().GetMethod("TieParts").Invoke(lab, new object[] { Chara(st, q), Str(q, "a", "hand_L"), Str(q, "b", "hand_R") });
                return r;
            }
            case "ragdoll.pin":
            {
                // pins a part where it is; "follow": an object key whose movement the pin follows (snap: to its origin)
                MonoBehaviour lab = Plugin("RagdollLab");
                ObjectCtrlInfo follow = q.ContainsKey("follow") ? Obj(st, new Dictionary<string, object> { { "key", q["follow"] } }) : null;
                r["pinned"] = lab.GetType().GetMethod("PinPart").Invoke(lab, new object[] { Chara(st, q), Str(q, "part", "hand_R"), follow, Bool(q, "snap", false) });
                return r;
            }
            case "ragdoll.unpin":
            {
                MonoBehaviour lab = Plugin("RagdollLab");
                OCIChar oc = q.ContainsKey("key") ? Chara(st, q) : null;
                r["unpinned"] = lab.GetType().GetMethod("Unpin").Invoke(lab, new object[] { oc });
                return r;
            }
            case "ragdoll.follower":
            {
                // a Studio folder that copies the part's pose every frame (parent items under it); returns its key
                MonoBehaviour lab = Plugin("RagdollLab");
                int fk = (int)lab.GetType().GetMethod("AddFollower").Invoke(lab, new object[] { Chara(st, q), Str(q, "part", "hand_R") });
                if (fk < 0) throw new Exception("no ragdoll part " + Str(q, "part", "hand_R") + " on that character (ragdoll.parts lists them)");
                r["folder"] = fk;
                return r;
            }
            case "ragdoll.turn":
            {
                // torque turning the torso front toward dir (default up = onto the back)
                MonoBehaviour lab = Plugin("RagdollLab");
                Vector3 dir = q.ContainsKey("dir") ? Vec(q["dir"]) : Vector3.up;
                r["ok2"] = lab.GetType().GetMethod("TurnFront").Invoke(lab, new object[] { Chara(st, q), dir, Flt(q, "strength", 2f) });
                return r;
            }
            case "ragdoll.settle":
            {
                // for `seconds`, steer the torso front toward dir (default up: lie on the back)
                MonoBehaviour lab = Plugin("RagdollLab");
                Vector3 dir = q.ContainsKey("dir") ? Vec(q["dir"]) : Vector3.up;
                r["ok2"] = lab.GetType().GetMethod("SettleFront").Invoke(lab, new object[] { Chara(st, q), dir,
                    Flt(q, "seconds", 2f), Flt(q, "gain", 6f), Flt(q, "damp", 1.5f) });
                return r;
            }
            case "record.start":
            {
                // path: raw output (RGBA, bottom-up rows); a .txt next to it gets one real-time stamp per frame
                if (rec != null) throw new Exception("already recording");
                rec = new Recorder();
                rec.w = Int(q, "width", 1280); rec.h = Int(q, "height", 720);
                rec.minDt = 1f / Mathf.Max(1f, Flt(q, "fps", 30f));
                rec.path = Str(q, "path", Path.Combine(BepInEx.Paths.GameRootPath, @"UserData\StudioBridge\rec.raw")); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(rec.path)));
                // fixed: the game advances exactly 1/fps per frame (slow motion while recording, smooth video)
                rec.fixedRate = Bool(q, "fixed", true);
                if (rec.fixedRate) { Time.captureFramerate = Mathf.RoundToInt(1f / rec.minDt); rec.minDt = 0f; }
                rec.Start();
                StartCoroutine(RecordLoop(rec));
                r["path"] = rec.path;
                return r;
            }
            case "record.stop":
            {
                if (rec == null) throw new Exception("not recording");
                Recorder rr = rec; rec = null;
                rr.stopping = true;
                if (rr.fixedRate) Time.captureFramerate = 0;
                r["frames"] = rr.frames; r["width"] = rr.w; r["height"] = rr.h; r["path"] = rr.path;
                return r;
            }
            case "camera.follow":
            {
                // target {key, bone[, offset]}; rot [pitch, yaw, 0]; dist; fov; seconds (0 = stop)
                float secs = Flt(q, "seconds", 2f);
                if (secs <= 0f) { followUntil = -1f; return r; }
                Dictionary<string, object> d = q["target"] as Dictionary<string, object>;
                OCIChar oc = Chara(st, d);
                followBone = FindBone(oc.charInfo.transform, Str(d, "bone", "cf_J_Spine02"));
                followOffset = d.ContainsKey("offset") ? Vec(d["offset"]) : Vector3.zero;
                followRot = q.ContainsKey("rot") ? Vec(q["rot"]) : new Vector3(60f, 0f, 0f);
                followDist = Flt(q, "dist", 12f); followFov = Flt(q, "fov", 0f); followAbove = Bool(q, "above", false);
                // rotTo / distTo: the camera moves there over the duration (orbit and pull back)
                followRot2 = q.ContainsKey("rotTo") ? Vec(q["rotTo"]) : followRot;
                followDist2 = Flt(q, "distTo", followDist);
                followStart = Time.time; followUntil = Time.time + secs; followSmoothFrame = -1;
                return r;
            }
            case "ragdoll.pusher":
            {
                // kinematic capsule from bone a to bone b of an animated character, pushing ragdolls (e.g. a kick)
                MonoBehaviour lab = Plugin("RagdollLab");
                r["count"] = lab.GetType().GetMethod("AddPusher").Invoke(lab, new object[] { Chara(st, q),
                    Str(q, "a", "cf_J_LegLow01_R"), Str(q, "b", "cf_J_Toes01_R"), Flt(q, "radius", 0.45f) });
                return r;
            }
            case "ragdoll.parts":
            {
                MonoBehaviour lab = Plugin("RagdollLab");
                string rep = (string)lab.GetType().GetMethod("PartReport").Invoke(lab, new object[] { Chara(st, q) });
                Dictionary<string, object> parts = new Dictionary<string, object>();
                foreach (string e in rep.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] kv = e.Split(':'); string[] n = kv[1].Split(',');
                    List<object> l = new List<object>();
                    foreach (string x in n) l.Add(double.Parse(x, CultureInfo.InvariantCulture));
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["pos"] = l.GetRange(0, 3); m["forward"] = l.GetRange(3, 3); m["up"] = l.GetRange(6, 3);
                    parts[kv[0]] = m;
                }
                r["parts"] = parts;
                return r;
            }
            case "ragdoll.push":
            {
                // impulse on a ragdoll part (pelvis, abdomen, chest, head, thigh_L ...); "local": [x,y,z] is in the
                // character's root frame (z = its facing direction), else "impulse" is world
                MonoBehaviour lab = Plugin("RagdollLab");
                OCIChar oc = Chara(st, q);
                Vector3 imp;
                if (q.ContainsKey("away"))
                {
                    // push away from another object (e.g. the kicker), horizontally, plus an optional lift
                    ObjectCtrlInfo from = Obj(st, new Dictionary<string, object> { { "key", q["away"] } });
                    Vector3 d = oc.charInfo.transform.position - from.guideObject.transformTarget.position;
                    Transform pb = FindBone(oc.charInfo.transform, Str(q, "bone", "cf_J_Spine02"));
                    if (pb != null) d = pb.position - from.guideObject.transformTarget.position;
                    d.y = 0f;
                    imp = d.normalized * Flt(q, "strength", 100f) + Vector3.up * Flt(q, "lift", 0f);
                }
                else imp = q.ContainsKey("local") ? oc.charInfo.transform.TransformDirection(Vec(q["local"])) : Vec(q["impulse"]);
                r["pushed"] = lab.GetType().GetMethod("PushPart").Invoke(lab, new object[] { oc, Str(q, "part", "chest"), imp });
                return r;
            }
            case "ragdoll.release":
            {
                MonoBehaviour lab = Plugin("RagdollLab");
                lab.GetType().GetMethod("ReleaseAllNow").Invoke(lab, null);
                return r;
            }
            case "fx.shoot":
            {
                // One RagdollLab bullet. from: item key (muzzle = the item's far end toward the target) or point [x,y,z];
                // target: {key, bone} or point; spread (degrees); kill: the first ragdoll hit dies.
                MonoBehaviour lab = Plugin("RagdollLab");
                Vector3 target = ShotPoint(st, q["target"]);
                Vector3 muzzle = FromPoint(st, q["from"], target);
                float spread = Flt(q, "spread", 0f);
                if (spread > 0f)
                {
                    Vector3 d = target - muzzle;
                    Vector2 rr = UnityEngine.Random.insideUnitCircle * spread;
                    d = Quaternion.LookRotation(d) * Quaternion.Euler(rr.y, rr.x, 0f) * Vector3.forward * d.magnitude;
                    target = muzzle + d;
                }
                // momentum: this bullet only (RagdollLab's own setting is restored, so FPS mode is unaffected)
                System.Reflection.FieldInfo mf = lab.GetType().GetField("bulletMomentum");
                object saved = mf != null ? mf.GetValue(lab) : null;
                if (mf != null && q.ContainsKey("momentum")) mf.SetValue(lab, Flt(q, "momentum", 181f));
                try { r["hits"] = lab.GetType().GetMethod("ShootFrom").Invoke(lab, new object[] { muzzle, target, Bool(q, "kill", false) }); }
                finally { if (mf != null) mf.SetValue(lab, saved); }
                r["muzzle"] = V(muzzle); r["target"] = V(target);
                return r;
            }
            case "wet.attach":
            {
                // WetLab on a character; with from/target the stain starts where that line (e.g. a bullet path:
                // from = gun item key or point, target = {key, bone}) first meets the character.
                MonoBehaviour wet = Plugin("WetLab");
                OCIChar oc = Chara(st, q);
                Vector3 a = Vector3.zero, b = Vector3.zero;
                if (q.ContainsKey("target"))
                {
                    b = ShotPoint(st, q["target"]);
                    a = FromPoint(st, q["from"], b);
                    b = a + (b - a) * 1.5f;   // past the aim point so the ray can reach the far side if needed
                }
                // drops and puddles land on the map floor under the character, not on WetLab's toe-based estimate
                float fl = MapFloorUnder(oc.charInfo.transform.position);
                wet.GetType().GetField("useFixedFloor").SetValue(wet, !float.IsNaN(fl));
                if (!float.IsNaN(fl)) { wet.GetType().GetField("fixedFloorY").SetValue(wet, fl); r["floor"] = fl; }
                if (q.ContainsKey("preset")) wet.GetType().GetMethod("ScriptPreset").Invoke(wet, new object[] { Str(q, "preset", "") });
                r["status"] = wet.GetType().GetMethod("ScriptAttach").Invoke(wet, new object[] { oc, a, b });
                return r;
            }
            case "obj.moveTo":
            {
                // walk an object over `time` seconds to `stop` units short of a target ({key, bone} or point),
                // on the same height, turning to face the target. stanceSpeed: the walk clip's foot speed at
                // animation speed 1 -> the character's animation speed is set so the feet do not slide.
                ObjectCtrlInfo oci = Obj(st, q);
                Vector3 tgt = ShotPoint(st, q["target"]);
                Vector3 from = oci.objectInfo.changeAmount.pos;
                Vector3 d = tgt - from; d.y = 0f;
                float stop = Flt(q, "stop", 4f), dur = Mathf.Max(0.05f, Flt(q, "time", 1f));
                Vector3 to = d.magnitude > stop ? from + d.normalized * (d.magnitude - stop) : from;
                Mover mv = new Mover();
                mv.oci = oci; mv.from = from; mv.to = to; mv.t0 = Time.time; mv.dur = dur;
                mv.yaw = d.sqrMagnitude > 1e-6f ? Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg : oci.objectInfo.changeAmount.rot.y;
                movers.RemoveAll(m => m.oci == oci);
                movers.Add(mv);
                OCIChar oc = oci as OCIChar;
                float stance = Flt(q, "stanceSpeed", 0f);
                if (oc != null && stance > 0f) oc.animeSpeed = Mathf.Clamp((to - from).magnitude / dur / stance, 0.2f, 2f);
                oci.objectInfo.changeAmount.rot = new Vector3(0f, mv.yaw, 0f);
                r["to"] = V(to); r["distance"] = (to - from).magnitude;
                return r;
            }
            case "wet.stain":
            {
                // prepare a named stain on the WetLab character (after wet.attach): preset, origin (from/target ray,
                // or the default crotch origin when no target), radius, spread (s), tail (s of dripping)
                MonoBehaviour wet = Plugin("WetLab");
                Vector3 a = Vector3.zero, b = Vector3.zero;
                if (q.ContainsKey("target"))
                {
                    b = ShotPoint(st, q["target"]);
                    a = FromPoint(st, q["from"], b);
                    b = a + (b - a) * 1.5f;
                }
                wet.GetType().GetField("skinDrips").SetValue(wet, Bool(q, "skinDrips", false));
                r["status"] = wet.GetType().GetMethod("ScriptDefineStain").Invoke(wet, new object[] {
                    Str(q, "name", "stain"), Str(q, "preset", "water"), a, b,
                    Flt(q, "radius", 1.8f), Flt(q, "spread", 3f), Flt(q, "tail", 5f) });
                return r;
            }
            case "wet.status":
            {
                MonoBehaviour wet = Plugin("WetLab");
                string list = (string)wet.GetType().GetMethod("ScriptStains").Invoke(wet, null);
                r["stains"] = new List<object>(list.Length > 0 ? list.Split(',') : new string[0]);
                return r;
            }
            case "wet.use":
            {
                MonoBehaviour wet = Plugin("WetLab");
                r["status"] = wet.GetType().GetMethod("ScriptUseStain").Invoke(wet, new object[] { Str(q, "name", ""), Bool(q, "start", true) });
                return r;
            }
            case "timeline.events":
            {
                // Commands run when Timeline playback passes their time (also after each loop wrap):
                // events: [{t, do: {cmd...}}]. clear:true removes them. Lets a looping shot drive plugins.
                if (Bool(q, "clear", false) || q.ContainsKey("events")) tlEvents.Clear();
                List<object> evs = q.ContainsKey("events") ? q["events"] as List<object> : null;
                if (evs != null)
                    foreach (object eo in evs)
                    {
                        Dictionary<string, object> e = eo as Dictionary<string, object>;
                        if (e == null || !e.ContainsKey("do")) continue;
                        TlEvent te = new TlEvent();
                        te.t = Flt(e, "t", 0f); te.cmd = e["do"] as Dictionary<string, object>;
                        tlEvents.Add(te);
                    }
                tlEvents.Sort((a, b) => a.t.CompareTo(b.t));
                tlPrev = -1f;
                r["events"] = tlEvents.Count;
                return r;
            }
            case "chara.bones":
            {
                // world positions (and forward) of named bones, e.g. to measure a walk cycle's stride
                OCIChar oc = Chara(st, q);
                List<object> names = q.ContainsKey("names") ? q["names"] as List<object> : null;
                if (names == null) throw new Exception("names: [bone, ...] required");
                Dictionary<string, object> res = new Dictionary<string, object>();
                foreach (object nm in names)
                {
                    Transform t = FindBone(oc.charInfo.objBodyBone != null ? oc.charInfo.objBodyBone.transform : oc.charInfo.transform, nm.ToString());
                    if (t == null) continue;
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["pos"] = V(t.position); m["forward"] = V(t.forward);
                    res[nm.ToString()] = m;
                }
                r["bones"] = res;
                r["time"] = Time.time;
                return r;
            }
            case "chara.skeleton":
            {
                // every transform under cf_J_Root: live local pos/rot/scale, pose relative to the character object, and the
                // bind (T) pose local rotation/position from the p_cf_anim / p_cm_anim prefab (for motion retargeting)
                OCIChar oc = Chara(st, q);
                ChaControl cha = oc.charInfo;
                Transform root = cha.objBodyBone != null ? cha.objBodyBone.transform : cha.transform;
                Transform charRoot = cha.transform;
                bool all = Bool(q, "all", false);
                string pname = cha.sex == 0 ? "p_cm_anim" : "p_cf_anim";
                GameObject prefab = null;
                try { prefab = CommonLib.LoadAsset<GameObject>("studio/base/00.unity3d", pname, false, "abdata"); } catch (Exception) { }
                if (prefab == null && cha.sex == 0)
                {
                    pname = "p_cf_anim";
                    try { prefab = CommonLib.LoadAsset<GameObject>("studio/base/00.unity3d", pname, false, "abdata"); } catch (Exception) { }
                }
                Dictionary<string, Transform> pmap = new Dictionary<string, Transform>();
                if (prefab != null) foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true)) if (!pmap.ContainsKey(t.name)) pmap[t.name] = t;
                List<object> bones = new List<object>();
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (!all && !(t.name.StartsWith("cf_J_") || t.name.StartsWith("cf_N_"))) continue;
                    Dictionary<string, object> b = new Dictionary<string, object>();
                    b["name"] = t.name; b["parent"] = t.parent != null ? t.parent.name : "";
                    b["lpos"] = V(t.localPosition); b["lrot"] = QL(t.localRotation); b["lscale"] = V(t.localScale);
                    b["pos"] = V(charRoot.InverseTransformPoint(t.position)); b["rot"] = QL(Quaternion.Inverse(charRoot.rotation) * t.rotation);
                    Transform p;
                    if (pmap.TryGetValue(t.name, out p)) { b["bindLrot"] = QL(p.localRotation); b["bindLpos"] = V(p.localPosition); b["bindLscale"] = V(p.localScale); }
                    bones.Add(b);
                }
                r["root"] = root.name; r["rootParent"] = root.parent != null ? root.parent.name : "";
                r["sex"] = cha.sex; r["prefab"] = prefab != null ? pname : null; r["bones"] = bones;
                r["charPos"] = V(charRoot.position); r["charRot"] = V(charRoot.eulerAngles); r["charScale"] = V(charRoot.lossyScale);
                return r;
            }
            case "ground.probe":
            {
                // Height of the map surface below each point. Studio maps often have no colliders, so the map's
                // meshes are baked once into a grid of upward-facing triangles and intersected with a vertical ray
                // starting `above` units over the reference height y (so ceilings are not found).
                float y0 = Flt(q, "y", 0f), above = Flt(q, "above", 6f), below = Flt(q, "below", 30f);
                Ground g = GroundIndex();
                List<object> pts = q.ContainsKey("points") ? q["points"] as List<object> : new List<object>();
                List<object> res = new List<object>();
                foreach (object po in pts)
                {
                    List<object> pl = po as List<object>;
                    if (pl == null || pl.Count < 2) continue;
                    float x = (float)Num(pl[0], 0), z = (float)Num(pl[pl.Count - 1], 0);
                    Vector3 n;
                    float h = g.Height(x, z, y0 + above, y0 - below, out n);
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["x"] = x; m["z"] = z;
                    if (float.IsNaN(h)) m["y"] = null; else { m["y"] = Math.Round(h, 4); m["normal"] = V(n); }
                    res.Add(m);
                }
                r["hits"] = res;
                r["triangles"] = g.triCount;
                return r;
            }
            case "debug.renderercount":
            {
                Dictionary<string, object> m = new Dictionary<string, object>();
                foreach (Renderer rd in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    string k = rd.GetType().Name + "@" + rd.gameObject.layer + (rd.enabled ? "" : "(off)");
                    object c; m[k] = (m.TryGetValue(k, out c) ? (int)c : 0) + 1;
                }
                r["counts"] = m;
                Studio.Map map = Studio.Map.Instance;
                r["mapRoot"] = map != null && map.MapRoot != null ? map.MapRoot.name : "";
                if (map != null && map.MapRoot != null)
                {
                    Dictionary<string, object> mm = new Dictionary<string, object>();
                    int readable = 0, total = 0, colliders = map.MapRoot.GetComponentsInChildren<Collider>(true).Length;
                    foreach (Renderer rd in map.MapRoot.GetComponentsInChildren<Renderer>(true))
                    {
                        string k = rd.GetType().Name + "@" + rd.gameObject.layer + (rd.enabled && rd.gameObject.activeInHierarchy ? "" : "(off)");
                        object c; mm[k] = (mm.TryGetValue(k, out c) ? (int)c : 0) + 1;
                        MeshFilter mf = rd.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null) { total++; if (mf.sharedMesh.isReadable) readable++; }
                    }
                    r["mapRenderers"] = mm; r["mapMeshes"] = total; r["mapReadable"] = readable; r["mapColliders"] = colliders;
                    Bounds bb = new Bounds(); bool first = true;
                    foreach (Renderer rd in map.MapRoot.GetComponentsInChildren<Renderer>(false))
                    { if (first) { bb = rd.bounds; first = false; } else bb.Encapsulate(rd.bounds); }
                    r["mapBoundsMin"] = V(bb.min); r["mapBoundsMax"] = V(bb.max);
                }
                return r;
            }
            case "debug.renderers":
            {
                // what is drawn near a point: layer, mesh readability, shader, bounds (to plan ground probing)
                Vector3 pt = Vec(q["point"]); float rad = Flt(q, "radius", 20f);
                List<object> list = new List<object>();
                foreach (MeshRenderer mr in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
                {
                    if (!mr.enabled || !mr.gameObject.activeInHierarchy) continue;
                    Bounds b = mr.bounds;
                    if (b.SqrDistance(pt) > rad * rad) continue;
                    MeshFilter mf = mr.GetComponent<MeshFilter>();
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["name"] = mr.name; m["layer"] = mr.gameObject.layer;
                    m["readable"] = mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable;
                    m["shader"] = mr.sharedMaterial != null ? mr.sharedMaterial.shader.name : "";
                    m["tag"] = mr.sharedMaterial != null ? mr.sharedMaterial.GetTag("RenderType", true, "") : "";
                    m["min"] = V(b.min); m["max"] = V(b.max);
                    list.Add(m);
                    if (list.Count >= Int(q, "limit", 40)) break;
                }
                r["renderers"] = list;
                return r;
            }
            case "timeline.build":
            {
                string xml = BuildTimelineXml(st, q);
                TimelineLoad(xml, !Bool(q, "append", false));
                if (Bool(q, "play", true)) { TL().GetMethod("Stop").Invoke(null, null); TL().GetMethod("Play").Invoke(null, null); }
                r["xmlLength"] = xml.Length;
                if (Bool(q, "returnXml", false)) r["xml"] = xml;
                return r;
            }
            case "timeline.loadxml":
            {
                TimelineLoad(Str(q, "xml", ""), !Bool(q, "append", false));
                return r;
            }
            case "timeline.play": TL().GetMethod("Play").Invoke(null, null); return r;
            case "timeline.pause": TL().GetMethod("Pause").Invoke(null, null); return r;
            case "timeline.stop": TL().GetMethod("Stop").Invoke(null, null); return r;
            case "timeline.seek":
                TL().GetMethod("Seek").Invoke(null, new object[] { Flt(q, "t", 0f) });
                return r;
            case "timeline.clear":
            {
                TL().GetMethod("Stop").Invoke(null, null);
                TimelineLoad("<root duration=\"10\" />", true);
                return r;
            }
            case "timeline.status":
            {
                Type tl = TL();
                r["playing"] = tl.GetProperty("isPlaying").GetValue(null, null);
                r["time"] = tl.GetProperty("playbackTime").GetValue(null, null);
                r["duration"] = tl.GetProperty("duration").GetValue(null, null);
                object self = TLSelf();
                System.Collections.IDictionary inter = tl.GetField("_interpolables", BF).GetValue(self) as System.Collections.IDictionary;
                r["interpolables"] = inter != null ? inter.Count : -1;
                return r;
            }
            case "timeline.export":
            {
                object self = TLSelf();
                StringWriter sw = new StringWriter();
                System.Xml.XmlTextWriter w = new System.Xml.XmlTextWriter(sw);
                w.WriteStartElement("root");
                TL().GetMethod("SceneWrite", BF).Invoke(self, new object[] { "", w });
                w.WriteEndElement();
                w.Flush();
                r["xml"] = sw.ToString();
                return r;
            }
            case "dof":
                return DofCommand(q, r);
            case "capture":
            {
                int w = Int(q, "width", 1280), h = Int(q, "height", 720);
                string name = Str(q, "name", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                Directory.CreateDirectory(ShotDir);
                string path = Path.Combine(ShotDir, name + ".png");
                Capture(w, h, path);
                r["path"] = path;
                return r;
            }
        }
        if (cmd.StartsWith("clothes.") || cmd.StartsWith("acc.") || cmd.StartsWith("wear.") || cmd.StartsWith("bundle.") || cmd.StartsWith("coord.") || cmd.StartsWith("hair."))
            return WardrobeCommand(cmd, q, r, st);
        if (cmd.StartsWith("motion."))
        {
            // forwarded to the MotionLab script (external motion clips). motion.play also turns the character's
            // Studio FK/IK off (keepKinematics:true to leave them), since they would fight the written pose.
            if (cmd == "motion.play" && q.ContainsKey("key") && !Bool(q, "keepKinematics", false))
            {
                OCIChar oc = Chara(st, q);
                if (oc.oiCharInfo.enableIK) oc.ActiveKinematicMode(OICharInfo.KinematicMode.IK, false, true);
                if (oc.oiCharInfo.enableFK) oc.ActiveKinematicMode(OICharInfo.KinematicMode.FK, false, true);
            }
            MonoBehaviour lab = Plugin("MotionLab");
            object res = lab.GetType().GetMethod("Command").Invoke(lab, new object[] { q });
            Dictionary<string, object> rd = res as Dictionary<string, object>;
            if (rd != null) foreach (KeyValuePair<string, object> kv in rd) r[kv.Key] = kv.Value;
            return r;
        }
        throw new Exception("unknown cmd '" + cmd + "'");
    }

    static ObjectCtrlInfo Obj(Studio.Studio st, Dictionary<string, object> q)
    {
        int key = Int(q, "key", int.MinValue);
        ObjectCtrlInfo oci;
        if (key != int.MinValue && st.dicObjectCtrl.TryGetValue(key, out oci)) return oci;
        throw new Exception("object key not found (see scene.objects[].key)");
    }

    Dictionary<string, object> Describe(int key, ObjectCtrlInfo oci)
    {
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["key"] = key;
        m["name"] = oci.treeNodeObject != null ? oci.treeNodeObject.textName : "";
        m["kind"] = oci is OCIChar ? "chara" : oci is OCIItem ? "item" : oci is OCIFolder ? "folder" : oci is OCILight ? "light"
            : oci is OCICamera ? "camera" : oci.GetType().Name;
        if (oci.objectInfo != null)
        {
            m["pos"] = V(oci.objectInfo.changeAmount.pos);
            m["rot"] = V(oci.objectInfo.changeAmount.rot);
            m["scale"] = V(oci.objectInfo.changeAmount.scale);
        }
        if (oci.guideObject != null && oci.guideObject.transformTarget != null) m["worldPos"] = V(oci.guideObject.transformTarget.position);
        if (oci.parentInfo != null) m["parentName"] = oci.parentInfo.treeNodeObject != null ? oci.parentInfo.treeNodeObject.textName : "";
        OCIChar oc = oci as OCIChar;
        if (oc != null && oc.charInfo != null)
        {
            string full = "";
            try { full = oc.charInfo.fileParam.fullname; } catch (Exception) { }
            m["fullname"] = full;
            m["sex"] = oc.charInfo.sex;
            Dictionary<string, object> a = new Dictionary<string, object>();
            a["group"] = oc.oiCharInfo.animeInfo.group;
            a["category"] = oc.oiCharInfo.animeInfo.category;
            a["no"] = oc.oiCharInfo.animeInfo.no;
            a["name"] = AnimeName(oc.oiCharInfo.animeInfo.group, oc.oiCharInfo.animeInfo.category, oc.oiCharInfo.animeInfo.no);
            a["speed"] = oc.animeSpeed;
            Animator an = oc.charAnimeCtrl != null ? oc.charAnimeCtrl.animator : null;
            if (an != null)
            {
                AnimatorStateInfo si = an.GetCurrentAnimatorStateInfo(0);
                a["length"] = si.length;           // seconds at speed 1 (for charAnimationTime tracks)
                a["normalizedTime"] = si.normalizedTime;
            }
            m["anime"] = a;
        }
        return m;
    }

    static string AnimeName(int g, int c, int n)
    {
        Dictionary<int, Dictionary<int, Info.AnimeLoadInfo>> gd; Dictionary<int, Info.AnimeLoadInfo> cd; Info.AnimeLoadInfo ai;
        if (Info.Instance.dicAnimeLoadInfo.TryGetValue(g, out gd) && gd.TryGetValue(c, out cd) && cd.TryGetValue(n, out ai)) return ai.name;
        return "";
    }

    Dictionary<string, object> CameraState()
    {
        Studio.CameraControl cc = Studio.Studio.Instance.cameraCtrl;
        Dictionary<string, object> m = new Dictionary<string, object>();
        if (cc == null) return m;
        Studio.CameraControl.CameraData d = cc.Export();
        m["target"] = V(d.pos);
        m["rot"] = V(d.rotate);
        m["dist"] = -d.distance.z;
        m["fov"] = d.parse;
        Camera cam = Camera.main;
        if (cam != null) { m["worldPos"] = V(cam.transform.position); m["forward"] = V(cam.transform.forward); }
        return m;
    }

    static void Capture(int w, int h, string path)
    {
        Camera cam = Camera.main;
        if (cam == null) throw new Exception("no main camera");
        RenderTexture rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture prevTarget = cam.targetTexture, prevActive = RenderTexture.active;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Destroy(tex);
        }
        finally
        {
            cam.targetTexture = prevTarget;
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // ---------------------------------------------------------------- recording

    public class Recorder
    {
        public int w, h, frames, pending;
        public float minDt, last = -1f;
        public bool fixedRate;
        public string path;
        public volatile bool stopping;
        public RenderTexture rt;
        readonly Queue<KeyValuePair<double, byte[]>> q = new Queue<KeyValuePair<double, byte[]>>();
        Thread th;

        public void Start()
        {
            rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
            th = new Thread(Write); th.IsBackground = true; th.Start();
        }

        public void Push(double t, byte[] data) { lock (q) q.Enqueue(new KeyValuePair<double, byte[]>(t, data)); }

        void Write()
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 22))
            using (StreamWriter ts = new StreamWriter(path + ".txt"))
            {
                while (true)
                {
                    KeyValuePair<double, byte[]> item = default(KeyValuePair<double, byte[]>);
                    bool got = false;
                    lock (q) { if (q.Count > 0) { item = q.Dequeue(); got = true; } }
                    if (!got)
                    {
                        if (stopping && Interlocked.CompareExchange(ref pending, 0, 0) == 0) break;
                        Thread.Sleep(2); continue;
                    }
                    fs.Write(item.Value, 0, item.Value.Length);
                    ts.WriteLine(item.Key.ToString("0.0000", CultureInfo.InvariantCulture));
                }
            }
        }
    }

    Recorder rec;

    // Taps the main camera's final image (after its image effects, without the UI) instead of rendering again.
    public class RecTap : MonoBehaviour
    {
        public Recorder r;
        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            Graphics.Blit(src, dst);
            if (r == null || r.stopping) return;
            float now = r.fixedRate ? Time.time : Time.realtimeSinceStartup;
            if (r.last >= 0f && now - r.last < r.minDt * 0.95f) return;
            r.last = now;
            Graphics.Blit(src, r.rt);
            Interlocked.Increment(ref r.pending);
            double stamp = now;
            Recorder rr = r;
            UnityEngine.Rendering.AsyncGPUReadback.Request(r.rt, 0, TextureFormat.RGBA32, req =>
            {
                if (!req.hasError) { rr.Push(stamp, req.GetData<byte>().ToArray()); rr.frames++; }
                Interlocked.Decrement(ref rr.pending);
            });
        }
    }

    System.Collections.IEnumerator RecordLoop(Recorder r)
    {
        Camera cam = Camera.main;
        RecTap tap = cam != null ? cam.gameObject.AddComponent<RecTap>() : null;
        if (tap != null) tap.r = r;
        while (!r.stopping) yield return null;
        if (tap != null) Destroy(tap);
        yield return new WaitForSeconds(0.5f);
        r.rt.Release();
    }

    // ---------------------------------------------------------------- scripted movement

    class Mover { public ObjectCtrlInfo oci; public Vector3 from, to; public float t0, dur, yaw; }
    readonly List<Mover> movers = new List<Mover>();

    void RunMovers()
    {
        for (int i = movers.Count - 1; i >= 0; i--)
        {
            Mover m = movers[i];
            if (m.oci == null || m.oci.objectInfo == null) { movers.RemoveAt(i); continue; }
            float f = Mathf.Clamp01((Time.time - m.t0) / m.dur);
            m.oci.objectInfo.changeAmount.pos = Vector3.Lerp(m.from, m.to, f);
            if (f >= 1f) movers.RemoveAt(i);
        }
    }

    // ---------------------------------------------------------------- Timeline events and shots

    class TlEvent { public float t; public Dictionary<string, object> cmd; }
    readonly List<TlEvent> tlEvents = new List<TlEvent>();
    float tlPrev = -1f;

    void RunTimelineEvents()
    {
        if (tlEvents.Count == 0) return;
        Type tl;
        bool playing;
        float now;
        try
        {
            tl = TL();
            playing = (bool)tl.GetProperty("isPlaying").GetValue(null, null);
            now = (float)tl.GetProperty("playbackTime").GetValue(null, null);
        }
        catch (Exception) { return; }
        if (!playing) { tlPrev = -1f; return; }
        float prev = tlPrev;
        tlPrev = now;
        foreach (TlEvent e in tlEvents)
        {
            bool due = now >= prev ? (e.t > prev && e.t <= now) : (e.t > prev || e.t <= now);
            if (!due) continue;
            try { Run(e.cmd); }
            catch (Exception ex) { Log.LogWarning("timeline event at " + e.t + " failed: " + ex.Message); }
        }
    }

    // long axis of a character's accessory (e.g. a gun barrel) pointing away from its bone, and its far end
    static void AccessoryAxis(OCIChar oc, int slotNo, out Vector3 ax, out Vector3 muzzle, out Vector3 c, out Vector3 anchor)
    {
        string slot = "ca_slot" + slotNo.ToString("00");
        Transform root = null;
        foreach (Transform t in oc.charInfo.GetComponentsInChildren<Transform>(true)) if (t.name == slot) { root = t; break; }
        if (root == null) throw new Exception(slot + " not found");
        List<Vector3> pts = new List<Vector3>();
        Mesh tmp = new Mesh();
        foreach (Renderer rd in root.GetComponentsInChildren<Renderer>(false))
        {
            Vector3[] vs; Matrix4x4 m;
            SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
            if (smr != null) { smr.BakeMesh(tmp); vs = tmp.vertices; m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one); }
            else
            {
                MeshFilter mf = rd.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                m = rd.transform.localToWorldMatrix;
                if (mf.sharedMesh.isReadable) vs = mf.sharedMesh.vertices;
                else
                {
                    // unreadable mesh: its local bounding box, sampled along the edges
                    Bounds bb = mf.sharedMesh.bounds; List<Vector3> bl = new List<Vector3>();
                    for (int i = 0; i < 8; i++)
                        for (int k = 0; k <= 10; k++)
                        {
                            float f = k / 10f;
                            Vector3 cr = new Vector3((i & 1) == 0 ? bb.min.x : bb.max.x, (i & 2) == 0 ? bb.min.y : bb.max.y, (i & 4) == 0 ? bb.min.z : bb.max.z);
                            bl.Add(Vector3.Lerp(bb.center, cr, f));
                        }
                    vs = bl.ToArray();
                }
            }
            for (int i = 0; i < vs.Length; i += Math.Max(1, vs.Length / 4000)) pts.Add(m.MultiplyPoint3x4(vs[i]));
        }
        UnityEngine.Object.Destroy(tmp);
        if (pts.Count < 3) throw new Exception("no readable mesh under " + slot);
        c = Vector3.zero; foreach (Vector3 p in pts) c += p; c /= pts.Count;
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (Vector3 p in pts) { Vector3 d = p - c; xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z; yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z; }
        ax = new Vector3(1, 0.3f, 0.2f).normalized;
        for (int it = 0; it < 50; it++)
            ax = new Vector3(xx * ax.x + xy * ax.y + xz * ax.z, xy * ax.x + yy * ax.y + yz * ax.z, xz * ax.x + yz * ax.y + zz * ax.z).normalized;
        anchor = root.parent != null ? root.parent.position : root.position;
        if (Vector3.Dot(c - anchor, ax) < 0f) ax = -ax;
        float best = float.MinValue; foreach (Vector3 p in pts) best = Mathf.Max(best, Vector3.Dot(p - c, ax));
        Vector3 far = Vector3.zero; int n = 0;
        foreach (Vector3 p in pts) if (Vector3.Dot(p - c, ax) > best - 0.3f) { far += p; n++; }
        muzzle = far / n;
    }

    // a shot's start: [x,y,z], an item key (its front end toward the target), {key, accessory} (a held
    // accessory's muzzle) or {key, bone[, offset]}
    static Vector3 FromPoint(Studio.Studio st, object spec, Vector3 target)
    {
        if (spec is List<object>) return Vec(spec);
        Dictionary<string, object> d = spec as Dictionary<string, object>;
        if (d != null && d.ContainsKey("accessory"))
        {
            Vector3 ax, mz, c, an;
            AccessoryAxis(Chara(st, d), Int(d, "accessory", 0), out ax, out mz, out c, out an);
            return mz;
        }
        if (d != null) return ShotPoint(st, d);
        return Muzzle(Obj(st, new Dictionary<string, object> { { "key", spec } }), target);
    }

    static Vector3 ShotPoint(Studio.Studio st, object spec)
    {
        if (spec is List<object>) return Vec(spec);
        Dictionary<string, object> d = spec as Dictionary<string, object>;
        if (d == null) throw new Exception("target: [x,y,z] or {key, bone}");
        OCIChar oc = Chara(st, d);
        Transform t = FindBone(oc.charInfo.transform, Str(d, "bone", "cf_J_Spine02"));
        if (t == null) throw new Exception("bone not found");
        return t.position + (d.ContainsKey("offset") ? Vec(d["offset"]) : Vector3.zero);
    }

    // The front end of an item (e.g. a rifle's muzzle) as seen toward a target: average of the item's bounding-box
    // corners that lie farthest along the item->target direction.
    static Vector3 Muzzle(ObjectCtrlInfo oci, Vector3 target)
    {
        OCIItem item = oci as OCIItem;
        if (item == null || item.objectItem == null) throw new Exception("from: not an item");
        Vector3 o = item.objectItem.transform.position, dir = (target - o).normalized;
        List<Vector3> pts = new List<Vector3>();
        foreach (Renderer rd in item.objectItem.GetComponentsInChildren<Renderer>())
        {
            Bounds b;
            SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
            if (smr != null) b = smr.localBounds;
            else
            {
                MeshFilter mf = rd.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                b = mf.sharedMesh.bounds;
            }
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                pts.Add(rd.transform.TransformPoint(c));
            }
        }
        if (pts.Count == 0) return o;
        float best = float.MinValue;
        foreach (Vector3 pt in pts) best = Mathf.Max(best, Vector3.Dot(pt - o, dir));
        Vector3 sum = Vector3.zero; int n = 0;
        foreach (Vector3 pt in pts) if (Vector3.Dot(pt - o, dir) > best - 0.6f) { sum += pt; n++; }
        return sum / n;
    }

    // ---------------------------------------------------------------- items following bones

    class Attachment
    {
        public OCIItem item;
        public OCIChar chara;
        public Transform bone;
        public Vector3 pos;
        public Quaternion rot;
    }

    static readonly List<Attachment> attachments = new List<Attachment>();
    static int attachFrame = -1;

    // ---- camera following a bone for a while (overrides the Timeline camera just before rendering)
    static Transform followBone;
    static Vector3 followOffset, followRot;
    static float followDist, followFov, followUntil = -1f;
    static bool followAbove;
    static Vector3 followRot2, followSmooth;
    static float followDist2, followStart;
    static int followSmoothFrame = -1;

    static void ApplyFollow(Camera cam)
    {
        if (followBone == null || Time.time > followUntil || cam != Camera.main) return;
        float u = Mathf.Clamp01((Time.time - followStart) / Mathf.Max(0.01f, followUntil - followStart));
        u = u * u * (3f - 2f * u);
        Vector3 want = followBone.position + followOffset;
        followSmooth = followSmoothFrame < 0 ? want : Vector3.Lerp(followSmooth, want, 1f - Mathf.Exp(-4f * Time.deltaTime));
        followSmoothFrame = Time.frameCount;
        Vector3 t = followSmooth;
        Quaternion q = Quaternion.Euler(Vector3.Lerp(followRot, followRot2, u));
        float dist = Mathf.Lerp(followDist, followDist2, u);
        if (followAbove)
        {
            // look straight down onto the bone from above, yaw = the body's long axis, so it never ends up
            // under another body or a leg
            followDist = Mathf.Max(followDist, 1f);
        }
        cam.transform.SetPositionAndRotation(t - q * Vector3.forward * dist, q);
        if (followFov > 0f) cam.fieldOfView = followFov;
    }

    static void OnPreCullAttach(Camera cam)
    {
        ApplyFollow(cam);
        DofPreCull(cam);
        if (attachments.Count == 0 || attachFrame == Time.frameCount) return;
        attachFrame = Time.frameCount;
        ApplyAttachments();
    }

    static void ApplyAttachments()
    {
        for (int i = attachments.Count - 1; i >= 0; i--)
        {
            Attachment a = attachments[i];
            if (a.item == null || a.item.objectItem == null || a.bone == null) { attachments.RemoveAt(i); continue; }
            Transform t = a.item.objectItem.transform;
            t.SetPositionAndRotation(a.bone.TransformPoint(a.pos), a.bone.rotation * a.rot);
        }
    }

    // ---------------------------------------------------------------- map ground (mesh based)

    class Ground
    {
        public int mapNo = int.MinValue;
        public Vector3[] v;          // triangle corners, 3 per triangle (world space)
        public Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        public float cell = 4f;
        public int triCount;

        static long Key(int ix, int iz) { return ((long)ix << 32) ^ (uint)iz; }

        public void Add(Vector3 a, Vector3 b, Vector3 c, List<Vector3> store)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            if (n.sqrMagnitude < 1e-10f) return;
            n.Normalize();
            if (Mathf.Abs(n.y) < 0.3f) return;          // walls are never ground
            int x0 = Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) / cell), x1 = Mathf.FloorToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) / cell);
            int z0 = Mathf.FloorToInt(Mathf.Min(a.z, Mathf.Min(b.z, c.z)) / cell), z1 = Mathf.FloorToInt(Mathf.Max(a.z, Mathf.Max(b.z, c.z)) / cell);
            if ((x1 - x0 + 1) * (z1 - z0 + 1) > 4096) return;   // absurdly large triangle
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
                if (float.IsNaN(best) || y > best)
                {
                    best = y;
                    normal = Vector3.Cross(b - a, c - a).normalized;
                    if (normal.y < 0f) normal = -normal;
                }
            }
            return best;
        }
    }

    // lowest map surface within 2 units around a point (NaN if the map has no floor mesh there)
    static float MapFloorUnder(Vector3 c)
    {
        Vector3 n;
        Ground g = GroundIndex();
        float best = float.NaN;
        for (int i = -2; i <= 2; i++)
            for (int k = -2; k <= 2; k++)
            {
                float h = g.Height(c.x + i, c.z + k, c.y + 6f, c.y - 20f, out n);
                if (!float.IsNaN(h) && (float.IsNaN(best) || h < best)) best = h;
            }
        return best;
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
        Studio.Studio st = Studio.Studio.Instance;
        int mapNo = st.sceneInfo != null ? st.sceneInfo.mapInfo.no : -1;
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
                    int[] tris = mesh.GetTriangles(sm);
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                        g.Add(m.MultiplyPoint3x4(verts[tris[i]]), m.MultiplyPoint3x4(verts[tris[i + 1]]), m.MultiplyPoint3x4(verts[tris[i + 2]]), store);
                }
            }
            Destroy(tmp);
        }
        g.v = store.ToArray();
        g.triCount = g.v.Length / 3;
        ground = g;
        Log.LogInfo("ground index for map " + mapNo + ": " + g.triCount + " floor-facing triangles in " + g.cells.Count + " cells");
        return g;
    }

    // ---------------------------------------------------------------- Timeline (through reflection)

    const System.Reflection.BindingFlags BF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
        | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

    static Type tlType;

    static Type TL()
    {
        if (tlType != null) return tlType;
        foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = a.GetType("Timeline.Timeline");
            if (t != null && t.GetMethod("Play") != null) { tlType = t; return t; }
        }
        throw new Exception("Timeline plugin not found");
    }

    static object TLSelf()
    {
        object self = TL().GetField("_self", BF).GetValue(null);
        if (self == null) throw new Exception("Timeline not initialised (open Studio first)");
        return self;
    }

    // Replace (or add to) the timeline with interpolables read from XML, the way a scene load does.
    static void TimelineLoad(string xml, bool replace)
    {
        Type tl = TL();
        object self = TLSelf();
        System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        if (replace)
        {
            tl.GetMethod("Stop").Invoke(null, null);
            object inter = tl.GetField("_interpolables", BF).GetValue(self);
            inter.GetType().GetMethod("Clear").Invoke(inter, null);
            object tree = tl.GetField("_interpolablesTree", BF).GetValue(self);
            tree.GetType().GetMethod("Clear").Invoke(tree, null);
        }
        List<KeyValuePair<int, ObjectCtrlInfo>> dic = new List<KeyValuePair<int, ObjectCtrlInfo>>(
            new SortedDictionary<int, ObjectCtrlInfo>(Studio.Studio.Instance.dicObjectCtrl));
        System.Reflection.MethodInfo load = null;
        foreach (System.Reflection.MethodInfo m in tl.GetMethods(BF))
            if (m.Name == "SceneLoad" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(System.Xml.XmlNode)) { load = m; break; }
        if (load == null) throw new Exception("Timeline.SceneLoad(XmlNode, List) not found");
        load.Invoke(self, new object[] { doc.FirstChild, dic });
        System.Reflection.MethodInfo view = tl.GetMethod("UpdateInterpolablesView", BF);
        if (view != null) view.Invoke(self, null);
    }

    static int ObjectIndex(Studio.Studio st, int key)
    {
        int i = 0;
        foreach (int k in new SortedDictionary<int, ObjectCtrlInfo>(st.dicObjectCtrl).Keys) { if (k == key) return i; i++; }
        throw new Exception("object key " + key + " not in scene");
    }

    // {duration, tracks: [{type, key?, ease?, keys: [{t, ...}]}]}
    // type: camera (target, rot (Euler), dist, fov), pos / rot / scale (object guide), anime (group, category, no),
    //       animeSpeed (v), eyes / mouth / leftHand / rightHand (v, int), eyesOpen / mouthOpen / blush (v, float),
    //       timeScale (v), visible (v, bool), raw (owner, id, attrs on interpolable, keys with attrs)
    string BuildTimelineXml(Studio.Studio st, Dictionary<string, object> q)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        StringBuilder sb = new StringBuilder();
        float duration = Flt(q, "duration", 10f);
        sb.Append("<root duration=\"").Append(duration.ToString(ci)).Append("\" blockLength=\"").Append(Flt(q, "blockLength", duration).ToString(ci))
          .Append("\" divisions=\"10\" timeScale=\"1\">");
        List<object> tracks = q.ContainsKey("tracks") ? q["tracks"] as List<object> : new List<object>();
        foreach (object to in tracks)
        {
            Dictionary<string, object> tr = to as Dictionary<string, object>;
            if (tr == null) continue;
            string type = Str(tr, "type", "");
            string ease = Str(tr, "ease", "inout");
            List<object> keys = tr.ContainsKey("keys") ? tr["keys"] as List<object> : new List<object>();
            string objAttr = tr.ContainsKey("key") ? " objectIndex=\"" + ObjectIndex(st, Int(tr, "key", 0)) + "\"" : "";
            if (type == "camera")
            {
                Interp(sb, "cameraOPos", "", keys, ease, k => Vec3Attr(k, "target"));
                Interp(sb, "cameraORot", "", keys, ease, k => QuatAttr(k, "rot"));
                Interp(sb, "cameraOZoom", "", keys, ease, k => k.ContainsKey("dist") ? FAttr(-Mathf.Abs(Flt(k, "dist", 10f))) : null);
                Interp(sb, "cameraFOV", "", keys, ease, k => k.ContainsKey("fov") ? FAttr(Flt(k, "fov", 23f)) : null);
                continue;
            }
            if (type == "dof") { DofTrackXml(sb, keys, ease); continue; }
            string id = null; Func<Dictionary<string, object>, string> val = null; string extra = objAttr;
            switch (type)
            {
                case "pos": id = "guideObjectPos"; extra += " guideObjectPath=\"\""; val = k => Vec3Attr(k, "v"); break;
                case "rot": id = "guideObjectRot"; extra += " guideObjectPath=\"" + GuidePath(st, tr) + "\""; val = k => QuatAttr(k, "v"); break;
                case "scale": id = "guideObjectScale"; extra += " guideObjectPath=\"\""; val = k => Vec3Attr(k, "v"); break;
                case "anime": id = "charAnimation"; ease = "step";
                    val = k => "valueCategory=\"" + Int(k, "category", 0) + "\" valueGroup=\"" + Int(k, "group", 0) + "\" valueNo=\"" + Int(k, "no", 0) + "\""; break;
                case "animeSpeed": id = "objectAnimationSpeed"; val = k => FAttr(Flt(k, "v", 1f)); break;
                case "eyes":
                {
                    id = "characterEyes"; ease = "step";
                    int[] ek = tr.ContainsKey("key") ? PatternKeys(Chara(st, tr), false) : new int[0];
                    bool rawIds = Bool(tr, "raw", false);
                    val = k => "value=\"" + (rawIds ? Int(k, "v", 0) : UiToId(ek, Int(k, "v", 0))) + "\"";
                    break;
                }
                case "mouth": id = "characterMouth"; ease = "step"; val = k => "value=\"" + Int(k, "v", 0) + "\""; break;
                case "leftHand": id = "characterLeftHand"; ease = "step"; val = k => "value=\"" + Int(k, "v", 0) + "\""; break;
                case "rightHand": id = "characterRightHand"; ease = "step"; val = k => "value=\"" + Int(k, "v", 0) + "\""; break;
                case "eyesOpen": id = "characterEyesOpen"; val = k => FAttr(Flt(k, "v", 1f)); break;
                case "mouthOpen": id = "characterMouthOpen"; val = k => FAttr(Flt(k, "v", 0f)); break;
                case "blush": id = "blush"; val = k => FAttr(Flt(k, "v", 0f)); break;
                case "timeScale": id = "timeScale"; extra = ""; val = k => FAttr(Flt(k, "v", 1f)); break;
                case "visible": id = "objectEnabled"; ease = "step"; val = k => "value=\"" + (Bool(k, "v", true) ? "true" : "false") + "\""; break;
                case "raw":
                    id = Str(tr, "id", ""); extra += " " + Str(tr, "attrs", "");
                    val = k => Str(k, "attrs", ""); break;
                default: throw new Exception("unknown track type " + type);
            }
            Interp(sb, id, extra, keys, ease, val, type == "raw" ? Str(tr, "owner", "Timeline") : "Timeline");
        }
        sb.Append("</root>");
        return sb.ToString();
    }

    // "bone": an FK bone of the track's character -> its guide object path below the character (Timeline's
    // guideObjectPath); FK must be on for that bone group while the track plays. No bone: the object itself.
    static string GuidePath(Studio.Studio st, Dictionary<string, object> tr)
    {
        string bone = Str(tr, "bone", "");
        if (bone == "") return "";
        OCIChar oc = Chara(st, tr);
        Transform root = oc.guideObject.transformTarget;
        foreach (OCIChar.BoneInfo b in oc.listBones)
        {
            if (BoneName(b) != bone) continue;
            Transform t = b.guideObject.transformTarget;
            List<string> parts = new List<string>();
            for (Transform x = t; x != null && x != root; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts.ToArray());
        }
        throw new Exception("FK bone not found: " + bone);
    }

    static void Interp(StringBuilder sb, string id, string extra, List<object> keys, string ease,
        Func<Dictionary<string, object>, string> val, string owner = "Timeline")
    {
        StringBuilder body = new StringBuilder();
        CultureInfo ci = CultureInfo.InvariantCulture;
        foreach (object ko in keys)
        {
            Dictionary<string, object> k = ko as Dictionary<string, object>;
            if (k == null) continue;
            string v = val(k);
            if (v == null) continue;
            body.Append("<keyframe time=\"").Append(Flt(k, "t", 0f).ToString(ci)).Append("\" ").Append(v).Append(">");
            string e = Str(k, "ease", ease);
            if (e == "inout") body.Append("<curveKeyframe time=\"0\" value=\"0\" inTangent=\"0\" outTangent=\"0\" /><curveKeyframe time=\"1\" value=\"1\" inTangent=\"0\" outTangent=\"0\" />");
            else if (e == "in") body.Append("<curveKeyframe time=\"0\" value=\"0\" inTangent=\"0\" outTangent=\"0\" /><curveKeyframe time=\"1\" value=\"1\" inTangent=\"2\" outTangent=\"2\" />");
            else if (e == "out") body.Append("<curveKeyframe time=\"0\" value=\"0\" inTangent=\"2\" outTangent=\"2\" /><curveKeyframe time=\"1\" value=\"1\" inTangent=\"0\" outTangent=\"0\" />");
            else if (e == "step") body.Append("<curveKeyframe time=\"0\" value=\"0\" inTangent=\"0\" outTangent=\"0\" /><curveKeyframe time=\"1\" value=\"0\" inTangent=\"0\" outTangent=\"0\" />");
            // "linear": no curve -> Timeline's default linear
            body.Append("</keyframe>");
        }
        if (body.Length == 0) return;
        sb.Append("<interpolable enabled=\"true\" owner=\"").Append(owner).Append("\" id=\"").Append(id).Append("\"").Append(extra)
          .Append(" bgColorR=\"1\" bgColorG=\"1\" bgColorB=\"1\" alias=\"\">").Append(body).Append("</interpolable>");
    }

    static string FAttr(float f) { return "value=\"" + f.ToString("R", CultureInfo.InvariantCulture) + "\""; }

    static string Vec3Attr(Dictionary<string, object> k, string name)
    {
        if (!k.ContainsKey(name)) return null;
        Vector3 v = Vec(k[name]);
        CultureInfo ci = CultureInfo.InvariantCulture;
        return "valueX=\"" + v.x.ToString("R", ci) + "\" valueY=\"" + v.y.ToString("R", ci) + "\" valueZ=\"" + v.z.ToString("R", ci) + "\"";
    }

    static string QuatAttr(Dictionary<string, object> k, string name)
    {
        if (!k.ContainsKey(name)) return null;
        Quaternion qt = Quaternion.Euler(Vec(k[name]));
        CultureInfo ci = CultureInfo.InvariantCulture;
        return "valueX=\"" + qt.x.ToString("R", ci) + "\" valueY=\"" + qt.y.ToString("R", ci) + "\" valueZ=\"" + qt.z.ToString("R", ci)
            + "\" valueW=\"" + qt.w.ToString("R", ci) + "\"";
    }

    static Transform FindBone(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++) { Transform r = FindBone(t.GetChild(i), name); if (r != null) return r; }
        return null;
    }

    static readonly string[] ClothesKinds = { "top", "bot", "inner_t", "inner_b", "gloves", "panst", "socks", "shoes" };

    static OCIChar Chara(Studio.Studio st, Dictionary<string, object> q)
    {
        OCIChar oc = Obj(st, q) as OCIChar;
        if (oc == null || oc.charInfo == null) throw new Exception("not a character");
        return oc;
    }

    static string BoneName(OCIChar.BoneInfo b)
    {
        return b.guideObject != null && b.guideObject.transformTarget != null ? b.guideObject.transformTarget.name : "?";
    }

    // Studio's face UI lists eyebrow/eye patterns by position in the character's list category; ids have gaps.
    static int[] PatternKeys(OCIChar oc, bool eyebrow)
    {
        AIChara.ChaListDefine.CategoryNo cat = oc.charInfo.sex == 0
            ? (eyebrow ? AIChara.ChaListDefine.CategoryNo.custom_eyebrow_m : AIChara.ChaListDefine.CategoryNo.custom_eye_m)
            : (eyebrow ? AIChara.ChaListDefine.CategoryNo.custom_eyebrow_f : AIChara.ChaListDefine.CategoryNo.custom_eye_f);
        Dictionary<int, AIChara.ListInfoBase> info = Manager.Character.Instance.chaListCtrl.GetCategoryInfo(cat);
        int[] keys = new int[info.Count];
        info.Keys.CopyTo(keys, 0);
        return keys;
    }

    static int UiToId(int[] keys, int ui) { return keys.Length == 0 ? ui : keys[Mathf.Clamp(ui, 0, keys.Length - 1)]; }

    static int IdToUi(int[] keys, int id) { int i = Array.IndexOf(keys, id); return i < 0 ? -1 : i; }

    static Dictionary<string, object> FaceState(OCIChar oc)
    {
        ChaFileStatus fs = oc.charInfo.fileStatus;
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["eyebrow"] = IdToUi(PatternKeys(oc, true), fs.eyebrowPtn); m["eyes"] = IdToUi(PatternKeys(oc, false), fs.eyesPtn);
        m["eyebrowId"] = fs.eyebrowPtn; m["eyesId"] = fs.eyesPtn; m["mouth"] = fs.mouthPtn;
        m["eyesOpen"] = fs.eyesOpenMax; m["mouthOpen"] = oc.oiCharInfo.mouthOpen; m["blink"] = fs.eyesBlink;
        m["eyesLook"] = fs.eyesLookPtn; m["neckLook"] = fs.neckLookPtn; m["tears"] = fs.tearsRate; m["blush"] = fs.hohoAkaRate;
        return m;
    }

    // the live instance of one of the other lab scripts (old hot-reloaded copies are destroyed)
    static MonoBehaviour Plugin(string typeName)
    {
        foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
            if (mb != null && mb.GetType().Name == typeName) return mb;
        throw new Exception("plugin " + typeName + " not running");
    }

    static object ConvertArg(object v, Type t)
    {
        if (t == typeof(float)) return (float)Num(v, 0);
        if (t == typeof(int)) return (int)Math.Round(Num(v, 0));
        if (t == typeof(double)) return Num(v, 0);
        if (t == typeof(bool)) return v is bool ? (bool)v : (v != null && v.ToString().ToLowerInvariant() == "true");
        if (t == typeof(string)) return v != null ? v.ToString() : null;
        if (t == typeof(Vector3)) return Vec(v);
        throw new Exception("unsupported argument type " + t.Name);
    }

    static bool Bool(Dictionary<string, object> q, string k, bool def)
    {
        object v; if (!q.TryGetValue(k, out v) || v == null) return def;
        if (v is bool) return (bool)v;
        return v.ToString().ToLowerInvariant() == "true" || v.ToString() == "1";
    }

    // ---------------------------------------------------------------- argument helpers

    static string Str(Dictionary<string, object> q, string k, string def)
    {
        object v; return q.TryGetValue(k, out v) && v != null ? v.ToString() : def;
    }

    static double Num(object v, double def)
    {
        if (v is double) return (double)v;
        if (v is long) return (long)v;
        double d;
        return v != null && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : def;
    }

    static int Int(Dictionary<string, object> q, string k, int def)
    {
        object v; return q.TryGetValue(k, out v) ? (int)Math.Round(Num(v, def)) : def;
    }

    static float Flt(Dictionary<string, object> q, string k, float def)
    {
        object v; return q.TryGetValue(k, out v) ? (float)Num(v, def) : def;
    }

    static Vector3 Vec(object v)
    {
        List<object> l = v as List<object>;
        if (l == null || l.Count < 3) throw new Exception("expected [x, y, z]");
        return new Vector3((float)Num(l[0], 0), (float)Num(l[1], 0), (float)Num(l[2], 0));
    }

    static List<object> QL(Quaternion q)
    {
        List<object> l = new List<object>();
        l.Add(Math.Round(q.x, 6)); l.Add(Math.Round(q.y, 6)); l.Add(Math.Round(q.z, 6)); l.Add(Math.Round(q.w, 6));
        return l;
    }

    static List<object> V(Vector3 v)
    {
        List<object> l = new List<object>();
        l.Add(Math.Round(v.x, 4)); l.Add(Math.Round(v.y, 4)); l.Add(Math.Round(v.z, 4));
        return l;
    }
}

// Minimal JSON reader/writer (objects -> Dictionary<string, object>, arrays -> List<object>,
// numbers -> double/long, strings, bools, null).
public static class MiniJson
{
    public static object Parse(string s)
    {
        int i = 0;
        object v = Value(s, ref i);
        Ws(s, ref i);
        if (i != s.Length) throw new Exception("trailing characters at " + i);
        return v;
    }

    static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

    static object Value(string s, ref int i)
    {
        Ws(s, ref i);
        if (i >= s.Length) throw new Exception("unexpected end");
        char c = s[i];
        if (c == '{')
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            i++; Ws(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                Ws(s, ref i);
                string k = Text(s, ref i);
                Ws(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new Exception("expected ':' at " + i);
                i++;
                d[k] = Value(s, ref i);
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return d; }
                throw new Exception("expected ',' or '}' at " + i);
            }
        }
        if (c == '[')
        {
            List<object> l = new List<object>();
            i++; Ws(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(Value(s, ref i));
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return l; }
                throw new Exception("expected ',' or ']' at " + i);
            }
        }
        if (c == '"') return Text(s, ref i);
        if (s.Length - i >= 4 && s.Substring(i, 4) == "true") { i += 4; return true; }
        if (s.Length - i >= 5 && s.Substring(i, 5) == "false") { i += 5; return false; }
        if (s.Length - i >= 4 && s.Substring(i, 4) == "null") { i += 4; return null; }
        int st = i;
        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        string num = s.Substring(st, i - st);
        if (num.Length == 0) throw new Exception("unexpected character at " + st);
        long lv;
        if (num.IndexOfAny(new char[] { '.', 'e', 'E' }) < 0 && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv)) return lv;
        return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    static string Text(string s, ref int i)
    {
        if (s[i] != '"') throw new Exception("expected string at " + i);
        i++;
        StringBuilder sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            char e = s[i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                default: sb.Append(e); break;
            }
        }
        throw new Exception("unterminated string");
    }

    public static string Serialize(object o)
    {
        StringBuilder sb = new StringBuilder();
        Write(sb, o);
        return sb.ToString();
    }

    static void Write(StringBuilder sb, object o)
    {
        if (o == null) { sb.Append("null"); return; }
        if (o is string) { Quote(sb, (string)o); return; }
        if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
        if (o is float) { sb.Append(((float)o).ToString("R", CultureInfo.InvariantCulture)); return; }
        if (o is double) { sb.Append(((double)o).ToString("R", CultureInfo.InvariantCulture)); return; }
        if (o is int || o is long || o is byte) { sb.Append(Convert.ToInt64(o).ToString(CultureInfo.InvariantCulture)); return; }
        Dictionary<string, object> d = o as Dictionary<string, object>;
        if (d != null)
        {
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, object> kv in d)
            {
                if (!first) sb.Append(',');
                first = false;
                Quote(sb, kv.Key); sb.Append(':'); Write(sb, kv.Value);
            }
            sb.Append('}');
            return;
        }
        System.Collections.IEnumerable e = o as System.Collections.IEnumerable;
        if (e != null)
        {
            sb.Append('[');
            bool first = true;
            foreach (object x in e) { if (!first) sb.Append(','); first = false; Write(sb, x); }
            sb.Append(']');
            return;
        }
        Quote(sb, o.ToString());
    }

    static void Quote(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
