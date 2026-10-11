// #name CharaToItem
// #author stp3
// #desc Converts a Studio character into a plain item with FK bones, baked to UserData\CharaToItem.
// #proc_filter StudioNEOV2

// Loaded by BepInEx.ScriptLoader together with the other scripts in <game>\scripts (C# 7.x, mcs).
//
// Converting a character bakes it to a file and adds the result to the scene as an ordinary Studio item:
//   - the transform hierarchy (whole skeleton + every visible part) with the current pose,
//   - every visible mesh, with the current blend shape weights (expression) baked into the vertices,
//   - every material: shader, keywords, queue and the value of every property the game or MaterialEditor knows;
//     where the material came from a clothes/accessory/hair/head/body prefab, that prefab's material is the base,
//     so properties nobody changes keep their authored values,
//   - textures: those still identical to the prefab's are referenced, the rest (skin composites, list textures)
//     are read back from the GPU, compressed and stored once in UserData\CharaToItem\textures by content hash,
//   - the Studio character FK bones (they become the item's FK bones) and the twist-bone corrections (Expression).
// Baked items are registered in Studio's item list (group "Chara -> Item") at startup; a scene stores them like any
// item (group/category/no + item FK rotations). Loading builds the object from the bake file (no character).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AIChara;
using BepInEx.Logging;
using HarmonyLib;
using KKAPI.Studio;
using Studio;
using UnityEngine;
using UnityEngine.Rendering;

public static class CharaToItemScript
{
    static GameObject host;
    static Harmony harmony;

    public static void Main()
    {
        CharaToItem.Log = BepInEx.Logging.Logger.CreateLogSource("CharaToItem");
        harmony = new Harmony("stp3.charatoitem");
        harmony.PatchAll(typeof(CharaToItemPatches));
        CharaToItemPatches.PatchSideloader(harmony);
        host = new GameObject("CharaToItem");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<CharaToItem>();
        host.AddComponent<C2ICutter>();
        CharaToItem.Log.LogInfo("CharaToItem loaded. Open it from the Studio left toolbar.");
    }

    public static void Unload()
    {
        if (host != null) UnityEngine.Object.DestroyImmediate(host);
        host = null;
        if (harmony != null) harmony.UnpatchSelf();
        harmony = null;
        if (CharaToItem.Log != null) BepInEx.Logging.Logger.Sources.Remove(CharaToItem.Log);
    }
}

public static class CharaToItemPatches
{
    // our items point at a made-up bundle path; the game checks the file exists before loading it
    [HarmonyPostfix]
    [HarmonyPatch(typeof(AssetBundleCheck), "IsFile")]
    public static void IsFilePostfix(string assetBundleName, ref bool __result)
    {
        if (!__result && C2IStore.IsOurBundle(assetBundleName)) __result = true;
    }

    // ... and the "asset" is the prototype built from the bake file (CommonLib.LoadAsset then clones it)
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AssetBundleManager), "LoadAsset", new Type[] { typeof(string), typeof(string), typeof(Type), typeof(string) })]
    public static bool LoadAssetPrefix(string assetBundleName, ref AssetBundleLoadAssetOperation __result)
    {
        int no;
        if (!C2IStore.TryParseBundle(assetBundleName, out no)) return true;
        __result = new AssetBundleLoadAssetOperationSimulation(CharaToItem.GetPrototype(no));
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Studio.Info), "isLoadList", MethodType.Setter)]
    public static void LoadListPostfix(bool value)
    {
        if (value) CharaToItem.RegisterAll();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Studio.Studio), "InitScene")]
    public static void InitScenePostfix()
    {
        if (CharaToItem.Instance != null) CharaToItem.Instance.ScheduleRelease();
    }

    // a scene may name a bake that is gone: register a placeholder so the item (and its FK data) survives
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AddObjectItem), "Load", new Type[] { typeof(OIItemInfo), typeof(ObjectCtrlInfo), typeof(TreeNodeObject), typeof(bool), typeof(int) })]
    public static void ItemLoadPrefix(OIItemInfo _info)
    {
        if (_info == null || _info.group != C2IStore.Group) return;
        CharaToItem.EnsureRegistered(_info.category, _info.no);
        try { CharaToItem.SeedFK(_info); }
        catch (Exception e) { CharaToItem.Log.LogWarning("FK of item " + _info.no + ": " + e.Message); }
    }

    // dragging a slider or scrolling on one of our windows must not also drive the Studio camera
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Studio.CameraControl), "InputMouseProc")]
    public static bool CameraMousePrefix(ref bool __result)
    {
        if (!CharaToItem.MouseOverWindow && !C2ICutter.MouseOverWindow) return true;
        __result = false;
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Studio.CameraControl), "InputMouseWheelZoomProc")]
    public static bool CameraWheelPrefix(ref bool __result)
    {
        if (!CharaToItem.MouseOverWindow && !C2ICutter.MouseOverWindow) return true;
        __result = false;
        return false;
    }

    // Sideloader "compatibility resolves" item numbers it does not know into zipmod items with the same original
    // slot; our numbers are not zipmod numbers, so keep it away from our group.
    public static void PatchSideloader(Harmony h)
    {
        try
        {
            Type t = AccessTools.TypeByName("Sideloader.AutoResolver.UniversalAutoResolver");
            MethodInfo m = t != null ? AccessTools.Method(t, "ResolveStudioObject", new Type[] { typeof(ObjectInfo) }) : null;
            if (m == null) { CharaToItem.Log.LogWarning("Sideloader ResolveStudioObject not found; item numbers are chosen to avoid clashes anyway"); return; }
            h.Patch(m, new HarmonyMethod(typeof(CharaToItemPatches), "SideloaderSkip"));
        }
        catch (Exception e) { CharaToItem.Log.LogWarning("Sideloader patch failed: " + e.Message); }
    }

    public static bool SideloaderSkip(ObjectInfo __0)
    {
        OIItemInfo it = __0 as OIItemInfo;
        return it == null || it.group != C2IStore.Group;
    }
}

// ==================================================================================================== data

public class C2INode
{
    public string name;
    public int parent;
    public Vector3 lp;
    public Quaternion lr;
    public Vector3 ls;
    public bool active;
    public int layer;
}

public class C2IMesh
{
    public string name;
    public int srcIndex = -1;      // >= 0: the mesh asset of a renderer in that source prefab (no vertex data stored)
    public string srcPath = "";
    public Vector3[] v, n;
    public Vector4[] t;
    public List<Vector4>[] uv = new List<Vector4>[4];
    public Color32[] col;
    public BoneWeight[] bw;
    public Matrix4x4[] bp;
    public int[] topo;
    public int[][] idx;
    public Bounds bounds;
}

public class C2ITex
{
    public string name, hash = "";
    public int srcIndex = -1;                         // texture used by a material of that source (found by name)
    public string refAB = "", refManifest = "";       // or: an asset loadable by name (list textures)
    public int w, h, format, wrapU, wrapV, filter, aniso;
    public bool mips, linear;
    public float mipBias;
}

public class C2ITexSlot
{
    public string prop;
    public Vector2 scale, offset;
    public int mode;      // 0: keep the base material's texture, 1: none, 2: stored texture
    public int tex = -1;
}

public class C2IMat
{
    public string name, shader, renderType = "";
    public int queue;
    public string[] keywords = new string[0];
    public string[] passesOff = new string[0];
    public int source = -1;        // index into C2IBake.sources: the base material (-1: none known)
    public int shaderSource = -1;  // source holding the exact shader object (same-named copies differ)
    public Vector4 inside = new Vector4(0.3f, 0.22f, 0.2f, 1f);   // solid colour (gamma) for the inside of skin parts
    public const int FromMaterialEditor = -2;   // MaterialEditor's shared copy ("Shader Optimization")
    public string srcPath = "";    // renderer path inside the prefab
    public int srcSlot;            // material slot on that renderer
    public string srcName = "";    // material name in the prefab
    public Dictionary<string, float> floats = new Dictionary<string, float>();
    public Dictionary<string, Vector4> vectors = new Dictionary<string, Vector4>();
    public List<C2ITexSlot> slots = new List<C2ITexSlot>();
}

public class C2IRend
{
    public int node;
    public bool skinned;
    public int mesh;
    public int[] mats;
    public int[] bones = new int[0];
    public int rootBone = -1;
    public Bounds localBounds;
    public int quality, shadow, sortingOrder;
    public bool offscreen, receive;
    public int anchor = -1, lightProbe, reflProbe;
    public uint renderingLayerMask = 1;
    public float[] blend = new float[0];   // blend shape weights (referenced meshes keep their shapes)
    public string part = "";       // character part key (C2IParts), "item" for Studio items, "" in older bakes
    public int inside;             // 2: cut by the Cutter, draw its inside (section colour, or own material for shells)
}

public class C2ISource
{
    public string ab, asset, manifest;
    public int kind;               // 0: prefab (GameObject), 1: material asset
}

public class C2IExpr
{
    public string elementName = "";
    public bool enable, enableLookAt, enableCorrect;
    public int index, categoryNo;
    // LookAt
    public string lookAtName = "", targetName = "", upAxisName = "";
    public int targetAxisType, upAxisType, sourceAxisType, limitAxisType, laRotOrder;
    public float limitMin, limitMax;
    // Correct
    public string correctName = "", referenceName = "";
    public int calcType, crRotOrder;
    public float charmRate, rxMin, rxMax, ryMin, ryMax, rzMin, rzMax;
    public bool useRX, useRY, useRZ;
}

public class C2IBake
{
    public const int Version = 6;
    public const int OldestReadable = 4;
    // header
    public int no;
    public string name = "", charaName = "";
    public int sex;
    public long created;
    public List<string> fkBones = new List<string>();
    public List<string> texHashes = new List<string>();
    public string parts = "";      // which parts were baked ("" = the whole character)
    // body
    public List<C2INode> nodes = new List<C2INode>();
    public List<C2IMesh> meshes = new List<C2IMesh>();
    public List<C2ITex> textures = new List<C2ITex>();
    public List<C2IMat> mats = new List<C2IMat>();
    public List<C2IRend> rends = new List<C2IRend>();
    public List<C2ISource> sources = new List<C2ISource>();
    public bool exprEnable;
    public List<C2IExpr> expr = new List<C2IExpr>();
    public bool fillInside;        // parts without the body around them: draw their insides (see C2IBuild.AddInsides)
    public Vector4 sectionColor = new Vector4(0.35f, 0.03f, 0.03f, 1f);   // (gamma) flat colour of cut sections
    public bool shellsOwnInside = true;   // cut clothes and hair show their own material inside instead
    // not stored
    public string file;
}

// ==================================================================================================== storage

// FK bones that move something in a bake: for every renderer, the bones its kept vertices are weighted to (stored
// meshes), all bones it is bound to (meshes referenced from a prefab: no weights stored), or the node it hangs on
// (unskinned); from each, the nearest FK bone at or above it. A piece cut out of a character keeps only the FK of
// what it still contains (a cut-off arm no longer offers leg or head FK).
public static class C2IFK
{
    public static HashSet<string> Used(C2IBake b)
    {
        HashSet<string> fk = new HashSet<string>(b.fkBones);
        HashSet<int> nodes = Moving(b);
        HashSet<string> used = new HashSet<string>();
        foreach (int start in nodes)
            for (int n = start; n >= 0 && n < b.nodes.Count; n = b.nodes[n].parent)
                if (fk.Contains(b.nodes[n].name)) { used.Add(b.nodes[n].name); break; }
        return used;
    }

    // keeps the FK bones that move something (in their order); returns how many were dropped
    public static int Prune(C2IBake b)
    {
        HashSet<string> used = Used(b);
        int before = b.fkBones.Count;
        b.fkBones = b.fkBones.Where(used.Contains).ToList();
        return before - b.fkBones.Count;
    }

    // A prop with no FK of its own gets FK on every bone of its skinned meshes (and every renderer node) from HS2US's
    // GenericFK, which only steps in for items whose FK list is empty. A piece cut from such a prop keeps the whole
    // bone chain, so it is given its own list instead: the bones its kept vertices are weighted to and the nodes its
    // unskinned renderers hang on, in node order (not the root; not names that occur twice, which FK finds by name).
    public static List<string> Weighted(C2IBake b)
    {
        HashSet<int> nodes = Moving(b);
        Dictionary<string, int> seen = new Dictionary<string, int>();
        foreach (C2INode n in b.nodes) { int c; seen.TryGetValue(n.name, out c); seen[n.name] = c + 1; }
        List<string> list = new List<string>();
        for (int i = 0; i < b.nodes.Count; i++)
            if (nodes.Contains(i) && b.nodes[i].parent >= 0 && seen[b.nodes[i].name] == 1) list.Add(b.nodes[i].name);
        return list;
    }

    // a piece cut from a prop without FK (see Weighted); the FK of other pieces only ever shrinks to a non-empty list
    public static bool GiveWeighted(C2IBake b)
    {
        if (b.fkBones.Count > 0) return false;
        b.fkBones = Weighted(b);
        return b.fkBones.Count > 0;
    }

    // nodes the renderers move with
    static HashSet<int> Moving(C2IBake b)
    {
        HashSet<int> nodes = new HashSet<int>();
        foreach (C2IRend r in b.rends)
        {
            C2IMesh m = r.mesh >= 0 && r.mesh < b.meshes.Count ? b.meshes[r.mesh] : null;
            if (!r.skinned || r.bones == null || r.bones.Length == 0) { nodes.Add(r.node); continue; }
            if (m != null && m.bw != null && m.bw.Length > 0)
            {
                foreach (BoneWeight w in m.bw)
                {
                    if (w.weight0 > 0f && w.boneIndex0 < r.bones.Length) nodes.Add(r.bones[w.boneIndex0]);
                    if (w.weight1 > 0f && w.boneIndex1 < r.bones.Length) nodes.Add(r.bones[w.boneIndex1]);
                    if (w.weight2 > 0f && w.boneIndex2 < r.bones.Length) nodes.Add(r.bones[w.boneIndex2]);
                    if (w.weight3 > 0f && w.boneIndex3 < r.bones.Length) nodes.Add(r.bones[w.boneIndex3]);
                }
            }
            else foreach (int x in r.bones) nodes.Add(x);
        }
        return nodes;
    }
}

public static class C2IStore
{
    public const int Group = 731406;
    public const int Category = 0;
    public const int FirstNo = 73100001;
    const string BundlePrefix = "charatoitem/bake_";
    const string BundleSuffix = ".unity3d";

    public static string Dir { get { return Path.GetFullPath(Path.Combine(Application.dataPath, "../UserData/CharaToItem")); } }
    public static string TexDir { get { return Path.Combine(Dir, "textures"); } }

    public static string BundleName(int no) { return BundlePrefix + no + BundleSuffix; }
    public static bool IsOurBundle(string b) { return b != null && b.StartsWith(BundlePrefix, StringComparison.Ordinal); }

    public static bool TryParseBundle(string b, out int no)
    {
        no = 0;
        if (!IsOurBundle(b)) return false;
        string s = b.Substring(BundlePrefix.Length);
        if (s.EndsWith(BundleSuffix)) s = s.Substring(0, s.Length - BundleSuffix.Length);
        return int.TryParse(s, out no);
    }

