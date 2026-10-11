// #name CharaToItem Cutter
// #author stp3
// #desc Cuts Studio objects (characters, items, baked items) with a plane or a box into new FK items.
// #proc_filter StudioNEOV2

// Part of CharaToItem (shares its bake format, item registration and loader; the host adds this component).
//
// A cut is made with a marker item, "Cut plane" or "Cut box" (group "Chara → Item", category "Cut tools"), placed
// with Studio's own move/rotate/scale guides. The target is captured the way CharaToItem bakes (a character), or
// from its bake file and live pose (one of our items), or from its prefab (any other Studio item). Every mesh is
// cut where it is posed now: the vertices are skinned on the CPU to world space, the cut function is evaluated
// there, and triangles are clipped in mesh space with all attributes interpolated (bone weights merged), so the
// pieces keep their skinning and FK. A ragged cut adds a zigzag + noise height field to the cutting surface; the
// triangles near the cut are subdivided first so the teeth show even on coarse meshes. Cut renderers draw their
// inside with a flat section colour (garments and hair: their own material, they are sheets).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using AIChara;
using KKAPI.Studio;
using Studio;
using UnityEngine;
using UnityEngine.Rendering;

// ==================================================================================================== cut markers

public static class C2ITools
{
    public const int Category = 1;
    public const int PlaneNo = 73099001, BoxNo = 73099002, TearNo = 73099003, HoleNo = 73099004;
    public const float PlaneSize = 10f;   // units (1 unit = 10 cm)
    public const float BoxSize = 4f;
    public const float TearLength = 1.5f, TearWidth = 0.4f, HoleSize = 0.6f, OpeningDepth = 0.3f;

    public static bool IsTool(int no) { return no >= PlaneNo && no <= HoleNo; }

    public static string Name(int no)
    {
        return no == PlaneNo ? "Cut plane" : no == BoxNo ? "Cut box" : no == TearNo ? "Cut tear" : "Cut hole";
    }

    // half width of a tear (pointed lens) or hole (ellipse) at t = position along it / half length (-1..1);
    // negative beyond the ends, so the opening closes there
    public static float Profile(bool tear, float t)
    {
        float q = 1f - t * t;
        return tear ? q : Mathf.Sign(q) * Mathf.Sqrt(Mathf.Abs(q));
    }

    static Material material;

    static Material Mat()
    {
        if (material != null) return material;
        foreach (string n in new[] { "Sprites/Default", "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended", "Unlit/Transparent" })
        {
            Shader sh = C2IBuild.FindShader(n);
            if (sh != null && sh.isSupported) { material = new Material(sh); break; }
        }
        if (material == null) material = new Material(Shader.Find("Standard"));
        material.name = "Cut marker";
        return material;
    }

    class MB
    {
        public List<Vector3> v = new List<Vector3>();
        public List<Color32> c = new List<Color32>();
        public List<int> t = new List<int>();

        public void Quad(Vector3 a, Vector3 b, Vector3 cc, Vector3 d, Color32 col)
        {
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(cc); v.Add(d);
            for (int k = 0; k < 4; k++) c.Add(col);
            t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
        }

        // a thin square prism from p to q (an edge or an arrow shaft)
        public void Bar(Vector3 p, Vector3 q, float w, Color32 col)
        {
            Vector3 d = (q - p).normalized;
            Vector3 a = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized * w;
            Vector3 b = Vector3.Cross(d, a).normalized * w;
            Vector3[] r = { a + b, a - b, -a - b, -a + b };
            for (int k = 0; k < 4; k++)
            {
                Vector3 r0 = r[k], r1 = r[(k + 1) % 4];
                Quad(p + r0, q + r0, q + r1, p + r1, col);
            }
        }

        public void Pyramid(Vector3 baseC, Vector3 apex, float w, Color32 col)
        {
            Vector3 d = (apex - baseC).normalized;
            Vector3 a = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized * w;
            Vector3 b = Vector3.Cross(d, a).normalized * w;
            Vector3[] r = { a + b, a - b, -a - b, -a + b };
            for (int k = 0; k < 4; k++)
            {
                int i = v.Count;
                v.Add(baseC + r[k]); v.Add(apex); v.Add(baseC + r[(k + 1) % 4]);
                c.Add(col); c.Add(col); c.Add(col);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
            }
        }

        public Mesh Mesh(string name)
        {
            Mesh m = new Mesh();
            m.name = name;
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }

    // plane: the local XZ plane, "front" is +Y (the arrow); box: a cube of BoxSize, sized with the item scale;
    // tear / hole: the opening's outline in the local XZ plane (length along X), with the cut depth along Y
    public static GameObject Prototype(int no, Transform container)
    {
        MB mb = new MB();
        if (no == TearNo || no == HoleNo)
        {
            bool tear = no == TearNo;
            float hl = (tear ? TearLength : HoleSize) / 2f, hw = (tear ? TearWidth : HoleSize) / 2f, hd = OpeningDepth / 2f;
            Color32 face = new Color32(255, 80, 200, 60), edge = new Color32(255, 90, 210, 235), depth = new Color32(255, 230, 120, 235);
            const int N = 24;
            List<Vector3> rim = new List<Vector3>();
            for (int i = 0; i <= N; i++) { float t = -1f + 2f * i / N; rim.Add(new Vector3(t * hl, 0f, hw * Mathf.Max(0f, Profile(tear, t)))); }
            for (int i = N - 1; i > 0; i--) { float t = -1f + 2f * i / N; rim.Add(new Vector3(t * hl, 0f, -hw * Mathf.Max(0f, Profile(tear, t)))); }
            int c0 = mb.v.Count;
            mb.v.Add(Vector3.zero); mb.c.Add(face);
            foreach (Vector3 r in rim) { mb.v.Add(r); mb.c.Add(face); }
            for (int i = 0; i < rim.Count; i++) { mb.t.Add(c0); mb.t.Add(c0 + 1 + i); mb.t.Add(c0 + 1 + (i + 1) % rim.Count); }
            for (int i = 0; i < rim.Count; i++) mb.Bar(rim[i], rim[(i + 1) % rim.Count], 0.012f, edge);
            mb.Bar(new Vector3(0, -hd, 0), new Vector3(0, hd, 0), 0.012f, depth);
            mb.Bar(new Vector3(-0.05f, hd, 0), new Vector3(0.05f, hd, 0), 0.01f, depth);
            mb.Bar(new Vector3(-0.05f, -hd, 0), new Vector3(0.05f, -hd, 0), 0.01f, depth);
        }
        else if (no == PlaneNo)
        {
            float h = PlaneSize / 2f;
            Color32 face = new Color32(80, 200, 255, 50), edge = new Color32(80, 200, 255, 230), arrow = new Color32(255, 210, 60, 240);
            mb.Quad(new Vector3(-h, 0, -h), new Vector3(-h, 0, h), new Vector3(h, 0, h), new Vector3(h, 0, -h), face);
            Vector3[] cs = { new Vector3(-h, 0, -h), new Vector3(-h, 0, h), new Vector3(h, 0, h), new Vector3(h, 0, -h) };
            for (int k = 0; k < 4; k++) mb.Bar(cs[k], cs[(k + 1) % 4], 0.04f, edge);
            mb.Bar(new Vector3(0, 0, 0), new Vector3(0, 2.2f, 0), 0.06f, arrow);
            mb.Pyramid(new Vector3(0, 2.2f, 0), new Vector3(0, 3f, 0), 0.3f, arrow);
        }
        else
        {
            float h = BoxSize / 2f;
            Color32 face = new Color32(255, 140, 60, 45), edge = new Color32(255, 160, 60, 230);
            Vector3[] p = new Vector3[8];
            for (int k = 0; k < 8; k++) p[k] = new Vector3((k & 1) != 0 ? h : -h, (k & 2) != 0 ? h : -h, (k & 4) != 0 ? h : -h);
            int[][] faces = { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } };
            foreach (int[] f in faces) mb.Quad(p[f[0]], p[f[1]], p[f[2]], p[f[3]], face);
            int[][] edges = { new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 }, new[] { 0, 2 }, new[] { 1, 3 }, new[] { 4, 6 }, new[] { 5, 7 }, new[] { 0, 4 }, new[] { 1, 5 }, new[] { 2, 6 }, new[] { 3, 7 } };
            foreach (int[] e in edges) mb.Bar(p[e[0]], p[e[1]], 0.03f, edge);
        }
        GameObject root = new GameObject(Name(no));
        root.transform.SetParent(container, false);
        GameObject g = new GameObject("marker");
        g.transform.SetParent(root.transform, false);
        g.AddComponent<MeshFilter>().sharedMesh = mb.Mesh(root.name);
        MeshRenderer mr = g.AddComponent<MeshRenderer>();
        mr.sharedMaterial = Mat();
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return root;
    }
}

