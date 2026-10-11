// Depth of field for StudioBridge (the rest of the partial class is StudioBridge.cs; ScriptLoader compiles both).
//
// Studio's depth of field is the legacy Standard Assets DepthOfField on the main camera, driven by the screen-effects
// panel (on/off, focal size, aperture; focus = the camera target or an item). This adds
//  - focus on a character (between the eyes by default, or any bone), an item, a point or a fixed distance, followed
//    every frame: DepthOfField.focalTransform is a proxy placed on the camera axis just before the camera culls;
//  - rack focus: the focal depth blends from one target to another while both keep moving;
//  - blur width relative to the frame height ("blur" = maxBlurSize on a 1080-line frame). maxBlurSize counts texels,
//    so a fixed value blurs ~3x more in a 1280x720 capture than on the 2400-line screen;
//  - sharp / full: how far behind the subject things stay sharp and where they are fully blurred, as multiples of
//    the focus distance, converted to focalSize / aperture;
//  - a Timeline interpolable (owner StudioBridge, id dof), so a shot can change focus and depth of field over time
//    and Studio's scene save keeps it.
//
// The shader's circle of confusion: coc(d) = clamp(A * |d - f| / d - focalSize, 0, 1) * maxBlurSize, with
// A = 1 / (1 - aperture) - 1 and d, f the depths of a pixel and of the focus. aperture 0 (A = 0) never blurs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using Studio;
using UnityEngine;
using StdDof = UnityStandardAssets.ImageEffects.DepthOfField;

public partial class StudioBridge
{
    public const string DofOwner = "StudioBridge";

    // what to keep in focus
    public class DofFocus
    {
        public string mode = "target";   // target (Studio camera target) | object | point | dist
        public int key = -1;              // object: Studio key
        public string bone = "";          // object: bone name; "" = characters between the eyes, items their bounds centre
        public Vector3 v;                 // object: world offset; point: world position; dist: v.x = distance from the camera

        public DofFocus Clone() { return (DofFocus)MemberwiseClone(); }

        public bool Same(DofFocus o)
        {
            return o != null && o.mode == mode && o.key == key && o.bone == bone && o.v == v;
        }
    }

    // one Timeline keyframe value (never modified: Timeline shares the reference when keys are copied)
    public class DofKey
    {
        public bool on = true, near = true;   // near: foreground blur spreads over sharp edges (Studio's default)
        public DofFocus focus = new DofFocus();
        public float size = 0.4f, aperture = 0.6f, blur = -1f;   // blur <= 0: leave maxBlurSize alone
    }

    static StdDof dofComp;
    static Transform dofProxy;
    static DofFocus dofFrom, dofTo;      // dofTo null: Studio places the focus (camera target or item)
    static float dofT0, dofDur;          // command rack focus, in game time (follows Time.captureFramerate)
    static string dofEase = "inout";
    static float dofFixed = -1f;         // >= 0: blend factor given by the Timeline
    static float dofBlur = -1f;          // > 0: maxBlurSize = dofBlur * frame lines / 1080
    static float dofLastDepth = -1f;
    static Dictionary<string, object> dofQuality;   // the component's quality settings when first seen
    static readonly Dictionary<string, Transform> dofBones = new Dictionary<string, Transform>();
    static readonly Dictionary<int, Renderer[]> dofRenderers = new Dictionary<int, Renderer[]>();
    static bool dofModelAdded;
    static int dofRegisterFrame;

    static StdDof DofComp()
    {
        if (dofComp != null) return dofComp;
        Camera cam = Camera.main;
        if (cam == null) return null;
        dofComp = cam.GetComponent<StdDof>();
        if (dofComp != null && dofQuality == null) dofQuality = DofQualityState(dofComp);
        return dofComp;
    }

    static Dictionary<string, object> DofQualityState(StdDof d)
    {
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["maxBlurSize"] = d.maxBlurSize; m["nearBlur"] = d.nearBlur; m["overlap"] = d.foregroundOverlap;
        m["highRes"] = d.highResolution; m["blurType"] = d.blurType == StdDof.BlurType.DX11 ? "dx11" : "disc";
        m["samples"] = d.blurSampleCount.ToString().ToLowerInvariant(); m["visualize"] = d.visualizeFocus;
        return m;
    }

    // ---------------------------------------------------------------- command

