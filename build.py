#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建 DigitalStoragePatch.dll —— 开源版构建脚本。

自动探测三样外部输入（全部可手动指定）：
  · 游戏目录   RimWorld（含 RimWorldWin64_Data/Managed）   --game-dir / 环境变量 RIMWORLD_DIR
  · Harmony    工坊 2009463077 的 0Harmony.dll             --harmony   / 环境变量 HARMONY_DLL
  · 原 mod DLL 工坊 3623684313 的 DigitalStorage 程序集     --orig-dll  / 环境变量 DS_ORIG_DLL
编译器（Roslyn）自动探测：--csc / 环境变量 CSC → .NET SDK → VS/BuildTools 的 MSBuild Roslyn。

最省事的用法（把本仓库放进 `<RimWorld>/Mods/` 后直接跑）：
    python build.py

其它用法：
    python build.py --game-dir "D:/SteamLibrary/steamapps/common/RimWorld"
    python build.py --harmony "…/2009463077/Current/Assemblies/0Harmony.dll" --orig-dll "…/DigitalStorage40.dll"

产物：Assemblies/DigitalStoragePatch.dll（并生成 Source/build.rsp，可忽略）。

说明：本脚本面向"任何一台装了游戏的机器"——不含也不写入任何本机绝对路径。
对外发布版 DLL 由维护者用同一套源码构建；不同机器构建出的字节不一定相同，属正常现象。
"""
import argparse
import glob
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC_DIR = os.path.join(HERE, "Source")
ASM_DIR = os.path.join(HERE, "Assemblies")

UNITY_REFS = [
    "UnityEngine.CoreModule.dll",
    "UnityEngine.IMGUIModule.dll",
    "UnityEngine.TextRenderingModule.dll",
]
CORE_REFS = ["mscorlib.dll", "System.dll", "System.Core.dll", "netstandard.dll"]
CORE_OPTIONAL = {"netstandard.dll"}   # Unity Mono 的 mscorlib 走 netstandard 转发；个别安装缺失则跳过
NOWARN = "1701,1702,8600,8601,8602,8603,8604,0436,0169,0649"


def fail(msg):
    print("[build] 失败：" + msg)
    sys.exit(1)


def info(msg):
    print("[build] " + msg)


def norm(p):
    return os.path.normpath(p) if p else p


def probe_game_dir(cli):
    cands = []
    if cli:
        cands.append(cli)
    env = os.environ.get("RIMWORLD_DIR")
    if env:
        cands.append(env)
    # 本仓库被放进 <游戏>/Mods/<任意名> 时的自动探测
    cands.append(os.path.join(HERE, "..", ".."))
    # 常见 Steam 库
    for root in ("C:/Program Files (x86)/Steam", "C:/SteamLibrary",
                 "D:/SteamLibrary", "D:/steam", "E:/SteamLibrary", "F:/SteamLibrary"):
        cands.append(os.path.join(root, "steamapps", "common", "RimWorld"))
    for c in cands:
        c = norm(c)
        if c and os.path.isdir(os.path.join(c, "RimWorldWin64_Data", "Managed")):
            return c
    fail("找不到 RimWorld 游戏目录 —— 用 --game-dir 指定（目录里应有 RimWorldWin64_Data）。")


def probe_harmony(cli, game_dir):
    cands = []
    if cli:
        cands.append(cli)
    if os.environ.get("HARMONY_DLL"):
        cands.append(os.environ["HARMONY_DLL"])
    steamapps = norm(os.path.join(game_dir, "..", ".."))
    for sub in ("Current/Assemblies", "Assemblies"):
        cands.append(os.path.join(steamapps, "workshop", "content", "294100",
                                  "2009463077", sub, "0Harmony.dll"))
    for c in cands:
        if c and os.path.isfile(c):
            return norm(c)
    fail("找不到 0Harmony.dll —— 请在工坊订阅 Harmony（2009463077），或用 --harmony 指定。")


def probe_orig_dll(cli, game_dir):
    cands = []
    if cli:
        cands.append(cli)
    if os.environ.get("DS_ORIG_DLL"):
        cands.append(os.environ["DS_ORIG_DLL"])
    steamapps = norm(os.path.join(game_dir, "..", ".."))
    mod_root = os.path.join(steamapps, "workshop", "content", "294100", "3623684313")
    for sub in ("Current/Assemblies", "Assemblies"):
        cands.extend(sorted(glob.glob(os.path.join(mod_root, sub, "*DigitalStorage*.dll"))))
        cands.extend(sorted(glob.glob(os.path.join(mod_root, sub, "*.dll"))))
    for mods_sub in ("cagier.digitalstorage", "DigitalStorage"):
        cands.extend(sorted(glob.glob(os.path.join(game_dir, "Mods", mods_sub,
                                                   "Assemblies", "*.dll"))))
    for c in cands:
        if c and os.path.isfile(c) and os.path.basename(c).lower() != "0harmony.dll":
            return norm(c)
    fail("找不到原 mod 的 DLL —— 请先订阅《【CA】数字存储-Digital Storage 4.0》（3623684313），"
         "或用 --orig-dll 指定其 Assemblies 下的 DLL。")


def probe_compiler(cli):
    cands = [cli, os.environ.get("CSC")]
    # .NET SDK（推荐）：dotnet <sdk>/Roslyn/bincore/csc.dll
    dotnet_exe = shutil.which("dotnet")
    sdk_out = ""
    if dotnet_exe:
        try:
            sdk_out = subprocess.run([dotnet_exe, "--list-sdks"], capture_output=True,
                                     text=True, timeout=30).stdout or ""
        except Exception:
            sdk_out = ""
    sdk_lines = [l for l in sdk_out.splitlines() if "[" in l]
    if sdk_lines:
        last = sdk_lines[-1]
        ver, base = last.split(" [", 1)
        base = base.rstrip("]").strip()
        cands.append(os.path.join(base, ver.strip(), "Roslyn", "bincore", "csc.dll"))
    # VS / BuildTools 的 MSBuild Roslyn
    for pattern in (
        "C:/Program Files/Microsoft Visual Studio/2022/*/MSBuild/Current/Bin/Roslyn/csc.exe",
        "C:/Program Files (x86)/Microsoft Visual Studio/2022/*/MSBuild/Current/Bin/Roslyn/csc.exe",
        "C:/Program Files/Microsoft Visual Studio/2019/*/MSBuild/Current/Bin/Roslyn/csc.exe",
        "C:/Program Files (x86)/Microsoft Visual Studio/2019/*/MSBuild/Current/Bin/Roslyn/csc.exe",
    ):
        cands.extend(sorted(glob.glob(pattern)))
    for c in cands:
        if not c:
            continue
        if os.path.isdir(c):
            # 允许直接给「Roslyn 目录」：自动挑 csc.exe / csc.dll
            for name in ("csc.exe", "csc.dll"):
                if os.path.isfile(os.path.join(c, name)):
                    c = os.path.join(c, name)
                    break
        if os.path.isfile(c):
            if c.lower().endswith("csc.dll") and dotnet_exe:
                return dotnet_exe, norm(c)      # 通过 dotnet 运行
            if c.lower().endswith(".exe"):
                return None, norm(c)
    fail("找不到 Roslyn 编译器 —— 安装 .NET SDK（推荐）或 VS/Build Tools，"
         "或用 --csc 指定 csc 的路径（或其所在目录）。")


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game-dir", help="RimWorld 游戏目录（含 RimWorldWin64_Data）")
    ap.add_argument("--harmony", help="0Harmony.dll 路径")
    ap.add_argument("--orig-dll", help="原 mod（Digital Storage 4.0）的程序集路径")
    ap.add_argument("--csc", help="Roslyn 编译器路径（csc.exe 或 csc.dll）")
    args = ap.parse_args()

    if not os.path.isdir(SRC_DIR):
        fail("找不到 Source/ 目录（本脚本应放在仓库根目录）。")

    game_dir = probe_game_dir(args.game_dir)
    managed = os.path.join(game_dir, "RimWorldWin64_Data", "Managed")
    harmony = probe_harmony(args.harmony, game_dir)
    orig = probe_orig_dll(args.orig_dll, game_dir)
    dotnet_exe, csc = probe_compiler(args.csc)

    refs = []
    for name in CORE_REFS + ["Assembly-CSharp.dll"] + UNITY_REFS:
        p = os.path.join(managed, name)
        if not os.path.isfile(p):
            if name in CORE_OPTIONAL:
                continue
            fail("游戏 Managed 目录缺少 %s —— 游戏安装可能不完整。" % name)
        refs.append(p)
    refs.append(harmony)
    refs.append(orig)

    info("游戏目录 : " + game_dir)
    info("Harmony  : " + harmony)
    info("原 mod   : " + orig)
    info("编译器   : " + (csc if not dotnet_exe else dotnet_exe + " " + csc))

    os.makedirs(ASM_DIR, exist_ok=True)
    out_dll = os.path.join(ASM_DIR, "DigitalStoragePatch.dll")

    lines = [
        "-target:library", "-nologo", "-nostdlib", "-langversion:9",
        "-platform:anycpu", "-optimize+", "-deterministic",
        "-nowarn:" + NOWARN,
        '-out:"%s"' % out_dll,
    ]
    for r in refs:
        lines.append('-r:"%s"' % r)

    sources = []
    for root, dirs, files in os.walk(SRC_DIR):
        dirs[:] = [d for d in dirs if d.lower() != "verify"]
        for f in sorted(files):
            if f.lower().endswith(".cs"):
                sources.append(os.path.join(root, f))
    if not sources:
        fail("Source/ 下没有 .cs 文件")
    sources.sort()
    for s in sources:
        lines.append('"%s"' % s)

    rsp = os.path.join(SRC_DIR, "build.rsp")
    with open(rsp, "w", encoding="utf-8-sig", newline="\r\n") as fh:
        fh.write("\n".join(lines) + "\n")

    info("编译 %d 个源文件 ..." % len(sources))
    cmd = ([dotnet_exe] if dotnet_exe else []) + [csc, "@" + rsp]
    proc = subprocess.run(cmd, capture_output=True)

    def _decode(raw):
        if not raw:
            return ""
        try:
            return raw.decode("utf-8")
        except UnicodeDecodeError:
            return raw.decode("gbk", errors="replace")

    out = (_decode(proc.stdout) + _decode(proc.stderr)).strip()
    if out:
        print(out)
    info("csc exit: %d" % proc.returncode)
    if proc.returncode != 0:
        fail("编译失败")
    info("完成：%s（%d 字节）" % (out_dll, os.path.getsize(out_dll)))


if __name__ == "__main__":
    main()
