#!/usr/bin/env python3
"""A mod's release zip: dist/<Mod>-<version>.zip holding BepInEx/plugins/<Mod>/<Mod>.dll.

    python3 tools/package.py <Mod> [--game DIR] [--mp-api DLL]

Builds mods/<Mod> in Release (against --game, else TLD_DIR, else the default Steam folder; --mp-api builds an API mod
against another TLDRevamp.Multiplayer.dll than the game's), refuses a DLL with a private string in it (home folder,
user name, LAN address, Steam ID, Windows user path), and prints the DLL's SHA-256 line for RevaMP's
packaging/approved-mods.txt and the zip's size for its packaging/mods.json.
"""
import argparse, getpass, hashlib, os, pathlib, re, subprocess, sys, zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent


def strings(data):
    """readable runs of 4+ characters, as `strings -a` and `strings -a -el` find them"""
    for m in re.finditer(rb"[\x20-\x7e]{4,}", data): yield m.group().decode("ascii")
    for m in re.finditer(rb"(?:[\x20-\x7e]\x00){4,}", data): yield m.group().decode("utf-16-le")


def private_hits(data):
    home, user = str(pathlib.Path.home()), getpass.getuser()
    pat = re.compile(r"192\.168\.\d|7656119\d{10}|[A-Za-z]:\\Users", re.I)
    return [s for s in strings(data) if home.lower() in s.lower() or (len(user) > 2 and user.lower() in s.lower()) or pat.search(s)]


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("mod")
    ap.add_argument("--game", help="the game folder to build against (beta branch, BepInEx installed)")
    ap.add_argument("--mp-api", help="TLDRevamp.Multiplayer.dll to build an API mod against")
    a = ap.parse_args()
    m = a.mod
    project = ROOT / "mods" / m / f"{m}.csproj"
    if not project.is_file(): sys.exit(f"no {project.relative_to(ROOT)}")
    cmd = ["dotnet", "build", str(project), "-c", "Release", "-nologo", "-v", "q", "--disable-build-servers"]
    if a.game: cmd.append(f"-p:GameDir={a.game}")
    if a.mp_api: cmd.append(f"-p:MpApi={a.mp_api}")
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0: print(r.stdout[-3000:], r.stderr[-1000:]); sys.exit(f"building {m} failed")
    dlls = sorted((ROOT / "mods" / m / "bin/Release").glob(f"*/{m}.dll"), key=lambda p: p.stat().st_mtime)
    if not dlls: sys.exit(f"no {m}.dll under mods/{m}/bin/Release")
    dll = dlls[-1]
    data = dll.read_bytes()
    hits = private_hits(data)
    if hits: sys.exit(f"private string in {dll}:\n  " + "\n  ".join(hits[:5]))
    src = (ROOT / "mods" / m / "Plugin.cs").read_text(encoding="utf-8", errors="replace")
    v = re.search(r'Version\s*=\s*"(\d+\.\d+\.\d+)"', src)
    g = re.search(r'Guid\s*=\s*"([^"]+)"', src) or re.search(r'BepInPlugin\(\s*"([^"]+)"', src)
    if not v or not g: sys.exit(f"no plugin GUID or version found in mods/{m}/Plugin.cs")
    ver, guid = v.group(1), g.group(1)
    out = ROOT / "dist" / f"{m}-{ver}.zip"
    out.parent.mkdir(exist_ok=True)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        z.writestr(f"BepInEx/plugins/{m}/{m}.dll", data)
    print(f"{out.relative_to(ROOT)} ({out.stat().st_size} bytes)")
    print(f"approved: {hashlib.sha256(data).hexdigest()} {guid} {ver}")


if __name__ == "__main__":
    main()