    // dof: on, focus, blend (seconds of rack focus), ease, size / aperture or sharp / full, blur, near, overlap, highRes,
    //      blurType (disc | dx11), samples (low | medium | high), visualize, release (focus back to Studio), reset
    Dictionary<string, object> DofCommand(Dictionary<string, object> q, Dictionary<string, object> r)
    {
        Studio.Studio st = Studio.Studio.Instance;
        StdDof d = DofComp();
        if (d == null) throw new Exception("the main camera has no DepthOfField component");
        if (Bool(q, "reset", false))
        {
            // Studio's defaults (screen effects panel) and the quality settings the component had when first seen
            st.sceneInfo.enableDepth = ScreenEffectDefine.DepthOfField;
            st.sceneInfo.depthFocalSize = ScreenEffectDefine.DepthOfFieldFocalSize;
            st.sceneInfo.depthAperture = ScreenEffectDefine.DepthOfFieldAperture;
            st.sceneInfo.depthForcus = -1;
            DofApplyQuality(d, dofQuality);
            dofBlur = -1f;
            DofRelease();
        }
        if (Bool(q, "release", false)) DofRelease();
        if (q.ContainsKey("on")) st.sceneInfo.enableDepth = Bool(q, "on", true);
        if (q.ContainsKey("sharp") || q.ContainsKey("full"))
        {
            float size, ap;
            SharpFull(Flt(q, "sharp", 0.1f), Flt(q, "full", 3f), out size, out ap);
            st.sceneInfo.depthFocalSize = size; st.sceneInfo.depthAperture = ap;
        }
        if (q.ContainsKey("size")) st.sceneInfo.depthFocalSize = Mathf.Clamp(Flt(q, "size", 0.4f), 0f, 2f);
        if (q.ContainsKey("aperture")) st.sceneInfo.depthAperture = Mathf.Clamp(Flt(q, "aperture", 0.6f), 0f, 0.99f);
        if (q.ContainsKey("blur")) dofBlur = Flt(q, "blur", -1f);
        DofApplyQuality(d, q);
        if (q.ContainsKey("focus"))
        {
            DofFocus f = ParseFocus(q["focus"]);
            DofSetFocus(f, Flt(q, "blend", 0f), Str(q, "ease", "inout"));
        }
        d.enabled = st.sceneInfo.enableDepth;
        d.focalSize = st.sceneInfo.depthFocalSize;
        d.aperture = st.sceneInfo.depthAperture;
        if (q.Count > 1) DofRefreshPanel();
        if (dofTo != null && dofProxy != null) d.focalTransform = dofProxy;
        r["dof"] = DofState();
        return r;
    }

    static void DofApplyQuality(StdDof d, Dictionary<string, object> q)
    {
        if (q == null) return;
        if (q.ContainsKey("maxBlurSize")) d.maxBlurSize = Flt(q, "maxBlurSize", d.maxBlurSize);
        if (q.ContainsKey("near")) d.nearBlur = Bool(q, "near", false);
        if (q.ContainsKey("nearBlur")) d.nearBlur = Bool(q, "nearBlur", false);
        if (q.ContainsKey("overlap")) d.foregroundOverlap = Flt(q, "overlap", 1f);
        if (q.ContainsKey("highRes")) d.highResolution = Bool(q, "highRes", false);
        if (q.ContainsKey("blurType")) d.blurType = Str(q, "blurType", "disc") == "dx11" ? StdDof.BlurType.DX11 : StdDof.BlurType.DiscBlur;
        if (q.ContainsKey("samples"))
        {
            string s = Str(q, "samples", "high");
            d.blurSampleCount = s == "low" ? StdDof.BlurSampleCount.Low : s == "medium" ? StdDof.BlurSampleCount.Medium : StdDof.BlurSampleCount.High;
        }
        if (q.ContainsKey("visualize")) d.visualizeFocus = Bool(q, "visualize", false);
    }

    // the screen-effects panel shows sceneInfo; its refresh also re-points the focus, which DofPreCull overrides again
    static void DofRefreshPanel()
    {
        try
        {
            SystemButtonCtrl sb = Studio.Studio.Instance.systemButtonCtrl;
            object info = sb != null ? typeof(SystemButtonCtrl).GetField("dofInfo", BF).GetValue(sb) : null;
            if (info != null) info.GetType().GetMethod("UpdateInfo").Invoke(info, null);
        }
        catch (Exception e) { Log.LogWarning("dof panel refresh: " + e.Message); }
    }

