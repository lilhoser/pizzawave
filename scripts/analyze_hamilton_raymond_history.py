#!/usr/bin/env python3
"""Analyze cross-site timing in PizzaWave five-minute TR health history."""

from __future__ import annotations

import argparse
import json
import math
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

import numpy as np
import pandas as pd


SITES = {
    "hamilton": {
        "timezone": "America/New_York",
        "latitude": 35.10,
        "longitude": -85.05,
    },
    "raymond": {
        "timezone": "America/Chicago",
        "latitude": 32.26,
        "longitude": -90.42,
    },
}


def solar_event_utc(day: date, latitude: float, longitude: float, sunrise: bool) -> datetime:
    zenith = 90.833
    n = day.timetuple().tm_yday
    lng_hour = longitude / 15.0
    target = 6 if sunrise else 18
    t = n + ((target - lng_hour) / 24.0)
    anomaly = (0.9856 * t) - 3.289
    true_longitude = anomaly + 1.916 * math.sin(math.radians(anomaly))
    true_longitude += 0.020 * math.sin(math.radians(2 * anomaly)) + 282.634
    true_longitude %= 360
    right_ascension = math.degrees(math.atan(0.91764 * math.tan(math.radians(true_longitude)))) % 360
    right_ascension += math.floor(true_longitude / 90) * 90 - math.floor(right_ascension / 90) * 90
    right_ascension /= 15
    sin_declination = 0.39782 * math.sin(math.radians(true_longitude))
    cos_declination = math.cos(math.asin(sin_declination))
    cos_hour = (
        math.cos(math.radians(zenith))
        - sin_declination * math.sin(math.radians(latitude))
    ) / (cos_declination * math.cos(math.radians(latitude)))
    hour_angle = 360 - math.degrees(math.acos(cos_hour)) if sunrise else math.degrees(math.acos(cos_hour))
    local_mean_time = hour_angle / 15 + right_ascension - 0.06571 * t - 6.622
    utc_hours = (local_mean_time - lng_hour) % 24
    return datetime.combine(day, datetime.min.time(), tzinfo=timezone.utc) + timedelta(hours=utc_hours)


def solar_event_local(day: date, site: dict, sunrise: bool) -> pd.Timestamp:
    value = pd.Timestamp(solar_event_utc(day, site["latitude"], site["longitude"], sunrise)).tz_convert(site["timezone"])
    if value.date() < day:
        value += pd.Timedelta(days=1)
    elif value.date() > day:
        value -= pd.Timedelta(days=1)
    return value


def load_health(path: Path, start: str, end: str) -> pd.DataFrame:
    frame = pd.read_csv(path)
    frame["timestamp"] = pd.to_datetime(frame.window_start_utc, utc=True, format="mixed")
    frame["rate"] = frame.decode_rate_total / frame.decode_lines.replace(0, np.nan)
    invalid = frame.sample_stops.gt(0) | frame.unable_source.gt(0) | frame.decode_lines.eq(0)
    frame.loc[invalid, "rate"] = np.nan
    frame = frame.set_index("timestamp").sort_index()
    frame = frame.loc[pd.Timestamp(start, tz="UTC"):pd.Timestamp(end, tz="UTC")]
    return frame[~frame.index.duplicated(keep="last")]


def correlation(left: pd.Series, right: pd.Series, shift_minutes: int) -> dict:
    shifted = right.copy()
    shifted.index = shifted.index + pd.Timedelta(minutes=shift_minutes)
    pair = pd.concat([left.rename("hamilton"), shifted.rename("raymond")], axis=1, sort=True).dropna()
    low_left = pair.hamilton.le(10)
    low_right = pair.raymond.le(10)
    union = low_left | low_right
    return {
        "raymond_shift_minutes": shift_minutes,
        "pairs": int(len(pair)),
        "pearson_rate": float(pair.corr().iloc[0, 1]),
        "simultaneous_low_jaccard": float((low_left & low_right).sum() / union.sum()) if union.any() else None,
        "both_low_pct": float((low_left & low_right).mean() * 100),
    }


def find_sustained(mask: pd.Series, bins: int) -> pd.Timestamp | None:
    rolling = mask.astype(int).rolling(bins, min_periods=bins).sum()
    matches = rolling.index[rolling.eq(bins)]
    return matches[0] - pd.Timedelta(minutes=5 * (bins - 1)) if len(matches) else None


