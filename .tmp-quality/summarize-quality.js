const fs = require('fs');
const files = JSON.parse(process.argv[1]);
function read(p){ return JSON.parse(fs.readFileSync(p,'utf8')); }
function keys(o){ return o && typeof o === 'object' ? Object.keys(o) : []; }
function get(o,path,def=null){ return path.split('.').reduce((a,k)=>a&&a[k]!==undefined?a[k]:undefined,o) ?? def; }
function summarize(name,q,r,h){
  const calls = get(q,'calls.total', get(q,'calls.count', 0));
  const incidents = get(q,'incidents.total', get(q,'incidents.count', get(q,'incidentOperations.created',0)));
  const creates = get(q,'incidentOperations.acceptedCreates', get(q,'incidentOperations.creates', get(q,'incidentOperations.createAccepted',0)));
  const updates = get(q,'incidentOperations.acceptedUpdates', get(q,'incidentOperations.updates', get(q,'incidentOperations.updateAccepted',0)));
  const rejects = get(q,'incidentOperations.rejected', get(q,'incidentOperations.rejects',0));
  const single = get(q,'incidents.singleCall', get(q,'incidents.singleCallCount',0));
  console.log(`\n## ${name}`);
  console.log('top keys q:', keys(q).join(', '));
  console.log('calls keys:', keys(q.calls).join(', '));
  console.log('incident keys:', keys(q.incidents).join(', '));
  console.log('op keys:', keys(q.incidentOperations).join(', '));
  console.log('ai keys:', keys(q.ai).join(', '));
  console.log('evidence keys:', keys(q.evidenceVerifier).join(', '));
  console.log('transcription keys:', keys(q.transcription).join(', '));
  console.log('embedding keys:', keys(q.embeddings).join(', '));
  console.log('health:', {queueDepth:h.queueDepth, pendingTranscriptions:h.pendingTranscriptions, pendingAudioSeconds:h.pendingAudioSeconds, recentCallsIngested:h.recentCallsIngested, recentCallsTranscribed:h.recentCallsTranscribed, avgTranscriptionSeconds:h.averageTranscriptionSeconds, rtf:h.averageTranscriptionRealtimeFactor, stackName:h.stackName});
  console.log('runtime keys:', keys(r).join(', '));
  console.log('calls:', q.calls);
  console.log('transcription:', q.transcription);
  console.log('ai:', q.ai);
  console.log('evidence:', q.evidenceVerifier);
  console.log('embeddings:', q.embeddings);
  console.log('incidents:', q.incidents);
  console.log('incidentOperations:', q.incidentOperations);
  if (q.incidentOperations?.rejectBuckets) console.log('rejectBuckets:', q.incidentOperations.rejectBuckets);
  if (q.incidentOperations?.recentRejected) console.log('recentRejected:', q.incidentOperations.recentRejected.slice(0,8));
  if (q.incidents?.recent) console.log('recentIncidents:', q.incidents.recent.slice(0,8));
  if (q.incidents?.lowConfidence) console.log('lowConfidence:', q.incidents.lowConfidence.slice(0,8));
}
summarize('RPI', read(files.rpiQ), read(files.rpiR), read(files.rpiH));
summarize('OT', read(files.otQ), read(files.otR), read(files.otH));