    public static string SafeName(string s)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char c in s) sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }

    // ---------------------------------------------------------------- texture files (content addressed)

    public static string SaveTexture(int w, int h, int format, bool mips, bool linear, byte[] data)
    {
        string hash;
        using (MD5 md5 = MD5.Create())
        {
            byte[] head = Encoding.ASCII.GetBytes(w + "x" + h + "f" + format + (mips ? "m" : "") + (linear ? "l" : ""));
            md5.TransformBlock(head, 0, head.Length, null, 0);
            md5.TransformFinalBlock(data, 0, data.Length);
            hash = BitConverter.ToString(md5.Hash).Replace("-", "").ToLowerInvariant();
        }
        Directory.CreateDirectory(TexDir);
        string path = Path.Combine(TexDir, hash + ".c2t");
        if (!File.Exists(path))
        {
            string tmp = path + ".tmp";
            using (FileStream fs = File.Create(tmp))
            {
                BinaryWriter bw = new BinaryWriter(fs);
                bw.Write(TexMagic);
                bw.Write(w); bw.Write(h); bw.Write(format); bw.Write(mips); bw.Write(linear);
                bw.Write(data.Length);
                bw.Flush();
                using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Compress, true)) ds.Write(data, 0, data.Length);
            }
            File.Move(tmp, path);
        }
        return hash;
    }

    const int TexMagic = 0x55324943;   // "CI2U": raw texels (all mip levels), deflated

    static byte[] ReadExactly(Stream s, int n)
    {
        byte[] b = new byte[n];
        int o = 0;
        while (o < n) { int k = s.Read(b, o, n - o); if (k <= 0) throw new EndOfStreamException(); o += k; }
        return b;
    }

    public static Texture2D LoadTexture(string hash)
    {
        string path = Path.Combine(TexDir, hash + ".c2t");
        if (!File.Exists(path)) return null;
        using (FileStream fs = File.OpenRead(path))
        {
            BinaryReader br = new BinaryReader(fs);
            if (br.ReadInt32() != TexMagic) return null;
            int w = br.ReadInt32(), h = br.ReadInt32(), format = br.ReadInt32();
            bool mips = br.ReadBoolean(), linear = br.ReadBoolean();
            int len = br.ReadInt32();
            byte[] data;
            using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Decompress, true)) data = ReadExactly(ds, len);
            Texture2D t = new Texture2D(w, h, (TextureFormat)format, mips, linear);
            t.LoadRawTextureData(data);
            t.Apply(false, true);
            return t;
        }
    }

    // ---------------------------------------------------------------- bake files

    static void W(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
    static void W(BinaryWriter w, Vector4 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }
    static void W(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
    static void W(BinaryWriter w, Bounds b) { W(w, b.center); W(w, b.size); }
    static void W(BinaryWriter w, string s) { w.Write(s ?? ""); }
    static void W(BinaryWriter w, string[] a) { w.Write(a.Length); foreach (string s in a) w.Write(s ?? ""); }
    static void W(BinaryWriter w, int[] a) { w.Write(a.Length); foreach (int i in a) w.Write(i); }
    static Vector3 V3(BinaryReader r) { return new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
    static Vector4 V4(BinaryReader r) { return new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
    static Quaternion Q(BinaryReader r) { return new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); }
    static Bounds B(BinaryReader r) { Vector3 c = V3(r); return new Bounds(c, V3(r)); }
    static string[] SA(BinaryReader r) { string[] a = new string[r.ReadInt32()]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadString(); return a; }
    static float[] FA(BinaryReader r) { float[] a = new float[r.ReadInt32()]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadSingle(); return a; }
    static int[] IA(BinaryReader r) { int[] a = new int[r.ReadInt32()]; for (int i = 0; i < a.Length; i++) a[i] = r.ReadInt32(); return a; }

    const int Magic = 0x31493243;   // "C2I1"

    public static void Write(C2IBake b, string path)
    {
        string tmp = path + ".tmp";
        using (FileStream fs = File.Create(tmp))
        {
            BinaryWriter hw = new BinaryWriter(fs, Encoding.UTF8);
            hw.Write(Magic); hw.Write(C2IBake.Version);
            hw.Write(b.no); W(hw, b.name); W(hw, b.charaName); hw.Write(b.sex); hw.Write(b.created);
            W(hw, b.fkBones.ToArray()); W(hw, b.texHashes.ToArray()); W(hw, b.parts);
            hw.Flush();
            using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Compress, true))
            using (BinaryWriter w = new BinaryWriter(new BufferedStream(ds, 1 << 20), Encoding.UTF8))
            {

            w.Write(b.nodes.Count);
            foreach (C2INode n in b.nodes) { W(w, n.name); w.Write(n.parent); W(w, n.lp); W(w, n.lr); W(w, n.ls); w.Write(n.active); w.Write(n.layer); }

            w.Write(b.sources.Count);
            foreach (C2ISource s in b.sources) { W(w, s.ab); W(w, s.asset); W(w, s.manifest); w.Write(s.kind); }

            w.Write(b.textures.Count);
            foreach (C2ITex t in b.textures)
            {
                W(w, t.name); W(w, t.hash); w.Write(t.srcIndex); W(w, t.refAB); W(w, t.refManifest); w.Write(t.w); w.Write(t.h); w.Write(t.format); w.Write(t.mips); w.Write(t.linear);
                w.Write(t.wrapU); w.Write(t.wrapV); w.Write(t.filter); w.Write(t.aniso); w.Write(t.mipBias);
            }

            w.Write(b.mats.Count);
            foreach (C2IMat m in b.mats)
            {
                W(w, m.name); W(w, m.shader); W(w, m.renderType); w.Write(m.queue); W(w, m.keywords); W(w, m.passesOff);
                w.Write(m.source); W(w, m.srcPath); w.Write(m.srcSlot); W(w, m.srcName); w.Write(m.shaderSource);
                w.Write(m.floats.Count); foreach (KeyValuePair<string, float> kv in m.floats) { w.Write(kv.Key); w.Write(kv.Value); }
                w.Write(m.vectors.Count); foreach (KeyValuePair<string, Vector4> kv in m.vectors) { w.Write(kv.Key); W(w, kv.Value); }
                w.Write(m.slots.Count);
                foreach (C2ITexSlot s in m.slots) { w.Write(s.prop); w.Write(s.scale.x); w.Write(s.scale.y); w.Write(s.offset.x); w.Write(s.offset.y); w.Write(s.mode); w.Write(s.tex); }
            }

            w.Write(b.meshes.Count);
            foreach (C2IMesh m in b.meshes)
            {
                W(w, m.name);
                w.Write(m.srcIndex); W(w, m.srcPath);
                if (m.srcIndex >= 0) continue;
                int vc = m.v.Length;
                w.Write(vc);
                foreach (Vector3 x in m.v) W(w, x);
                w.Write(m.n != null && m.n.Length == vc); if (m.n != null && m.n.Length == vc) foreach (Vector3 x in m.n) W(w, x);
                w.Write(m.t != null && m.t.Length == vc); if (m.t != null && m.t.Length == vc) foreach (Vector4 x in m.t) W(w, x);
                for (int ch = 0; ch < 4; ch++)
                {
                    bool has = m.uv[ch] != null && m.uv[ch].Count == vc;
                    w.Write(has); if (has) foreach (Vector4 x in m.uv[ch]) W(w, x);
                }
                w.Write(m.col != null && m.col.Length == vc);
                if (m.col != null && m.col.Length == vc) foreach (Color32 c in m.col) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
                w.Write(m.bw != null && m.bw.Length == vc);
                if (m.bw != null && m.bw.Length == vc)
                    foreach (BoneWeight x in m.bw)
                    {
                        w.Write(x.boneIndex0); w.Write(x.boneIndex1); w.Write(x.boneIndex2); w.Write(x.boneIndex3);
                        w.Write(x.weight0); w.Write(x.weight1); w.Write(x.weight2); w.Write(x.weight3);
                    }
                w.Write(m.bp != null ? m.bp.Length : 0);
                if (m.bp != null) foreach (Matrix4x4 x in m.bp) for (int i = 0; i < 16; i++) w.Write(x[i]);
                w.Write(m.idx.Length);
                for (int s = 0; s < m.idx.Length; s++) { w.Write(m.topo[s]); W(w, m.idx[s]); }
                W(w, m.bounds);
            }

            w.Write(b.rends.Count);
            foreach (C2IRend r in b.rends)
            {
                w.Write(r.node); w.Write(r.skinned); w.Write(r.mesh); W(w, r.mats); W(w, r.bones); w.Write(r.rootBone);
                W(w, r.localBounds); w.Write(r.quality); w.Write(r.shadow); w.Write(r.sortingOrder); w.Write(r.offscreen); w.Write(r.receive);
                w.Write(r.anchor); w.Write(r.lightProbe); w.Write(r.reflProbe); w.Write(r.renderingLayerMask);
                w.Write(r.blend.Length); foreach (float x in r.blend) w.Write(x);
            }

            w.Write(b.exprEnable);
            w.Write(b.expr.Count);
            foreach (C2IExpr e in b.expr)
            {
                W(w, e.elementName); w.Write(e.enable); w.Write(e.enableLookAt); w.Write(e.enableCorrect); w.Write(e.index); w.Write(e.categoryNo);
                W(w, e.lookAtName); W(w, e.targetName); W(w, e.upAxisName);
                w.Write(e.targetAxisType); w.Write(e.upAxisType); w.Write(e.sourceAxisType); w.Write(e.limitAxisType); w.Write(e.laRotOrder);
                w.Write(e.limitMin); w.Write(e.limitMax);
                W(w, e.correctName); W(w, e.referenceName); w.Write(e.calcType); w.Write(e.crRotOrder); w.Write(e.charmRate);
                w.Write(e.useRX); w.Write(e.rxMin); w.Write(e.rxMax); w.Write(e.useRY); w.Write(e.ryMin); w.Write(e.ryMax);
                w.Write(e.useRZ); w.Write(e.rzMin); w.Write(e.rzMax);
            }
            w.Write(b.fillInside);
            w.Write(b.mats.Count); foreach (C2IMat m in b.mats) W(w, m.inside);
            w.Write(b.rends.Count); foreach (C2IRend r in b.rends) { W(w, r.part); w.Write(r.inside); }
            W(w, b.sectionColor); w.Write(b.shellsOwnInside);
            w.Write(Magic);
            }
        }
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    // headerOnly: just what the item list needs (fast; read for every bake at startup)
    public static C2IBake Read(string path, bool headerOnly)
    {
        using (FileStream fs = File.OpenRead(path))
        {
            BinaryReader hr = new BinaryReader(fs, Encoding.UTF8);
            if (hr.ReadInt32() != Magic) throw new Exception("not a CharaToItem bake: " + path);
            int ver = hr.ReadInt32();
            if (ver < C2IBake.OldestReadable || ver > C2IBake.Version) throw new Exception("bake format " + ver + " is not one this plugin reads (" + C2IBake.OldestReadable + "-" + C2IBake.Version + ")");
            C2IBake b = new C2IBake();
            b.file = path;
            b.no = hr.ReadInt32(); b.name = hr.ReadString(); b.charaName = hr.ReadString(); b.sex = hr.ReadInt32(); b.created = hr.ReadInt64();
            b.fkBones = SA(hr).ToList(); b.texHashes = SA(hr).ToList();
            if (ver >= 5) b.parts = hr.ReadString();
            if (headerOnly) return b;
            using (DeflateStream ds = new DeflateStream(fs, CompressionMode.Decompress, true))
            {
            BinaryReader r = new BinaryReader(new BufferedStream(ds, 1 << 20), Encoding.UTF8);

            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
                b.nodes.Add(new C2INode { name = r.ReadString(), parent = r.ReadInt32(), lp = V3(r), lr = Q(r), ls = V3(r), active = r.ReadBoolean(), layer = r.ReadInt32() });

            n = r.ReadInt32();
            for (int i = 0; i < n; i++) b.sources.Add(new C2ISource { ab = r.ReadString(), asset = r.ReadString(), manifest = r.ReadString(), kind = r.ReadInt32() });

            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
                b.textures.Add(new C2ITex
                {
                    name = r.ReadString(), hash = r.ReadString(), srcIndex = r.ReadInt32(), refAB = r.ReadString(), refManifest = r.ReadString(), w = r.ReadInt32(), h = r.ReadInt32(), format = r.ReadInt32(),
                    mips = r.ReadBoolean(), linear = r.ReadBoolean(), wrapU = r.ReadInt32(), wrapV = r.ReadInt32(),
                    filter = r.ReadInt32(), aniso = r.ReadInt32(), mipBias = r.ReadSingle()
                });

            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                C2IMat m = new C2IMat();
                m.name = r.ReadString(); m.shader = r.ReadString(); m.renderType = r.ReadString(); m.queue = r.ReadInt32();
                m.keywords = SA(r); m.passesOff = SA(r);
                m.source = r.ReadInt32(); m.srcPath = r.ReadString(); m.srcSlot = r.ReadInt32(); m.srcName = r.ReadString(); m.shaderSource = r.ReadInt32();
                int c = r.ReadInt32(); for (int k = 0; k < c; k++) { string key = r.ReadString(); m.floats[key] = r.ReadSingle(); }
                c = r.ReadInt32(); for (int k = 0; k < c; k++) { string key = r.ReadString(); m.vectors[key] = V4(r); }
                c = r.ReadInt32();
                for (int k = 0; k < c; k++)
                {
                    C2ITexSlot s = new C2ITexSlot();
                    s.prop = r.ReadString();
                    s.scale = new Vector2(r.ReadSingle(), r.ReadSingle());
                    s.offset = new Vector2(r.ReadSingle(), r.ReadSingle());
                    s.mode = r.ReadInt32(); s.tex = r.ReadInt32();
                    m.slots.Add(s);
                }
                b.mats.Add(m);
            }

            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                C2IMesh m = new C2IMesh();
                m.name = r.ReadString();
                m.srcIndex = r.ReadInt32(); m.srcPath = r.ReadString();
                if (m.srcIndex >= 0) { b.meshes.Add(m); continue; }
                int vc = r.ReadInt32();
                m.v = new Vector3[vc]; for (int k = 0; k < vc; k++) m.v[k] = V3(r);
                if (r.ReadBoolean()) { m.n = new Vector3[vc]; for (int k = 0; k < vc; k++) m.n[k] = V3(r); }
                if (r.ReadBoolean()) { m.t = new Vector4[vc]; for (int k = 0; k < vc; k++) m.t[k] = V4(r); }
                for (int ch = 0; ch < 4; ch++)
                    if (r.ReadBoolean()) { m.uv[ch] = new List<Vector4>(vc); for (int k = 0; k < vc; k++) m.uv[ch].Add(V4(r)); }
                if (r.ReadBoolean()) { m.col = new Color32[vc]; for (int k = 0; k < vc; k++) m.col[k] = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte()); }
                if (r.ReadBoolean())
                {
                    m.bw = new BoneWeight[vc];
                    for (int k = 0; k < vc; k++)
                        m.bw[k] = new BoneWeight
                        {
                            boneIndex0 = r.ReadInt32(), boneIndex1 = r.ReadInt32(), boneIndex2 = r.ReadInt32(), boneIndex3 = r.ReadInt32(),
                            weight0 = r.ReadSingle(), weight1 = r.ReadSingle(), weight2 = r.ReadSingle(), weight3 = r.ReadSingle()
                        };
                }
                int bpc = r.ReadInt32();
                m.bp = new Matrix4x4[bpc];
                for (int k = 0; k < bpc; k++) { Matrix4x4 x = new Matrix4x4(); for (int e = 0; e < 16; e++) x[e] = r.ReadSingle(); m.bp[k] = x; }
                int sc = r.ReadInt32();
                m.topo = new int[sc]; m.idx = new int[sc][];
                for (int s = 0; s < sc; s++) { m.topo[s] = r.ReadInt32(); m.idx[s] = IA(r); }
                m.bounds = B(r);
                b.meshes.Add(m);
            }

            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
                b.rends.Add(new C2IRend
                {
                    node = r.ReadInt32(), skinned = r.ReadBoolean(), mesh = r.ReadInt32(), mats = IA(r), bones = IA(r), rootBone = r.ReadInt32(),
                    localBounds = B(r), quality = r.ReadInt32(), shadow = r.ReadInt32(), sortingOrder = r.ReadInt32(),
                    offscreen = r.ReadBoolean(), receive = r.ReadBoolean(),
                    anchor = r.ReadInt32(), lightProbe = r.ReadInt32(), reflProbe = r.ReadInt32(), renderingLayerMask = r.ReadUInt32(),
                    blend = FA(r)
                });

            b.exprEnable = r.ReadBoolean();
            n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                C2IExpr e = new C2IExpr();
                e.elementName = r.ReadString(); e.enable = r.ReadBoolean(); e.enableLookAt = r.ReadBoolean(); e.enableCorrect = r.ReadBoolean();
                e.index = r.ReadInt32(); e.categoryNo = r.ReadInt32();
                e.lookAtName = r.ReadString(); e.targetName = r.ReadString(); e.upAxisName = r.ReadString();
                e.targetAxisType = r.ReadInt32(); e.upAxisType = r.ReadInt32(); e.sourceAxisType = r.ReadInt32(); e.limitAxisType = r.ReadInt32(); e.laRotOrder = r.ReadInt32();
                e.limitMin = r.ReadSingle(); e.limitMax = r.ReadSingle();
                e.correctName = r.ReadString(); e.referenceName = r.ReadString(); e.calcType = r.ReadInt32(); e.crRotOrder = r.ReadInt32(); e.charmRate = r.ReadSingle();
                e.useRX = r.ReadBoolean(); e.rxMin = r.ReadSingle(); e.rxMax = r.ReadSingle();
                e.useRY = r.ReadBoolean(); e.ryMin = r.ReadSingle(); e.ryMax = r.ReadSingle();
                e.useRZ = r.ReadBoolean(); e.rzMin = r.ReadSingle(); e.rzMax = r.ReadSingle();
                b.expr.Add(e);
            }
            if (ver >= 5)
            {
                b.fillInside = r.ReadBoolean();
                int nm = r.ReadInt32();
                for (int i = 0; i < nm; i++) { Vector4 v = V4(r); if (i < b.mats.Count) b.mats[i].inside = v; }
            }
            if (ver >= 6)
            {
                int nr = r.ReadInt32();
                for (int i = 0; i < nr; i++)
                {
                    string part = r.ReadString(); int ins = r.ReadInt32();
                    if (i < b.rends.Count) { b.rends[i].part = part; b.rends[i].inside = ins; }
                }
                b.sectionColor = V4(r); b.shellsOwnInside = r.ReadBoolean();
            }
            if (r.ReadInt32() != Magic) throw new Exception("bake file is truncated: " + path);
            return b;
            }
        }
    }
}

// ==================================================================================================== property names

public static partial class C2INames
{
    static string[] floats, vectors;
    static HashSet<string> untyped;

    public static string[] Floats { get { Init(); return floats; } }
    public static string[] Vectors { get { Init(); return vectors; } }
    public static bool Untyped(string name) { Init(); return untyped.Contains(name); }

    // MaterialEditor ships an XML of every shader property it can edit (with types). The game's own names have no
    // type; those are read both ways and kept where the value is not 0 (a read with the wrong type gives 0).
    static void Init()
    {
        if (floats != null) return;
        HashSet<string> f = new HashSet<string>(), v = new HashSet<string>();
        try
        {
            Assembly me = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name.IndexOf("MaterialEditor", StringComparison.OrdinalIgnoreCase) >= 0
                && a.GetManifestResourceNames().Any(n => n.EndsWith("default.xml")));
            if (me != null)
            {
                string res = me.GetManifestResourceNames().First(n => n.EndsWith("default.xml"));
                using (Stream st = me.GetManifestResourceStream(res))
                {
                    System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
                    doc.Load(st);
                    foreach (System.Xml.XmlNode p in doc.GetElementsByTagName("Property"))
                    {
                        string name = p.Attributes["Name"] != null ? "_" + p.Attributes["Name"].Value : null;
                        string type = p.Attributes["Type"] != null ? p.Attributes["Type"].Value : "";
                        if (name == null) continue;
                        if (type == "Float") f.Add(name);
                        else if (type == "Color" || type == "Vector") v.Add(name);
                    }
                }
            }
            else CharaToItem.Log.LogWarning("MaterialEditor property list not found; using the built-in names only");
        }
        catch (Exception e) { CharaToItem.Log.LogWarning("MaterialEditor property list: " + e.Message); }
        untyped = new HashSet<string>();
        foreach (string s in Game)
            if (!f.Contains(s) && !v.Contains(s)) { untyped.Add(s); f.Add(s); v.Add(s); }
        floats = f.ToArray();
        vectors = v.ToArray();
    }
}

// ==================================================================================================== parts

public class C2IPart
{
    public string key, label, item = "";
    public Transform root;
    public bool hairAcc;
}

// The character split the way the game builds it: the body, the head, each hair slot, each clothes kind, each
// accessory slot. A renderer belongs to the part whose root is its nearest ancestor; anything else (animation
// props, plugin objects outside the parts) is "other".
public static class C2IParts
{
    static readonly string[] HairLabels = { "Hair (back)", "Hair (front)", "Hair (side)", "Hair (extension)" };
    static readonly string[] ClothesLabels = { "Top", "Bottom", "Inner top", "Inner bottom", "Gloves", "Pantyhose", "Socks", "Shoes" };

    static bool Real(ListInfoBase li)
    {
        if (li == null) return false;
        string d = li.GetInfo(ChaListDefine.KeyType.MainData);
        return !string.IsNullOrEmpty(d) && d != "0" && d != "p_dummy";
    }

    public static List<C2IPart> Of(ChaControl cha)
    {
        List<C2IPart> l = new List<C2IPart>();
        if (cha.objBody != null) l.Add(new C2IPart { key = "body", label = "Body", root = cha.objBody.transform });
        if (cha.objHead != null) l.Add(new C2IPart { key = "head", label = "Head (face)", root = cha.objHead.transform });
        for (int i = 0; i < cha.objHair.Length; i++)
            if (cha.objHair[i] != null && i < cha.infoHair.Length && Real(cha.infoHair[i]))
                l.Add(new C2IPart { key = "hair" + i, label = i < HairLabels.Length ? HairLabels[i] : "Hair " + i, item = cha.infoHair[i].Name, root = cha.objHair[i].transform });
        for (int k = 0; k < cha.objClothes.Length; k++)
            if (cha.objClothes[k] != null && k < cha.infoClothes.Length && Real(cha.infoClothes[k]))
                l.Add(new C2IPart { key = "clothes" + k, label = k < ClothesLabels.Length ? ClothesLabels[k] : "Clothes " + k, item = cha.infoClothes[k].Name, root = cha.objClothes[k].transform });
        for (int a = 0; a < cha.objAccessory.Length; a++)
            if (cha.objAccessory[a] != null && a < cha.infoAccessory.Length && cha.infoAccessory[a] != null)
            {
                CmpAccessory ca = cha.cmpAccessory != null && a < cha.cmpAccessory.Length ? cha.cmpAccessory[a] : null;
                bool hair = ca != null && ca.typeHair;
                l.Add(new C2IPart { key = "acc" + a, label = "Acc " + (a + 1) + (hair ? " (hair)" : ""), item = cha.infoAccessory[a].Name, root = cha.objAccessory[a].transform, hairAcc = hair });
            }
        return l;
    }

    public static Dictionary<Transform, string> Roots(List<C2IPart> parts)
    {
        Dictionary<Transform, string> d = new Dictionary<Transform, string>();
        foreach (C2IPart p in parts) if (p.root != null && !d.ContainsKey(p.root)) d[p.root] = p.key;
        return d;
    }

    public static string KeyOf(Transform t, Dictionary<Transform, string> roots)
    {
        for (Transform p = t; p != null; p = p.parent)
        {
            string k;
            if (roots.TryGetValue(p, out k)) return k;
        }
        return "other";
    }

    // presets: "body" (body, head, hair), "hair", "clothes", "acc" (accessories that are not hair)
    public static HashSet<string> Preset(List<C2IPart> parts, string which)
    {
        HashSet<string> k = new HashSet<string>();
        foreach (C2IPart p in parts)
        {
            bool hair = p.key.StartsWith("hair") || p.hairAcc;
            if (which == "body" && (p.key == "body" || p.key == "head" || hair)) k.Add(p.key);
            else if (which == "hair" && hair) k.Add(p.key);
            else if (which == "clothes" && p.key.StartsWith("clothes")) k.Add(p.key);
            else if (which == "acc" && p.key.StartsWith("acc") && !p.hairAcc) k.Add(p.key);
        }
        return k;
    }

    // a short name for the parts of a partial bake (goes into the item name)
    public static string Label(List<C2IPart> parts, HashSet<string> keys)
    {
        if (keys == null) return "";
        List<string> names = new List<string>();
        foreach (C2IPart p in parts)
            if (keys.Contains(p.key)) names.Add(p.key.StartsWith("clothes") || p.key.StartsWith("acc") ? (string.IsNullOrEmpty(p.item) ? p.label : p.item) : p.label);
        if (keys.Contains("other")) names.Add("Other");
        if (names.Count == 0) return "(nothing)";
        if (names.Count <= 2) return string.Join(" + ", names.ToArray());
        foreach (string w in new[] { "body", "hair", "clothes", "acc" })
        {
            HashSet<string> pre = Preset(parts, w);
            if (pre.SetEquals(keys)) return w == "body" ? "Body" : w == "hair" ? "Hair" : w == "clothes" ? "Clothes" : "Accessories";
        }
        if (keys.All(k => k.StartsWith("clothes"))) return "Clothes (" + names.Count + ")";
        if (keys.All(k => k.StartsWith("acc"))) return "Accessories (" + names.Count + ")";
        return names.Count + " parts";
    }
}

// ==================================================================================================== capture

public static class C2ICapture
{
    static readonly string[] Passes = { "ShadowCaster", "ForwardBase", "ForwardAdd", "Deferred", "Meta", "Always", "Outline" };