def nightly_events(frame: pd.DataFrame, site: dict) -> list[dict]:
    local = frame.copy()
    local.index = local.index.tz_convert(site["timezone"])
    events = []
    for day in pd.date_range(local.index.min().date(), local.index.max().date(), freq="D"):
        day_value = day.date()
        sunset = solar_event_local(day_value, site, sunrise=False)
        sunrise = solar_event_local(day_value + timedelta(days=1), site, sunrise=True)
        daytime = local.loc[sunset - pd.Timedelta(hours=7):sunset - pd.Timedelta(hours=3), "rate"].dropna()
        search = local.loc[sunset - pd.Timedelta(hours=2):sunrise + pd.Timedelta(hours=2), "rate"]
        if len(daytime) < 36 or len(search.dropna()) < 90:
            continue
        baseline = float(daytime.median())
        if baseline < 20:
            continue
        onset = find_sustained(search.le(10) & search.notna(), 6)
        if onset is None:
            continue
        recovery_search = search.loc[onset + pd.Timedelta(minutes=30):]
        recovery = find_sustained(recovery_search.ge(20) & recovery_search.notna(), 6)
        events.append(
            {
                "night": day_value.isoformat(),
                "baseline_median": round(baseline, 2),
                "sunset_local": sunset.isoformat(),
                "onset_local": onset.isoformat(),
                "onset_utc": onset.tz_convert("UTC").isoformat(),
                "onset_minutes_from_sunset": round((onset - sunset).total_seconds() / 60, 1),
                "recovery_local": recovery.isoformat() if recovery is not None else None,
                "recovery_utc": recovery.tz_convert("UTC").isoformat() if recovery is not None else None,
            }
        )
    return events


def profile(frame: pd.DataFrame, timezone_name: str, utc: bool) -> pd.DataFrame:
    converted = frame.copy()
    if not utc:
        converted.index = converted.index.tz_convert(timezone_name)
    return converted.groupby(converted.index.hour).agg(
        mean_rate=("rate", "mean"),
        median_rate=("rate", "median"),
        low10_pct=("rate", lambda values: float(values.le(10).mean() * 100)),
        bins=("rate", "count"),
    ).rename_axis("hour").reset_index()


def sunset_profile(frame: pd.DataFrame, site: dict) -> pd.DataFrame:
    local = frame.copy()
    local.index = local.index.tz_convert(site["timezone"])
    offsets = []
    for timestamp in local.index:
        candidates = [
            solar_event_local(timestamp.date() - timedelta(days=1), site, sunrise=False),
            solar_event_local(timestamp.date(), site, sunrise=False),
        ]
        sunset = min(candidates, key=lambda candidate: abs((timestamp - candidate).total_seconds()))
        offsets.append((timestamp - sunset).total_seconds() / 60)
    local["sunset_offset_minutes"] = offsets
    local = local.loc[local.sunset_offset_minutes.between(-360, 720)].copy()
    local["sunset_bin_minutes"] = (local.sunset_offset_minutes / 30).round() * 30
    return local.groupby("sunset_bin_minutes").agg(
        mean_rate=("rate", "mean"),
        median_rate=("rate", "median"),
        low10_pct=("rate", lambda values: float(values.le(10).mean() * 100)),
        bins=("rate", "count"),
    ).reset_index()


def load_weather(path: Path) -> pd.DataFrame:
    payload = json.loads(path.read_text(encoding="utf-8"))
    hourly = payload["hourly"]
    frame = pd.DataFrame({key: value for key, value in hourly.items() if key != "time"})
    frame.index = pd.to_datetime(hourly["time"], utc=True)
    frame["dewpoint_spread_c"] = frame.temperature_2m - frame.dew_point_2m
    return frame


