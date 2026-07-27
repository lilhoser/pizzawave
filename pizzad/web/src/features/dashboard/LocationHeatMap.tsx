import { useEffect, useMemo, useRef, useState } from "react";
import { divIcon, type Map as LeafletMap } from "leaflet";
import { MapContainer, Marker, TileLayer, Tooltip, useMap, useMapEvents } from "react-leaflet";
import "leaflet/dist/leaflet.css";
import type { Incident, LocationHeat } from "../../types";
import { locationDisplayName, locationKey } from "./location";

type Props = {
  rows: LocationHeat[];
  incidents: Incident[];
  focusedKey?: string | null;
  resetToken?: number;
  onFocusKey?: (key: string | null) => void;
  onSelectLocation?: (row: LocationHeat | null) => void;
  emptyText?: string;
};

type HeatCluster = {
  key: string;
  rows: LocationHeat[];
  latitude: number;
  longitude: number;
  count: number;
  category: string;
};

const clusterDistancePixels = 54;

export function LocationHeatMap({ rows, incidents, focusedKey, resetToken = 0, onFocusKey, onSelectLocation, emptyText = "No geolocated incidents detected in the selected range." }: Props) {
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
        <MapPosition rows={points} positionKey={positionKey} focusedKey={focusedKey} resetToken={resetToken} />
        <HeatNodes rows={points} incidents={incidents} focusedKey={focusedKey} onFocusKey={onFocusKey} onSelectLocation={onSelectLocation} />
      </MapContainer>
    </div>
    <div className="location-heat-list"><span className="location-heat-list-count">{points.length} geolocated address{points.length === 1 ? "" : "es"}</span></div>
  </div>;
}

function HeatNodes({ rows, incidents, focusedKey, onFocusKey, onSelectLocation }: Omit<Props, "emptyText">) {
  const [zoomRevision, setZoomRevision] = useState(0);
  const map = useMapEvents({ zoomend: () => setZoomRevision(value => value + 1) });
  const clusters = useMemo(() => buildClusters(rows, incidents, map), [rows, incidents, map, zoomRevision]);

  return <>{clusters.map(cluster => {
    const active = Boolean(focusedKey && cluster.rows.some(row => locationKey(row) === focusedKey));
    const size = Math.min(58, 28 + Math.log2(cluster.count + 1) * 8);
    const clustered = cluster.rows.length > 1;
    const icon = divIcon({
      className: `map-heat-node category-${cluster.category}${clustered ? " clustered" : ""}${active ? " active" : ""}`,
      html: `<span>${cluster.count.toLocaleString()}</span>`,
      iconSize: [size, size],
      iconAnchor: [size / 2, size / 2]
    });
    const representative = cluster.rows[0];
    return <Marker
      key={`${cluster.key}:${active}`}
      position={[cluster.latitude, cluster.longitude]}
      icon={icon}
      eventHandlers={{
        click: event => {
          event.originalEvent.stopPropagation();
          const selected = clustered ? mergeClusterRows(cluster.rows) : representative;
          onSelectLocation?.(selected);
          if (clustered) {
            onFocusKey?.(null);
            const separationZoom = firstSeparationZoom(cluster.rows, map);
            if (separationZoom !== null)
              map.setView([cluster.latitude, cluster.longitude], separationZoom, { animate: false });
            return;
          }
          onFocusKey?.(locationKey(representative));
        }
      }}
    >
      <Tooltip direction="top" offset={[0, -(size / 2 + 4)]} opacity={0.96}>
        <strong>{clustered ? `${cluster.rows.length} nearby locations` : locationDisplayName(representative)}</strong><br />
        {clusterNodeCountLabel(cluster.rows, incidents)} · {cluster.rows.reduce((sum, row) => sum + row.count, 0).toLocaleString()} matched call{cluster.rows.reduce((sum, row) => sum + row.count, 0) === 1 ? "" : "s"}<br />
        Latest {new Date(Math.max(...cluster.rows.map(row => row.lastHeard)) * 1000).toLocaleString()}{clustered && map.getZoom() < map.getMaxZoom() ? <><br />Click to separate nearby nodes</> : null}
      </Tooltip>
    </Marker>;
  })}</>;
}

