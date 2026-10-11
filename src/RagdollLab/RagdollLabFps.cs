// #name RagdollLabFps
// #author stp3
// #desc Zombie FPS mode for RagdollLab: first-person camera, hitscan bullets that pass through bodies, effects.
// #proc_filter StudioNEOV2

// Compiled together with RagdollLab.cs by ScriptLoader (partial classes). C# 7.x / mcs only.
//
// Bullets are hitscan rays through the lab physics scene. Each body a bullet passes through takes a fixed
// fraction of the bullet's remaining momentum as an impulse (capped per body so light limbs do not explode),
// and the doll accumulates that impulse as "stagger" (decaying) and as damage. Stagger over the threshold
// knocks the doll down (it gets up again); HP at zero makes it limp until it revives.
// Room walls only stop bullets on their inner faces, so shooting from outside the room works.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AIChara;
using KKAPI.Studio;
using Studio;
using UnityEngine;
using UnityEngine.Rendering;

public partial class RagdollLab
{
    // ---------------------------------------------------------------- settings
    public float fireRate = 10f;           // rounds/s while holding the trigger
    public float bulletMomentum = 181f;    // mass * units/s carried by a bullet (game-scaled)
    public float bulletTransfer = 0.18f;   // fraction of the remaining momentum each body takes
    public float maxPartDv = 40f;          // units/s: cap on the velocity change of one body per hit
    public float bulletDamage = 1f;
    public float headMultiplier = 4f;
    public float limbMultiplier = 0.6f;
    public float maxHP = 1000f;            // applied on next Ragdoll
    public float staggerThreshold = 600f;  // accumulated absorbed momentum that knocks a doll down
    public float staggerDecay = 1.0f;      // 1/s
    public float hitWeaken = 20f;          // momentum that fully weakens the hit body (neighbours 1/2, rest 0.15)
    public float weakenRegain = 1.93f;      // weakness recovered per second
    public float weakenMuscle = 0.27f;      // share of muscle strength a fully weakened body loses (pins lose all)
    public float reviveTime = 6f;          // s after death until it gets up again (0 = stays dead)
    public float spreadDeg = 0.6f;
    public float recoilDeg = 0.5f;
    public float mouseSensitivity = 2.0f;
    public float moveSpeed = 25f;          // units/s (x2.5 with Shift)
    public float eyeHeight = 15f;          // units above the room floor on entering FPS
    public bool gore = true;
    public bool faceCameraOnGetUp = true;  // turn toward the camera (player) when getting up
    public bool fxThroughWalls = false;    // draw effects without depth test (if they turn out invisible)
    bool foldFps = true;

    public static bool FpsActive;
    static int fpsExitFrame = -10, fpsToggleFrame = -10;
    // true while in FPS mode and for the frame we leave it (so the Esc that exits does not reach Studio's exit dialog)
    public static bool SuppressKeys { get { return FpsActive || Time.frameCount <= fpsExitFrame + 1; } }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    bool escWasDown;

    // Esc read straight from Windows: works even when another plugin swallows Unity input (e.g. a focused text box).
    bool EscPressedRaw()
    {
        bool down = Application.isFocused && (GetAsyncKeyState(0x1B) & 0x8000) != 0;
        bool pressed = down && !escWasDown;
        escWasDown = down;
        return pressed;
    }

    public bool CheckFpsToggle()
    {
        if (fpsToggleFrame == Time.frameCount) return true;
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool toggle = ctrl && shift && Input.GetKeyDown(KeyCode.F) && StudioAPI.StudioLoaded;
        bool esc = FpsActive && (EscPressedRaw() || Input.GetKeyDown(KeyCode.Escape));
        if (!toggle && !esc) return false;
        fpsToggleFrame = Time.frameCount;
        if (esc || FpsActive) ExitFps(); else EnterFps();
        return true;
    }

    void OnApplicationFocus(bool focus)
    {
        if (!focus && FpsActive) { ExitFps(); Log.LogInfo("FPS mode left: game window lost focus"); }
    }

    // Leave FPS mode if some UI grabbed the keyboard: an IMGUI text field or a uGUI/TMP input field.
    static bool SomeTextFieldFocused()
    {
        if (GUIUtility.keyboardControl != 0) return true;
        UnityEngine.EventSystems.EventSystem es = UnityEngine.EventSystems.EventSystem.current;
        GameObject sel = es != null ? es.currentSelectedGameObject : null;
        if (sel == null) return false;
        foreach (Component c in sel.GetComponents<Component>())
        {
            if (c != null && c.GetType().Name.Contains("InputField")) return true;
        }
        return false;
    }
    Studio.CameraControl.CameraData savedCamera;
    float savedFov;
    float fireCooldown;
    bool aiming;
    float hitMarkerT;
    bool hitMarkerKill;
    string hudMessage = "";
    float hudMessageT;

    // ---------------------------------------------------------------- FPS camera

    static Studio.CameraControl StudioCameraCtrl()
    {
        Studio.Studio st = Studio.Studio.Instance;
        return st != null ? st.cameraCtrl : null;
    }

    void ToggleFps()
    {
        if (FpsActive) ExitFps(); else EnterFps();
    }

    void EnterFps()
    {
        Studio.CameraControl cc = StudioCameraCtrl();
        Camera cam = StudioCamera();
        if (cc == null || cam == null) { status = "No Studio camera."; return; }
        savedCamera = cc.Export();
        savedFov = savedCamera.parse;
        Studio.CameraControl.CameraData d = cc.Export();
        Vector3 eye = cam.transform.position;
        if (roomRoot != null) eye.y = roomRoot.transform.position.y + eyeHeight;
        d.pos = cc.transBase != null ? cc.transBase.InverseTransformPoint(eye) : eye;
        Vector3 e = cam.transform.rotation.eulerAngles;
        d.rotate = new Vector3(e.x, e.y, 0f);
        d.distance = Vector3.zero;
        cc.Import(d);
        FpsActive = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        GUIUtility.keyboardControl = 0;
        if (UnityEngine.EventSystems.EventSystem.current != null) UnityEngine.EventSystems.EventSystem.current.SetSelectedGameObject(null);
        escWasDown = true;   // ignore an Esc that is still held
        ShowHud("FPS mode  -  LMB fire, RMB aim, WASD/QE move, Shift run, G throw, Esc or Ctrl+Shift+F exit");
    }