// ==================================================================================================== the cutting surface

public class C2IShape
{
    public int kind;                // 0 plane, 1 box, 2 tear, 3 hole
    public bool box { get { return kind == 1; } }
    public bool opening { get { return kind >= 2; } }   // tear / hole: one piece with an opening
    public Vector3 c, ax, ay, az;    // world centre and unit axes (plane normal = ay)
    public Vector3 half;            // half extents along ax, ay, az (world units): box; tear / hole: half length, depth, width
    public Quaternion rot;
    public float amp, tooth = 0.2f, randomness = 0.5f;   // ragged surface (world units)
    public int seed = 1;

    public static C2IShape FromMarker(OCIItem marker)
    {
        Transform t = marker.objectItem.transform;
        C2IShape s = new C2IShape();
        int no = marker.itemInfo.no;
        s.kind = no == C2ITools.BoxNo ? 1 : no == C2ITools.TearNo ? 2 : no == C2ITools.HoleNo ? 3 : 0;
        s.c = t.position;
        s.rot = t.rotation;
        s.ax = t.right; s.ay = t.up; s.az = t.forward;
        Vector3 ls = t.lossyScale;
        Vector3 size = s.kind == 2 ? new Vector3(C2ITools.TearLength, C2ITools.OpeningDepth, C2ITools.TearWidth)
            : s.kind == 3 ? new Vector3(C2ITools.HoleSize, C2ITools.OpeningDepth, C2ITools.HoleSize)
            : Vector3.one * C2ITools.BoxSize;
        s.half = Vector3.Scale(new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)), size) / 2f;
        return s;
    }

    public int Faces { get { return kind == 0 ? 1 : kind == 1 ? 6 : kind == 2 ? 4 : 3; } }

    // Inside the opening when every face is <= 0.
    // Tear: the two rims (k 0, 1) and the depth limits (k 2, 3); ragged rims taper to nothing at the tips so it stays pointed.
    // Hole: the rim (k 0, an ellipse; its teeth run round it by angle) and the depth limits (k 1, 2).
    float OpeningFace(int k, Vector3 p, bool ragged)
    {
        Vector3 d = p - c;
        float u = Vector3.Dot(ax, d), w = Vector3.Dot(ay, d), v = Vector3.Dot(az, d);
        if (kind == 3)
        {
            if (k == 1) return w - half.y;
            if (k == 2) return -w - half.y;
            float nu = u / Mathf.Max(1e-4f, half.x), nv = v / Mathf.Max(1e-4f, half.z);
            float fh = (Mathf.Sqrt(nu * nu + nv * nv) - 1f) * Mathf.Min(half.x, half.z);
            if (ragged && amp > 0f) fh += Rag(Mathf.Atan2(nv, nu) * (half.x + half.z) * 0.5f, w, 0);
            return fh;
        }
        if (k == 2) return w - half.y;
        if (k == 3) return -w - half.y;
        float prof = C2ITools.Profile(kind == 2, u / Mathf.Max(1e-4f, half.x));
        float f = (k == 0 ? v : -v) - half.z * prof;
        if (ragged && amp > 0f) f += Rag(u, w, k) * Mathf.Clamp01(prof * 3f);
        return f;
    }

    void FaceAxes(int k, out Vector3 n, out float h, out Vector3 u, out Vector3 v)
    {
        if (!box) { n = ay; h = 0f; u = ax; v = az; return; }
        switch (k)
        {
            case 0: n = ax; h = half.x; u = ay; v = az; break;
            case 1: n = -ax; h = half.x; u = ay; v = az; break;
            case 2: n = ay; h = half.y; u = ax; v = az; break;
            case 3: n = -ay; h = half.y; u = ax; v = az; break;
            case 4: n = az; h = half.z; u = ax; v = ay; break;
            default: n = -az; h = half.z; u = ax; v = ay; break;
        }
    }

    // plane: > 0 in front (the arrow side). box face k: > 0 outside that face.
    public float Face(int k, Vector3 p, bool ragged)
    {
        if (opening) return OpeningFace(k, p, ragged);
        Vector3 n, u, v; float h;
        FaceAxes(k, out n, out h, out u, out v);
        Vector3 d = p - c;
        float f = Vector3.Dot(n, d) - h;
        if (ragged && amp > 0f) f += Rag(Vector3.Dot(u, d), Vector3.Dot(v, d), k);
        return f;
    }

    // distance-like value without the ragged part: plane signed distance, box: inside < 0
    public float Base(Vector3 p)
    {
        if (kind == 0) return Face(0, p, false);
        float m = float.MinValue;
        for (int k = 0; k < Faces; k++) m = Mathf.Max(m, Face(k, p, false));
        return m;
    }

    static float Hash(int x, int y, int s)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263 + s * 1442695041;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    static float VNoise(float x, float y, int s)
    {
        int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy);
        float a = Hash(xi, yi, s), b = Hash(xi + 1, yi, s), c0 = Hash(xi, yi + 1, s), d = Hash(xi + 1, yi + 1, s);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c0, d, fx), fy);
    }

    static float Tri(float x)
    {
        float f = x - Mathf.Floor(x);
        return 1f - 4f * Mathf.Abs(f - 0.5f);   // -1..1, sharp peaks
    }

    // zigzag teeth in both directions of the surface, heights and phase disturbed by noise (randomness 0..1)
    public float Rag(float u, float v, int face)
    {
        float s = Mathf.Max(1e-3f, tooth);
        int sd = seed * 131 + face * 7919;
        float x = u / s, y = v / s;
        float wob = (VNoise(x * 0.31f, y * 0.31f, sd + 1) - 0.5f) * 1.4f * randomness;
        float zig = 0.5f * (Tri(x + wob) + Tri(y - wob * 0.7f));
        float hv = Mathf.Lerp(1f, 0.3f + 1.4f * Hash(Mathf.FloorToInt(x + wob), Mathf.FloorToInt(y - wob), sd + 2), randomness);
        float fine = VNoise(x * 2.3f, y * 2.3f, sd + 3) * 2f - 1f;
        return amp * (zig * hv * (1f - 0.4f * randomness) + fine * 0.4f * randomness);
    }
}

// ==================================================================================================== working mesh

public class C2IWMesh
{
    public string name;
    public List<Vector3> v = new List<Vector3>(), p = new List<Vector3>(), n = new List<Vector3>();
    public List<Vector4> t = new List<Vector4>();
    public List<Vector4>[] uv = new List<Vector4>[4];
    public List<Color32> col = new List<Color32>();
    public List<BoneWeight> bw = new List<BoneWeight>();
    public bool hasN, hasT, hasCol, hasBW;
    public Matrix4x4[] bp;
    public List<int>[] tris;      // per sub-mesh; null for sub-meshes that are not triangles
    public int[] topo;
    public int[][] other;          // non-triangle sub-meshes (kept whole)

    public int Count { get { return v.Count; } }

    public static C2IWMesh From(C2IMesh m)
    {
        C2IWMesh w = new C2IWMesh();
        w.name = m.name;
        int vc = m.v.Length;
        w.v.AddRange(m.v);
        w.hasN = m.n != null && m.n.Length == vc; if (w.hasN) w.n.AddRange(m.n);
        w.hasT = m.t != null && m.t.Length == vc; if (w.hasT) w.t.AddRange(m.t);
        w.hasCol = m.col != null && m.col.Length == vc; if (w.hasCol) w.col.AddRange(m.col);
        w.hasBW = m.bw != null && m.bw.Length == vc; if (w.hasBW) w.bw.AddRange(m.bw);
        for (int ch = 0; ch < 4; ch++) if (m.uv[ch] != null && m.uv[ch].Count == vc) w.uv[ch] = new List<Vector4>(m.uv[ch]);
        w.bp = m.bp;
        int sc = m.idx.Length;
        w.tris = new List<int>[sc]; w.topo = (int[])m.topo.Clone(); w.other = new int[sc][];
        for (int s = 0; s < sc; s++)
        {
            if ((MeshTopology)m.topo[s] == MeshTopology.Triangles) w.tris[s] = new List<int>(m.idx[s]);
            else w.other[s] = m.idx[s];
        }
        return w;
    }