    static void DofSetFocus(DofFocus f, float blend, string ease)
    {
        DofFocus cur = dofTo;
        if (cur != null && dofFrom != null && dofProxy != null)
        {
            // a blend is under way: start the new one from where the focus is now
            cur = new DofFocus(); cur.mode = "point"; cur.v = dofProxy.position;
        }
        if (cur == null)
        {
            cur = new DofFocus();
            int k = Studio.Studio.Instance.sceneInfo.depthForcus;
            if (k >= 0) { cur.mode = "object"; cur.key = k; }
        }
        dofFrom = blend > 0f ? cur : null;
        dofTo = f;
        dofT0 = Time.time; dofDur = blend; dofEase = ease;
        dofFixed = -1f;
    }

    // hand the focus back to Studio (camera target, or the item chosen in the screen-effects panel)
    static void DofRelease()
    {
        dofFrom = dofTo = null; dofFixed = -1f;
        StdDof d = DofComp();
        if (d == null) return;
        try { Studio.Studio.Instance.systemButtonCtrl.SetDepthOfFieldForcus(Studio.Studio.Instance.sceneInfo.depthForcus); }
        catch (Exception) { d.focalTransform = Studio.Studio.Instance.cameraCtrl.targetObj; }
    }

    Dictionary<string, object> DofState()
    {
        StdDof d = DofComp();
        Dictionary<string, object> m = new Dictionary<string, object>();
        if (d == null) return m;
        m["on"] = d.enabled;
        m["size"] = Math.Round(d.focalSize, 4);
        m["aperture"] = Math.Round(d.aperture, 4);
        float a = 1f / (1f - Mathf.Min(d.aperture, 0.999f)) - 1f, s = d.focalSize;
        // behind the subject: sharp up to focus * (1 + sharp), fully blurred beyond focus * full; in front: sharp
        // from focus * nearSharp, fully blurred nearer than focus * nearFull (-1: never)
        m["sharp"] = a > s ? Math.Round(s / (a - s), 4) : -1.0;
        m["full"] = a > 1f + s ? Math.Round(1f / (1f - (1f + s) / a), 3) : -1.0;
        m["nearSharp"] = a > 0f ? Math.Round(1f / (1f + s / a), 4) : 0.0;
        m["nearFull"] = a > 0f ? Math.Round(1f / (1f + (1f + s) / a), 4) : 0.0;
        m["blur"] = dofBlur > 0f ? Math.Round(dofBlur, 3) : Math.Round(d.maxBlurSize * 1080f / Mathf.Max(1, Screen.height), 3);
        m["blurFollowsFrame"] = dofBlur > 0f;
        m["maxBlurSize"] = Math.Round(d.maxBlurSize, 3);
        foreach (KeyValuePair<string, object> kv in DofQualityState(d)) if (kv.Key != "maxBlurSize") m[kv.Key] = kv.Value;
        m["focus"] = dofTo != null ? FocusJson(dofTo) : (object)"studio";
        m["studioFocusItem"] = Studio.Studio.Instance.sceneInfo.depthForcus;
        m["driver"] = dofTo == null ? "studio" : dofFixed >= 0f ? "timeline" : "command";
        m["blending"] = dofFrom != null && (dofFixed < 0f || dofFixed < 1f);
        if (dofLastDepth >= 0f) m["focusDepth"] = Math.Round(dofLastDepth, 3);
        m["focalTransform"] = d.focalTransform != null ? d.focalTransform.name : "";
        return m;
    }

    // behind the subject, sharp up to focus * (1 + sharp) and fully blurred from focus * full
    static void SharpFull(float sharp, float full, out float size, out float aperture)
    {
        sharp = Mathf.Max(0f, sharp);
        float u = 1f / (1f + sharp) - 1f / Mathf.Max(full, 1.0001f);
        if (u < 0.02f) throw new Exception("full must be clearly larger than 1 + sharp");
        float a = 1f / u;
        size = Mathf.Min(2f, a * sharp / (1f + sharp));
        aperture = Mathf.Min(0.99f, a / (1f + a));
    }

