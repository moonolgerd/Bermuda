import { useEffect } from 'react'
import { MapContainer, TileLayer, CircleMarker, Tooltip, useMap } from 'react-leaflet'
import 'leaflet/dist/leaflet.css'
import type { IncidentItem } from '../types'
import styles from './IncidentMap.module.css'

interface Props {
  incidents: readonly IncidentItem[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}

function parseCoords(location: string): [number, number] | null {
  const m = location.match(/([\d.]+)°([NS])\s+([\d.]+)°([EW])/)
  if (!m) return null
  const lat = parseFloat(m[1]) * (m[2] === 'S' ? -1 : 1)
  const lng = parseFloat(m[3]) * (m[4] === 'W' ? -1 : 1)
  return [lat, lng]
}

function FlyToSelected({ incidents, selectedId }: { incidents: readonly IncidentItem[]; selectedId: string | null }) {
  const map = useMap()
  useEffect(() => {
    if (!selectedId) return
    const incident = incidents.find(i => i.id === selectedId)
    if (!incident) return
    const coords = parseCoords(incident.location)
    if (coords) map.flyTo(coords, Math.max(map.getZoom(), 7), { duration: 0.8 })
  }, [selectedId, incidents, map])
  return null
}

export function IncidentMap({ incidents, selectedId, onSelect }: Props) {
  return (
    <MapContainer center={[25, -71]} zoom={5} className={styles.map} zoomControl>
      <TileLayer
        url="https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png"
        attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors &copy; <a href="https://carto.com/attributions">CARTO</a>'
      />
      <FlyToSelected incidents={incidents} selectedId={selectedId} />
      {incidents.map(incident => {
        const coords = parseCoords(incident.location)
        if (!coords) return null
        const selected = incident.id === selectedId
        return (
          <CircleMarker
            key={incident.id}
            center={coords}
            radius={selected ? 10 : 6}
            pathOptions={{
              fillColor: selected ? '#38bdf8' : '#f59e0b',
              fillOpacity: selected ? 0.9 : 0.7,
              color: selected ? '#0ea5e9' : '#d97706',
              weight: selected ? 2 : 1,
            }}
            eventHandlers={{ click: () => onSelect(incident.id) }}
          >
            <Tooltip direction="top" offset={[0, -8]}>
              <strong>{incident.date}</strong><br />
              {incident.location}<br />
              {incident.description}<br />
              Status: {incident.status} · Priority: {incident.priority} · Witnesses: {incident.witnesses}
            </Tooltip>
          </CircleMarker>
        )
      })}
    </MapContainer>
  )
}
