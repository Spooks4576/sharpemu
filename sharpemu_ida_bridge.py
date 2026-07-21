# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

"""Read-only HTTP bridge for querying the IDB open in IDA Pro.

Install this file as an IDAPython plugin or run it with File -> Script file.
All database access is marshalled onto IDA's main thread.
"""

from __future__ import annotations

import hmac
import json
import os
import threading
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Callable

import ida_bytes
import ida_funcs
import ida_ida
import ida_idaapi
import ida_idp
import ida_kernwin
import ida_lines
import ida_nalt
import ida_name
import ida_segment
import ida_xref
import idautils
import idc


DEFAULT_HOST = "0.0.0.0"
DEFAULT_PORT = 5731
MAX_REQUEST_BYTES = 4 * 1024 * 1024
MAX_READ_BYTES = 1024 * 1024
MAX_DISASSEMBLY_ITEMS = 512
MAX_MAPPED_ADDRESSES = 4096

_server: "IdaBridgeHttpServer | None" = None
_server_thread: threading.Thread | None = None


class QueryError(ValueError):
    pass


def _parse_address(value: Any, field: str = "address") -> int:
    if isinstance(value, bool):
        raise QueryError(f"'{field}' must be an integer or hexadecimal string.")
    if isinstance(value, int):
        address = value
    elif isinstance(value, str):
        try:
            address = int(value.strip(), 0)
        except ValueError as exc:
            raise QueryError(f"'{field}' is not a valid address: {value!r}") from exc
    else:
        raise QueryError(f"'{field}' must be an integer or hexadecimal string.")
    if address < 0 or address > 0xFFFFFFFFFFFFFFFF:
        raise QueryError(f"'{field}' is outside the 64-bit address range.")
    return address


def _inf_min_ea() -> int:
    getter = getattr(ida_ida, "inf_get_min_ea", None)
    if getter is not None:
        return int(getter())
    return int(ida_idaapi.get_inf_structure().min_ea)


def _inf_max_ea() -> int:
    getter = getattr(ida_ida, "inf_get_max_ea", None)
    if getter is not None:
        return int(getter())
    return int(ida_idaapi.get_inf_structure().max_ea)


def _clean_disassembly_line(address: int) -> str:
    line = idc.generate_disasm_line(address, 0) or ""
    return ida_lines.tag_remove(line)


def _name_at(address: int) -> str:
    name = ida_name.get_ea_name(address, ida_name.GN_VISIBLE)
    return name or ""


def _function_summary(address: int) -> dict[str, Any] | None:
    function = ida_funcs.get_func(address)
    if function is None:
        return None
    start = int(function.start_ea)
    end = int(function.end_ea)
    return {
        "start": f"0x{start:X}",
        "end": f"0x{end:X}",
        "size": max(0, end - start),
        "name": ida_funcs.get_func_name(start) or _name_at(start),
        "offset": max(0, address - start),
    }


def _compact_lookup(address: int) -> dict[str, Any]:
    item = int(ida_bytes.get_item_head(address))
    if item == ida_idaapi.BADADDR:
        item = address
    function = _function_summary(address)
    segment = ida_segment.getseg(address)
    return {
        "address": f"0x{address:X}",
        "mapped": segment is not None,
        "segment": ida_segment.get_segm_name(segment) if segment is not None else "",
        "name": _name_at(address),
        "itemAddress": f"0x{item:X}",
        "instruction": _clean_disassembly_line(item) if segment is not None else "",
        "function": function,
    }


def _health() -> dict[str, Any]:
    image_base = int(ida_nalt.get_imagebase())
    min_ea = _inf_min_ea()
    max_ea = _inf_max_ea()
    try:
        import ida_hexrays

        decompiler = bool(ida_hexrays.init_hexrays_plugin())
    except Exception:
        decompiler = False
    return {
        "ok": True,
        "readOnly": True,
        "database": ida_nalt.get_root_filename(),
        "inputFile": ida_nalt.get_input_file_path(),
        "processor": ida_idp.get_idp_name(),
        "imageBase": f"0x{image_base:X}",
        "minAddress": f"0x{min_ea:X}",
        "maxAddress": f"0x{max_ea:X}",
        "imageSize": max(0, max_ea - image_base),
        "decompilerAvailable": decompiler,
    }


def _lookup(request: dict[str, Any]) -> dict[str, Any]:
    return _compact_lookup(_parse_address(request.get("address")))