    public C2IWMesh Clone()
    {
        C2IWMesh w = (C2IWMesh)MemberwiseClone();
        w.v = new List<Vector3>(v); w.p = new List<Vector3>(p); w.n = new List<Vector3>(n); w.t = new List<Vector4>(t);
        w.uv = new List<Vector4>[4];
        for (int ch = 0; ch < 4; ch++) if (uv[ch] != null) w.uv[ch] = new List<Vector4>(uv[ch]);
        w.col = new List<Color32>(col); w.bw = new List<BoneWeight>(bw);
        w.tris = tris.Select(x => x != null ? new List<int>(x) : null).ToArray();
        return w;
    }

    public int TriangleCount { get { return tris.Where(x => x != null).Sum(x => x.Count / 3); } }

    // a new vertex between a and b (t from a to b)
    public int Lerp(int a, int b, float t)
    {
        int i = v.Count;
        v.Add(Vector3.Lerp(v[a], v[b], t));
        p.Add(Vector3.Lerp(p[a], p[b], t));
        if (hasN) n.Add(Vector3.Lerp(n[a], n[b], t).normalized);
        if (hasT)
        {
            Vector3 x = Vector3.Lerp((Vector3)t0(a), (Vector3)t0(b), t).normalized;
            this.t.Add(new Vector4(x.x, x.y, x.z, this.t[a].w));
        }
        for (int ch = 0; ch < 4; ch++) if (uv[ch] != null) uv[ch].Add(Vector4.Lerp(uv[ch][a], uv[ch][b], t));
        if (hasCol) col.Add(Color32.Lerp(col[a], col[b], t));
        if (hasBW) bw.Add(Mix(bw[a], bw[b], t));
        return i;
    }

    Vector4 t0(int i) { return t[i]; }

    static BoneWeight Mix(BoneWeight a, BoneWeight b, float t)
    {
        Dictionary<int, float> d = new Dictionary<int, float>();
        Action<int, float> add = (i, w) => { if (w <= 0f) return; float o; d.TryGetValue(i, out o); d[i] = o + w; };
        float s = 1f - t;
        add(a.boneIndex0, a.weight0 * s); add(a.boneIndex1, a.weight1 * s); add(a.boneIndex2, a.weight2 * s); add(a.boneIndex3, a.weight3 * s);
        add(b.boneIndex0, b.weight0 * t); add(b.boneIndex1, b.weight1 * t); add(b.boneIndex2, b.weight2 * t); add(b.boneIndex3, b.weight3 * t);
        List<KeyValuePair<int, float>> top = d.OrderByDescending(x => x.Value).Take(4).ToList();
        float sum = top.Sum(x => x.Value);
        if (sum <= 0f) return a;
        BoneWeight r = new BoneWeight();
        for (int k = 0; k < top.Count; k++)
        {
            float w = top[k].Value / sum;
            switch (k)
            {
                case 0: r.boneIndex0 = top[k].Key; r.weight0 = w; break;
                case 1: r.boneIndex1 = top[k].Key; r.weight1 = w; break;
                case 2: r.boneIndex2 = top[k].Key; r.weight2 = w; break;
                default: r.boneIndex3 = top[k].Key; r.weight3 = w; break;
            }
        }
        return r;
    }

    // drops unused vertices
    public C2IMesh ToMesh()
    {
        int[] map = Enumerable.Repeat(-1, v.Count).ToArray();
        List<int> order = new List<int>();
        Action<int> use = i => { if (map[i] < 0) { map[i] = order.Count; order.Add(i); } };
        foreach (List<int> l in tris) if (l != null) foreach (int i in l) use(i);
        foreach (int[] o in other) if (o != null) foreach (int i in o) use(i);
        C2IMesh m = new C2IMesh();
        m.name = name;
        m.v = order.Select(i => v[i]).ToArray();
        if (hasN) m.n = order.Select(i => n[i]).ToArray();
        if (hasT) m.t = order.Select(i => t[i]).ToArray();
        for (int ch = 0; ch < 4; ch++) if (uv[ch] != null) m.uv[ch] = order.Select(i => uv[ch][i]).ToList();
        if (hasCol) m.col = order.Select(i => col[i]).ToArray();
        if (hasBW) m.bw = order.Select(i => bw[i]).ToArray();
        m.bp = bp;
        int sc = tris.Length;
        m.topo = topo; m.idx = new int[sc][];
        for (int s = 0; s < sc; s++)
            m.idx[s] = tris[s] != null ? tris[s].Select(i => map[i]).ToArray() : (other[s] ?? new int[0]).Select(i => map[i]).ToArray();
        Bounds bb = new Bounds(m.v.Length > 0 ? m.v[0] : Vector3.zero, Vector3.zero);
        foreach (Vector3 x in m.v) bb.Encapsulate(x);
        m.bounds = bb;
        return m;
    }
}

// ==================================================================================================== geometry

public static class C2ICut
{
    // where every vertex is now, in world space (linear blend skinning, as Unity does it)
    public static void Pose(C2IWMesh w, Renderer live)
    {
        w.p.Clear();
        SkinnedMeshRenderer smr = live as SkinnedMeshRenderer;
        if (smr != null && w.hasBW && w.bp != null && w.bp.Length > 0)
        {
            Transform[] bones = smr.bones;
            Matrix4x4[] m = new Matrix4x4[Math.Max(bones.Length, w.bp.Length)];
            for (int i = 0; i < m.Length; i++)
            {
                Matrix4x4 bm = i < bones.Length && bones[i] != null ? bones[i].localToWorldMatrix : live.transform.localToWorldMatrix;
                m[i] = bm * (i < w.bp.Length ? w.bp[i] : Matrix4x4.identity);
            }
            for (int k = 0; k < w.v.Count; k++)
            {
                BoneWeight b = w.bw[k];
                Vector3 v = w.v[k], r = Vector3.zero;
                if (b.weight0 > 0f && b.boneIndex0 < m.Length) r += m[b.boneIndex0].MultiplyPoint3x4(v) * b.weight0;
                if (b.weight1 > 0f && b.boneIndex1 < m.Length) r += m[b.boneIndex1].MultiplyPoint3x4(v) * b.weight1;
                if (b.weight2 > 0f && b.boneIndex2 < m.Length) r += m[b.boneIndex2].MultiplyPoint3x4(v) * b.weight2;
                if (b.weight3 > 0f && b.boneIndex3 < m.Length) r += m[b.boneIndex3].MultiplyPoint3x4(v) * b.weight3;
                w.p.Add(r);
            }
        }
        else
        {
            Matrix4x4 l = live.transform.localToWorldMatrix;
            foreach (Vector3 v in w.v) w.p.Add(l.MultiplyPoint3x4(v));
        }
    }

    static long Key(int a, int b) { return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a; }

