import { useEffect, useMemo } from "react";
import { CircleMarker, MapContainer, TileLayer, Tooltip, useMap } from "react-leaflet";
import "leaflet/dist/leaflet.css";
import type { Incident, LocationHeat } from "../../types";
import { locationDisplayName, locationKey } from "./location";

type Props = {
  rows: LocationHeat[];
  incidents: Incident[];
  focusedKey?: string | null;
  onFocusKey?: (key: string | null) => void;
  onSelectLocation?: (row: LocationHeat | null) => void;
  emptyText?: string;
};

export function LocationHeatMap({ rows, incidents, focusedKey, onFocusKey, onSelectLocation, emptyText = "No geolocated incidents detected in the selected range." }: Props) {
  const points = useMemo(() => rows.filter(hasCoordinates), [rows]);
  const positionKey = useMemo(() => points.map(row => `${locationKey(row)}:${row.latitude}:${row.longitude}`).sort().join("|"), [points]);

  if (!points.length) {
    return <div className="card location-heat-card"><p className="muted">{emptyText}</p></div>;
  }

  return <div className="card location-heat-card">
    <div className="location-map-shell">
      <MapContainer className="location-map" center={[points[0].latitude, points[0].longitude]} zoom={12} minZoom={3} maxZoom={18} zoomControl scrollWheelZoom>
        <TileLayer
          attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
          url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
        />
        <MapPosition rows={points} positionKey={positionKey} focusedKey={focusedKey} />
        {points.map(row => {
          const key = locationKey(row);
          const active = focusedKey === key;
          const count = locationNodeCount(row, incidents);
          return <CircleMarker
            key={`${key}:${active}`}
            center={[row.latitude, row.longitude]}
            radius={(22 + row.intensity * 36) / 2}
            className={`map-heat-marker category-${row.category || "other"}${active ? " active" : ""}`}
            pathOptions={{ fillOpacity: 0.9, opacity: 1, weight: active ? 5 : 3 }}
            eventHandlers={{
              click: event => {
                event.originalEvent.stopPropagation();
                onFocusKey?.(key);
                onSelectLocation?.(row);
              }
            }}
          >
            <Tooltip permanent direction="center" className="map-heat-count">{count}</Tooltip>
            <Tooltip direction="top" offset={[0, -18]} opacity={0.96}>
              <strong>{locationDisplayName(row)}</strong><br />
              {locationNodeCountLabel(row, incidents)} · {row.count} matched call{row.count === 1 ? "" : "s"}<br />
              Latest {new Date(row.lastHeard * 1000).toLocaleString()}
            </Tooltip>
          </CircleMarker>;
        })}
      </MapContainer>
    </div>
    <div className="location-heat-list"><span className="location-heat-list-count">{points.length} geolocated address{points.length === 1 ? "" : "es"}</span></div>
  </div>;
}

function MapPosition({ rows, positionKey, focusedKey }: { rows: LocationHeat[]; positionKey: string; focusedKey?: string | null }) {
  const map = useMap();
  useEffect(() => {
    const focused = focusedKey ? rows.find(row => locationKey(row) === focusedKey) : null;
    if (focused) {
      map.flyTo([focused.latitude, focused.longitude], Math.max(map.getZoom(), 12), { animate: false });
      return;
    }
    if (rows.length === 1) {
      map.setView([rows[0].latitude, rows[0].longitude], 12, { animate: false });
      return;
    }
    map.fitBounds(rows.map(row => [row.latitude, row.longitude] as [number, number]), { padding: [32, 32], maxZoom: 13, animate: false });
  }, [map, positionKey, focusedKey, rows]);
  return null;
}

function hasCoordinates(row: LocationHeat) {
  return Number.isFinite(row.latitude) && Number.isFinite(row.longitude);
}

function locationNodeCount(row: LocationHeat, incidents: Incident[]) {
  const incidentCount = row.incidentLinks?.length ?? 0;
  const standaloneCount = standaloneLocationCallIds(row, incidents).length;
  return incidentCount + standaloneCount || row.count;
}

function locationNodeCountLabel(row: LocationHeat, incidents: Incident[]) {
  const incidentCount = row.incidentLinks?.length ?? 0;
  const standaloneCount = standaloneLocationCallIds(row, incidents).length;
  const count = incidentCount + standaloneCount || row.count;
  if (incidentCount && standaloneCount) return `${incidentCount} incident${incidentCount === 1 ? "" : "s"}, ${standaloneCount} call${standaloneCount === 1 ? "" : "s"}`;
  if (incidentCount) return `${incidentCount} incident${incidentCount === 1 ? "" : "s"}`;
  return `${count} call${count === 1 ? "" : "s"}`;
}

function standaloneLocationCallIds(row: LocationHeat, incidents: Incident[]) {
  const linkedIncidentIds = new Set((row.incidentLinks ?? []).map(link => link.incidentId));
  const incidentCallIds = new Set(incidents.filter(incident => linkedIncidentIds.has(incident.id)).flatMap(incident => incident.calls.map(call => call.callId)));
  return (row.callIds ?? []).filter(callId => !incidentCallIds.has(callId));
}