    public class Ctx
    {
        public OCIChar oc;
        public ChaControl cha;
        public C2IBake bake;
        public Dictionary<Transform, int> nodeIndex = new Dictionary<Transform, int>();
        public List<Renderer> rends = new List<Renderer>();
        public Dictionary<Transform, int> sourceRoots = new Dictionary<Transform, int>();
        public List<UnityEngine.Object> sourceObjs = new List<UnityEngine.Object>();   // GameObject (prefab) or Material
        public Dictionary<int, int> shaderSrc = new Dictionary<int, int>();   // shader instance -> first source holding it
        public Dictionary<int, int> texSrc = new Dictionary<int, int>();      // texture instance -> first source using it
        public int faceMatSrc = -1, bodyMatSrc = -1;
        public Dictionary<int, int> meshIndex = new Dictionary<int, int>();       // referenced meshes
        public Dictionary<int, int> meshDataIndex = new Dictionary<int, int>();   // stored, unshaped meshes
        public Dictionary<int, int> matIndex = new Dictionary<int, int>();
        public Dictionary<int, int> texIndex = new Dictionary<int, int>();
        public List<KeyValuePair<int, Texture>> pendingTex = new List<KeyValuePair<int, Texture>>();   // textures whose texels are stored
        public List<KeyValuePair<int, Texture>> unresolved = new List<KeyValuePair<int, Texture>>();   // maybe list textures
        public StringBuilder log = new StringBuilder();
        public StringBuilder timing = new StringBuilder();
        public long msMesh, msList;
        public Dictionary<Renderer, string> rendKey = new Dictionary<Renderer, string>();
        public List<Renderer> liveRends = new List<Renderer>();   // the live renderer of each bake.rends entry
    }

    // Step 1 (instant): hierarchy, meshes, materials. Textures that must be stored are queued in ctx.pendingTex.
    // keys: the parts to bake (null: the whole character); fillWanted: draw the insides of parts left hollow
    public static Ctx Begin(OCIChar oc, int no, string name, int fkGroups, HashSet<string> keys, bool fillWanted)
    {
        Ctx c = new Ctx();
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        c.oc = oc;
        c.cha = oc.charInfo;
        c.bake = new C2IBake { no = no, name = name, charaName = c.cha.fileParam.fullname, sex = c.cha.sex, created = DateTime.Now.Ticks };
        Transform root = c.cha.objRoot.transform;
        List<C2IPart> parts = C2IParts.Of(c.cha);
        Dictionary<Transform, string> partRoots = C2IParts.Roots(parts);
        c.bake.parts = C2IParts.Label(parts, keys);

        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            Mesh m = MeshOf(r);
            if (m == null) continue;
            string key = C2IParts.KeyOf(r.transform, partRoots);
            if (keys != null && !keys.Contains(key)) continue;
            c.rends.Add(r);
            c.rendKey[r] = key;
        }

        // ---- transforms: the whole skeleton, every kept renderer and its bones, the FK targets, and their ancestors
        HashSet<Transform> need = new HashSet<Transform>();
        foreach (Transform t in c.cha.objBodyBone.GetComponentsInChildren<Transform>(true)) need.Add(t);
        List<Transform> fkTargets = new List<Transform>();
        foreach (OCIChar.BoneInfo bi in oc.listBones)
        {
            if (bi == null || bi.guideObject == null || !bi.boneWeight || ((int)bi.boneGroup & fkGroups) == 0) continue;
            Transform t = bi.guideObject.transformTarget;
            if (t != null && !fkTargets.Contains(t)) fkTargets.Add(t);
        }
        BuildNodes(c, root, need, fkTargets);

        c.timing.Append("nodes " + sw.ElapsedMilliseconds);
        // ---- where materials, shaders and textures came from: the game's hair shader materials, the face/body
        // skin materials, then the part prefabs
        AddMaterialSource(c, "chara/hair_shader_mat.unity3d", "hair_dithering", "abdata");
        AddMaterialSource(c, "chara/hair_shader_mat.unity3d", "hair_cutout", "abdata");
        if (c.cha.customMatBody != null) c.bodyMatSrc = AddMaterialSource(c, "chara/mm_base.unity3d", BaseName(c.cha.customMatBody.name), "abdata");
        if (c.cha.customMatFace != null && c.cha.infoHead != null)
            c.faceMatSrc = AddMaterialSource(c, c.cha.infoHead.GetInfo(ChaListDefine.KeyType.MainAB), c.cha.infoHead.GetInfo(ChaListDefine.KeyType.MatData), c.cha.infoHead.GetInfo(ChaListDefine.KeyType.MainManifest));
        AddSource(c, c.cha.objBody, "chara/oo_base.unity3d", c.cha.objBody != null ? c.cha.objBody.name : "", "abdata");
        AddSource(c, c.cha.objHead, c.cha.infoHead);
        for (int i = 0; i < c.cha.objHair.Length; i++) AddSource(c, c.cha.objHair[i], c.cha.infoHair.Length > i ? c.cha.infoHair[i] : null);
        for (int i = 0; i < c.cha.objClothes.Length; i++) AddSource(c, c.cha.objClothes[i], c.cha.infoClothes.Length > i ? c.cha.infoClothes[i] : null);
        for (int i = 0; i < c.cha.objAccessory.Length; i++) AddSource(c, c.cha.objAccessory[i], c.cha.infoAccessory.Length > i ? c.cha.infoAccessory[i] : null);

        c.timing.Append(", sources " + sw.ElapsedMilliseconds);
        // ---- renderers, meshes, materials
        CaptureRenderers(c);

        ResolveListTextures(c);

