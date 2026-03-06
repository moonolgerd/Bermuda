import { useState } from 'react'
import { graphql, useLazyLoadQuery } from 'react-relay'
import type { IncidentsDashboardQuery } from '../__generated__/IncidentsDashboardQuery.graphql'
import { IncidentsTable } from './IncidentsTable'
import { IncidentMap } from './IncidentMap'
import styles from './IncidentsDashboard.module.css'

const query = graphql`
  query IncidentsDashboardQuery {
    incidents {
      id
      date
      location
      description
      status
      priority
      witnesses
    }
  }
`

export function IncidentsDashboard() {
  const data = useLazyLoadQuery<IncidentsDashboardQuery>(query, {})
  const [selectedId, setSelectedId] = useState<string | null>(null)

  return (
    <div className={styles.layout}>
      <div className={styles.tableSection}>
        <IncidentsTable
          incidents={data.incidents}
          selectedId={selectedId}
          onSelect={setSelectedId}
        />
      </div>
      <div className={styles.mapSection}>
        <IncidentMap
          incidents={data.incidents}
          selectedId={selectedId}
          onSelect={setSelectedId}
        />
      </div>
    </div>
  )
}
