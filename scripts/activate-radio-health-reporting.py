"""Activate the owner's temporary broad-access exception after trusted TLS verification."""
import hashlib
import json
from pathlib import Path
import socket
import ssl
import sys

assert sys.argv[1:] == ['--approved-temporary-broad-access'], 'Explicit exception required'
assert hashlib.sha256(Path('/etc/systemd/system/pizzad.service').read_bytes()).hexdigest() == '01cb8dd38677b89c77dc9ca7cad15fa0eaaf1f07e971ab8a05febab362e79eca', 'Service drift'
p = Path('/etc/pizzawave/radio-health-reporting.json')
config = json.loads(p.read_text())
assert config == {'enabled': False, 'brokerHost': 'house-hass.whiteoaklabs.net', 'brokerPort': 8883,
                  'username': 'whiteoak_radio_aor', 'passwordFile': '/etc/pizzawave/radio-health-mqtt.password'}, 'Reporting configuration drift'
assert p.stat().st_mode & 0o777 == 0o640 and p.stat().st_uid == 0, 'Configuration ownership drift'
with socket.create_connection((config['brokerHost'], config['brokerPort']), timeout=5) as connection:
    with ssl.create_default_context().wrap_socket(connection, server_hostname=config['brokerHost']) as secure:
        print('Broker certificate trusted:', secure.version())
config['enabled'] = True
with p.open('w') as output:
    output.write(json.dumps(config, indent=2) + '\n')
print('Radio reporting enabled under recorded temporary broad-access exception; restart required')
