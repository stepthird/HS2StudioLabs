# Studio Bridge 1.1.0: commands

Studio Bridge listens on `http://127.0.0.1:17321/` while CharaStudio runs. POST one JSON object per request:

    curl -s -X POST http://127.0.0.1:17321/ -d "{\"cmd\":\"camera.get\"}"

The command runs on Studio's main thread. The reply is `{"ok": true, ...}` or `{"ok": false, "error": "..."}`.
Requests that carry an `Origin` or `Sec-Fetch-Site` header (web browsers) are refused with HTTP 403.
Non-ASCII text (Japanese item names, for example) is safest from a script (Python `urllib`) rather than a shell.

Conventions:

- Objects are addressed by `key`, the number Studio gives each object in the workspace (`scene` lists them).
- Positions are in Studio units: 1 unit = 10 cm. `pos`, `rot`, `scale` of `obj.transform` are Studio's own values
  (local to the parent, rotations in degrees).
- A "target" is usually `{"key": K, "bone": "cf_J_Head", "offset": [x, y, z]}` or a point `[x, y, z]`.
- Arguments that are left out keep their current value; commands that only read return the state.
- Camera: `rot` is [pitch, yaw, 0]; positive pitch looks down; yaw 0 looks toward +z, 90 toward +x, 180 toward -z.
  `fov` is the vertical field of view. The camera moves on the next frame: camera replies report the target,
  rotation and distance you set, but `worldPos` / `forward` from before the move.
- New to this? Read `AGENTS.md` first.

## Scene, objects, maps

