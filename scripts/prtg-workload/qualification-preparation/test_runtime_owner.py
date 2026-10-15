import hashlib
import struct
import tempfile
import unittest
import uuid
from pathlib import Path

from verify_runtime_owner import _assembly_mvid, verify


def _tiny_cli_assembly(mvid: uuid.UUID) -> bytes:
    """Build a minimal PE/CLI metadata image sufficient to exercise read-only MVID parsing."""
    version = b"v4.0\0\0\0\0"
    prefix = struct.pack("<IHHII", 0x424A5342, 1, 1, 0, len(version)) + version
    prefix += b"\0" * ((-len(prefix)) % 4) + struct.pack("<HH", 0, 3)
    table_stream = bytearray(38)
    struct.pack_into("<I", table_stream, 0, 0)
    table_stream[4:8] = bytes((2, 0, 0, 1))
    struct.pack_into("<Q", table_stream, 8, 1)
    struct.pack_into("<Q", table_stream, 16, 0)
    struct.pack_into("<I", table_stream, 24, 1)
    struct.pack_into("<HHHHH", table_stream, 28, 0, 1, 1, 0, 0)
    streams = [("#~", bytes(table_stream)), ("#Strings", b"\0module.dll\0"), ("#GUID", mvid.bytes_le)]
    headers = []
    for name, data in streams:
        encoded = name.encode("ascii") + b"\0"
        encoded += b"\0" * ((-len(encoded)) % 4)
        headers.append((encoded, data))
    data_offset = len(prefix) + sum(8 + len(name) for name, _ in headers)
    header_bytes = bytearray()
    payload = bytearray()
    for name, data in headers:
        header_bytes += struct.pack("<II", data_offset + len(payload), len(data)) + name
        payload += data
    metadata = prefix + header_bytes + payload

    image = bytearray(0x600)
    image[:2] = b"MZ"
    struct.pack_into("<I", image, 0x3C, 0x80)
    image[0x80:0x84] = b"PE\0\0"
    coff = 0x84
    struct.pack_into("<HHIIIHH", image, coff, 0x14C, 1, 0, 0, 0, 224, 0x210E)
    optional = coff + 20
    struct.pack_into("<H", image, optional, 0x10B)
    struct.pack_into("<I", image, optional + 92, 16)
    cli_directory = optional + 96 + 14 * 8
    struct.pack_into("<II", image, cli_directory, 0x2000, 72)
    section = optional + 224
    image[section:section + 8] = b".text\0\0\0"
    struct.pack_into("<IIII", image, section + 8, 0x400, 0x2000, 0x400, 0x200)
    cli_offset = 0x200
    struct.pack_into("<IHHII", image, cli_offset, 72, 2, 5, 0x2048, len(metadata))
    image[0x248:0x248 + len(metadata)] = metadata
    return bytes(image)


class RuntimeOwnerContractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.web_path = self.root / "LogForesight.Web.dll"
        self.core_path = self.root / "LogForesight.Core.dll"
        self.web_mvid = uuid.UUID("12345678-1234-5678-9abc-def012345678")
        self.core_mvid = uuid.UUID("87654321-4321-8765-cba9-876543210fed")
        self.web_path.write_bytes(_tiny_cli_assembly(self.web_mvid))
        self.core_path.write_bytes(_tiny_cli_assembly(self.core_mvid))
        (self.root / "Db").mkdir()
        self.database = self.root / "Db" / "logforesight.db"

    def tearDown(self):
        self.temp.cleanup()

    def request(self):
        root = str(self.root)
        database = str(self.database)
        web = str(self.web_path)
        core = str(self.core_path)
        tokens = ["dotnet", web, "--urls=http://127.0.0.1:5100",
                  f"--Storage:DataRoot={root}", "--Storage:Type=Sqlite",
                  f"--Storage:ConnectionString=Data Source={database};Pooling=False;Cache=Shared",
                  "--E2:ProviderDescriptorSha256=" + "B" * 64]
        return {
            "processId": 42, "processStartUtc": "2026-10-14T00:00:00Z", "processStartUtcTicks": 638960544000000000,
            "expectedPort": 5100, "expectedAddress": "127.0.0.1",
            "listeners": [{"address": "127.0.0.1", "port": 5100, "processId": 42}],
            "expectedAssembly": web, "assemblySha256": hashlib.sha256(self.web_path.read_bytes()).hexdigest().upper(),
            "expectedCoreAssembly": core,
            "coreAssemblySha256": hashlib.sha256(self.core_path.read_bytes()).hexdigest().upper(),
            "expectedDataRoot": root, "expectedProvider": "Sqlite", "expectedDatabase": database,
            "descriptorSha256": "B" * 64, "commandTokens": tokens,
            "runtimeMetadata": {
                "processId": 42, "processStartUtcTicks": 638960544000000000,
                "dataRoot": root, "provider": "Sqlite", "databasePath": database,
                "webAssemblyLocation": web, "webAssemblyMvid": str(self.web_mvid).upper(),
                "coreAssemblyLocation": core, "coreAssemblyMvid": str(self.core_mvid).upper(),
                "descriptorSha256": "B" * 64,
            },
        }

    def test_pe_metadata_mvid_is_read_without_loading_assembly(self):
        self.assertEqual(str(self.web_mvid).upper(), _assembly_mvid(str(self.web_path)))
        self.assertEqual(str(self.core_mvid).upper(), _assembly_mvid(str(self.core_path)))

    def test_exact_listener_runtime_and_configured_descriptor_pass(self):
        result = verify(self.request())
        self.assertEqual("round54-web-runtime-owner-v1", result["schema"])
        self.assertNotIn("commandTokens", result)
        self.assertEqual("B" * 64, result["descriptorSha256"])
        self.assertEqual(str(self.web_mvid).upper(), result["webAssemblyMvid"])
        self.assertEqual(str(self.core_mvid).upper(), result["coreAssemblyMvid"])

    def test_runtime_configuration_and_process_must_match_local_listener(self):
        request = self.request()
        request["runtimeMetadata"]["descriptorSha256"] = "C" * 64
        with self.assertRaisesRegex(ValueError, "runtime-loaded-descriptor"):
            verify(request)
        request = self.request()
        request["runtimeMetadata"]["processStartUtcTicks"] += 1
        with self.assertRaisesRegex(ValueError, "runtime-metadata-process"):
            verify(request)

    def test_loaded_paths_and_web_core_mvids_must_match_selected_files(self):
        request = self.request()
        request["runtimeMetadata"]["webAssemblyMvid"] = str(uuid.uuid4())
        with self.assertRaisesRegex(ValueError, "runtime-loaded-web-mvid"):
            verify(request)
        request = self.request()
        request["runtimeMetadata"]["coreAssemblyLocation"] += ".old"
        with self.assertRaisesRegex(ValueError, "runtime-loaded-core-path"):
            verify(request)

    def test_descriptor_argv_and_unique_listener_are_still_required(self):
        request = self.request()
        request["commandTokens"][-1] = "--E2:ProviderDescriptorSha256=" + "C" * 64
        with self.assertRaisesRegex(ValueError, "descriptor-hash"):
            verify(request)
        request = self.request()
        request["listeners"].append(dict(request["listeners"][0]))
        with self.assertRaisesRegex(ValueError, "foreign-or-ambiguous"):
            verify(request)

    def test_provider_and_database_argv_mismatches_are_rejected(self):
        request = self.request()
        request["commandTokens"][4] = "--Storage:Type=SqlServer"
        with self.assertRaisesRegex(ValueError, "provider-argv"):
            verify(request)
        request = self.request()
        request["commandTokens"][5] += ";Pooling=False"
        with self.assertRaisesRegex(ValueError, "database-connection"):
            verify(request)


if __name__ == "__main__":
    unittest.main()