    void ExitFps()
    {
        if (!FpsActive) return;
        FpsActive = false;
        fpsExitFrame = Time.frameCount;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Studio.CameraControl cc = StudioCameraCtrl();
        if (cc != null && savedCamera != null) cc.Import(savedCamera);
        savedCamera = null;
    }

    void ShowHud(string msg)
    {
        hudMessage = msg;
        hudMessageT = 4f;
    }

    void UpdateFps(float dtReal)
    {
        if (!FpsActive) return;
        Studio.CameraControl cc = StudioCameraCtrl();
        Camera cam = StudioCamera();
        if (cc == null || cam == null) { ExitFps(); return; }
        if (SomeTextFieldFocused()) { ExitFps(); Log.LogInfo("FPS mode left: a text field took keyboard focus"); return; }
        if (Cursor.lockState != CursorLockMode.Locked) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }

        Studio.CameraControl.CameraData d = cc.Export();
        aiming = Input.GetMouseButton(1);
        float sens = mouseSensitivity * (aiming ? 0.5f : 1f);
        d.rotate.y = (d.rotate.y + Input.GetAxis("Mouse X") * sens) % 360f;
        float pitch = d.rotate.x > 180f ? d.rotate.x - 360f : d.rotate.x;
        pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * sens, -85f, 85f);
        d.rotate.x = pitch;
        d.rotate.z = 0f;
        d.distance = Vector3.zero;

        Quaternion yaw = Quaternion.Euler(0f, d.rotate.y, 0f);
        Vector3 mv = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) mv += Vector3.forward;
        if (Input.GetKey(KeyCode.S)) mv -= Vector3.forward;
        if (Input.GetKey(KeyCode.D)) mv += Vector3.right;
        if (Input.GetKey(KeyCode.A)) mv -= Vector3.right;
        Vector3 world = yaw * mv.normalized;
        if (Input.GetKey(KeyCode.E)) world += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) world -= Vector3.up;
        float speed = moveSpeed * ((Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 2.5f : 1f);
        Vector3 delta = world * speed * dtReal;
        d.pos += cc.transBase != null ? cc.transBase.InverseTransformVector(delta) : delta;
        float targetFov = aiming ? savedFov * 0.5f : savedFov;
        d.parse = Mathf.Lerp(d.parse, targetFov, Mathf.Clamp01(dtReal * 12f));

        // trigger
        fireCooldown -= dtReal;
        if (Input.GetMouseButton(0) && fireCooldown <= 0f && sceneReady)
        {
            fireCooldown = 1f / Mathf.Max(0.5f, fireRate);
            float spread = spreadDeg * (aiming ? 0.25f : 1f);
            Vector2 r = UnityEngine.Random.insideUnitCircle * spread;
            Quaternion aim = Quaternion.Euler(d.rotate.x, d.rotate.y, 0f) * Quaternion.Euler(r.y, r.x, 0f);
            Vector3 eyeWorld = cam.transform.position;
            Vector3 muzzle = cam.transform.position + cam.transform.right * 0.8f - cam.transform.up * 0.7f + cam.transform.forward * 1.5f;
            FireBullet(eyeWorld, aim * Vector3.forward, muzzle);
            d.rotate.x = Mathf.Clamp(d.rotate.x - recoilDeg * (aiming ? 0.5f : 1f), -85f, 85f);
        }
        if (Input.GetKeyDown(KeyCode.G) && roomRoot != null)
        {
            ThrowProp(cam, new Ray(cam.transform.position, cam.transform.forward));
        }
        cc.Import(d);
    }

    // ---------------------------------------------------------------- bullets

    static readonly RaycastHit[] hitBuf = new RaycastHit[64];

    // ---------------------------------------------------------------- scripted shots (StudioBridge, Timeline events)

    // Ragdoll one character without touching the Studio selection. False if it already is one or the build failed.
    public bool BuildFor(OCIChar oci)
    {
        ChaControl cha = oci != null ? oci.charInfo : null;
        if (cha == null) return false;
        for (int i = 0; i < dolls.Count; i++) if (dolls[i].Owns(cha)) return false;
        Ragdoll doll = new Ragdoll(cha, oci);
        try
        {
            if (doll.Build(this))
            {
                dolls.Add(doll);
                if (roomRoot == null) BuildRoom(cha.transform.position, doll.LowestPoint() - FloorMargin);
                return true;
            }
        }
        catch (Exception e) { Log.LogError("Build failed: " + e); }
        doll.Release(false);
        return false;
    }

    // put the collision room's floor at a known height (e.g. the map floor measured by StudioBridge)
    public void SetRoomFloor(Vector3 center, float floorY)
    {
        EnsureScene();
        BuildRoom(center, floorY);
    }

    // ---- world geometry for other plugins (FpsLab): static mesh colliders in the lab scene that ragdolls lie on
    // and bullets stop at, e.g. the map's own meshes (Studio maps have no colliders). They share the room layer.
    readonly List<GameObject> worldMeshes = new List<GameObject>();
    public int WorldMeshCount { get { return worldMeshes.Count; } }

    public bool AddWorldMesh(Mesh mesh, Matrix4x4 localToWorld)
    {
        if (mesh == null || !mesh.isReadable) return false;
        EnsureScene();
        GameObject go = NewLabObject("RagdollLab_World_" + mesh.name);
        go.layer = RoomLayer;
        go.transform.position = localToWorld.MultiplyPoint3x4(Vector3.zero);
        go.transform.rotation = localToWorld.rotation;
        go.transform.localScale = localToWorld.lossyScale;
        try
        {
            MeshCollider mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.sharedMaterial = material;
        }
        catch (Exception e) { Log.LogWarning("world mesh " + mesh.name + ": " + e.Message); Destroy(go); return false; }
        worldMeshes.Add(go);
        return true;
    }

    // an axis-aligned box in the object's local frame (e.g. an item's renderer bounds when its mesh is not readable)
    public void AddWorldBox(Vector3 center, Quaternion rot, Vector3 size)
    {
        EnsureScene();
        GameObject go = NewLabObject("RagdollLab_WorldBox");
        go.layer = RoomLayer;
        go.transform.SetPositionAndRotation(center, rot);
        BoxCollider bc = go.AddComponent<BoxCollider>();
        bc.size = size;
        bc.sharedMaterial = material;
        worldMeshes.Add(go);
    }

    public void ClearWorldMeshes()
    {
        foreach (GameObject go in worldMeshes) if (go != null) Destroy(go);
        worldMeshes.Clear();
    }

    // kill one character's ragdoll outright (it stays dead: no revive); false when it is not a ragdoll
    public bool KillDoll(OCIChar oci)
    {
        Ragdoll d = DollOf(oci);
        if (d == null) return false;
        d.Kill("killed by script");
        return true;
    }

    public bool IsDoll(OCIChar oci) { return DollOf(oci) != null; }

    // "" when not a ragdoll, else up / DOWN / getting up / DEAD
    public string DollState(OCIChar oci)
    {
        Ragdoll d = DollOf(oci);
        return d != null ? d.StateName : "";
    }

    // release one character's ragdoll back to its animation (e.g. when an enemy is reset)
    public bool ReleaseDoll(OCIChar oci)
    {
        Ragdoll d = DollOf(oci);
        if (d == null) return false;
        if (active != null && active.doll == d) { DropGrab(active); active = null; }
        for (int i = pins.Count - 1; i >= 0; i--) if (pins[i].doll == d) { DropGrab(pins[i]); pins.RemoveAt(i); }
        d.Release(false);
        dolls.Remove(d);
        return true;
    }

    // ---- pushers: kinematic capsules that follow an animated character's bones (e.g. a kicking leg) and push
    // ragdolls in the lab's physics scene without being pushed back.
    class Pusher { public Transform a, b; public GameObject go; public Rigidbody rb; public CapsuleCollider col; public Vector3 pos; public Quaternion rot; }
    readonly List<Pusher> pushers = new List<Pusher>();

    public int AddPusher(OCIChar oci, string boneA, string boneB, float radius)
    {
        if (oci == null || oci.charInfo == null) return -1;
        Transform root = oci.charInfo.transform, ta = FindIn(root, boneA), tb = FindIn(root, boneB);
        if (ta == null || tb == null) return -1;
        Pusher p = new Pusher();
        p.a = ta; p.b = tb;
        p.go = NewLabObject("RagdollLab_Pusher_" + boneA);
        p.go.layer = PartLayer;
        p.rb = p.go.AddComponent<Rigidbody>();
        p.rb.isKinematic = true;
        p.rb.interpolation = RigidbodyInterpolation.None;
        p.col = p.go.AddComponent<CapsuleCollider>();
        p.col.direction = 2; p.col.radius = radius;
        p.col.sharedMaterial = material;
        pushers.Add(p);
        UpdatePushers();
        p.go.transform.SetPositionAndRotation(p.pos, p.rot);
        return pushers.Count;
    }

    public void ClearPushers()
    {
        foreach (Pusher p in pushers) if (p.go != null) Destroy(p.go);
        pushers.Clear();
    }

    static Transform FindIn(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++) { Transform r = FindIn(t.GetChild(i), name); if (r != null) return r; }
        return null;
    }

    void UpdatePushers()
    {
        for (int i = pushers.Count - 1; i >= 0; i--)
        {
            Pusher p = pushers[i];
            if (p.a == null || p.b == null || p.go == null) { if (p.go != null) Destroy(p.go); pushers.RemoveAt(i); continue; }
            Vector3 d = p.b.position - p.a.position;
            float len = Mathf.Max(0.01f, d.magnitude);
            p.pos = (p.a.position + p.b.position) * 0.5f;
            p.rot = Quaternion.LookRotation(d / len);
            p.col.height = len + 2f * p.col.radius;
        }
    }

    void MovePushers()
    {
        foreach (Pusher p in pushers) { p.rb.MovePosition(p.pos); p.rb.MoveRotation(p.rot); }
    }

    Ragdoll DollOf(OCIChar oci)
    {
        if (oci == null) return null;
        for (int i = 0; i < dolls.Count; i++) if (dolls[i].Owns(oci.charInfo)) return dolls[i];
        return null;
    }

    public int LockParts(OCIChar oci, string prefixes)
    {
        Ragdoll d = DollOf(oci);
        return d != null ? d.LockParts(prefixes.Split(',')) : -1;
    }

    // "key:px,py,pz,fx,fy,fz,ux,uy,uz;..." for every part of the character's ragdoll (bone forward/up, world)
    public string PartReport(OCIChar oci)
    {
        Ragdoll d = DollOf(oci);
        return d != null ? d.PartReport() : "";
    }

    public bool TieParts(OCIChar oci, string a, string b)
    {
        Ragdoll d = DollOf(oci);
        return d != null && d.TieParts(a, b);
    }

    public bool SettleFront(OCIChar oci, Vector3 dir, float seconds, float gain, float damp)
    {
        Ragdoll d = DollOf(oci);
        if (d == null) return false;
        d.SettleFront(dir, seconds, gain, damp);
        return true;
    }

    public bool TurnFront(OCIChar oci, Vector3 dir, float strength)
    {
        Ragdoll d = DollOf(oci);
        if (d == null) return false;
        d.TurnFront(dir, strength);
        return true;
    }

    public bool PushPart(OCIChar oci, string key, Vector3 impulse)
    {
        Ragdoll d = DollOf(oci);
        return d != null && d.PushPart(key, impulse);
    }

    // Give every ragdolled character back to its animation at once (e.g. when a Timeline loop restarts).
    public void ReleaseAllNow()
    {
        ClearPushers();
        ReleaseAll(false, true);
        DestroyRoom();
        ClearFx();
    }

    // One bullet from a muzzle point toward a target point, with FPS-mode hit handling and effects.
    // kill: the first ragdoll hit dies even if the damage alone would only stagger it. Returns ragdolls hit.
    public int ShootFrom(Vector3 muzzle, Vector3 target, bool kill)
    {
        Vector3 dir = target - muzzle;
        if (dir.sqrMagnitude < 1e-6f) return 0;
        dir.Normalize();
        List<Ragdoll> hit = FireBullet(muzzle, dir, muzzle);
        if (kill && hit.Count > 0) hit[0].Kill("scripted shot");
        return hit.Count;
    }

    List<Ragdoll> FireBullet(Vector3 origin, Vector3 dir, Vector3 muzzle)
    {
        List<Ragdoll> hitDolls = new List<Ragdoll>();
        int mask = (1 << PartLayer) | (1 << RoomLayer);
        int n = phys.Raycast(origin, dir, hitBuf, 3000f, mask, QueryTriggerInteraction.Ignore);
        // insertion sort by distance
        for (int i = 1; i < n; i++)
        {
            RaycastHit h = hitBuf[i]; int j = i - 1;
            while (j >= 0 && hitBuf[j].distance > h.distance) { hitBuf[j + 1] = hitBuf[j]; j--; }
            hitBuf[j + 1] = h;
        }
        Vector3 roomC = roomRoot != null ? roomRoot.transform.position + new Vector3(0, roomSize.y * 0.5f, 0) : origin;
        float P = bulletMomentum;
        Vector3 end = origin + dir * 600f;
        HashSet<Rigidbody> done = new HashSet<Rigidbody>();
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = hitBuf[i];
            if (h.collider == null) continue;
            if (h.collider.gameObject.layer == RoomLayer)
            {
                if (Vector3.Dot(h.normal, roomC - h.point) <= 0f) continue;   // outer face of a wall: pass
                end = h.point;
                SpawnDust(h.point, h.normal, dir);
                break;
            }
            Rigidbody rb = h.rigidbody;
            if (rb == null || !done.Add(rb)) continue;
            Ragdoll doll = FindOwner(rb);
            float J = P * (doll != null ? bulletTransfer : 0.5f);
            float applied = Mathf.Min(J, maxPartDv * rb.mass);
            rb.AddForceAtPosition(dir * applied, h.point, ForceMode.Impulse);
            if (doll != null)
            {
                bool killed;
                bool head;
                doll.OnBulletHit(rb, J, this, out head, out killed);
                if (!hitDolls.Contains(doll)) hitDolls.Add(doll);
                hitMarkerT = 0.15f;
                hitMarkerKill = killed || head;
                if (killed) ShowHud(doll.DisplayName + (head ? " - headshot kill" : " - kill"));
                if (gore) SpawnBlood(h.point, dir, J, head);
            }
            P -= J;
            if (P < bulletMomentum * 0.08f) { end = h.point + dir * 1.5f; break; }
        }
        AddTracer(muzzle, end);
        AddFlash(muzzle + dir * 0.3f);
        return hitDolls;
    }

    // ---------------------------------------------------------------- effects

    struct Particle
    {
        public Vector3 pos, vel;
        public float life, maxLife, size;
        public Color color;
        public bool blood;
    }
    struct Splat
    {
        public Vector3 pos;
        public float size, angle, life;
        public Color color;
        public Vector2 dir;     // horizontal travel direction at impact (unit) and how fast it was going
        public float speed;
        public int seed;
    }
    struct Streak
    {
        public Vector3 a, b;
        public float life, maxLife;
    }

    readonly List<Particle> particles = new List<Particle>();
    readonly List<Splat> splats = new List<Splat>();
    readonly List<Streak> tracers = new List<Streak>();
    readonly List<Streak> flashes = new List<Streak>();
    Material fxAlpha, fxAdd;

    static Color BloodColor()
    {
        float v = UnityEngine.Random.Range(0.7f, 1.1f);
        return new Color(0.32f * v, 0.02f, 0.03f, 0.95f);
    }

    void SpawnBlood(Vector3 p, Vector3 dir, float J, bool head)
    {
        int count = Mathf.Clamp((int)(J / 6f), 6, head ? 60 : 35);
        for (int i = 0; i < count && particles.Count < 3000; i++)
        {
            Particle q = new Particle();
            q.pos = p;
            bool exit = i % 3 != 0;   // most of the spray leaves along the bullet, some back toward the shooter
            Vector3 baseDir = exit ? dir : -dir;
            q.vel = (baseDir + UnityEngine.Random.insideUnitSphere * 0.6f).normalized * UnityEngine.Random.Range(8f, exit ? 45f : 20f);
            q.maxLife = q.life = UnityEngine.Random.Range(0.6f, 1.6f);
            q.size = UnityEngine.Random.Range(0.05f, 0.18f);
            q.color = BloodColor();
            q.blood = true;
            particles.Add(q);
        }
    }

    void SpawnDust(Vector3 p, Vector3 normal, Vector3 dir)
    {
        Vector3 refl = Vector3.Reflect(dir, normal);
        for (int i = 0; i < 12 && particles.Count < 3000; i++)
        {
            Particle q = new Particle();
            q.pos = p + normal * 0.05f;
            q.vel = (refl * 0.5f + normal + UnityEngine.Random.insideUnitSphere * 0.7f).normalized * UnityEngine.Random.Range(5f, 25f);
            q.maxLife = q.life = UnityEngine.Random.Range(0.3f, 0.7f);
            q.size = UnityEngine.Random.Range(0.06f, 0.2f);
            float g = UnityEngine.Random.Range(0.55f, 0.8f);
            q.color = i < 4 ? new Color(1f, 0.8f, 0.4f, 1f) : new Color(g, g, g * 0.95f, 0.8f);
            q.blood = false;
            particles.Add(q);
        }
    }

    void AddTracer(Vector3 a, Vector3 b)
    {
        Streak s = new Streak(); s.a = a; s.b = b; s.maxLife = s.life = 0.09f;
        tracers.Add(s);
    }

    void AddFlash(Vector3 p)
    {
        Streak s = new Streak(); s.a = p; s.b = p; s.maxLife = s.life = 0.05f;
        flashes.Add(s);
    }

    void ClearFx()
    {
        particles.Clear(); splats.Clear(); tracers.Clear(); flashes.Clear();
    }

    void UpdateFx(float dt)
    {
        float floorY = roomRoot != null ? roomRoot.transform.position.y : float.NegativeInfinity;
        Vector3 g = new Vector3(0f, -gravity, 0f);
        for (int i = particles.Count - 1; i >= 0; i--)
        {
            Particle q = particles[i];
            q.life -= dt;
            q.vel += g * dt;
            q.vel *= 1f - Mathf.Clamp01(dt * 1.5f);
            q.pos += q.vel * dt;
            if (q.blood && q.pos.y <= floorY + 0.02f)
            {
                if (splats.Count >= 400) splats.RemoveAt(0);
                Splat sp = new Splat();
                sp.pos = new Vector3(q.pos.x, floorY + 0.02f, q.pos.z);
                sp.size = q.size * UnityEngine.Random.Range(3f, 7f);
                sp.angle = UnityEngine.Random.Range(0f, 360f);
                sp.life = 25f;
                sp.color = q.color;
                Vector2 hv = new Vector2(q.vel.x, q.vel.z);
                sp.speed = hv.magnitude;
                sp.dir = sp.speed > 1e-3f ? hv / sp.speed : new Vector2(1f, 0f);
                sp.seed = UnityEngine.Random.Range(0, 1 << 20);
                splats.Add(sp);
                particles.RemoveAt(i);
                continue;
            }
            if (q.life <= 0f || q.pos.y < floorY - 1f) { particles.RemoveAt(i); continue; }
            particles[i] = q;
        }
        for (int i = splats.Count - 1; i >= 0; i--)
        {
            Splat sp = splats[i]; sp.life -= dt;
            if (sp.life <= 0f) splats.RemoveAt(i); else splats[i] = sp;
        }
        for (int i = tracers.Count - 1; i >= 0; i--)
        {
            Streak s = tracers[i]; s.life -= dt;
            if (s.life <= 0f) tracers.RemoveAt(i); else tracers[i] = s;
        }
        for (int i = flashes.Count - 1; i >= 0; i--)
        {
            Streak s = flashes[i]; s.life -= dt;
            if (s.life <= 0f) flashes.RemoveAt(i); else flashes[i] = s;
        }
        if (hitMarkerT > 0f) hitMarkerT -= Time.unscaledDeltaTime;
        if (hudMessageT > 0f) hudMessageT -= Time.unscaledDeltaTime;
    }

    Material MakeFxMaterial(bool additive)
    {
        Shader sh = Shader.Find("Hidden/Internal-Colored");
        if (sh == null) return null;
        Material m = new Material(sh);
        m.hideFlags = HideFlags.HideAndDontSave;
        m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        m.SetInt("_DstBlend", (int)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
        m.SetInt("_Cull", (int)CullMode.Off);
        m.SetInt("_ZWrite", 0);
        return m;
    }

    void DrawFx(Camera cam)
    {
        if (particles.Count == 0 && splats.Count == 0 && tracers.Count == 0 && flashes.Count == 0) return;
        if (fxAlpha == null) fxAlpha = MakeFxMaterial(false);
        if (fxAdd == null) fxAdd = MakeFxMaterial(true);
        if (fxAlpha == null || fxAdd == null) return;
        int zt = (int)(fxThroughWalls ? CompareFunction.Always : CompareFunction.LessEqual);
        fxAlpha.SetInt("_ZTest", zt);
        fxAdd.SetInt("_ZTest", zt);
        Vector3 right = cam.transform.right, up = cam.transform.up;

        fxAlpha.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.TRIANGLES);
        for (int i = 0; i < splats.Count; i++)
        {
            Splat sp = splats[i];
            Color c = sp.color; c.a *= Mathf.Clamp01(sp.life / 3f);
            GL.Color(c);
            DrawSplat(sp);
        }
        for (int i = 0; i < particles.Count; i++)
        {
            Particle q = particles[i];
            Color c = q.color; c.a *= Mathf.Clamp01(q.life / (q.maxLife * 0.5f));
            GL.Color(c);
            // a round droplet (billboard hexagon) with a tapering tail behind it along its velocity
            Vector3 tail = -q.vel * 0.012f;
            Vector3 prev = q.pos + right * q.size;
            for (int k = 1; k <= 6; k++)
            {
                float a = k * Mathf.PI / 3f;
                Vector3 cur = q.pos + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * q.size;
                GL.Vertex(q.pos); GL.Vertex(prev); GL.Vertex(cur);
                prev = cur;
            }
            if (tail.sqrMagnitude > q.size * q.size * 0.25f)
            {
                Vector3 side = Vector3.Cross(tail, cam.transform.forward).normalized * q.size * 0.8f;
                GL.Vertex(q.pos + side); GL.Vertex(q.pos - side); GL.Vertex(q.pos + tail);
            }
        }
        GL.End();
        GL.PopMatrix();

        fxAdd.SetPass(0);
        GL.PushMatrix();
        GL.Begin(GL.QUADS);
        for (int i = 0; i < tracers.Count; i++)
        {
            Streak s = tracers[i];
            float k = s.life / s.maxLife;
            Vector3 dir = (s.b - s.a);
            Vector3 side = Vector3.Cross(dir.normalized, cam.transform.forward).normalized;
            float w = 0.06f;
            GL.Color(new Color(1f, 0.85f, 0.45f, 0.9f * k));
            GL.Vertex(s.a - side * w); GL.Vertex(s.a + side * w); GL.Vertex(s.b + side * w * 0.3f); GL.Vertex(s.b - side * w * 0.3f);
            GL.Color(new Color(1f, 1f, 0.9f, k));
            GL.Vertex(s.a - side * w * 0.3f); GL.Vertex(s.a + side * w * 0.3f); GL.Vertex(s.b + side * w * 0.1f); GL.Vertex(s.b - side * w * 0.1f);
        }
        for (int i = 0; i < flashes.Count; i++)
        {
            Streak s = flashes[i];
            float k = s.life / s.maxLife;
            float sz = 0.5f + 0.3f * UnityEngine.Random.value;
            GL.Color(new Color(1f, 0.75f, 0.3f, k));
            for (int arm = 0; arm < 3; arm++)
            {
                float ang = arm * 60f + UnityEngine.Random.Range(0f, 30f);
                Vector3 a = Quaternion.AngleAxis(ang, cam.transform.forward) * right * sz;
                Vector3 b = Quaternion.AngleAxis(ang + 90f, cam.transform.forward) * right * sz * 0.15f;
                GL.Vertex(s.a - a - b); GL.Vertex(s.a - a + b); GL.Vertex(s.a + a + b); GL.Vertex(s.a + a - b);
            }
        }
        GL.End();
        GL.PopMatrix();
    }

    static float Hash(int seed, int i)
    {
        unchecked
        {
            uint h = (uint)(seed * 73856093) ^ (uint)(i * 19349663);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xffff) / 65535f;
        }
    }

    // A pool of blood on the floor: an irregular round blob stretched along the travel direction, plus a few
    // satellite droplets thrown further ahead.
    static void DrawSplat(Splat sp)
    {
        Vector3 d = new Vector3(sp.dir.x, 0f, sp.dir.y), sdir = new Vector3(-sp.dir.y, 0f, sp.dir.x);
        float stretch = 1f + Mathf.Clamp(sp.speed / 25f, 0f, 1.2f);
        Vector3 c = sp.pos;
        const int n = 14;
        Vector3 first = Vector3.zero, prev = Vector3.zero;
        for (int k = 0; k <= n; k++)
        {
            int kk = k % n;
            float a = kk * Mathf.PI * 2f / n;
            float r = sp.size * (0.7f + 0.45f * Hash(sp.seed, kk));
            float ca = Mathf.Cos(a), sa = Mathf.Sin(a);
            Vector3 p = c + d * ca * r * (ca > 0f ? stretch : 1f) + sdir * sa * r * 0.85f;
            if (k > 0) { GL.Vertex(c); GL.Vertex(prev); GL.Vertex(p); }
            prev = p;
        }
        int drops = 2 + (int)(Hash(sp.seed, 99) * 4f);
        for (int m = 0; m < drops; m++)
        {
            float along = sp.size * (1.3f + 1.8f * Hash(sp.seed, 200 + m)) * stretch;
            float off = (Hash(sp.seed, 300 + m) - 0.5f) * sp.size * 1.4f;
            float rr = sp.size * (0.08f + 0.17f * Hash(sp.seed, 400 + m));
            Vector3 dc = c + d * along + sdir * off;
            Vector3 pv = dc + d * rr;
            for (int k = 1; k <= 6; k++)
            {
                float a = k * Mathf.PI / 3f;
                Vector3 p = dc + (d * Mathf.Cos(a) + sdir * Mathf.Sin(a)) * rr;
                GL.Vertex(dc); GL.Vertex(pv); GL.Vertex(p);
                pv = p;
            }
        }
    }

    // ---------------------------------------------------------------- HUD / settings UI

    void DrawHud()
    {
        float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
        float sc = UiScale();
        float len = 10f * sc, gap = (aiming ? 3f : 6f) * sc, th = Mathf.Max(1f, 2f * sc);
        Color old = GUI.color;
        GUI.color = hitMarkerT > 0f ? (hitMarkerKill ? new Color(1f, 0.15f, 0.1f) : new Color(1f, 0.6f, 0.2f)) : new Color(1f, 1f, 1f, 0.85f);
        Texture2D t = Texture2D.whiteTexture;
        GUI.DrawTexture(new Rect(cx - gap - len, cy - th / 2, len, th), t);
        GUI.DrawTexture(new Rect(cx + gap, cy - th / 2, len, th), t);
        GUI.DrawTexture(new Rect(cx - th / 2, cy - gap - len, th, len), t);
        GUI.DrawTexture(new Rect(cx - th / 2, cy + gap, th, len), t);
        GUI.color = old;

        // HP / stagger bars at the top centre (the bottom-left corner belongs to Studio's coordinate UI)
        if (hudStyle == null || hudStyleScale != sc)
        {
            hudStyle = new GUIStyle(GUI.skin.label);
            hudStyle.fontSize = Mathf.RoundToInt(15f * sc);
            hudStyle.fontStyle = FontStyle.Bold;
            hudStyle.alignment = TextAnchor.MiddleLeft;
            hudStyleScale = sc;
        }
        float bw = 260f * sc, bh = 12f * sc, rowH = 40f * sc;
        float x0 = cx - bw * 0.5f, y = 16f * sc;
        for (int i = 0; i < dolls.Count; i++)
        {
            Ragdoll dl = dolls[i];
            float hp = dl.MaxHP > 0f ? Mathf.Clamp01(dl.HP / dl.MaxHP) : 0f;
            float st = Mathf.Clamp01(dl.Stagger / Mathf.Max(1f, staggerThreshold));
            ShadowLabel(new Rect(x0, y, bw * 2f, 18f * sc), dl.DisplayName + "   " + dl.StateName);
            float by = y + 18f * sc;
            Fill(new Rect(x0 - 2f, by - 2f, bw + 4f, bh + 4f), new Color(0f, 0f, 0f, 0.6f));
            Fill(new Rect(x0, by, bw * hp, bh), dl.Dead ? new Color(0.4f, 0.4f, 0.4f, 0.9f) : Color.Lerp(new Color(0.9f, 0.1f, 0.1f), new Color(0.2f, 0.85f, 0.25f), hp));
            Fill(new Rect(x0, by + bh + 1f, bw * st, Mathf.Max(2f, 3f * sc)), new Color(1f, 0.8f, 0.1f, 0.9f));
            Fill(new Rect(x0, by + bh + 1f + Mathf.Max(2f, 3f * sc), bw * dl.MaxWeak(), Mathf.Max(1f, 2f * sc)), new Color(0.3f, 0.7f, 1f, 0.9f));
            y += rowH;
        }
        if (hudMessageT > 0f) ShadowLabel(new Rect(x0, y + 4f * sc, 1200f * sc, 20f * sc), hudMessage);
    }

    GUIStyle hudStyle;
    float hudStyleScale;

    static void Fill(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    void ShadowLabel(Rect r, string text)
    {
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        GUI.Label(new Rect(r.x + 1.5f, r.y + 1.5f, r.width, r.height), text, hudStyle);
        GUI.color = Color.white;
        GUI.Label(r, text, hudStyle);
        GUI.color = old;
    }

    void DrawFpsSettings()
    {
        foldFps = GUILayout.Toggle(foldFps, "-- zombie FPS (Ctrl+Shift+F) --");
        if (!foldFps) return;
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Enter FPS (exit: Esc)")) EnterFps();
        if (GUILayout.Button("Zombie preset"))
        {
            muscleFreq = 20f; muscleDampingRatio = 1f; pinWeight = 1f; pinFreq = 12f; pinFalloff = 3f; pinHipsOnly = false;
            knockDistance = 5f; knockDv = 0f; gravityComp = true;
            hitWeaken = 20f; weakenRegain = 1.93f; weakenMuscle = 0.27f; staggerThreshold = 600f;
            bulletMomentum = 181f; bulletTransfer = 0.18f; maxPartDv = 40f; bulletDamage = 1f; maxHP = 1000f; staggerDecay = 1f;
        }
        if (GUILayout.Button("Revive all")) { for (int i = 0; i < dolls.Count; i++) dolls[i].Revive(this); }
        GUILayout.EndHorizontal();
        fireRate = Slider("Fire rate (/s)", fireRate, 1f, 20f, "0");
        bulletMomentum = Slider("Bullet momentum", bulletMomentum, 20f, 2000f, "0");
        bulletTransfer = Slider("Transfer/body", bulletTransfer, 0.05f, 1f, "0.00");
        maxPartDv = Slider("Max dv/body", maxPartDv, 5f, 200f, "0");
        bulletDamage = Slider("Damage", bulletDamage, 1f, 100f, "0");
        headMultiplier = Slider("Head x", headMultiplier, 1f, 10f, "0.0");
        maxHP = Slider("Max HP (next)", maxHP, 10f, 1000f, "0");
        staggerThreshold = Slider("Stagger thresh", staggerThreshold, 50f, 3000f, "0");
        staggerDecay = Slider("Stagger decay", staggerDecay, 0f, 5f, "0.00");
        hitWeaken = Slider("Hit weaken ref", hitWeaken, 20f, 2000f, "0");
        weakenRegain = Slider("Weak regain/s", weakenRegain, 0.1f, 5f, "0.00");
        weakenMuscle = Slider("Weak muscle", weakenMuscle, 0f, 1f, "0.00");
        reviveTime = Slider("Revive (s)", reviveTime, 0f, 30f, "0.0");
        spreadDeg = Slider("Spread (deg)", spreadDeg, 0f, 5f, "0.0");
        recoilDeg = Slider("Recoil (deg)", recoilDeg, 0f, 3f, "0.0");
        mouseSensitivity = Slider("Mouse sens", mouseSensitivity, 0.2f, 8f, "0.0");
        moveSpeed = Slider("Move speed", moveSpeed, 5f, 100f, "0");
        GUILayout.BeginHorizontal();
        gore = GUILayout.Toggle(gore, "Blood");
        faceCameraOnGetUp = GUILayout.Toggle(faceCameraOnGetUp, "Get up facing me");
        fxThroughWalls = GUILayout.Toggle(fxThroughWalls, "FX no depth test");
        GUILayout.EndHorizontal();
    }
}