def _disassemble(request: dict[str, Any]) -> dict[str, Any]:
    address = _parse_address(request.get("address"))
    count = int(request.get("count", 32))
    if count < 1 or count > MAX_DISASSEMBLY_ITEMS:
        raise QueryError(f"'count' must be between 1 and {MAX_DISASSEMBLY_ITEMS}.")

    function = ida_funcs.get_func(address)
    end = int(function.end_ea) if function is not None else _inf_max_ea()
    cursor = int(ida_bytes.get_item_head(address))
    if cursor == ida_idaapi.BADADDR:
        cursor = address
    items: list[dict[str, Any]] = []
    for _ in range(count):
        if cursor == ida_idaapi.BADADDR or cursor >= end:
            break
        size = max(1, int(ida_bytes.get_item_size(cursor)))
        raw = ida_bytes.get_bytes(cursor, size) or b""
        items.append({
            "address": f"0x{cursor:X}",
            "size": size,
            "bytes": raw.hex().upper(),
            "mnemonic": idc.print_insn_mnem(cursor) or "",
            "operands": [
                operand
                for index in range(8)
                if (operand := idc.print_operand(cursor, index))
            ],
            "text": _clean_disassembly_line(cursor),
        })
        next_address = int(ida_bytes.next_head(cursor, end))
        if next_address == ida_idaapi.BADADDR or next_address <= cursor:
            break
        cursor = next_address
    return {
        "address": f"0x{address:X}",
        "function": _function_summary(address),
        "items": items,
    }


def _decompile(request: dict[str, Any]) -> dict[str, Any]:
    address = _parse_address(request.get("address"))
    function = ida_funcs.get_func(address)
    if function is None:
        raise QueryError(f"0x{address:X} is not inside an IDA function.")
    try:
        import ida_hexrays

        if not ida_hexrays.init_hexrays_plugin():
            raise QueryError("The Hex-Rays decompiler is unavailable for this database.")
        pseudocode = str(ida_hexrays.decompile(function.start_ea))
    except QueryError:
        raise
    except Exception as exc:
        raise QueryError(f"Decompilation failed at 0x{address:X}: {exc}") from exc
    return {
        "address": f"0x{address:X}",
        "function": _function_summary(address),
        "pseudocode": pseudocode,
    }


def _xref_item(xref: Any, target: int) -> dict[str, Any]:
    source = int(xref.frm)
    destination = int(xref.to)
    other = source if destination == target else destination
    return {
        "from": f"0x{source:X}",
        "to": f"0x{destination:X}",
        "type": int(xref.type),
        "isCode": bool(xref.iscode),
        "other": _compact_lookup(other),
    }


def _xrefs(request: dict[str, Any]) -> dict[str, Any]:
    address = _parse_address(request.get("address"))
    direction = str(request.get("direction", "both")).lower()
    if direction not in {"to", "from", "both"}:
        raise QueryError("'direction' must be 'to', 'from', or 'both'.")
    result: dict[str, Any] = {"address": f"0x{address:X}"}
    if direction in {"to", "both"}:
        result["to"] = [_xref_item(xref, address) for xref in idautils.XrefsTo(address, 0)]
    if direction in {"from", "both"}:
        result["from"] = [_xref_item(xref, address) for xref in idautils.XrefsFrom(address, 0)]
    return result


def _read_bytes(request: dict[str, Any]) -> dict[str, Any]:
    address = _parse_address(request.get("address"))
    size = int(request.get("size", 16))
    if size < 1 or size > MAX_READ_BYTES:
        raise QueryError(f"'size' must be between 1 and {MAX_READ_BYTES}.")
    raw = ida_bytes.get_bytes(address, size)
    if raw is None:
        raise QueryError(f"IDA could not read {size} byte(s) at 0x{address:X}.")
    return {"address": f"0x{address:X}", "size": len(raw), "bytes": raw.hex().upper()}


def _map_addresses(request: dict[str, Any]) -> dict[str, Any]:
    values = request.get("addresses")
    if not isinstance(values, list):
        raise QueryError("'addresses' must be a JSON array.")
    if len(values) > MAX_MAPPED_ADDRESSES:
        raise QueryError(f"At most {MAX_MAPPED_ADDRESSES} addresses may be mapped per request.")
    return {"items": [_compact_lookup(_parse_address(value, "addresses[]")) for value in values]}


_QUERY_HANDLERS: dict[str, Callable[[dict[str, Any]], dict[str, Any]]] = {
    "health": lambda _: _health(),
    "lookup": _lookup,
    "disassemble": _disassemble,
    "decompile": _decompile,
    "xrefs": _xrefs,
    "read-bytes": _read_bytes,
    "map-addresses": _map_addresses,
}


def execute_query(request: dict[str, Any]) -> dict[str, Any]:
    if not isinstance(request, dict):
        raise QueryError("The request body must be a JSON object.")
    command = str(request.get("command", "")).strip().lower()
    handler = _QUERY_HANDLERS.get(command)
    if handler is None:
        raise QueryError(
            "Unknown command. Available commands: " + ", ".join(sorted(_QUERY_HANDLERS))
        )
    return handler(request)


def _execute_on_ida_thread(request: dict[str, Any]) -> dict[str, Any]:
    result: dict[str, Any] = {}

    def invoke() -> int:
        try:
            result["value"] = execute_query(request)
        except Exception as exc:
            result["error"] = exc
        return 1

    ida_kernwin.execute_sync(invoke, ida_kernwin.MFF_READ)
    error = result.get("error")
    if error is not None:
        raise error
    return result["value"]


