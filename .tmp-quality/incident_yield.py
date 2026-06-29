import sqlite3, json, datetime, time
con=sqlite3.connect('file:/var/lib/pizzawave/pizzad.db?mode=ro', uri=True)
con.row_factory=sqlite3.Row
print('now_utc', datetime.datetime.utcnow().isoformat())
print('\n-- hourly calls/incidents/audit last 30h')
q='''
WITH hours AS (
  SELECT strftime('%Y-%m-%d %H:00', datetime(start_time,'unixepoch')) hour, count(*) calls
  FROM calls WHERE start_time >= strftime('%s','now','-30 hours') GROUP BY 1
), inc AS (
  SELECT strftime('%Y-%m-%d %H:00', datetime(first_seen,'unixepoch')) hour, count(*) incidents
  FROM incidents WHERE first_seen >= strftime('%s','now','-30 hours') GROUP BY 1
), aud AS (
  SELECT strftime('%Y-%m-%d %H:00', timestamp_utc) hour,
    sum(case when accepted=1 and operation='create' then 1 else 0 end) creates,
    sum(case when accepted=1 and operation='update' then 1 else 0 end) updates,
    sum(case when accepted=0 then 1 else 0 end) rejects
  FROM incident_operation_audit WHERE timestamp_utc >= datetime('now','-30 hours') GROUP BY 1
)
SELECT coalesce(hours.hour, inc.hour, aud.hour) hour, coalesce(calls,0) calls, coalesce(incidents,0) incidents,
       coalesce(creates,0) creates, coalesce(updates,0) updates, coalesce(rejects,0) rejects
FROM hours FULL OUTER JOIN inc USING(hour) FULL OUTER JOIN aud USING(hour)
ORDER BY hour;
'''
try:
    rows=con.execute(q).fetchall()
except Exception:
    q='''
SELECT h.hour,
  (SELECT count(*) FROM calls WHERE strftime('%Y-%m-%d %H:00', datetime(start_time,'unixepoch'))=h.hour) calls,
  (SELECT count(*) FROM incidents WHERE strftime('%Y-%m-%d %H:00', datetime(first_seen,'unixepoch'))=h.hour) incidents,
  (SELECT count(*) FROM incident_operation_audit WHERE accepted=1 and operation='create' and strftime('%Y-%m-%d %H:00', timestamp_utc)=h.hour) creates,
  (SELECT count(*) FROM incident_operation_audit WHERE accepted=1 and operation='update' and strftime('%Y-%m-%d %H:00', timestamp_utc)=h.hour) updates,
  (SELECT count(*) FROM incident_operation_audit WHERE accepted=0 and strftime('%Y-%m-%d %H:00', timestamp_utc)=h.hour) rejects
FROM (
 SELECT strftime('%Y-%m-%d %H:00', datetime(start_time,'unixepoch')) hour FROM calls WHERE start_time >= strftime('%s','now','-30 hours')
 UNION SELECT strftime('%Y-%m-%d %H:00', datetime(first_seen,'unixepoch')) hour FROM incidents WHERE first_seen >= strftime('%s','now','-30 hours')
 UNION SELECT strftime('%Y-%m-%d %H:00', timestamp_utc) hour FROM incident_operation_audit WHERE timestamp_utc >= datetime('now','-30 hours')
) h ORDER BY h.hour;
'''
    rows=con.execute(q).fetchall()
for r in rows: print(dict(r))
print('\n-- top reject reasons since yesterday 16:00 local / 20:00Z')
for r in con.execute('''select accepted, reason, count(*) count, avg(score) avg_score, max(timestamp_utc) latest from incident_operation_audit where timestamp_utc >= '2026-05-21T20:00:00Z' group by accepted, reason order by accepted asc, count desc limit 25'''):
    print(dict(r))
print('\n-- incident creates since 20Z')
for r in con.execute('''select id, incident_key, title, category, incident_score, datetime(first_seen,'unixepoch') first_seen, datetime(last_seen,'unixepoch') last_seen from incidents where first_seen >= strftime('%s','2026-05-21T20:00:00Z') order by first_seen desc limit 25'''):
    print(dict(r))