    // Splits long edges near the cutting surface (shared midpoints, so no cracks), a few rounds.
    public static void Refine(C2IWMesh w, C2IShape s, float target, float band, int maxVerts)
    {
        for (int pass = 0; pass < 6 && w.Count < maxVerts; pass++)
        {
            Dictionary<long, int> mid = new Dictionary<long, int>();
            Dictionary<long, bool> decided = new Dictionary<long, bool>();
            float[] bv = w.p.Select(x => s.Base(x)).ToArray();
            Func<int, int, int> Mid = (a, b) =>
            {
                long k = Key(a, b);
                bool split;
                if (!decided.TryGetValue(k, out split))
                {
                    float len = Vector3.Distance(w.p[a], w.p[b]);
                    split = len > target && (Mathf.Min(Mathf.Abs(bv[a]), Mathf.Abs(bv[b])) <= band + len || bv[a] * bv[b] < 0f);
                    decided[k] = split;
                }
                if (!split) return -1;
                int m;
                if (!mid.TryGetValue(k, out m)) { m = w.Lerp(a, b, 0.5f); mid[k] = m; }
                return m;
            };
            bool any = false;
            for (int sm = 0; sm < w.tris.Length; sm++)
            {
                List<int> l = w.tris[sm];
                if (l == null) continue;
                List<int> o = new List<int>(l.Count);
                for (int i = 0; i + 2 < l.Count; i += 3)
                {
                    int a = l[i], b = l[i + 1], c = l[i + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    int n = (ab >= 0 ? 1 : 0) + (bc >= 0 ? 1 : 0) + (ca >= 0 ? 1 : 0);
                    if (n == 0) { o.Add(a); o.Add(b); o.Add(c); continue; }
                    any = true;
                    if (n == 3)
                    {
                        o.AddRange(new[] { a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca });
                        continue;
                    }
                    // rotate so the split edges come first: (a,b) always split
                    for (int r = 0; r < 3 && ab < 0; r++)
                    {
                        int ta = a; a = b; b = c; c = ta;
                        int tab = ab; ab = bc; bc = ca; ca = tab;
                    }
                    if (n == 1) { o.AddRange(new[] { a, ab, c, ab, b, c }); continue; }
                    if (bc >= 0) o.AddRange(new[] { a, ab, c, ab, bc, c, ab, b, bc });
                    else o.AddRange(new[] { a, ab, ca, ab, b, c, ab, c, ca });
                }
                w.tris[sm] = o;
            }
            if (!any) break;
        }
    }

    // Keeps the part where f(p) <= 0 (keepNeg) or > 0; returns whether anything was cut away or split.
    public static bool Clip(C2IWMesh w, Func<Vector3, float> f, bool keepNeg)
    {
        List<float> sv = new List<float>(w.Count);
        for (int i = 0; i < w.Count; i++) { float x = f(w.p[i]); sv.Add(keepNeg ? x : -x); }
        Dictionary<long, int> cross = new Dictionary<long, int>();
        bool changed = false;
        Func<int, int, int> Cross = (a, b) =>
        {
            long k = Key(a, b);
            int r;
            if (cross.TryGetValue(k, out r)) return r;
            float t = sv[a] / (sv[a] - sv[b]);
            r = w.Lerp(a, b, Mathf.Clamp01(t));
            sv.Add(0f);
            cross[k] = r;
            return r;
        };
        List<int> poly = new List<int>(4);
        for (int sm = 0; sm < w.tris.Length; sm++)
        {
            List<int> l = w.tris[sm];
            if (l == null) continue;
            List<int> o = new List<int>(l.Count);
            for (int i = 0; i + 2 < l.Count; i += 3)
            {
                int a = l[i], b = l[i + 1], c = l[i + 2];
                bool ia = sv[a] <= 0f, ib = sv[b] <= 0f, ic = sv[c] <= 0f;
                if (ia && ib && ic) { o.Add(a); o.Add(b); o.Add(c); continue; }
                changed = true;
                if (!ia && !ib && !ic) continue;
                poly.Clear();
                int[] tv = { a, b, c };
                for (int e = 0; e < 3; e++)
                {
                    int cur = tv[e], nxt = tv[(e + 1) % 3];
                    bool ic2 = sv[cur] <= 0f, in2 = sv[nxt] <= 0f;
                    if (ic2) poly.Add(cur);
                    if (ic2 != in2) poly.Add(Cross(cur, nxt));
                }
                for (int k = 1; k + 1 < poly.Count; k++) { o.Add(poly[0]); o.Add(poly[k]); o.Add(poly[k + 1]); }
            }
            w.tris[sm] = o;
        }
        return changed;
    }

    public static void Append(C2IWMesh into, C2IWMesh from)
    {
        int off = into.Count;
        into.v.AddRange(from.v); into.p.AddRange(from.p);
        if (into.hasN) into.n.AddRange(from.n);
        if (into.hasT) into.t.AddRange(from.t);
        for (int ch = 0; ch < 4; ch++) if (into.uv[ch] != null) into.uv[ch].AddRange(from.uv[ch]);
        if (into.hasCol) into.col.AddRange(from.col);
        if (into.hasBW) into.bw.AddRange(from.bw);
        for (int s = 0; s < into.tris.Length; s++) if (into.tris[s] != null) into.tris[s].AddRange(from.tris[s].Select(i => i + off));
    }
}

// ==================================================================================================== the tool

public class C2ICutter : MonoBehaviour
{
    public static C2ICutter Instance;

    // ---- settings (public: StudioBridge plugin.get/set)
    public int targetKey = -1;
    public int markerKey = -1;
    public int planeMode;            // 0 both sides, 1 front (arrow) side, 2 back side
    public int boxMode;              // 0 inside, 1 outside, 2 both
    public float sectionR = 0.35f, sectionG = 0.03f, sectionB = 0.03f;
    public bool shellsOwnInside = true;
    public float ragCm;              // 0: a clean cut
    public float toothCm = 2f;
    public float ragRandom = 0.5f;
    public int ragSeed = 1;
    public bool hideTarget = true;
    public bool clothesOnly;         // cut only clothes; body, hair and accessories go whole to one piece
    public string newName = "";      // name for the next cut's pieces (blank: target, side and time); cleared after use
    bool ownsFocus;
    public string status = "";
    public bool busy;
    public string lastItems = "";
    public float uiScale = 0f;

    bool showWindow;
    Rect windowRect = new Rect(-1f, 80f, 440f, 600f);
    Vector2 scroll;

    public static bool MouseOverWindow;

    void Awake() { Instance = this; }

    void OnDestroy()
    {
        RemoveToolbarButton();
        MouseOverWindow = false;
        if (Instance == this) Instance = null;
    }

    public void Show(bool on) { showWindow = on; }

    // ---------------------------------------------------------------- scene objects

    static Studio.Studio St { get { return Singleton<Studio.Studio>.Instance; } }

    static bool IsMarker(ObjectCtrlInfo o)
    {
        OCIItem i = o as OCIItem;
        return i != null && i.itemInfo.group == C2IStore.Group && i.itemInfo.category == C2ITools.Category;
    }

    static string NameOf(ObjectCtrlInfo o) { return o != null && o.treeNodeObject != null ? o.treeNodeObject.textName : "?"; }

    List<OCIItem> Markers() { return St.dicObjectCtrl.Values.Where(IsMarker).Cast<OCIItem>().OrderBy(x => x.objectInfo.dicKey).ToList(); }

    public string UseSelected()
    {
        foreach (ObjectCtrlInfo o in St.treeNodeCtrl.selectObjectCtrl)
        {
            if (IsMarker(o)) { markerKey = o.objectInfo.dicKey; continue; }
            if (o is OCIChar || o is OCIItem) targetKey = o.objectInfo.dicKey;
        }
        return "target " + targetKey + ", marker " + markerKey;
    }

    // a marker in front of the target (its renderers' centre), or at the camera's focus
    public string AddMarker(bool box) { return AddMarkerNo(box ? C2ITools.BoxNo : C2ITools.PlaneNo); }

    public string AddMarkerNo(int no)
    {
        if (!C2ITools.IsTool(no)) return "not a cut tool: " + no;
        OCIItem m = AddObjectItem.Add(C2IStore.Group, C2ITools.Category, no);
        if (m == null) return "could not add the marker";
        ObjectCtrlInfo t;
        Vector3 at = St.cameraCtrl.targetPos;
        if (St.dicObjectCtrl.TryGetValue(targetKey, out t))
        {
            Transform root = t is OCIChar ? ((OCIChar)t).charInfo.objRoot.transform : ((OCIItem)t).objectItem.transform;
            Renderer[] rs = root.GetComponentsInChildren<Renderer>().Where(r => r.enabled).ToArray();
            if (rs.Length > 0) { Bounds b = rs[0].bounds; foreach (Renderer r in rs) b.Encapsulate(r.bounds); at = b.center; }
        }
        Transform space = m.objectItem.transform.parent;
        m.objectInfo.changeAmount.pos = space != null ? space.InverseTransformPoint(at) : at;
        Singleton<UndoRedoManager>.Instance.Clear();
        markerKey = m.objectInfo.dicKey;
        try { St.treeNodeCtrl.SelectSingle(m.treeNodeObject); } catch (Exception) { }
        return "marker " + markerKey;
    }

    // ---------------------------------------------------------------- cutting

    public string Cut()
    {
        if (busy) return "busy";
        StartCoroutine(CutRoutine());
        return "started";
    }

    IEnumerator CutRoutine()
    {
        busy = true;
        IEnumerator e = CutSteps();
        while (true)
        {
            object cur;
            try { if (!e.MoveNext()) break; cur = e.Current; }
            catch (Exception ex)
            {
                CharaToItem.Log.LogError("cut: " + ex);
                status = "Failed: " + ex.Message;
                break;
            }
            yield return cur;
        }
        busy = false;
    }

    class Source
    {
        public C2IBake bake;
        public List<Renderer> live;
        public Matrix4x4 rootWorld;
        public string name;
    }