        // A part baked without the garment over it: the game hides the body (and inner wear) under the top with
        // _alpha_a/_alpha_b and under the bottom with _alpha_d; 0 shows everything, as with the garment taken off.
        if (keys != null)
        {
            bool top = keys.Contains("clothes0"), bot = keys.Contains("clothes1");
            foreach (C2IMat cm in c.bake.mats)
            {
                if (!top) { ZeroProp(cm, "_alpha_a"); ZeroProp(cm, "_alpha_b"); }
                if (!bot) ZeroProp(cm, "_alpha_d");
            }
        }
        bool hasBody = c.rendKey.Values.Contains("body"), hasHead = c.rendKey.Values.Contains("head");
        c.bake.fillInside = fillWanted && !(hasBody && hasHead);
        c.timing.Append(", renderers " + sw.ElapsedMilliseconds + " (meshes " + c.msMesh + ", list index " + c.msList + ")");
        // ---- twist-bone corrections
        CharaUtils.Expression ex = c.cha.expression;
        if (ex != null && ex.info != null)
        {
            c.bake.exprEnable = ex.enable;
            foreach (CharaUtils.Expression.ScriptInfo si in ex.info)
            {
                C2IExpr e = new C2IExpr();
                e.elementName = si.elementName; e.enable = si.enable; e.enableLookAt = si.enableLookAt; e.enableCorrect = si.enableCorrect;
                e.index = si.index; e.categoryNo = si.categoryNo;
                if (si.lookAt != null)
                {
                    e.lookAtName = si.lookAt.lookAtName; e.targetName = si.lookAt.targetName; e.upAxisName = si.lookAt.upAxisName;
                    e.targetAxisType = (int)si.lookAt.targetAxisType; e.upAxisType = (int)si.lookAt.upAxisType; e.sourceAxisType = (int)si.lookAt.sourceAxisType;
                    e.limitAxisType = (int)si.lookAt.limitAxisType; e.laRotOrder = (int)si.lookAt.rotOrder;
                    e.limitMin = si.lookAt.limitMin; e.limitMax = si.lookAt.limitMax;
                }
                if (si.correct != null)
                {
                    e.correctName = si.correct.correctName; e.referenceName = si.correct.referenceName;
                    e.calcType = (int)si.correct.calcType; e.crRotOrder = (int)si.correct.rotOrder; e.charmRate = si.correct.charmRate;
                    e.useRX = si.correct.useRX; e.rxMin = si.correct.valRXMin; e.rxMax = si.correct.valRXMax;
                    e.useRY = si.correct.useRY; e.ryMin = si.correct.valRYMin; e.ryMax = si.correct.valRYMax;
                    e.useRZ = si.correct.useRZ; e.rzMin = si.correct.valRZMin; e.rzMax = si.correct.valRZMax;
                }
                c.bake.expr.Add(e);
            }
        }
        return c;
    }

    // nodes: `need`, every kept renderer with its bones, the FK targets, and all their ancestors up to root,
    // in depth-first order; FK target names made unique (ItemFKCtrl finds bones by name)
    public static void BuildNodes(Ctx c, Transform root, HashSet<Transform> need, List<Transform> fkTargets)
    {
        foreach (Renderer r in c.rends)
        {
            need.Add(r.transform);
            SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
            if (smr != null)
            {
                foreach (Transform b in smr.bones) if (b != null) need.Add(b);
                if (smr.rootBone != null) need.Add(smr.rootBone);
            }
        }
        foreach (Transform t in fkTargets) need.Add(t);
        HashSet<Transform> closure = new HashSet<Transform>();
        foreach (Transform t in need)
        {
            List<Transform> chain = new List<Transform>();
            Transform p = t;
            while (p != null && p != root) { chain.Add(p); p = p.parent; }
            if (p == null) continue;   // not under the root
            foreach (Transform x in chain) closure.Add(x);
        }
        closure.Add(root);
        Stack<KeyValuePair<Transform, int>> stack = new Stack<KeyValuePair<Transform, int>>();
        stack.Push(new KeyValuePair<Transform, int>(root, -1));
        while (stack.Count > 0)
        {
            KeyValuePair<Transform, int> kv = stack.Pop();
            Transform t = kv.Key;
            int i = c.bake.nodes.Count;
            c.nodeIndex[t] = i;
            bool isRoot = t == root;
            c.bake.nodes.Add(new C2INode
            {
                name = t.name, parent = kv.Value,
                lp = isRoot ? Vector3.zero : t.localPosition,
                lr = isRoot ? Quaternion.identity : t.localRotation,
                ls = isRoot ? Vector3.one : t.localScale,
                active = isRoot || t.gameObject.activeSelf,
                layer = t.gameObject.layer
            });
            for (int k = t.childCount - 1; k >= 0; k--)   // reversed so children come out in order
            {
                Transform ch = t.GetChild(k);
                if (closure.Contains(ch)) stack.Push(new KeyValuePair<Transform, int>(ch, i));
            }
        }

        Dictionary<string, List<int>> byName = new Dictionary<string, List<int>>();
        for (int i = 0; i < c.bake.nodes.Count; i++)
        {
            List<int> l;
            if (!byName.TryGetValue(c.bake.nodes[i].name, out l)) byName[c.bake.nodes[i].name] = l = new List<int>();
            l.Add(i);
        }
        // (a duplicate that is itself an FK target gets its own "~index" name when its turn comes)
        HashSet<string> used = new HashSet<string>();
        List<Transform> fk = fkTargets.Where(t => c.nodeIndex.ContainsKey(t)).ToList();
        HashSet<int> fkNodes = new HashSet<int>(fk.Select(t => c.nodeIndex[t]));
        foreach (Transform t in fk)
        {
            int i = c.nodeIndex[t];
            string nm = c.bake.nodes[i].name;
            List<int> same;
            if (used.Contains(nm)) { nm = nm + "~" + i; c.bake.nodes[i].name = nm; }
            else if (byName.TryGetValue(nm, out same))
                foreach (int j in same) if (j != i && !fkNodes.Contains(j)) c.bake.nodes[j].name = nm + "~" + j;
            used.Add(nm);
            c.bake.fkBones.Add(nm);
        }
    }

    // renderers, meshes and materials of c.rends
    public static void CaptureRenderers(Ctx c)
    {
        foreach (Renderer r in c.rends)
        {
            SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
            C2IRend cr = new C2IRend();
            cr.node = c.nodeIndex[r.transform];
            cr.skinned = smr != null;
            string key;
            cr.part = c.rendKey.TryGetValue(r, out key) ? key : "";
            System.Diagnostics.Stopwatch swm = System.Diagnostics.Stopwatch.StartNew();
            cr.mesh = CaptureMesh(c, r, cr);
            c.msMesh += swm.ElapsedMilliseconds;
            if (cr.mesh < 0) continue;
            Material[] ms = r.sharedMaterials;
            cr.mats = new int[ms.Length];
            for (int k = 0; k < ms.Length; k++) cr.mats[k] = ms[k] == null ? -1 : CaptureMaterial(c, ms[k], r, k);
            if (smr != null)
            {
                Transform[] bones = smr.bones;
                cr.bones = new int[bones.Length];
                for (int k = 0; k < bones.Length; k++)
                {
                    int bi;
                    cr.bones[k] = bones[k] != null && c.nodeIndex.TryGetValue(bones[k], out bi) ? bi : -1;
                }
                int rb;
                cr.rootBone = smr.rootBone != null && c.nodeIndex.TryGetValue(smr.rootBone, out rb) ? rb : -1;
                cr.localBounds = smr.localBounds;
                cr.quality = (int)smr.quality;
                cr.offscreen = smr.updateWhenOffscreen;
            }
            cr.shadow = (int)r.shadowCastingMode;
            cr.receive = r.receiveShadows;
            cr.sortingOrder = r.sortingOrder;
            int an;
            cr.anchor = r.probeAnchor != null && c.nodeIndex.TryGetValue(r.probeAnchor, out an) ? an : -1;
            cr.lightProbe = (int)r.lightProbeUsage;
            cr.reflProbe = (int)r.reflectionProbeUsage;
            cr.renderingLayerMask = r.renderingLayerMask;
            c.bake.rends.Add(cr);
            c.liveRends.Add(r);
        }
    }

    // Any Studio item (not one of ours): its prefab is the source of meshes, materials and shaders.
    public static Ctx BeginItem(OCIItem oi, int no, string name)
    {
        Ctx c = new Ctx();
        Transform root = oi.objectItem.transform;
        c.bake = new C2IBake { no = no, name = name, charaName = name, sex = -1, created = DateTime.Now.Ticks };
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (MeshOf(r) == null) continue;
            c.rends.Add(r);
            c.rendKey[r] = "item";
        }
        Studio.Info.ItemLoadInfo li = null;
        Dictionary<int, Dictionary<int, Studio.Info.ItemLoadInfo>> cats;
        Dictionary<int, Studio.Info.ItemLoadInfo> items;
        if (Singleton<Studio.Info>.Instance.dicItemLoadInfo.TryGetValue(oi.itemInfo.group, out cats) && cats.TryGetValue(oi.itemInfo.category, out items))
            items.TryGetValue(oi.itemInfo.no, out li);
        List<Transform> fk = new List<Transform>();
        if (li != null && li.bones != null)
            foreach (string bn in li.bones) { Transform t = FindDeep(root, bn); if (t != null && !fk.Contains(t)) fk.Add(t); }
        BuildNodes(c, root, new HashSet<Transform>(), fk);
        if (li != null) AddSource(c, root.gameObject, li.bundlePath, li.fileName, li.manifest);
        CaptureRenderers(c);
        ResolveListTextures(c);
        return c;
    }

    public static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++) { Transform r = FindDeep(t.GetChild(i), name); if (r != null) return r; }
        return null;
    }

    // a mesh's data as a stored mesh, with the renderer's current blend shape weights baked in
    public static C2IMesh MeshData(Mesh src, SkinnedMeshRenderer smr)
    {
        C2IMesh m = new C2IMesh();
        m.name = src.name;
        m.v = src.vertices;
        m.n = src.normals;
        m.t = src.tangents;
        for (int ch = 0; ch < 4; ch++)
        {
            List<Vector4> l = new List<Vector4>();
            src.GetUVs(ch, l);
            if (l.Count == m.v.Length) m.uv[ch] = l;
        }
        m.col = src.colors32;
        m.bw = src.boneWeights;
        m.bp = src.bindposes;
        int sc = src.subMeshCount;
        m.topo = new int[sc];
        m.idx = new int[sc][];
        for (int s2 = 0; s2 < sc; s2++) { m.topo[s2] = (int)src.GetTopology(s2); m.idx[s2] = src.GetIndices(s2); }
        m.bounds = src.bounds;
        bool shaped = false;
        if (smr != null)
            for (int s2 = 0; s2 < src.blendShapeCount; s2++) if (Mathf.Abs(smr.GetBlendShapeWeight(s2)) > 1e-4f) { shaped = true; break; }
        if (shaped) BakeBlendShapes(src, smr, m);
        return m;
    }

    static void ZeroProp(C2IMat cm, string p)
    {
        if (!cm.floats.ContainsKey(p) && !cm.vectors.ContainsKey(p)) return;
        cm.floats[p] = 0f;
        if (cm.vectors.ContainsKey(p)) cm.vectors[p] = Vector4.zero;
    }

    // the average colour of a skin material, darkened: the solid colour seen through openings of hollow skin
    static Vector4 InsideColor(Material m)
    {
        Color avg = new Color(0.55f, 0.42f, 0.38f, 1f);
        try
        {
            Texture t = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
            if (t != null)
            {
                RenderTexture rt = RenderTexture.GetTemporary(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Color32[] px = BlitRead(t, rt);
                RenderTexture.ReleaseTemporary(rt);
                Vector3 sum = Vector3.zero;
                foreach (Color32 q in px) sum += new Vector3(q.r, q.g, q.b);
                sum /= px.Length * 255f;
                avg = new Color(sum.x, sum.y, sum.z, 1f).gamma;
            }
            if (m.HasProperty("_Color")) { Color k = m.GetColor("_Color"); avg = new Color(avg.r * k.r, avg.g * k.g, avg.b * k.b, 1f); }
        }
        catch (Exception) { }
        return new Vector4(avg.r * 0.6f, avg.g * 0.6f, avg.b * 0.6f, 1f);
    }

    static Mesh MeshOf(Renderer r)
    {
        SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
        if (smr != null) return smr.sharedMesh;
        MeshFilter mf = r.GetComponent<MeshFilter>();
        return mf != null ? mf.sharedMesh : null;
    }

    static void AddSource(Ctx c, GameObject part, ListInfoBase info)
    {
        if (part == null || info == null) return;
        AddSource(c, part, info.GetInfo(ChaListDefine.KeyType.MainAB), info.GetInfo(ChaListDefine.KeyType.MainData), info.GetInfo(ChaListDefine.KeyType.MainManifest));
    }

    static void AddSource(Ctx c, GameObject part, string ab, string asset, string manifest)
    {
        if (part == null || string.IsNullOrEmpty(ab) || string.IsNullOrEmpty(asset) || asset == "0" || asset == "p_dummy") return;
        if (c.sourceRoots.ContainsKey(part.transform)) return;
        GameObject prefab = null;
        try { prefab = CommonLib.LoadAsset<GameObject>(ab, asset, false, manifest == "0" ? "" : manifest); }
        catch (Exception e) { c.log.AppendLine("source " + ab + " / " + asset + ": " + e.Message); }
        if (prefab == null) { c.log.AppendLine("source not loadable: " + ab + " / " + asset); return; }
        int idx = c.bake.sources.Count;
        c.sourceRoots[part.transform] = idx;
        c.sourceObjs.Add(prefab);
        c.bake.sources.Add(new C2ISource { ab = ab, asset = asset, manifest = manifest == "0" ? "" : manifest, kind = 0 });
        foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
            foreach (Material m in r.sharedMaterials) IndexSourceMaterial(c, m, idx);
    }

    static int AddMaterialSource(Ctx c, string ab, string asset, string manifest)
    {
        if (string.IsNullOrEmpty(ab) || string.IsNullOrEmpty(asset) || asset == "0") return -1;
        manifest = manifest == "0" ? "" : manifest;
        Material mat = null;
        try { mat = CommonLib.LoadAsset<Material>(ab, asset, false, manifest); }
        catch (Exception e) { c.log.AppendLine("material source " + ab + " / " + asset + ": " + e.Message); }
        if (mat == null) { c.log.AppendLine("material source not loadable: " + ab + " / " + asset); return -1; }
        int idx = c.bake.sources.Count;
        c.sourceObjs.Add(mat);
        c.bake.sources.Add(new C2ISource { ab = ab, asset = asset, manifest = manifest, kind = 1 });
        IndexSourceMaterial(c, mat, idx);
        return idx;
    }

    static void IndexSourceMaterial(Ctx c, Material m, int idx)
    {
        if (m == null) return;
        if (m.shader != null && !c.shaderSrc.ContainsKey(m.shader.GetInstanceID())) c.shaderSrc[m.shader.GetInstanceID()] = idx;
        foreach (string p in m.GetTexturePropertyNames())
        {
            Texture t = m.GetTexture(p);
            if (t != null && !c.texSrc.ContainsKey(t.GetInstanceID())) c.texSrc[t.GetInstanceID()] = idx;
        }
    }

    // loads a source object in a later session
    public static UnityEngine.Object LoadSource(C2ISource s)
    {
        if (s.kind == 1) return CommonLib.LoadAsset<Material>(s.ab, s.asset, false, s.manifest);
        return CommonLib.LoadAsset<GameObject>(s.ab, s.asset, false, s.manifest);
    }

    public static IEnumerable<Material> SourceMaterials(UnityEngine.Object o)
    {
        Material m = o as Material;
        if (m != null) { yield return m; yield break; }
        GameObject g = o as GameObject;
        if (g == null) yield break;
        foreach (Renderer r in g.GetComponentsInChildren<Renderer>(true))
            foreach (Material x in r.sharedMaterials) if (x != null) yield return x;
    }

    // wanted asset name -> (bundle, manifest) pairs of the list entries that name it
    static Dictionary<string, List<KeyValuePair<string, string>>> ListTextures(Ctx c, HashSet<string> wanted)
    {
        System.Diagnostics.Stopwatch swl = System.Diagnostics.Stopwatch.StartNew();
        Dictionary<string, List<KeyValuePair<string, string>>> res = new Dictionary<string, List<KeyValuePair<string, string>>>();
        try
        {
            object ctrl = Singleton<Manager.Character>.Instance.chaListCtrl;
            Dictionary<int, Dictionary<int, ListInfoBase>> all = Traverse.Create(ctrl).Field("dictListInfo").GetValue<Dictionary<int, Dictionary<int, ListInfoBase>>>();
            List<string> hits = new List<string>();
            foreach (Dictionary<int, ListInfoBase> cat in all.Values)
                foreach (ListInfoBase li in cat.Values)
                {
                    hits.Clear();
                    foreach (KeyValuePair<int, string> kv in li.dictInfo)
                        if (kv.Value != null && wanted.Contains(kv.Value)) hits.Add(kv.Value);
                    if (hits.Count == 0) continue;
                    List<string> abs = new List<string>(), manis = new List<string>();
                    foreach (KeyValuePair<int, string> kv in li.dictInfo)
                    {
                        if (kv.Value == null) continue;
                        if (kv.Value.EndsWith(".unity3d")) { if (!abs.Contains(kv.Value)) abs.Add(kv.Value); }
                        else if ((kv.Value == "abdata" || kv.Value.StartsWith("add")) && !manis.Contains(kv.Value)) manis.Add(kv.Value);
                    }
                    if (manis.Count == 0) manis.Add("abdata");
                    foreach (string v in hits)
                    {
                        List<KeyValuePair<string, string>> l;
                        if (!res.TryGetValue(v, out l)) res[v] = l = new List<KeyValuePair<string, string>>();
                        foreach (string ab in abs)
                            foreach (string mf in manis)
                            {
                                KeyValuePair<string, string> e = new KeyValuePair<string, string>(ab, mf);
                                if (!l.Contains(e)) l.Add(e);
                            }
                    }
                }
        }
        catch (Exception e) { c.log.AppendLine("list index: " + e.Message); }
        c.msList = swl.ElapsedMilliseconds;
        return res;
    }

    // ---------------------------------------------------------------- meshes

    // A mesh that is still the asset of a part prefab is referenced (its blend shape weights go on the renderer);
    // anything else (meshes replaced or edited at runtime) is stored, with the expression baked into the vertices.
    static int CaptureMesh(Ctx c, Renderer r, C2IRend cr)
    {
        Mesh src = MeshOf(r);
        SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;
        int idx;
        int si; string path;
        if (FindMeshSource(c, r, src, out si, out path))
        {
            if (smr != null && src.blendShapeCount > 0)
            {
                cr.blend = new float[src.blendShapeCount];
                for (int s = 0; s < cr.blend.Length; s++) cr.blend[s] = smr.GetBlendShapeWeight(s);
            }
            if (c.meshIndex.TryGetValue(src.GetInstanceID(), out idx)) return idx;
            idx = c.bake.meshes.Count;
            c.bake.meshes.Add(new C2IMesh { name = src.name, srcIndex = si, srcPath = path });
            c.meshIndex[src.GetInstanceID()] = idx;
            return idx;
        }
        if (!src.isReadable) { c.log.AppendLine("skipped " + r.name + ": its mesh is neither readable nor found in a part prefab"); return -1; }
        bool shaped = false;
        if (smr != null)
            for (int s = 0; s < src.blendShapeCount; s++) if (Mathf.Abs(smr.GetBlendShapeWeight(s)) > 1e-4f) { shaped = true; break; }
        if (!shaped && c.meshDataIndex.TryGetValue(src.GetInstanceID(), out idx)) return idx;

        C2IMesh m = new C2IMesh();
        m.name = src.name;
        m.v = src.vertices;
        m.n = src.normals;
        m.t = src.tangents;
        for (int ch = 0; ch < 4; ch++)
        {
            List<Vector4> l = new List<Vector4>();
            src.GetUVs(ch, l);
            if (l.Count == m.v.Length) m.uv[ch] = l;
        }
        m.col = src.colors32;
        m.bw = src.boneWeights;
        m.bp = src.bindposes;
        int sc = src.subMeshCount;
        m.topo = new int[sc];
        m.idx = new int[sc][];
        for (int s = 0; s < sc; s++) { m.topo[s] = (int)src.GetTopology(s); m.idx[s] = src.GetIndices(s); }
        m.bounds = src.bounds;
        if (shaped) BakeBlendShapes(src, smr, m);

        idx = c.bake.meshes.Count;
        c.bake.meshes.Add(m);
        if (!shaped) c.meshDataIndex[src.GetInstanceID()] = idx;
        return idx;
    }

    static Mesh RendererMesh(Transform t)
    {
        if (t == null) return null;
        SkinnedMeshRenderer smr = t.GetComponent<SkinnedMeshRenderer>();
        if (smr != null) return smr.sharedMesh;
        MeshFilter mf = t.GetComponent<MeshFilter>();
        return mf != null ? mf.sharedMesh : null;
    }

    // Some plugin copies the part meshes when a character loads; a copy whose data still equals the prefab's
    // mesh is as good as the asset itself.
    static bool SameMesh(Mesh live, Mesh asset)
    {
        if (live == null || asset == null) return false;
        if (live == asset) return true;
        if (live.name != asset.name || live.vertexCount != asset.vertexCount || live.subMeshCount != asset.subMeshCount
            || live.blendShapeCount != asset.blendShapeCount || !live.isReadable || !asset.isReadable) return false;
        Vector3[] a = live.vertices, b = asset.vertices;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        Matrix4x4[] pa = live.bindposes, pb = asset.bindposes;
        if (pa.Length != pb.Length) return false;
        for (int i = 0; i < pa.Length; i++) if (pa[i] != pb[i]) return false;
        BoneWeight[] wa = live.boneWeights, wb = asset.boneWeights;
        if (wa.Length != wb.Length) return false;
        for (int i = 0; i < wa.Length; i++) if (!wa[i].Equals(wb[i])) return false;
        for (int s = 0; s < live.subMeshCount; s++) if (live.GetIndexCount(s) != asset.GetIndexCount(s)) return false;
        return true;
    }

    static bool FindMeshSource(Ctx c, Renderer r, Mesh mesh, out int si, out string path)
    {
        si = -1; path = "";
        for (Transform p = r.transform; p != null; p = p.parent)
        {
            int k;
            if (!c.sourceRoots.TryGetValue(p, out k)) continue;
            GameObject prefab = (GameObject)c.sourceObjs[k];
            string rel = RelPath(r.transform, p);
            if (SameMesh(mesh, RendererMesh(string.IsNullOrEmpty(rel) ? prefab.transform : prefab.transform.Find(rel)))) { si = k; path = rel; return true; }
            break;
        }
        for (int k = 0; k < c.sourceObjs.Count; k++)
        {
            GameObject prefab = c.sourceObjs[k] as GameObject;
            if (prefab == null) continue;
            foreach (Renderer x in prefab.GetComponentsInChildren<Renderer>(true))
                if (SameMesh(mesh, RendererMesh(x.transform))) { si = k; path = RelPath(x.transform, prefab.transform); return true; }
        }
        return false;
    }

    // the expression lives in blend shapes: add the weighted deltas to the vertices and drop the shapes
    static void BakeBlendShapes(Mesh src, SkinnedMeshRenderer smr, C2IMesh m)
    {
        int vc = m.v.Length;
        bool hasN = m.n != null && m.n.Length == vc, hasT = m.t != null && m.t.Length == vc;
        Vector3[] dv = new Vector3[vc], dn = new Vector3[vc], dt = new Vector3[vc];
        Vector3[] dv2 = new Vector3[vc], dn2 = new Vector3[vc], dt2 = new Vector3[vc];
        for (int s = 0; s < src.blendShapeCount; s++)
        {
            float w = smr.GetBlendShapeWeight(s);
            if (Mathf.Abs(w) <= 1e-4f) continue;
            int fc = src.GetBlendShapeFrameCount(s);
            if (fc == 0) continue;
            int f0 = 0, f1 = -1;
            float a = 0f;
            float w0 = src.GetBlendShapeFrameWeight(s, 0);
            if (fc == 1 || w <= w0) { a = w0 != 0f ? w / w0 : 0f; }
            else
            {
                float wl = src.GetBlendShapeFrameWeight(s, fc - 1);
                if (w >= wl) { f0 = fc - 1; a = wl != 0f ? w / wl : 0f; }
                else
                {
                    for (int f = 0; f < fc - 1; f++)
                    {
                        float wa = src.GetBlendShapeFrameWeight(s, f), wb = src.GetBlendShapeFrameWeight(s, f + 1);
                        if (w >= wa && w <= wb) { f0 = f; f1 = f + 1; a = (w - wa) / Mathf.Max(1e-6f, wb - wa); break; }
                    }
                }
            }
            src.GetBlendShapeFrameVertices(s, f0, dv, dn, dt);
            if (f1 < 0)
            {
                for (int k = 0; k < vc; k++)
                {
                    m.v[k] += dv[k] * a;
                    if (hasN) m.n[k] += dn[k] * a;
                    if (hasT) { Vector3 d = dt[k] * a; m.t[k] += new Vector4(d.x, d.y, d.z, 0f); }
                }
            }
            else
            {
                src.GetBlendShapeFrameVertices(s, f1, dv2, dn2, dt2);
                for (int k = 0; k < vc; k++)
                {
                    m.v[k] += Vector3.Lerp(dv[k], dv2[k], a);
                    if (hasN) m.n[k] += Vector3.Lerp(dn[k], dn2[k], a);
                    if (hasT) { Vector3 d = Vector3.Lerp(dt[k], dt2[k], a); m.t[k] += new Vector4(d.x, d.y, d.z, 0f); }
                }
            }
        }
        if (hasN) for (int k = 0; k < vc; k++) m.n[k] = m.n[k].normalized;
        if (hasT) for (int k = 0; k < vc; k++) { Vector3 d = new Vector3(m.t[k].x, m.t[k].y, m.t[k].z).normalized; m.t[k] = new Vector4(d.x, d.y, d.z, m.t[k].w); }
        Bounds b = new Bounds(m.v.Length > 0 ? m.v[0] : Vector3.zero, Vector3.zero);
        foreach (Vector3 p in m.v) b.Encapsulate(p);
        m.bounds = b;
    }

    // ---------------------------------------------------------------- materials

    static string BaseName(string n)
    {
        while (n.EndsWith(" (Instance)")) n = n.Substring(0, n.Length - " (Instance)".Length);
        return n;
    }

    static string RelPath(Transform t, Transform root)
    {
        List<string> parts = new List<string>();
        while (t != null && t != root) { parts.Add(t.name); t = t.parent; }
        parts.Reverse();
        return string.Join("/", parts.ToArray());
    }

    public static Material FindSourceMaterial(GameObject prefab, string relPath, int slot, string name)
    {
        if (prefab == null) return null;
        Transform t = string.IsNullOrEmpty(relPath) ? prefab.transform : prefab.transform.Find(relPath);
        if (t != null)
        {
            Renderer r = t.GetComponent<Renderer>();
            if (r != null)
            {
                Material[] ms = r.sharedMaterials;
                if (slot >= 0 && slot < ms.Length && ms[slot] != null && BaseName(ms[slot].name) == name) return ms[slot];
            }
        }
        foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
            foreach (Material m in r.sharedMaterials)
                if (m != null && BaseName(m.name) == name) return m;
        return null;
    }

    static int CaptureMaterial(Ctx c, Material m, Renderer r, int slot)
    {
        int idx;
        if (c.matIndex.TryGetValue(m.GetInstanceID(), out idx)) return idx;
        C2IMat cm = new C2IMat();
        cm.name = m.name;
        cm.shader = m.shader != null ? m.shader.name : "";
        cm.queue = m.renderQueue;
        cm.keywords = m.shaderKeywords ?? new string[0];
        cm.renderType = m.GetTag("RenderType", false, "");
        List<string> off = new List<string>();
        foreach (string p in Passes) if (!m.GetShaderPassEnabled(p)) off.Add(p);
        cm.passesOff = off.ToArray();

        // the base material: the face/body skin material asset, else the material of the part prefab
        Material srcMat = null;
        if (c.cha != null && m == c.cha.customMatBody && c.bodyMatSrc >= 0) { cm.source = c.bodyMatSrc; srcMat = (Material)c.sourceObjs[c.bodyMatSrc]; }
        else if (c.cha != null && m == c.cha.customMatFace && c.faceMatSrc >= 0) { cm.source = c.faceMatSrc; srcMat = (Material)c.sourceObjs[c.faceMatSrc]; }
        else
            for (Transform p = r.transform; p != null; p = p.parent)
            {
                int si;
                if (!c.sourceRoots.TryGetValue(p, out si)) continue;
                string rel = RelPath(r.transform, p);
                srcMat = FindSourceMaterial((GameObject)c.sourceObjs[si], rel, slot, BaseName(m.name));
                if (srcMat != null) { cm.source = si; cm.srcPath = rel; cm.srcSlot = slot; cm.srcName = BaseName(srcMat.name); }
                break;
            }
        int ss;
        if (m.shader != null && c.shaderSrc.TryGetValue(m.shader.GetInstanceID(), out ss)) cm.shaderSource = ss;
        else if (m.shader != null && C2IBuild.MaterialEditorShader(cm.shader) == m.shader) cm.shaderSource = C2IMat.FromMaterialEditor;
        else c.log.AppendLine("shader of " + m.name + " (" + cm.shader + ") not found in any source; matched by name on load");

        foreach (string p in C2INames.Floats)
            if (m.HasProperty(p)) { float f = m.GetFloat(p); if (f != 0f || !C2INames.Untyped(p)) cm.floats[p] = f; }
        foreach (string p in C2INames.Vectors)
            if (m.HasProperty(p)) { Vector4 v = m.GetVector(p); if (v != Vector4.zero || !C2INames.Untyped(p)) cm.vectors[p] = v; }
        // an untyped property that reads 0 both ways is 0 in its real type: write both
        foreach (string p in C2INames.Floats)
            if (C2INames.Untyped(p) && m.HasProperty(p) && !cm.floats.ContainsKey(p) && !cm.vectors.ContainsKey(p)) { cm.floats[p] = 0f; cm.vectors[p] = Vector4.zero; }

        if (cm.shader.IndexOf("Skin", StringComparison.OrdinalIgnoreCase) >= 0) cm.inside = InsideColor(m);

        foreach (string p in m.GetTexturePropertyNames())
        {
            C2ITexSlot s = new C2ITexSlot { prop = p, scale = m.GetTextureScale(p), offset = m.GetTextureOffset(p) };
            Texture tx = m.GetTexture(p);
            if (srcMat != null && srcMat.HasProperty(p) && srcMat.GetTexture(p) == tx) s.mode = 0;
            else if (tx == null) s.mode = 1;
            else if (tx is Texture2D || tx is RenderTexture) { s.mode = 2; s.tex = QueueTexture(c, tx); }
            else s.mode = 0;   // cubemaps etc. cannot be stored: keep whatever the base material has
            cm.slots.Add(s);
        }

        idx = c.bake.mats.Count;
        c.bake.mats.Add(cm);
        c.matIndex[m.GetInstanceID()] = idx;
        return idx;
    }

    static int QueueTexture(Ctx c, Texture tx)
    {
        int idx;
        if (c.texIndex.TryGetValue(tx.GetInstanceID(), out idx)) return idx;
        idx = c.bake.textures.Count;
        C2ITex info = new C2ITex
        {
            name = tx.name, w = tx.width, h = tx.height, wrapU = (int)tx.wrapModeU, wrapV = (int)tx.wrapModeV,
            filter = (int)tx.filterMode, aniso = tx.anisoLevel, mipBias = tx.mipMapBias
        };
        c.bake.textures.Add(info);
        c.texIndex[tx.GetInstanceID()] = idx;
        int si;
        if (c.texSrc.TryGetValue(tx.GetInstanceID(), out si)) { info.srcIndex = si; return idx; }
        if (tx is Texture2D && !string.IsNullOrEmpty(tx.name)) c.unresolved.Add(new KeyValuePair<int, Texture>(idx, tx));
        else c.pendingTex.Add(new KeyValuePair<int, Texture>(idx, tx));
        return idx;
    }

    // textures the game loaded by name from list entries (skin, eyes, eyebrows, hair colour...) are referenced
    static void ResolveListTextures(Ctx c)
    {
        if (c.unresolved.Count == 0) return;
        Dictionary<string, List<KeyValuePair<string, string>>> index = ListTextures(c, new HashSet<string>(c.unresolved.Select(kv => kv.Value.name)));
        foreach (KeyValuePair<int, Texture> kv in c.unresolved)
        {
            C2ITex info = c.bake.textures[kv.Key];
            List<KeyValuePair<string, string>> cands;
            bool found = false;
            if (index.TryGetValue(kv.Value.name, out cands))
                foreach (KeyValuePair<string, string> ab in cands)
                {
                    Texture2D t = null;
                    try { t = CommonLib.LoadAsset<Texture2D>(ab.Key, kv.Value.name, false, ab.Value); } catch (Exception) { }
                    if (t == kv.Value) { info.refAB = ab.Key; info.refManifest = ab.Value; found = true; break; }
                }
            if (!found) c.pendingTex.Add(kv);
        }
        c.unresolved.Clear();
    }

    // ---------------------------------------------------------------- textures (step 2, one per frame)

    public static void StoreTexture(Ctx c, int i)
    {
        int ti = c.pendingTex[i].Key;
        Texture tx = c.pendingTex[i].Value;
        C2ITex info = c.bake.textures[ti];
        bool linear;
        List<Color32[]> levels = ReadRaw(tx, out linear);
        Texture2D t2 = tx as Texture2D;
        RenderTexture rt = tx as RenderTexture;
        bool mips = t2 != null ? t2.mipmapCount > 1 : (rt != null && rt.useMipMap);
        bool alpha = false;
        foreach (Color32[] px in levels)
        {
            for (int k = 0; k < px.Length; k++) if (px[k].a != 255) { alpha = true; break; }
            if (alpha) break;
        }
        Texture2D tmp = new Texture2D(info.w, info.h, alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, mips, linear);
        if (mips && levels.Count == tmp.mipmapCount)
        {
            // the source's own mip chain: Unity's import can keep alpha coverage (cut-out hair) that a box filter loses
            for (int m = 0; m < levels.Count; m++) tmp.SetPixels32(levels[m], m);
            tmp.Apply(false);
        }
        else
        {
            tmp.SetPixels32(levels[0]);
            tmp.Apply(mips);
        }
        // kept lossless: DXT visibly shifted the composited skin colours, and deflate packs these textures smaller anyway
        info.format = (int)tmp.format;
        info.mips = mips;
        info.linear = linear;
        info.hash = C2IStore.SaveTexture(info.w, info.h, info.format, mips, linear, tmp.GetRawTextureData());
        UnityEngine.Object.Destroy(tmp);
        if (!c.bake.texHashes.Contains(info.hash)) c.bake.texHashes.Add(info.hash);
    }

    static Color32[] BlitRead(Texture src, RenderTexture rt)
    {
        Graphics.Blit(src, rt);
        return ReadActive(rt, true);
    }

    static Color32[] ReadActive(RenderTexture rt, bool linear)
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D t = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, linear);
        t.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
        t.Apply(false);
        RenderTexture.active = prev;
        Color32[] px = t.GetPixels32();
        UnityEngine.Object.Destroy(t);
        return px;
    }

    // The stored texels are the raw ones (so an sRGB texture stays 8-bit sRGB), plus whether to sample them as sRGB.
    // Unity 2018 cannot tell a Texture2D's colour space, so it is measured: the raw texels (copied into a texture
    // flagged linear) are compared with what sampling the original gives. Every mip level of a Texture2D is copied.
    public static List<Color32[]> ReadRaw(Texture tx, out bool linear)
    {
        List<Color32[]> levels = new List<Color32[]>();
        int w = tx.width, h = tx.height;
        RenderTexture rtSrc = tx as RenderTexture;
        if (rtSrc != null)
        {
            linear = !rtSrc.sRGB;
            levels.Add(ReadActive(rtSrc, linear));
            return levels;
        }
        Texture2D src = (Texture2D)tx;
        RenderTexture tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Color32[] sampled = BlitRead(src, tmp);
        RenderTexture.ReleaseTemporary(tmp);
        for (int m = 0; m < src.mipmapCount; m++)
        {
            Color32[] raw = ReadMip(src, m);
            if (raw == null) break;
            levels.Add(raw);
        }
        if (levels.Count == 0) { linear = true; levels.Add(sampled); return levels; }
        Color32[] r0 = levels[0];
        int mid = 0, diff = 0, nonzero = 0;
        int step = Mathf.Max(1, r0.Length / 20000);
        for (int k = 0; k < r0.Length; k += step)
        {
            Color32 a = r0[k], b = sampled[k];
            if (a.r != 0 || a.g != 0 || a.b != 0 || a.a != 0) nonzero++;
            CountMid(a.r, b.r, ref mid, ref diff); CountMid(a.g, b.g, ref mid, ref diff); CountMid(a.b, b.b, ref mid, ref diff);
        }
        if (nonzero == 0 && sampled.Any(p => p.r != 0 || p.g != 0 || p.b != 0 || p.a != 0))
        {
            // the raw copy did not work for this format: keep what sampling gives, as linear data
            levels.Clear(); levels.Add(sampled); linear = true; return levels;
        }
        linear = !(mid > 0 && diff * 2 > mid);
        return levels;
    }

    static Color32[] ReadMip(Texture2D src, int mip)
    {
        int w = Mathf.Max(1, src.width >> mip), h = Mathf.Max(1, src.height >> mip);
        Texture2D lin = null;
        RenderTexture rt = null;
        try
        {
            lin = new Texture2D(w, h, src.format, false, true);
            Graphics.CopyTexture(src, 0, mip, lin, 0, 0);
            rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            return BlitRead(lin, rt);
        }
        catch (Exception) { return null; }
        finally
        {
            if (lin != null) UnityEngine.Object.Destroy(lin);
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
        }
    }

    static void CountMid(byte raw, byte smp, ref int mid, ref int diff)
    {
        if (raw < 40 || raw > 215) return;
        mid++;
        if (Mathf.Abs(raw - smp) > 8) diff++;
    }
}

