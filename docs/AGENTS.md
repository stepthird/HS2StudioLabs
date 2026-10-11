# Studio Bridge: notes for AI agents

You can drive HoneySelect 2 CharaStudio through Studio Bridge (stp3 Studio Labs 1.1.0): load maps and
characters, pose them, set the camera, build Timeline shots, and capture images you can look at. This file is what
you need to start; `StudioBridge_Commands.md` (same folder) lists every command and argument.

## 1. Check that it is there

    POST http://127.0.0.1:17321/   body: {"cmd": "ping"}
    -> {"ok": true, "studio": true, "version": 1, "frame": 6, "realtime": 22.9, "screen": "1920x1080"}

- Connection refused: CharaStudio is not running, or `HS2_StudioBridge.dll` is not installed
  (`<game>\BepInEx\plugins\stp3\`). Ask the user to start CharaStudio (`<game>\StudioNEOV2.exe`); startup takes
  about half a minute to a few minutes.
- `"studio": false`: Studio is still loading. Poll `ping` every few seconds.
- HTTP 403: the request carried an `Origin` or `Sec-Fetch-Site` header. Browsers send these; Studio Bridge refuses
  them on purpose. Use a plain HTTP client.

## 2. How to call it

One JSON object per POST; the reply is one JSON object. Every reply has `"ok"`; failures are
`{"ok": false, "error": "..."}`. A request blocks until the command has run on Studio's main thread (at most 60 s).

Use a small script rather than shell one-liners: JSON quoting differs between shells, and names in the game are
often Japanese (UTF-8 is required).

```python
import json, urllib.request

def call(cmd, **args):
    args["cmd"] = cmd
    req = urllib.request.Request("http://127.0.0.1:17321/", data=json.dumps(args).encode("utf-8"), method="POST")
    with urllib.request.urlopen(req, timeout=120) as r:
        rep = json.loads(r.read().decode("utf-8"))
    if not rep.get("ok"):
        raise RuntimeError("%s: %s" % (cmd, rep.get("error")))
    return rep
```

(curl works too: `curl -s -X POST http://127.0.0.1:17321/ -d "{\"cmd\":\"scene\"}"` in cmd.exe; in PowerShell
prefer `Invoke-RestMethod -Method Post -Uri http://127.0.0.1:17321/ -Body '{"cmd":"scene"}'`.)

## 3. A first session

```python
call("scene")                                   # map, camera, and every object: key, kind, name, pos, rot, ...
call("maps", q="ロビー")                         # search map names (many are Japanese); returns [{no, name}]
call("map.load", no=10)                         # asynchronous: poll call("scene")["map"]["no"] until it is 10
r = call("chara.load", path="MyCard.png")       # a card file under <game>\UserData\chara\female
key = r["added"][0]["key"]                      # (male card: add sex="male", file under ...\male; or an absolute path)
call("chara.anime", key=key, group=0, category=1, no=0)        # a standing pose (find others with anime.search)

# Studio puts a new character at the map's start point: maybe inside furniture, maybe floating.
# Find an open spot first: look at a top-down capture (section 4, "Floor plan") and pick a clear [x, z].
x, z = 20, -10
floor = call("ground.probe", points=[[x, z]], y=0, above=1, below=5)["hits"][0]["y"]   # None: no floor there
call("obj.transform", key=key, pos=[x, floor, z])
toe = call("chara.bones", key=key, names=["cf_J_Toes01_L"])["bones"]["cf_J_Toes01_L"]["pos"][1]
call("obj.transform", key=key, pos=[x, floor - (toe - floor - 0.1), z])  # the toe joint sits ~0.1 above the sole

call("camera.frame", key=key, height=8.5, dist=30, yaw=180, pitch=5, fov=35)   # front view of a character at rot 0
path = call("capture", name="first", width=1280, height=800)["path"]           # PNG of the main camera, no UI
```

Then open the PNG and look at it: is the whole body there, feet included, with nothing in front of it? Check
every scene you build this way before reporting it as done; a reply of `"ok": true` only means the command ran.