    IEnumerator CutSteps()
    {
        ObjectCtrlInfo target, mo;
        if (!St.dicObjectCtrl.TryGetValue(targetKey, out target) || IsMarker(target)) { status = "Choose the object to cut (select it, then \"Use selected\")."; yield break; }
        if (!St.dicObjectCtrl.TryGetValue(markerKey, out mo) || !IsMarker(mo)) { status = "Add or choose a cut plane, box, tear or hole."; yield break; }
        OCIItem marker = (OCIItem)mo;
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        status = "Capturing " + NameOf(target) + "...";
        // a hidden target has its renderers off: show it while it is captured
        bool wasVisible = target.treeNodeObject.visible;
        if (!wasVisible) { target.treeNodeObject.SetVisible(true); yield return null; yield return null; }
        IEnumerator steps = CutCaptured(target, marker, sw);
        try { while (steps.MoveNext()) yield return steps.Current; }
        finally { if (!wasVisible) target.treeNodeObject.SetVisible(false); }
    }

    IEnumerator CutCaptured(ObjectCtrlInfo target, OCIItem marker, System.Diagnostics.Stopwatch sw)
    {
        yield return null;

        // ---- the target as a bake + its live renderers
        Source src = null;
        OCIChar oc = target as OCIChar;
        OCIItem oi = target as OCIItem;
        if (oc != null)
        {
            ChaControl cha = oc.charInfo;
            bool blink = cha.GetEyesBlinkFlag();
            if (blink) cha.ChangeEyesBlinkFlag(false);
            yield return null; yield return null;
            C2ICapture.Ctx c;
            try
            {
                int fk = CharaToItem.Instance != null ? CharaToItem.Instance.fkGroups : 0x7FF;
                c = C2ICapture.Begin(oc, 0, cha.fileParam.fullname, fk, null, false);
            }
            finally { if (blink) cha.ChangeEyesBlinkFlag(true); }
            for (int i = 0; i < c.pendingTex.Count; i++) { status = "Storing textures " + (i + 1) + " / " + c.pendingTex.Count; yield return null; C2ICapture.StoreTexture(c, i); }
            src = new Source { bake = c.bake, live = c.liveRends, rootWorld = cha.objRoot.transform.localToWorldMatrix, name = cha.fileParam.fullname };
        }
        else if (oi != null && oi.itemInfo.group == C2IStore.Group && oi.itemInfo.category == C2IStore.Category)
        {
            if (CharaToItem.Header(oi.itemInfo.no) == null) { status = "This baked item's file was deleted, so it cannot be cut."; yield break; }
            src = CharaToItem.MatchesBake(oi) ? FromBaked(oi) : null;
            if (src == null)
            {
                status = "This item was made from an older bake under the same number (converted again or replaced). "
                    + "Delete it and add it again from the item list (\"Chara → Item\"), then cut.";
                yield break;
            }
        }
        else if (oi != null)
        {
            C2ICapture.Ctx c = C2ICapture.BeginItem(oi, 0, NameOf(oi));
            for (int i = 0; i < c.pendingTex.Count; i++) { status = "Storing textures " + (i + 1) + " / " + c.pendingTex.Count; yield return null; C2ICapture.StoreTexture(c, i); }
            src = new Source { bake = c.bake, live = c.liveRends, rootWorld = oi.objectItem.transform.localToWorldMatrix, name = NameOf(oi) };
        }
        else { status = "Only characters and items can be cut."; yield break; }
        if (src.bake.rends.Count == 0) { status = "Nothing visible to cut."; yield break; }
        long tCap = sw.ElapsedMilliseconds;

        C2IShape shape = C2IShape.FromMarker(marker);
        shape.amp = ragCm / 10f;
        shape.tooth = Mathf.Max(0.05f, toothCm / 10f);
        shape.randomness = ragRandom;
        shape.seed = ragSeed;

        // ---- pieces
        status = "Cutting...";
        yield return null;
        StringBuilder notes = new StringBuilder();
        IEnumerator cb = CutBake(src, shape, notes);
        while (cb.MoveNext()) yield return cb.Current;
        List<KeyValuePair<string, C2IBake>> pieces = cutResult;
        long tCut = sw.ElapsedMilliseconds - tCap;
        if (pieces.Count == 0) { status = "The cut leaves nothing (" + notes.ToString().Trim() + ")."; yield break; }
        // a given name: as it is for one piece, with the side added for two
        string custom = (newName ?? "").Trim();

        // a cut-apart piece pivots on the marker; an object with an opening keeps its own origin
        Vector3 pivotPos = shape.c;
        Quaternion pivotRot = shape.rot;
        if (shape.opening)
        {
            pivotPos = src.rootWorld.GetColumn(3);
            pivotRot = Quaternion.LookRotation(src.rootWorld.GetColumn(2), src.rootWorld.GetColumn(1));
        }
        Matrix4x4 pivot = Matrix4x4.TRS(pivotPos, pivotRot, Vector3.one);
        List<string> made = new List<string>();
        foreach (KeyValuePair<string, C2IBake> pc in pieces)
        {
            C2IBake b = pc.Value;
            int no = CharaToItem.AllocateNo();
            b.no = no;
            string baseName = custom.Length > 0 ? (pieces.Count > 1 ? custom + " " + pc.Key : custom) : src.name + " " + pc.Key;
            b.name = custom.Length > 0 ? baseName : baseName + " " + DateTime.Now.ToString("MM-dd HH:mm");
            b.charaName = src.name;
            b.parts = pc.Key;
            b.created = DateTime.Now.Ticks;
            b.sectionColor = new Vector4(sectionR, sectionG, sectionB, 1f);
            b.shellsOwnInside = shellsOwnInside;
            Repivot(b, src.rootWorld, pivot);
            string file = Path.Combine(C2IStore.Dir, no + "_" + C2IStore.SafeName(baseName) + ".c2i");
            Directory.CreateDirectory(C2IStore.Dir);
            C2IStore.Write(b, file);
            CharaToItem.AddBakeFile(file);
            status = "Adding " + b.name + "...";
            yield return null;
            OCIItem item = AddObjectItem.Add(C2IStore.Group, C2IStore.Category, no);
            if (item == null) { notes.AppendLine("could not add " + b.name); continue; }
            Transform space = item.objectItem.transform.parent;
            item.objectInfo.changeAmount.pos = space != null ? space.InverseTransformPoint(pivotPos) : pivotPos;
            item.objectInfo.changeAmount.rot = (space != null ? Quaternion.Inverse(space.rotation) * pivotRot : pivotRot).eulerAngles;
            item.objectInfo.changeAmount.scale = Vector3.one;
            made.Add(item.objectInfo.dicKey.ToString());
        }
        Singleton<UndoRedoManager>.Instance.Clear();
        if (made.Count > 0 && hideTarget) target.treeNodeObject.SetVisible(false);
        lastItems = string.Join(",", made.ToArray());
        if (made.Count > 0 && custom.Length > 0 && (newName ?? "").Trim() == custom) newName = "";
        if (notes.Length > 0) CharaToItem.Log.LogWarning("cut notes:\n" + notes);
        CharaToItem.Log.LogInfo("cut through " + cutNames.Count + " renderer(s): " + string.Join(", ", cutNames.ToArray()));
        status = "Done: " + made.Count + " piece(s) of " + src.name + " (" + (sw.ElapsedMilliseconds / 1000f).ToString("0.0") + " s)";
        CharaToItem.Log.LogInfo(status + " -> keys " + lastItems + " (capture " + tCap + " ms, cut " + tCut + " ms)");
    }

    // one of our items: its bake, with the nodes moved to where the live item has them now (FK)
    static Source FromBaked(OCIItem oi)
    {
        string file = CharaToItem.BakeFileOf(oi.itemInfo.no);
        if (file == null || !File.Exists(file)) return null;
        C2IBake b = C2IStore.Read(file, false);
        List<Transform> live = NodesOf(b, oi.objectItem.transform);
        if (live == null) return null;
        for (int i = 1; i < live.Count; i++)
        {
            b.nodes[i].lp = live[i].localPosition; b.nodes[i].lr = live[i].localRotation; b.nodes[i].ls = live[i].localScale;
            b.nodes[i].active = live[i].gameObject.activeSelf;
        }
        List<Renderer> rends = RendsOf(b, oi.objectItem.transform);
        if (rends == null) return null;
        return new Source { bake = b, live = rends, rootWorld = oi.objectItem.transform.localToWorldMatrix, name = b.charaName.Length > 0 ? b.charaName : b.name };
    }