function MapPosition({ rows, positionKey, focusedKey, resetToken }: { rows: LocationHeat[]; positionKey: string; focusedKey?: string | null; resetToken: number }) {
  const map = useMap();
  const positioned = useRef(false);
  useEffect(() => {
    if (resetToken <= 0)
      return;
    const center = defaultMapCenter(rows);
    map.setView([center.latitude, center.longitude], defaultMapZoom(rows), { animate: false });
  }, [map, resetToken]);
  useEffect(() => {
    const focused = focusedKey ? rows.find(row => locationKey(row) === focusedKey) : null;
    if (focused) {
      map.flyTo([focused.latitude, focused.longitude], Math.max(map.getZoom(), 12), { animate: false });
      return;
    }
    if (positioned.current)
      return;
    positioned.current = true;
    const center = defaultMapCenter(rows);
    map.setView([center.latitude, center.longitude], defaultMapZoom(rows), { animate: false });
  }, [map, positionKey, focusedKey]);
  return null;
}

function defaultMapCenter(rows: LocationHeat[]) {
  return {
    latitude: rows.reduce((sum, row) => sum + row.latitude, 0) / rows.length,
    longitude: rows.reduce((sum, row) => sum + row.longitude, 0) / rows.length
  };
}

function defaultMapZoom(rows: LocationHeat[]) {
  if (rows.length < 2) return 12;
  const latitudeSpan = Math.max(...rows.map(row => row.latitude)) - Math.min(...rows.map(row => row.latitude));
  const longitudeSpan = Math.max(...rows.map(row => row.longitude)) - Math.min(...rows.map(row => row.longitude));
  const span = Math.max(latitudeSpan, longitudeSpan);
  if (span <= 0.15) return 13;
  if (span <= 0.45) return 12;
  if (span <= 1.1) return 11;
  if (span <= 3) return 10;
  if (span <= 8) return 8;
  return 5;
}

function firstSeparationZoom(rows: LocationHeat[], map: LeafletMap) {
  for (let zoom = map.getZoom() + 1; zoom <= map.getMaxZoom(); zoom++) {
    if (clusterGroups(rows, map, zoom).length > 1)
      return zoom;
  }
  return null;
}

function buildClusters(rows: LocationHeat[], incidents: Incident[], map: LeafletMap): HeatCluster[] {
  const zoom = map.getZoom();
  return clusterGroups(rows, map, zoom).map(group => {
    const count = clusterNodeCount(group, incidents);
    const categories = new Set(group.map(row => row.category || "other"));
    const weight = group.reduce((sum, row) => sum + Math.max(1, locationNodeCount(row, incidents)), 0);
    return {
      key: group.map(locationKey).sort().join("||"),
      rows: group,
      latitude: group.reduce((sum, row) => sum + row.latitude * Math.max(1, locationNodeCount(row, incidents)), 0) / weight,
      longitude: group.reduce((sum, row) => sum + row.longitude * Math.max(1, locationNodeCount(row, incidents)), 0) / weight,
      count,
      category: categories.size === 1 ? [...categories][0] : "mixed"
    };
  });
}

function clusterGroups(rows: LocationHeat[], map: LeafletMap, zoom: number) {
  const projected = rows.map(row => map.project([row.latitude, row.longitude], zoom));
  const parents = rows.map((_, index) => index);
  const find = (index: number): number => parents[index] === index ? index : (parents[index] = find(parents[index]));
  const join = (left: number, right: number) => {
    const leftRoot = find(left);
    const rightRoot = find(right);
    if (leftRoot !== rightRoot) parents[rightRoot] = leftRoot;
  };

  for (let left = 0; left < rows.length; left++) {
    for (let right = left + 1; right < rows.length; right++) {
      if (projected[left].distanceTo(projected[right]) <= clusterDistancePixels)
        join(left, right);
    }
  }

  const groups = new Map<number, LocationHeat[]>();
  rows.forEach((row, index) => groups.set(find(index), [...(groups.get(find(index)) ?? []), row]));
  return [...groups.values()];
}

