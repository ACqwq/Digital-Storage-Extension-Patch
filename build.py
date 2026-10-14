#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""构建 DigitalStoragePatch.dll —— 开源版构建脚本（1.6 / 1.7 双版本产出）。

本 mod 采用「版本目录」结构：Mod 根目录的 `LoadFolders.xml` 会按游戏版本给玩家加载
`1.6/` 或 `1.7/` 里的构建。本脚本会**按你机器上的游戏版本自动选择**可编的版本：

  · 游戏目录是 1.7（游戏根目录直接有程序集）→ 产出 `1.7/Assemblies/DigitalStoragePatch.dll`
  · 游戏目录是 1.6（`RimWorldWin64_Data/Managed` 存在）→ 产出 `1.6/Assemblies/DigitalStoragePatch.dll`
  · 两套引用都在的机器（少见）：两个都会编

自动探测外部输入（全部可手动指定）：
  · 游戏目录   --game-dir / 环境变量 RIMWORLD_DIR
  · Harmony    工坊 2009463077 的 0Harmony.dll（1.7 在 `1.7/Assemblies/`，1.6 在 `1.6/Assemblies/` 或旧版 `Current/`）
  · 原 mod DLL 工坊 3623684313 的 DigitalStorage 程序集（--orig-dll 可指定）
编译器（Roslyn）自动探测：--csc / 环境变量 CSC → .NET SDK → VS/BuildTools 的 MSBuild Roslyn。

两种编译口径：
  · **1.7 模式**：引用游戏自带 BCL（System.Runtime 10.0 体系；net472 参考程序集会与 0Harmony 1.7 撞 CS1705）
    + `UnityStubs.dll`（旧 UnityEngine 模块已被移除）+ `Waylith.dll`（Material 等渲染类型）。
  · **1.6 模式**：net472 参考程序集 + 三个 `UnityEngine.*Module.dll` + 游戏 Managed 的 mscorlib/System。

用法（把本仓库放进 `<RimWorld>/Mods/` 后直接跑最省事）：
    python build.py                     # 按机器上的游戏版本自动编（通常是其中一个）
    python build.py --16 / --17         # 只编指定版本（引用缺失时报错）
    python build.py --game-dir "D:/SteamLibrary/steamapps/common/RimWorld"

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

UNITY_REFS_16 = [
    "UnityEngine.CoreModule.dll",
    "UnityEngine.IMGUIModule.dll",
    "UnityEngine.TextRenderingModule.dll",
]
UNITY_REF_17 = "UnityStubs.dll"
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
        if not c or not os.path.isdir(c):
            continue
        if os.path.isfile(os.path.join(c, "Assembly-CSharp.dll")):            # 1.7 布局
            return c
        if os.path.isdir(os.path.join(c, "RimWorldWin64_Data", "Managed")):   # 1.6 布局
            return c
    fail("找不到 RimWorld 游戏目录 —— 用 --game-dir 指定（1.7 认游戏根目录，1.6 认含 RimWorldWin64_Data 的目录）。")


def probe_harmony(cli, game_dir, mode):
    if cli:
        if os.path.isfile(cli):
            return norm(cli)
        fail("--harmony 指向的文件不存在：%s" % cli)
    if os.environ.get("HARMONY_DLL"):
        c = os.environ["HARMONY_DLL"]
        if os.path.isfile(c):
            return norm(c)
    steamapps = norm(os.path.join(game_dir, "..", ".."))
    mod_root = os.path.join(steamapps, "workshop", "content", "294100", "2009463077")
    if mode == "17":
        subs = ("1.7/Assemblies", "1.8/Assemblies", "Current/Assemblies", "1.6/Assemblies")
    else:
        subs = ("1.6/Assemblies", "Current/Assemblies")
    for sub in subs:
        c = os.path.join(mod_root, sub, "0Harmony.dll")
        if os.path.isfile(c):
            return norm(c)
    for c in sorted(glob.glob(os.path.join(mod_root, "*", "Assemblies", "0Harmony.dll"))):
        if os.path.isfile(c):
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
            for name in ("csc.exe", "csc.dll"):
                if os.path.isfile(os.path.join(c, name)):
                    c = os.path.join(c, name)
                    break
        if os.path.isfile(c):
            if c.lower().endswith("csc.dll") and dotnet_exe:
                return dotnet_exe, norm(c)
            if c.lower().endswith(".exe"):
                return None, norm(c)
    fail("找不到 Roslyn 编译器 —— 安装 .NET SDK（推荐）或 VS/Build Tools，"
         "或用 --csc 指定 csc 的路径（或其所在目录）。")


