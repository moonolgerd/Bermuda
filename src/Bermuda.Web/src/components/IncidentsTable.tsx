import { useState, useMemo } from 'react'
import type { IncidentItem } from '../types'
import styles from './IncidentsTable.module.css'

const statusLabel: Record<string, string> = {
  OPEN: 'Open',
  INVESTIGATING: 'Investigating',
  CLOSED: 'Closed',
}

const priorityLabel: Record<string, string> = {
  LOW: 'Low',
  MEDIUM: 'Medium',
  HIGH: 'High',
  CRITICAL: 'Critical',
}

type SortKey = keyof Pick<IncidentItem, 'date' | 'location' | 'description' | 'status' | 'priority' | 'witnesses'>;
type SortDir = 'asc' | 'desc';

interface SortThProps {
  col: SortKey;
  sortKey: SortKey;
  sortDir: SortDir;
  onSort: (col: SortKey) => void;
  children: React.ReactNode;
}

function SortTh({ col, sortKey, sortDir, onSort, children }: SortThProps) {
  const active = sortKey === col
  return (
    <th
      className={`${styles.sortable} ${active ? styles.sorted : ''}`}
      onClick={() => onSort(col)}
      aria-sort={active ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none'}
    >
      <span className={styles.thInner}>
        {children}
        <span className={styles.sortIcon} aria-hidden>
          {active ? (sortDir === 'asc' ? '▲' : '▼') : '⬍'}
        </span>
      </span>
    </th>
  )
}

interface Props {
  incidents: readonly IncidentItem[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}

export function IncidentsTable({ incidents, selectedId, onSelect }: Props) {
  const [sortKey, setSortKey] = useState<SortKey>('date')
  const [sortDir, setSortDir] = useState<SortDir>('asc')

  const sorted = useMemo(() => {
    return [...incidents].sort((a, b) => {
      const av = a[sortKey]
      const bv = b[sortKey]
      const cmp = typeof av === 'number' && typeof bv === 'number'
        ? av - bv
        : String(av).localeCompare(String(bv))
      return sortDir === 'asc' ? cmp : -cmp
    })
  }, [incidents, sortKey, sortDir])

  function handleSort(key: SortKey) {
    if (key === sortKey) {
      setSortDir(d => (d === 'asc' ? 'desc' : 'asc'))
    } else {
      setSortKey(key)
      setSortDir('asc')
    }
  }

  return (
    <table className={styles.table}>
      <thead>
        <tr>
          <SortTh col="date" sortKey={sortKey} sortDir={sortDir} onSort={handleSort}>Date</SortTh>
          <SortTh col="location" sortKey={sortKey} sortDir={sortDir} onSort={handleSort}>Location</SortTh>
          <SortTh col="description" sortKey={sortKey} sortDir={sortDir} onSort={handleSort}>Description</SortTh>
          <SortTh col="status" sortKey={sortKey} sortDir={sortDir} onSort={handleSort}>Status</SortTh>
          <SortTh col="priority" sortKey={sortKey} sortDir={sortDir} onSort={handleSort}>Priority</SortTh>
          <SortTh col="witnesses" sortKey={sortKey} sortDir={sortDir} onSort={handleSort}>Witnesses</SortTh>
        </tr>
      </thead>
      <tbody>
        {sorted.map((incident) => (
          <tr
            key={incident.id}
            data-status={incident.status.toLowerCase()}
            className={incident.id === selectedId ? styles.selectedRow : undefined}
            onClick={() => onSelect(incident.id)}
          >
            <td>{incident.date}</td>
            <td className={styles.mono}>{incident.location}</td>
            <td>{incident.description}</td>
            <td>
              <span className={`${styles.badge} ${styles[incident.status.toLowerCase()]}`}>
                {statusLabel[incident.status] ?? incident.status}
              </span>
            </td>
            <td>
              <span className={`${styles.badge} ${styles[incident.priority.toLowerCase()]}`}>
                {priorityLabel[incident.priority] ?? incident.priority}
              </span>
            </td>
            <td className={styles.center}>{incident.witnesses}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