    static DofFocus ParseFocus(object o)
    {
        DofFocus f = new DofFocus();
        if (o == null) return f;
        if (o is string)
        {
            if ((string)o == "target" || (string)o == "") return f;
            throw new Exception("focus: \"target\", a key, {key, bone, offset}, {point} or {dist}");
        }
        if (o is double || o is long) { f.mode = "object"; f.key = (int)Math.Round(Num(o, -1)); return f; }
        if (o is List<object>) { f.mode = "point"; f.v = Vec(o); return f; }
        Dictionary<string, object> m = o as Dictionary<string, object>;
        if (m == null) throw new Exception("bad focus");
        if (m.ContainsKey("key"))
        {
            f.mode = "object"; f.key = Int(m, "key", -1); f.bone = Str(m, "bone", "");
            if (m.ContainsKey("offset")) f.v = Vec(m["offset"]);
            if (!Studio.Studio.Instance.dicObjectCtrl.ContainsKey(f.key)) throw new Exception("focus: object key " + f.key + " not in scene");
        }
        else if (m.ContainsKey("point")) { f.mode = "point"; f.v = Vec(m["point"]); }
        else if (m.ContainsKey("dist")) { f.mode = "dist"; f.v = new Vector3(Flt(m, "dist", 10f), 0f, 0f); }
        return f;
    }

    static Dictionary<string, object> FocusJson(DofFocus f)
    {
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["mode"] = f.mode;
        if (f.mode == "object") { m["key"] = f.key; if (f.bone != "") m["bone"] = f.bone; if (f.v != Vector3.zero) m["offset"] = V(f.v); }
        else if (f.mode == "point") m["point"] = V(f.v);
        else if (f.mode == "dist") m["dist"] = f.v.x;
        return m;
    }

    // the script is being unloaded: the focus proxy goes away, so Studio takes the focus back
    static void DofShutdown()
    {
        StdDof d = dofComp;
        if (d != null && dofBlur > 0f) d.maxBlurSize = Mathf.Max(0.1f, dofBlur * Screen.height / 1080f);
        dofBlur = -1f;
        if (dofTo != null) DofRelease();
        if (dofProxy != null) UnityEngine.Object.Destroy(dofProxy.gameObject);
        dofProxy = null;
    }

    // ---------------------------------------------------------------- per frame

    static void DofPreCull(Camera cam)
    {
        StdDof d = DofComp();
        if (d == null || cam.gameObject != d.gameObject) return;
        if (dofBlur > 0f) d.maxBlurSize = Mathf.Max(0.1f, dofBlur * cam.pixelHeight / 1080f);
        if (dofTo == null) { dofLastDepth = -1f; return; }
        float f = 1f;
        if (dofFixed >= 0f) f = dofFixed;
        else if (dofFrom != null && dofDur > 0f)
        {
            f = Mathf.Clamp01((Time.time - dofT0) / dofDur);
            f = dofEase == "linear" ? f : dofEase == "in" ? f * f : dofEase == "out" ? 1f - (1f - f) * (1f - f) : f * f * (3f - 2f * f);
        }
        Transform ct = cam.transform;
        float depth = FocusDepth(dofTo, cam);
        if (f < 1f && dofFrom != null) depth = Mathf.Lerp(FocusDepth(dofFrom, cam), depth, f);
        else if (dofFixed < 0f) dofFrom = null;   // a command's rack focus has arrived
        depth = Mathf.Max(depth, cam.nearClipPlane + 0.05f);
        dofLastDepth = depth;
        if (dofProxy == null)
        {
            GameObject go = new GameObject("StudioBridgeFocus");
            UnityEngine.Object.DontDestroyOnLoad(go);
            dofProxy = go.transform;
        }
        dofProxy.position = ct.position + ct.forward * depth;
        if (d.focalTransform != dofProxy) d.focalTransform = dofProxy;
    }

    static float FocusDepth(DofFocus f, Camera cam)
    {
        if (f.mode == "dist") return f.v.x;
        return Vector3.Dot(FocusPoint(f, cam) - cam.transform.position, cam.transform.forward);
    }

    static Vector3 FocusPoint(DofFocus f, Camera cam)
    {
        Studio.Studio st = Studio.Studio.Instance;
        if (f.mode == "point") return f.v;
        if (f.mode == "dist") return cam.transform.position + cam.transform.forward * f.v.x;
        if (f.mode == "object")
        {
            ObjectCtrlInfo oci;
            Vector3 p;
            if (st.dicObjectCtrl.TryGetValue(f.key, out oci) && ObjectFocusPoint(f, oci, out p)) return p + f.v;
        }
        Transform t = st.cameraCtrl != null ? st.cameraCtrl.targetObj : null;
        return t != null ? t.position : cam.transform.position + cam.transform.forward * 10f;
    }

