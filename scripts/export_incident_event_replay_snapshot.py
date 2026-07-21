#!/usr/bin/env python3
"""Export a bounded calls-only SQLite snapshot without writing the source database."""

from __future__ import annotations

import argparse
import pathlib
import sqlite3


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--start", required=True, type=int)
    parser.add_argument("--end", required=True, type=int)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    if args.start < 0 or args.end <= args.start:
        raise ValueError("the half-open replay window is invalid")

    output = pathlib.Path(args.output).resolve()
    if output.exists():
        raise FileExistsError(f"refusing to overwrite {output}")

    source_uri = f"file:{pathlib.Path(args.source).resolve().as_posix()}?mode=ro"
    with sqlite3.connect(source_uri, uri=True) as source:
        source.row_factory = sqlite3.Row
        schema = source.execute(
            "select sql from sqlite_master where type = 'table' and name = 'calls'"
        ).fetchone()
        if schema is None or not schema[0]:
            raise ValueError("source database does not contain the calls table")

        try:
            with sqlite3.connect(output) as target:
                target.execute(schema[0])
                columns = [row[1] for row in source.execute("pragma table_info(calls)")]
                quoted_columns = ",".join(
                    '"' + column.replace('"', '""') + '"' for column in columns
                )
                placeholders = ",".join("?" for _ in columns)
                rows = source.execute(
                    "select * from calls where start_time >= ? and start_time < ? "
                    "order by start_time, id",
                    (args.start, args.end),
                )
                target.executemany(
                    f"insert into calls ({quoted_columns}) values ({placeholders})",
                    (tuple(row) for row in rows),
                )
                target.commit()
                summary = target.execute(
                    "select count(*), min(start_time), max(start_time) from calls"
                ).fetchone()
        except Exception:
            output.unlink(missing_ok=True)
            raise

    print(f"calls={summary[0]} start={summary[1]} end={summary[2]} output={output}")


if __name__ == "__main__":
    main()