def resolve_mode(mode, game_dir, harmony, orig):
    """返回 (label, refs) ；引用缺失时返回 None（并说明原因）。"""
    managed_17 = game_dir
    managed_16 = os.path.join(game_dir, "RimWorldWin64_Data", "Managed")
    refs = []

    def add(p):
        if os.path.isfile(p):
            refs.append(norm(p))

    if mode == "17":
        if not os.path.isfile(os.path.join(managed_17, UNITY_REF_17)):
            return "游戏目录没有 UnityStubs.dll（不是 1.7 布局）", None
        add(os.path.join(managed_17, "Assembly-CSharp.dll"))
        add(harmony)
        add(orig)
        for name in sorted(os.listdir(managed_17)):        # 游戏自带 BCL
            if not name.lower().endswith(".dll"):
                continue
            if not (name == "netstandard.dll" or name == "mscorlib.dll"
                    or name.startswith("System.")):
                continue
            if name.endswith(".Native.dll"):
                continue
            add(os.path.join(managed_17, name))
        add(os.path.join(managed_17, UNITY_REF_17))
        add(os.path.join(managed_17, "Waylith.dll"))
        add(os.path.join(managed_17, "Waylith.UnityCompat.dll"))
        return "1.7（Waylith/CoreCLR：游戏根目录 + UnityStubs + 游戏 BCL）", refs

    if not os.path.isdir(managed_16):
        return "游戏目录没有 RimWorldWin64_Data/Managed（不是 1.6 布局）", None
    for m in UNITY_REFS_16:
        if not os.path.isfile(os.path.join(managed_16, m)):
            return "1.6 Managed 里缺 %s" % m, None
    add(os.path.join(managed_16, "Assembly-CSharp.dll"))
    add(harmony)
    add(orig)
    for name in ("mscorlib.dll", "System.dll", "System.Core.dll", "netstandard.dll"):
        add(os.path.join(managed_16, name))
    for u in UNITY_REFS_16:
        add(os.path.join(managed_16, u))
    return "1.6（Managed + UnityEngine 模块 + Mono BCL）", refs


def build_one(mode, game_dir, args, csc, dotnet_exe, sources):
    harmony = probe_harmony(args.harmony, game_dir, mode)
    orig = probe_orig_dll(args.orig_dll, game_dir)
    label, refs = resolve_mode(mode, game_dir, harmony, orig)
    if refs is None:
        return False, label

    if not any(r.endswith("Assembly-CSharp.dll") for r in refs):
        return False, "引用集不完整（缺 Assembly-CSharp.dll 等）"

    out_dir = os.path.join(HERE, "1.6" if mode == "16" else "1.7", "Assemblies")
    os.makedirs(out_dir, exist_ok=True)
    out_dll = os.path.join(out_dir, "DigitalStoragePatch.dll")

    info("── 模式 %s ──" % label)
    info("Harmony : %s" % harmony)
    info("原 mod  : %s" % orig)

    lines = [
        "-target:library", "-nologo", "-nostdlib", "-langversion:9",
        "-platform:anycpu", "-optimize+", "-deterministic",
        "-nowarn:" + NOWARN,
        '-out:"%s"' % out_dll,
    ]
    for r in refs:
        lines.append('-r:"%s"' % r)
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
    if proc.returncode != 0:
        info("模式 %s 编译失败" % mode)
        return False, "编译失败"
    info("完成：%s（%d 字节）" % (out_dll, os.path.getsize(out_dll)))
    return True, None


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game-dir", help="RimWorld 游戏目录（1.7 根目录 / 1.6 含 RimWorldWin64_Data）")
    ap.add_argument("--harmony", help="0Harmony.dll 路径")
    ap.add_argument("--orig-dll", help="原 mod（Digital Storage 4.0）的程序集路径")
    ap.add_argument("--csc", help="Roslyn 编译器路径（csc.exe 或 csc.dll，或其所在目录）")
    ap.add_argument("--16", dest="only16", action="store_true", help="只构建 1.6")
    ap.add_argument("--17", dest="only17", action="store_true", help="只构建 1.7")
    args = ap.parse_args()

    if not os.path.isdir(SRC_DIR):
        fail("找不到 Source/ 目录（本脚本应放在仓库根目录）。")

    game_dir = probe_game_dir(args.game_dir)
    dotnet_exe, csc = probe_compiler(args.csc)
    info("游戏目录 : " + game_dir)
    info("编译器   : " + (csc if not dotnet_exe else dotnet_exe + " " + csc))

    sources = []
    for root, dirs, files in os.walk(SRC_DIR):
        dirs[:] = [d for d in dirs if d.lower() != "verify"]
        for f in sorted(files):
            if f.lower().endswith(".cs"):
                sources.append(os.path.join(root, f))
    if not sources:
        fail("Source/ 下没有 .cs 文件")
    sources.sort()

    if args.only16:
        modes, explicit = ["16"], True
    elif args.only17:
        modes, explicit = ["17"], True
    else:
        modes, explicit = ["17", "16"], False

    results = []
    for mode in modes:
        ok, why = build_one(mode, game_dir, args, csc, dotnet_exe, sources)
        results.append((mode, ok, why))

    misses = [(m, w) for m, ok, w in results if not ok]
    if explicit and misses:
        fail("指定的版本无法构建：%s（%s）" % (misses[0][0], misses[0][1]))
    if not any(ok for _, ok, _ in results):
        fail("没有任何版本可构建（%s）—— 检查游戏安装与 --game-dir。" %
             "；".join("%s: %s" % (m, w) for m, w in misses))
    for m, why in misses:
        info("跳过 %s：%s" % (m, why))
    info("Done.")


if __name__ == "__main__":
    main()