    static bool ObjectFocusPoint(DofFocus f, ObjectCtrlInfo oci, out Vector3 p)
    {
        p = Vector3.zero;
        OCIChar oc = oci as OCIChar;
        if (oc != null && oc.charInfo != null)
        {
            if (f.bone == "" || f.bone == "eyes")
            {
                Transform l = DofBone(f.key, oc.charInfo.transform, "cf_J_Eye_t_L"), r = DofBone(f.key, oc.charInfo.transform, "cf_J_Eye_t_R");
                if (l == null || r == null) return false;
                p = (l.position + r.position) * 0.5f;
                return true;
            }
            Transform b = DofBone(f.key, oc.charInfo.transform, f.bone);
            if (b == null) return false;
            p = b.position;
            return true;
        }
        OCIItem item = oci as OCIItem;
        if (item != null && item.objectItem != null)
        {
            if (f.bone != "")
            {
                Transform b = DofBone(f.key, item.objectItem.transform, f.bone);
                if (b == null) return false;
                p = b.position;
                return true;
            }
            Renderer[] rs;
            if (!dofRenderers.TryGetValue(f.key, out rs) || rs.Length == 0 || rs[0] == null)
                dofRenderers[f.key] = rs = item.objectItem.GetComponentsInChildren<Renderer>();
            bool any = false;
            Bounds bb = new Bounds();
            foreach (Renderer rd in rs)
            {
                if (rd == null || !rd.enabled) continue;
                if (!any) { bb = rd.bounds; any = true; } else bb.Encapsulate(rd.bounds);
            }
            p = any ? bb.center : item.objectItem.transform.position;
            return true;
        }
        if (oci.guideObject != null && oci.guideObject.transformTarget != null) { p = oci.guideObject.transformTarget.position; return true; }
        return false;
    }

    static Transform DofBone(int key, Transform root, string name)
    {
        string k = key + "/" + name;
        Transform t;
        if (dofBones.TryGetValue(k, out t) && t != null && t.IsChildOf(root)) return t;
        t = FindBone(root, name);
        dofBones[k] = t;
        return t;
    }

    // ---------------------------------------------------------------- Timeline interpolable

    // called from Update: registers "Depth of Field" with Timeline as soon as the Timeline plugin exists (it keeps
    // its model list from game start, so this is in place before a scene with dof keys is loaded)
    void DofRegister()
    {
        if (dofModelAdded || Time.frameCount < dofRegisterFrame) return;
        dofRegisterFrame = Time.frameCount + 30;
        Type tl;
        object self;
        try { tl = TL(); self = tl.GetField("_self", BF).GetValue(null); }
        catch (Exception) { return; }
        if (self == null) return;
        try
        {
            System.Reflection.Assembly asm = tl.Assembly;
            Type model = asm.GetType("Timeline.InterpolableModel"), del = asm.GetType("Timeline.InterpolableDelegate");
            // a previous load of this script left its model behind (its delegates belong to the old assembly)
            System.Collections.IList list = (System.Collections.IList)tl.GetField("_interpolableModelsList", BF).GetValue(self);
            for (int i = list.Count - 1; i >= 0; i--)
                if ((string)model.GetField("owner").GetValue(list[i]) == DofOwner) list.RemoveAt(i);
            System.Collections.IDictionary dict = (System.Collections.IDictionary)tl.GetField("_interpolableModelsDictionary", BF).GetValue(self);
            if (dict.Contains(DofOwner)) dict.Remove(DofOwner);

            System.Reflection.ConstructorInfo ctor = null;
            foreach (System.Reflection.ConstructorInfo c in model.GetConstructors())
                if (c.GetParameters().Length == 16 && c.GetParameters()[4].ParameterType == del) { ctor = c; break; }
            if (ctor == null) throw new Exception("InterpolableModel constructor not found");
            Delegate before = Delegate.CreateDelegate(del, typeof(StudioBridge).GetMethod("DofInterpolate", BF));
            Func<ObjectCtrlInfo, bool> compatible = oci => true;
            Func<ObjectCtrlInfo, object, object> getValue = (oci, p) => DofCurrentKey();
            Func<object, XmlNode, object> read = (p, node) => DofKeyFromXml(node);
            Action<object, XmlTextWriter, object> write = (p, w, o) => DofKeyToXml(w, o as DofKey);
            object m = ctor.Invoke(new object[] { DofOwner, "dof", null, "Depth of Field", before, null, compatible, getValue, read, write,
                null, null, null, false, null, null });
            tl.GetMethod("AddInterpolableModel", BF).Invoke(null, new object[] { m });
            dofModelAdded = true;
            Log.LogInfo("Timeline interpolable 'Depth of Field' registered");
            DofRebindTimeline(tl, self);
        }
        catch (Exception e) { dofModelAdded = true; Log.LogWarning("could not register the Timeline DOF interpolable: " + e); }
    }

