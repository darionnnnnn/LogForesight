"""Exact argv/listener contract used by Prepare-Qualification.ps1."""
from __future__ import annotations

import hashlib
import ipaddress
import json
import re
import struct
import sys
import uuid
from pathlib import Path, PureWindowsPath


def _canonical(path: str) -> str:
    return str(PureWindowsPath(path)).casefold()


def _option(tokens: list[str], name: str) -> list[str]:
    values = []
    prefix = name + "="
    for i, token in enumerate(tokens):
        if token.startswith(prefix):
            values.append(token[len(prefix):])
        elif token == name:
            if i + 1 >= len(tokens):
                raise ValueError("runtime option has no value")
            values.append(tokens[i + 1])
    return values


def _align4(value: int) -> int:
    return (value + 3) & ~3


def _assembly_mvid(path: str) -> str:
    """Read the manifest MVID from PE metadata without loading or executing the assembly."""
    image = Path(path).read_bytes()

    def u16(offset: int) -> int:
        return struct.unpack_from("<H", image, offset)[0]

    def u32(offset: int) -> int:
        return struct.unpack_from("<I", image, offset)[0]

    def rva_offset(rva: int) -> int:
        for virtual_address, virtual_size, raw_pointer, raw_size in sections:
            span = max(virtual_size, raw_size)
            if virtual_address <= rva < virtual_address + span:
                delta = rva - virtual_address
                if delta >= raw_size:
                    break
                return raw_pointer + delta
        raise ValueError("assembly-metadata-rva-invalid")

    if len(image) < 0x100 or image[:2] != b"MZ":
        raise ValueError("assembly-pe-header-invalid")
    pe = u32(0x3C)
    if image[pe:pe + 4] != b"PE\0\0":
        raise ValueError("assembly-pe-header-invalid")
    coff = pe + 4
    section_count = u16(coff + 2)
    optional_size = u16(coff + 16)
    optional = coff + 20
    magic = u16(optional)
    if magic == 0x10B:
        directory_start, directory_count_offset = optional + 96, optional + 92
    elif magic == 0x20B:
        directory_start, directory_count_offset = optional + 112, optional + 108
    else:
        raise ValueError("assembly-pe-optional-header-invalid")
    if u32(directory_count_offset) <= 14 or directory_start + 15 * 8 > optional + optional_size:
        raise ValueError("assembly-cli-header-missing")
    cli_rva = u32(directory_start + 14 * 8)
    if cli_rva == 0:
        raise ValueError("assembly-cli-header-missing")
    section_table = optional + optional_size
    sections = []
    for index in range(section_count):
        offset = section_table + index * 40
        sections.append((u32(offset + 12), u32(offset + 8), u32(offset + 20), u32(offset + 16)))
    cli = rva_offset(cli_rva)
    metadata_rva = u32(cli + 8)
    metadata_size = u32(cli + 12)
    metadata = rva_offset(metadata_rva)
    if metadata_size < 20 or image[metadata:metadata + 4] != b"BSJB":
        raise ValueError("assembly-cli-metadata-invalid")
    version_length = u32(metadata + 12)
    cursor = _align4(metadata + 16 + version_length)
    stream_count = u16(cursor + 2)
    cursor += 4
    streams: dict[str, tuple[int, int]] = {}
    for _ in range(stream_count):
        relative, size = struct.unpack_from("<II", image, cursor)
        name_start = cursor + 8
        name_end = image.index(0, name_start, min(name_start + 32, metadata + metadata_size))
        name = image[name_start:name_end].decode("ascii")
        streams[name] = (metadata + relative, size)
        cursor = _align4(name_end + 1)
    tables = streams.get("#~") or streams.get("#-")
    guid_heap = streams.get("#GUID")
    if tables is None or guid_heap is None:
        raise ValueError("assembly-module-metadata-missing")
    tables_start, tables_size = tables
    if tables_size < 28:
        raise ValueError("assembly-module-table-invalid")
    heap_sizes = image[tables_start + 6]
    valid_tables = struct.unpack_from("<Q", image, tables_start + 8)[0]
    if not valid_tables & 1:
        raise ValueError("assembly-module-row-missing")
    row_count = u32(tables_start + 24)
    if row_count < 1:
        raise ValueError("assembly-module-row-missing")
    string_width = 4 if heap_sizes & 1 else 2
    guid_width = 4 if heap_sizes & 2 else 2
    mvid_index_offset = tables_start + 28 + 2 + string_width
    mvid_index = u32(mvid_index_offset) if guid_width == 4 else u16(mvid_index_offset)
    guid_start, guid_size = guid_heap
    guid_offset = guid_start + (mvid_index - 1) * 16
    if mvid_index < 1 or guid_offset + 16 > guid_start + guid_size:
        raise ValueError("assembly-module-mvid-invalid")
    return str(uuid.UUID(bytes_le=image[guid_offset:guid_offset + 16])).upper()


