#!/usr/bin/env python3
"""Writes the synthetic WeChat exports the iOS tests read.

Run from anywhere: python3 -I ios/Scripts/make-fixtures.py
The output is committed; re-run only when the fixture contents change.

Entries are written the way WeChat writes them: UTF-8 names with the
language-encoding flag cleared, deflate compression. Python sets the flag on
any non-ASCII name, so the header bits are patched after writing.
"""
import io
import os
import struct
import zipfile

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Tests", "Fixtures")
MEDIA = "聊天记录内的图片、视频和文件/"

# A 1x1 PNG: the smallest real image, so UIImage can decode it in tests.
PNG = bytes.fromhex(
    "89504e470d0a1a0a0000000d4948445200000001000000010806000000"
    "1f15c4890000000d49444154789c6360000002000154a24f5d0000000049454e44ae426082"
)


def message(sender, stamp, text):
    return f"·{sender}\n{stamp}\n{text}\n"


def write(name, transcript, media):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("聊天记录.txt", transcript)
        for path, data in media.items():
            archive.writestr(MEDIA + path, data)
    data = bytearray(buffer.getvalue())
    clear_utf8_flag(data)
    with open(os.path.join(OUT, name), "wb") as handle:
        handle.write(data)


def clear_utf8_flag(data):
    """Clears bit 11 in every local and central directory header."""
    offset = 0
    while True:
        local = data.find(b"PK\x03\x04", offset)
        if local < 0:
            break
        flags = struct.unpack_from("<H", data, local + 6)[0]
        struct.pack_into("<H", data, local + 6, flags & ~0x800)
        offset = local + 4
    offset = 0
    while True:
        central = data.find(b"PK\x01\x02", offset)
        if central < 0:
            break
        flags = struct.unpack_from("<H", data, central + 8)[0]
        struct.pack_into("<H", data, central + 8, flags & ~0x800)
        offset = central + 4


def main():
    os.makedirs(OUT, exist_ok=True)

    # group-a: three people, every placeholder kind. 8 messages.
    group_a = "\n".join([
        message("甲", "2026年10月20日 09:00", "早上好，今天讨论发布计划"),
        message("乙", "2026年10月20日 09:01", "[图片] 微信图片_20261020_1.png"),
        message("丙", "2026年10月20日 09:02", "[链接] 发布清单模板 https://mp.weixin.qq.com/s/abc123"),
        message("甲", "2026年10月20日 09:03", "[视频号] 产品演示 https://weixin.qq.com/sph/xyz789"),
        message("乙", "2026年10月20日 09:04", "[视频] 录屏_1.mp4"),
        message("丙", "2026年10月20日 09:05", "[文件] 需求文档.pdf"),
        message("甲", "2026年10月20日 09:06", "第一行\n第二行"),
        message("乙", "2026年10月20日 09:07", "[破涕为笑]"),
    ])
    write("group-a.zip", group_a, {
        "微信图片_20261020_1.png": PNG,
        "录屏_1.mp4": b"\x00\x00\x00\x18ftypmp42",
        "需求文档.pdf": b"%PDF-1.4\n%%EOF\n",
    })

    # group-a-later: the same group a day later, interleaving with group-a when
    # merged by time is not possible (later day), but the sender set overlaps so
    # the fingerprint matches. Also reuses an image name with different bytes.
    group_a_later = "\n".join([
        message("甲", "2026年10月21日 10:00", "昨天的结论再确认一下"),
        message("丙", "2026年10月21日 10:01", "[图片] 微信图片_20261020_1.png"),
        message("乙", "2026年10月21日 10:02", "没问题"),
    ])
    write("group-a-later.zip", group_a_later, {
        "微信图片_20261020_1.png": PNG + b"\x00",
    })

    # interleave: times fall between group-a messages, for merge ordering.
    interleave = "\n".join([
        message("乙", "2026年10月20日 09:00", "插在第一条之后"),
        message("丙", "2026年10月20日 09:03", "插在中间"),
    ])
    write("interleave.zip", interleave, {})

    # not-wechat: a ZIP with no transcript, to exercise parseFailed.
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("readme.txt", "hello")
    with open(os.path.join(OUT, "not-wechat.zip"), "wb") as handle:
        handle.write(buffer.getvalue())


if __name__ == "__main__":
    main()
