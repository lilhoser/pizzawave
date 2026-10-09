"""Push bounded local SDR health over its existing restricted NAS SFTP identity."""
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import urllib.request

MAX_BYTES = 4096
LINK = 'http://100.105.110.92:8080/?page=system&tab=recommendations'


def stamp(value):
    return value.isoformat()


def api_time(value):
    # .NET emits seven fractional digits; DSM Python 3.8 accepts only three
    # or six. Preserve the instant with the interoperable microsecond format.
    if value is None or value == '':
        return None
    return datetime.fromisoformat(value.replace('Z', '+00:00')).isoformat(timespec='microseconds')


def local_api(route):
    request = urllib.request.Request('http://127.0.0.1:8080/api/v1/' + route)
    token = Path('/etc/pizzawave/pizzad.token')
    if token.is_file():
        request.add_header('Authorization', 'Bearer ' + token.read_text().strip())
    with urllib.request.urlopen(request, timeout=20) as response:
        if route == 'system/recommendations':
            result = current_items(response)
            result['readAtUtc'] = stamp(datetime.now(timezone.utc))
            return result
        raw = response.read(4*1024*1024+1)
    if len(raw) > 4*1024*1024:
        raise ValueError('Local response exceeds limit')
    return json.loads(raw)


def current_items(stream):
    # The old SDR API includes many megabytes of RF history after current items.
    # Use the established incremental parser, project only public scalar fields,
    # and close the loopback response when the current array ends. No history
    # object is constructed and no raw response leaves this process.
    sys.path.insert(0, str(Path(__file__).with_name('ijson.whl')))
    from ijson.backends.python import parse
    fields={'id','kind','severity','title','workflowStatus','activityState','findingId','lastSeenUtc'}
    items=[]
    item=None
    consumed=0
    class Limited:
        def read(self,size=-1):
            nonlocal consumed
            if size<0: size=4096
            raw=stream.read(min(size,4096));consumed+=len(raw)
            if consumed>8*1024*1024: raise ValueError('Current recommendation limit')
            return raw
    for prefix,event,value in parse(Limited(),buf_size=4096):
        if prefix=='items.item' and event=='start_map': item={'target':{}}
        elif prefix=='items.item' and event=='end_map':
            items.append(item)
            if len(items)>10000: raise ValueError('Too many current recommendations')
        elif prefix=='items' and event=='end_array': return {'items':items}
        elif item is not None and event in ('string','number','boolean','null'):
            key=prefix.removeprefix('items.item.')
            if key in fields: item[key]=value
            elif key in ('target.topTab','target.subTab'): item['target'][key.split('.')[-1]]=value
    raise ValueError('Missing complete current recommendation array')


def current(value, now, minutes):
    try:
        age = (now-datetime.fromisoformat(value.replace('Z', '+00:00'))).total_seconds()
        return -30 <= age <= minutes*60
    except (TypeError, ValueError, AttributeError):
        return False


def pipeline(health, now):
    if not current(health.get('serverTimeUtc'), now, 2):
        raise ValueError('Stale health evidence')
    incident = health.get('incidentAnalysisQueueHealth') or {}
    ai = health.get('aiCompletionHealth') or {}
    capture = health.get('liveTrActivity') or {}
    return {'observed_at': api_time(health['serverTimeUtc']),
        'window_minutes': health.get('throughputWindowMinutes'),
        'calls_received': health.get('recentCallsIngested'),
        'calls_transcribed': health.get('recentCallsTranscribed'),
        'awaiting_transcription': health.get('pendingTranscriptions'),
        'ingest_paused': (health.get('ingest') or {}).get('paused'),
        'capture_stale': capture.get('stale'),
        'last_live_call_at': api_time(capture.get('lastLiveCallUtc')),
        'incident_status': incident.get('status'),
        'awaiting_incident_analysis': incident.get('pendingCalls'),
        'latest_analyzed_call_at': api_time(incident.get('latestCompletedCallUtc')),
        'ai_window_minutes': ai.get('windowMinutes'),
        'ai_requests': ai.get('requests'), 'ai_failures': ai.get('failures')}