`chara.load` (and `item.add`) add a new object on every call. If you run your script again, read `scene` first and
reuse the character you added instead of adding another; delete the extra ones you made by mistake.
`capture` writes `<name>.png` to `<game>\UserData\StudioBridge\shots\` and overwrites a file of the same name
(characters not allowed in file names become `_`); the reply always gives the full path. Use names of your own,
such as a prefix, so you do not overwrite the user's captures.

Reply shapes of the commands you will use most (besides `"ok"`):

| command | reply |
| --- | --- |
| `scene` | `map` {no, name}, `camera` {target, rot, dist, fov, worldPos, forward}, `objects` [{key, kind, name, pos, rot, scale, worldPos, ...}] |
| `maps` | `maps` [{no, name}] |
| `anime.search` | `anime` [{group, category, no, groupName, categoryName, name}], `truncated` |
| `item.search` | `items` [{group, category, no, groupName, categoryName, name}], `truncated` |
| `clothes.search`, `acc.search` | `category`, `items` [...], `truncated` |
| `chara.load`, `item.add` | `added` [{key, name, kind, pos, rot, scale, ...}] |
| `chara.bones` | `bones` {name: {pos, forward}} |
| `ground.probe` | `hits` [{x, z, y, normal}] (`y` is null where nothing was hit) |
| `capture` | `path` |
| `toolbar.list` | `buttons` [{id, tip, owner, on, shown, rect}] |

## 4. How Studio's world works

- Objects are addressed by `key` (an integer, from `scene` or from the reply of a command that adds something).
  Keys are kept while Studio runs; loading a scene file can renumber them, so read `scene` again after loading.
- Units: 1 unit = 10 cm. A typical character is about 16 units tall. y is up. Rotations are Euler degrees.
  A character with rotation [0, 0, 0] faces +z. Standing poses are looping animations, so bones sway a little.
- Camera: `rot` is [pitch, yaw, 0] around `target`, at distance `dist`. Positive pitch looks down. yaw 0 looks toward
  +z, 90 toward +x, 180 toward −z, 270 toward −x, so a character at rotation 0 is seen from the front with yaw 180.
  `fov` is the vertical field of view (the zoom).
- `camera.frame` targets an object's root position plus `height` (a character's hips are about 9 above its root,
  its head about 13), then sets yaw, pitch, dist and fov like `camera.set`.
- The camera moves on the frame after the command. Replies of camera commands give the target, rotation, distance
  and fov you set, but `worldPos` and `forward` from before the move; call `camera.get` later for those.
  `capture` waits 3 frames by default (`waitFrames`).
- Walls do not stop the camera. A camera behind a wall gives an image of the wall (or a flat colour) and no error.
  When the view is blocked, move closer and widen `fov` instead of backing away; always look at the capture.
- Floor plan: a top-down capture shows the layout. `camera.set target=[x, 0, z], rot=[90, 180, 0], dist=D, fov=F`:
  the image's up is −z and its right is −x, and the image height spans about 2·D·tan(F/2) units.
- `ground.probe` casts a vertical ray at each [x, z] from `y + above` down to `y − below` (defaults y 0, above 6,
  below 30) and returns the first upward-facing map surface it meets: a table top if one is in range. To find the
  floor, keep `above` small (for example y = 0, above = 1), and check the spot on a top-down capture.
  Maps usually have no colliders, which is why this works on meshes.
- Characters with shortened legs need their root below the floor (a root y such as −1.4 with the floor at 0 is
  normal); the toe-joint check in section 3 handles this.
- Mod items, clothes and maps get new numbers from Sideloader each time Studio starts. Look them up by name
  (`item.search`, `clothes.search`, `maps`, `anime.search`) in every session; do not reuse numbers from an earlier run.
  Searches return at most `limit` results (60 for animations and items, 50 for maps, clothes and accessories) and
  say `"truncated": true` when there are more: narrow `q` or raise `limit`. `anime.groups` is large (about 40 KB).
- Face: `chara.face` eyebrow and eye numbers are the ones shown in Studio's panel; the reply also gives internal ids.
- Timeline (`timeline.build`): two keys at the same time make Timeline drop the whole track; offset cuts by 0.02 s.

## 5. Other stp3 plugins

- `plugin.get` / `plugin.set` / `plugin.call` reach the public fields and methods of a running plugin by its type
  name: `RagdollLab`, `WetLab`, `CharaToItem`, `C2ICutter`, `StudioToolbarTweaks`, `MotionLab`.
  The error `plugin X not running` means that plugin is not installed or not loaded.
- Shortcuts: `ragdoll.*` and `fx.shoot` (Ragdoll Lab), `wet.*` (Wet Lab), `motion.*` (Motion Lab).
- `toolbar.list` lists the left toolbar's buttons; `toolbar.click {"id": ..., "on": true}` opens a plugin's window
  (`"on": false` closes it). The stp3 buttons are `stp3.ragdolllab`, `stp3.wetlab`, `stp3.charatoitem`,
  `stp3.charatoitem.cutter`, and `stp3.toolbartweaks.<name>` for the window buttons Toolbar Tweaks adds; other
  plugins' ids are in `toolbar.list` (`tip` is the tooltip, `owner` the plugin's GUID).
- To capture the screen with Studio's UI and plugin windows (when Chara to Item is installed):
  `plugin.call {"plugin": "CharaToItem", "method": "ScreenShot", "args": ["C:\\path\\to\\file.png"]}` with an
  absolute path; Unity writes the file at the end of a frame, so wait about a second before reading it.

## 6. Working alongside the user

The user may be working in this Studio session. Studio Bridge changes the scene directly and there is no undo.

- Do not delete, move or restyle objects you did not create unless asked. Note the keys you add, and clean up
  only those. Leaving the camera where you put it is fine.
- Do not save or overwrite scenes, cards or coordinate files unless asked (`coord.save`, scene files).
- Chara to Item: deleting a bake also deletes textures that no remaining bake file uses. Never move bake files
  out of `UserData\CharaToItem` while deleting bakes.
- If Studio stops answering (`timed out waiting for the main thread`), it is busy or was closed; check `ping` and
  tell the user rather than retrying in a loop.