    // existing dof interpolables still call the previous script's delegates: write the timeline out and read it back
    static void DofRebindTimeline(Type tl, object self)
    {
        System.Collections.IDictionary inter = tl.GetField("_interpolables", BF).GetValue(self) as System.Collections.IDictionary;
        if (inter == null) return;
        bool any = false;
        foreach (object it in inter.Values)
            if ((string)it.GetType().GetField("owner").GetValue(it) == DofOwner) { any = true; break; }
        if (!any) return;
        bool playing = (bool)tl.GetProperty("isPlaying").GetValue(null, null);
        float t = (float)tl.GetProperty("playbackTime").GetValue(null, null);
        StringWriter sw = new StringWriter();
        XmlTextWriter w = new XmlTextWriter(sw);
        w.WriteStartElement("root");
        tl.GetMethod("SceneWrite", BF).Invoke(self, new object[] { "", w });
        w.WriteEndElement();
        w.Flush();
        TimelineLoad(sw.ToString(), true);
        if (playing) { tl.GetMethod("Play").Invoke(null, null); tl.GetMethod("Seek").Invoke(null, new object[] { t }); }
        Log.LogInfo("Timeline reloaded so its Depth of Field keys use this script");
    }

    static void DofInterpolate(ObjectCtrlInfo oci, object parameter, object leftValue, object rightValue, float factor)
    {
        DofKey a = leftValue as DofKey, b = rightValue as DofKey;
        StdDof d = DofComp();
        if (a == null || b == null || d == null) return;
        // a side that is off counts as aperture 0 (no blur), so on/off keys fade with a smooth curve
        float apA = a.on ? a.aperture : 0f, apB = b.on ? b.aperture : 0f;
        float sizeA = a.on || !b.on ? a.size : b.size, sizeB = b.on || !a.on ? b.size : a.size;
        bool on = factor <= 0f ? a.on : factor >= 1f ? b.on : a.on || b.on;
        float ap = Mathf.Lerp(apA, apB, factor), size = Mathf.Lerp(sizeA, sizeB, factor);
        float blur = a.blur > 0f && b.blur > 0f ? Mathf.Lerp(a.blur, b.blur, factor) : a.blur > 0f ? a.blur : b.blur;
        d.enabled = on; d.focalSize = size; d.aperture = ap; d.nearBlur = factor >= 1f ? b.near : a.near;
        if (blur > 0f) dofBlur = blur;
        Studio.SceneInfo si = Studio.Studio.Instance.sceneInfo;
        si.enableDepth = on; si.depthFocalSize = size; si.depthAperture = ap;
        dofFrom = a.focus; dofTo = b.focus; dofFixed = a.focus.Same(b.focus) ? 1f : factor;
    }

    static DofKey DofCurrentKey()
    {
        DofKey k = new DofKey();
        StdDof d = DofComp();
        if (d == null) return k;
        Studio.Studio st = Studio.Studio.Instance;
        k.on = d.enabled; k.size = d.focalSize; k.aperture = d.aperture; k.near = d.nearBlur;
        k.blur = dofBlur > 0f ? dofBlur : d.maxBlurSize * 1080f / Mathf.Max(1, Screen.height);
        if (dofTo != null) k.focus = dofTo.Clone();
        else if (st.sceneInfo.depthForcus >= 0) { k.focus.mode = "object"; k.focus.key = st.sceneInfo.depthForcus; }
        return k;
    }

    static DofKey DofKeyFromXml(XmlNode n)
    {
        DofKey k = new DofKey();
        k.on = XAttr(n, "on", "true") == "true";
        k.near = XAttr(n, "near", "false") == "true";
        k.size = XFlt(n, "size", 0.4f); k.aperture = XFlt(n, "aperture", 0.6f); k.blur = XFlt(n, "blur", -1f);
        k.focus.mode = XAttr(n, "focus", "target");
        k.focus.key = (int)XFlt(n, "focusKey", -1f);
        k.focus.bone = XAttr(n, "focusBone", "");
        k.focus.v = new Vector3(XFlt(n, "focusX", 0f), XFlt(n, "focusY", 0f), XFlt(n, "focusZ", 0f));
        return k;
    }

