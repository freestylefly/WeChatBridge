#!/usr/bin/env python3
"""下载微信视频号分享链接（weixin.qq.com/sph/xxx）为可播放 mp4。

解析链参考 GitHub codersjj/sph-downloader：sph 短链 → content_id（id##1）
→ sph.miuistore.com /sph/public/quick（需 enc.js 本地 AlgoSign 签名）
→ 返回加密视频流地址 + base64 XOR 密钥 → 下载并把文件头部按密钥 XOR 还原。
依赖：node（生成签名）、ffprobe（可选，用于完整性校验）。
"""
import base64
import json
import pathlib
import re
import subprocess
import sys
import urllib.parse
import urllib.request

SIGN_MJS = pathlib.Path(__file__).resolve().parent / "sign.mjs"
API = "https://sph.miuistore.com/sph/public/quick"
UA = ("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")


def content_id(url: str) -> str:
    m = re.search(r"/sph/([A-Za-z0-9_-]+)", url) or re.search(r"[?&]id=([A-Za-z0-9_-]+)", url)
    if not m:
        sys.exit(f"无法识别的链接：{url}")
    return m.group(1) + "##1"


def make_sign(cid: str) -> str:
    r = subprocess.run(["node", str(SIGN_MJS), cid], capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit("签名失败：" + r.stderr.strip())
    return r.stdout.strip()


def resolve(cid: str) -> dict:
    q = urllib.parse.urlencode({"id": cid, "sign": make_sign(cid), "_from": "", "title": ""})
    req = urllib.request.Request(f"{API}?{q}", headers={"User-Agent": UA,
                                                       "Referer": "https://sph.miuistore.com/"})
    data = json.loads(urllib.request.urlopen(req, timeout=30).read())
    if data.get("error") != 0 or not data.get("url"):
        sys.exit(f"解析失败：{data.get('message') or data}")
    return data


def download(url: str, key: bytes, out: pathlib.Path) -> int:
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    resp = urllib.request.urlopen(req, timeout=60)
    written = 0
    with open(out, "wb") as f:
        while chunk := resp.read(1 << 16):
            if written < len(key):
                n = min(len(key) - written, len(chunk))
                chunk = bytes(b ^ key[written + i] for i, b in enumerate(chunk[:n])) + chunk[n:]
            f.write(chunk)
            written += len(chunk)
            print(f"\r下载中 {written / 1048576:.1f} MB", end="", flush=True)
    print()
    return written


def verify(path: pathlib.Path) -> None:
    try:
        r = subprocess.run(["ffprobe", "-v", "error", "-show_entries",
                            "format=duration", "-of", "csv=p=0", str(path)],
                           capture_output=True, text=True)
        print(f"ffprobe 时长: {r.stdout.strip() or '未知'}s" if r.returncode == 0
              else "ffprobe 校验失败：" + r.stderr.strip())
    except FileNotFoundError:
        pass


def main() -> None:
    url = sys.argv[1] if len(sys.argv) > 1 else sys.exit("用法: save_video.py <视频号分享链接> [输出目录]")
    outdir = pathlib.Path(sys.argv[2] if len(sys.argv) > 2 else pathlib.Path.home() / "Downloads")
    outdir.mkdir(parents=True, exist_ok=True)
    cid = content_id(url)
    info = resolve(cid)
    title = re.sub(r'[\\/:*?"<>|\r\n]', "_", (info.get("media") or {}).get("title") or cid)
    out = outdir / f"{title}.mp4"
    size = download(info["url"], base64.b64decode(info.get("_data") or ""), out)
    print(f"已保存: {out} ({size / 1048576:.2f} MB)")
    verify(out)


if __name__ == "__main__":
    main()
