import importlib.util
import json
import io
from pathlib import Path
import unittest
from datetime import datetime, timedelta, timezone

spec=importlib.util.spec_from_file_location('report',Path(__file__).with_name('sdr-health-report.py'))
report=importlib.util.module_from_spec(spec); spec.loader.exec_module(report)


class ReportingTests(unittest.TestCase):
    def setUp(self):
        self.now=datetime.now(timezone.utc)
        self.health={'serverTimeUtc':self.now.isoformat(),'recentCallsIngested':12,
            'recentCallsTranscribed':10,'pendingTranscriptions':2,'databasePath':'PRIVATE',
            'aiCompletionHealth':{'latestFailure':'PRIVATE'},'liveTrActivity':{'stale':False}}
        self.source={'generatedAtUtc':self.now.isoformat(),'items':[],'history':[{'secret':'PRIVATE'}]}

    def fetch(self,route):
        return self.health if route=='health' else self.source

    def test_exports_metrics_without_raw_health_or_history(self):
        body=report.collect(self.fetch,self.now)
        self.assertLessEqual(len(body),4096)
        self.assertNotIn(b'PRIVATE',body)
        self.assertEqual(12,json.loads(body)['pipeline']['calls_received'])

    def test_stale_local_health_cannot_be_refreshed_by_upload(self):
        self.health['serverTimeUtc']=(self.now-timedelta(minutes=3)).isoformat()
        data=json.loads(report.collect(self.fetch,self.now))
        self.assertEqual('check_failed',data['pipeline_result'])
        self.assertEqual({},data['pipeline'])

    def test_excludes_rf_and_accepted_findings_and_dormant_pipeline(self):
        base={'id':'ai-generation-health','title':'AI delayed','severity':'high','kind':'problem','activityState':'quiet','workflowStatus':'new','target':{},'findingId':123}
        self.source['items']=[base,{**base,'activityState':'active'},{**base,'id':'tr-rf-bradley','activityState':'active'},{**base,'workflowStatus':'known_issue','activityState':'active'}]
        data=json.loads(report.collect(self.fetch,self.now))
        self.assertEqual(1,data['open_finding_count'])
        self.assertEqual('active',data['open_findings'][0]['activity'])
        self.assertEqual(report.LINK+'&finding=123',data['open_findings'][0]['details_url'])

    def test_dormant_findings_cannot_crowd_active_findings_out_of_three_slots(self):
        base={'id':'queue-pressure','title':'Old failure','severity':'critical','kind':'problem',
            'activityState':'quiet','target':{}}
        self.source['items']=[base]*5+[{**base,'title':'Current failure','severity':'medium','activityState':'active'}]
        data=json.loads(report.collect(self.fetch,self.now))
        self.assertEqual(1,data['open_finding_count'])
        self.assertEqual('Current failure',data['open_findings'][0]['title'])

    def test_unicode_findings_remain_bounded(self):
        self.source['items']=[{'id':str(i),'title':'測'*1000,'severity':'high','activityState':'active','target':{}} for i in range(50)]
        data=json.loads(report.collect(self.fetch,self.now))
        self.assertEqual(50,data['open_finding_count'])
        self.assertEqual(3,len(data['open_findings']))

    def test_failed_api_checks_do_not_imply_zero_alerts(self):
        def fail(route): raise RuntimeError('private exception')
        data=json.loads(report.collect(fail,self.now))
        self.assertEqual('check_failed',data['findings_result'])
        self.assertIsNone(data['open_finding_count'])

    def test_current_items_skip_large_history_and_private_details(self):
        raw=json.dumps({'items':[{'id':'ai-generation-health','title':'AI delayed',
            'severity':'high','detail':'PRIVATE','target':{'topTab':'ai','subTab':'usage'},
            'episodes':[{'secret':'PRIVATE'}]}], 'history':['PRIVATE'*1000000]}).encode()
        stream=io.BytesIO(raw)
        projected=report.current_items(stream)
        self.assertNotIn('PRIVATE',json.dumps(projected))
        self.assertLess(stream.tell(),len(raw))
        self.assertEqual('usage',projected['items'][0]['target']['subTab'])

    def test_dotnet_times_are_normalized_for_dsm_python(self):
        self.assertEqual('2026-10-08T23:52:56.742122+00:00',
            report.api_time('2026-10-08T23:52:56.7421223Z'))
        self.health['serverTimeUtc']=self.now.strftime('%Y-%m-%dT%H:%M:%S.')+'1234567Z'
        self.health['liveTrActivity']['lastLiveCallUtc']=self.health['serverTimeUtc']
        data=json.loads(report.collect(self.fetch,self.now))
        self.assertEqual('reported',data['pipeline_result'])
        self.assertTrue(data['pipeline']['observed_at'].endswith('.123456+00:00'))


if __name__=='__main__': unittest.main()