class IdaBridgeHttpServer(ThreadingHTTPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, address: tuple[str, int], token: str) -> None:
        super().__init__(address, IdaBridgeRequestHandler)
        self.token = token


class IdaBridgeRequestHandler(BaseHTTPRequestHandler):
    server: IdaBridgeHttpServer

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        if self.path != "/health":
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "Unknown endpoint."})
            return
        if not self._authorized():
            return
        self._handle_query({"command": "health"})

    def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        if self.path != "/api/query":
            self._send_json(HTTPStatus.NOT_FOUND, {"error": "Unknown endpoint."})
            return
        if not self._authorized():
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length < 1 or length > MAX_REQUEST_BYTES:
                raise QueryError(f"Request size must be between 1 and {MAX_REQUEST_BYTES} bytes.")
            request = json.loads(self.rfile.read(length).decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError, QueryError, ValueError) as exc:
            self._send_json(HTTPStatus.BAD_REQUEST, {"error": str(exc)})
            return
        self._handle_query(request)

    def _authorized(self) -> bool:
        if not self.server.token:
            return True
        authorization = self.headers.get("Authorization", "")
        supplied = authorization[7:] if authorization.startswith("Bearer ") else ""
        if not supplied:
            supplied = self.headers.get("X-SharpEmu-Token", "")
        if hmac.compare_digest(supplied, self.server.token):
            return True
        self._send_json(
            HTTPStatus.UNAUTHORIZED,
            {"error": "A valid SharpEmu IDA bridge token is required."},
        )
        return False

    def _handle_query(self, request: dict[str, Any]) -> None:
        try:
            response = _execute_on_ida_thread(request)
            self._send_json(HTTPStatus.OK, response)
        except QueryError as exc:
            self._send_json(HTTPStatus.BAD_REQUEST, {"error": str(exc)})
        except Exception as exc:
            self._send_json(HTTPStatus.INTERNAL_SERVER_ERROR, {"error": str(exc)})

    def _send_json(self, status: HTTPStatus, value: Any) -> None:
        body = json.dumps(value, ensure_ascii=False).encode("utf-8")
        self.send_response(status.value)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format_string: str, *args: Any) -> None:
        ida_kernwin.msg("[SharpEmu IDA Bridge] " + (format_string % args) + "\n")


def start_server(
    host: str | None = None,
    port: int | None = None,
    token: str | None = None,
) -> dict[str, Any]:
    """Start the bridge. Safe to call from IDA's Python console."""
    global _server, _server_thread
    if _server is not None:
        actual_host, actual_port = _server.server_address[:2]
        return {"running": True, "host": actual_host, "port": actual_port}

    resolved_host = host or os.environ.get("SHARPEMU_IDA_BRIDGE_HOST", DEFAULT_HOST)
    resolved_port = port or int(os.environ.get("SHARPEMU_IDA_BRIDGE_PORT", DEFAULT_PORT))
    resolved_token = token if token is not None else os.environ.get("SHARPEMU_IDA_BRIDGE_TOKEN", "")
    server = IdaBridgeHttpServer((resolved_host, resolved_port), resolved_token)
    thread = threading.Thread(
        target=server.serve_forever,
        name="SharpEmu-IDA-Bridge",
        daemon=True,
    )
    _server = server
    _server_thread = thread
    thread.start()
    actual_host, actual_port = server.server_address[:2]
    ida_kernwin.msg(
        f"[SharpEmu IDA Bridge] Listening on http://{actual_host}:{actual_port} "
        f"(token={'required' if resolved_token else 'not required'}, read-only)\n"
    )
    return {"running": True, "host": actual_host, "port": actual_port}


def stop_server() -> None:
    global _server, _server_thread
    server = _server
    thread = _server_thread
    _server = None
    _server_thread = None
    if server is None:
        return
    server.shutdown()
    server.server_close()
    if thread is not None and thread is not threading.current_thread():
        thread.join(timeout=2.0)
    ida_kernwin.msg("[SharpEmu IDA Bridge] Stopped.\n")


class SharpEmuIdaBridgePlugin(ida_idaapi.plugin_t):
    flags = ida_idaapi.PLUGIN_FIX
    comment = "Read-only remote IDB queries for SharpEmu debugging"
    help = "Run to toggle the SharpEmu IDA HTTP bridge."
    wanted_name = "SharpEmu IDA Bridge"
    wanted_hotkey = ""

    def init(self) -> int:
        return ida_idaapi.PLUGIN_OK

    def run(self, argument: int) -> None:
        _ = argument
        if _server is None:
            try:
                start_server()
            except Exception as exc:
                ida_kernwin.warning(f"SharpEmu IDA Bridge could not start: {exc}")
        else:
            stop_server()

    def term(self) -> None:
        stop_server()


def PLUGIN_ENTRY() -> SharpEmuIdaBridgePlugin:
    return SharpEmuIdaBridgePlugin()


if __name__ == "__main__":
    start_server()
