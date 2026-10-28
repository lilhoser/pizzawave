"""Exercise the built application in an isolated database; never pause production."""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.request


def run(executable):
    with tempfile.TemporaryDirectory(prefix='pizzawave-ingest-restart-') as folder:
        root=Path(folder)
        with socket.socket() as listener:
            listener.bind(('127.0.0.1',0));port=listener.getsockname()[1]
        config=dict(server=dict(httpBind='127.0.0.1',httpPort=port),
            storage=dict(databasePath=str(root/'state.db'),audioRoot=str(root/'audio'),appDataRoot=str(root/'appdata')),
            auth=dict(tokenFile=str(root/'test.token'),readRequiresAuth=False,writeRequiresAuth=False),
            setup=dict(completed=False),transcription=dict(provider='none'),
            aiInsights=dict(enabled=False),embeddings=dict(enabled=False),
            trunkRecorder=dict(configPath=str(root/'no-tr-config.json'),talkgroupCatalogPath=str(root/'catalog.json'),
                talkgroupsPath=str(root/'no-talkgroups.csv'),logServiceName='whiteoak-isolated-test-no-service'))
        path=root/'config.json';path.write_text(json.dumps(config))
        process=None
        def request(route,body=None):
            data=json.dumps(body).encode() if body is not None else None
            req=urllib.request.Request(f'http://127.0.0.1:{port}/api/v1/'+route,data=data,
                headers={'Content-Type':'application/json'})
            with urllib.request.urlopen(req,timeout=20) as response:return json.load(response)
        def start():
            nonlocal process
            environment=os.environ.copy()
            environment.pop('PIZZAD_RADIO_HEALTH_REPORTING_CONFIG',None)
            environment.pop('PIZZAD_CONFIG',None)
            process=subprocess.Popen([str(executable),'--config',str(path)],cwd=root,env=environment,
                stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
            deadline=time.monotonic()+60
            while time.monotonic()<deadline:
                if process.poll() is not None:raise RuntimeError('Isolated app exited during startup')
                try:return request('health')
                except (OSError,ValueError):time.sleep(.25)
            raise RuntimeError('Isolated health timeout')
        def crash():
            nonlocal process
            if process is not None and process.poll() is None:process.kill();process.wait(timeout=20)
            process=None
        try:
            first=start()
            assert first['liveTrActivity']['status']=='warming'
            assert not first['ingest']['paused']
            paused=request('system/ingest',dict(pause=True,untilQueueClear=False,reason='isolated restart test'))
            assert paused['paused'] and paused['stateStatus']=='durable'
            crash()
            recovered=start()
            assert recovered['ingest']['paused']
            assert recovered['ingest']['pausedAtUtc']==paused['pausedAtUtc']
            assert recovered['ingest']['reason']=='isolated restart test'
            assert recovered['liveTrActivity']['status']=='warming'
            released=request('system/ingest',dict(pause=False))
            assert not released['paused']
            crash()
            assert not start()['ingest']['paused']
            print('PASS isolated process crash/restart: pause, original time/reason, explicit resume, native warmup')
        finally:crash()
    print('Isolated test database/configuration removed; no production actions')

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--executable',type=Path,required=True)
    run(parser.parse_args().executable)