    // the transforms of an item (or prototype) built from bake b, in the bake's node order (null: another node tree)
    static List<Transform> NodesOf(C2IBake b, Transform root)
    {
        List<Transform> live = new List<Transform>();
        Stack<Transform> st = new Stack<Transform>();
        st.Push(root);
        while (st.Count > 0)
        {
            Transform t = st.Pop();
            live.Add(t);
            for (int k = t.childCount - 1; k >= 0; k--)
            {
                Transform ch = t.GetChild(k);
                if (!ch.name.EndsWith("~inside")) st.Push(ch);
            }
        }
        if (live.Count != b.nodes.Count) { CharaToItem.Log.LogWarning("live item has " + live.Count + " nodes, bake " + b.nodes.Count); return null; }
        return live;
    }

    // the renderer of each bake.rends entry on an item (or prototype) built from b (null: they do not match)
    internal static List<Renderer> RendsOf(C2IBake b, Transform root)
    {
        List<Transform> live = NodesOf(b, root);
        if (live == null) return null;
        List<Renderer> rends = b.rends.Select(r => live[r.node].GetComponent<Renderer>()).ToList();
        return rends.Any(r => r == null) ? null : rends;
    }

    // the mesh of bake renderer i as a working mesh (null: not readable)
    internal static C2IWMesh Working(C2IBake b, int i, Renderer live)
    {
        C2IRend r = b.rends[i];
        C2IMesh m = b.meshes[r.mesh];
        if (m.srcIndex >= 0)
        {
            Mesh src = live is SkinnedMeshRenderer ? ((SkinnedMeshRenderer)live).sharedMesh : (live.GetComponent<MeshFilter>() != null ? live.GetComponent<MeshFilter>().sharedMesh : null);
            if (src == null || !src.isReadable) return null;
            m = C2ICapture.MeshData(src, live as SkinnedMeshRenderer);
        }
        C2IWMesh w = C2IWMesh.From(m);
        C2ICut.Pose(w, live);
        return w;
    }

    // The centre of mass of a bake's surface as posed now (world space): triangle centroids weighted by area, so a
    // finely meshed patch does not pull it over. Renderers whose mesh cannot be read count with their bounds centre
    // (weight: the bounds' surface); false when there is nothing to measure.
    internal static bool Centroid(C2IBake b, List<Renderer> live, out Vector3 centre)
    {
        Vector3 sum = Vector3.zero;
        double area = 0;
        for (int i = 0; i < b.rends.Count && i < live.Count; i++)
        {
            if (live[i] == null) continue;
            C2IWMesh w = Working(b, i, live[i]);
            if (w == null)
            {
                Bounds bb = live[i].bounds;
                Vector3 e = bb.size;
                float a = 2f * (e.x * e.y + e.y * e.z + e.z * e.x);
                sum += bb.center * a; area += a;
                continue;
            }
            foreach (List<int> t in w.tris)
            {
                if (t == null) continue;
                for (int k = 0; k + 2 < t.Count; k += 3)
                {
                    Vector3 p0 = w.p[t[k]], p1 = w.p[t[k + 1]], p2 = w.p[t[k + 2]];
                    float a = 0.5f * Vector3.Cross(p1 - p0, p2 - p0).magnitude;
                    sum += (p0 + p1 + p2) * (a / 3f); area += a;
                }
            }
        }
        centre = area > 1e-12 ? sum / (float)area : Vector3.zero;
        return area > 1e-12;
    }

    List<KeyValuePair<string, C2IBake>> cutResult;

    List<string> cutNames = new List<string>();

    IEnumerator CutBake(Source src, C2IShape s, StringBuilder notes)
    {
        cutNames.Clear();
        bool box = s.box;
        // the sides to make: plane: back (f <= 0) / front; box: inside / outside; tear / hole: only what is outside the opening
        bool wantA = s.opening ? false : box ? boxMode != 1 : planeMode != 1;
        bool wantB = s.opening ? true : box ? boxMode != 0 : planeMode != 2;
        string nameA = box ? "inside" : "back", nameB = s.kind == 2 ? "torn" : s.kind == 3 ? "holed" : box ? "outside" : "front";
        // clothes only: other renderers are not cut (when the target has clothes at all)
        bool onlyClothes = clothesOnly && src.bake.rends.Any(IsClothes);
        List<C2IRend> rendsA = new List<C2IRend>(), rendsB = new List<C2IRend>();
        List<C2IMesh> extraA = new List<C2IMesh>(), extraB = new List<C2IMesh>();
        int baseMeshes = src.bake.meshes.Count;
        float target = s.amp > 0f ? Mathf.Max(0.02f, s.tooth / 3f) : box ? Mathf.Max(0.05f, Mathf.Min(s.half.x, Mathf.Min(s.half.y, s.half.z))) : float.MaxValue;
        if (s.opening) target = Mathf.Max(0.01f, Mathf.Min(Mathf.Min(s.half.x, s.half.z) * 0.25f, target));   // the outline needs fine edges
        float band = s.amp * 1.5f;

        for (int i = 0; i < src.bake.rends.Count; i++)
        {
            C2IRend r = src.bake.rends[i];
            Renderer live = src.live[i];
            status = "Cutting " + (i + 1) + " / " + src.bake.rends.Count + (live != null ? " (" + live.name + ")" : "");
            if (onlyClothes && !IsClothes(r))
            {
                // not cut: an opening leaves it as it is; otherwise it goes whole to the side its centre is on
                bool toA = !s.opening && live != null && Inside(s, live.bounds.center);
                if (toA && wantA) rendsA.Add(Copy(r));
                else if (!toA && wantB) rendsB.Add(Copy(r));
                continue;
            }
            // whole on one side (by its bounds): nothing to cut
            int side = live != null ? Classify(s, live.bounds) : 0;
            if (side != 0)
            {
                if (side < 0 && wantA) rendsA.Add(Copy(r));
                if (side > 0 && wantB) rendsB.Add(Copy(r));
                continue;
            }
            C2IWMesh w = live != null ? Working(src.bake, i, live) : null;
            if (w == null)
            {
                // a mesh that cannot be read goes whole to the side its centre is on
                bool inA = !s.opening && (live == null || Inside(s, live.bounds.center));
                notes.AppendLine((live != null ? live.name : "?") + ": mesh not readable, kept whole on the " + (inA ? nameA : nameB) + " side");
                if (inA) rendsA.Add(Copy(r)); else rendsB.Add(Copy(r));
                continue;
            }
            if (target < float.MaxValue) C2ICut.Refine(w, s, target, band, 400000);
            int tri0 = w.TriangleCount;
            C2IWMesh a = null, bm = null;
            bool changedA = false, changedB = false;
            if (wantA)
            {
                a = w.Clone();
                if (s.kind != 0) for (int k = 0; k < s.Faces; k++) { int kk = k; changedA |= C2ICut.Clip(a, p => s.Face(kk, p, true), true); }
                else changedA = C2ICut.Clip(a, p => s.Face(0, p, true), true);
            }
            if (wantB)
            {
                if (s.kind == 0) { bm = w.Clone(); changedB = C2ICut.Clip(bm, p => s.Face(0, p, true), false); }
                else
                {
                    // outside the box / opening = outside face k while inside faces 0..k-1, for every face k
                    for (int k = 0; k < s.Faces; k++)
                    {
                        C2IWMesh part = w.Clone();
                        for (int j = 0; j < k; j++) { int jj = j; C2ICut.Clip(part, p => s.Face(jj, p, true), true); }
                        int kk = k;
                        C2ICut.Clip(part, p => s.Face(kk, p, true), false);
                        if (bm == null) bm = part; else C2ICut.Append(bm, part);
                    }
                    changedB = bm.TriangleCount != tri0;
                }
            }
            if (a != null) Emit(r, a, changedA, rendsA, extraA, baseMeshes);
            if (bm != null) Emit(r, bm, changedB, rendsB, extraB, baseMeshes);
            if ((changedA || changedB) && live != null) cutNames.Add(live.name + (r.part != null ? " [" + r.part + "]" : ""));
            yield return null;
        }

        List<KeyValuePair<string, C2IBake>> res = new List<KeyValuePair<string, C2IBake>>();
        if (wantA && rendsA.Count > 0) res.Add(new KeyValuePair<string, C2IBake>(nameA, Piece(src.bake, rendsA, extraA)));
        if (wantB && rendsB.Count > 0) res.Add(new KeyValuePair<string, C2IBake>(nameB, Piece(src.bake, rendsB, extraB)));
        cutResult = res;
    }