function mergeClusterRows(rows: LocationHeat[]): LocationHeat {
  const first = rows[0];
  const incidentLinks = [...new Map(rows.flatMap(row => row.incidentLinks ?? []).map(link => [link.incidentId, link])).values()];
  const sourceCalls = [...new Map(rows.flatMap(row => row.sourceCalls ?? []).map(call => [call.callId, call])).values()];
  const categories = new Set(rows.map(row => row.category || "other"));
  return {
    ...first,
    locationText: `${rows.length} nearby locations`,
    geocodeQuery: "",
    geocodeDisplayName: `${rows.length} nearby locations`,
    latitude: rows.reduce((sum, row) => sum + row.latitude, 0) / rows.length,
    longitude: rows.reduce((sum, row) => sum + row.longitude, 0) / rows.length,
    count: rows.reduce((sum, row) => sum + row.count, 0),
    intensity: Math.max(...rows.map(row => row.intensity)),
    lastHeard: Math.max(...rows.map(row => row.lastHeard)),
    category: categories.size === 1 ? [...categories][0] : "other",
    callIds: [...new Set(rows.flatMap(row => row.callIds ?? []))],
    incidentTitles: [...new Set(rows.flatMap(row => row.incidentTitles ?? []))],
    incidentLinks,
    sourceCalls
  };
}

function hasCoordinates(row: LocationHeat) {
  return Number.isFinite(row.latitude) && Number.isFinite(row.longitude);
}

function clusterNodeCount(rows: LocationHeat[], incidents: Incident[]) {
  const incidentIds = new Set(rows.flatMap(row => (row.incidentLinks ?? []).map(link => link.incidentId)));
  if (incidentIds.size)
    return incidentIds.size;
  const linkedCallIds = new Set(incidents.filter(incident => incidentIds.has(incident.id)).flatMap(incident => incident.calls.map(call => call.callId)));
  const standaloneCallIds = new Set(rows.flatMap(row => row.callIds ?? []).filter(callId => !linkedCallIds.has(callId)));
  return standaloneCallIds.size || rows.reduce((sum, row) => sum + row.count, 0);
}

function clusterNodeCountLabel(rows: LocationHeat[], incidents: Incident[]) {
  const incidentIds = new Set(rows.flatMap(row => (row.incidentLinks ?? []).map(link => link.incidentId)));
  if (incidentIds.size) return `${incidentIds.size} incident${incidentIds.size === 1 ? "" : "s"}`;
  const linkedCallIds = new Set(incidents.filter(incident => incidentIds.has(incident.id)).flatMap(incident => incident.calls.map(call => call.callId)));
  const standaloneCallIds = new Set(rows.flatMap(row => row.callIds ?? []).filter(callId => !linkedCallIds.has(callId)));
  const count = standaloneCallIds.size || rows.reduce((sum, row) => sum + row.count, 0);
  return `${count} call${count === 1 ? "" : "s"}`;
}

function locationNodeCount(row: LocationHeat, incidents: Incident[]) {
  const incidentCount = row.incidentLinks?.length ?? 0;
  if (incidentCount)
    return incidentCount;
  const standaloneCount = standaloneLocationCallIds(row, incidents).length;
  return standaloneCount || row.count;
}

function standaloneLocationCallIds(row: LocationHeat, incidents: Incident[]) {
  const linkedIncidentIds = new Set((row.incidentLinks ?? []).map(link => link.incidentId));
  const incidentCallIds = new Set(incidents.filter(incident => linkedIncidentIds.has(incident.id)).flatMap(incident => incident.calls.map(call => call.callId)));
  return (row.callIds ?? []).filter(callId => !incidentCallIds.has(callId));
}
