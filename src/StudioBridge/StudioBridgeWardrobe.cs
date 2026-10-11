// Wardrobe commands for StudioBridge (the rest of the partial class is StudioBridge.cs; ScriptLoader compiles all).
//
// For building clothes mods without restarting Studio:
//  - clothes.search / clothes.set / clothes.color: put any listed top, bottom, ... on a character by its runtime id
//    (mod items have Sideloader slot ids, so search first) and set the three colour-picker colours;
//  - acc.search / acc.set / acc.move: the same for accessory slots (hats are ao_head = 351);
//  - wear.info / wear.export: renderers, materials and textures of a worn piece, and its meshes as a .json + .bin
//    pair (bind-pose arrays, bone names, bind poses, and the current pose baked to world space);
//  - wear.setmesh / wear.settex / wear.setprop / wear.restore: swap a renderer's mesh or a material's texture or
//    values at run time (kept until the piece is changed or restored), so an edited mesh can be looked at in the
//    real shader and pose before it is written into a bundle.
// A worn piece is addressed by kind (clothes 0 top .. 7 shoes) or slot (accessory 0..19), plus a renderer name.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using AIChara;
using Studio;
using UnityEngine;

public partial class StudioBridge
{
    static readonly string[] TexProps = {
        "_MainTex", "_BumpMap", "_BumpMap2", "_DetailMainTex", "_DetailNormalMap", "_DetailGlossMap", "_DetailMask",
        "_ColorMask", "_MetallicGlossMap", "_OcclusionMap", "_AlphaMask", "_AlphaMask2", "_EmissionMap", "_ParallaxMap",
        "_LineMask", "_PatternMask1", "_PatternMask2", "_PatternMask3", "_SpecGlossMap", "_GlossMap", "_NormalMap",
        "_LiquidMask", "_Texture2", "_Texture3", "_WeatheringMask", "_HairGloss", "_AnotherRamp", "_RampG", "_LineMask2" };
    static readonly string[] ColorProps = { "_Color", "_Color2", "_Color3", "_Color4", "_BaseColor", "_SpecColor",
        "_EmissionColor", "_ShadowColor", "_LineColor" };
    static readonly string[] FloatProps = { "_Glossiness", "_Glossiness2", "_Glossiness3", "_Glossiness4", "_Metallic",
        "_Metallic2", "_Metallic3", "_Metallic4", "_BumpScale", "_DetailNormalMapScale", "_Cutoff", "_OcclusionStrength",
        "_AlphaEx", "_UVScalePattern", "_LineWidthS", "_rimpower", "_rimV" };

    // original meshes of swapped renderers, to restore
    static readonly Dictionary<Renderer, Mesh> wearOrigMesh = new Dictionary<Renderer, Mesh>();
    static AssetBundle wearBundle;

    static readonly Dictionary<Renderer, UnityEngine.Rendering.ShadowCastingMode> castSaved = new Dictionary<Renderer, UnityEngine.Rendering.ShadowCastingMode>();