def verify(request: dict) -> dict:
    pid = int(request["processId"])
    if pid <= 0 or int(request["processStartUtcTicks"]) <= 0:
        raise ValueError("listener process identity is incomplete")
    port = int(request["expectedPort"])
    address = str(request["expectedAddress"])
    expected_ip = ipaddress.ip_address(address)
    listeners = request["listeners"]
    if len(listeners) != 1:
        raise ValueError("foreign-or-ambiguous-listener")
    listener = listeners[0]
    if int(listener["port"]) != port or int(listener["processId"]) != pid:
        raise ValueError("foreign-or-ambiguous-listener")
    try:
        live_ip = ipaddress.ip_address(str(listener["address"]))
    except ValueError as exc:
        raise ValueError("listener-address-invalid") from exc
    if not live_ip.is_loopback or live_ip != expected_ip:
        raise ValueError("listener-is-not-the-explicit-loopback-address")

    tokens = request["commandTokens"]
    if not isinstance(tokens, list) or not tokens:
        raise ValueError("runtime-command-tokens-missing")
    assembly_tokens = [t for t in tokens if re.search(r"(?i)(?:^|[\\/])LogForesight\.Web\.dll$", t)]
    if len(assembly_tokens) != 1 or _canonical(assembly_tokens[0]) != _canonical(request["expectedAssembly"]):
        raise ValueError("web-assembly-argv-token-mismatch")
    if not re.fullmatch(r"[A-Fa-f0-9]{64}", str(request["assemblySha256"])):
        raise ValueError("web-assembly-hash-invalid")

    root = _option(tokens, "--Storage:DataRoot")
    provider = _option(tokens, "--Storage:Type")
    connection = _option(tokens, "--Storage:ConnectionString")
    descriptor = _option(tokens, "--E2:ProviderDescriptorSha256")
    if len(root) != 1 or _canonical(root[0]) != _canonical(request["expectedDataRoot"]):
        raise ValueError("runtime-data-root-argv-mismatch")
    if provider != [request["expectedProvider"]]:
        raise ValueError("runtime-provider-argv-mismatch")
    expected_connection = f"Data Source={request['expectedDatabase']};Pooling=False;Cache=Shared"
    if connection != [expected_connection]:
        raise ValueError("runtime-database-connection-argv-mismatch")
    expected_descriptor_hash = str(request["descriptorSha256"]).upper()
    if not re.fullmatch(r"[A-F0-9]{64}", expected_descriptor_hash):
        raise ValueError("provider-descriptor-hash-invalid")
    if len(descriptor) != 1 or descriptor[0].upper() != expected_descriptor_hash:
        raise ValueError("provider-descriptor-hash-argv-mismatch")
    runtime = request.get("runtimeMetadata")
    if not isinstance(runtime, dict):
        raise ValueError("runtime-metadata-missing")
    core_path = str(request["expectedCoreAssembly"])
    disk_web_mvid = _assembly_mvid(str(request["expectedAssembly"]))
    disk_core_mvid = _assembly_mvid(core_path)
    if int(runtime.get("processId", 0)) != pid or int(runtime.get("processStartUtcTicks", 0)) != int(request["processStartUtcTicks"]):
        raise ValueError("runtime-metadata-process-mismatch")
    if _canonical(str(runtime.get("dataRoot", ""))) != _canonical(str(request["expectedDataRoot"])):
        raise ValueError("runtime-metadata-data-root-mismatch")
    if runtime.get("provider") != request["expectedProvider"]:
        raise ValueError("runtime-metadata-provider-mismatch")
    if _canonical(str(runtime.get("databasePath", ""))) != _canonical(str(request["expectedDatabase"])):
        raise ValueError("runtime-metadata-database-mismatch")
    if _canonical(str(runtime.get("webAssemblyLocation", ""))) != _canonical(str(request["expectedAssembly"])):
        raise ValueError("runtime-loaded-web-path-mismatch")
    if _canonical(str(runtime.get("coreAssemblyLocation", ""))) != _canonical(core_path):
        raise ValueError("runtime-loaded-core-path-mismatch")
    if str(runtime.get("webAssemblyMvid", "")).upper() != disk_web_mvid:
        raise ValueError("runtime-loaded-web-mvid-mismatch")
    if str(runtime.get("coreAssemblyMvid", "")).upper() != disk_core_mvid:
        raise ValueError("runtime-loaded-core-mvid-mismatch")
    if str(runtime.get("descriptorSha256", "")).upper() != expected_descriptor_hash:
        raise ValueError("runtime-loaded-descriptor-hash-mismatch")
    token_hash = hashlib.sha256("\0".join(tokens).encode("utf-8")).hexdigest().upper()
    return {
        "schema": "round54-web-runtime-owner-v1",
        "processId": pid,
        "processStartUtc": request["processStartUtc"],
        "processStartUtcTicks": int(request["processStartUtcTicks"]),
        "localAddress": str(live_ip),
        "port": port,
        "assemblyPath": str(PureWindowsPath(assembly_tokens[0])),
        "assemblySha256": str(request["assemblySha256"]).upper(),
        "webAssemblyMvid": disk_web_mvid,
        "coreAssemblyPath": str(PureWindowsPath(core_path)),
        "coreAssemblySha256": str(request["coreAssemblySha256"]).upper(),
        "coreAssemblyMvid": disk_core_mvid,
        "commandTokenCount": len(tokens),
        "commandTokensSha256": token_hash,
        "dataRoot": str(PureWindowsPath(root[0])),
        "provider": provider[0],
        "databasePath": str(PureWindowsPath(request["expectedDatabase"])),
        "descriptorSha256": expected_descriptor_hash,
    }


if __name__ == "__main__":
    try:
        print(json.dumps(verify(json.load(sys.stdin)), separators=(",", ":")))
    except Exception as exc:
        print(f"E2 runtime ownership check failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        raise SystemExit(2)
