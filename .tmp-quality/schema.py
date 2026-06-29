import sqlite3, json, datetime, sys
con=sqlite3.connect('file:/var/lib/pizzawave/pizzad.db?mode=ro', uri=True)
con.row_factory=sqlite3.Row
for t in ['incident_operation_audit','incidents','calls']:
    print('\n--', t)
    for r in con.execute("select sql from sqlite_master where type='table' and name=?", (t,)):
        print(r['sql'])