def weather_analysis(frame: pd.DataFrame, weather: pd.DataFrame, site: dict) -> dict:
    rates = frame.rate.resample("1h").mean().rename("rate")
    merged = pd.concat([rates, weather], axis=1, join="inner").dropna()
    local_hour = merged.index.tz_convert(site["timezone"]).hour
    variables = [
        "temperature_2m",
        "relative_humidity_2m",
        "dew_point_2m",
        "dewpoint_spread_c",
        "pressure_msl",
        "cloud_cover",
        "wind_speed_10m",
        "precipitation",
    ]
    raw = {name: float(merged.rate.corr(merged[name])) for name in variables}
    residual = merged.copy()
    for name in ["rate", *variables]:
        residual[name] = merged[name] - merged[name].groupby(local_hour).transform("mean")
    controlled = {name: float(residual.rate.corr(residual[name])) for name in variables}
    low = merged.loc[merged.rate.le(10), variables]
    healthy = merged.loc[merged.rate.ge(20), variables]
    return {
        "hours": int(len(merged)),
        "raw_rate_correlation": raw,
        "local_hour_controlled_rate_correlation": controlled,
        "low_hours": int(len(low)),
        "healthy_hours": int(len(healthy)),
        "low_weather_mean": {name: float(low[name].mean()) for name in variables},
        "healthy_weather_mean": {name: float(healthy[name].mean()) for name in variables},
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--hamilton", type=Path, required=True)
    parser.add_argument("--raymond", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--hamilton-weather", type=Path)
    parser.add_argument("--raymond-weather", type=Path)
    parser.add_argument("--start", default="2026-07-10T12:00:00")
    parser.add_argument("--end", default="2026-08-08T00:00:00")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    frames = {
        "hamilton": load_health(args.hamilton, args.start, args.end),
        "raymond": load_health(args.raymond, args.start, args.end),
    }
    overlap_start = max(frame.index.min() for frame in frames.values())
    overlap_end = min(frame.index.max() for frame in frames.values())
    overlap = {key: frame.loc[overlap_start:overlap_end] for key, frame in frames.items()}

    scan = [correlation(overlap["hamilton"].rate, overlap["raymond"].rate, lag) for lag in range(-360, 361, 5)]
    best = max(scan, key=lambda row: row["pearson_rate"])
    events = {key: nightly_events(frame, SITES[key]) for key, frame in frames.items()}
    local_profiles = {key: profile(frame, SITES[key]["timezone"], utc=False) for key, frame in frames.items()}
    utc_profiles = {key: profile(frame, SITES[key]["timezone"], utc=True) for key, frame in frames.items()}
    sunset_profiles = {key: sunset_profile(frame, SITES[key]) for key, frame in frames.items()}
    weather_paths = {"hamilton": args.hamilton_weather, "raymond": args.raymond_weather}
    weather_results = {
        key: weather_analysis(frames[key], load_weather(path), SITES[key])
        for key, path in weather_paths.items()
        if path is not None
    }
    local_profile_correlation = float(
        local_profiles["hamilton"].set_index("hour").mean_rate.corr(
            local_profiles["raymond"].set_index("hour").mean_rate
        )
    )
    utc_profile_correlation = float(
        utc_profiles["hamilton"].set_index("hour").mean_rate.corr(
            utc_profiles["raymond"].set_index("hour").mean_rate
        )
    )
    sunset_profile_correlation = float(
        sunset_profiles["hamilton"].set_index("sunset_bin_minutes").mean_rate.corr(
            sunset_profiles["raymond"].set_index("sunset_bin_minutes").mean_rate
        )
    )
    paired_events = []
    by_night = {key: {event["night"]: event for event in values} for key, values in events.items()}
    for night in sorted(set(by_night["hamilton"]) & set(by_night["raymond"])):
        h = pd.Timestamp(by_night["hamilton"][night]["onset_utc"])
        r = pd.Timestamp(by_night["raymond"][night]["onset_utc"])
        h_local = pd.Timestamp(by_night["hamilton"][night]["onset_local"])
        r_local = pd.Timestamp(by_night["raymond"][night]["onset_local"])
        paired_events.append(
            {
                "night": night,
                "hamilton_onset_local": h_local.isoformat(),
                "raymond_onset_local": r_local.isoformat(),
                "raymond_minus_hamilton_utc_minutes": round((r - h).total_seconds() / 60, 1),
                "raymond_minus_hamilton_local_clock_minutes": round(
                    ((r_local.hour * 60 + r_local.minute) - (h_local.hour * 60 + h_local.minute)), 1
                ),
                "hamilton_minutes_from_sunset": by_night["hamilton"][night]["onset_minutes_from_sunset"],
                "raymond_minutes_from_sunset": by_night["raymond"][night]["onset_minutes_from_sunset"],
            }
        )

    summary = {
        "window": {"start_utc": overlap_start.isoformat(), "end_utc": overlap_end.isoformat()},
        "coordinates_note": "Approximate receiver-area coordinates; solar offsets are only interpreted at tens-of-minutes resolution.",
        "site_summary": {
            key: {
                "valid_bins": int(frame.rate.notna().sum()),
                "mean_rate": float(frame.rate.mean()),
                "median_rate": float(frame.rate.median()),
                "low10_pct": float(frame.rate.le(10).mean() * 100),
                "nightly_events": events[key],
            }
            for key, frame in frames.items()
        },
        "correlation": {
            "same_utc": next(row for row in scan if row["raymond_shift_minutes"] == 0),
            "same_local_clock": next(row for row in scan if row["raymond_shift_minutes"] == -60),
            "best_lag": best,
            "mean_24_hour_profile_same_local_clock": local_profile_correlation,
            "mean_24_hour_profile_same_utc_clock": utc_profile_correlation,
            "mean_profile_relative_to_sunset": sunset_profile_correlation,
        },
        "paired_nightly_events": paired_events,
        "weather": weather_results,
    }
    (args.output / "history-analysis.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    pd.DataFrame(scan).to_csv(args.output / "history-correlation-scan.csv", index=False)
    pd.DataFrame(paired_events).to_csv(args.output / "paired-nightly-events.csv", index=False)
    for key, frame in frames.items():
        local_profiles[key].to_csv(args.output / f"{key}-history-local-hour.csv", index=False)
        utc_profiles[key].to_csv(args.output / f"{key}-history-utc-hour.csv", index=False)
        sunset_profiles[key].to_csv(args.output / f"{key}-history-sunset-relative.csv", index=False)
        pd.DataFrame(events[key]).to_csv(args.output / f"{key}-history-night-events.csv", index=False)
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
