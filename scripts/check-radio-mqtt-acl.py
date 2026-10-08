# Diagnostic probe only: acknowledgements do not prove forbidden delivery is blocked.
# Follow-up acceptance also requires subscriber-side delivery verification.
"""Attended TLS/MQTT v5 denial check; no equipment/discovery topics or fake health."""
import json
from pathlib import Path
import socket
import ssl

options = json.loads(Path('/etc/pizzawave/radio-health-reporting.json').read_text())
password = Path(options['passwordFile']).read_text().strip()
def string(text):
    data = text.encode()
    return len(data).to_bytes(2, 'big') + data
def send(sock, header, body):
    count = len(body)
    length = bytearray()
    while True:
        part = count % 128
        count //= 128
        length.append(part | (128 if count else 0))
        if not count: break
    sock.sendall(bytes([header]) + length + body)
def exact(sock, count):
    result = b''
    while len(result) < count:
        chunk = sock.recv(count-len(result))
        assert chunk, 'Broker closed during verification'
        result += chunk
    return result
def receive(sock):
    header = exact(sock, 1)[0]
    length, multiplier = 0, 1
    while True:
        part = exact(sock, 1)[0]
        length += (part & 127)*multiplier
        if not part & 128: break
        multiplier *= 128
        assert multiplier <= 128**3, 'Invalid MQTT length'
    assert length <= 4096, 'Unexpected verification response size'
    return header, exact(sock, length)

with ssl.create_default_context().wrap_socket(socket.create_connection(
    (options['brokerHost'], options['brokerPort']), timeout=10),
    server_hostname=options['brokerHost']) as sock:
    body = string('MQTT') + bytes([5, 0xc2, 0, 60, 0])
    body += string('whiteoak-radio-acl-check') + string(options['username']) + string(password)
    send(sock, 0x10, body)
    header, response = receive(sock)
    assert header == 0x20 and response[1] == 0, 'Restricted identity connection failed'
    for packet_id, topic in enumerate([
        'whiteoak/health/v1/aor/radio/acl-verification-probe',
        'whiteoak/health/v1/aor/acl-verification-probe/summary'], start=1):
        # Non-retained inert probes in the health namespace only. Never equipment
        # commands, HA discovery, real area state, recordings, or subscriber data.
        body = string(topic) + packet_id.to_bytes(2, 'big') + b'\0acl-verification'
        send(sock, 0x32, body)
        header, response = receive(sock)
        assert header == 0x40 and len(response) >= 3 and response[2] == 0x87, f'Unexpected write authorization: frame={header:#x}, bytes={len(response)}, reason={response[2] if len(response)>2 else 0:#x}'
    send(sock, 0x82, b'\0\x03\0' + string('whiteoak/health/v1/aor/radio/summary') + b'\0')
    header, response = receive(sock)
    assert header == 0x90 and response[-1] == 0x87, 'Unexpected subscribe authorization'
    send(sock, 0xe0, b'')
print('TLS identity verified; both unauthorized writes and read subscription denied')