    static bool IsClothes(C2IRend r) { return r.part != null && r.part.StartsWith("clothes"); }

    // -1: the bounds lie wholly on the back / inside, +1: wholly in front / outside, 0: the cut may cross them
    static int Classify(C2IShape s, Bounds b)
    {
        b.Expand(s.amp * 2f + 0.02f);
        Vector3 mn = b.min, mx = b.max;
        Vector3[] cs = new Vector3[8];
        for (int k = 0; k < 8; k++) cs[k] = new Vector3((k & 1) != 0 ? mx.x : mn.x, (k & 2) != 0 ? mx.y : mn.y, (k & 4) != 0 ? mx.z : mn.z);
        if (s.kind == 0)
        {
            int neg = cs.Count(c => s.Face(0, c, false) <= 0f);
            return neg == 8 ? -1 : neg == 0 ? 1 : 0;
        }
        if (s.opening)
        {
            // the opening's own box (its rims bulge at most half.z + the ragged amplitude)
            Vector3 e = new Vector3(s.half.x, s.half.y, s.half.z) + Vector3.one * (s.amp * 2f + 0.02f);
            for (int k = 0; k < 3; k++)
            {
                Vector3 axis = k == 0 ? s.ax : k == 1 ? s.ay : s.az;
                float lo = cs.Min(c => Vector3.Dot(axis, c - s.c)), hi = cs.Max(c => Vector3.Dot(axis, c - s.c));
                if (lo > e[k] || hi < -e[k]) return 1;
            }
            return 0;
        }
        bool allIn = cs.All(c => Enumerable.Range(0, 6).All(k => s.Face(k, c, false) <= 0f));
        if (allIn) return -1;
        for (int k = 0; k < 6; k++) if (cs.All(c => s.Face(k, c, false) > 0f)) return 1;   // all beyond one face
        return 0;
    }

    static bool Inside(C2IShape s, Vector3 p)
    {
        if (s.kind == 0) return s.Face(0, p, true) <= 0f;
        for (int k = 0; k < s.Faces; k++) if (s.Face(k, p, true) > 0f) return false;
        return true;
    }

    static C2IRend Copy(C2IRend r)
    {
        return new C2IRend
        {
            node = r.node, skinned = r.skinned, mesh = r.mesh, mats = (int[])r.mats.Clone(), bones = (int[])r.bones.Clone(), rootBone = r.rootBone,
            localBounds = r.localBounds, quality = r.quality, shadow = r.shadow, sortingOrder = r.sortingOrder, offscreen = r.offscreen, receive = r.receive,
            anchor = r.anchor, lightProbe = r.lightProbe, reflProbe = r.reflProbe, renderingLayerMask = r.renderingLayerMask,
            blend = (float[])r.blend.Clone(), part = r.part, inside = r.inside
        };
    }

    // a piece's renderer: untouched (same mesh), gone (nothing left), or a cut copy with its inside drawn
    static void Emit(C2IRend r, C2IWMesh w, bool changed, List<C2IRend> into, List<C2IMesh> extra, int baseMeshes)
    {
        if (!changed) { into.Add(Copy(r)); return; }
        if (w.TriangleCount == 0) return;
        C2IRend c = Copy(r);
        c.mesh = baseMeshes + extra.Count;
        extra.Add(w.ToMesh());
        c.blend = new float[0];   // baked into the cut mesh
        c.inside = 2;
        into.Add(c);
    }

    static C2IBake Piece(C2IBake src, List<C2IRend> rends, List<C2IMesh> extra)
    {
        C2IBake b = new C2IBake();
        b.sex = src.sex;
        b.fkBones = new List<string>(src.fkBones);
        b.texHashes = new List<string>(src.texHashes);
        b.nodes = src.nodes.Select(n => new C2INode { name = n.name, parent = n.parent, lp = n.lp, lr = n.lr, ls = n.ls, active = n.active, layer = n.layer }).ToList();
        b.meshes = new List<C2IMesh>(src.meshes); b.meshes.AddRange(extra);
        b.textures = src.textures; b.mats = src.mats; b.sources = src.sources;
        b.rends = rends;
        b.exprEnable = src.exprEnable; b.expr = src.expr;
        b.fillInside = src.fillInside;
        C2IFK.Prune(b);   // only the FK of what is left in this piece
        C2IFK.GiveWeighted(b);   // a prop without FK: its own list, or GenericFK would show the whole bone chain
        return b;
    }

    // the item origin moves to the cut: a new root at `pivot`; the old root hangs under it where it was
    internal static void Repivot(C2IBake b, Matrix4x4 oldRootWorld, Matrix4x4 pivot, string rootName = "cut")
    {
        Matrix4x4 local = pivot.inverse * oldRootWorld;
        Vector3 pos = local.GetColumn(3);
        Vector3 sx = local.GetColumn(0), sy = local.GetColumn(1), sz = local.GetColumn(2);
        Quaternion rot = Quaternion.LookRotation(sz, sy);
        Vector3 scl = new Vector3(sx.magnitude, sy.magnitude, sz.magnitude);
        List<C2INode> nodes = new List<C2INode>();
        nodes.Add(new C2INode { name = rootName, parent = -1, lp = Vector3.zero, lr = Quaternion.identity, ls = Vector3.one, active = true, layer = b.nodes.Count > 0 ? b.nodes[0].layer : 0 });
        foreach (C2INode n in b.nodes) { n.parent = n.parent < 0 ? 0 : n.parent + 1; nodes.Add(n); }
        nodes[1].lp = pos; nodes[1].lr = rot; nodes[1].ls = scl;
        b.nodes = nodes;
        foreach (C2IRend r in b.rends)
        {
            r.node += 1;
            r.bones = r.bones.Select(x => x >= 0 ? x + 1 : x).ToArray();
            if (r.rootBone >= 0) r.rootBone += 1;
            if (r.anchor >= 0) r.anchor += 1;
        }
    }

    // ---------------------------------------------------------------- toolbar and window

    void Update()
    {
        if (!StudioAPI.StudioLoaded) { MouseOverWindow = false; return; }
        EnsureToolbarButton();
        if (toolbarToggle != null && !toolbarToggle.IsDisposed && toolbarToggle.Toggled.Value != showWindow) toolbarToggle.Toggled.OnNext(showWindow);
        Vector2 gm = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / UiScale();
        MouseOverWindow = showWindow && windowRect.Contains(gm);
        if (ownsFocus && (!showWindow || (Input.GetMouseButtonDown(0) && !MouseOverWindow))) { GUIUtility.keyboardControl = 0; ownsFocus = false; }
    }

    KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle toolbarToggle;
    bool toolbarFailed;
    Texture2D toolbarIcon;

    void EnsureToolbarButton()
    {
        if (toolbarToggle != null || toolbarFailed) return;
        try
        {
            toolbarIcon = MakeIcon();
            PropertyInfo pi = typeof(KKAPI.KoikatuAPI).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            BepInEx.BaseUnityPlugin owner = pi != null ? pi.GetValue(null, null) as BepInEx.BaseUnityPlugin : null;
            toolbarToggle = new KKAPI.Studio.UI.Toolbars.SimpleToolbarToggle("stp3.charatoitem.cutter", "Cutter", () => toolbarIcon, showWindow, owner, v => { showWindow = v; });
            KKAPI.Studio.UI.Toolbars.ToolbarManager.AddLeftToolbarControl(toolbarToggle);
        }
        catch (Exception ex) { toolbarFailed = true; toolbarToggle = null; CharaToItem.Log.LogWarning("cutter toolbar: " + ex.Message); }
    }

    void RemoveToolbarButton()
    {
        if (toolbarToggle != null)
        {
            try { KKAPI.Studio.UI.Toolbars.ToolbarManager.RemoveControl(toolbarToggle); } catch (Exception) { }
            toolbarToggle = null;
        }
        if (toolbarIcon != null) Destroy(toolbarIcon);
    }