public partial class Ragdoll
{
    public float HP = 100f;
    public float MaxHP = 100f;
    public float Stagger;
    float deadT;

    public string DisplayName
    {
        get
        {
            try
            {
                string n = cha != null && cha.fileParam != null ? cha.fileParam.fullname : null;
                return string.IsNullOrEmpty(n) ? Name : n;
            }
            catch (Exception) { return Name; }
        }
    }

    public bool Dead { get { return State == 3; } }

    public string HudLine()
    {
        if (Dead) return DisplayName + "  DEAD" + (deadT > 0f ? "" : "");
        int bars = Mathf.Clamp(Mathf.RoundToInt(HP / Mathf.Max(1f, MaxHP) * 20f), 0, 20);
        return DisplayName + "  [" + new string('|', bars) + new string('.', 20 - bars) + "]  " + StateName
            + "  stagger " + Mathf.RoundToInt(Stagger);
    }

    public void InitCombat(RagdollLab lab)
    {
        MaxHP = lab.maxHP; HP = MaxHP; Stagger = 0f; deadT = 0f;
    }

    // Called for every body a bullet passes through. J = momentum taken by this body.
    public void OnBulletHit(Rigidbody rb, float J, RagdollLab lab, out bool head, out bool killed)
    {
        head = false; killed = false;
        Part p;
        if (!byBody.TryGetValue(rb, out p)) return;
        head = p.key == "head";
        bool limb = p.key.StartsWith("upperarm") || p.key.StartsWith("forearm") || p.key.StartsWith("hand")
            || p.key.StartsWith("shin") || p.key.StartsWith("foot");
        float mult = head ? lab.headMultiplier : (limb ? lab.limbMultiplier : 1f);
        WakeUp();
        Weaken(p, J / Mathf.Max(1f, lab.hitWeaken));
        if (Dead) return;
        HP -= lab.bulletDamage * mult;
        Stagger += J * (head ? 1.5f : 1f);
        if (HP <= 0f)
        {
            HP = 0f;
            killed = true;
            Die(head ? "headshot" : "shot");
        }
        else if (Stagger >= lab.staggerThreshold && State == 0)
        {
            Stagger = 0f;
            KnockDown("stagger");
        }
    }

