// #name StudioToolbarTweaks
// #author stp3
// #desc Studio left toolbar: lays the plugin buttons out in N columns (default 3) and adds buttons for plugin windows that only open with a hotkey.
// #proc_filter StudioNEOV2

// Loaded by BepInEx.ScriptLoader together with the other scripts in <game>\scripts (C# 7.x, mcs).
//
// Columns: KKAPI (1.48) places every button that has no saved position in a grid two columns wide
// (ToolbarManager.UpdateInterface: row = n / 2, column = n % 2). A transpiler replaces that 2 with Columns().
// Buttons dragged to a position in KKAPI's edit mode (right-click the toolbar's move button) keep it.
//
// Window buttons: several plugins open their window only with a hotkey. Each button (see Specs) reads the
// plugin's own "window shown" flag to show its state (green = open) and opens / closes the window by
//   - writing that flag, or calling the plugin's show / hide method, or
//   - "pressing" the plugin's own BepInEx hotkey for one frame (a prefix on KeyboardShortcut.IsDown), for
//     plugins whose opening does more than set a flag (Particle Editor) or that are obfuscated (GDC Wardrobe).
// Hotkeys still work; the button colour follows the window. A plugin that is missing, or whose members changed
// in an update, gets no button (a warning names it in the BepInEx log).
//
// Settings: <game>\BepInEx\config\stp3.toolbartweaks.cfg (columns, and each button on / off)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using KKAPI.Studio;
using KKAPI.Studio.UI.Toolbars;
using UnityEngine;

public static class StudioToolbarTweaksScript
{
    static GameObject host;
    static Harmony harmony;

    public static void Main()
    {
        StudioToolbarTweaks.Log = BepInEx.Logging.Logger.CreateLogSource("StudioToolbarTweaks");
        StudioToolbarTweaks.LoadConfig();
        harmony = new Harmony("stp3.toolbartweaks");
        MethodInfo ui = AccessTools.Method(typeof(ToolbarManager), "UpdateInterface");
        if (ui != null) harmony.Patch(ui, transpiler: new HarmonyMethod(typeof(StudioToolbarTweaks), "ColumnsTranspiler"));
        else StudioToolbarTweaks.Log.LogWarning("KKAPI ToolbarManager.UpdateInterface not found; the column count stays as it is");
        harmony.Patch(AccessTools.Method(typeof(KeyboardShortcut), "IsDown"), prefix: new HarmonyMethod(typeof(StudioToolbarTweaks), "IsDownPrefix"));
        host = new GameObject("StudioToolbarTweaks");
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<StudioToolbarTweaks>();
        ToolbarManager.RequestToolbarRelayout();   // hot reload: lay the existing buttons out again
    }

    public static void Unload()
    {
        if (host != null) UnityEngine.Object.DestroyImmediate(host);
        host = null;
        if (harmony != null) harmony.UnpatchSelf();
        harmony = null;
        ToolbarManager.RequestToolbarRelayout();
        if (StudioToolbarTweaks.Log != null) BepInEx.Logging.Logger.Sources.Remove(StudioToolbarTweaks.Log);
    }
}

public class StudioToolbarTweaks : MonoBehaviour
{
    public static ManualLogSource Log;
    static ConfigFile cfg;
    static ConfigEntry<int> columns;

    public static void LoadConfig()
    {
        cfg = new ConfigFile(Path.Combine(Paths.ConfigPath, "stp3.toolbartweaks.cfg"), true);
        columns = cfg.Bind("Toolbar", "Columns", 3, new ConfigDescription(
            "Columns of the left toolbar for buttons without a saved position (KKAPI's own layout uses 2).", new AcceptableValueRange<int>(1, 6)));
        columns.SettingChanged += (s, e) => ToolbarManager.RequestToolbarRelayout();
    }

    public static int Columns() { return columns != null ? Mathf.Clamp(columns.Value, 1, 6) : 2; }

