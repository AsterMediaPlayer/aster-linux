#!/usr/bin/env python3
"""
Aster's artwork for a non-Steam shortcut: the library's tiles, the game
page's banner and the icon, in place of Steam's blank ones.

Steam keeps a shortcut's artwork in userdata/<account>/config/grid, named by
the shortcut's app id: <id>p.png (the portrait tile, 600x900), <id>.png (the
wide tile, 920x430, Home's last played), <id>_hero.png (the game page's
banner, 1920x620), <id>_logo.png (drawn over the banner, in place of
the name) and <id>_icon.png. Every shortcut that runs Aster gets
them, in each account on this PC. Steam shows them once it starts again.

The icon in the library's list is the shortcut's own "icon" setting, kept in
shortcuts.vdf. Steam writes that file over from its memory, so it's changed
only while Steam isn't running; otherwise the script says so and leaves it.

    python3 packaging/steam/install-art.py
"""
import glob
import os
import shutil
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ART = {"p.png": "capsule.png", ".png": "header.png", "_hero.png": "hero.png", "_logo.png": "logo.png", "_icon.png": "icon.png"}
STEAM_ROOTS = ["~/.local/share/Steam", "~/.steam/steam", "~/.var/app/com.valvesoftware.Steam/.local/share/Steam"]


# ---- Steam's binary VDF (shortcuts.vdf): maps, strings, 32-bit numbers ----

def parse(data, i=0):
    node = {}
    while True:
        kind = data[i]; i += 1
        if kind == 8:
            return node, i
        end = data.index(b"\0", i); name = data[i:end].decode("utf-8", "surrogateescape"); i = end + 1
        if kind == 0:
            value, i = parse(data, i)
        elif kind == 1:
            end = data.index(b"\0", i); value = data[i:end].decode("utf-8", "surrogateescape"); i = end + 1
        elif kind == 2:
            value = struct.unpack("<I", data[i:i + 4])[0]; i += 4
        else:
            raise ValueError(f"unknown VDF type {kind}")
        node[name] = value


def dump(node):
    out = bytearray()
    for name, value in node.items():
        key = name.encode("utf-8", "surrogateescape") + b"\0"
        if isinstance(value, dict):
            out += b"\0" + key + dump(value)
        elif isinstance(value, int):
            out += b"\2" + key + struct.pack("<I", value)
        else:
            out += b"\1" + key + value.encode("utf-8", "surrogateescape") + b"\0"
    return bytes(out + b"\x08")


def field(shortcut, name):
    """Steam's fields come in more than one case (AppName, appname)."""
    return next((v for k, v in shortcut.items() if k.lower() == name.lower()), None)


def is_aster(shortcut):
    text = " ".join(str(field(shortcut, n) or "") for n in ("exe", "LaunchOptions", "AppName"))
    return "Aster.Desktop" in text or "aster" in str(field(shortcut, "AppName") or "").lower()


def steam_running():
    return subprocess.run(["pgrep", "-x", "steam"], capture_output=True).returncode == 0


def main():
    files = set()
    for root in STEAM_ROOTS:
        files.update(os.path.realpath(p) for p in glob.glob(os.path.join(os.path.expanduser(root), "userdata", "*", "config", "shortcuts.vdf")))
    if not files:
        sys.exit("No Steam shortcuts found. Add Aster to Steam first (Games > Add a Non-Steam Game).")
    running = steam_running()
    found = 0
    for path in sorted(files):
        with open(path, "rb") as f:
            vdf, _ = parse(f.read())
        grid = os.path.join(os.path.dirname(path), "grid")
        changed = False
        for shortcut in vdf.get("shortcuts", {}).values():
            if not is_aster(shortcut) or field(shortcut, "appid") is None:
                continue
            found += 1
            appid = field(shortcut, "appid")
            os.makedirs(grid, exist_ok=True)
            for suffix, name in ART.items():
                shutil.copyfile(os.path.join(HERE, name), os.path.join(grid, f"{appid}{suffix}"))
            icon = os.path.join(grid, f"{appid}_icon.png")
            print(f"{field(shortcut, 'AppName')} ({appid}): artwork in {grid}")
            key = next((k for k in shortcut if k.lower() == "icon"), "icon")
            if shortcut.get(key) != icon:
                if running:
                    print("  The list's icon: quit Steam and run this again to set it.")
                else:
                    shortcut[key] = icon
                    changed = True
        if changed:
            shutil.copyfile(path, path + ".bak")
            with open(path, "wb") as f:
                f.write(dump(vdf))
            print(f"  Icon set (the old shortcuts file kept as {path}.bak).")
    if not found:
        sys.exit("No shortcut runs Aster.")
    print("Restart Steam to see it." if running else "Start Steam to see it.")


if __name__ == "__main__":
    main()
