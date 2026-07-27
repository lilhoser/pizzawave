import type { LocationHeat } from "../../types";

export function locationKey(row: LocationHeat) {
  return `${row.areaId}|${row.locationText}`.toLowerCase();
}

export function locationDisplayName(row: LocationHeat) {
  return row.geocodeDisplayName?.trim() || row.geocodeQuery?.trim() || row.locationText;
}

export function locationShortName(row: LocationHeat) {
  const display = locationDisplayName(row).trim();
  const parts = display.split(",").map(part => part.trim()).filter(Boolean);
  const primary = parts[0];
  if (primary && /^\d+[a-z]?$/i.test(primary) && parts[1]) return `${primary} ${parts[1]}`;
  return primary || display || row.locationText;
}
