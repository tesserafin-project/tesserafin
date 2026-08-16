#!/usr/bin/env python3
"""Read a PE image with the standard library only (W1-A2, issue #236).

Why this exists rather than a call to llvm-readobj or dumpbin:

  * the import closure, the architecture check and the wrong-architecture
    negative control are then runnable on ANY host, including the Linux
    workstation where this file was written. A validator that only runs on the
    hosted Windows runner cannot be exercised before it is dispatched, and a
    negative control that cannot be run locally is a negative control nobody has
    seen fail;
  * the tool stays inside the frozen input set. The 246-package MSYS2 lock is
    fixed and `pacman -S` is forbidden, so a validator must not acquire a
    dependency the lock does not already contain;
  * delay-loaded imports are read here. They are the ones that matter most for
    this runtime: DXVA2, D3D11, D3D12, NVENC and AMF are reached lazily, so a
    check that reads only the ordinary import directory concludes that a binary
    with hardware support has none.

Nothing here writes, patches or executes an image. It reads.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path

IMAGE_FILE_MACHINE_AMD64 = 0x8664
IMAGE_FILE_MACHINE_I386 = 0x014C
IMAGE_FILE_MACHINE_ARM64 = 0xAA64
PE32_PLUS_MAGIC = 0x20B
PE32_MAGIC = 0x10B

MACHINE_NAMES = {
    IMAGE_FILE_MACHINE_AMD64: "x86-64",
    IMAGE_FILE_MACHINE_I386: "x86",
    IMAGE_FILE_MACHINE_ARM64: "arm64",
}

DIRECTORY_IMPORT = 1
DIRECTORY_DELAY_IMPORT = 13


class PEError(Exception):
    """The bytes are not a PE image this reader will interpret."""


@dataclass
class Section:
    name: str
    virtual_address: int
    virtual_size: int
    raw_offset: int
    raw_size: int
    characteristics: int


@dataclass
class PEImage:
    path: str
    machine: int
    magic: int
    timestamp: int
    checksum: int
    characteristics: int
    dll_characteristics: int
    subsystem: int
    sections: list[Section] = field(default_factory=list)
    imports: dict[str, list[str]] = field(default_factory=dict)
    delay_imports: dict[str, list[str]] = field(default_factory=dict)

    @property
    def is_pe32_plus(self) -> bool:
        return self.magic == PE32_PLUS_MAGIC

    @property
    def is_amd64(self) -> bool:
        return self.machine == IMAGE_FILE_MACHINE_AMD64

    @property
    def machine_name(self) -> str:
        return MACHINE_NAMES.get(self.machine, f"0x{self.machine:04x}")

    def all_dlls(self) -> list[str]:
        """Every DLL this image needs, ordinary and delay-loaded, lowercased."""
        return sorted({d.lower() for d in (*self.imports, *self.delay_imports)})

    def to_dict(self) -> dict:
        return {
            "path": self.path,
            "machine": self.machine_name,
            "machineValue": f"0x{self.machine:04x}",
            "format": "PE32+" if self.is_pe32_plus else f"0x{self.magic:03x}",
            "timeDateStamp": self.timestamp,
            "checkSum": self.checksum,
            "subsystem": self.subsystem,
            "characteristics": f"0x{self.characteristics:04x}",
            "dllCharacteristics": f"0x{self.dll_characteristics:04x}",
            "sections": [s.name for s in self.sections],
            "imports": {k: sorted(v) for k, v in sorted(self.imports.items())},
            "delayImports": {k: sorted(v) for k, v in sorted(self.delay_imports.items())},
            "allDlls": self.all_dlls(),
        }


def _cstr(data: bytes, offset: int, limit: int = 512) -> str:
    end = data.find(b"\0", offset, offset + limit)
    if end < 0:
        raise PEError(f"unterminated string at 0x{offset:x}")
    return data[offset:end].decode("ascii", errors="replace")


def read(path: str | Path) -> PEImage:
    data = Path(path).read_bytes()
    if len(data) < 0x40 or data[:2] != b"MZ":
        raise PEError(f"{path}: no MZ header")
    (e_lfanew,) = struct.unpack_from("<I", data, 0x3C)
    if e_lfanew + 24 > len(data) or data[e_lfanew : e_lfanew + 4] != b"PE\0\0":
        raise PEError(f"{path}: no PE signature at 0x{e_lfanew:x}")

    coff = e_lfanew + 4
    machine, nsections, timestamp = struct.unpack_from("<HHI", data, coff)
    opt_size, characteristics = struct.unpack_from("<HH", data, coff + 16)
    opt = coff + 20
    (magic,) = struct.unpack_from("<H", data, opt)
    if magic == PE32_PLUS_MAGIC:
        checksum = struct.unpack_from("<I", data, opt + 64)[0]
        subsystem, dll_characteristics = struct.unpack_from("<HH", data, opt + 68)
        dir_offset = opt + 112
    elif magic == PE32_MAGIC:
        checksum = struct.unpack_from("<I", data, opt + 64)[0]
        subsystem, dll_characteristics = struct.unpack_from("<HH", data, opt + 68)
        dir_offset = opt + 96
    else:
        raise PEError(f"{path}: unknown optional-header magic 0x{magic:x}")

    (dir_count,) = struct.unpack_from("<I", data, dir_offset - 4)
    directories: list[tuple[int, int]] = []
    for i in range(min(dir_count, 16)):
        directories.append(struct.unpack_from("<II", data, dir_offset + i * 8))

    section_table = opt + opt_size
    sections: list[Section] = []
    for i in range(nsections):
        base = section_table + i * 40
        if base + 40 > len(data):
            raise PEError(f"{path}: section table runs past end of file")
        raw_name = data[base : base + 8].rstrip(b"\0")
        vsize, vaddr, rsize, roff = struct.unpack_from("<IIII", data, base + 8)
        (chars,) = struct.unpack_from("<I", data, base + 36)
        sections.append(
            Section(
                name=raw_name.decode("ascii", errors="replace"),
                virtual_address=vaddr,
                virtual_size=vsize,
                raw_offset=roff,
                raw_size=rsize,
                characteristics=chars,
            )
        )

    image = PEImage(
        path=str(path),
        machine=machine,
        magic=magic,
        timestamp=timestamp,
        checksum=checksum,
        characteristics=characteristics,
        dll_characteristics=dll_characteristics,
        subsystem=subsystem,
        sections=sections,
    )

    def rva_to_offset(rva: int) -> int | None:
        for s in sections:
            if s.virtual_address <= rva < s.virtual_address + max(s.virtual_size, s.raw_size):
                delta = rva - s.virtual_address
                if delta >= s.raw_size:
                    return None  # in a zero-filled tail; nothing on disk to read
                return s.raw_offset + delta
        return None

    thunk_size = 8 if magic == PE32_PLUS_MAGIC else 4
    ordinal_flag = 1 << (63 if magic == PE32_PLUS_MAGIC else 31)

    def read_names(thunk_rva: int) -> list[str]:
        names: list[str] = []
        off = rva_to_offset(thunk_rva)
        if off is None:
            return names
        while True:
            if off + thunk_size > len(data):
                break
            value = int.from_bytes(data[off : off + thunk_size], "little")
            if value == 0:
                break
            if value & ordinal_flag:
                names.append(f"#{value & 0xFFFF}")
            else:
                name_off = rva_to_offset(value & 0x7FFFFFFF)
                if name_off is not None:
                    try:
                        names.append(_cstr(data, name_off + 2))
                    except PEError:
                        pass
            off += thunk_size
        return names

    # --- ordinary imports ---------------------------------------------------
    if len(directories) > DIRECTORY_IMPORT:
        rva, _size = directories[DIRECTORY_IMPORT]
        off = rva_to_offset(rva) if rva else None
        while off is not None and off + 20 <= len(data):
            orig_thunk, _ts, _fwd, name_rva, first_thunk = struct.unpack_from("<IIIII", data, off)
            if not (orig_thunk or name_rva or first_thunk):
                break
            name_off = rva_to_offset(name_rva)
            if name_off is None:
                break
            dll = _cstr(data, name_off)
            image.imports[dll] = read_names(orig_thunk or first_thunk)
            off += 20

    # --- delay-loaded imports ------------------------------------------------
    if len(directories) > DIRECTORY_DELAY_IMPORT:
        rva, _size = directories[DIRECTORY_DELAY_IMPORT]
        off = rva_to_offset(rva) if rva else None
        image_base = 0
        if magic == PE32_PLUS_MAGIC:
            (image_base,) = struct.unpack_from("<Q", data, opt + 24)
        else:
            (image_base,) = struct.unpack_from("<I", data, opt + 28)
        while off is not None and off + 32 <= len(data):
            attrs, name_rva, _hmod, _iat, int_rva = struct.unpack_from("<IIIII", data, off)
            if not (name_rva or int_rva):
                break
            # Bit 0 of grAttrs says the addresses are RVAs. The original Visual
            # C++ layout stored virtual addresses instead; both appear in the
            # wild, and reading a VA as an RVA lands nowhere and reports a
            # binary with delay imports as having none.
            if not attrs & 1:
                name_rva = max(name_rva - image_base, 0)
                int_rva = max(int_rva - image_base, 0)
            name_off = rva_to_offset(name_rva)
            if name_off is None:
                break
            dll = _cstr(data, name_off)
            image.delay_imports[dll] = read_names(int_rva)
            off += 32

    return image


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Read PE headers and imports.")
    parser.add_argument("paths", nargs="+")
    parser.add_argument("--json", action="store_true", help="emit JSON")
    args = parser.parse_args(argv)

    results = []
    status = 0
    for p in args.paths:
        try:
            results.append(read(p).to_dict())
        except PEError as exc:
            print(f"{exc}", file=sys.stderr)
            status = 1
    if args.json:
        print(json.dumps(results, indent=2, sort_keys=False))
    else:
        for r in results:
            print(f"{r['path']}: {r['format']} {r['machine']} "
                  f"timeDateStamp={r['timeDateStamp']}")
            for dll in r["allDlls"]:
                kind = "delay" if dll in {k.lower() for k in r["delayImports"]} else "import"
                print(f"  {kind:6} {dll}")
    return status


if __name__ == "__main__":
    raise SystemExit(main())
