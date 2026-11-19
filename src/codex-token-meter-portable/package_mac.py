import hashlib
import pathlib
import plistlib
import struct
import sys
import zipfile


def unpack(fmt, data, offset=0):
    return struct.unpack_from(fmt, data, offset)


def verify_signature(path, expected_cpu, info=None, resources=None):
    image = path.read_bytes()
    magic, cpu, _, _, commands, _, _, _ = unpack("<8I", image)
    if magic != 0xFEEDFACF or cpu != expected_cpu:
        raise ValueError(f"Unexpected Mach-O architecture in {path.name}")
    cursor = 32
    sign_position = sign_length = 0
    for _ in range(commands):
        kind, size = unpack("<II", image, cursor)
        if kind == 0x1D:
            sign_position, sign_length = unpack("<II", image, cursor + 8)
        cursor += size
    data = image[sign_position : sign_position + sign_length]
    marker, _, count = unpack(">III", data)
    if marker != 0xFADE0CC0:
        raise ValueError("Missing Mach-O signature")
    checked = False
    for slot in range(count):
        kind, offset = unpack(">II", data, 12 + slot * 8)
        if kind not in (0, 0x1000):
            continue
        directory = data[offset:]
        marker, _, version, flags, hashes, identifier, special, pages, limit = unpack(">9I", directory)
        if marker != 0xFADE0C02 or not (flags & 0x02):
            raise ValueError("Missing ad-hoc CodeDirectory")
        length, algorithm, _, exponent = unpack("4B", directory, 36)
        convert = {1: hashlib.sha1, 2: hashlib.sha256, 3: hashlib.sha256, 4: hashlib.sha384}[algorithm]

        def digest(content):
            return convert(content).digest()[:length]

        if version >= 0x20300 and not limit:
            limit = unpack(">Q", directory, 56)[0]
        page_size = 1 << exponent if exponent else limit
        if pages != (limit + page_size - 1) // page_size or limit > sign_position:
            raise ValueError("Invalid CodeDirectory page coverage")
        for index, position in enumerate(range(0, limit, page_size)):
            start = hashes + index * length
            if digest(image[position : min(position + page_size, limit)]) != directory[start : start + length]:
                raise ValueError(f"Signature page hash mismatch at page {index}")
        for number, content in ((1, info), (3, resources)):
            if content is None:
                continue
            start = hashes - number * length
            if special < number or digest(content) != directory[start : start + length]:
                raise ValueError(f"Signature resource hash mismatch for slot {number}")
        if info and directory[identifier : directory.index(0, identifier)].decode() != "local.codex-token-meter.desktop":
            raise ValueError("Unexpected signature identifier")
        checked = True
    if not checked:
        raise ValueError("No verifiable CodeDirectory")

    if info:
        bundle_marker = bytes.fromhex("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae")
        marker = image.find(bundle_marker)
        if marker < 8:
            raise ValueError("Missing .NET single-file app marker")
        manifest = unpack("<Q", image, marker - 8)[0]
        if not marker < manifest < sign_position:
            raise ValueError("Invalid .NET bundle manifest offset")
        major, _, entries = unpack("<3I", image, manifest)
        if major < 6 or entries < 50 or b"CodexTokenMeter.dll" not in image[manifest:sign_position]:
            raise ValueError("Invalid .NET managed and runtime entries")


def add(archive, arc_name, data, mode=0o100644):
    entry = zipfile.ZipInfo(arc_name)
    entry.create_system = 3
    entry.external_attr = mode << 16
    entry.compress_type = zipfile.ZIP_DEFLATED
    archive.writestr(entry, data, compresslevel=5)


def main():
    root, destination, readme, architecture = sys.argv[1:]
    root = pathlib.Path(root)
    destination = pathlib.Path(destination)
    info = (root / "Contents/Info.plist").read_bytes()
    metadata = plistlib.loads(info)
    resources = (root / "Contents/_CodeSignature/CodeResources").read_bytes()
    if metadata["LSMinimumSystemVersion"] != "14.0" or metadata["CFBundleExecutable"] != "CodexTokenMeter":
        raise ValueError("Invalid app bundle metadata")
    cpu = {"arm64": 0x0100000C, "x64": 0x01000007}[architecture]
    executable = root / "Contents/MacOS/CodexTokenMeter"
    verify_signature(executable, cpu, info, resources)
    if executable.stat().st_size < 50_000_000:
        raise ValueError("Self-contained .NET payload is missing")
    native = root / "Contents/MacOS"
    for library in ("libAvaloniaNative.dylib", "libSkiaSharp.dylib", "libHarfBuzzSharp.dylib"):
        if not (native / library).is_file():
            raise FileNotFoundError(library)

    with zipfile.ZipFile(destination, "w") as archive:
        for path in sorted(root.rglob("*")):
            if path.is_file():
                logical = path.relative_to(root).as_posix()
                mode = 0o100755 if logical.startswith("Contents/MacOS/") else 0o100644
                add(archive, root.name + "/" + logical, path.read_bytes(), mode)
        add(archive, "安装说明.txt", pathlib.Path(readme).read_bytes())
    with zipfile.ZipFile(destination) as archive:
        if archive.testzip() is not None:
            raise ValueError("Archive CRC mismatch")
        if archive.getinfo(root.name + "/Contents/MacOS/CodexTokenMeter").external_attr >> 16 != 0o100755:
            raise ValueError("Executable bit is missing")
    print(f"Verified {architecture} Mach-O, ad-hoc code pages, sealed bundle metadata and ZIP ({destination.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
