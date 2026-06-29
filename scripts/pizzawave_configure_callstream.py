#!/usr/bin/env python3
import argparse
import copy
import datetime
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description="Patch trunk-recorder callstream clients to local pizzad.")
    parser.add_argument("--config", default="/etc/trunk-recorder/config.json")
    parser.add_argument("--address", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=9123)
    args = parser.parse_args()

    path = Path(args.config)
    data = json.loads(path.read_text())
    original = copy.deepcopy(data)
    plugins = data.setdefault("plugins", [])
    callstream = None
    for plugin in plugins:
        if str(plugin.get("name", "")).lower() == "callstream":
            callstream = plugin
            break

    if callstream is None:
        callstream = {"name": "callstream", "library": "libcallstream.so"}
        plugins.append(callstream)

    clients = callstream.setdefault("clients", [])
    local = {"address": args.address, "port": args.port}
    clients[:] = [c for c in clients if c.get("address") == args.address or str(c.get("address", "")).startswith("127.")]
    if not any(c.get("address") == args.address and int(c.get("port", 0)) == args.port for c in clients):
        clients.append(local)

    if data == original:
        print(f"No changes needed: {path}")
        return

    backup = path.with_suffix(path.suffix + "." + datetime.datetime.now().strftime("%Y%m%d%H%M%S") + ".bak")
    backup.write_text(json.dumps(original, indent=2))
    path.write_text(json.dumps(data, indent=2) + "\n")
    print(f"Patched callstream client to {args.address}:{args.port}")
    print(f"Backup: {backup}")


if __name__ == "__main__":
    main()
