#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
fix_obfuscated_rsrc.py - 修复 Obfuscar 3.0 混淆产物损坏的 Win32 资源段
=====================================================================
背景：
  Obfuscar 3.0.0-beta.19 重写 PE 时会将 PE32+ (AnyCPU x64) 程序集改写成
  PE32 格式，并把 .rsrc 段整体向后搬移（因 .text 变大），同时还会"误修"
  资源数据区内的若干 DWORD。其结果是：
    1. 资源树内叶子数据条目的 OffsetToData（绝对 RVA）仍指向旧 .rsrc 段
       基址 -> CreateAppHost / 资源提取器解析越界；
    2. 资源数据区中若干原本指向旧段内数据的 DWORD 被改写成垃圾值。
  本脚本用混淆前的原始 dll 作为 .rsrc 内容基准：
    1) 用原始 .rsrc 段内容整体覆盖混淆版 .rsrc 段（树 + 数据全部还原）；
    2) 遍历资源目录树，把每个叶子数据条目的 OffsetToData 按
       (新段VA - 旧段VA) 增量重定位到新段位置。
  目录条目中的偏移是"相对 .rsrc 段基址"（微软 cvtres 生成方式），
  段整体搬移后依然有效，无需修改。

用法：
  python scripts/fix_obfuscated_rsrc.py <原始dll> <混淆dll> [输出dll]
  省略输出 dll 时直接原地修改混淆 dll。
"""
import struct
import sys


def get_rsrc_section(path):
    """返回 (VA, VirtualSize, RawPtr, RawSize) 或 None"""
    with open(path, 'rb') as f:
        dll = f.read()
    pe = struct.unpack_from('<I', dll, 0x3c)[0]
    nsec = struct.unpack_from('<H', dll, pe + 6)[0]
    size_opt = struct.unpack_from('<H', dll, pe + 20)[0]
    sptr = pe + 24 + size_opt
    for i in range(nsec):
        name = dll[sptr + i * 40:sptr + i * 40 + 8].rstrip(b'\x00').decode('latin1')
        if name == '.rsrc':
            vsize, vaddr, rsize, rptr = struct.unpack_from('<IIII', dll, sptr + i * 40 + 8)
            return (vaddr, vsize, rptr, rsize)
    return None


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(2)
    orig_path, obf_path = sys.argv[1], sys.argv[2]
    out_path = sys.argv[3] if len(sys.argv) > 3 else obf_path

    orig = get_rsrc_section(orig_path)
    obf = get_rsrc_section(obf_path)
    if orig is None or obf is None:
        print('[错误] 两个文件中至少一个没有 .rsrc 段')
        sys.exit(1)

    (va_orig, vs_orig, raw_orig, raw_size_orig) = orig
    (va_obf, vs_obf, raw_obf, raw_size_obf) = obf
    delta = va_obf - va_orig

    if raw_size_orig != raw_size_obf:
        print(f'[错误] .rsrc RawSize 不一致: 原始 0x{raw_size_orig:x} vs 混淆 0x{raw_size_obf:x}')
        sys.exit(1)

    print(f'[信息] 原始 .rsrc: VA=0x{va_orig:x} RawPtr=0x{raw_orig:x} RawSize=0x{raw_size_orig:x}')
    print(f'[信息] 混淆 .rsrc: VA=0x{va_obf:x} RawPtr=0x{raw_obf:x} RawSize=0x{raw_size_obf:x}')
    print(f'[信息] 段搬移量 DELTA = +0x{delta:x}')

    src = open(orig_path, 'rb').read()
    dll = bytearray(open(obf_path, 'rb').read())

    # 1) 整体还原 .rsrc 内容
    dll[raw_obf:raw_obf + raw_size_orig] = src[raw_orig:raw_orig + raw_size_orig]

    # 2) 遍历资源目录树，重定位叶子数据条目 OffsetToData
    moved = 0

    def walk(rva):
        """rva 为相对 .rsrc 段基址的偏移（目录条目低31位）"""
        nonlocal moved
        off = raw_obf + rva
        nid, nit = struct.unpack_from('<HH', dll, off + 12)
        for i in range(nid + nit):
            _, data_rva = struct.unpack_from('<II', dll, off + 16 + i * 8)
            if data_rva & 0x80000000:
                walk(data_rva & 0x7fffffff)
            else:
                e_off = raw_obf + data_rva
                d_rva, d_size, _ = struct.unpack_from('<III', dll, e_off)
                if not (va_orig <= d_rva < va_orig + vs_orig):
                    print(f'[警告] 叶子数据RVA=0x{d_rva:x} 不在原始 .rsrc 范围，跳过')
                    continue
                struct.pack_into('<I', dll, e_off, d_rva + delta)
                moved += 1

    walk(0)
    print(f'[信息] 已重定位叶子数据条目: {moved} 个')

    # 3) 校验：全部叶子 d_rva 必须落入新 .rsrc 段范围
    ok = True
    n = 0

    def walk_check(rva):
        nonlocal ok, n
        off = raw_obf + rva
        nid, nit = struct.unpack_from('<HH', dll, off + 12)
        for i in range(nid + nit):
            _, data_rva = struct.unpack_from('<II', dll, off + 16 + i * 8)
            if data_rva & 0x80000000:
                walk_check(data_rva & 0x7fffffff)
            else:
                n += 1
                e_off = raw_obf + data_rva
                d_rva, _, _ = struct.unpack_from('<III', dll, e_off)
                if not (va_obf <= d_rva < va_obf + vs_obf):
                    ok = False
                    print(f'[错误] 校验失败: d_rva=0x{d_rva:x} 不在新段 [0x{va_obf:x}, 0x{va_obf + vs_obf:x})')

    walk_check(0)
    print(f'[信息] 校验: {n} 个叶子条目, {"全部通过" if ok else "存在失败"}')

    if not ok:
        print('[错误] 校验未通过，拒绝写出')
        sys.exit(1)

    open(out_path, 'wb').write(bytes(dll))
    print(f'[完成] 已写出: {out_path}')


if __name__ == '__main__':
    main()