    // row = n / 2; column = n % 2  ->  row = n / Columns(); column = n % Columns()
    static IEnumerable<CodeInstruction> ColumnsTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = new List<CodeInstruction>(instructions);
        MethodInfo get = AccessTools.Method(typeof(StudioToolbarTweaks), "Columns");
        int n = 0;
        for (int i = 0; i + 1 < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Ldc_I4_2) continue;
            if (code[i + 1].opcode != OpCodes.Div && code[i + 1].opcode != OpCodes.Rem) continue;
            code[i].opcode = OpCodes.Call;   // in place: keeps any labels on the instruction
            code[i].operand = get;
            n++;
        }
        if (n != 2) Log.LogWarning("toolbar columns: expected 2 places to patch, found " + n);
        else Log.LogInfo("toolbar columns: " + Columns());
        return code;
    }

    // ---------------------------------------------------------------- virtual hotkey press

    static KeyboardShortcut pressed;
    static int pressedFrame = -1;

    // every KeyboardShortcut equal to the pressed one reads as "just pressed" for the whole next frame
    static void Press(KeyboardShortcut s)
    {
        pressed = s;
        pressedFrame = Time.frameCount + 1;
    }

    static bool IsDownPrefix(ref KeyboardShortcut __instance, ref bool __result)
    {
        if (pressedFrame != Time.frameCount || pressed.MainKey == KeyCode.None || !__instance.Equals(pressed)) return true;
        __result = true;
        return false;
    }

    // ---------------------------------------------------------------- window buttons

    class Spec
    {
        public string id, label, title, guid;
        public string state;                 // "[Type:]member.member" to the bool that says the window is shown; null = unknown
        public string show, hide;            // methods (no arguments) on the object holding the flag
        public string set;                   // method (bool) on the object holding the flag
        public string keySection, keyName;   // the plugin's own hotkey setting (tooltip; opening by "pressing" it)
        public string keyText;               // tooltip hotkey when it is not a setting
        public bool openByKey;               // open by pressing the hotkey (close by clearing the flag)
        public bool shirtIcon;
    }

    static readonly Spec[] Specs =
    {
        new Spec { id = "striptease2", title = "StripTease2", guid = "com.ziglo.striptease2", state = "_windowVisible",
            show = "ShowWindow", hide = "HideWindow", keySection = "General", keyName = "Toggle window", shirtIcon = true },
        new Spec { id = "configmanager", label = "CFG", title = "Configuration Manager", guid = "com.bepis.bepinex.configurationmanager",
            state = "DisplayingWindow", keySection = "General", keyName = "Show config manager" },
        new Spec { id = "animcontroller", label = "ANIM", title = "Animation Controller", guid = "com.deathweasel.bepinex.animationcontroller",
            state = "GUIVisible", keySection = "Keyboard Shortcuts", keyName = "Toggle Animation Controller Window" },
        new Spec { id = "particleeditor", label = "PTCL", title = "Particle Editor (select one particle item first)", guid = "com.rskoi.particleeditor",
            state = "showWindow", openByKey = true, keySection = "Keyboard Shortcuts", keyName = "Show ParticleEditor GUI" },
        new Spec { id = "lightfromcamera", label = "LCAM", title = "Light From Camera", guid = "com.plugins.lightfromcamera",
            state = "showWindow", keyText = "F6" },
        new Spec { id = "toolkit", label = "TK", title = "Tool Kit", guid = "studio.itsnt.toolkit",
            state = "window.Visible", keySection = "General", keyName = "Toggle shortcut" },
        new Spec { id = "betterfog", label = "FOG", title = "BetterFog", guid = "com.ziglo.BetterFog",
            state = "_windowVisible", set = "SetWindowVisible", keySection = "General", keyName = "Toggle window" },
        new Spec { id = "charmonitor", label = "MON", title = "Studio Character Monitor", guid = "orange.spork.studiocharmon",
            state = "_shown.Value", keySection = "General", keyName = "Toggle monitor" },
        new Spec { id = "gdcwardrobe", label = "GDC", title = "GDC Wardrobe", guid = "com.kumiho.bepinex.hs2.gdcplugin",
            keySection = "Hotkeys", keyName = "Window toggle" },
        new Spec { id = "screenshot", label = "SHOT", title = "Screenshot Manager settings", guid = "com.bepis.bepinex.screenshotmanager",
            state = "_uiShow", keySection = "Keyboard shortcuts", keyName = "Open settings window" },
        new Spec { id = "expressioneditor", label = "EXPR", title = "Expression Editor", guid = "com.arg0x.HS2ExpressionEditor",
            state = "showGUI", keyText = "F8" },
        new Spec { id = "runtimeeditor", label = "RUE", title = "Runtime Unity Editor", guid = "RuntimeUnityEditor",
            state = "RuntimeUnityEditor.Core.RuntimeUnityEditorCore:Instance.Show", keySection = "General", keyName = "Open/close runtime editor" },
        new Spec { id = "autotranslator", label = "XUA", title = "XUnity Auto Translator", guid = "gravydevsupreme.xunity.autotranslator",
            state = "XUnity.AutoTranslator.Plugin.Core.AutoTranslationPlugin:Current.MainWindow.IsShown", keyText = "Alt+0" },
    };

    // A field or property by name on a type or its bases, cached. Plain reflection: AccessTools.Field / Property log a
    // HarmonyX warning whenever they find nothing, and these lookups run every frame (1.3 million log lines once).
    static readonly Dictionary<string, MemberInfo> members = new Dictionary<string, MemberInfo>();

    static MemberInfo Member(Type t, string name)
    {
        string key = t.AssemblyQualifiedName + "|" + name;
        MemberInfo m;
        if (members.TryGetValue(key, out m)) return m;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        for (Type x = t; x != null && m == null; x = x.BaseType)
        {
            m = x.GetField(name, all);
            if (m == null) m = x.GetProperties(all).FirstOrDefault(pp => pp.Name == name && pp.GetIndexParameters().Length == 0);
        }
        members[key] = m;
        return m;
    }

    // a bool reached from a plugin: [Type:]a.b.c, where the type (default: the plugin's) is the plugin itself,
    // a component in the scene (found when needed) or a holder of statics
    class BoolPath
    {
        public Type root;
        public BaseUnityPlugin plugin;
        public string[] path;
        UnityEngine.Object found;

        object Root()
        {
            if (root.IsInstanceOfType(plugin)) return plugin;
            if (typeof(Component).IsAssignableFrom(root))
            {
                if (found == null) found = UnityEngine.Object.FindObjectOfType(root);
                return found;
            }
            return null;
        }

        static object Get(object o, Type t, string name)
        {
            MemberInfo m = Member(t, name);
            FieldInfo f = m as FieldInfo;
            if (f != null) return f.GetValue(f.IsStatic ? null : o);
            PropertyInfo p = m as PropertyInfo;
            if (p != null) return p.GetValue(p.GetGetMethod(true).IsStatic ? null : o, null);
            throw new MissingMemberException(t.Name, name);
        }

        static bool IsStatic(Type t, string name)
        {
            MemberInfo m = Member(t, name);
            if (m is FieldInfo) return ((FieldInfo)m).IsStatic;
            PropertyInfo p = m as PropertyInfo;
            return p != null && p.GetGetMethod(true).IsStatic;
        }

        // the object holding the last member (null for a static one) and its type
        bool Holder(out object o, out Type t)
        {
            t = root;
            o = IsStatic(root, path[0]) ? null : Root();
            if (o == null && !IsStatic(root, path[0])) return false;
            for (int i = 0; i + 1 < path.Length; i++)
            {
                o = Get(o, t, path[i]);
                if (o == null) return false;
                t = o.GetType();
            }
            return true;
        }

        public bool TryGet(out bool v)
        {
            v = false;
            object o; Type t;
            if (!Holder(out o, out t)) return false;
            object r = Get(o, t, path[path.Length - 1]);
            if (!(r is bool)) return false;
            v = (bool)r;
            return true;
        }

        public bool TrySet(bool v)
        {
            object o; Type t;
            if (!Holder(out o, out t)) return false;
            string name = path[path.Length - 1];
            MemberInfo m = Member(t, name);
            FieldInfo f = m as FieldInfo;
            if (f != null) { f.SetValue(f.IsStatic ? null : o, v); return true; }
            PropertyInfo p = m as PropertyInfo;
            if (p == null || p.GetSetMethod(true) == null) return false;
            p.SetValue(p.GetSetMethod(true).IsStatic ? null : o, v, null);
            return true;
        }

        public bool Call(string method, object[] args)
        {
            object o; Type t;
            if (!Holder(out o, out t)) return false;
            MethodInfo m = AccessTools.Method(t, method);
            if (m == null) return false;
            m.Invoke(m.IsStatic ? null : o, args);
            return true;
        }
    }

    class WinButton
    {
        public Spec spec;
        public BaseUnityPlugin plugin;
        public BoolPath state;
        public ConfigEntryBase key;
        public ConfigEntry<bool> enabled;
        public ToolbarControlBase control;
        public Texture2D icon;
        public bool failed;

        public SimpleToolbarToggle Toggle { get { return control as SimpleToolbarToggle; } }
    }

    readonly List<WinButton> buttons = new List<WinButton>();
    bool resolved;

    void Update()
    {
        if (!StudioAPI.StudioLoaded) return;
        if (!resolved) Resolve();
        foreach (WinButton b in buttons)
        {
            if (b.failed) continue;
            if (!b.enabled.Value) { RemoveButton(b); continue; }
            if (b.control == null) AddButton(b);
            SimpleToolbarToggle t = b.Toggle;
            if (t == null || t.IsDisposed) continue;
            bool open;
            if (Shown(b, out open) && t.Toggled.Value != open) t.Toggled.OnNext(open);   // the hotkey changed it
        }
    }

    void OnDestroy()
    {
        foreach (WinButton b in buttons) RemoveButton(b);
    }

    void Resolve()
    {
        resolved = true;
        foreach (Spec s in Specs)
        {
            PluginInfo pi;
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(s.guid, out pi) || pi.Instance == null) continue;
            WinButton b = new WinButton { spec = s, plugin = pi.Instance };
            b.enabled = cfg.Bind("Buttons", s.title.Split('(')[0].Trim(), true, "Show a left toolbar button for this window.");
            try
            {
                if (s.keyName != null)
                    foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> kv in pi.Instance.Config)
                        if (kv.Key.Section == s.keySection && kv.Key.Key == s.keyName) b.key = kv.Value;
                if (s.state != null)
                {
                    string typeName = null, path = s.state;
                    int colon = s.state.IndexOf(':');
                    if (colon >= 0) { typeName = s.state.Substring(0, colon); path = s.state.Substring(colon + 1); }
                    Type root = pi.Instance.GetType();
                    if (typeName != null)
                    {
                        root = pi.Instance.GetType().Assembly.GetTypes().FirstOrDefault(t => t.Name == typeName || t.FullName == typeName)
                            ?? AccessTools.TypeByName(typeName);
                        if (root == null) throw new Exception("type " + typeName + " not found");
                    }
                    b.state = new BoolPath { root = root, plugin = pi.Instance, path = path.Split('.') };
                    if (Member(root, b.state.path[0]) == null)
                        throw new Exception(root.Name + "." + b.state.path[0] + " not found");
                }
                if ((s.openByKey || s.state == null) && !(b.key != null && b.key.BoxedValue is KeyboardShortcut)) throw new Exception("hotkey setting not found");
                if (s.show != null && AccessTools.Method(pi.Instance.GetType(), s.show) == null) throw new Exception(s.show + " not found");
                if (s.set != null && AccessTools.Method(pi.Instance.GetType(), s.set) == null) throw new Exception(s.set + " not found");
            }
            catch (Exception ex)
            {
                Log.LogWarning(s.title + " " + pi.Metadata.Version + ": no button (" + ex.Message + "; plugin changed?)");
                continue;
            }
            buttons.Add(b);
        }
    }

    // false when the state cannot be read now (e.g. its scene component does not exist yet)
    static bool Shown(WinButton b, out bool open)
    {
        open = false;
        if (b.state == null) return false;
        try { return b.state.TryGet(out open); }
        catch (Exception) { return false; }
    }

    static void SetShown(WinButton b, bool open)
    {
        bool now;
        if (Shown(b, out now) && now == open) return;
        Spec s = b.spec;
        try
        {
            bool done;
            if (open && s.openByKey) { Press((KeyboardShortcut)b.key.BoxedValue); done = true; }
            else if (s.show != null) done = b.state.Call(open ? s.show : s.hide, null);
            else if (s.set != null) done = b.state.Call(s.set, new object[] { open });
            else done = b.state.TrySet(open);
            if (!done) Log.LogWarning(s.title + ": the window is not available right now");
        }
        catch (Exception ex) { Log.LogWarning(s.title + " " + (open ? "open" : "close") + ": " + (ex.InnerException ?? ex).Message); }
    }

    void AddButton(WinButton b)
    {
        try
        {
            Spec s = b.spec;
            b.icon = s.shirtIcon ? ShirtIcon() : LabelIcon(s.label);
            string keyText = b.key != null ? b.key.BoxedValue.ToString() : s.keyText;
            string tip = s.title + (string.IsNullOrEmpty(keyText) || keyText == "None" ? "" : " [" + keyText + "]");
            string id = "stp3.toolbartweaks." + s.id;
            if (s.state == null)
            {
                b.control = new SimpleToolbarButton(id, tip, () => b.icon, b.plugin,
                    ib => { if (ib == UnityEngine.EventSystems.PointerEventData.InputButton.Left) Press((KeyboardShortcut)b.key.BoxedValue); });
            }
            else
            {
                bool open;
                Shown(b, out open);
                b.control = new SimpleToolbarToggle(id, tip, () => b.icon, open, b.plugin, v => SetShown(b, v));
            }
            ToolbarManager.AddLeftToolbarControl(b.control);
        }
        catch (Exception ex) { b.failed = true; b.control = null; Log.LogWarning(b.spec.title + " button: " + ex.Message); }
    }

    static void RemoveButton(WinButton b)
    {
        if (b.control != null)
        {
            try { ToolbarManager.RemoveControl(b.control); } catch (Exception) { }
            b.control = null;
        }
        if (b.icon != null) { Destroy(b.icon); b.icon = null; }
    }

    // diagnostics (StudioBridge plugin.call): "row,column id [owner]" of every visible button, then each window button's state
    public string Layout()
    {
        List<string> rows = new List<string>();
        foreach (ToolbarControlBase b in ToolbarManager.GetAllButtons(false))
        {
            if (b.ButtonObject == null || !b.ButtonObject.gameObject.activeSelf) continue;
            Vector2 p = ((RectTransform)b.ButtonObject.transform).anchoredPosition;
            // ToolbarControlAdapter (the game's own buttons) is internal: mcs lets a script name it, csc does not
            string owner = b.GetType().Name == "ToolbarControlAdapter" ? "BaseGame" : b.Owner != null ? b.Owner.Info.Metadata.GUID : "?";
            rows.Add(Mathf.RoundToInt(p.y) + "," + Mathf.RoundToInt(p.x) + " " + b.ButtonID + " [" + owner + "]");
        }
        rows.Sort();
        List<string> states = new List<string>();
        foreach (WinButton b in buttons)
        {
            bool open;
            states.Add(b.spec.id + ": " + (b.failed ? "failed" : b.control == null ? "no button" : b.state == null ? "hotkey" : Shown(b, out open) ? (open ? "open" : "closed") : "unknown"));
        }
        return "columns " + Columns() + "\n" + string.Join("\n", states.ToArray()) + "\n" + string.Join("\n", rows.ToArray());
    }

    public string SetWindow(string id, bool open)
    {
        WinButton b = buttons.FirstOrDefault(x => x.spec.id == id);
        if (b == null) return "no button " + id;
        if (b.state == null) { Press((KeyboardShortcut)b.key.BoxedValue); return "pressed"; }
        SetShown(b, open);
        return "ok";
    }

    // presses the plugin's own hotkey (checks the virtual key press on a plugin whose state can be read)
    public string PressKey(string id)
    {
        WinButton b = buttons.FirstOrDefault(x => x.spec.id == id);
        if (b == null || b.key == null || !(b.key.BoxedValue is KeyboardShortcut)) return "no hotkey setting for " + id;
        Press((KeyboardShortcut)b.key.BoxedValue);
        return "pressed " + b.key.BoxedValue;
    }

    public bool IsOpen(string id)
    {
        WinButton b = buttons.FirstOrDefault(x => x.spec.id == id);
        bool open;
        return b != null && Shown(b, out open) && open;
    }

    // ---------------------------------------------------------------- icons (y = 0 is the bottom row)

    static readonly Dictionary<char, string> Font = new Dictionary<char, string>
    {
        { 'A', ".###.#...##...#######...##...##...#" }, { 'B', "####.#...##...#####.#...##...#####." },
        { 'C', ".###.#...##....#....#....#...#.###." }, { 'D', "####.#...##...##...##...##...#####." },
        { 'E', "######....#....####.#....#....#####" }, { 'F', "######....#....####.#....#....#...." },
        { 'G', ".###.#...##....#.####...##...#.####" }, { 'H', "#...##...##...#######...##...##...#" },
        { 'I', ".###...#....#....#....#....#...###." }, { 'K', "#...##..#.#.#..##...#.#..#..#.#...#" },
        { 'L', "#....#....#....#....#....#....#####" }, { 'M', "#...###.###.#.##.#.##...##...##...#" },
        { 'N', "#...##...###..##.#.##..###...##...#" }, { 'O', ".###.#...##...##...##...##...#.###." },
        { 'P', "####.#...##...#####.#....#....#...." }, { 'R', "####.#...##...#####.#.#..#..#.#...#" },
        { 'S', ".#####....#.....###.....#....#####." }, { 'T', "#####..#....#....#....#....#....#.." },
        { 'U', "#...##...##...##...##...##...#.###." }, { 'X', "#...##...#.#.#...#...#.#.#...##...#" },
        { 'Y', "#...##...#.#.#...#....#....#....#.." }, { '2', ".###.#...#....#...#...#...#...#####" },
    };

    static void DrawText(Color32[] px, string text, int x0, int top, int sx, int sy, int gap, bool bold)
    {
        Color32 on = new Color32(255, 255, 255, 255);
        for (int g = 0; g < text.Length; g++)
        {
            string glyph;
            if (!Font.TryGetValue(text[g], out glyph)) continue;
            for (int r = 0; r < 7; r++)
                for (int c = 0; c < 5; c++)
                {
                    if (glyph[r * 5 + c] != '#') continue;
                    for (int dy = 0; dy < sy; dy++)
                        for (int dx = 0; dx < sx + (bold ? 1 : 0); dx++)
                        {
                            int x = x0 + g * (5 * sx + gap) + c * sx + dx, y = top - r * sy - dy;
                            if (x >= 0 && x < 32 && y >= 0 && y < 32) px[y * 32 + x] = on;
                        }
                }
        }
    }

    static Texture2D Finish(Color32[] px, string name)
    {
        Texture2D t = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        t.SetPixels32(px);
        t.Apply(false);
        t.name = name;
        return t;
    }

    // the label in big letters (2 letters: 10 x 14 each; 3: 10 x 14, 1 px apart; 4: bold 6 x 14)
    static Texture2D LabelIcon(string label)
    {
        Color32[] px = new Color32[32 * 32];
        int n = label.Length;
        bool bold = n >= 4;
        int sx = n <= 3 ? 2 : 1, gap = n <= 2 ? 2 : bold ? 2 : 1;
        int w = n * 5 * sx + (n - 1) * gap + (bold ? 1 : 0);
        DrawText(px, label, (32 - w) / 2, 22, sx, 2, gap, bold);
        return Finish(px, "ToolbarIcon_" + label);
    }

    // a shirt over the letters "ST2"
    static Texture2D ShirtIcon()
    {
        Color32[] px = new Color32[32 * 32];
        Color32 on = new Color32(255, 255, 255, 255);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                bool body = x >= 11 && x <= 20 && y >= 11 && y <= 25;
                bool sleeve = y >= 19 && y <= 25 && ((x >= 5 && x <= 10 && y - 19 >= 10 - x - 2) || (x >= 21 && x <= 26 && y - 19 >= x - 21 - 2));
                float dx = x - 15.5f, dy = y - 26.5f;
                if ((body || sleeve) && dx * dx + dy * dy >= 9f) px[y * 32 + x] = on;
            }
        DrawText(px, "ST2", 7, 8, 1, 1, 1, false);
        return Finish(px, "StripTease2Icon");
    }
}