    // PuppetMaster-style hit reaction: the hit body loses pin/muscle strength, its neighbours half, the rest a little.
    void Weaken(Part hit, float amount)
    {
        foreach (Part q in parts)
        {
            float f = q == hit ? 1f : ((q == hit.parent || q.parent == hit) ? 0.5f : 0.15f);
            q.weak = Mathf.Min(1f, q.weak + amount * f);
        }
    }

    public float MaxWeak()
    {
        float m = 0f;
        foreach (Part q in parts) m = Mathf.Max(m, q.weak);
        return m;
    }

    // Hold the current pose of every part whose key starts with one of the prefixes (e.g. "upperarm", "forearm",
    // "hand": arms tied behind the back keep their shape, also after death). Returns how many joints were locked.
    public int LockParts(string[] prefixes)
    {
        int n = 0;
        foreach (Part q in parts)
        {
            if (q.joint == null) continue;
            bool hit = false;
            foreach (string pf in prefixes) if (q.key.StartsWith(pf)) hit = true;
            if (!hit) continue;
            q.hold = true;
            q.holdLocal = Quaternion.Inverse(q.parent.tr.rotation) * q.tr.rotation;   // the pose right now
            q.appliedW = -1f;
            n++;
        }
        return n;
    }

    // join two parts where they are now (e.g. wrists tied together): a fixed joint between their bodies
    public bool TieParts(string a, string b)
    {
        Part pa = null, pb = null;
        foreach (Part q in parts) { if (q.key == a) pa = q; if (q.key == b) pb = q; }
        if (pa == null || pb == null || pa.rb == null || pb.rb == null) return false;
        FixedJoint fj = pa.rb.gameObject.AddComponent<FixedJoint>();
        fj.connectedBody = pb.rb;
        fj.enableCollision = false;
        return true;
    }