// ==================================================================================================== build

public static class C2IBuild
{
    static Dictionary<string, Shader> shaderCache = new Dictionary<string, Shader>();

    // MaterialEditor keeps one copy of each shader it knows and, with "Shader Optimization" on, puts it on every
    // loaded material; characters then render with that copy rather than the one in their bundle
    static Dictionary<string, Shader> meShaders;
    public static Shader MaterialEditorShader(string name)
    {
        try
        {
            if (meShaders == null)
            {
                meShaders = new Dictionary<string, Shader>();
                Type t = AccessTools.TypeByName("MaterialEditorAPI.MaterialEditorPluginBase");
                FieldInfo f = t != null ? AccessTools.Field(t, "LoadedShaders") : null;
                System.Collections.IDictionary d = f != null ? f.GetValue(null) as System.Collections.IDictionary : null;
                if (d != null)
                    foreach (System.Collections.DictionaryEntry e in d)
                    {
                        Shader sh = e.Value != null ? Traverse.Create(e.Value).Field("Shader").GetValue<Shader>() : null;
                        if (sh != null) meShaders[(string)e.Key] = sh;
                    }
            }
            Shader r;
            return meShaders.TryGetValue(name, out r) && r != null ? r : null;
        }
        catch (Exception) { return null; }
    }

    static readonly string[] InsideSkipShaders = { "Eye", "namida", "eyelash", "eyeshadow", "Liquid", "Glass" };
    static readonly string[] InsideSkipRenderers = { "o_tooth", "o_tang", "o_eyebase_L", "o_eyebase_R", "o_eyelashes", "o_eyeshadow", "o_namida", "o_mayuge", "o_eyebrow" };
    static Shader solidShader;