    Dictionary<string, object> WardrobeCommand(string cmd, Dictionary<string, object> q, Dictionary<string, object> r, Studio.Studio st)
    {
        switch (cmd)
        {
            case "wear.effects":
            {
                // image effects on the main camera (post-processing, AO, ...): list, and switch by type name
                // (enable / disable: [names]) to tell a rendering artefact from a modelling one
                Camera cam = Camera.main;
                if (cam == null) throw new Exception("no main camera");
                HashSet<string> off = new HashSet<string>(), on = new HashSet<string>();
                if (q.ContainsKey("disable")) foreach (object o in (List<object>)q["disable"]) off.Add(o.ToString());
                if (q.ContainsKey("enable")) foreach (object o in (List<object>)q["enable"]) on.Add(o.ToString());
                List<object> list = new List<object>();
                foreach (Behaviour b in cam.GetComponents<Behaviour>())
                {
                    string n = b.GetType().Name;
                    if (off.Contains(n)) b.enabled = false;
                    if (on.Contains(n)) b.enabled = true;
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["type"] = n; e["enabled"] = b.enabled;
                    list.Add(e);
                }
                r["camera"] = cam.name; r["effects"] = list;
                if (q.ContainsKey("allCast"))
                {
                    // every renderer in the scene: shadow casting on / off (restored by allCast:true), to tell a
                    // cast shadow from a shading artefact
                    bool on2 = Bool(q, "allCast", true); int n2 = 0;
                    // scope: "all" (default), "chara" (only under the character key), "other" (everything else)
                    string scope = q.ContainsKey("scope") ? q["scope"].ToString() : "all";
                    Transform croot = q.ContainsKey("key") ? Chara(st, q).charInfo.transform : null;
                    foreach (Renderer rr in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    {
                        bool under = croot != null && rr.transform.IsChildOf(croot);
                        if (!on2 && scope == "chara" && !under) continue;
                        if (!on2 && scope == "other" && under) continue;
                        if (!on2 && q.ContainsKey("rmin") && croot != null)
                        {
                            // only renderers whose bounds come within [rmin, rmax) of the character root
                            float dd = Mathf.Sqrt(rr.bounds.SqrDistance(croot.position));
                            if (dd < Flt(q, "rmin", 0f) || dd >= Flt(q, "rmax", 1e9f)) continue;
                        }
                        if (!on2 && rr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) { castSaved[rr] = rr.shadowCastingMode; rr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; n2++; }
                        if (on2 && castSaved.ContainsKey(rr)) { rr.shadowCastingMode = castSaved[rr]; n2++; }
                    }
                    if (!on2 && Bool(q, "names", false))
                    {
                        List<object> nm = new List<object>();
                        foreach (Renderer rr in castSaved.Keys) if (nm.Count < 60) nm.Add(rr.name + " @" + rr.bounds.center.ToString("F1") + " size " + rr.bounds.size.ToString("F1"));
                        r["names"] = nm;
                    }
                    if (on2) castSaved.Clear();
                    r["changed"] = n2;
                }
                List<object> lights = new List<object>();
                Dictionary<string, object> ls = q.ContainsKey("lights") ? (Dictionary<string, object>)q["lights"] : null;
                foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
                {
                    if (ls != null && ls.ContainsKey(l.name))
                    {
                        // {"<light name>": {"shadows": "None|Hard|Soft", "bias": f, "normalBias": f, "enabled": b}}
                        Dictionary<string, object> c = (Dictionary<string, object>)ls[l.name];
                        if (c.ContainsKey("shadows")) l.shadows = (LightShadows)Enum.Parse(typeof(LightShadows), c["shadows"].ToString());
                        if (c.ContainsKey("bias")) l.shadowBias = Flt(c, "bias", l.shadowBias);
                        if (c.ContainsKey("normalBias")) l.shadowNormalBias = Flt(c, "normalBias", l.shadowNormalBias);
                        if (c.ContainsKey("enabled")) l.enabled = Bool(c, "enabled", true);
                    }
                    Dictionary<string, object> e = new Dictionary<string, object>();
                    e["bias"] = l.shadowBias; e["normalBias"] = l.shadowNormalBias; e["strength"] = l.shadowStrength;
                    e["name"] = l.name; e["type"] = l.type.ToString(); e["shadows"] = l.shadows.ToString();
                    e["enabled"] = l.enabled && l.gameObject.activeInHierarchy; e["intensity"] = l.intensity;
                    e["forward"] = V(l.transform.forward);
                    lights.Add(e);
                }
                r["lights"] = lights;
                return r;
            }
            case "clothes.search":
            case "acc.search":
            {
                // list items of a category: clothes by kind (female 240+kind, male 140+kind) or any category number
                OCIChar oc = q.ContainsKey("key") ? Chara(st, q) : null;
                int cat;
                if (q.ContainsKey("category")) cat = Int(q, "category", 0);
                else if (cmd == "acc.search") cat = 351;
                else cat = ((oc != null && oc.charInfo.sex == 0) ? 140 : 240) + Int(q, "kind", 0);
                string text = Str(q, "q", "").ToLowerInvariant();
                int limit = Int(q, "limit", 50);
                Dictionary<int, ListInfoBase> info = Manager.Character.Instance.chaListCtrl.GetCategoryInfo((ChaListDefine.CategoryNo)cat);
                List<object> list = new List<object>();
                if (info != null)
                    foreach (KeyValuePair<int, ListInfoBase> kv in info)
                    {
                        string data = kv.Value.GetInfo(ChaListDefine.KeyType.MainData);
                        if (text.Length > 0 && (kv.Value.Name ?? "").ToLowerInvariant().IndexOf(text, StringComparison.Ordinal) < 0
                            && (data ?? "").ToLowerInvariant().IndexOf(text, StringComparison.Ordinal) < 0) continue;
                        Dictionary<string, object> m = new Dictionary<string, object>();
                        m["id"] = kv.Key; m["name"] = kv.Value.Name;
                        m["ab"] = kv.Value.GetInfo(ChaListDefine.KeyType.MainAB); m["data"] = data;
                        list.Add(m);
                        if (list.Count >= limit) { r["truncated"] = true; break; }
                    }
                r["category"] = cat;
                r["items"] = list;
                return r;
            }
            case "clothes.set":
            {
                // kind, id; default colours of the item unless keepColor
                OCIChar oc = Chara(st, q);
                ChaControl c = oc.charInfo;
                int kind = Int(q, "kind", 0), id = Int(q, "id", 0);
                // Studio frees the colouring inputs after a load (releaseCustomInputTexture), so colours only take
                // effect by loading the item again: once for its default colours, once more to apply them
                c.nowCoordinate.clothes.parts[kind].id = id;
                c.ChangeClothes(kind, id, true);
                if (!Bool(q, "keepColor", false)) { c.SetClothesDefaultSetting(kind); c.ChangeClothes(kind, id, true); }
                oc.SetClothesStateAll(0);
                r["state"] = WearState(c, kind, -1);
                return r;
            }
            case "clothes.color":
            {
                // colors: [[r,g,b(,a)], ...] (0..1) for the three colour pickers; gloss / metallic: [..]
                ChaControl c = Chara(st, q).charInfo;
                int kind = Int(q, "kind", 0);
                ChaFileClothes.PartsInfo p = c.nowCoordinate.clothes.parts[kind];
                List<object> cols = q.ContainsKey("colors") ? q["colors"] as List<object> : null;
                if (cols != null)
                    for (int i = 0; i < cols.Count && i < 3; i++) if (cols[i] != null) p.colorInfo[i].baseColor = Col(cols[i]);
                List<object> gl = q.ContainsKey("gloss") ? q["gloss"] as List<object> : null;
                if (gl != null) for (int i = 0; i < gl.Count && i < 3; i++) p.colorInfo[i].glossPower = (float)Num(gl[i], 0);
                List<object> me = q.ContainsKey("metallic") ? q["metallic"] as List<object> : null;
                if (me != null) for (int i = 0; i < me.Count && i < 3; i++) p.colorInfo[i].metallicPower = (float)Num(me[i], 0);
                if (q.ContainsKey("pattern")) for (int i = 0; i < 3; i++) p.colorInfo[i].pattern = Int(q, "pattern", 0);
                c.ChangeClothes(kind, p.id, true);   // reload: see clothes.set
                r["state"] = WearState(c, kind, -1);
                return r;
            }
            case "acc.set":
            {
                // slot, category (default 351 head), id (-1 removes), parent (e.g. N_Head_top)
                ChaControl c = Chara(st, q).charInfo;
                int slot = Int(q, "slot", 0);
                int cat = Int(q, "category", 351), id = Int(q, "id", -1);
                string parent = Str(q, "parent", "");
                if (id < 0) cat = 350;
                if (parent.Length == 0 && cat != 350)
                {
                    ListInfoBase li = Manager.Character.Instance.chaListCtrl.GetListInfo((ChaListDefine.CategoryNo)cat, id);
                    if (li != null) parent = li.GetInfo(ChaListDefine.KeyType.Parent);
                }
                c.nowCoordinate.accessory.parts[slot].parentKey = parent;
                c.ChangeAccessory(slot, cat, id, parent, true);
                if (cat != 350)
                {
                    c.nowCoordinate.accessory.parts[slot].parentKey = parent;
                    c.SetAccessoryDefaultColor(slot);
                    c.ChangeAccessoryColor(slot);
                    c.SetAccessoryState(slot, true);
                }
                r["state"] = WearState(c, -1, slot);
                return r;
            }
            case "acc.move":
            {
                // slot, idx (0/1 adjustment), pos [x,y,z], rot [x,y,z], scale [x,y,z] (absolute)
                ChaControl c = Chara(st, q).charInfo;
                int slot = Int(q, "slot", 0), idx = Int(q, "idx", 0);
                ChaFileAccessory.PartsInfo p = c.nowCoordinate.accessory.parts[slot];
                if (q.ContainsKey("pos")) p.addMove[idx, 0] = Vec(q["pos"]);
                if (q.ContainsKey("rot")) p.addMove[idx, 1] = Vec(q["rot"]);
                if (q.ContainsKey("scale")) p.addMove[idx, 2] = Vec(q["scale"]);
                c.UpdateAccessoryMoveFromInfo(slot);
                r["pos"] = V(p.addMove[idx, 0]); r["rot"] = V(p.addMove[idx, 1]); r["scale"] = V(p.addMove[idx, 2]);
                return r;
            }
            case "acc.color":
            {
                ChaControl c = Chara(st, q).charInfo;
                int slot = Int(q, "slot", 0);
                ChaFileAccessory.PartsInfo p = c.nowCoordinate.accessory.parts[slot];
                List<object> cols = q.ContainsKey("colors") ? q["colors"] as List<object> : null;
                if (cols != null)
                    for (int i = 0; i < cols.Count && i < p.colorInfo.Length; i++) if (cols[i] != null) p.colorInfo[i].color = Col(cols[i]);
                c.ChangeAccessoryColor(slot);
                r["state"] = WearState(c, -1, slot);
                return r;
            }
            case "wear.info":
            {
                ChaControl c = Chara(st, q).charInfo;
                GameObject root = WearRoot(c, q);
                List<object> rends = new List<object>();
                foreach (Renderer rd in root.GetComponentsInChildren<Renderer>(true))
                    rends.Add(RendererInfo(rd, root.transform));
                r["root"] = root.name;
                r["renderers"] = rends;
                r["state"] = WearState(c, q.ContainsKey("slot") ? -1 : Int(q, "kind", 0), q.ContainsKey("slot") ? Int(q, "slot", 0) : -1);
                CmpClothes cc = root.GetComponent<CmpClothes>();
                if (cc != null)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["useColor"] = new List<object> { cc.useColorN01, cc.useColorN02, cc.useColorN03 };
                    m["defColor"] = new List<object> { CL(cc.defMainColor01), CL(cc.defMainColor02), CL(cc.defMainColor03) };
                    m["rend01"] = Names(cc.rendNormal01); m["rend02"] = Names(cc.rendNormal02); m["rend03"] = Names(cc.rendNormal03);
                    m["topDef"] = cc.objTopDef != null ? cc.objTopDef.name : ""; m["topHalf"] = cc.objTopHalf != null ? cc.objTopHalf.name : "";
                    m["botDef"] = cc.objBotDef != null ? cc.objBotDef.name : ""; m["botHalf"] = cc.objBotHalf != null ? cc.objBotHalf.name : "";
                    r["cmpClothes"] = m;
                }
                return r;
            }
            case "wear.export":
            {
                // dir: writes <renderer>.json + <renderer>.bin for every mesh renderer of the piece (or just "renderer")
                ChaControl c = Chara(st, q).charInfo;
                GameObject root = WearRoot(c, q);
                string dir = Str(q, "dir", Path.Combine(BepInEx.Paths.GameRootPath, @"UserData\StudioBridge\wear"));
                Directory.CreateDirectory(dir);
                string only = Str(q, "renderer", "");
                List<object> files = new List<object>();
                foreach (Renderer rd in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (only.Length > 0 && rd.name != only) continue;
                    string f = ExportRenderer(rd, root.transform, dir);
                    if (f != null) files.Add(f);
                }
                r["files"] = files;
                return r;
            }
            case "wear.setmesh":
            {
                // renderer, file (.json written by our exporter format: vertices/normals/tangents/uv/uv2/boneIndex/
                // boneWeight/submeshes in the .bin). Bones and bind poses stay those of the renderer.
                ChaControl c = Chara(st, q).charInfo;
                Renderer rd = WearRenderer(c, q);
                Mesh orig = SharedMesh(rd);
                if (!wearOrigMesh.ContainsKey(rd)) wearOrigMesh[rd] = orig;
                List<string> boneNames;
                Mesh m = LoadMesh(Str(q, "file", ""), wearOrigMesh[rd], out boneNames);
                SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
                if (boneNames != null && smr != null)
                {
                    // the mesh brings its own bone list: bind it to the character's body bones by name
                    Dictionary<string, Transform> body = new Dictionary<string, Transform>();
                    foreach (Transform t in (c.objBodyBone != null ? c.objBodyBone.transform : c.transform).GetComponentsInChildren<Transform>(true))
                        if (!body.ContainsKey(t.name)) body[t.name] = t;
                    Transform[] bones = new Transform[boneNames.Count];
                    List<object> missing = new List<object>();
                    for (int i = 0; i < bones.Length; i++)
                        if (!body.TryGetValue(boneNames[i], out bones[i])) missing.Add(boneNames[i]);
                    if (missing.Count > 0) { r["missingBones"] = missing; throw new Exception("bones not found: " + missing.Count); }
                    smr.bones = bones;
                }
                SetSharedMesh(rd, m);
                r["vertices"] = m.vertexCount; r["submeshes"] = m.subMeshCount;
                return r;
            }
            case "wear.restore":
            {
                ChaControl c = Chara(st, q).charInfo;
                Renderer rd = WearRenderer(c, q);
                Mesh orig;
                if (wearOrigMesh.TryGetValue(rd, out orig)) { SetSharedMesh(rd, orig); wearOrigMesh.Remove(rd); r["restored"] = true; }
                return r;
            }
            case "wear.settex":
            {
                // renderer, mat (index, default all), prop (default _MainTex), file (png/jpg), linear (normal maps)
                ChaControl c = Chara(st, q).charInfo;
                Renderer rd = WearRenderer(c, q);
                string file = Str(q, "file", "");
                bool linear = Bool(q, "linear", false);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
                if (!tex.LoadImage(File.ReadAllBytes(file))) throw new Exception("cannot read image " + file);
                tex.name = Path.GetFileNameWithoutExtension(file);
                tex.wrapMode = TextureWrapMode.Repeat;
                tex.anisoLevel = 4;
                string prop = Str(q, "prop", "_MainTex");
                int mi = Int(q, "mat", -1);
                Material[] mats = rd.materials;
                for (int i = 0; i < mats.Length; i++)
                    if (mi < 0 || mi == i) mats[i].SetTexture(prop, tex);
                r["size"] = new List<object> { tex.width, tex.height };
                return r;
            }
            case "wear.setprop":
            {
                // renderer, mat, floats {name: v}, colors {name: [r,g,b,a]}, textures {name: null} to clear,
                // keywords {name: bool}
                ChaControl c = Chara(st, q).charInfo;
                Renderer rd = WearRenderer(c, q);
                int mi = Int(q, "mat", -1);
                Material[] mats = rd.materials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mi >= 0 && mi != i) continue;
                    Material mat = mats[i];
                    Dictionary<string, object> fl = q.ContainsKey("floats") ? q["floats"] as Dictionary<string, object> : null;
                    if (fl != null) foreach (KeyValuePair<string, object> kv in fl) mat.SetFloat(kv.Key, (float)Num(kv.Value, 0));
                    Dictionary<string, object> co = q.ContainsKey("colors") ? q["colors"] as Dictionary<string, object> : null;
                    if (co != null) foreach (KeyValuePair<string, object> kv in co) mat.SetColor(kv.Key, Col(kv.Value));
                    Dictionary<string, object> tx = q.ContainsKey("textures") ? q["textures"] as Dictionary<string, object> : null;
                    if (tx != null) foreach (KeyValuePair<string, object> kv in tx) mat.SetTexture(kv.Key, null);
                    Dictionary<string, object> kw = q.ContainsKey("keywords") ? q["keywords"] as Dictionary<string, object> : null;
                    if (kw != null) foreach (KeyValuePair<string, object> kv in kw)
                        { if (kv.Value is bool && (bool)kv.Value) mat.EnableKeyword(kv.Key); else mat.DisableKeyword(kv.Key); }
                }
                r["material"] = RendererInfo(rd, rd.transform);
                return r;
            }
            case "wear.show":
            {
                // renderer, on: hide or show one renderer (e.g. compare with and without a part)
                ChaControl c = Chara(st, q).charInfo;
                Renderer rd = WearRenderer(c, q);
                if (q.ContainsKey("on")) rd.enabled = Bool(q, "on", true);
                if (q.ContainsKey("cast")) rd.shadowCastingMode = Bool(q, "cast", true) ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                if (q.ContainsKey("receive")) rd.receiveShadows = Bool(q, "receive", true);
                r["cast"] = rd.shadowCastingMode.ToString(); r["receive"] = rd.receiveShadows;
                return r;
            }
            case "coord.save":
            {
                // key, path (absolute, or a file name under UserData/coordinate/<female|male>), name, png (card image)
                OCIChar oc = Chara(st, q);
                ChaControl c = oc.charInfo;
                string path = Str(q, "path", "");
                if (!Path.IsPathRooted(path))
                    path = Path.GetFullPath(Path.Combine(Application.dataPath, "../UserData/coordinate/" + (c.sex == 0 ? "male/" : "female/") + path));
                ChaFileCoordinate cf = c.nowCoordinate;
                string png = Str(q, "png", "");
                if (png.Length > 0) cf.pngData = File.ReadAllBytes(png);
                cf.coordinateName = Str(q, "name", cf.coordinateName);
                cf.SaveFile(path, 0);
                r["path"] = path;
                return r;
            }
            case "hair.info":
            {
                // the four hair slots (0 back, 1 front, 2 side, 3 extension): list id, name, object
                ChaControl c = Chara(st, q).charInfo;
                List<object> l = new List<object>();
                for (int k = 0; k < c.fileHair.parts.Length; k++)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["kind"] = k; m["id"] = c.fileHair.parts[k].id;
                    m["name"] = c.infoHair[k] != null ? c.infoHair[k].Name : "";
                    m["object"] = c.objHair[k] != null ? c.objHair[k].name : "";
                    l.Add(m);
                }
                r["hair"] = l;
                return r;
            }
            case "hair.set":
            {
                // kinds: {"0": id, ...} (0 back, 1 front, 2 side, 3 extension). Studio has no hair change of its own:
                // the hair FK guides are dropped and rebuilt the way OCIChar.ChangeChara does when a card is replaced.
                OCIChar oc = Chara(st, q);
                ChaControl c = oc.charInfo;
                Dictionary<string, object> kinds = q.ContainsKey("kinds") ? q["kinds"] as Dictionary<string, object> : null;
                if (kinds == null || kinds.Count == 0) throw new Exception("kinds: {\"0\": id, ...}");
                List<OCIChar.BoneInfo> keep = new List<OCIChar.BoneInfo>();
                foreach (OCIChar.BoneInfo b in oc.listBones)
                    if (b.boneGroup == OIBoneInfo.BoneGroup.Hair) Singleton<GuideObjectManager>.Instance.Delete(b.guideObject);
                    else keep.Add(b);
                oc.listBones = keep;
                List<int> hairKeys = new List<int>();
                foreach (KeyValuePair<int, OIBoneInfo> kv in oc.oiCharInfo.bones) if (kv.Value.group == OIBoneInfo.BoneGroup.Hair) hairKeys.Add(kv.Key);
                foreach (int k in hairKeys) oc.oiCharInfo.bones.Remove(k);
                foreach (KeyValuePair<string, object> kv in kinds)
                {
                    int kind = int.Parse(kv.Key), id = (int)Num(kv.Value, 0);
                    c.ChangeHair(kind, id, true);   // synchronous (asyncFlags false)
                }
                for (int k = 0; k < 2; k++)
                    if (c.objHair[k] != null) AddObjectAssist.ArrangeNames(c.objHair[k].transform);
                AddObjectAssist.InitHairBone(oc, Singleton<Info>.Instance.dicBoneInfo);
                oc.InitFK(null);
                for (int i = 0; i < FKCtrl.parts.Length; i++) oc.ActiveFK(FKCtrl.parts[i], oc.oiCharInfo.activeFK[i], oc.oiCharInfo.activeFK[i]);
                oc.ActiveKinematicMode(OICharInfo.KinematicMode.FK, oc.oiCharInfo.enableFK, true);
                oc.UpdateFKColor(OIBoneInfo.BoneGroup.Hair);
                List<object> l = new List<object>();
                for (int k = 0; k < c.fileHair.parts.Length; k++)
                    l.Add(k + ": " + c.fileHair.parts[k].id + " " + (c.infoHair[k] != null ? c.infoHair[k].Name : ""));
                r["hair"] = l;
                return r;
            }
            case "coord.load":
            {
                // key, path: Studio's own clothes-file load (as the workspace's 衣装 button does)
                OCIChar oc = Chara(st, q);
                string path = Str(q, "path", "");
                if (!Path.IsPathRooted(path))
                    path = Path.GetFullPath(Path.Combine(Application.dataPath, "../UserData/coordinate/" + (oc.charInfo.sex == 0 ? "male/" : "female/") + path));
                oc.LoadClothesFile(path);
                r["path"] = path;
                return r;
            }
            case "bundle.wear":
            {
                // path (bundle file), prefab, part (renderer name inside the prefab), renderer (worn one), maintex:
                // put the prefab's mesh, bones (by name) and material on a worn renderer, to check a rebuilt bundle
                // in this Unity without restarting. The bundle stays loaded until the next bundle.wear.
                if (wearBundle != null) { wearBundle.Unload(false); wearBundle = null; }
                wearBundle = AssetBundle.LoadFromFile(Str(q, "path", ""));
                if (wearBundle == null) throw new Exception("AssetBundle.LoadFromFile failed");
                GameObject go = wearBundle.LoadAsset<GameObject>(Str(q, "prefab", ""));
                if (go == null) throw new Exception("prefab not found");
                SkinnedMeshRenderer src = null;
                foreach (SkinnedMeshRenderer x in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (x.name == Str(q, "part", "")) src = x;
                if (src == null) throw new Exception("part not found");
                ChaControl c = Chara(st, q).charInfo;
                SkinnedMeshRenderer dst = WearRenderer(c, q) as SkinnedMeshRenderer;
                Dictionary<string, Transform> body = new Dictionary<string, Transform>();
                foreach (Transform t in (c.objBodyBone != null ? c.objBodyBone.transform : c.transform).GetComponentsInChildren<Transform>(true))
                    if (!body.ContainsKey(t.name)) body[t.name] = t;
                Transform[] bones = new Transform[src.bones.Length];
                for (int i = 0; i < bones.Length; i++)
                    if (src.bones[i] == null || !body.TryGetValue(src.bones[i].name, out bones[i])) throw new Exception("bone " + i + " unresolved");
                if (!wearOrigMesh.ContainsKey(dst)) wearOrigMesh[dst] = dst.sharedMesh;
                dst.bones = bones;
                SetSharedMesh(dst, src.sharedMesh);
                Material m = new Material(src.sharedMaterial);
                string mt = Str(q, "maintex", "");
                if (mt.Length > 0) { Texture2D t = wearBundle.LoadAsset<Texture2D>(mt); if (t != null) m.SetTexture("_MainTex", t); }
                dst.sharedMaterial = m;
                r["vertices"] = src.sharedMesh.vertexCount; r["bones"] = bones.Length;
                r["material"] = RendererInfo(dst, dst.transform);
                return r;
            }
            case "bundle.check":
            {
                // load an asset bundle file (not a zipmod) and list its assets, to check a rebuilt bundle loads in this
                // Unity; unloads it again
                string path = Str(q, "path", "");
                AssetBundle ab = AssetBundle.LoadFromFile(path);
                if (ab == null) throw new Exception("AssetBundle.LoadFromFile failed: " + path);
                try
                {
                    List<object> names = new List<object>();
                    foreach (string n in ab.GetAllAssetNames()) names.Add(n);
                    r["assets"] = names;
                    string asset = Str(q, "asset", "");
                    if (asset.Length > 0)
                    {
                        GameObject go = ab.LoadAsset<GameObject>(asset);
                        if (go == null) throw new Exception("asset not found: " + asset);
                        List<object> rends = new List<object>();
                        foreach (Renderer rd in go.GetComponentsInChildren<Renderer>(true)) rends.Add(RendererInfo(rd, go.transform));
                        r["renderers"] = rends;
                    }
                }
                finally { ab.Unload(true); }
                return r;
            }
        }
        throw new Exception("unknown cmd '" + cmd + "'");
    }

    // ---------------------------------------------------------------- helpers

    static GameObject WearRoot(ChaControl c, Dictionary<string, object> q)
    {
        string part = Str(q, "part", "");
        if (part.StartsWith("hair")) { int hi = part.Length > 4 ? int.Parse(part.Substring(4)) : 0; GameObject h = c.objHair[hi]; if (h == null) throw new Exception("no hair " + hi); return h; }
        GameObject go = part == "body" ? c.objBody : part == "head" ? c.objHead
            : q.ContainsKey("slot") ? c.objAccessory[Int(q, "slot", 0)] : c.objClothes[Int(q, "kind", 0)];
        if (go == null) throw new Exception("nothing worn there");
        return go;
    }

    static Renderer WearRenderer(ChaControl c, Dictionary<string, object> q)
    {
        GameObject root = WearRoot(c, q);
        string name = Str(q, "renderer", "");
        Renderer first = null;
        foreach (Renderer rd in root.GetComponentsInChildren<Renderer>(true))
        {
            if (name.Length == 0 || rd.name == name || RelPath(rd.transform, root.transform) == name) return rd;
            if (first == null) first = rd;
        }
        throw new Exception("renderer not found: " + name);
    }

    static Dictionary<string, object> WearState(ChaControl c, int kind, int slot)
    {
        Dictionary<string, object> m = new Dictionary<string, object>();
        if (kind >= 0)
        {
            ChaFileClothes.PartsInfo p = c.nowCoordinate.clothes.parts[kind];
            m["kind"] = kind; m["id"] = p.id;
            m["name"] = c.infoClothes[kind] != null ? c.infoClothes[kind].Name : "";
            List<object> cols = new List<object>();
            for (int i = 0; i < 3; i++) cols.Add(CL(p.colorInfo[i].baseColor));
            m["colors"] = cols;
            m["object"] = c.objClothes[kind] != null ? c.objClothes[kind].name : "";
        }
        if (slot >= 0)
        {
            ChaFileAccessory.PartsInfo p = c.nowCoordinate.accessory.parts[slot];
            m["slot"] = slot; m["category"] = p.type; m["id"] = p.id; m["parent"] = p.parentKey;
            m["name"] = c.infoAccessory[slot] != null ? c.infoAccessory[slot].Name : "";
            m["object"] = c.objAccessory[slot] != null ? c.objAccessory[slot].name : "";
        }
        return m;
    }

    static Mesh SharedMesh(Renderer rd)
    {
        SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
        if (smr != null) return smr.sharedMesh;
        MeshFilter mf = rd.GetComponent<MeshFilter>();
        return mf != null ? mf.sharedMesh : null;
    }

    static void SetSharedMesh(Renderer rd, Mesh m)
    {
        SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
        if (smr != null) { smr.sharedMesh = m; smr.updateWhenOffscreen = true; return; }   // localBounds are in root-bone space: leave them
        MeshFilter mf = rd.GetComponent<MeshFilter>();
        if (mf != null) mf.sharedMesh = m;
    }

    static string RelPath(Transform t, Transform root)
    {
        string p = t.name;
        while (t.parent != null && t != root) { t = t.parent; if (t == root) break; p = t.name + "/" + p; }
        return p;
    }

    static List<object> Names(Renderer[] rs)
    {
        List<object> l = new List<object>();
        if (rs != null) foreach (Renderer x in rs) l.Add(x != null ? x.name : "");
        return l;
    }

    static List<object> CL(Color c)
    {
        return new List<object> { Math.Round(c.r, 4), Math.Round(c.g, 4), Math.Round(c.b, 4), Math.Round(c.a, 4) };
    }

    static Color Col(object v)
    {
        List<object> l = v as List<object>;
        if (l == null || l.Count < 3) throw new Exception("expected [r, g, b(, a)]");
        return new Color((float)Num(l[0], 0), (float)Num(l[1], 0), (float)Num(l[2], 0), l.Count > 3 ? (float)Num(l[3], 1) : 1f);
    }

    static Dictionary<string, object> RendererInfo(Renderer rd, Transform root)
    {
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["name"] = rd.name; m["path"] = RelPath(rd.transform, root); m["type"] = rd.GetType().Name;
        m["enabled"] = rd.enabled; m["active"] = rd.gameObject.activeInHierarchy;
        Mesh mesh = SharedMesh(rd);
        if (mesh != null)
        {
            m["mesh"] = mesh.name; m["vertices"] = mesh.vertexCount; m["submeshes"] = mesh.subMeshCount;
            m["blendShapes"] = mesh.blendShapeCount;
        }
        SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
        if (smr != null) { m["bones"] = smr.bones.Length; m["rootBone"] = smr.rootBone != null ? smr.rootBone.name : ""; }
        List<object> mats = new List<object>();
        foreach (Material mat in rd.sharedMaterials)
        {
            if (mat == null) { mats.Add(null); continue; }
            Dictionary<string, object> mm = new Dictionary<string, object>();
            mm["name"] = mat.name; mm["shader"] = mat.shader != null ? mat.shader.name : "";
            mm["renderQueue"] = mat.renderQueue;
            Dictionary<string, object> tx = new Dictionary<string, object>();
            List<string> props = new List<string>(TexProps);
            System.Reflection.MethodInfo gtp = typeof(Material).GetMethod("GetTexturePropertyNames", Type.EmptyTypes);
            if (gtp != null)
                foreach (string p in (string[])gtp.Invoke(mat, null)) if (!props.Contains(p)) props.Add(p);
            foreach (string p in props)
                if (mat.HasProperty(p))
                {
                    Texture t = mat.GetTexture(p);
                    tx[p] = t != null ? (object)new List<object> { t.name, t.width, t.height } : null;
                }
            mm["textures"] = tx;
            Dictionary<string, object> co = new Dictionary<string, object>();
            foreach (string p in ColorProps) if (mat.HasProperty(p)) co[p] = CL(mat.GetColor(p));
            mm["colors"] = co;
            Dictionary<string, object> fl = new Dictionary<string, object>();
            foreach (string p in FloatProps) if (mat.HasProperty(p)) fl[p] = Math.Round(mat.GetFloat(p), 4);
            mm["floats"] = fl;
            mm["keywords"] = new List<object>(mat.shaderKeywords);
            mats.Add(mm);
        }
        m["materials"] = mats;
        return m;
    }

    // .bin layout (little endian, float32 / int32): vertices n*3, normals n*3, tangents n*4, uv n*2, uv2 n*2 (if any),
    // boneIndex n*4, boneWeight n*4, then per submesh its triangle indices, then baked world vertices n*3,
    // bind poses b*16, bone world matrices b*16. The .json says which arrays are present and their offsets.
    static string ExportRenderer(Renderer rd, Transform root, string dir)
    {
        Mesh mesh = SharedMesh(rd);
        if (mesh == null) return null;
        string safe = RelPath(rd.transform, root).Replace('/', '~');
        foreach (char ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
        string bin = Path.Combine(dir, safe + ".bin");
        Dictionary<string, object> meta = new Dictionary<string, object>();
        Dictionary<string, object> offs = new Dictionary<string, object>();
        using (BinaryWriter w = new BinaryWriter(File.Create(bin)))
        {
            Vector3[] v = mesh.vertices; Vector3[] n = mesh.normals; Vector4[] t = mesh.tangents;
            Vector2[] uv = mesh.uv; Vector2[] uv2 = mesh.uv2; BoneWeight[] bw = mesh.boneWeights;
            offs["vertices"] = w.BaseStream.Position; foreach (Vector3 x in v) { w.Write(x.x); w.Write(x.y); w.Write(x.z); }
            if (n.Length == v.Length) { offs["normals"] = w.BaseStream.Position; foreach (Vector3 x in n) { w.Write(x.x); w.Write(x.y); w.Write(x.z); } }
            if (t.Length == v.Length) { offs["tangents"] = w.BaseStream.Position; foreach (Vector4 x in t) { w.Write(x.x); w.Write(x.y); w.Write(x.z); w.Write(x.w); } }
            if (uv.Length == v.Length) { offs["uv"] = w.BaseStream.Position; foreach (Vector2 x in uv) { w.Write(x.x); w.Write(x.y); } }
            if (uv2.Length == v.Length) { offs["uv2"] = w.BaseStream.Position; foreach (Vector2 x in uv2) { w.Write(x.x); w.Write(x.y); } }
            if (bw.Length == v.Length)
            {
                offs["boneIndex"] = w.BaseStream.Position;
                foreach (BoneWeight b in bw) { w.Write(b.boneIndex0); w.Write(b.boneIndex1); w.Write(b.boneIndex2); w.Write(b.boneIndex3); }
                offs["boneWeight"] = w.BaseStream.Position;
                foreach (BoneWeight b in bw) { w.Write(b.weight0); w.Write(b.weight1); w.Write(b.weight2); w.Write(b.weight3); }
            }
            List<object> subs = new List<object>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int[] tri = mesh.GetTriangles(s);
                subs.Add(new List<object> { w.BaseStream.Position, tri.Length });
                foreach (int i in tri) w.Write(i);
            }
            offs["submeshes"] = subs;
            SkinnedMeshRenderer smr = rd as SkinnedMeshRenderer;
            Mesh baked = new Mesh();
            Matrix4x4 l2w;
            if (smr != null)
            {
                smr.BakeMesh(baked);
                // BakeMesh already applies the renderer's scale
                l2w = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
            }
            else { baked = mesh; l2w = rd.transform.localToWorldMatrix; }
            offs["baked"] = w.BaseStream.Position;
            foreach (Vector3 x in baked.vertices) { Vector3 p = l2w.MultiplyPoint3x4(x); w.Write(p.x); w.Write(p.y); w.Write(p.z); }
            if (smr != null)
            {
                Matrix4x4[] bp = mesh.bindposes;
                offs["bindposes"] = w.BaseStream.Position;
                foreach (Matrix4x4 mx in bp) for (int k = 0; k < 16; k++) w.Write(mx[k]);
                offs["boneWorld"] = w.BaseStream.Position;
                foreach (Transform b in smr.bones) { Matrix4x4 mx = b != null ? b.localToWorldMatrix : Matrix4x4.identity; for (int k = 0; k < 16; k++) w.Write(mx[k]); }
                List<object> bones = new List<object>();
                foreach (Transform b in smr.bones) bones.Add(b != null ? b.name : "");
                meta["bones"] = bones;
                meta["rootBone"] = smr.rootBone != null ? smr.rootBone.name : "";
                meta["rendererWorld"] = M16(smr.transform.localToWorldMatrix);
            }
            if (baked != mesh) UnityEngine.Object.Destroy(baked);
            meta["vertexCount"] = v.Length;
        }
        meta["offsets"] = offs;
        meta["renderer"] = RendererInfo(rd, root);
        meta["bin"] = bin;
        string json = Path.Combine(dir, safe + ".json");
        File.WriteAllText(json, MiniJson.Serialize(meta), Encoding.UTF8);
        return json;
    }

    static List<object> M16(Matrix4x4 m)
    {
        List<object> l = new List<object>();
        for (int k = 0; k < 16; k++) l.Add(m[k]);
        return l;
    }

    // reads a mesh written in the export layout (only vertexCount, offsets{vertices, normals, tangents, uv, uv2,
    // boneIndex, boneWeight, submeshes} and bin are needed)
    static Mesh LoadMesh(string jsonPath, Mesh orig, out List<string> boneNames)
    {
        boneNames = null;
        Dictionary<string, object> meta = MiniJson.Parse(File.ReadAllText(jsonPath)) as Dictionary<string, object>;
        Dictionary<string, object> offs = meta["offsets"] as Dictionary<string, object>;
        string binPath = meta.ContainsKey("bin") ? meta["bin"].ToString() : Path.ChangeExtension(jsonPath, ".bin");
        byte[] data = File.ReadAllBytes(binPath);
        int n = (int)Num(meta["vertexCount"], 0);
        Func<string, int, float[]> F = (k, comps) =>
        {
            if (!offs.ContainsKey(k)) return null;
            int o = (int)Num(offs[k], 0);
            float[] a = new float[n * comps];
            Buffer.BlockCopy(data, o, a, 0, a.Length * 4);
            return a;
        };
        Mesh m = new Mesh();
        m.name = orig != null ? orig.name + "_edit" : "edit";
        if (n > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        float[] fv = F("vertices", 3);
        Vector3[] v = new Vector3[n];
        for (int i = 0; i < n; i++) v[i] = new Vector3(fv[i * 3], fv[i * 3 + 1], fv[i * 3 + 2]);
        m.vertices = v;
        float[] fn = F("normals", 3);
        if (fn != null) { Vector3[] a = new Vector3[n]; for (int i = 0; i < n; i++) a[i] = new Vector3(fn[i * 3], fn[i * 3 + 1], fn[i * 3 + 2]); m.normals = a; }
        float[] ft = F("tangents", 4);
        if (ft != null) { Vector4[] a = new Vector4[n]; for (int i = 0; i < n; i++) a[i] = new Vector4(ft[i * 4], ft[i * 4 + 1], ft[i * 4 + 2], ft[i * 4 + 3]); m.tangents = a; }
        float[] fu = F("uv", 2);
        if (fu != null) { Vector2[] a = new Vector2[n]; for (int i = 0; i < n; i++) a[i] = new Vector2(fu[i * 2], fu[i * 2 + 1]); m.uv = a; }
        float[] fu2 = F("uv2", 2);
        if (fu2 != null) { Vector2[] a = new Vector2[n]; for (int i = 0; i < n; i++) a[i] = new Vector2(fu2[i * 2], fu2[i * 2 + 1]); m.uv2 = a; }
        if (offs.ContainsKey("boneIndex"))
        {
            int oi = (int)Num(offs["boneIndex"], 0), ow = (int)Num(offs["boneWeight"], 0);
            BoneWeight[] bw = new BoneWeight[n];
            for (int i = 0; i < n; i++)
            {
                BoneWeight b = new BoneWeight();
                b.boneIndex0 = BitConverter.ToInt32(data, oi + i * 16); b.boneIndex1 = BitConverter.ToInt32(data, oi + i * 16 + 4);
                b.boneIndex2 = BitConverter.ToInt32(data, oi + i * 16 + 8); b.boneIndex3 = BitConverter.ToInt32(data, oi + i * 16 + 12);
                b.weight0 = BitConverter.ToSingle(data, ow + i * 16); b.weight1 = BitConverter.ToSingle(data, ow + i * 16 + 4);
                b.weight2 = BitConverter.ToSingle(data, ow + i * 16 + 8); b.weight3 = BitConverter.ToSingle(data, ow + i * 16 + 12);
                bw[i] = b;
            }
            m.boneWeights = bw;
        }
        if (meta.ContainsKey("bones") && meta.ContainsKey("bindposes"))
        {
            List<object> bl = meta["bones"] as List<object>, bp = meta["bindposes"] as List<object>;
            boneNames = new List<string>();
            Matrix4x4[] poses = new Matrix4x4[bl.Count];
            for (int i = 0; i < bl.Count; i++)
            {
                boneNames.Add(bl[i].ToString());
                Matrix4x4 mx = new Matrix4x4();
                for (int k = 0; k < 16; k++) mx[k] = (float)Num(bp[i * 16 + k], 0);
                poses[i] = mx;
            }
            m.bindposes = poses;
        }
        else if (orig != null) m.bindposes = orig.bindposes;
        List<object> subs = offs["submeshes"] as List<object>;
        m.subMeshCount = subs.Count;
        for (int s = 0; s < subs.Count; s++)
        {
            List<object> sd = subs[s] as List<object>;
            int o = (int)Num(sd[0], 0), cnt = (int)Num(sd[1], 0);
            int[] tri = new int[cnt];
            Buffer.BlockCopy(data, o, tri, 0, cnt * 4);
            m.SetTriangles(tri, s);
        }
        if (offs.ContainsKey("normals") == false) m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }
}