def findings(source, now):
    if not current(source.get('generatedAtUtc') or source.get('readAtUtc'), now, 6):
        raise ValueError('Stale recommendation evidence')
    selected = []
    for item in source.get('items', []):
        target = item.get('target') or {}
        if (item.get('kind', 'problem') != 'problem'
            or str(item.get('id', '')).startswith('tr-rf-')
            or target.get('subTab') == 'rf'
            or (target.get('topTab') == 'tr' and target.get('subTab') == 'metrics')
            or item.get('activityState') != 'active'
            or item.get('severity') not in ('medium', 'high', 'critical')
            or item.get('workflowStatus') in ('known_issue', 'resolved', 'dismissed')):
            continue
        title = ''.join(c for c in str(item.get('title', '')) if c.isprintable())
        title = title.encode()[:120].decode('utf8', errors='ignore')
        finding = item.get('findingId')
        url = LINK + ('&finding='+str(finding) if type(finding) is int and 0 < finding <= 9007199254740991 else '')
        selected.append({'title': title, 'severity': item['severity'],
            'activity': 'dormant' if item.get('activityState') == 'quiet' else 'active',
            'last_seen_at': api_time(item.get('lastSeenUtc')), 'details_url': url})
    selected.sort(key=lambda item: {'critical':0, 'high':1, 'medium':2}[item['severity']])
    return selected


def collect(fetch=local_api, now=None):
    now = now or datetime.now(timezone.utc)
    report = {'schema_version':1, 'source_id':'sdr-1861', 'generated_at':stamp(now),
        'valid_until':stamp(now+timedelta(minutes=20)), 'interval_minutes':15,
        'pipeline_result':'check_failed', 'findings_result':'check_failed',
        'pipeline':{}, 'open_findings':[], 'open_finding_count':None}
    try:
        report['pipeline'] = pipeline(fetch('health'), now)
        report['pipeline_result'] = 'reported'
    except Exception:
        pass  # Only the fixed failure code leaves the device, never raw API errors.
    try:
        items = findings(fetch('system/recommendations'), now)
        report.update(open_findings=items[:3], open_finding_count=len(items), findings_result='reported')
    except Exception:
        pass
    body = json.dumps(report, separators=(',', ':'), ensure_ascii=False).encode()
    if len(body) > MAX_BYTES:
        raise ValueError('SDR health report exceeds limit')
    return body


def upload(body):
    # Reuse the existing root-only service key and NAS host-key pin. No human
    # agent, new credential, remote command or repository change is involved.
    root = Path('/etc/whiteoak-machine-backup')
    profile = json.loads((root/'profile.json').read_text())
    if profile.get('nas_user') != 'wo-sdr1861-backup' or profile.get('repository') != 'sftp:wo-sdr1861-backup@100.124.70.102:/home/repository':
        raise ValueError('Existing approved upload destination changed')
    for name in ('id_ed25519', 'known_hosts'):
        p = root/name
        info = p.lstat()
        if not p.is_file() or p.is_symlink() or info.st_uid != 0 or info.st_mode & 0o077:
            raise ValueError('Existing transport file permissions changed')
    with tempfile.TemporaryDirectory(dir='/run/whiteoak-sdr-health', prefix='report-') as folder:
        path = Path(folder)/'report.json'
        path.write_bytes(body)
        path.chmod(0o600)
        batch = f'put {path} /home/sdr-health.json.tmp\nrename /home/sdr-health.json.tmp /home/sdr-health.json\n'
        result = subprocess.run(['sftp', '-q', '-b', '-', '-i', str(root/'id_ed25519'),
            '-o', 'IdentitiesOnly=yes', '-o', 'IdentityAgent=none', '-o', 'BatchMode=yes',
            '-o', 'StrictHostKeyChecking=yes', '-o', 'UserKnownHostsFile='+str(root/'known_hosts'),
            '-o', 'ConnectTimeout=10', '-o', 'ConnectionAttempts=1',
            'wo-sdr1861-backup@100.124.70.102'], input=batch.encode(),
            capture_output=True, timeout=30)
        if result.returncode:
            raise RuntimeError('Existing SFTP upload failed')
    print('SDR health uploaded:', len(body), 'bytes')


if __name__ == '__main__':
    try:
        if os.geteuid() != 0:
            raise RuntimeError('Existing service transport requires root')
        upload(collect())
    except Exception as error:
        print('SDR reporting failed:', type(error).__name__)
        raise SystemExit(1)