| cmd | arguments | what it does |
| --- | --- | --- |
| `ping` | | alive check: Studio loaded, frame, screen size |
| `scene` | | map, camera and every object (key, kind, name, pos/rot/scale; animation of characters) |
| `select` | `key` | selects the object in the workspace |
| `obj.transform` | `key`, `pos`, `rot`, `scale` | sets the object's transform (Studio values, local to the parent) |
| `obj.moveTo` | `key`, `target`, `time`, `stop`, `stanceSpeed` | walks the object toward a target over `time` s, stopping `stop` units short and turning to face it; with `stanceSpeed` (the walk clip's foot speed) the animation speed is set so the feet do not slide |
| `delete` | `key` | deletes the object |
| `item.search` | `q`, `group`, `category`, `limit` (60) | searches the item list; `truncated: true` when more matched than `limit` |
| `item.add` | `group`, `category`, `no` | adds an item; returns its key |
| `item.attach` | `key` (item), `chara`, `bone`, `pos`, `rot`, `off` | makes an item follow a bone every frame (after the animation, before drawing); not saved with the scene |
| `item.parent` | `key`, `chara`, `point` (name or number, e.g. 48 = right hand; empty = detach) | Studio's own parenting to a character part (as dragging in the workspace); saved with the scene and still movable by hand |
| `chara.load` | `path` (card; relative paths are under `UserData/chara/female` or `male`), `sex` | adds a character from a card |
| `maps` | `q`, `limit` (50) | searches map names; returns `[{no, name}]` |
| `map.load` | `no` or `name` | loads a map (asynchronous: check `scene` -> `map.no`) |
| `ground.probe` | `points` [[x, z], ...], `y` (0), `above` (6), `below` (30) | casts a vertical ray at each point from `y + above` down to `y - below` and returns the first upward-facing map surface (`y`, `normal`; `y: null` when nothing is in range). Works on the map meshes, since most Studio maps have no colliders. A table top in range is found before the floor: keep `above` small to get the floor |
| `debug.renderers` | `point`, `radius` | what is drawn near a point: layer, mesh readability, shader, bounds |
| `debug.renderercount` | | renderers and colliders of the map, by type and layer |
| `toolbar.list` | | buttons of Studio's left toolbar: id, tooltip, owner plugin, toggle state, screen rect [x, y, w, h] (pixels from the top left) |
| `toolbar.click` | `id`, `on` | presses a toolbar button as a click would (for example to open a plugin's window); `on: true / false` sets a toggle |

## Characters: animation, pose, body

| cmd | arguments | what it does |
| --- | --- | --- |
| `anime.groups` | | animation groups and categories |
| `anime.search` | `q`, `group`, `category`, `limit` (60) | searches animations; `truncated: true` when more matched than `limit` |
| `chara.anime` | `key`, `group`, `category`, `no`, `speed`, `time`, `loop` | changes the animation |
| `chara.speed` | `key`, `speed`, `loop` | playback speed and forced loop |
| `chara.animeOption` | `key`, `visible` | the animation's props (for example a rifle): list, parent bone, show or hide |
| `pose.load` | `key`, `path` (under `UserData/studio/pose`, or absolute) | loads a kinematics pose file (animation and IK/FK); the root position is unchanged |
| `fk.list` | `key`, `group` (Body, Neck, LeftHand, ...) | FK joints and their angles |
| `fk.capture` | `key`, `stopAnime` | copies the current animation pose into FK and turns FK on (speed 0 by default) |
| `fk.set` | `key`, `bones` {name: [x, y, z]} | FK joint angles (local Euler) |
| `fk.enable` / `ik.enable` | `key`, `on`, `groups` | FK / IK on or off |
| `chara.bones` | `key`, `names` [] | world positions and directions of bones |
| `chara.skeleton` | `key`, `all` | every cf_J_ / cf_N_ bone: local pose, pose in the character frame, and the bind (T) pose of the body prefab |
| `chara.face` | `key`, `eyebrow`, `eyes`, `mouth`, `eyesOpen`, `mouthOpen`, `blink`, `eyesLook`, `neckLook`, `tears`, `blush`, `raw` | expression and gaze. Eyebrow and eye numbers are those shown in Studio's panel; `raw: true` uses the internal ids (also returned as `eyebrowId`, `eyesId`) |
| `chara.hands` | `key`, `left`, `right` | hand shapes (pattern number; 0 = from the animation) |
| `chara.clothes` | `key`, `all`, `states` {top, bot, inner_t, inner_b, gloves, panst, socks, shoes: 0 on, 1 half, 2 off} | clothes state |
| `chara.accessories` | `key`, `show` {slot: bool} | accessory slots (name, parent, shown) and showing or hiding them |
| `chara.accessoryAxis` | `key`, `slot` | the long axis of an accessory's mesh and its far end (for aiming a held weapon) |
| `hair.info` / `hair.set` | `key` / `key`, `kinds` {"0": id, ...} (0 back, 1 front, 2 side, 3 extension) | reads / changes the hair parts; the hair FK guides are rebuilt |

## Camera and output

| cmd | arguments | what it does |
| --- | --- | --- |
| `camera.get` / `camera.set` | `target`, `rot`, `dist`, `fov` | Studio camera: target point, rotation, distance, field of view |
| `camera.frame` | `key` or `point`, `height` (0), `dist` (25), `yaw` (180), `pitch` (10), `fov` | targets the object's root (or the point) raised by `height`, then sets the orbit like `camera.set` |
| `camera.follow` | `target`, `rot` [pitch, yaw, 0], `dist`, `fov`, `seconds` (0 = stop), `rotTo`, `distTo`, `above` | follows a bone for `seconds`, optionally orbiting to `rotTo` and moving to `distTo` |
| `capture` | `name`, `width`, `height`, `waitFrames` (3) | renders the main camera to `UserData/StudioBridge/shots/<name>.png` (no UI) and returns `path`; a file of the same name is overwritten, and characters not allowed in file names become `_`. It waits `waitFrames` frames first so camera changes apply |
| `record.start` | `width`, `height`, `fps`, `path`, `fixed` | records raw RGBA frames (bottom-up rows) to `path` (default `UserData/StudioBridge/rec.raw`) with a `.txt` of frame times; `fixed: true` (default) steps the game exactly 1/fps per frame (slow while recording, smooth video) |
| `record.stop` | | stops recording |
| `dof` | see below | depth of field |

`dof` arguments: `on`; `focus` (`"target"` = Studio's camera target, a key or `{"key": K}` = a character's eyes or
an item's centre, `{"key", "bone", "offset"}`, `{"point": [x, y, z]}`, `{"dist": d}`), followed every frame;
`blend` (seconds of rack focus) and `ease` (inout, linear, in, out); `sharp` and `full` (behind the subject, things
stay sharp up to (1 + sharp) times the focus distance and are fully blurred beyond `full` times) or Studio's raw
`size` / `aperture`; `blur` (maximum blur as on a 1080-line frame, scaled to the frame being drawn); `blurType`
(`disc`, Studio's default, clean up to about 5; `dx11`, smooth up to 20 at about half the strength); `near`;
`samples`, `highRes`, `overlap`, `visualize`; `release` (focus back to Studio); `reset` (Studio defaults).
Timeline gets a "Depth of Field" item (owner StudioBridge, id dof) so these can be keyed and saved with the scene.

## Timeline

| cmd | arguments | what it does |
| --- | --- | --- |
| `timeline.build` | `duration`, `tracks` [], `play`, `append`, `returnXml` | builds Timeline keyframes from a JSON shot description and loads them |
| `timeline.events` | `events` [{`t`, `do`: {command}}], `clear` | runs commands when playback passes their time (again after each loop) |
| `timeline.play` / `pause` / `stop` / `seek` / `clear` / `status` | `t` | playback |
| `timeline.export` / `timeline.loadxml` | / `xml` | Timeline's XML out and in |

A track is `{"type": ..., "key": object, "ease": "inout|in|out|linear|step", "keys": [{"t": seconds, ...}]}`.
A key may carry its own `ease` for the span up to the next key.

| type | values in a key |
| --- | --- |
| `camera` | `target` [3], `rot` [3], `dist`, `fov` |
| `pos`, `rot`, `scale` | `v` [3] (the object's Studio values) |
| `anime` | `group`, `category`, `no` |
| `animeSpeed`, `eyesOpen`, `mouthOpen`, `blush`, `timeScale` | `v` |
| `eyes`, `mouth`, `leftHand`, `rightHand` | `v` (pattern number; eyes as in Studio's panel, `raw: true` on the track for internal ids) |
| `visible` | `v` (true / false) |
| `dof` | `on`, `focus`, `sharp` / `full` (or `size` / `aperture`), `blur`, `near`; missing values carry over from the previous key |
| `raw` | `owner`, `id`, `attrs` and `keys[].attrs`: any other plugin's Timeline item |

Two keys at the same time make Timeline drop the whole item: offset cuts by 0.02 s.

## Other plugins

| cmd | arguments | what it does |
| --- | --- | --- |
| `plugin.get` / `plugin.set` / `plugin.call` | `plugin` (type name: `RagdollLab`, `WetLab`, `CharaToItem`, `C2ICutter`, `StudioToolbarTweaks`, `MotionLab`), `fields` / `values` / `method`, `args` | reads and sets public fields and calls methods of a running plugin |
| `ragdoll.build` | `key`, `mapFloor` (default true), `room` | Ragdoll Lab: ragdolls one character (no selection needed); the room floor goes on the map surface |
| `ragdoll.release` | | releases every ragdoll at once and clears blood marks |
| `ragdoll.push` | `key`, `part` (pelvis, abdomen, chest, head, thigh_L, ...), `impulse` (world) or `local` [x, y, z] (character frame), `away` (push away from another object) | an impulse on one part |
| `ragdoll.lock` | `key`, `parts` (prefixes, default "upperarm,forearm,hand") | holds those parts in their current pose |
| `ragdoll.tie` | `key`, `a`, `b` (default hand_L, hand_R) | ties two parts together |
| `ragdoll.pin` | `key`, `part` (default hand_R), `follow` (object key), `snap` (false) | pins a part where it is; with `follow` the pin follows that object from then on (`snap`: pulled to the object's origin instead of keeping the offset) |
| `ragdoll.unpin` | `key` (none: every character) | drops pins; returns how many |
| `ragdoll.follower` | `key`, `part` (default hand_R) | adds a Studio folder that copies the part's pose every frame (parent items under it); returns `folder` (its key) |
| `ragdoll.turn` / `ragdoll.settle` | `key`, `dir`, `strength` / `seconds`, `gain`, `damp` | turns / steers the torso front toward `dir` (default up: onto the back) |
| `ragdoll.pusher` | `key`, `a`, `b` (bones), `radius` | a kinematic capsule along a bone of an animated character that pushes ragdolls (a kick) |
| `ragdoll.parts` | `key` | report of the ragdoll parts |
| `fx.shoot` | `from` (item key or point), `target`, `spread` (degrees), `kill` | one Ragdoll Lab bullet; the muzzle is the item's far end toward the target |
| `wet.attach` | `key`, `from`, `target`, `preset` | Wet Lab on a character without selecting it; with `from` / `target` the stain starts where that line first meets the body. Start and dry with `plugin.call WetLab ScriptStart` / `ScriptDry` |
| `wet.stain` / `wet.use` / `wet.status` | `name`, `preset`, `target`, `skinDrips`, ... / `name`, `start` / | named stains prepared ahead and started later |
| `motion.load` / `play` / `pause` / `seek` / `speed` / `stop` / `status` | see Motion Lab | forwarded to Motion Lab |

## Wardrobe tools (for making clothes mods)

| cmd | arguments | what it does |
| --- | --- | --- |
| `clothes.search` / `acc.search` | `key`, `kind` (clothes 0 top .. 7 shoes) or `category`, `q`, `limit` | list items with their runtime ids (Sideloader renumbers mods, so search by name) |
| `clothes.set` / `clothes.color` | `key`, `kind`, `id` / `colors` [[r, g, b, a] x 3], `gloss`, `metallic` | puts clothes on, changes their colours |
| `acc.set` / `acc.move` / `acc.color` | `key`, `slot`, `category` (default 351), `id` (-1 removes), `parent` / `idx`, `pos`, `rot`, `scale` / `colors` | accessory slots |
| `wear.info` / `wear.export` | `key`, `kind` or `slot` or `part` ("body", "head", "hair0".."hair3"), `dir`, `renderer` | renderers, materials and textures of a worn piece / its meshes as `.json` + `.bin` (default folder `UserData/StudioBridge/wear`) |
| `wear.setmesh` / `wear.restore` | `key`, `kind` / `slot`, `renderer`, `file` | swaps a renderer's mesh for an exported (and edited) one / restores it |
| `wear.settex` / `wear.setprop` / `wear.show` | `renderer`, `prop`, `file`, `linear` / `floats`, `colors`, `textures`, `keywords` / `on`, `cast`, `receive` | changes textures and material values at run time |
| `wear.effects` | `disable` / `enable` [type names], `lights` {name: {...}}, `allCast`, `scope`, `key`, `rmin`, `rmax`, `names` | camera effects and lights: list and switch; turn shadow casting off scene-wide to find where a shadow comes from |
| `bundle.check` / `bundle.wear` | `path`, `asset` / `path`, `prefab`, `part`, `renderer`, `maintex` | checks that a rebuilt bundle loads / puts its mesh and material on a worn piece |
| `coord.save` / `coord.load` | `key`, `path` (relative paths are under `UserData/coordinate/<sex>`), `name`, `png` | saves the current outfit as a coordinate card / loads one |