    static void DofKeyToXml(XmlTextWriter w, DofKey k)
    {
        if (k == null) k = new DofKey();
        foreach (KeyValuePair<string, string> kv in DofAttrs(k)) w.WriteAttributeString(kv.Key, kv.Value);
    }

    static List<KeyValuePair<string, string>> DofAttrs(DofKey k)
    {
        CultureInfo ci = CultureInfo.InvariantCulture;
        List<KeyValuePair<string, string>> l = new List<KeyValuePair<string, string>>();
        l.Add(new KeyValuePair<string, string>("on", k.on ? "true" : "false"));
        l.Add(new KeyValuePair<string, string>("size", k.size.ToString("R", ci)));
        l.Add(new KeyValuePair<string, string>("aperture", k.aperture.ToString("R", ci)));
        l.Add(new KeyValuePair<string, string>("blur", k.blur.ToString("R", ci)));
        l.Add(new KeyValuePair<string, string>("near", k.near ? "true" : "false"));
        l.Add(new KeyValuePair<string, string>("focus", k.focus.mode));
        l.Add(new KeyValuePair<string, string>("focusKey", k.focus.key.ToString(ci)));
        l.Add(new KeyValuePair<string, string>("focusBone", k.focus.bone));
        l.Add(new KeyValuePair<string, string>("focusX", k.focus.v.x.ToString("R", ci)));
        l.Add(new KeyValuePair<string, string>("focusY", k.focus.v.y.ToString("R", ci)));
        l.Add(new KeyValuePair<string, string>("focusZ", k.focus.v.z.ToString("R", ci)));
        return l;
    }

    static string XAttr(XmlNode n, string name, string def)
    {
        XmlAttribute a = n.Attributes != null ? n.Attributes[name] : null;
        return a != null ? a.Value : def;
    }

    static float XFlt(XmlNode n, string name, float def)
    {
        float f;
        return float.TryParse(XAttr(n, name, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out f) ? f : def;
    }

    // timeline.build track {"type": "dof", "ease", "keys": [{t, on, focus, size, aperture | sharp, full, blur, near}]}:
    // a field left out of a key keeps the previous key's value (first key: on, camera target, sharp 0.1, full 3,
    // blur 3).
    static void DofTrackXml(StringBuilder sb, List<object> keys, string ease)
    {
        DofKey prev = new DofKey();
        prev.blur = 3f;
        float s0, a0;
        SharpFull(0.1f, 3f, out s0, out a0);
        prev.size = s0; prev.aperture = a0;
        List<object> filled = new List<object>();
        foreach (object ko in keys)
        {
            Dictionary<string, object> k = ko as Dictionary<string, object>;
            if (k == null) continue;
            DofKey c = new DofKey();
            c.on = Bool(k, "on", prev.on); c.near = Bool(k, "near", prev.near);
            c.size = prev.size; c.aperture = prev.aperture; c.blur = Flt(k, "blur", prev.blur);
            if (k.ContainsKey("sharp") || k.ContainsKey("full")) SharpFull(Flt(k, "sharp", 0.1f), Flt(k, "full", 3f), out c.size, out c.aperture);
            if (k.ContainsKey("size")) c.size = Mathf.Clamp(Flt(k, "size", c.size), 0f, 2f);
            if (k.ContainsKey("aperture")) c.aperture = Mathf.Clamp(Flt(k, "aperture", c.aperture), 0f, 0.99f);
            c.focus = k.ContainsKey("focus") ? ParseFocus(k["focus"]) : prev.focus;
            StringBuilder attrs = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in DofAttrs(c))
                attrs.Append(kv.Key).Append("=\"").Append(System.Security.SecurityElement.Escape(kv.Value)).Append("\" ");
            Dictionary<string, object> fk = new Dictionary<string, object>();
            fk["t"] = k.ContainsKey("t") ? k["t"] : 0.0;
            if (k.ContainsKey("ease")) fk["ease"] = k["ease"];
            fk["attrs"] = attrs.ToString().TrimEnd();
            filled.Add(fk);
            prev = c;
        }
        Interp(sb, "dof", "", filled, ease, k => Str(k, "attrs", ""), DofOwner);
    }
}