    // garments and hair are sheets with no inside of their own
    static bool IsShell(C2IRend r, C2IMat cm)
    {
        string p = r.part ?? "";
        if (p.StartsWith("clothes") || p.StartsWith("hair")) return true;
        string sh = cm.shader ?? "";
        return sh.IndexOf("Clothes", StringComparison.OrdinalIgnoreCase) >= 0 || sh.IndexOf("hair", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static bool sectionLogged;

    // a flat (unlit) colour for cut sections
    public static Material SectionMaterial(Vector4 c)
    {
        Color col = new Color(c.x, c.y, c.z, 1f);
        Shader sh = MaterialEditorShader("Unlit/Color") ?? FindShader("Unlit/Color");
        if (sh != null && sh.isSupported) { Material m = new Material(sh); m.color = col; m.name = "Cut section"; return m; }
        // no unlit shader in this build: Standard with a black albedo, no highlights or reflections, the colour as emission
        sh = SolidShader();
        if (sh == null) return null;
        if (!sectionLogged) { sectionLogged = true; CharaToItem.Log.LogInfo("cut sections drawn with " + sh.name + " (emission)"); }
        Material e = new Material(sh);
        e.name = "Cut section";
        e.color = Color.black;
        if (e.HasProperty("_Glossiness")) e.SetFloat("_Glossiness", 0f);
        if (e.HasProperty("_Metallic")) e.SetFloat("_Metallic", 0f);
        if (e.HasProperty("_SpecularHighlights")) e.SetFloat("_SpecularHighlights", 0f);
        if (e.HasProperty("_GlossyReflections")) e.SetFloat("_GlossyReflections", 0f);
        e.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        e.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
        e.EnableKeyword("_EMISSION");
        e.SetColor("_EmissionColor", col);
        e.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        return e;
    }

    static Shader SolidShader()
    {
        if (solidShader != null) return solidShader;
        foreach (string n in new[] { "Standard", "Legacy Shaders/Diffuse", "Diffuse", "Unlit/Color" })
        {
            Shader sh = MaterialEditorShader(n) ?? FindShader(n);
            if (sh != null && sh.isSupported) { solidShader = sh; break; }
        }
        return solidShader;
    }

    // Parts cut out of a character are hollow: through an opening (a sleeve without its arm, a head without its
    // neck) the far side would be seen from behind, which back-face culling leaves invisible. Each such mesh gets
    // a copy with its triangles turned round, drawn on the inside: garments, hair and accessories with their own
    // material (fabric seen from inside; cut-outs and masks still apply), skin with a solid colour.
    // The copy sits 1 mm inside the surface, so the surface wins the depth test from outside and the copy from
    // inside, also with shaders that already draw both sides (AIT/Clothes does; a coincident copy z-fought).
    static void AddInsides(C2IBake b, Transform[] tr, Mesh[] meshes, Material[] mats, List<UnityEngine.Object> owned, StringBuilder log)
    {
        Dictionary<int, Material> solids = new Dictionary<int, Material>();
        Dictionary<string, Mesh> cache = new Dictionary<string, Mesh>();
        int made = 0;
        Material section = null;
        foreach (C2IRend r in b.rends)
        {
            bool cut = r.inside == 2;
            if (!cut && !b.fillInside) continue;
            GameObject g = tr[r.node].gameObject;
            Renderer outer = g.GetComponent<Renderer>();
            Mesh src = r.mesh >= 0 && r.mesh < meshes.Length ? meshes[r.mesh] : null;
            if (outer == null || src == null || !src.isReadable || InsideSkipRenderers.Contains(g.name)) continue;
            int sub = src.subMeshCount;
            bool[] keep = new bool[sub];
            Material[] im = new Material[sub];
            Material any = null;
            for (int k = 0; k < sub; k++)
            {
                int mi = k < r.mats.Length ? r.mats[k] : -1;
                if (mi < 0 || src.GetTopology(k) != MeshTopology.Triangles) continue;
                C2IMat cm = b.mats[mi];
                if (cm.queue > 2500 || cm.renderType == "Transparent" || InsideSkipShaders.Any(x => cm.shader.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (cut)
                {
                    if (IsShell(r, cm) && b.shellsOwnInside) im[k] = mats[mi];
                    else
                    {
                        if (section == null) { section = SectionMaterial(b.sectionColor); if (section != null) owned.Add(section); }
                        im[k] = section;
                    }
                }
                else if (cm.shader.IndexOf("Skin", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Material sm;
                    if (!solids.TryGetValue(mi, out sm))
                    {
                        Shader sh = SolidShader();
                        if (sh == null) continue;
                        sm = new Material(sh);
                        sm.name = cm.name + " (inside)";
                        sm.color = new Color(cm.inside.x, cm.inside.y, cm.inside.z, 1f);
                        if (sm.HasProperty("_Glossiness")) sm.SetFloat("_Glossiness", 0.15f);
                        if (sm.HasProperty("_Metallic")) sm.SetFloat("_Metallic", 0f);
                        owned.Add(sm);
                        solids[mi] = sm;
                    }
                    im[k] = sm;
                }
                else im[k] = mats[mi];
                if (im[k] == null) continue;
                keep[k] = true;
                any = im[k];
            }
            if (any == null) continue;
            for (int k = 0; k < sub; k++) if (im[k] == null) im[k] = any;   // (empty sub-mesh: draws nothing)
            float inset = InsetWorld / Mathf.Max(1e-4f, MeshToWorldScale(outer, src));
            string ck = src.GetInstanceID() + ":" + string.Join("", keep.Select(x => x ? "1" : "0").ToArray()) + ":" + inset.ToString("G4");
            Mesh inside;
            if (!cache.TryGetValue(ck, out inside))
            {
                inside = InsideMesh(src, keep, inset);
                owned.Add(inside);
                cache[ck] = inside;
            }
            GameObject ig = new GameObject(g.name + "~inside");
            ig.layer = g.layer;
            ig.transform.SetParent(g.transform, false);
            Renderer ir;
            SkinnedMeshRenderer osmr = outer as SkinnedMeshRenderer;
            if (osmr != null)
            {
                SkinnedMeshRenderer s2 = ig.AddComponent<SkinnedMeshRenderer>();
                s2.bones = osmr.bones;
                s2.rootBone = osmr.rootBone;
                s2.sharedMesh = inside;
                s2.quality = osmr.quality;
                s2.updateWhenOffscreen = osmr.updateWhenOffscreen;
                s2.localBounds = osmr.localBounds;
                for (int k = 0; k < inside.blendShapeCount && k < r.blend.Length; k++) s2.SetBlendShapeWeight(k, r.blend[k]);
                ir = s2;
            }
            else
            {
                ig.AddComponent<MeshFilter>().sharedMesh = inside;
                ir = ig.AddComponent<MeshRenderer>();
            }
            ir.sharedMaterials = im;
            ir.shadowCastingMode = ShadowCastingMode.Off;   // the outside already casts the shadow
            ir.receiveShadows = outer.receiveShadows;
            ir.lightProbeUsage = outer.lightProbeUsage;
            ir.reflectionProbeUsage = outer.reflectionProbeUsage;
            ir.probeAnchor = outer.probeAnchor;
            ir.renderingLayerMask = outer.renderingLayerMask;
            made++;
        }
        if (made == 0) log.AppendLine("no insides drawn (no suitable meshes)");
    }

    const float InsetWorld = 0.01f;   // 1 mm (1 unit = 10 cm)

    // how many world units one mesh unit is, in the prototype (item root at the origin, unscaled)
    static float MeshToWorldScale(Renderer outer, Mesh mesh)
    {
        SkinnedMeshRenderer smr = outer as SkinnedMeshRenderer;
        Matrix4x4 m = outer.transform.localToWorldMatrix;
        if (smr != null)
        {
            Transform[] bones = smr.bones;
            Matrix4x4[] bp = mesh.bindposes;
            for (int i = 0; i < bones.Length && i < bp.Length; i++)
                if (bones[i] != null) { m = bones[i].localToWorldMatrix * bp[i]; break; }
        }
        return m.MultiplyVector(new Vector3(1f, 1f, 1f).normalized).magnitude;
    }

    // triangles turned round, normals reversed, pushed `inset` mesh units inward; sub-meshes not kept are emptied
    static Mesh InsideMesh(Mesh src, bool[] keep, float inset)
    {
        Mesh m = UnityEngine.Object.Instantiate(src);
        m.name = src.name + " (inside)";
        Vector3[] n = m.normals;
        if (n != null && n.Length > 0)
        {
            Vector3[] v = m.vertices;
            for (int i = 0; i < n.Length && i < v.Length; i++) v[i] -= n[i] * inset;
            m.vertices = v;
            for (int i = 0; i < n.Length; i++) n[i] = -n[i];
            m.normals = n;
        }
        Vector4[] t = m.tangents;
        if (t != null && t.Length > 0) { for (int i = 0; i < t.Length; i++) t[i].w = -t[i].w; m.tangents = t; }
        for (int s = 0; s < m.subMeshCount; s++)
        {
            if (!keep[s]) { m.SetIndices(new int[0], MeshTopology.Triangles, s, false); continue; }
            int[] idx = m.GetIndices(s);
            for (int i = 0; i + 2 < idx.Length; i += 3) { int x = idx[i + 1]; idx[i + 1] = idx[i + 2]; idx[i + 2] = x; }
            m.SetIndices(idx, MeshTopology.Triangles, s, false);
        }
        if (src.blendShapeCount > 0)
        {
            // the shapes' normal offsets turn round with the normals
            int vc = src.vertexCount;
            Vector3[] dv = new Vector3[vc], dn = new Vector3[vc], dt = new Vector3[vc];
            m.ClearBlendShapes();
            for (int sh = 0; sh < src.blendShapeCount; sh++)
            {
                string name = src.GetBlendShapeName(sh);
                for (int f = 0; f < src.GetBlendShapeFrameCount(sh); f++)
                {
                    src.GetBlendShapeFrameVertices(sh, f, dv, dn, dt);
                    for (int i = 0; i < vc; i++) dn[i] = -dn[i];
                    m.AddBlendShapeFrame(name, src.GetBlendShapeFrameWeight(sh, f), dv, dn, dt);
                }
            }
        }
        return m;
    }

    static Mesh RendererMeshOf(Transform t)
    {
        if (t == null) return null;
        SkinnedMeshRenderer smr = t.GetComponent<SkinnedMeshRenderer>();
        if (smr != null) return smr.sharedMesh;
        MeshFilter mf = t.GetComponent<MeshFilter>();
        return mf != null ? mf.sharedMesh : null;
    }

    public static Shader FindShader(string name)
    {
        Shader s;
        if (shaderCache.TryGetValue(name, out s) && s != null) return s;
        s = Shader.Find(name);
        if (s == null) s = Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(x => x.name == name);
        if (s != null) shaderCache[name] = s;
        return s;
    }

    public static GameObject Build(C2IBake b, Transform container, Dictionary<string, Texture2D> texCache, StringBuilder log, List<UnityEngine.Object> owned, HashSet<string> usedTex)
    {
        // nodes
        Transform[] tr = new Transform[b.nodes.Count];
        for (int i = 0; i < b.nodes.Count; i++)
        {
            C2INode n = b.nodes[i];
            GameObject g = new GameObject(n.name);
            g.layer = n.layer;   // the Studio character light only lights the Chara layer
            Transform t = g.transform;
            t.SetParent(n.parent < 0 ? container : tr[n.parent], false);
            t.localPosition = n.lp; t.localRotation = n.lr; t.localScale = n.ls;
            tr[i] = t;
        }
        GameObject root = tr[0].gameObject;
        root.name = b.name;

        // sources: part prefabs and material assets (loading them also brings their shaders into memory)
        UnityEngine.Object[] srcs = new UnityEngine.Object[b.sources.Count];
        for (int i = 0; i < b.sources.Count; i++)
        {
            C2ISource s = b.sources[i];
            try { srcs[i] = C2ICapture.LoadSource(s); }
            catch (Exception e) { log.AppendLine("source " + s.ab + ": " + e.Message); }
            if (srcs[i] == null) log.AppendLine("source missing: " + s.ab + " / " + s.asset);
        }

        // textures: referenced assets, or our stored texels
        Texture[] texs = new Texture[b.textures.Count];
        for (int i = 0; i < b.textures.Count; i++)
        {
            C2ITex t = b.textures[i];
            if (t.srcIndex >= 0)
            {
                if (t.srcIndex < srcs.Length && srcs[t.srcIndex] != null)
                    foreach (Material m in C2ICapture.SourceMaterials(srcs[t.srcIndex]))
                    {
                        foreach (string p in m.GetTexturePropertyNames())
                        {
                            Texture x = m.GetTexture(p);
                            if (x != null && x.name == t.name) { texs[i] = x; break; }
                        }
                        if (texs[i] != null) break;
                    }
                if (texs[i] == null) log.AppendLine("texture " + t.name + " not found in its source");
                continue;
            }
            if (!string.IsNullOrEmpty(t.refAB))
            {
                try { texs[i] = CommonLib.LoadAsset<Texture2D>(t.refAB, t.name, false, t.refManifest); } catch (Exception) { }
                if (texs[i] == null) log.AppendLine("texture asset missing: " + t.refAB + " / " + t.name);
                continue;
            }
            if (string.IsNullOrEmpty(t.hash)) continue;
            Texture2D st;
            if (!texCache.TryGetValue(t.hash, out st) || st == null)
            {
                st = C2IStore.LoadTexture(t.hash);
                if (st == null) { log.AppendLine("texture missing: " + t.name + " " + t.hash); continue; }
                texCache[t.hash] = st;
            }
            usedTex.Add(t.hash);
            st.name = t.name;
            st.wrapModeU = (TextureWrapMode)t.wrapU; st.wrapModeV = (TextureWrapMode)t.wrapV;
            st.filterMode = (FilterMode)t.filter; st.anisoLevel = t.aniso; st.mipMapBias = t.mipBias;
            texs[i] = st;
        }

        // materials
        Material[] mats = new Material[b.mats.Count];
        for (int i = 0; i < b.mats.Count; i++)
        {
            C2IMat cm = b.mats[i];
            Material baseMat = null;
            if (cm.source >= 0 && cm.source < srcs.Length && srcs[cm.source] != null)
                baseMat = srcs[cm.source] is Material ? (Material)srcs[cm.source] : C2ICapture.FindSourceMaterial((GameObject)srcs[cm.source], cm.srcPath, cm.srcSlot, cm.srcName);
            Shader sh = null;
            if (cm.shaderSource >= 0 && cm.shaderSource < srcs.Length && srcs[cm.shaderSource] != null)
                foreach (Material x in C2ICapture.SourceMaterials(srcs[cm.shaderSource]))
                    if (x.shader != null && x.shader.name == cm.shader) { sh = x.shader; break; }
            if (sh == null && cm.shaderSource == C2IMat.FromMaterialEditor) sh = MaterialEditorShader(cm.shader);
            if (sh == null && baseMat != null && baseMat.shader != null && baseMat.shader.name == cm.shader) sh = baseMat.shader;
            if (sh == null) sh = MaterialEditorShader(cm.shader);
            if (sh == null) sh = FindShader(cm.shader);
            if (sh == null && baseMat == null)
            {
                log.AppendLine("shader missing: " + cm.shader + " (" + cm.name + "), using Standard");
                sh = Shader.Find("Standard");
            }
            Material m = baseMat != null ? new Material(baseMat) : new Material(sh);
            owned.Add(m);
            if (sh != null && m.shader != sh) m.shader = sh;
            m.name = cm.name;
            foreach (KeyValuePair<string, float> kv in cm.floats) m.SetFloat(kv.Key, kv.Value);
            foreach (KeyValuePair<string, Vector4> kv in cm.vectors) m.SetVector(kv.Key, kv.Value);
            foreach (C2ITexSlot s in cm.slots)
            {
                if (s.mode == 1) m.SetTexture(s.prop, null);
                else if (s.mode == 2 && s.tex >= 0 && s.tex < texs.Length && texs[s.tex] != null) m.SetTexture(s.prop, texs[s.tex]);
                m.SetTextureScale(s.prop, s.scale);
                m.SetTextureOffset(s.prop, s.offset);
            }
            m.shaderKeywords = cm.keywords;
            if (!string.IsNullOrEmpty(cm.renderType)) m.SetOverrideTag("RenderType", cm.renderType);
            foreach (string p in cm.passesOff) m.SetShaderPassEnabled(p, false);
            m.renderQueue = cm.queue;
            mats[i] = m;
        }

        // meshes
        Mesh[] meshes = new Mesh[b.meshes.Count];
        for (int i = 0; i < b.meshes.Count; i++)
        {
            C2IMesh cm = b.meshes[i];
            if (cm.srcIndex >= 0)
            {
                GameObject prefab = cm.srcIndex < srcs.Length ? srcs[cm.srcIndex] as GameObject : null;
                Mesh found = null;
                if (prefab != null)
                {
                    found = RendererMeshOf(string.IsNullOrEmpty(cm.srcPath) ? prefab.transform : prefab.transform.Find(cm.srcPath));
                    if (found == null || found.name != cm.name)
                        foreach (Renderer x in prefab.GetComponentsInChildren<Renderer>(true))
                        {
                            Mesh mx = RendererMeshOf(x.transform);
                            if (mx != null && mx.name == cm.name) { found = mx; break; }
                        }
                }
                if (found == null) log.AppendLine("mesh " + cm.name + " not found in its source");
                meshes[i] = found;
                continue;
            }
            Mesh m = new Mesh();
            owned.Add(m);
            m.name = cm.name;
            if (cm.v.Length > 65000) m.indexFormat = IndexFormat.UInt32;
            m.vertices = cm.v;
            if (cm.n != null) m.normals = cm.n;
            if (cm.t != null) m.tangents = cm.t;
            for (int ch = 0; ch < 4; ch++) if (cm.uv[ch] != null) m.SetUVs(ch, cm.uv[ch]);
            if (cm.col != null) m.colors32 = cm.col;
            if (cm.bw != null) m.boneWeights = cm.bw;
            if (cm.bp != null && cm.bp.Length > 0) m.bindposes = cm.bp;
            m.subMeshCount = cm.idx.Length;
            for (int s = 0; s < cm.idx.Length; s++) m.SetIndices(cm.idx[s], (MeshTopology)cm.topo[s], s, false);
            m.bounds = cm.bounds;
            meshes[i] = m;
        }

        // renderers
        foreach (C2IRend r in b.rends)
        {
            GameObject g = tr[r.node].gameObject;
            Renderer rend;
            if (r.skinned)
            {
                SkinnedMeshRenderer smr = g.AddComponent<SkinnedMeshRenderer>();
                Transform[] bones = new Transform[r.bones.Length];
                for (int k = 0; k < bones.Length; k++) bones[k] = r.bones[k] >= 0 ? tr[r.bones[k]] : tr[0];
                smr.bones = bones;
                smr.rootBone = r.rootBone >= 0 ? tr[r.rootBone] : null;
                smr.sharedMesh = meshes[r.mesh];
                if (smr.sharedMesh != null)
                    for (int k = 0; k < r.blend.Length && k < smr.sharedMesh.blendShapeCount; k++) smr.SetBlendShapeWeight(k, r.blend[k]);
                smr.quality = (SkinQuality)r.quality;
                smr.updateWhenOffscreen = r.offscreen;
                smr.localBounds = r.localBounds;   // as the character had them (larger bounds only make it drawn off-screen)
                rend = smr;
            }
            else
            {
                g.AddComponent<MeshFilter>().sharedMesh = meshes[r.mesh];
                rend = g.AddComponent<MeshRenderer>();
            }
            Material[] rm = new Material[r.mats.Length];
            for (int k = 0; k < rm.Length; k++) rm[k] = r.mats[k] >= 0 ? mats[r.mats[k]] : null;
            rend.sharedMaterials = rm;
            rend.shadowCastingMode = (ShadowCastingMode)r.shadow;
            rend.receiveShadows = r.receive;
            rend.sortingOrder = r.sortingOrder;
            rend.lightProbeUsage = (LightProbeUsage)r.lightProbe;
            rend.reflectionProbeUsage = (ReflectionProbeUsage)r.reflProbe;
            if (r.anchor >= 0) rend.probeAnchor = tr[r.anchor];
            rend.renderingLayerMask = r.renderingLayerMask;
        }

        if (b.fillInside || b.rends.Any(x => x.inside == 2)) AddInsides(b, tr, meshes, mats, owned, log);

        // twist-bone corrections (Expression finds its bones by name under the root; the instance helper re-links clones)
        if (b.expr.Count > 0)
        {
            CharaUtils.Expression ex = root.AddComponent<CharaUtils.Expression>();
            ex.info = new CharaUtils.Expression.ScriptInfo[b.expr.Count];
            for (int i = 0; i < b.expr.Count; i++)
            {
                C2IExpr e = b.expr[i];
                CharaUtils.Expression.ScriptInfo si = new CharaUtils.Expression.ScriptInfo();
                si.elementName = e.elementName; si.enable = e.enable; si.enableLookAt = e.enableLookAt; si.enableCorrect = e.enableCorrect;
                si.index = e.index; si.categoryNo = e.categoryNo;
                si.lookAt.lookAtName = e.lookAtName; si.lookAt.targetName = e.targetName; si.lookAt.upAxisName = e.upAxisName;
                si.lookAt.targetAxisType = (CharaUtils.Expression.LookAt.AxisType)e.targetAxisType;
                si.lookAt.upAxisType = (CharaUtils.Expression.LookAt.AxisType)e.upAxisType;
                si.lookAt.sourceAxisType = (CharaUtils.Expression.LookAt.AxisType)e.sourceAxisType;
                si.lookAt.limitAxisType = (CharaUtils.Expression.LookAt.AxisType)e.limitAxisType;
                si.lookAt.rotOrder = (CharaUtils.Expression.LookAt.RotationOrder)e.laRotOrder;
                si.lookAt.limitMin = e.limitMin; si.lookAt.limitMax = e.limitMax;
                si.correct.correctName = e.correctName; si.correct.referenceName = e.referenceName;
                si.correct.calcType = (CharaUtils.Expression.Correct.CalcType)e.calcType;
                si.correct.rotOrder = (CharaUtils.Expression.Correct.RotationOrder)e.crRotOrder;
                si.correct.charmRate = e.charmRate;
                si.correct.useRX = e.useRX; si.correct.valRXMin = e.rxMin; si.correct.valRXMax = e.rxMax;
                si.correct.useRY = e.useRY; si.correct.valRYMin = e.ryMin; si.correct.valRYMax = e.ryMax;
                si.correct.useRZ = e.useRZ; si.correct.valRZMin = e.rzMin; si.correct.valRZMax = e.rzMax;
                ex.info[i] = si;
            }
            ex.enable = b.exprEnable;
            ex.SetCharaTransform(root.transform);
            ex.Initialize();
        }
        CharaToItemInstance inst = root.AddComponent<CharaToItemInstance>();
        inst.no = b.no;
        inst.created = b.created;

        for (int i = 0; i < b.nodes.Count; i++) if (!b.nodes[i].active) tr[i].gameObject.SetActive(false);
        return root;
    }
}

// Lives on every baked item. Unity's Instantiate copies Expression's settings but not its resolved bone links.
public class CharaToItemInstance : MonoBehaviour
{
    public int no;
    public long created;   // the bake's creation time: tells an item built from an older bake under the same number

    void Awake()
    {
        CharaUtils.Expression ex = GetComponent<CharaUtils.Expression>();
        if (ex != null) { ex.SetCharaTransform(transform); ex.Initialize(); }
    }
}

// ==================================================================================================== plugin

public class CharaToItem : MonoBehaviour
{
    public static ManualLogSource Log;
    public static CharaToItem Instance;
    public static bool MouseOverWindow;

    // ---------------------------------------------------------------- state (public: readable through StudioBridge)
    public bool hideOriginal = true;
    public string partSpec = "";     // comma-separated part keys (C2IParts); empty: the whole character
    public bool splitParts;          // one item per selected part
    public string newName = "";      // name for the next conversion (blank: character, parts and time); cleared after use
    public bool fillInsides = true;  // draw the insides of parts baked without the body around them
    public bool centrePartOrigin = true;   // chosen parts: the item origin at their centre of mass, not the character's
    public int fkGroups = 0x7FF;   // OIBoneInfo.BoneGroup flags of the character FK bones that become item FK bones
    public string status = "";
    public bool busy;
    public int lastNo;
    public int lastItemKey = -1;
    public float uiScale = 0f;

    static Dictionary<int, C2IBake> headers = new Dictionary<int, C2IBake>();
    static Dictionary<int, GameObject> prototypes = new Dictionary<int, GameObject>();
    static Dictionary<int, List<UnityEngine.Object>> protoOwned = new Dictionary<int, List<UnityEngine.Object>>();
    static Dictionary<int, HashSet<string>> protoTex = new Dictionary<int, HashSet<string>>();
    static Dictionary<string, Texture2D> texCache = new Dictionary<string, Texture2D>();
    static List<UnityEngine.Object> retired = new List<UnityEngine.Object>();
    static GameObject container;

    bool showWindow;
    Rect windowRect = new Rect(-1f, 80f, 420f, 500f);
    Vector2 scroll;
    int confirmDelete = -1;
    int renaming = -1;
    string renameText = "";
    bool ownsFocus;

    void Awake()
    {
        Instance = this;
        EnsureAllRegistered();
    }

    void OnDestroy()
    {
        RemoveToolbarButton();
        MouseOverWindow = false;
        if (Instance == this) Instance = null;
        // prototypes and their meshes/materials stay alive: items in the scene still use them after a hot reload
    }

    // ---------------------------------------------------------------- registry

    static Transform Container()
    {
        if (container == null)
        {
            container = new GameObject("CharaToItem prototypes");
            container.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(container);
        }
        return container.transform;
    }

    static bool registered;

    // Studio's item lists are loaded once at startup (Info.isLoadList turns true). The setter hook can miss it
    // (a tiny auto-property may be inlined), so Update also watches for it, and loading a scene item checks first.
    public static void EnsureAllRegistered()
    {
        if (registered || !Singleton<Studio.Info>.IsInstance() || !Singleton<Studio.Info>.Instance.isLoadList) return;
        RegisterAll();
    }

    public static void RegisterAll()
    {
        registered = true;
        try
        {
            for (int t = C2ITools.PlaneNo; t <= C2ITools.HoleNo; t++) Register(t, C2ITools.Name(t), new List<string>(), C2ITools.Category);
            Directory.CreateDirectory(C2IStore.Dir);
            foreach (string f in Directory.GetFiles(C2IStore.Dir, "*.c2i"))
            {
                try
                {
                    C2IBake h = C2IStore.Read(f, true);
                    headers[h.no] = h;
                    Register(h.no, h.name, h.fkBones);
                }
                catch (Exception e) { Log.LogWarning("bake " + Path.GetFileName(f) + ": " + e.Message); }
            }
            RefreshItemList();
        }
        catch (Exception e) { Log.LogError("RegisterAll: " + e); }
    }

    static void Register(int no, string name, List<string> fkBones) { Register(no, name, fkBones, C2IStore.Category); }

    static void Register(int no, string name, List<string> fkBones, int category)
    {
        Studio.Info info = Singleton<Studio.Info>.Instance;
        Studio.Info.GroupInfo g;
        if (!info.dicItemGroupCategory.TryGetValue(C2IStore.Group, out g))
        {
            g = new Studio.Info.GroupInfo { name = "Chara → Item", sort = 100000 };
            info.dicItemGroupCategory[C2IStore.Group] = g;
        }
        if (!g.dicCategory.ContainsKey(C2IStore.Category))
            g.dicCategory[C2IStore.Category] = new Studio.Info.CategoryInfo { name = "Baked objects", sort = 0 };
        if (!g.dicCategory.ContainsKey(C2ITools.Category))
            g.dicCategory[C2ITools.Category] = new Studio.Info.CategoryInfo { name = "Cut tools", sort = 1 };
        Dictionary<int, Dictionary<int, Studio.Info.ItemLoadInfo>> cats;
        if (!info.dicItemLoadInfo.TryGetValue(C2IStore.Group, out cats)) info.dicItemLoadInfo[C2IStore.Group] = cats = new Dictionary<int, Dictionary<int, Studio.Info.ItemLoadInfo>>();
        Dictionary<int, Studio.Info.ItemLoadInfo> items;
        if (!cats.TryGetValue(category, out items)) cats[category] = items = new Dictionary<int, Studio.Info.ItemLoadInfo>();
        Studio.Info.ItemLoadInfo li = new Studio.Info.ItemLoadInfo(new List<string> { no.ToString(), C2IStore.Group.ToString(), category.ToString(), name, "", C2IStore.BundleName(no), "item" });
        li.bones = new List<string>(fkBones);
        items[no] = li;
    }

    public static void EnsureRegistered(int category, int no)
    {
        EnsureAllRegistered();
        Studio.Info info = Singleton<Studio.Info>.Instance;
        Dictionary<int, Dictionary<int, Studio.Info.ItemLoadInfo>> cats;
        Dictionary<int, Studio.Info.ItemLoadInfo> items;
        if (info.dicItemLoadInfo.TryGetValue(C2IStore.Group, out cats) && cats.TryGetValue(category, out items) && items.ContainsKey(no)) return;
        C2IBake h;
        if (C2ITools.IsTool(no)) Register(no, C2ITools.Name(no), new List<string>(), C2ITools.Category);
        else if (headers.TryGetValue(no, out h)) Register(no, h.name, h.fkBones);
        else
        {
            Log.LogWarning("scene uses bake " + no + " which is not in " + C2IStore.Dir + "; it loads empty");
            Register(no, "(missing bake " + no + ")", new List<string>());
        }
    }

    // An item whose FK came from HS2US's GenericFK keeps its angles under bone paths ("Rope/root/Bone/Bone.001");
    // Studio's own FK, which a bake with an FK list gets, keys them by bone name. Before such an item loads, each
    // listed bone takes over the angles saved under a path ending in its name; a bone with none saved starts at its
    // baked angles (Studio would start a loaded item's new bone at 0, 0, 0 and straighten a bent rope). The paths
    // are dropped: the bones they name off the list belong to the part that was cut away.
    public static void SeedFK(OIItemInfo info)
    {
        Dictionary<int, Dictionary<int, Studio.Info.ItemLoadInfo>> cats;
        Dictionary<int, Studio.Info.ItemLoadInfo> items;
        Studio.Info.ItemLoadInfo li;
        if (!Singleton<Studio.Info>.Instance.dicItemLoadInfo.TryGetValue(C2IStore.Group, out cats) || !cats.TryGetValue(info.category, out items)
            || !items.TryGetValue(info.no, out li) || li.bones == null || li.bones.Count == 0) return;
        Dictionary<string, OIBoneInfo> byName = new Dictionary<string, OIBoneInfo>();
        foreach (KeyValuePair<string, OIBoneInfo> kv in info.bones.ToList())
        {
            int slash = kv.Key.LastIndexOf('/');
            if (slash < 0) continue;
            string name = kv.Key.Substring(slash + 1);
            if (!byName.ContainsKey(name)) byName[name] = kv.Value;
            info.bones.Remove(kv.Key);
        }
        GameObject proto = null;
        foreach (string name in li.bones)
        {
            if (info.bones.ContainsKey(name)) continue;
            OIBoneInfo bone;
            if (!byName.TryGetValue(name, out bone))
            {
                if (proto == null) proto = GetPrototype(info.no);
                Transform t = FindNode(proto.transform, name);
                if (t == null) continue;
                bone = new OIBoneInfo(Studio.Studio.GetNewIndex());
                bone.changeAmount.rot = t.localEulerAngles;
            }
            info.bones[name] = bone;
        }
    }

    static Transform FindNode(Transform t, string name)
    {
        if (t.name == name) return t;
        foreach (Transform c in t)
        {
            Transform f = FindNode(c, name);
            if (f != null) return f;
        }
        return null;
    }

    // Studio's item list builds its group/category lists from Info; rebuild them so new bakes show up
    static void RefreshItemList()
    {
        try
        {
            ItemGroupList gl = UnityEngine.Object.FindObjectsOfType<ItemGroupList>().FirstOrDefault();
            if (gl != null) gl.InitList();
            foreach (ItemList il in Resources.FindObjectsOfTypeAll<ItemList>())
            {
                FieldInfo fg = typeof(ItemList).GetField("group", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                FieldInfo fc = typeof(ItemList).GetField("category", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fg != null) fg.SetValue(il, -1);
                if (fc != null) fc.SetValue(il, -1);
            }
        }
        catch (Exception e) { Log.LogWarning("item list refresh: " + e.Message); }
    }

    public static GameObject GetPrototype(int no)
    {
        GameObject p;
        if (prototypes.TryGetValue(no, out p) && p != null) return p;
        if (C2ITools.IsTool(no)) { p = C2ITools.Prototype(no, Container()); prototypes[no] = p; return p; }
        StringBuilder log = new StringBuilder();
        C2IBake h;
        try
        {
            if (!headers.TryGetValue(no, out h))
            {
                Log.LogWarning("bake " + no + " is gone; its items load empty");
                p = new GameObject("(missing bake " + no + ")");
                p.transform.SetParent(Container(), false);
                prototypes[no] = p;
                return p;
            }
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            C2IBake b = C2IStore.Read(h.file, false);
            List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            HashSet<string> used = new HashSet<string>();
            p = C2IBuild.Build(b, Container(), texCache, log, owned, used);
            protoOwned[no] = owned;
            protoTex[no] = used;
            Log.LogInfo("built " + b.name + " (" + no + ") in " + sw.ElapsedMilliseconds + " ms: " + b.nodes.Count + " nodes, " + b.rends.Count + " renderers, " + b.textures.Count + " textures");
        }
        catch (Exception e)
        {
            Log.LogError("bake " + no + ": " + e);
            p = new GameObject("(missing bake " + no + ")");
            p.transform.SetParent(Container(), false);
        }
        if (log.Length > 0) Log.LogWarning(log.ToString());
        prototypes[no] = p;
        return p;
    }

    // A new or loaded scene starts with Studio.InitScene: prototypes (and the meshes, materials and stored textures
    // made for them) that no item uses any more are freed a frame later, once the old items are really gone.
    public void ScheduleRelease() { StartCoroutine(ReleaseLater()); }

    IEnumerator ReleaseLater()
    {
        yield return null;
        ReleaseUnused();
    }

    // A bake replaced or deleted while its items are in the scene: those items keep the old prototype's meshes and
    // materials, so these are only dropped from the cache (new items build from the file) and freed at the next InitScene.
    static void Retire(int no)
    {
        GameObject p;
        if (!prototypes.TryGetValue(no, out p)) return;
        if (p != null) retired.Add(p);
        List<UnityEngine.Object> owned;
        if (protoOwned.TryGetValue(no, out owned)) retired.AddRange(owned);
        HashSet<string> tex;
        if (protoTex.TryGetValue(no, out tex)) protoTex[-no] = tex;   // keeps its textures until the next release
        prototypes.Remove(no); protoOwned.Remove(no); protoTex.Remove(no);
    }

    public static string ReleaseUnused()
    {
        foreach (UnityEngine.Object o in retired) if (o != null) Destroy(o);
        retired.Clear();
        foreach (int k in protoTex.Keys.Where(k => k < 0).ToList()) protoTex.Remove(k);
        HashSet<int> live = new HashSet<int>();
        foreach (CharaToItemInstance x in FindObjectsOfType<CharaToItemInstance>()) live.Add(x.no);
        int freed = 0;
        foreach (int no in prototypes.Keys.ToList())
        {
            if (live.Contains(no) || C2ITools.IsTool(no)) continue;
            if (prototypes[no] != null) Destroy(prototypes[no]);
            List<UnityEngine.Object> owned;
            if (protoOwned.TryGetValue(no, out owned)) foreach (UnityEngine.Object o in owned) if (o != null) Destroy(o);
            prototypes.Remove(no); protoOwned.Remove(no); protoTex.Remove(no);
            freed++;
        }
        HashSet<string> keepTex = new HashSet<string>();
        foreach (HashSet<string> h in protoTex.Values) keepTex.UnionWith(h);
        int texFreed = 0;
        foreach (string h in texCache.Keys.ToList())
        {
            if (keepTex.Contains(h)) continue;
            if (texCache[h] != null) Destroy(texCache[h]);
            texCache.Remove(h);
            texFreed++;
        }
        if (freed > 0) Log.LogInfo("released " + freed + " unused prototype(s), " + texFreed + " texture(s)");
        return freed + " prototypes, " + texFreed + " textures";
    }

    // ---- for the Cutter
    public static int AllocateNo() { return NextNo(); }
    public static C2IBake Header(int no) { C2IBake h; return headers.TryGetValue(no, out h) ? h : null; }
    public static string BakeFileOf(int no) { C2IBake h = Header(no); return h != null ? h.file : null; }

    public static void AddBakeFile(string file)
    {
        C2IBake h = C2IStore.Read(file, true);
        Retire(h.no);
        headers[h.no] = h;
        Register(h.no, h.name, h.fkBones);
        RefreshItemList();
    }

    // Numbers are never handed out twice: a deleted bake's number may still be used by items in the scene or in saved
    // scenes, and a reused number made Studio show the old bake's cached prototype for the new one.
    // The highest number handed out is kept in last_no.txt.
    static string LastNoFile { get { return Path.Combine(C2IStore.Dir, "last_no.txt"); } }

    static int NextNo()
    {
        int max = C2IStore.FirstNo - 1;
        foreach (int k in headers.Keys) max = Math.Max(max, k);
        foreach (int k in prototypes.Keys) if (!C2ITools.IsTool(k)) max = Math.Max(max, k);
        foreach (CharaToItemInstance x in FindObjectsOfType<CharaToItemInstance>()) max = Math.Max(max, x.no);
        try { int last; if (File.Exists(LastNoFile) && int.TryParse(File.ReadAllText(LastNoFile).Trim(), out last)) max = Math.Max(max, last); }
        catch (Exception e) { Log.LogWarning("last_no.txt: " + e.Message); }
        int no = max + 1;
        try { Directory.CreateDirectory(C2IStore.Dir); File.WriteAllText(LastNoFile, no.ToString()); }
        catch (Exception e) { Log.LogWarning("last_no.txt: " + e.Message); }
        return no;
    }

    // the item was built from the bake file now registered under its number (false: an older bake, or none).
    // Items built before a script reload carry the previous assembly's component, which GetComponent does not see:
    // those are let through (the Cutter still compares the node tree with the file).
    public static bool MatchesBake(OCIItem oi)
    {
        C2IBake h = Header(oi.itemInfo.no);
        if (h == null) return false;
        CharaToItemInstance x = oi.objectItem != null ? oi.objectItem.GetComponent<CharaToItemInstance>() : null;
        return x == null || x.created == 0 || x.created == h.created;
    }

    // ---------------------------------------------------------------- convert

    public string Convert(int key)
    {
        ObjectCtrlInfo oci;
        if (busy) return "busy";
        if (!Singleton<Studio.Studio>.Instance.dicObjectCtrl.TryGetValue(key, out oci) || !(oci is OCIChar)) return "no character " + key;
        StartCoroutine(ConvertRoutine(new List<OCIChar> { (OCIChar)oci }));
        return "started";
    }

    public void ConvertSelected()
    {
        if (busy) return;
        List<OCIChar> sel = Singleton<Studio.Studio>.Instance.treeNodeCtrl.selectObjectCtrl.OfType<OCIChar>().ToList();
        if (sel.Count == 0) { status = "Select one or more characters in the workspace first."; return; }
        StartCoroutine(ConvertRoutine(sel));
    }

    string customName = "";
    bool customSeveralCharas;
    int madeTotal;

    IEnumerator ConvertRoutine(List<OCIChar> chars)
    {
        busy = true;
        customName = (newName ?? "").Trim();
        customSeveralCharas = chars.Count > 1;
        madeTotal = 0;
        foreach (OCIChar oc in chars)
        {
            IEnumerator e = ConvertOne(oc);
            while (true)
            {
                object cur;
                try { if (!e.MoveNext()) break; cur = e.Current; }
                catch (Exception ex)
                {
                    Log.LogError("convert: " + ex);
                    status = "Failed: " + ex.Message;
                    break;
                }
                yield return cur;
            }
        }
        if (madeTotal > 0 && customName.Length > 0 && (newName ?? "").Trim() == customName) newName = "";
        busy = false;
    }

    static HashSet<string> ParseSpec(string spec)
    {
        if (string.IsNullOrEmpty(spec)) return null;
        return new HashSet<string>(spec.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
    }

    IEnumerator ConvertOne(OCIChar oc)
    {
        ChaControl cha = oc.charInfo;
        string chName = cha.fileParam.fullname;
        List<C2IPart> parts = C2IParts.Of(cha);
        HashSet<string> sel = ParseSpec(partSpec);
        List<HashSet<string>> groups = new List<HashSet<string>>();
        if (sel == null) groups.Add(null);
        else if (splitParts)
        {
            foreach (C2IPart pt in parts) if (sel.Contains(pt.key)) groups.Add(new HashSet<string> { pt.key });
            if (sel.Contains("other")) groups.Add(new HashSet<string> { "other" });
        }
        else groups.Add(sel);
        if (groups.Count == 0) { status = "None of the chosen parts is on " + chName + "."; yield break; }

        bool wasVisible = oc.treeNodeObject.visible;
        // a blink caught mid-way would be frozen into the item: hold the eyes open while capturing
        bool blink = cha.GetEyesBlinkFlag();
        if (!wasVisible) oc.treeNodeObject.SetVisible(true);
        if (blink) cha.ChangeEyesBlinkFlag(false);
        int made = 0;
        try
        {
            yield return null; yield return null; yield return null;
            foreach (HashSet<string> g in groups)
            {
                IEnumerator one = BakeOne(oc, parts, g, groups.Count > 1);
                while (one.MoveNext()) yield return one.Current;
                if (lastBakeOk) { made++; madeTotal++; }
            }
        }
        finally
        {
            if (blink) cha.ChangeEyesBlinkFlag(true);
            if (!wasVisible) oc.treeNodeObject.SetVisible(false);
        }
        if (made > 0 && hideOriginal) oc.treeNodeObject.SetVisible(false);
        if (made > 1) status = "Done: " + made + " items from " + chName;
    }

    bool lastBakeOk;

    // several: one of several items from this character (one per part), so a given name gets the part added
    IEnumerator BakeOne(OCIChar oc, List<C2IPart> parts, HashSet<string> keys, bool several)
    {
        lastBakeOk = false;
        ChaControl cha = oc.charInfo;
        string chName = cha.fileParam.fullname;
        int no = NextNo();
        string label = C2IParts.Label(parts, keys);
        string baseName = customName.Length > 0
            ? customName + (customSeveralCharas ? " - " + chName : "") + (several && label.Length > 0 ? " " + label : "")
            : chName + (label.Length > 0 ? " " + label : "");
        string name = customName.Length > 0 ? baseName : baseName + " " + DateTime.Now.ToString("MM-dd HH:mm");
        status = "Capturing " + name + "...";
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        C2ICapture.Ctx c = C2ICapture.Begin(oc, no, name, fkGroups, keys, fillInsides);
        if (c.bake.rends.Count == 0) { status = "Nothing visible to bake for " + name + "."; Log.LogWarning(status); yield break; }
        long tBegin = sw.ElapsedMilliseconds;
        for (int i = 0; i < c.pendingTex.Count; i++)
        {
            status = "Baking textures " + (i + 1) + " / " + c.pendingTex.Count + " (" + name + ")";
            yield return null;
            C2ICapture.StoreTexture(c, i);
        }
        long tTex = sw.ElapsedMilliseconds;
        // chosen parts (shoes, a hat): the item origin goes to their centre of mass, turned and scaled as the character
        Transform root = cha.objRoot.transform;
        Vector3 origin = root.position;
        Vector3 centre;
        if (keys != null && centrePartOrigin && C2ICutter.Centroid(c.bake, c.liveRends, out centre))
        {
            C2ICutter.Repivot(c.bake, root.localToWorldMatrix, Matrix4x4.TRS(centre, root.rotation, root.lossyScale), "centre");
            origin = centre;
        }
        Directory.CreateDirectory(C2IStore.Dir);
        string file = Path.Combine(C2IStore.Dir, no + "_" + C2IStore.SafeName(baseName) + ".c2i");
        C2IStore.Write(c.bake, file);
        c.bake.file = file;
        Retire(no);
        headers[no] = C2IStore.Read(file, true);
        Register(no, name, c.bake.fkBones);
        RefreshItemList();
        if (c.log.Length > 0) Log.LogWarning("capture notes:\n" + c.log);
        Log.LogInfo("capture timing (ms): " + c.timing);
        Log.LogInfo("baked " + name + " -> " + file + " (" + (new FileInfo(file).Length / 1048576f).ToString("0.00") + " MB; " + c.bake.rends.Count + " renderers; "
            + c.bake.meshes.Count(m => m.srcIndex < 0) + " of " + c.bake.meshes.Count + " meshes and " + c.pendingTex.Count + " of " + c.bake.textures.Count
            + " textures stored; insides " + c.bake.fillInside + ") in " + sw.ElapsedMilliseconds + " ms (capture " + tBegin + ", textures " + (tTex - tBegin) + ", write " + (sw.ElapsedMilliseconds - tTex) + ")");

        // the item is built from the file, so what appears now is exactly what later sessions load
        status = "Adding item...";
        yield return null;
        OCIItem item = AddObjectItem.Add(C2IStore.Group, C2IStore.Category, no);
        if (item == null) { status = "Baked, but the item could not be added (see log)."; yield break; }
        Transform space = item.objectItem.transform.parent;
        item.objectInfo.changeAmount.pos = space != null ? space.InverseTransformPoint(origin) : origin;
        item.objectInfo.changeAmount.rot = (space != null ? Quaternion.Inverse(space.rotation) * root.rotation : root.rotation).eulerAngles;
        item.objectInfo.changeAmount.scale = root.lossyScale;
        Singleton<UndoRedoManager>.Instance.Clear();
        lastNo = no;
        lastItemKey = item.objectInfo.dicKey;
        try { Singleton<Studio.Studio>.Instance.treeNodeCtrl.SelectSingle(item.treeNodeObject); }
        catch (Exception ex) { Log.LogWarning("select: " + ex.Message); }
        status = "Done: " + name + " (" + (sw.ElapsedMilliseconds / 1000f).ToString("0.0") + " s)";
        lastBakeOk = true;
    }

    // parts of a character in the scene, for scripts: "key<TAB>label<TAB>item" per line
    public string PartsOf(int key)
    {
        ObjectCtrlInfo oci;
        if (!Singleton<Studio.Studio>.Instance.dicObjectCtrl.TryGetValue(key, out oci) || !(oci is OCIChar)) return "no character " + key;
        StringBuilder sb = new StringBuilder();
        foreach (C2IPart pt in C2IParts.Of(((OCIChar)oci).charInfo)) sb.AppendLine(pt.key + "\t" + pt.label + "\t" + pt.item);
        return sb.ToString();
    }

    public string AddBake(int no)
    {
        if (!headers.ContainsKey(no)) return "no bake " + no;
        Singleton<Studio.Studio>.Instance.AddItem(C2IStore.Group, C2IStore.Category, no);
        return "ok";
    }

    public string DeleteBake(int no)
    {
        C2IBake h;
        if (!headers.TryGetValue(no, out h)) return "no bake " + no;
        File.Delete(h.file);
        headers.Remove(no);
        Retire(no);
        Dictionary<int, Dictionary<int, Studio.Info.ItemLoadInfo>> cats;
        Dictionary<int, Studio.Info.ItemLoadInfo> items;
        if (Singleton<Studio.Info>.Instance.dicItemLoadInfo.TryGetValue(C2IStore.Group, out cats) && cats.TryGetValue(C2IStore.Category, out items)) items.Remove(no);
        int removed = busy || (C2ICutter.Instance != null && C2ICutter.Instance.busy) ? 0 : SweepTextures();
        RefreshItemList();
        return "deleted " + h.name + " (" + removed + " unused textures removed)";
    }

    // Renames a bake: rewrites its file under the new name, renames it in Studio's item list and renames the items in
    // the scene that still carry the old name (a loaded scene names its items from the list anyway).
    public string RenameBake(int no, string name)
    {
        name = (name ?? "").Trim();
        C2IBake h;
        if (!headers.TryGetValue(no, out h)) return "no bake " + no;
        if (name.Length == 0) return "The name is empty.";
        if (name == h.name) return "Name unchanged.";
        if (busy || (C2ICutter.Instance != null && C2ICutter.Instance.busy)) return "Busy; rename after the current job.";
        string old = h.name;
        try
        {
            C2IBake b = C2IStore.Read(h.file, false);
            b.name = name;
            string file = Path.Combine(C2IStore.Dir, no + "_" + C2IStore.SafeName(name) + ".c2i");
            string tmp = file + ".tmp";
            C2IStore.Write(b, tmp);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
            if (!string.Equals(Path.GetFullPath(h.file), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase) && File.Exists(h.file)) File.Delete(h.file);
            C2IBake nh = C2IStore.Read(file, true);
            headers[no] = nh;
            Register(no, nh.name, nh.fkBones);
            RefreshItemList();
        }
        catch (Exception e) { Log.LogError("rename " + no + ": " + e); return "Rename failed: " + e.Message; }
        int n = 0;
        foreach (ObjectCtrlInfo o in Singleton<Studio.Studio>.Instance.dicObjectCtrl.Values)
        {
            OCIItem it = o as OCIItem;
            if (it == null || it.itemInfo.group != C2IStore.Group || it.itemInfo.category != C2IStore.Category || it.itemInfo.no != no) continue;
            if (it.treeNodeObject.textName != old) continue;
            it.treeNodeObject.textName = name;
            n++;
        }
        return "Renamed to " + name + (n > 0 ? " (" + n + " item(s) in the scene)" : "");
    }

    static readonly HashSet<string> PieceKinds = new HashSet<string> { "back", "front", "inside", "outside", "torn", "holed" };

    // diagnostics (read only): what TrimCutFK would keep for each cut piece
    public string FKReport()
    {
        StringBuilder sb = new StringBuilder();
        foreach (C2IBake h in headers.Values.OrderBy(x => x.no))
        {
            if (!PieceKinds.Contains(h.parts ?? "")) continue;
            try
            {
                C2IBake b = C2IStore.Read(h.file, false);
                HashSet<string> used = C2IFK.Used(b);
                List<string> kept = b.fkBones.Count > 0 ? b.fkBones.Where(used.Contains).ToList() : C2IFK.Weighted(b);
                sb.Append(h.no + " " + h.name + ": FK " + (b.fkBones.Count > 0 ? b.fkBones.Count.ToString() : "none (GenericFK: every bone)") + " -> " + kept.Count
                    + "  e.g. " + string.Join(", ", kept.Take(8).ToArray()) + "\n");
            }
            catch (Exception e) { sb.Append(h.no + " " + h.name + ": " + e.Message + "\n"); }
        }
        return sb.Length > 0 ? sb.ToString() : "no cut pieces";
    }

    // Pieces made by the Cutter before it trimmed FK: drop the FK bones that move nothing in them, and give pieces of
    // props without FK their own list (C2IFK.Weighted). Rewrites the bake files; items already in the scene keep their
    // guides until they are added again or the scene is saved and loaded again (their angles carry over: SeedFK).
    public string TrimCutFK()
    {
        if (busy || (C2ICutter.Instance != null && C2ICutter.Instance.busy)) return "Busy; try again after the current job.";
        int files = 0, bones = 0, given = 0;
        foreach (C2IBake h in headers.Values.ToList())
        {
            if (!PieceKinds.Contains(h.parts ?? "")) continue;
            try
            {
                C2IBake b = C2IStore.Read(h.file, false);
                int n = C2IFK.Prune(b);
                if (C2IFK.GiveWeighted(b)) given++;
                else if (n == 0) continue;
                string tmp = h.file + ".tmp";
                C2IStore.Write(b, tmp);
                File.Delete(h.file);
                File.Move(tmp, h.file);
                C2IBake nh = C2IStore.Read(h.file, true);
                headers[h.no] = nh;
                Register(h.no, nh.name, nh.fkBones);
                files++; bones += n;
            }
            catch (Exception e) { Log.LogWarning("trim FK " + h.no + ": " + e.Message); }
        }
        RefreshItemList();
        if (files == 0) return "No cut piece had FK bones to drop.";
        return "Trimmed the FK of " + files + " cut piece(s)" + (bones > 0 ? ", " + bones + " bone(s) dropped" : "")
            + (given > 0 ? ", " + given + " piece(s) of props without FK given only their own bones" : "")
            + ". Save and load the scene again (or add the pieces again) to see it.";
    }

    // Bakes of chosen parts made before their origin went to the parts' centre (a root node named "centre"): re-roots
    // them there, measured on the prototype. Items of them in the scene are moved by the same offset, so they stay put
    // once the scene is saved and loaded again (until then they show the old mesh, shifted). Cut pieces keep their
    // origin on the cut. spec: bake numbers ("73100068,73100069"), or blank for all such bakes.
    public string CentreOrigins(string spec)
    {
        if (busy || (C2ICutter.Instance != null && C2ICutter.Instance.busy)) return "Busy; try again after the current job.";
        HashSet<string> only = ParseSpec(spec);
        StringBuilder sb = new StringBuilder();
        int files = 0, moved = 0;
        foreach (C2IBake h in headers.Values.OrderBy(x => x.no).ToList())
        {
            if (only != null ? !only.Contains(h.no.ToString()) : (string.IsNullOrEmpty(h.parts) || PieceKinds.Contains(h.parts))) continue;
            try
            {
                C2IBake b = C2IStore.Read(h.file, false);
                if (b.nodes.Count > 0 && b.nodes[0].name == "centre") { sb.AppendLine(h.no + " " + h.name + ": already centred"); continue; }
                Transform proto = GetPrototype(h.no).transform;
                List<Renderer> rends = C2ICutter.RendsOf(b, proto);
                Vector3 c;
                if (rends == null || !C2ICutter.Centroid(b, rends, out c)) { sb.AppendLine(h.no + " " + h.name + ": nothing to measure"); continue; }
                Vector3 local = proto.InverseTransformPoint(c);
                C2ICutter.Repivot(b, Matrix4x4.identity, Matrix4x4.TRS(local, Quaternion.identity, Vector3.one), "centre");
                string tmp = h.file + ".tmp";
                C2IStore.Write(b, tmp);
                File.Delete(h.file);
                File.Move(tmp, h.file);
                AddBakeFile(h.file);
                int n = 0;
                foreach (ObjectCtrlInfo o in Singleton<Studio.Studio>.Instance.dicObjectCtrl.Values.ToList())
                {
                    OCIItem it = o as OCIItem;
                    if (it == null || it.itemInfo.group != C2IStore.Group || it.itemInfo.category != C2IStore.Category || it.itemInfo.no != h.no) continue;
                    Transform t = it.objectItem.transform;
                    Vector3 w = t.TransformPoint(local);
                    it.objectInfo.changeAmount.pos = t.parent != null ? t.parent.InverseTransformPoint(w) : w;
                    n++;
                }
                files++; moved += n;
                sb.AppendLine(h.no + " " + h.name + ": origin moved by " + local.ToString("F3") + (n > 0 ? ", " + n + " scene item(s) moved with it" : ""));
            }
            catch (Exception e) { Log.LogWarning("centre " + h.no + ": " + e); sb.AppendLine(h.no + " " + h.name + ": " + e.Message); }
        }
        RefreshItemList();
        if (files > 0) sb.AppendLine("Save and load the scene again: until then the moved items show their old mesh, shifted.");
        return sb.Length > 0 ? sb.ToString() : "no part bakes";
    }

    // texture files no bake refers to any more (not while a bake is being written: its textures come first)
    int SweepTextures()
    {
        HashSet<string> keep = new HashSet<string>();
        foreach (C2IBake o in headers.Values) foreach (string t in o.texHashes) keep.Add(t);
        int removed = 0;
        if (!Directory.Exists(C2IStore.TexDir)) return 0;
        foreach (string f in Directory.GetFiles(C2IStore.TexDir, "*.c2t"))
            if (!keep.Contains(Path.GetFileNameWithoutExtension(f))) { File.Delete(f); removed++; }
        return removed;
    }

    // ---------------------------------------------------------------- diagnostics (StudioBridge plugin.call)

    // removes a scene object as Studio's delete button does (test clean-up)
    public string DeleteObject(int key)
    {
        ObjectCtrlInfo oci;
        if (!Singleton<Studio.Studio>.Instance.dicObjectCtrl.TryGetValue(key, out oci)) return "no object " + key;
        Singleton<Studio.Studio>.Instance.treeNodeCtrl.DeleteNode(oci.treeNodeObject);
        Singleton<UndoRedoManager>.Instance.Clear();
        return "deleted " + key;
    }

    public string Vis(int key, bool on)
    {
        ObjectCtrlInfo oci;
        if (!Singleton<Studio.Studio>.Instance.dicObjectCtrl.TryGetValue(key, out oci)) return "no object " + key;
        oci.treeNodeObject.SetVisible(on);
        return "ok";
    }

    // compares every kept renderer of a character with the same-named renderer of an item made from it:
    // shader, queue, keywords, the known property values, and the texels each texture slot samples
    public string Verify(int charKey, int itemKey)
    {
        Studio.Studio st = Singleton<Studio.Studio>.Instance;
        OCIChar oc = st.dicObjectCtrl[charKey] as OCIChar;
        OCIItem oi = st.dicObjectCtrl[itemKey] as OCIItem;
        Transform cr = oc.charInfo.objRoot.transform, ir = oi.objectItem.transform;
        Dictionary<string, Renderer> items = new Dictionary<string, Renderer>();
        foreach (Renderer r in ir.GetComponentsInChildren<Renderer>(true)) items[PathOf(r.transform, ir)] = r;
        StringBuilder sb = new StringBuilder();
        RenderTexture rt = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
        int nMat = 0, nBad = 0;
        foreach (Renderer r in cr.GetComponentsInChildren<Renderer>(true))
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            Renderer r2;
            if (!items.TryGetValue(PathOf(r.transform, cr), out r2)) { sb.AppendLine("MISSING renderer " + PathOf(r.transform, cr)); continue; }
            Material[] a = r.sharedMaterials, b = r2.sharedMaterials;
            for (int k = 0; k < Math.Min(a.Length, b.Length); k++)
            {
                Material ma = a[k], mb = b[k];
                if (ma == null || mb == null) continue;
                nMat++;
                List<string> diffs = new List<string>();
                if (ma.shader != mb.shader) diffs.Add("shader " + ma.shader.name + " vs " + mb.shader.name);
                if (ma.renderQueue != mb.renderQueue) diffs.Add("queue " + ma.renderQueue + " vs " + mb.renderQueue);
                string ka = string.Join(" ", ma.shaderKeywords.OrderBy(x => x).ToArray()), kb = string.Join(" ", mb.shaderKeywords.OrderBy(x => x).ToArray());
                if (ka != kb) diffs.Add("keywords [" + ka + "] vs [" + kb + "]");
                foreach (string p in C2INames.Floats)
                    if (ma.HasProperty(p) && Mathf.Abs(ma.GetFloat(p) - mb.GetFloat(p)) > 1e-4f) diffs.Add(p + " f " + ma.GetFloat(p) + " vs " + mb.GetFloat(p));
                foreach (string p in C2INames.Vectors)
                    if (ma.HasProperty(p) && (ma.GetVector(p) - mb.GetVector(p)).sqrMagnitude > 1e-6f) diffs.Add(p + " v " + ma.GetVector(p) + " vs " + mb.GetVector(p));
                foreach (string p in ma.GetTexturePropertyNames())
                {
                    Texture ta = ma.GetTexture(p), tb = mb.HasProperty(p) ? mb.GetTexture(p) : null;
                    if (ta == null && tb == null) continue;
                    if (ta == null || tb == null) { diffs.Add(p + " tex " + (ta ? ta.name : "null") + " vs " + (tb ? tb.name : "null")); continue; }
                    if (ma.GetTextureScale(p) != mb.GetTextureScale(p) || ma.GetTextureOffset(p) != mb.GetTextureOffset(p)) diffs.Add(p + " st differs");
                    if (ta == tb) continue;
                    Vector4 ea = MeanTexel(ta, rt), eb = MeanTexel(tb, rt), ed = MeanAbsDiff(ta, tb, rt);
                    if (ed.x + ed.y + ed.z + ed.w > 0.02f) diffs.Add(p + " texels mean " + ea.ToString("F3") + " vs " + eb.ToString("F3") + " absdiff " + ed.ToString("F3") + " (" + ta.name + ")");
                }
                if (diffs.Count > 0) { nBad++; sb.AppendLine(r.name + "#" + k + " " + ma.name + ":"); foreach (string d in diffs) sb.AppendLine("    " + d); }
            }
        }
        RenderTexture.ReleaseTemporary(rt);
        sb.Insert(0, "materials " + nMat + ", with differences " + nBad + "\n");
        Directory.CreateDirectory(C2IStore.Dir); string file = Path.Combine(C2IStore.Dir, "verify_" + itemKey + ".txt");
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    static string PathOf(Transform t, Transform root)
    {
        List<string> parts = new List<string>();
        while (t != null && t != root) { parts.Add(t.name); t = t.parent; }
        parts.Reverse();
        return string.Join("/", parts.ToArray());
    }

    static Color[] Sample(Texture t, RenderTexture rt)
    {
        Graphics.Blit(t, rt);
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D x = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
        x.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
        x.Apply(false);
        RenderTexture.active = prev;
        Color[] px = x.GetPixels();
        Destroy(x);
        return px;
    }

    static Vector4 MeanTexel(Texture t, RenderTexture rt)
    {
        Color[] px = Sample(t, rt);
        Vector4 s = Vector4.zero;
        foreach (Color c in px) s += (Vector4)c;
        return s / px.Length;
    }

    static Vector4 MeanAbsDiff(Texture a, Texture b, RenderTexture rt)
    {
        Color[] pa = Sample(a, rt), pb = Sample(b, rt);
        Vector4 s = Vector4.zero;
        for (int i = 0; i < pa.Length; i++) s += new Vector4(Mathf.Abs(pa[i].r - pb[i].r), Mathf.Abs(pa[i].g - pb[i].g), Mathf.Abs(pa[i].b - pb[i].b), Mathf.Abs(pa[i].a - pb[i].a));
        return s / pa.Length;
    }

    // "bone:x,y,z;bone2:x,y,z" (local Euler, like the item FK panel); enables the item's FK; empty spec lists the bones
    public string ItemFK(int itemKey, string spec)
    {
        OCIItem oi = Singleton<Studio.Studio>.Instance.dicObjectCtrl[itemKey] as OCIItem;
        if (oi == null || !oi.isFK) return "not an item with FK";
        oi.ActiveFK(true);
        StringBuilder sb = new StringBuilder();
        if (string.IsNullOrEmpty(spec))
        {
            foreach (KeyValuePair<string, OIBoneInfo> kv in oi.itemInfo.bones) sb.Append(kv.Key + ":" + kv.Value.changeAmount.rot.ToString("F1") + ";");
            return sb.ToString();
        }
        foreach (string part in spec.Split(';'))
        {
            if (part.Trim() == "") continue;
            string[] kv = part.Split(':');
            string[] v = kv[1].Split(',');
            OIBoneInfo bi;
            if (!oi.itemInfo.bones.TryGetValue(kv[0], out bi)) { sb.Append("no bone " + kv[0] + "; "); continue; }
            bi.changeAmount.rot = new Vector3(float.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.Length > 0 ? sb.ToString() : "ok";
    }

    public void Show(bool on) { showWindow = on; }

    public string SelectKey(int key)
    {
        Studio.Studio st = Singleton<Studio.Studio>.Instance;
        OCIItem oi = st.dicObjectCtrl[key] as OCIItem;
        try { st.treeNodeCtrl.SelectSingle(st.dicObjectCtrl[key].treeNodeObject); } catch (Exception e) { return "select: " + e.Message; }
        if (oi == null || oi.listBones == null) return "selected";
        int active = oi.listBones.Count(b => b.active);
        GuideObject g0 = oi.listBones.Count > 0 ? oi.listBones[0].guideObject : null;
        return "selected; fk guides " + active + "/" + oi.listBones.Count + (g0 != null ? " first guide active=" + g0.gameObject.activeInHierarchy + " scale " + g0.transform.lossyScale.ToString("F3") + " visible=" + g0.visible : "");
    }

    public string DuplicateKey(int key)
    {
        Studio.Studio st = Singleton<Studio.Studio>.Instance;
        string err = "";
        try { st.treeNodeCtrl.SelectSingle(st.dicObjectCtrl[key].treeNodeObject); } catch (Exception e) { err = " (select: " + e.GetType().Name + ")"; }
        int n = st.treeNodeCtrl.selectNodes.Length;
        int before = st.dicObjectCtrl.Count;
        st.Duplicate();
        return "selected " + n + err + ", objects " + before + " -> " + st.dicObjectCtrl.Count;
    }
    public void ScreenShot(string path)
    {
        // ScreenCaptureModule is not referenced by the script compiler
        Type t = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule");
        t.GetMethod("CaptureScreenshot", new Type[] { typeof(string) }).Invoke(null, new object[] { path });
    }

    // test helpers: Studio's own save (as SaveScene does it) to a chosen path, and the UI's load coroutine
    public string SaveSceneTo(string path)
    {
        Studio.Studio st = Singleton<Studio.Studio>.Instance;
        foreach (KeyValuePair<int, ObjectCtrlInfo> kv in st.dicObjectCtrl) kv.Value.OnSavePreprocessing();
        st.sceneInfo.cameraSaveData = st.cameraCtrl.Export();
        return st.sceneInfo.Save(path) ? "saved" : "failed";
    }

    public bool sceneLoading;
    public string LoadSceneFrom(string path)
    {
        if (!File.Exists(path)) return "no file";
        StartCoroutine(LoadSceneRoutine(path));
        return "loading";
    }

    IEnumerator LoadSceneRoutine(string path)
    {
        sceneLoading = true;
        yield return Singleton<Studio.Studio>.Instance.LoadSceneCoroutine(path);
        sceneLoading = false;
    }

    public string Describe(int no)
    {
        C2IBake h;
        if (!headers.TryGetValue(no, out h)) return "no bake " + no;
        C2IBake b = C2IStore.Read(h.file, false);
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(b.name + " nodes " + b.nodes.Count + " meshes " + b.meshes.Count + " (" + b.meshes.Sum(m => m.v != null ? m.v.Length : 0) + " stored verts, " + b.meshes.Count(m => m.srcIndex >= 0) + " referenced) rends " + b.rends.Count + " mats " + b.mats.Count + " fk " + b.fkBones.Count);
        for (int i = 0; i < b.sources.Count; i++) sb.AppendLine("src" + i + " " + b.sources[i].ab + " / " + b.sources[i].asset + " / " + b.sources[i].manifest);
        foreach (C2IMat m in b.mats)
        {
            sb.AppendLine("M " + m.name + " | " + m.shader + " src=" + m.source + " " + m.srcPath + "#" + m.srcSlot + " '" + m.srcName + "' f=" + m.floats.Count + " v=" + m.vectors.Count);
            foreach (C2ITexSlot s in m.slots)
                if (s.mode != 0)
                {
                    C2ITex t = s.tex >= 0 ? b.textures[s.tex] : null;
                    string what = t == null ? "" : t.srcIndex >= 0 ? " " + t.name + " (in source " + t.srcIndex + ")" : t.refAB != "" ? " " + t.name + " (asset " + t.refAB + ")"
                        : " " + t.name + " " + t.w + "x" + t.h + " fmt=" + (TextureFormat)t.format + " lin=" + t.linear + " stored";
                    sb.AppendLine("   " + s.prop + " mode=" + s.mode + what);
                }
            sb.AppendLine("   shaderSource=" + m.shaderSource);
        }
        Directory.CreateDirectory(C2IStore.Dir); string file = Path.Combine(C2IStore.Dir, "describe_" + no + ".txt");
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    public string ListBakes()
    {
        StringBuilder sb = new StringBuilder();
        foreach (C2IBake h in headers.Values.OrderBy(x => x.no)) sb.AppendLine(h.no + "\t" + h.name + "\t" + Path.GetFileName(h.file) + "\t" + h.fkBones.Count + " FK");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- frame loop, toolbar, window

    void Update()
    {
        EnsureAllRegistered();
        if (!StudioAPI.StudioLoaded) { MouseOverWindow = false; return; }
        EnsureToolbarButton();
        SyncToolbarButton();
        float sc = UiScale();
        Vector2 gm = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / sc;
        MouseOverWindow = showWindow && windowRect.Contains(gm);
        if (ownsFocus && (!showWindow || (Input.GetMouseButtonDown(0) && !MouseOverWindow))) { GUIUtility.keyboardControl = 0; ownsFocus = false; renaming = -1; }
    }

    KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle toolbarToggle;
    bool toolbarFailed;
    Texture2D toolbarIcon;

    void EnsureToolbarButton()
    {
        if (toolbarToggle != null || toolbarFailed || !StudioAPI.StudioLoaded) return;
        try
        {
            toolbarIcon = MakeToolbarIcon();
            toolbarToggle = new KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle("stp3.charatoitem", "Chara → Item", () => toolbarIcon, showWindow,
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
        PropertyInfo pi = typeof(KKAPI.KoikatuAPI).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
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

    // a figure standing on a small plinth (y = 0 is the bottom row)
    static Texture2D MakeToolbarIcon()
    {
        Color32[] px = new Color32[32 * 32];
        Color32 on = new Color32(255, 255, 255, 255);
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);
        Action<int, int, int, int> rect = (x0, y0, x1, y1) =>
        {
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) px[y * 32 + x] = on;
        };
        rect(7, 2, 24, 5);          // plinth
        rect(13, 6, 14, 13);        // legs
        rect(17, 6, 18, 13);
        rect(12, 14, 19, 22);       // body
        rect(9, 15, 11, 21);        // arms
        rect(20, 15, 22, 21);
        for (int y = 24; y <= 29; y++)   // head
            for (int x = 13; x <= 18; x++)
                if ((x - 15.5f) * (x - 15.5f) + (y - 26.5f) * (y - 26.5f) <= 9.5f) px[y * 32 + x] = on;
        Texture2D t = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        t.SetPixels32(px); t.Apply(false);
        t.name = "CharaToItemIcon";
        return t;
    }

    float UiScale()
    {
        if (uiScale > 0f) return uiScale;
        return Mathf.Max(1f, Mathf.Round(Screen.height / 1080f * 4f) / 4f);
    }

    void OnGUI()
    {
        if (!showWindow || !StudioAPI.StudioLoaded) return;
        GUI.depth = -1000;
        float sc = UiScale();
        Matrix4x4 old = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(sc, sc, 1f));
        float sw = Screen.width / sc, sh = Screen.height / sc;
        windowRect.width = 420f;
        windowRect.height = Mathf.Max(200f, Mathf.Min(sh - 40f, 800f));
        if (windowRect.x < 0f) windowRect.x = Mathf.Max(0f, sw - 3f * (windowRect.width + 20f));
        windowRect = GUI.Window(0x43324954, windowRect, DrawWindow, "Chara → Item");
        windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, sw - 60f));
        windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, sh - windowRect.height));
        GUI.matrix = old;
    }

    // ---- parts: listed for the first selected character; the choice (part keys) applies to every converted one
    List<C2IPart> uiParts = new List<C2IPart>();
    ChaControl uiChara;
    float uiPartsTime;
    Vector2 partsScroll;

    void RefreshUiParts()
    {
        if (Time.unscaledTime - uiPartsTime < 0.5f) return;
        uiPartsTime = Time.unscaledTime;
        OCIChar oc = null;
        try { oc = Singleton<Studio.Studio>.Instance.treeNodeCtrl.selectObjectCtrl.OfType<OCIChar>().FirstOrDefault(); } catch (Exception) { }
        ChaControl cha = oc != null ? oc.charInfo : null;
        if (cha == null) { uiChara = null; uiParts.Clear(); return; }
        uiChara = cha;
        try { uiParts = C2IParts.Of(cha); } catch (Exception) { uiParts.Clear(); }
    }

    void SetParts(HashSet<string> keys) { partSpec = keys == null ? "" : string.Join(",", keys.OrderBy(x => x).ToArray()); }

    void DrawParts()
    {
        if (Event.current.type == EventType.Layout) RefreshUiParts();
        HashSet<string> sel = ParseSpec(partSpec);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Parts:", GUILayout.Width(45));
        if (GUILayout.Toggle(sel == null, "Whole", "Button")) { if (sel != null) SetParts(null); }
        if (GUILayout.Button("Body")) SetParts(C2IParts.Preset(uiParts, "body"));
        if (GUILayout.Button("Hair")) SetParts(C2IParts.Preset(uiParts, "hair"));
        if (GUILayout.Button("Clothes")) SetParts(C2IParts.Preset(uiParts, "clothes"));
        if (GUILayout.Button("Accs")) SetParts(C2IParts.Preset(uiParts, "acc"));
        GUILayout.EndHorizontal();
        if (uiChara == null) GUILayout.Label("(select a character to list its parts)");
        else
        {
            partsScroll = GUILayout.BeginScrollView(partsScroll, GUILayout.Height(170));
            List<KeyValuePair<string, string>> rows = uiParts.Select(x => new KeyValuePair<string, string>(x.key, x.label + (x.item.Length > 0 ? ": " + x.item : ""))).ToList();
            rows.Add(new KeyValuePair<string, string>("other", "Other (animation props, plugin objects)"));
            foreach (KeyValuePair<string, string> row in rows)
            {
                bool on = sel == null || sel.Contains(row.Key);
                bool now = GUILayout.Toggle(on, " " + row.Value);
                if (now != on)
                {
                    HashSet<string> k = sel ?? new HashSet<string>(rows.Select(x => x.Key));
                    if (now) k.Add(row.Key); else k.Remove(row.Key);
                    SetParts(k);
                    sel = ParseSpec(partSpec);
                }
            }
            GUILayout.EndScrollView();
        }
        splitParts = GUILayout.Toggle(splitParts, " One item per part");
        fillInsides = GUILayout.Toggle(fillInsides, " Solid insides where the body is missing");
        centrePartOrigin = GUILayout.Toggle(centrePartOrigin, " Item origin at the parts' centre (off: the character's)");
    }

    void GroupToggle(string label, int mask)
    {
        bool on = (fkGroups & mask) != 0;
        bool now = GUILayout.Toggle(on, " " + label, GUILayout.Width(80));
        if (now != on) fkGroups = now ? fkGroups | mask : fkGroups & ~mask;
    }

    // Text fields: InputHotkeyBlock blocks every hotkey (Studio's and plugins') while an IMGUI text field has the
    // keyboard, so our fields give it up on Enter / Escape, on a click outside the window and when the window closes.
    void TextKeys()
    {
        Event ev = Event.current;
        string focus = GUI.GetNameOfFocusedControl() ?? "";
        ownsFocus = GUIUtility.keyboardControl != 0 && focus.StartsWith("c2i_");
        if (!ownsFocus || ev.type != EventType.KeyDown) return;
        if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
        {
            if (focus == "c2i_rename" && renaming >= 0) status = RenameBake(renaming, renameText);
            renaming = -1;
            GUIUtility.keyboardControl = 0;
            ev.Use();
        }
        else if (ev.keyCode == KeyCode.Escape)
        {
            renaming = -1;
            GUIUtility.keyboardControl = 0;
            ev.Use();
        }
    }

    void DrawWindow(int id)
    {
        // darker backdrop: the default skin is see-through over busy maps. Drawn first inside the window so it lies
        // under the controls (drawn outside, before GUI.Window, it ended up over them and dimmed the text)
        // (below the title bar, which the skin has already drawn)
        float top = Mathf.Max(16f, GUI.skin.window.border.top);
        Color oc = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.75f);
        GUI.DrawTexture(new Rect(0f, top, windowRect.width, windowRect.height - top), Texture2D.whiteTexture);
        GUI.color = oc;
        TextKeys();
        GUILayout.BeginHorizontal();
        GUILayout.Label("UI " + UiScale().ToString("0.00"), GUILayout.Width(60));
        if (GUILayout.Button("-")) uiScale = Mathf.Max(0.75f, UiScale() - 0.25f);
        if (GUILayout.Button("+")) uiScale = Mathf.Min(4f, UiScale() + 0.25f);
        if (GUILayout.Button("auto")) uiScale = 0f;
        GUILayout.EndHorizontal();

        GUILayout.Label("Bakes the selected character(s), or chosen parts of them, into items with FK bones (pose, expression, clothes and accessories as they are now).");
        hideOriginal = GUILayout.Toggle(hideOriginal, " Hide the character after converting");
        GUILayout.BeginHorizontal();
        GUILayout.Label("FK bones:", GUILayout.Width(70));
        GroupToggle("Body", 0x1F); GroupToggle("Hands", 0x60); GroupToggle("Neck", 0x100);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("", GUILayout.Width(70));
        GroupToggle("Breast", 0x200); GroupToggle("Hair", 0x80); GroupToggle("Skirt", 0x400);
        GUILayout.EndHorizontal();
        DrawParts();
        GUILayout.BeginHorizontal();
        GUILayout.Label("Name", GUILayout.Width(45));
        GUI.SetNextControlName("c2i_newname");
        newName = GUILayout.TextField(newName ?? "");
        GUILayout.EndHorizontal();
        GUILayout.Label(string.IsNullOrEmpty(newName) ? "  Blank: character, parts and time." : "  Several parts or characters get the part / character added.");
        GUI.enabled = !busy;
        if (GUILayout.Button(busy ? "Working..." : "Convert selected character", GUILayout.Height(30))) { GUIUtility.keyboardControl = 0; ConvertSelected(); }
        GUI.enabled = true;
        if (!string.IsNullOrEmpty(status)) GUILayout.Label(status);

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Baked items (" + headers.Count + ")");
        if (GUILayout.Button("Trim FK of cut pieces", GUILayout.Width(160))) status = TrimCutFK();
        if (GUILayout.Button("Open folder", GUILayout.Width(100)))
        {
            Directory.CreateDirectory(C2IStore.Dir);
            System.Diagnostics.Process.Start(C2IStore.Dir);
        }
        GUILayout.EndHorizontal();
        scroll = GUILayout.BeginScrollView(scroll);
        foreach (C2IBake h in headers.Values.OrderByDescending(x => x.no).ToList())
        {
            GUILayout.BeginHorizontal();
            if (renaming == h.no)
            {
                GUI.SetNextControlName("c2i_rename");
                renameText = GUILayout.TextField(renameText ?? "");
                if (GUILayout.Button("OK", GUILayout.Width(40))) { status = RenameBake(h.no, renameText); renaming = -1; GUIUtility.keyboardControl = 0; }
                if (GUILayout.Button("Cancel", GUILayout.Width(60))) { renaming = -1; GUIUtility.keyboardControl = 0; }
                GUILayout.EndHorizontal();
                continue;
            }
            GUILayout.Label(h.name);
            if (GUILayout.Button("Rename", GUILayout.Width(70))) { renaming = h.no; renameText = h.name; GUI.FocusControl("c2i_rename"); }
            if (GUILayout.Button("Add", GUILayout.Width(50))) AddBake(h.no);
            if (confirmDelete == h.no)
            {
                if (GUILayout.Button("Sure?", GUILayout.Width(60))) { status = DeleteBake(h.no); confirmDelete = -1; }
            }
            else if (GUILayout.Button("Delete", GUILayout.Width(60))) confirmDelete = h.no;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
        GUILayout.Label("Items also appear in the item list under \"Chara → Item\". Item FK: Kinematics → FK on the item.");
        GUI.DragWindow();
    }
}