    // torque impulse that turns the torso's front toward `dir` (e.g. up: settle on the back). strength = angular
    // velocity change (rad/s) at full misalignment.
    public void TurnFront(Vector3 dir, float strength)
    {
        WakeUp();
        foreach (Part q in parts)
        {
            if (q.rb == null || !(q.key == "chest" || q.key == "abdomen" || q.key == "pelvis")) continue;
            Vector3 f = (q.tr.rotation * q.offset) * Vector3.forward;
            Vector3 axis = Vector3.Cross(f, dir.normalized);
            float mis = Mathf.Clamp01((1f - Vector3.Dot(f, dir.normalized)) * 0.5f);
            if (axis.sqrMagnitude < 1e-6f) continue;
            q.rb.AddTorque(Vector3.Scale(axis.normalized * strength * mis, q.rb.inertiaTensor.magnitude * Vector3.one), ForceMode.Impulse);
        }
    }

    // ---- settling: for a while, steer the torso's front toward a direction (PD torque), e.g. to end on the back
    Vector3 settleDir = Vector3.up;
    float settleUntil = -1f, settleGain = 6f, settleDamp = 1.5f;

    public void SettleFront(Vector3 dir, float seconds, float gain, float damp)
    {
        settleDir = dir.normalized; settleUntil = Time.time + seconds; settleGain = gain; settleDamp = damp;
        WakeUp();
    }