    // scissors (y = 0 is the bottom row)
    static Texture2D MakeIcon()
    {
        Color32[] px = new Color32[32 * 32];
        for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);
        Color32 on = new Color32(255, 255, 255, 255);
        Action<float, float, float> dot = (x, y, r) =>
        {
            for (int yy = 0; yy < 32; yy++) for (int xx = 0; xx < 32; xx++)
                    if ((xx - x) * (xx - x) + (yy - y) * (yy - y) <= r * r) px[yy * 32 + xx] = on;
        };
        Action<float, float, float, float, float> line = (x0, y0, x1, y1, r) => { for (int k = 0; k <= 40; k++) dot(Mathf.Lerp(x0, x1, k / 40f), Mathf.Lerp(y0, y1, k / 40f), r); };
        line(9, 26, 23, 12, 1.6f);     // blades
        line(23, 26, 9, 12, 1.6f);
        for (int k = 0; k < 2; k++)    // rings
        {
            float cx = k == 0 ? 8f : 24f, cy = 8f;
            for (int a = 0; a < 48; a++) dot(cx + 4.5f * Mathf.Cos(a * Mathf.PI / 24f), cy + 4.5f * Mathf.Sin(a * Mathf.PI / 24f), 1.1f);
        }
        line(12, 11, 16, 17, 1.2f);
        line(20, 11, 16, 17, 1.2f);
        Texture2D t = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        t.SetPixels32(px); t.Apply(false);
        t.name = "CutterIcon";
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
        windowRect.width = 440f;
        windowRect.height = Mathf.Max(200f, Mathf.Min(sh - 40f, 800f));
        // first placement: just right of the Chara → Item window (which starts at sw - 3 * 440), left of the workspace list
        if (windowRect.x < 0f) windowRect.x = Mathf.Max(100f, sw - 3f * 440f + 430f);
        windowRect = GUI.Window(0x43325443, windowRect, DrawWindow, "Cutter");
        windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, sw - 60f));
        windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, sh - windowRect.height));
        GUI.matrix = old;
    }

    static readonly KeyValuePair<string, Color>[] Presets =
    {
        new KeyValuePair<string, Color>("Dark red", new Color(0.35f, 0.03f, 0.03f)),
        new KeyValuePair<string, Color>("Flesh", new Color(0.75f, 0.35f, 0.32f)),
        new KeyValuePair<string, Color>("Bone", new Color(0.85f, 0.82f, 0.72f)),
        new KeyValuePair<string, Color>("Black", new Color(0.03f, 0.03f, 0.03f)),
        new KeyValuePair<string, Color>("Grey", new Color(0.4f, 0.4f, 0.4f)),
    };

    float Slider(string label, float v, float min, float max, string fmt)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(110));
        v = GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(200));
        GUILayout.Label(v.ToString(fmt), GUILayout.Width(60));
        GUILayout.EndHorizontal();
        return v;
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
        // the name field gives up the keyboard on Enter / Escape (InputHotkeyBlock blocks all hotkeys while it has it)
        Event ev = Event.current;
        ownsFocus = GUIUtility.keyboardControl != 0 && (GUI.GetNameOfFocusedControl() ?? "") == "cut_newname";
        if (ownsFocus && ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter || ev.keyCode == KeyCode.Escape))
        {
            GUIUtility.keyboardControl = 0;
            ev.Use();
        }
        GUILayout.BeginHorizontal();
        GUILayout.Label("UI " + UiScale().ToString("0.00"), GUILayout.Width(60));
        if (GUILayout.Button("-")) uiScale = Mathf.Max(0.75f, UiScale() - 0.25f);
        if (GUILayout.Button("+")) uiScale = Mathf.Min(4f, UiScale() + 0.25f);
        if (GUILayout.Button("auto")) uiScale = 0f;
        GUILayout.EndHorizontal();
        scroll = GUILayout.BeginScrollView(scroll);

        ObjectCtrlInfo t;
        GUILayout.BeginHorizontal();
        GUILayout.Label("Target: " + (St.dicObjectCtrl.TryGetValue(targetKey, out t) && !IsMarker(t) ? NameOf(t) : "(none)"));
        if (GUILayout.Button("Use selected", GUILayout.Width(110))) status = UseSelected();
        GUILayout.EndHorizontal();

        GUILayout.Label("Cut with:");
        List<OCIItem> ms = Markers();
        foreach (OCIItem m in ms)
        {
            bool on = markerKey == m.objectInfo.dicKey;
            if (GUILayout.Toggle(on, " " + NameOf(m) + " #" + m.objectInfo.dicKey) && !on) markerKey = m.objectInfo.dicKey;
        }
        if (ms.Count == 0) GUILayout.Label("  (no cut plane or box in the scene)");
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Add cut plane")) status = AddMarker(false);
        if (GUILayout.Button("Add cut box")) status = AddMarker(true);
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Add tear")) status = AddMarkerNo(C2ITools.TearNo);
        if (GUILayout.Button("Add hole")) status = AddMarkerNo(C2ITools.HoleNo);
        GUILayout.EndHorizontal();
        GUILayout.Label("Tear / hole: lay it flat on the surface; the pink outline is the opening, the yellow bar how deep it cuts "
            + "(scale X = length, Z = width, Y = depth). The object stays in one piece.");
        GUILayout.Label("Place it with Studio's move / rotate / scale guides. Plane: the arrow points to the front side.");

        GUILayout.BeginHorizontal();
        GUILayout.Label("Plane keeps", GUILayout.Width(90));
        planeMode = GUILayout.Toolbar(planeMode, new[] { "Both sides", "Front", "Back" });
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        GUILayout.Label("Box keeps", GUILayout.Width(90));
        boxMode = GUILayout.Toolbar(boxMode, new[] { "Inside", "Outside", "Both" });
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Section colour", GUILayout.Width(110));
        Color prev = GUI.color;
        GUI.color = new Color(sectionR, sectionG, sectionB, 1f);
        GUILayout.Box(Texture2D.whiteTexture, GUILayout.Width(40), GUILayout.Height(18));
        GUI.color = prev;
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal();
        foreach (KeyValuePair<string, Color> p in Presets)
            if (GUILayout.Button(p.Key)) { sectionR = p.Value.r; sectionG = p.Value.g; sectionB = p.Value.b; }
        GUILayout.EndHorizontal();
        sectionR = Slider("  R", sectionR, 0f, 1f, "0.00");
        sectionG = Slider("  G", sectionG, 0f, 1f, "0.00");
        sectionB = Slider("  B", sectionB, 0f, 1f, "0.00");
        shellsOwnInside = GUILayout.Toggle(shellsOwnInside, " Clothes and hair show their own fabric inside");

        GUILayout.Space(4);
        ragCm = Slider("Ragged edge", ragCm, 0f, 5f, "0.0 'cm'");
        GUI.enabled = ragCm > 0f;
        toothCm = Slider("  Tooth size", toothCm, 0.3f, 10f, "0.0 'cm'");
        ragRandom = Slider("  Randomness", ragRandom, 0f, 1f, "0.00");
        GUILayout.BeginHorizontal();
        GUILayout.Label("  Pattern #" + ragSeed, GUILayout.Width(110));
        if (GUILayout.Button("New pattern", GUILayout.Width(110))) ragSeed = UnityEngine.Random.Range(1, 99999);
        GUILayout.EndHorizontal();
        GUI.enabled = true;

        clothesOnly = GUILayout.Toggle(clothesOnly, " Cut clothes only (body, hair and accessories stay whole)");
        hideTarget = GUILayout.Toggle(hideTarget, " Hide the target after cutting");
        GUILayout.BeginHorizontal();
        GUILayout.Label("Name", GUILayout.Width(45));
        GUI.SetNextControlName("cut_newname");
        newName = GUILayout.TextField(newName ?? "");
        GUILayout.EndHorizontal();
        GUILayout.Label(string.IsNullOrEmpty(newName) ? "  Blank: target, side and time." : "  Two pieces get the side (front / back ...) added.");
        GUI.enabled = !busy;
        if (GUILayout.Button(busy ? "Working..." : "Cut", GUILayout.Height(30))) { GUIUtility.keyboardControl = 0; UseSelectedIfNone(); Cut(); }
        GUI.enabled = true;
        if (!string.IsNullOrEmpty(status)) GUILayout.Label(status);
        GUILayout.Label("Pieces are baked items (\"Chara → Item\" / \"Baked objects\"); they keep the bones, so FK still bends them. Their origin is the marker (tear / hole: the object's own).");
        GUILayout.EndScrollView();
        GUI.DragWindow();
    }

    void UseSelectedIfNone()
    {
        ObjectCtrlInfo t;
        if (!St.dicObjectCtrl.TryGetValue(targetKey, out t) || IsMarker(t)) UseSelected();
    }
}
