"""Stage only owner-approved Radio reporting files on guarded OT; never print secrets."""
import grp
import hashlib
import json
import os
from pathlib import Path
import secrets

ASSEMBLY_SHA = 'd48ac031ccc83ab241ba3f57e151645a8509c1eef9d6696d71d1a99e0ee948bd'
UNIT_SHA = '01cb8dd38677b89c77dc9ca7cad15fa0eaaf1f07e971ab8a05febab362e79eca'

if __name__ == '__main__':
    assert hashlib.sha256(Path('/opt/pizzawave/pizzad/pizzad.dll').read_bytes()).hexdigest() == ASSEMBLY_SHA, 'Backend drift'
    assert hashlib.sha256(Path('/etc/systemd/system/pizzad.service').read_bytes()).hexdigest() == UNIT_SHA, 'Service drift'
    password = Path('/etc/pizzawave/radio-health-mqtt.password')
    options = Path('/etc/pizzawave/radio-health-reporting.json')
    dropin = Path('/etc/systemd/system/pizzad.service.d/radio-health.conf')
    assert all(not p.exists() for p in [password, options, dropin]), 'Reporting file drift'
    gid = grp.getgrnam('pizzawave').gr_gid
    def write_private(path, text):
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o640)
        with os.fdopen(fd, 'w') as output:
            output.write(text)
        os.chown(path, 0, gid)
    write_private(password, secrets.token_urlsafe(48) + '\n')
    write_private(options, json.dumps({'enabled': False,
        'brokerHost': 'house-hass.whiteoaklabs.net', 'brokerPort': 8883,
        'username': 'whiteoak_radio_aor', 'passwordFile': str(password)}, indent=2) + '\n')
    dropin.parent.mkdir(exist_ok=True)
    dropin.write_text('[Service]\nEnvironment=PIZZAD_RADIO_HEALTH_REPORTING_CONFIG=/etc/pizzawave/radio-health-reporting.json\n')
    os.chmod(dropin, 0o644)
    print('Radio reporting files staged disabled; credentials remain target-local; enable only after TLS verification and recorded temporary broad-access approval')