    void Settle(float dt)
    {
        foreach (Part q in parts)
        {
            if (q.rb == null || !(q.key == "chest" || q.key == "abdomen" || q.key == "pelvis")) continue;
            Vector3 f = (q.tr.rotation * q.offset) * Vector3.forward;
            Vector3 axis = Vector3.Cross(f, settleDir);          // |axis| = sin(angle)
            float ang = Mathf.Atan2(axis.magnitude, Vector3.Dot(f, settleDir));
            Vector3 want = axis.sqrMagnitude > 1e-8f ? axis.normalized * ang * settleGain : Vector3.zero;
            // only the roll/pitch part of the angular velocity is damped
            Vector3 w = q.rb.angularVelocity;
            Vector3 dv = (want - w * settleDamp) * dt;
            q.rb.AddTorque(Vector3.Scale(dv, q.rb.inertiaTensor.magnitude * Vector3.one), ForceMode.Impulse);
        }
    }

    public string PartReport()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        System.Globalization.CultureInfo ci = System.Globalization.CultureInfo.InvariantCulture;
        foreach (Part q in parts)
        {
            if (q.rb == null) continue;
            Quaternion br = q.tr.rotation * q.offset;   // the bone's rotation implied by the proxy
            Vector3 p = q.rb.worldCenterOfMass, f = br * Vector3.forward, u = br * Vector3.up;
            sb.Append(q.key).Append(':');
            foreach (float v in new float[] { p.x, p.y, p.z, f.x, f.y, f.z, u.x, u.y, u.z }) sb.Append(v.ToString("0.###", ci)).Append(',');
            sb.Length--; sb.Append(';');
        }
        return sb.ToString();
    }

    // impulse (mass * units/s) on one part, e.g. to throw a body backwards when shot
    public bool PushPart(string key, Vector3 impulse)
    {
        foreach (Part q in parts)
            if (q.key == key && q.rb != null) { WakeUp(); q.rb.AddForce(impulse, ForceMode.Impulse); return true; }
        return false;
    }

    bool stayDead;   // killed by a script (a scene): never revives, unlike FPS-mode zombies

    public void Kill(string why)
    {
        stayDead = true;
        if (!built || State == 3) return;
        HP = 0f;
        Die(why);
    }

    void Die(string why)
    {
        State = 3; stateT = 0f; settleT = 0f; deadT = 0f;
        pinScale = 0f; muscleScale = 0f; poseBlend = 1f; riseStarted = false; riseW = 0f;
        RagdollLab.Log.LogInfo(Name + " died (" + why + ")");
    }

    public void Revive(RagdollLab lab)
    {
        if (!built) return;
        HP = MaxHP; Stagger = 0f;
        if (State == 3 || State == 1) BeginGetUp(lab);
    }

    // returns true when the caller should skip the regular state machine this frame
    bool UpdateCombat(float dt, RagdollLab lab)
    {
        Stagger *= Mathf.Exp(-lab.staggerDecay * dt);
        foreach (Part q in parts) q.weak = Mathf.Max(0f, q.weak - lab.weakenRegain * dt);
        if (State != 3) return false;
        stateT += dt;
        pinScale = 0f; muscleScale = 0f;
        Part pelvis = parts[0];
        settleT = pelvis.rb.velocity.magnitude < 3f ? settleT + dt : 0f;
        if (!stayDead && lab.reviveTime > 0f && stateT >= lab.reviveTime && settleT >= 0.4f && lab.pinWeight > 0f)
        {
            HP = MaxHP; Stagger = 0f;
            BeginGetUp(lab);
        }
        return true;
    }
}
