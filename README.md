# stp3 Studio Labs 1.1.0

BepInEx plugins for HoneySelect 2 CharaStudio, one DLL each.

| Plugin | Version | What it does |
| --- | --- | --- |
| Ragdoll Lab | 1.1.0 | Turns a character into a physics ragdoll you can grab, push and throw things at, with a zombie FPS mode |
| Wet Lab | 1.1.0 | A wet (or blood) stain that spreads through clothes and skin, with drips and a reflective floor puddle |
| Chara to Item | 1.1.0 | Bakes a character (or some of its parts) into a Studio item with FK bones; Cutter cuts characters and items |
| Toolbar Tweaks (optional) | 1.0.0 | Lays the left toolbar out in 3 columns and adds buttons for plugin windows that only open with a hotkey |
| Studio Bridge (optional) | 1.1.0 | Lets programs on this PC drive Studio through a local HTTP/JSON interface |
| Motion Lab (optional) | 1.0.0 | Plays motion clips (.hs2m) on characters; driven through Studio Bridge |

How to install and use them: [docs/README.md](docs/README.md)
(日本語: [docs/README_ja.md](docs/README_ja.md), 简体中文: [docs/README_zh-Hans.md](docs/README_zh-Hans.md)).
The illustrated guides are in [docs/guides](docs/guides), the Studio Bridge commands in
[docs/StudioBridge_Commands.md](docs/StudioBridge_Commands.md) and a guide for AI agents in [docs/AGENTS.md](docs/AGENTS.md).

## Source layout

    src/<Plugin>/*.cs                 the plugin code
    src/<Plugin>/Plugin.cs            the BepInEx entry (BepInPlugin attributes, dependencies)
    src/<Plugin>/HS2_<Plugin>.csproj
    src/Directory.Build.props         settings and game references shared by every project
    docs/                             the documents and guides that go into the release ZIP

The same code also runs as a C# Script Loader script (`<game>\scripts\`): the script entry is the `Main()` that
`Plugin.cs` calls. With both copies installed, the DLL stays off and logs a warning.

## Build

You need the .NET SDK (any recent version; the projects target net46 and reference the game's own assemblies, so no
other packages are downloaded) and an HS2 install with BepInEx and HS2API (KKAPI).

    dotnet build src/RagdollLab -c Release -p:GameDir="<game folder>"

`<game folder>` is the folder with `StudioNEOV2.exe`. The DLL lands in `src/RagdollLab/bin/Release/net46/`; copy it
to `<game>\BepInEx\plugins\stp3\`. The builds are deterministic and keep no debug information or local paths.

## Terms

The code of these plugins was written by stp3, who releases it into the public domain: use, change and share it
freely. This does not extend to HoneySelect 2, its assets or other people's mods: what you make from them with
these plugins (baked items, edited textures, captures) stays under their owners' terms.
